using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维转换模式的核心工具：状态判定、进入/退出、光效绘制与寻路缓存刷新。
    /// 所有 Harmony patch 的守卫统一走 <see cref="IsHighDim"/>（先 def 短路，再查 comp 缓存，性能优先）。
    /// 视觉采用"实体渲染 + 脚下能量光环"方案，不使用隐形/半透明，避免渲染树材质替换引发的头发/衣服问题。
    /// </summary>
    public static class ArtificialMaidHighDimUtility
    {
        /// <summary>高维能量光环的透明度。</summary>
        public const float HighDimGlowAlpha = 0.35f;

        /// <summary>高维能量光环的颜色（偏紫的"维度"色）。</summary>
        public static readonly Color HighDimGlowColor = new Color(0.65f, 0.4f, 1f);

        // 高维光环的半透明材质（缓存，避免每帧创建）
        private static Material highDimGlowMat;

        private static Material HighDimGlowMat
        {
            get
            {
                if (highDimGlowMat == null)
                {
                    highDimGlowMat = SolidColorMaterials.NewSolidColorMaterial(
                        new Color(HighDimGlowColor.r, HighDimGlowColor.g, HighDimGlowColor.b, HighDimGlowAlpha),
                        ShaderDatabase.Transparent);
                }
                return highDimGlowMat;
            }
        }

        // 反射调用私有 PathFinder.RecycleGridJobData，清空排队中的寻路任务，让网格切换立即生效
        private static readonly MethodInfo recycleGridJobDataMethod =
            AccessTools.Method(typeof(PathFinder), "RecycleGridJobData");

        /// <summary>
        /// 快速判定 Pawn 是否处于高维状态。
        /// 高频调用点（ThreatDisabled、CanHitTargetFrom 等）统一走此入口。
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

        /// <summary>进入高维：置状态、结束当前 Job、刷新寻路。</summary>
        public static void EnterHighDim(Pawn pawn)
        {
            if (pawn == null || pawn.Dead)
            {
                return;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp == null || comp.isHighDim)
            {
                return;
            }

            // 仅展示柜内/未生成时禁止进入（防御性守卫；gizmo 已保证地图上才显示开关）
            if (!pawn.Spawned || pawn.ParentHolder is Building_ArtificialMaidDisplayCase)
            {
                return;
            }

            comp.isHighDim = true;

            // 结束当前 Job，让后续寻路立即以高维网格重新规划
            if (pawn.jobs != null && pawn.CurJob != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            RefreshPathing(pawn);

            Messages.Message("ArtificialMaid_HighDimEnter".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// 退出高维：置状态、刷新寻路。
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

            RefreshPathing(pawn);

            Messages.Message("ArtificialMaid_HighDimExit".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// 每帧绘制高维状态光效：女仆脚下脉动的能量光环（实体渲染不受影响）。
        /// 由 ArtificialMaidMapComponent.MapComponentUpdate 驱动。
        /// </summary>
        public static void DrawHighDimEffect(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map == null)
            {
                return;
            }

            // 脉动：半径与透明度随时间缓慢起伏
            float pulse = 0.5f + 0.5f * Mathf.Sin(Find.TickManager.TicksGame * 0.08f);
            float radius = 1.15f + 0.25f * pulse;

            // 光环绘制在地面稍上方，避免被地形完全遮挡
            Vector3 pos = pawn.DrawPos;
            pos.y = AltitudeLayer.Blueprint.AltitudeFor() + 0.01f;

            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(radius, 1f, radius));
            Graphics.DrawMesh(MeshPool.plane10, matrix, HighDimGlowMat, 0);
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
