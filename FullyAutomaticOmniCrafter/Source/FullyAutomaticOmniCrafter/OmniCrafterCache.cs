using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using FullyAutomaticOmniCrafter.OuterrealmStorage;

namespace FullyAutomaticOmniCrafter
{
    // ─── Item Cache ───────────────────────────────────────────────────────────
    public static class OmniCrafterCache
    {
        private static List<ThingDef> _allCraftable;
        private static Dictionary<ThingCategoryDef, List<ThingDef>> _byCategory;
        private static List<string> _allModNames;
        private static Game _cachedForGame;

        public static List<ThingDef> AllCraftable
        {
            get
            {
                InvalidateIfNeeded();
                if (_allCraftable == null) BuildCache();
                return _allCraftable;
            }
        }

        public static Dictionary<ThingCategoryDef, List<ThingDef>> ByCategory
        {
            get
            {
                InvalidateIfNeeded();
                if (_byCategory == null) BuildCache();
                return _byCategory;
            }
        }

        /// <summary>所有可制造物品涉及的 Mod 名称列表（已排序，首项为原版）</summary>
        public static List<string> AllModNames
        {
            get
            {
                InvalidateIfNeeded();
                if (_allModNames == null) BuildCache();
                return _allModNames;
            }
        }

        /// <summary>获取 ThingDef 所属 Mod 的友好名称，外源异常时返回 "Unknown"</summary>
        public static string GetModName(ThingDef def)
        {
            try
            {
                return def?.modContentPack?.Name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        public static void Reset()
        {
            _allCraftable = null;
            _byCategory = null;
            _allModNames = null;
            _cachedForGame = null;
            PinyinSearchEngine.Invalidate();
        }

        private static void InvalidateIfNeeded()
        {
            if (Current.Game != _cachedForGame)
            {
                _allCraftable = null;
                _byCategory = null;
                _allModNames = null;
                _cachedForGame = Current.Game;
                PinyinSearchEngine.Invalidate();
            }
        }

        private static void BuildCache()
        {
            _allCraftable = new List<ThingDef>();
            var alreadyAdded = new HashSet<ThingDef>();

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                try
                {
                    if (IsValidCraftable(def) && alreadyAdded.Add(def))
                        _allCraftable.Add(def);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[OmniCrafter] Skipped def '{def?.defName}' during cache build: {ex.Message}");
                }
            }

            // 植物特殊处理：将可收割植物的收获产物加入列表
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                try
                {
                    if (def.plant?.harvestedThingDef == null) continue;
                    ThingDef harvested = def.plant.harvestedThingDef;
                    if (IsValidCraftable(harvested) && alreadyAdded.Add(harvested))
                        _allCraftable.Add(harvested);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[OmniCrafter] Skipped plant harvest def '{def?.defName}': {ex.Message}");
                }
            }

            _allCraftable.SortBy(d => d.label ?? d.defName);

            _byCategory = new Dictionary<ThingCategoryDef, List<ThingDef>>();
            foreach (ThingDef def in _allCraftable)
            {
                try
                {
                    if (def.thingCategories == null) continue;
                    foreach (ThingCategoryDef cat in def.thingCategories)
                    {
                        if (!_byCategory.ContainsKey(cat)) _byCategory[cat] = new List<ThingDef>();
                        _byCategory[cat].Add(def);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[OmniCrafter] Skipped category assignment for '{def?.defName}': {ex.Message}");
                }
            }

            // 收集所有涉及的 Mod 名称
            var modSet = new HashSet<string>();
            foreach (ThingDef def in _allCraftable)
            {
                try
                {
                    modSet.Add(GetModName(def));
                }
                catch
                {
                    /* ignore */
                }
            }

            _allModNames = modSet.OrderBy(n => n).ToList();
            // 拼音索引不在此处构建，延迟到用户首次启用拼音搜索时按需构建
        }

        private static bool IsValidCraftable(ThingDef def)
        {
            try
            {
                if (def == null) return false;
                if (def.IsBlueprint || def.IsFrame) return false;
                // if (def.destroyable == false) return false;
                if (def.category == ThingCategory.Mote) return false;
                if (def.category == ThingCategory.Ethereal) return false;
                if (def.category == ThingCategory.Projectile) return false;
                if (def.category == ThingCategory.Attachment) return false;
                if (def.category == ThingCategory.Pawn) return false;
                if (def.thingClass == null) return false;
                if (typeof(Skyfaller).IsAssignableFrom(def.thingClass)) return false;
                if (typeof(Mote).IsAssignableFrom(def.thingClass)) return false;
                if (typeof(Projectile).IsAssignableFrom(def.thingClass)) return false;
                if (typeof(Plant).IsAssignableFrom(def.thingClass)) return false;
                if (def.label.NullOrEmpty() && def.defName.NullOrEmpty()) return false;
                if (def.category != ThingCategory.Item && def.category != ThingCategory.Building) return false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] IsValidCraftable failed for '{def?.defName}': {ex.Message}");
                return false;
            }
        }

