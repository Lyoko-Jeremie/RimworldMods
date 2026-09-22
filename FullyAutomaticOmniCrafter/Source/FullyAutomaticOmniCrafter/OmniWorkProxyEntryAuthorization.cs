using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 入口授权（R-10）：池 P 的代理从图 X 进入图 Y，当且仅当 **X 图上存在入口 E**，使
    /// `SafeGetTargetMap(E) == Y`，且本池存在可用工作站 W 满足 `W.CoversOn(X, E.Position)`。
    ///
    /// 多段换图**逐段判定**（严格）：A→C→B 的每一段都要单独通过；任一段不通过则目标不可达。
    /// 非 portal 型跨图（RV 上下车等）退化为"载体（车辆）位置必须落在工作站范围内"。
    ///
    /// 判定时机：插在 `JobGiver_Work.TryIssueJobPackage` 的 Prefix。MultiFloors 是"先把 pawn
    /// 瞬移到候选层、再递归重跑同一个 TryIssueJobPackage"，因此那一刻 `pawn.Map` 就是候选图，
    /// 不需要识别 Mod 身份 —— 任何"改写 pawn.Map 后重跑工作查询"的未知 Mod 都会被自动约束。
    ///
    /// 性能：前缀只做 from→to 的两级字典查（O(1)），**绝不**在高温路径上枚举 listerThings。
    /// 授权表由归属图组件的维护周期全量重算，并在工作站/入口增删时立即失效。
    /// </summary>
    public static class OmniWorkProxyEntryAuthorization
    {
        /// <summary>
        /// 严格模式（默认）= 未授权直接拒绝；false = 仅警告模式（放行但记日志）。
        /// 由 Mod 设置 `OmniCrafterMod.Settings.entryAuthStrict` 驱动并随设置保存；
        /// 设置对象尚未初始化时按"严格"处理（保守）。
        /// </summary>
        public static bool StrictMode
        {
            get
            {
                OmniCrafterSettings settings = OmniCrafterMod.Settings;
                return settings == null || settings.entryAuthStrict;
            }
        }

        private static readonly Dictionary<Map, Dictionary<Map, bool>> AuthCache =
            new Dictionary<Map, Dictionary<Map, bool>>();

        private static readonly Dictionary<Map, int> UncoveredPortalCount = new Dictionary<Map, int>();

        /// <summary>载体探测（RV with built-in PD 等）：静态构造期解析并缓存，运行期只读一个字段。</summary>
        private static readonly Func<Map, Thing> CarrierGetter = BuildCarrierGetter();

        // ── 查询（高频，只做字典操作）──────────────────────────────────────────

        /// <summary>本池是否可以派发代理进入 queryMap。未命中一律按"未授权"处理（保守）。</summary>
        public static bool IsAuthorized(Map pool, Map queryMap)
        {
            if (pool == null || queryMap == null || pool == queryMap) return true;
            if (AuthCache.TryGetValue(pool, out Dictionary<Map, bool> row) &&
                row.TryGetValue(queryMap, out bool allowed))
                return allowed;
            return false;
        }

        /// <summary>本图上"通往其它图、但没有被本池任何工作站覆盖"的入口数量（界面提示用）。</summary>
        public static int UncoveredPortals(Map pool)
        {
            return pool != null && UncoveredPortalCount.TryGetValue(pool, out int count) ? count : 0;
        }

        /// <summary>本池当前可以派发代理去的图（界面展示用；只读缓存，不重算）。</summary>
        public static void GetReachableMaps(Map pool, List<Map> into)
        {
            into.Clear();
            if (pool == null) return;
            if (!AuthCache.TryGetValue(pool, out Dictionary<Map, bool> row)) return;
            foreach (KeyValuePair<Map, bool> pair in row)
                if (pair.Value) into.Add(pair.Key);
        }

        /// <summary>授权被明确拒绝时回调，供界面/日志记录（仅在仅警告模式下会放行）。</summary>
        public static void NotifyUnauthorized(Map pool, Map queryMap, Pawn pawn)
        {
            if (pawn == null || queryMap == null) return;
            Log.WarningOnce("[OmniWorkstation] proxy work denied on unauthorized map '" +
                            queryMap.uniqueID + "' (pool map " + (pool?.uniqueID ?? -1) + ")",
                Gen.HashCombineInt(pawn.thingIDNumber, 7743091));
        }

        // ── 缓存维护（低频）────────────────────────────────────────────────────

        /// <summary>失效某池的授权缓存（工作站或入口增删、配置变化时调用）。</summary>
        public static void Invalidate(Map pool)
        {
            if (pool == null) return;
            AuthCache.Remove(pool);
            UncoveredPortalCount.Remove(pool);
        }

        public static void InvalidateAll()
        {
            AuthCache.Clear();
            UncoveredPortalCount.Clear();
        }

        /// <summary>全量重算某图的授权表（每个维护周期一次；入口数量少，成本可忽略）。</summary>
        public static void Refresh(Map pool)
        {
            if (pool == null) return;

            if (!AuthCache.TryGetValue(pool, out Dictionary<Map, bool> row))
            {
                row = new Dictionary<Map, bool>();
                AuthCache[pool] = row;
            }
            row.Clear();

            List<Thing> portals = pool.listerThings.ThingsInGroup(ThingRequestGroup.MapPortal);
            int uncovered = 0;
            for (int i = 0; i < portals.Count; i++)
            {
                MapPortal portal = portals[i] as MapPortal;
                if (portal == null || portal.Destroyed) continue;

                Map target = SafeGetTargetMap(portal);
                if (target == null || target == pool) continue;

                if (IsCoveredByPool(pool, portal.Position))
                {
                    // 只要存在一个覆盖该入口的可用工作站，这张目标图就对本池开放。
                    row[target] = true;
                }
                else
                {
                    uncovered++;
                    if (!row.ContainsKey(target)) row[target] = false;
                }
            }
            UncoveredPortalCount[pool] = uncovered;

            // 非 portal 型跨图：载体（车辆）位置落在本池工作站范围内才授权。
            // portal 已授权通过的图不重复判定；未通过的图若能探到载体则按载体重判。
            if (CarrierGetter == null) return;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                Map candidate = maps[i];
                if (candidate == pool || row.ContainsKey(candidate)) continue;
                Thing carrier = CarrierGetter(candidate);
                if (carrier == null) continue;
                if (IsCarrierAuthorized(pool, carrier)) row[candidate] = true;
            }
        }

        private static readonly List<Building_OmniWorkstation> StationsScratch =
            new List<Building_OmniWorkstation>();

        /// <summary>入口所在位置是否被本池任一可用工作站覆盖（跨图用同坐标投影）。</summary>
        public static bool IsCoveredByPool(Map pool, IntVec3 cell)
        {
            return IsCoveredByStations(CollectOperationalStations(pool), pool, cell);
        }

        private static bool IsCoveredByStations(List<Building_OmniWorkstation> stations, Map coveredMap,
            IntVec3 cell)
        {
            for (int i = 0; i < stations.Count; i++)
                if (stations[i].CoversOn(coveredMap, cell)) return true;
            return false;
        }

        /// <summary>
        /// 收集某图上的可用工作站（低频路径专用；复用列表避免分配）。
        /// 只在维护周期与入口判定时调用，不在高温路径上。
        /// </summary>
        private static List<Building_OmniWorkstation> CollectOperationalStations(Map map)
        {
            StationsScratch.Clear();
            if (map == null) return StationsScratch;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i] is Building_OmniWorkstation station && station.Operational)
                    StationsScratch.Add(station);
            return StationsScratch;
        }

        /// <summary>载体授权：载体当前所在位置必须落在本池某个工作站范围内。</summary>
        public static bool IsCarrierAuthorized(Map pool, Thing carrier)
        {
            if (carrier == null) return false;
            Map carrierMap = carrier.MapHeld;
            if (carrierMap == null) return false;   // 载体不在任何图上（gravship 在世界地图）→ 保守拒绝
            return IsCoveredByPool(carrierMap, carrier.PositionHeld);
        }

        /// <summary>
        /// 安全求"入口通往的目标图"：不触发 pocket map 惰性生成，不容忍任何异常。
        /// `MapPortal.PocketMap` 的 getter 只做"父地图已撤下则清空"的清理、不会生成
        /// （`Source/RimWorld/MapPortal.cs:32-53` 已确证），因此 `PocketMapExists` 是安全读取。
        /// </summary>
        public static Map SafeGetTargetMap(MapPortal portal)
        {
            if (portal == null) return null;
            try
            {
                // ① 原版口袋图出口：反向链接回入口所在图，无副作用。
                if (portal is PocketMapExit exit) return exit.entrance?.Map;
                // ② 原版正向链接（入口 → 出口）。必须校验 exit **真的已生成**：
                //    SimplePortal_Building.ExposeData() 会 `base.exit = new PocketMapExit()`
                //    塞一个未生成的占位对象（MapHeld 恒为 null），此时若直接 return null，
                //    该入口就会被永久抹出授权表。
                //    （详见 Docs/OmniWorkProxyCrossMapCompat_Investigation.md 缺陷 A）
                PocketMapExit originalExit = portal.exit;
                if (originalExit != null && originalExit.MapHeld != null) return originalExit.MapHeld;
                // ③ 已经生成过的口袋图：只读属性。
                if (portal.PocketMapExists) return portal.PocketMap;
                // ④ 第三方子类（MultiFloors 的 Stair*、SimplePortal）都 override 了 GetOtherMap；
                //    精确的原版 MapPortal 若尚未链接则**绝不**调用，避免惰性生成 pocket map。
                //    ★ 顺序与条件不可调整：MultiFloors 的 Stair 从不设置 exit，正是靠这一步通过授权，
                //      且其基类版本会抛 NotImplementedException，由下面的 catch 兜住。
                if (portal.GetType() != typeof(MapPortal)) return portal.GetOtherMap();
                return null;
            }
            catch (Exception error)
            {
                // MultiFloors 的 Stair.GetOtherMap() 会抛 NotImplementedException，这里必须吞掉。
                Log.WarningOnce("[OmniWorkstation] portal target lookup failed: " + error,
                    portal.thingIDNumber);
                return null;
            }
        }

        // ── 入口型 Job 的启动期校验（缺陷 C 的兜底，见调查报告 9.3）────────────

        /// <summary>
        /// 判定一份即将启动的 Job 是否"会把代理带出归属图、且本池未授权"。
        ///
        /// 入口授权前缀只工作在 `JobGiver_Work.TryIssueJobPackage` —— 那是"代理已经站在某张图上"的时刻；
        /// 而第三方（如 RV Auto-Embark 的 WorkGiver_TendRoom）是在**归属图上**选中"走到入口换图"的 Job，
        /// 该 Job 启动时 `pawn.Map` 仍是归属图，前缀看不到它。这里在那份 Job 启动的瞬间补上同一套判据。
        ///
        /// 与 MultiFloors 的关系：它的跨层 Job 只在"递归 TryIssueJobPackage 已获授权"之后才产出，
        /// 因此到达这里时 IsAuthorized 必然为 true，不会被拦；而且它大量使用 `target == null`
        /// 的形式（MakeChangeLevelThroughStairJob(null, map)），本方法第一步即跳过。
        /// </summary>
        public static bool DeniesEntryJob(Pawn pawn, Job job)
        {
            if (pawn == null || job == null) return false;
            Map homeMap = GameComponent_OmniWorkProxyRegistry.HomeMapOf(pawn);
            if (homeMap == null) return false;
            return DeniesEntryTarget(homeMap, job.targetA) ||
                   DeniesEntryTarget(homeMap, job.targetB) ||
                   DeniesEntryTarget(homeMap, job.targetC);
        }

        private static bool DeniesEntryTarget(Map homeMap, LocalTargetInfo target)
        {
            // 目标可能无效（MultiFloors 的 ChangeLevelThroughStair 常用 target == null），直接跳过。
            if (!target.IsValid || !target.HasThing) return false;
            MapPortal portal = target.Thing as MapPortal;
            if (portal == null) return false;

            Map dest = SafeGetTargetMap(portal);
            // 求不出目标图，或目标就是归属图（例如驻外代理的回程 Job）→ 放行。
            if (dest == null || dest == homeMap) return false;

            // 与 Patch_OmniWorkProxy_EntryAuthorization 的判据与顺序完全一致。
            MapComponent_OmniWorkstation homeManager = homeMap.GetComponent<MapComponent_OmniWorkstation>();
            if (homeManager != null && !homeManager.AnyStationAllowsCrossMapWork()) return true;
            return !IsAuthorized(homeMap, dest);
        }

        // ── 载体探测（可选加速器；探测失败即整体静默退化为"无载体"）────────────

        private static Func<Map, Thing> BuildCarrierGetter()
        {
            try
            {
                Type componentType = null;
                foreach (Type type in AccessTools.AllTypes())
                {
                    if (type == null || type.Name != "InteriorSpaceMapComponent") continue;
                    if (!typeof(MapComponent).IsAssignableFrom(type)) continue;
                    componentType = type;
                    break;
                }
                if (componentType == null) return null;

                FieldInfo field = componentType.GetField("ownerThing",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null || !typeof(Thing).IsAssignableFrom(field.FieldType)) return null;

                Type capturedType = componentType;
                return map =>
                {
                    try
                    {
                        MapComponent component = map.GetComponent(capturedType);
                        return component == null ? null : field.GetValue(component) as Thing;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                };
            }
            catch (Exception)
            {
                // 类型探测失败即禁用该加速器（第 3 章 C3：缺失可降级）。
                return null;
            }
        }
    }
}
