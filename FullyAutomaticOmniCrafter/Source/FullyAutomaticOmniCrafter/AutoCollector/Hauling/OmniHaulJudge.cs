using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 判定用 Pawn（"虚拟判定 Pawn"）的生成、缓存与「伪造已生成」窗口。
    ///
    /// 设计要点（详见 Docs/AutoCollectorHaulingDesign.md §5）：
    /// 1. 完整走 PawnGenerator 生成，再手工补 AddComponentsForSpawn，
    ///    但永不 GenSpawn —— 判定只需要 Pawn 的"形"，不需要它的"命"。
    /// 2. 判定窗口内直接写 Thing 的私有字段 mapIndexOrState 与 SetPositionDirect，
    ///    让 Spawned / Map / Position 真实成立；退出时复位。
    ///    这比 patch 三个极小非虚 getter 更可靠：不会遇到 Mono JIT 内联失效，
    ///    也没有每 tick 数万次读取 delegate 的热路径开销。
    /// 3. 每张地图一个共享实例；位置由判定上下文注入（collector 的建筑格）。
    ///    判定 Pawn 不进 mapPawns / thingGrid / 存档，读档后自然重建。
    /// 4. 与 OmniWorkProxy 完全隔离：使用独立 kindDef（FAOC_OmniJudge），
    ///    绝不复用代理的 kindDef，否则代理专用补丁会把它当成真 Spawn 的代理。
    ///
    /// 线程：只在主线程的收集器 Tick 内使用。Reachability 的 Prefix 可能在寻路
    /// 线程被调用，但那时 activeJudge 的引用比较必然不成立（ReferenceEquals），
    /// 不会误伤其它 Pawn。
    /// </summary>
    public static class OmniHaulJudge
    {
        private static readonly Dictionary<int, Pawn> JudgesByMap = new Dictionary<int, Pawn>();

        private static AccessTools.FieldRef<Thing, sbyte> mapIndexOrState;
        private static bool initialized;
        private static bool unavailable;

        // 当前判定窗口。仅主线程写，主线程判定链读。
        private static Pawn activeJudge;
        private static bool activeWholeMap;

        /// <summary>字段与 Def 是否可用；不可用时整条填充通道降级关闭。</summary>
        public static bool Available
        {
            get
            {
                EnsureInit();
                return !unavailable && OmniWorkstationDefOf.FAOC_OmniJudge != null;
            }
        }

        /// <summary>当前是否处于判定窗口内。</summary>
        public static bool IsWindowOpen => activeJudge != null;

        /// <summary>当前窗口是否为"全图"模式（Prefix 让 CanReach 恒真）。</summary>
        public static bool WholeMapMode => activeWholeMap;

        /// <summary>当前判定 Pawn（调试与自检用）。</summary>
        public static Pawn ActiveJudge => activeJudge;

        private static void EnsureInit()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                System.Reflection.FieldInfo field = AccessTools.Field(typeof(Thing), "mapIndexOrState");
                if (field == null || field.FieldType != typeof(sbyte))
                {
                    unavailable = true;
                    Log.ErrorOnce(
                        "[FAOC] Thing.mapIndexOrState 字段缺失或类型变更，判定 Pawn 填充通道已禁用。",
                        197403301);
                    return;
                }

                mapIndexOrState = AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");
            }
            catch (Exception error)
            {
                unavailable = true;
                Log.ErrorOnce("[FAOC] 初始化判定 Pawn 字段访问失败：" + error, 197403302);
            }
        }

        /// <summary>是否为本模组专用的判定 Pawn。</summary>
        public static bool IsJudge(Pawn pawn)
        {
            return pawn != null
                && OmniWorkstationDefOf.FAOC_OmniJudge != null
                && pawn.kindDef == OmniWorkstationDefOf.FAOC_OmniJudge;
        }

        /// <summary>取本图共享的判定 Pawn；首次使用时生成。</summary>
        public static Pawn GetOrCreate(Map map)
        {
            if (map == null || !Available) return null;

            int key = map.uniqueID;
            Pawn pawn;
            if (JudgesByMap.TryGetValue(key, out pawn)
                && pawn != null
                && !pawn.Destroyed
                && pawn.def != null)
            {
                return pawn;
            }

            pawn = Create(map);
            if (pawn != null) JudgesByMap[key] = pawn;
            return pawn;
        }

        private static Pawn Create(Map map)
        {
            try
            {
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    OmniWorkstationDefOf.FAOC_OmniJudge,
                    null,
                    canGeneratePawnRelations: false));

                if (pawn == null) return null;

                // 先设派系：AddAndRemoveDynamicComponents 用 Faction.IsPlayer 决定
                // 是否补 playerSettings / outfits / drafter 等。
                pawn.SetFactionDirect(Faction.OfPlayer);

                // 原版 Building_CryptosleepCasket 对未 Spawn 的 Pawn 就是这么调的。
                PawnComponentsUtility.AddComponentsForSpawn(pawn);
                pawn.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();

                // 只补纯内部状态；不调 hediffSet.DirtyCache()，它会触碰
                // pawn.Drawer.renderer，而该对象在未 Spawn 时才为 null。
                pawn.health.Notify_Spawned();

                // 明确停留在"世界之外"：未 Spawn，位置无效。
                pawn.SetPositionDirect(IntVec3.Invalid);

                if (Prefs.DevMode)
                {
                    string label = pawn.Name != null ? pawn.LabelShortCap : pawn.def.label;
                    Log.Message("[FAOC] 判定 Pawn 已创建："
                        + label
                        + " spawned=" + pawn.Spawned
                        + " map=" + (pawn.Map == null ? "null" : pawn.Map.ToString()));
                }

                return pawn;
            }
            catch (Exception error)
            {
                Log.ErrorOnce("[FAOC] 创建判定 Pawn 失败：" + error, 197403303);
                return null;
            }
        }

        /// <summary>
        /// 打开判定窗口：伪造 Spawned / Map / Position。
        /// 返回 false 表示已有窗口（重入）或参数非法，调用方必须跳过本次判定。
        /// </summary>
        public static bool Begin(Pawn judge, Map map, IntVec3 anchor, bool wholeMap)
        {
            EnsureInit();
            if (unavailable || mapIndexOrState == null) return false;
            if (judge == null || map == null) return false;
            if (activeJudge != null) return false;

            int mapIndex = map.Index;
            if (mapIndex < 0 || mapIndex > sbyte.MaxValue) return false;

            activeJudge = judge;
            activeWholeMap = wholeMap;

            // 起点必须可站立：建筑所在格通常不可通行，若直接从该格出发，
            // 原版寻路在 RegionGrid 上取不到有效 region，档 B 会被判成不可达。
            IntVec3 anchorCell = anchor;
            if (!anchorCell.Standable(map))
            {
                IntVec3 standable = CellFinder.StandableCellNear(anchorCell, map, 8f);
                if (standable.IsValid) anchorCell = standable;
            }

            // 先位置（此时 Spawned 为 false，Position 的 setter 不会走 grid 分支），
            // 再状态（Spawned 立即为 true、Map 立即正确）。
            judge.SetPositionDirect(anchorCell);
            mapIndexOrState(judge) = (sbyte)mapIndex;
            return true;
        }

        /// <summary>关闭判定窗口：先撤回状态，再清位置。</summary>
        public static void End()
        {
            Pawn judge = activeJudge;
            if (judge == null) return;

            activeJudge = null;
            activeWholeMap = false;

            if (mapIndexOrState != null)
            {
                try { mapIndexOrState(judge) = -1; }
                catch (Exception error)
                {
                    Log.ErrorOnce("[FAOC] 复位判定 Pawn 状态失败：" + error, 197403304);
                }
            }

            try { judge.SetPositionDirect(IntVec3.Invalid); }
            catch { /* 未 Spawn 状态下只是字段赋值，失败也无害 */ }
        }

        /// <summary>判定链内判断某个 Pawn 是否就是当前判定 Pawn。</summary>
        public static bool IsJudging(Pawn pawn)
        {
            return pawn != null && ReferenceEquals(pawn, activeJudge);
        }

        /// <summary>全图档的"目标有效"语义：目标存在且已生成即视为可达。</summary>
        public static bool IsValidReachTarget(LocalTargetInfo dest)
        {
            if (dest.HasThing)
            {
                Thing thing = dest.Thing;
                return thing != null && thing.Spawned && !thing.Destroyed;
            }

            return dest.Cell.IsValid;
        }

        /// <summary>地图移除时释放该图的判定 Pawn。</summary>
        public static void ReleaseMap(Map map)
        {
            if (map == null) return;
            int key = map.uniqueID;
            Pawn pawn;
            if (JudgesByMap.TryGetValue(key, out pawn))
            {
                JudgesByMap.Remove(key);
                if (ReferenceEquals(pawn, activeJudge)) End();
            }
        }

        /// <summary>新游戏 / 读档时清空全部缓存。</summary>
        public static void ClearAll()
        {
            if (activeJudge != null) End();
            JudgesByMap.Clear();
        }

        /// <summary>开发期自检：返回窗口前后关键状态的文本。</summary>
        public static string SelfCheck(Pawn judge, Map map, IntVec3 anchor)
        {
            if (judge == null || map == null) return "judge或map为空";

            StringBuilder sb = new StringBuilder(256);
            sb.Append("before: spawned=").Append(judge.Spawned)
              .Append(" map=").Append(judge.Map == null ? "null" : "set")
              .Append(" pos=").Append(judge.Position.IsValid ? "valid" : "invalid");

            if (Begin(judge, map, anchor, true))
            {
                sb.Append(" | in: spawned=").Append(judge.Spawned)
                  .Append(" sameMap=").Append(judge.Map == map)
                  .Append(" posMatch=").Append(judge.Position == anchor)
                  .Append(" canReach=").Append(judge.CanReach(anchor, PathEndMode.Touch, Danger.Deadly));
                End();
            }
            else
            {
                sb.Append(" | 无法打开窗口");
            }

            sb.Append(" | after: spawned=").Append(judge.Spawned)
              .Append(" map=").Append(judge.Map == null ? "null" : "set")
              .Append(" inWorldPawns=").Append(Find.WorldPawns.Contains(judge));
            return sb.ToString();
        }
    }
}