        public static int CountOnMap(ThingDef def, Map map)
        {
            if (map == null || def == null) return 0;
            int count = 0;
            try
            {
                // 若该物品是可打包建筑，只统计打包（MinifiedThing）状态的数量，
                // 忽略已展开放置在地图上的建筑实体，避免重复计入。
                if (def.minifiedDef != null)
                {
                    foreach (Thing t in map.listerThings.ThingsMatching(
                                 ThingRequest.ForGroup(ThingRequestGroup.MinifiedThing)))
                        if (t is MinifiedThing mt && mt.InnerThing?.def == def
                                                  && !IsOuterrealmViewCopy(t))
                            count += t.stackCount;
                }
                else
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(def))
                        if (!IsOuterrealmViewCopy(t))
                            count += t.stackCount;
                }

                // 加上搬运中（carryTracker）与 IHaulSource 直接持有物（原版 RecipeWorkerCounter 同语义，
                // 防止补货期间搬运工正搬入的物品被重复制造）
                count += CountCarriedByPawns(def, map);
                count += CountHeldByHaulSources(def, map);

                // 加上超维存储仓（vault）全局层中的真实数量
                count += CountInOuterrealmVault(def, map);
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] CountOnMap failed for '{def?.defName}': {ex.Message}");
            }

            return count;
        }

        /// <summary>仅统计处于存储区（stockpile/仓储格）中的物品数量。</summary>
        public static int CountInStorage(ThingDef def, Map map)
        {
            if (map == null || def == null) return 0;
            int count = 0;
            try
            {
                if (def.minifiedDef != null)
                {
                    foreach (Thing t in map.listerThings.ThingsMatching(
                                 ThingRequest.ForGroup(ThingRequestGroup.MinifiedThing)))
                        if (t is MinifiedThing mt && mt.InnerThing?.def == def
                                                  && t.Position.GetSlotGroup(map) != null
                                                  && !IsOuterrealmViewCopy(t))
                            count += t.stackCount;
                }
                else
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(def))
                        if (t.Position.GetSlotGroup(map) != null && !IsOuterrealmViewCopy(t))
                            count += t.stackCount;
                }

                // 加上搬运中（carryTracker）与 IHaulSource 直接持有物（原版 RecipeWorkerCounter 同语义，
                // 防止补货期间搬运工正搬入的物品被重复制造）
                count += CountCarriedByPawns(def, map);
                count += CountHeldByHaulSources(def, map);

                // 加上超维存储仓（vault）全局层中的真实数量
                count += CountInOuterrealmVault(def, map);
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] CountInStorage failed for '{def?.defName}': {ex.Message}");
            }

            return count;
        }

        /// <summary>
        /// 判断物品是否为超维存储仓的"视图副本"（伪 Spawned 投影）。
        /// 视图副本是全局条目的投影，其 stackCount 恒为 min(全局剩余, stackLimit)，
        /// 不代表真实数量；统计时必须排除，改以全局层真实 long 计数为准，
        /// 否则会低估（vault 存量超过一摞时）或与全局计数重复。
        /// 借出副本（真 Spawned、holdingOwner=null，借出时已从全局扣减）不在此列，
        /// 正常计入地图统计。
        /// </summary>
        private static bool IsOuterrealmViewCopy(Thing t)
        {
            return t != null && t.holdingOwner is OuterrealmVaultViewThingOwner;
        }

        /// <summary>
        /// 超维存储仓（vault）中的真实数量：仅当当前地图存在已生成的 vault 建筑时，
        /// 计入其全局层中该 ThingDef 的总量（vault 建筑即该地图的存储区）。
        /// 该地图无 vault 时返回 0（本地图无法存取全局层内容，不应计入补货统计）。
        /// </summary>
        private static int CountInOuterrealmVault(ThingDef def, Map map)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || !gs.HasVaultOnMap(map)) return 0;
            long total = gs.TotalCountOf(def);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// 统计被殖民地 pawn 搬运中（carryTracker.CarriedThing）的物品数量。
        /// 与原版 RecipeWorkerCounter.GetCarriedCount 同语义：搬运中的物品即将进入目标地，
        /// 计入可避免补货期间搬运工正搬入的物品被重复制造。
        /// 搬运中的物品未 Spawned，不在 listerThings 中，不会与地图统计重复。
        /// </summary>
        private static int CountCarriedByPawns(ThingDef def, Map map)
        {
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            int count = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Thing carried = pawns[i].carryTracker?.CarriedThing;
                if (carried == null) continue;
                // MinifiedThing（打包建筑）按内层 def 匹配；普通物品 GetInnerIfMinified 返回自身
                Thing inner = carried.GetInnerIfMinified();
                if (inner != null && inner.def == def)
                    count += carried.stackCount;
            }
            return count;
        }

        /// <summary>
        /// 统计 IHaulSource（衣物架等存储源）直接持有的、未 Spawned 的物品数量。
        /// 与原版 RecipeWorkerCounter 的 haulSources 遍历同语义。
        /// 注意：超维存储仓建筑（Building_OuterrealmVault）实现 IHaulSource，
        /// 随建筑 SpawnSetup 自动注册进 haulDestinationManager（原版 Thing.cs），
        /// 其 GetDirectlyHeldThings() 返回视图副本（全局条目的投影）——内容已由
        /// CountInOuterrealmVault 全局层计数，此处跳过整体遍历（兼性能：副本数可能较多）。
        /// </summary>
        private static int CountHeldByHaulSources(ThingDef def, Map map)
        {
            List<IHaulSource> sources = map.haulDestinationManager.AllHaulSourcesListForReading;
            int count = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                IHaulSource holder = sources[i];
                // vault 的内容全部在全局层（TotalCountOf），视图副本只是投影，跳过整体遍历
                if (holder == null || holder is Building_OuterrealmVault) continue;
                ThingOwner directlyHeld = holder.GetDirectlyHeldThings();
                if (directlyHeld == null) continue;
                for (int j = 0; j < directlyHeld.Count; j++)
                {
                    Thing t = directlyHeld[j];
                    // 排除防重复：
                    //  1) 已 Spawned 的物品（含 vault 伪 Spawned 视图副本）已在 listerThings 路径处理；
                    //  2) vault 视图副本（OuterrealmVaultViewThingOwner 持有，含潜在未 Spawned 形态）：
                    //     全局层投影，由 CountInOuterrealmVault 计数——不依赖具体 holder 类型，防御性兜底。
                    if (t == null || t.Spawned || IsOuterrealmViewCopy(t)) continue;
                    Thing inner = t.GetInnerIfMinified();
                    if (inner != null && inner.def == def)
                        count += t.stackCount;
                }
            }
            return count;
        }

        public static List<ThingDef> GetValidStuffs(ThingDef def)
        {
            try
            {
                if (!def.MadeFromStuff || def.stuffCategories == null) return new List<ThingDef>();
                List<ThingDef> result = new List<ThingDef>();
                foreach (ThingDef stuff in DefDatabase<ThingDef>.AllDefs)
                {
                    try
                    {
                        if (!stuff.IsStuff || stuff.stuffProps?.categories == null) continue;
                        foreach (StuffCategoryDef cat in def.stuffCategories)
                        {
                            if (stuff.stuffProps.categories.Contains(cat))
                            {
                                result.Add(stuff);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        /* skip malformed stuff def */
                    }
                }

                result.SortBy(s => s.label ?? s.defName);
                return result;
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] GetValidStuffs failed for '{def?.defName}': {ex.Message}");
                return new List<ThingDef>();
            }
        }
    }

}