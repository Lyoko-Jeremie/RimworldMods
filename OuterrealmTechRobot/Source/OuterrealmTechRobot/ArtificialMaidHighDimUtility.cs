using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维转换模式的核心工具：状态判定、进入/退出、视觉渐变控制与寻路缓存刷新。
    /// 所有 Harmony patch 的守卫统一走 <see cref="IsHighDim"/>（先 def 短路，再查 comp 缓存，性能优先）。
    /// </summary>
    public static class ArtificialMaidHighDimUtility
    {
        /// <summary>高维幻影的透明度下限：玩家仍能看到半透明的女仆（可选中/操作），AI 心理隐形不受影响。</summary>
        public const float HighDimAlphaBase = 0.2f;

        // 反射调用私有 PathFinder.RecycleGridJobData，清空排队中的寻路任务，让网格切换立即生效
        private static readonly MethodInfo recycleGridJobDataMethod =
            AccessTools.Method(typeof(PathFinder), "RecycleGridJobData");

        /// <summary>
        /// 快速判定 Pawn 是否处于高维状态。
        /// 高频调用点（ThreatDisabled、CanHitTargetFrom、GetAlpha 等）统一走此入口。
        /// </summary>
        public static bool IsHighDim(Pawn pawn)
        {
            if (pawn == null || pawn.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return false;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            return comp != null && comp.isHighDim;
        }

        /// <summary>进入高维：置状态、复位并淡出视觉、结束当前 Job、刷新寻路。</summary>
        public static void EnterHighDim(Pawn pawn)
        {
            if (pawn == null || pawn.Dead)
            {
                return;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp == null || comp.isHighDim)
            {
                Log.Message($"[OuterrealmTech-HighDim] EnterHighDim 拦截: comp==null={comp == null}, isHighDim={comp?.isHighDim}");
                return;
            }

            // 仅展示柜内/未生成时禁止进入（防御性守卫；gizmo 已保证地图上才显示开关）
            if (!pawn.Spawned || pawn.ParentHolder is Building_ArtificialMaidDisplayCase)
            {
                Log.Message($"[OuterrealmTech-HighDim] EnterHighDim 拦截: spawned={pawn.Spawned}, holder={pawn.ParentHolder?.GetType().Name ?? "null"}");
                return;
            }

            comp.isHighDim = true;
            EnsureHighDimHediff(pawn);

            // 复位到完全可见后开始淡出（1 → 0.2 的渐出），保证每次进入都有渐变效果
            HediffComp_Invisibility inv = GetInvisibilityComp(pawn);
            inv?.BecomeVisible(true);
            inv?.BecomeInvisible(false);
            Log.Message($"[OuterrealmTech-HighDim] EnterHighDim 成功: isHighDim={comp.isHighDim}, invComp={inv != null}");

            // 结束当前 Job，让后续寻路立即以高维网格重新规划
            if (pawn.jobs != null && pawn.CurJob != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            RefreshPathing(pawn);

            Messages.Message("ArtificialMaid_HighDimEnter".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// 退出高维：置状态、淡入视觉、刷新寻路。
        /// 若停在不可站立格（山体/深水/真空/被建筑占据），先传送到附近可站立格，避免卡进地形。
        /// </summary>
        /// <param name="pawn">目标女仆</param>
        /// <param name="force">true 时不进行落点传送（收纳/DeSpawn 等场景，位置由外部流程管理）</param>
        public static void ExitHighDim(Pawn pawn, bool force = false)
        {
            if (pawn == null)
            {
                return;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp == null || !comp.isHighDim)
            {
                return;
            }

            comp.isHighDim = false;

            // 若当前格对普通规则不可站立，传送回附近可站立格
            if (!force && pawn.Spawned && pawn.Map != null && !pawn.Position.Standable(pawn.Map))
            {
                Map map = pawn.Map;
                if (TryFindExitCell(pawn, out IntVec3 cell))
                {
                    pawn.DeSpawn();
                    GenSpawn.Spawn(pawn, cell, map, Rot4.Random);
                }
            }

            // 淡入（0.2 → 1）
            HediffComp_Invisibility inv = GetInvisibilityComp(pawn);
            inv?.BecomeVisible(false);

            RefreshPathing(pawn);

            Messages.Message("ArtificialMaid_HighDimExit".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>确保高维视觉 hediff 常驻（平时 alpha=1 无副作用，渐变随时可用）。</summary>
        public static void EnsureHighDimHediff(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.health == null)
            {
                return;
            }

            HediffDef def = ArtificialMaidDefOf.AM_HighDim;
            if (def != null && !pawn.health.hediffSet.HasHediff(def))
            {
                pawn.health.AddHediff(def);
                // 刚添加的隐形 hediff 会被 CompPostPostAdd 设为隐形状态，
                // 显式复位为完全可见，保证非高维时 alpha=1 无副作用（消除版本差异）
                GetInvisibilityComp(pawn)?.BecomeVisible(true);
            }
        }

        /// <summary>获取高维视觉 hediff 上的隐身组件（用于控制渐出渐入）。</summary>
        public static HediffComp_Invisibility GetInvisibilityComp(Pawn pawn)
        {
            if (pawn?.health?.hediffSet == null)
            {
                return null;
            }

            HediffDef def = ArtificialMaidDefOf.AM_HighDim;
            if (def == null)
            {
                return null;
            }

            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def);
            return hediff?.TryGetComp<HediffComp_Invisibility>();
        }

        /// <summary>
        /// 刷新寻路：清空排队的 PathGridJob（反射调用原版私有方法）并清空可达性缓存，
        /// 使高维/普通网格切换立即生效。
        /// </summary>
        private static void RefreshPathing(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null)
            {
                return;
            }

            try
            {
                recycleGridJobDataMethod?.Invoke(pawn.Map.pathFinder, null);
            }
            catch (System.Exception e)
            {
                Log.Warning("[OuterrealmTech] 高维寻路缓存刷新失败: " + e.Message);
            }

            pawn.Map.reachability.ClearCache();
            if (pawn.Spawned)
            {
                pawn.Map.attackTargetsCache.UpdateTarget(pawn);
            }
        }

        /// <summary>在女仆当前位置附近寻找普通规则下可站立且未被占用的落点（先小半径，再扩大到全图）。</summary>
        private static bool TryFindExitCell(Pawn pawn, out IntVec3 cell)
        {
            Map map = pawn.Map;
            if (map == null)
            {
                cell = IntVec3.Invalid;
                return false;
            }

            if (pawn.Position.InBounds(map) && pawn.Position.Standable(map) && !pawn.Position.Filled(map))
            {
                cell = pawn.Position;
                return true;
            }

            // 先小半径搜索，失败则扩大到全图，避免女仆以普通规则卡在不可站立格
            if (CellFinder.TryFindRandomCellNear(
                    pawn.Position, map, 8,
                    c => c.InBounds(map) && c.Standable(map) && !c.Filled(map) && !c.Fogged(map),
                    out cell))
            {
                return true;
            }

            return CellFinder.TryFindRandomCell(
                map,
                c => c.InBounds(map) && c.Standable(map) && !c.Filled(map) && !c.Fogged(map),
                out cell);
        }
    }
}
