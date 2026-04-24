using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

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
                if (def.destroyable == false) return false;
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
                        if (t is MinifiedThing mt && mt.InnerThing?.def == def)
                            count += t.stackCount;
                }
                else
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(def))
                        count += t.stackCount;
                }
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
                                                  && t.Position.GetSlotGroup(map) != null)
                            count += t.stackCount;
                }
                else
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(def))
                        if (t.Position.GetSlotGroup(map) != null)
                            count += t.stackCount;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] CountInStorage failed for '{def?.defName}': {ex.Message}");
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