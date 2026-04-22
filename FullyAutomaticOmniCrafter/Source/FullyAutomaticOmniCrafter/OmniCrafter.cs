using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    // ─── Enums & Data ─────────────────────────────────────────────────────────
    public enum OutputMode
    {
        DropNear,
        SendToStorage
    }

    public enum ProductionMode
    {
        FixedCount,
        MaintainStock
    }

    public class AutoOrder : IExposable
    {
        public ThingDef thingDef;
        public ThingDef stuffDef;
        public QualityCategory quality = QualityCategory.Normal;
        public int targetCount = 10;
        public OutputMode outputMode = OutputMode.DropNear;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref thingDef, "thingDef");
            Scribe_Defs.Look(ref stuffDef, "stuffDef");
            Scribe_Values.Look(ref quality, "quality", QualityCategory.Normal);
            Scribe_Values.Look(ref targetCount, "targetCount", 10);
            Scribe_Values.Look(ref outputMode, "outputMode", OutputMode.DropNear);
        }
    }

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
        }

        private static void InvalidateIfNeeded()
        {
            if (Current.Game != _cachedForGame)
            {
                _allCraftable = null;
                _byCategory = null;
                _allModNames = null;
                _cachedForGame = Current.Game;
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
                foreach (Thing t in map.listerThings.ThingsOfDef(def))
                    count += t.stackCount;
                if (def.minifiedDef != null)
                    foreach (Thing t in map.listerThings.ThingsMatching(
                                 ThingRequest.ForGroup(ThingRequestGroup.MinifiedThing)))
                        if (t is MinifiedThing mt && mt.InnerThing?.def == def)
                            count += t.stackCount;
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] CountOnMap failed for '{def?.defName}': {ex.Message}");
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

    // ─── Power Cost ───────────────────────────────────────────────────────────
    public static class OmniPowerCost
    {
        private static readonly float[] QualityMult = { 0.5f, 0.8f, 1.0f, 2.0f, 4.0f, 8.0f, 16.0f };

        public static float CostWd(ThingDef def, ThingDef stuff, QualityCategory quality, int count)
        {
            if (def == null) return 0f;
            float baseValue = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            if (baseValue < 1f) baseValue = 1f;
            return baseValue * QualityMult[(int)quality] * count;
        }

        public static float TotalStoredEnergy(PowerNet net)
        {
            return net?.CurrentStoredEnergy() ?? 0f;
        }

        public static bool TryDrainPower(PowerNet net, float amountWd)
        {
            if (net == null || net.CurrentStoredEnergy() < amountWd) return false;
            float remaining = amountWd;
            foreach (CompPowerBattery bat in net.batteryComps)
            {
                if (remaining <= 0f) break;
                float draw = Mathf.Min(bat.StoredEnergy, remaining);
                bat.DrawPower(draw);
                remaining -= draw;
            }

            return true;
        }
    }

    // ─── Building ─────────────────────────────────────────────────────────────
    public class Building_OmniCrafter : Building
    {
        public List<string> favorites = new List<string>();
        public List<string> recentCrafted = new List<string>();
        public List<AutoOrder> autoOrders = new List<AutoOrder>();

        private CompPowerTrader powerComp;

        private int rareTickCounter = 0;

        // TickRare = every 250 ticks; we want ~every 1000 ticks (4 rare ticks)
        private const int RareTicksPerCheck = 4;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref favorites, "favorites", LookMode.Value);
            Scribe_Collections.Look(ref recentCrafted, "recentCrafted", LookMode.Value);
            Scribe_Collections.Look(ref autoOrders, "autoOrders", LookMode.Deep);
            if (favorites == null) favorites = new List<string>();
            if (recentCrafted == null) recentCrafted = new List<string>();
            if (autoOrders == null) autoOrders = new List<AutoOrder>();
        }

        public override void TickRare()
        {
            base.TickRare();
            rareTickCounter++;
            if (rareTickCounter >= RareTicksPerCheck)
            {
                rareTickCounter = 0;
                ProcessAutoOrders();
            }
        }

        private void ProcessAutoOrders()
        {
            if (powerComp == null || !powerComp.PowerOn) return;
            PowerNet net = powerComp.PowerNet;
            if (net == null) return;
            foreach (AutoOrder order in autoOrders)
            {
                if (order.thingDef == null) continue;
                int current = OmniCrafterCache.CountOnMap(order.thingDef, Map);
                if (current >= order.targetCount) continue;
                int needed = order.targetCount - current;
                float cost = OmniPowerCost.CostWd(order.thingDef, order.stuffDef, order.quality, needed);
                if (!OmniPowerCost.TryDrainPower(net, cost)) continue;
                SpawnItems(order.thingDef, order.stuffDef, order.quality, needed, order.outputMode);
            }
        }

        public void AddRecent(ThingDef def)
        {
            recentCrafted.Remove(def.defName);
            recentCrafted.Insert(0, def.defName);
            if (recentCrafted.Count > 10) recentCrafted.RemoveAt(recentCrafted.Count - 1);
        }

        public void SpawnItems(ThingDef def, ThingDef stuff, QualityCategory quality, int count, OutputMode mode)
        {
            int remaining = count;
            while (remaining > 0)
            {
                // 建筑打包为 MinifiedThing，每次只能生成 1 个
                int stackMax = (def.category == ThingCategory.Building)
                    ? 1
                    : (def.stackLimit > 0 ? def.stackLimit : 1);
                int stackSize = Mathf.Min(remaining, stackMax);
                Thing thing = MakeThing(def, stuff, quality, stackSize);
                if (thing == null)
                {
                    remaining--;
                    continue;
                }

                if (mode == OutputMode.SendToStorage)
                {
                    IntVec3 storeCell;
                    IHaulDestination dest;
                    if (StoreUtility.TryFindBestBetterStorageFor(thing, null, Map, StoragePriority.Unstored,
                            Faction.OfPlayer, out storeCell, out dest))
                    {
                        if (storeCell.IsValid)
                            GenSpawn.Spawn(thing, storeCell, Map);
                        else
                            GenPlace.TryPlaceThing(thing, Position, Map, ThingPlaceMode.Near);
                    }
                    else
                        GenPlace.TryPlaceThing(thing, Position, Map, ThingPlaceMode.Near);
                }
                else
                    GenPlace.TryPlaceThing(thing, Position, Map, ThingPlaceMode.Near);

                remaining -= stackSize;
            }
        }

        private Thing MakeThing(ThingDef def, ThingDef stuff, QualityCategory quality, int count)
        {
            try
            {
                if (def.category == ThingCategory.Building)
                {
                    if (!def.Minifiable) return null;
                    ThingDef stuffToUse = def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null;
                    Thing inner = ThingMaker.MakeThing(def, stuffToUse);
                    SetQuality(inner, quality);
                    SetArt(inner, quality);
                    MinifiedThing minified = (MinifiedThing)ThingMaker.MakeThing(def.minifiedDef);
                    minified.InnerThing = inner;
                    return minified;
                }
                else
                {
                    ThingDef stuffToUse = def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null;
                    Thing thing = ThingMaker.MakeThing(def, stuffToUse);
                    thing.stackCount = Mathf.Clamp(count, 1, def.stackLimit > 0 ? def.stackLimit : 1);
                    SetQuality(thing, quality);
                    SetArt(thing, quality);
                    return thing;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] Failed to make {def?.defName}: {ex.Message}");
                return null;
            }
        }

        private static void SetQuality(Thing thing, QualityCategory quality)
        {
            thing.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Colony);
        }

        private static void SetArt(Thing thing, QualityCategory quality)
        {
            if (quality >= QualityCategory.Excellent)
            {
                CompArt art = thing.TryGetComp<CompArt>();
                if (art != null && !art.Active) art.InitializeArt(ArtGenerationContext.Colony);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos()) yield return g;
            yield return new Command_Action
            {
                defaultLabel = "OmniCrafter_OpenUI".Translate(),
                defaultDesc = "OmniCrafter_OpenUIDesc".Translate(),
                icon = FullyAutomaticOmniCrafterTex.IconLaunchReport,
                action = () => Find.WindowStack.Add(new Dialog_OmniCrafter(this))
            };
        }
    }

    [StaticConstructorOnStartup]
    public static class FullyAutomaticOmniCrafterTex
    {
        public static readonly Texture2D IconLaunchReport =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_LaunchReport", true) ?? BaseContent.WhiteTex;
    }

    // ─── Dialog ───────────────────────────────────────────────────────────────
    public class Dialog_OmniCrafter : Window
    {
        private readonly Building_OmniCrafter building;

        private ThingCategoryDef selectedCategory;
        private bool showFavorites;
        private bool showRecent;

        private string searchText = "";
        private List<ThingDef> searchCache;
        private string lastSearch = "";

        private Vector2 middleScroll;
        private Vector2 leftScroll;
        private Vector2 rightScroll;

        private ThingDef selectedDef;
        private List<ThingDef> currentList;

        private enum SortMode
        {
            Name,
            Value,
            Weight
        }

        private SortMode sortMode = SortMode.Name;

        // Mod Filter
        private string selectedModFilter = null; // null = show all mods

        private ThingDef selectedStuff;
        private QualityCategory selectedQuality = QualityCategory.Normal;
        private int craftCount = 1;
        private ProductionMode productionMode = ProductionMode.FixedCount;
        private int maintainCount = 10;
        private OutputMode outputMode = OutputMode.DropNear;
        private List<ThingDef> validStuffs;

        public override Vector2 InitialSize => new Vector2(1100f, 700f);

        public Dialog_OmniCrafter(Building_OmniCrafter building)
        {
            this.building = building;
            doCloseButton = true;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
        }

        private List<ThingDef> CurrentList
        {
            get
            {
                if (currentList == null) currentList = BuildFilteredList();
                return currentList;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float topH = 36f;
            Rect topRect = new Rect(inRect.x, inRect.y, inRect.width, topH);
            float bodyY = inRect.y + topH + 4f;
            float bodyH = inRect.height - topH - 52f;
            Rect bodyRect = new Rect(inRect.x, bodyY, inRect.width, bodyH);

            DrawTopBar(topRect);

            float leftW = 200f;
            float rightW = 340f;
            float midW = bodyRect.width - leftW - rightW - 8f;

            DrawLeftPanel(new Rect(bodyRect.x, bodyRect.y, leftW, bodyRect.height));
            DrawMiddlePanel(new Rect(bodyRect.x + leftW + 4f, bodyRect.y, midW, bodyRect.height));
            DrawRightPanel(new Rect(bodyRect.x + leftW + 4f + midW + 4f, bodyRect.y, rightW, bodyRect.height));
        }

        private void DrawTopBar(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 220f, rect.height), "OmniCrafter_Title".Translate());
            Text.Font = GameFont.Small;

            float sx = rect.x + 230f;
            Widgets.Label(new Rect(sx, rect.y + 4f, 50f, 26f), "Search:");
            string ns = Widgets.TextField(new Rect(sx + 52f, rect.y + 4f, 280f, 26f), searchText);
            if (ns != searchText)
            {
                searchText = ns;
                searchCache = null;
                currentList = null;
            }
        }

        private void DrawLeftPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            rect = rect.ContractedBy(3f);
            float lh = 26f;
            var cats = OmniCrafterCache.ByCategory.OrderBy(k => k.Key.label ?? k.Key.defName).ToList();
            float totalH = lh * 2 + 6f + lh * cats.Count;
            Rect view = new Rect(0f, 0f, rect.width - 16f, totalH);
            Widgets.BeginScrollView(rect, ref leftScroll, view);
            float y = 0f;

            DrawNavItem(new Rect(0f, y, view.width, lh), "★ " + "OmniCrafter_Favorites".Translate(),
                showFavorites, () =>
                {
                    showFavorites = true;
                    showRecent = false;
                    selectedCategory = null;
                    currentList = null;
                    searchCache = null;
                });
            y += lh;
            DrawNavItem(new Rect(0f, y, view.width, lh), "⟳ " + "OmniCrafter_Recent".Translate(),
                showRecent, () =>
                {
                    showRecent = true;
                    showFavorites = false;
                    selectedCategory = null;
                    currentList = null;
                    searchCache = null;
                });
            y += lh + 6f;

            foreach (var kv in cats)
            {
                ThingCategoryDef cat = kv.Key;
                string label = (cat.label ?? cat.defName).CapitalizeFirst() + $" ({kv.Value.Count})";
                DrawNavItem(new Rect(0f, y, view.width, lh), label, selectedCategory == cat,
                    () =>
                    {
                        selectedCategory = cat;
                        showFavorites = false;
                        showRecent = false;
                        currentList = null;
                        searchCache = null;
                    });
                y += lh;
            }

            Widgets.EndScrollView();
        }

        private void DrawNavItem(Rect r, string label, bool selected, Action onClick)
        {
            if (selected) Widgets.DrawHighlight(r);
            else if (Mouse.IsOver(r)) Widgets.DrawHighlightIfMouseover(r);
            Widgets.Label(r.ContractedBy(2f, 0f), label);
            if (Widgets.ButtonInvisible(r)) onClick();
        }

        private List<ThingDef> BuildFilteredList()
        {
            List<ThingDef> source;
            if (showFavorites)
                source = building.favorites.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
                    .Where(d => d != null).ToList();
            else if (showRecent)
                source = building.recentCrafted.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
                    .Where(d => d != null).ToList();
            else if (selectedCategory != null)
            {
                List<ThingDef> catList;
                OmniCrafterCache.ByCategory.TryGetValue(selectedCategory, out catList);
                source = catList ?? new List<ThingDef>();
            }
            else
                source = OmniCrafterCache.AllCraftable;

            // Mod 筛选器
            if (selectedModFilter != null)
                source = source.Where(d => OmniCrafterCache.GetModName(d) == selectedModFilter).ToList();

            string q = searchText?.ToLower() ?? "";
            if (!q.NullOrEmpty())
            {
                if (searchCache == null || lastSearch != q)
                {
                    lastSearch = q;
                    searchCache = source.Where(d =>
                    {
                        try
                        {
                            return (d.label != null && d.label.ToLower().Contains(q)) ||
                                   (d.defName != null && d.defName.ToLower().Contains(q));
                        }
                        catch
                        {
                            return false;
                        }
                    }).ToList();
                }

                source = searchCache;
            }

            try
            {
                switch (sortMode)
                {
                    case SortMode.Value:
                        return source.OrderByDescending(d =>
                        {
                            try
                            {
                                return d.GetStatValueAbstract(StatDefOf.MarketValue);
                            }
                            catch
                            {
                                return 0f;
                            }
                        }).ToList();
                    case SortMode.Weight:
                        return source.OrderBy(d =>
                        {
                            try
                            {
                                return d.GetStatValueAbstract(StatDefOf.Mass);
                            }
                            catch
                            {
                                return 0f;
                            }
                        }).ToList();
                    default: return source.OrderBy(d => d.label ?? d.defName).ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] BuildFilteredList sort failed: {ex.Message}");
                return source;
            }
        }

        private void DrawMiddlePanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f, 0.5f));

            // Sort bar
            Rect sortBar = new Rect(rect.x, rect.y, rect.width, 28f);
            DrawSortButtons(sortBar);

            // Mod filter bar
            Rect modFilterBar = new Rect(rect.x, rect.y + 32f, rect.width, 28f);
            DrawModFilterBar(modFilterBar);

            Rect gridArea = new Rect(rect.x, rect.y + 64f, rect.width, rect.height - 64f);
            List<ThingDef> list = CurrentList;

            float iconSize = 64f;
            float pad = 4f;
            int cols = Mathf.Max(1, (int)((gridArea.width - pad) / (iconSize + pad)));
            int rows = list.Count > 0 ? Mathf.CeilToInt((float)list.Count / cols) : 1;
            float totalH = rows * (iconSize + pad) + pad;

            Rect view = new Rect(0f, 0f, gridArea.width - 16f, totalH);
            Widgets.BeginScrollView(gridArea, ref middleScroll, view);
            float scrollY = middleScroll.y;
            float visH = gridArea.height;

            for (int i = 0; i < list.Count; i++)
            {
                int row = i / cols, col = i % cols;
                float x = pad + col * (iconSize + pad);
                float y = pad + row * (iconSize + pad);

                if (y + iconSize < scrollY || y > scrollY + visH) continue;

                ThingDef def = list[i];
                Rect ir = new Rect(x, y, iconSize, iconSize);

                if (selectedDef == def) Widgets.DrawHighlight(ir);
                else Widgets.DrawHighlightIfMouseover(ir);

                // 外源异常捕获：某些 Mod 的物品可能缺少贴图或图标数据
                try
                {
                    Widgets.ThingIcon(ir, def);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[OmniCrafter] ThingIcon failed for '{def?.defName}': {ex.Message}");
                    GUI.color = Color.gray;
                    Widgets.DrawBox(ir);
                    GUI.color = Color.white;
                }

                if (building.favorites.Contains(def.defName))
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(new Rect(x, y, 12f, 12f), "★");
                    GUI.color = Color.white;
                }

                // 外源异常捕获：description 可能含非法字符
                try
                {
                    string tip = def.LabelCap + (def.description.NullOrEmpty() ? "" : "\n" + def.description);
                    TooltipHandler.TipRegion(ir, tip);
                }
                catch
                {
                    TooltipHandler.TipRegion(ir, def?.defName ?? "?");
                }

                if (Widgets.ButtonInvisible(ir)) SelectDef(def);
            }

            Widgets.EndScrollView();
        }

        private void DrawModFilterBar(Rect rect)
        {
            float x = rect.x + 4f;
            Widgets.Label(new Rect(x, rect.y + 2f, 44f, 24f), "Mod:");
            x += 46f;

            // "All" button
            if (selectedModFilter == null) GUI.color = Color.cyan;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 48f, 24f), "All"))
            {
                selectedModFilter = null;
                currentList = null;
                searchCache = null;
            }

            GUI.color = Color.white;
            x += 50f;

            // Show current filter name button (opens FloatMenu to pick a mod)
            string currentLabel = selectedModFilter ?? "All";
            string btnLabel = currentLabel.Length > 20 ? currentLabel.Substring(0, 18) + "…" : currentLabel;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 180f, 24f), $"▼ {btnLabel}"))
            {
                var modNames = OmniCrafterCache.AllModNames;
                var options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("All Mods", () =>
                {
                    selectedModFilter = null;
                    currentList = null;
                    searchCache = null;
                }));
                foreach (string mod in modNames)
                {
                    string captured = mod;
                    options.Add(new FloatMenuOption(captured, () =>
                    {
                        selectedModFilter = captured;
                        currentList = null;
                        searchCache = null;
                    }));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        private void DrawSortButtons(Rect rect)
        {
            float x = rect.x + 4f;
            Widgets.Label(new Rect(x, rect.y + 2f, 36f, 24f), "Sort:");
            x += 38f;
            foreach (SortMode sm in Enum.GetValues(typeof(SortMode)))
            {
                if (sortMode == sm) GUI.color = Color.cyan;
                if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 72f, 24f), sm.ToString()))
                {
                    sortMode = sm;
                    currentList = null;
                }

                GUI.color = Color.white;
                x += 74f;
            }
        }

        private void SelectDef(ThingDef def)
        {
            selectedDef = def;
            validStuffs = def.MadeFromStuff ? OmniCrafterCache.GetValidStuffs(def) : null;
            selectedStuff = validStuffs != null && validStuffs.Count > 0 ? validStuffs[0] : null;
            selectedQuality = QualityCategory.Normal;
            craftCount = 1;
        }

        private void DrawRightPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.12f, 0.12f, 0.12f, 0.6f));
            rect = rect.ContractedBy(6f);

            if (selectedDef == null)
            {
                Widgets.Label(rect, "OmniCrafter_SelectItem".Translate());
                return;
            }

            float y = rect.y;
            float w = rect.width;

            // Icon + Name + Favorite
            Rect ir = new Rect(rect.x, y, 64f, 64f);
            try
            {
                Widgets.ThingIcon(ir, selectedDef, selectedStuff);
            }
            catch
            {
                Widgets.DrawBox(ir);
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x + 70f, y, w - 70f, 32f), selectedDef.LabelCap);
            Text.Font = GameFont.Small;
            bool isFav = building.favorites.Contains(selectedDef.defName);
            if (Widgets.ButtonText(new Rect(rect.x + 70f, y + 34f, 120f, 24f),
                    isFav ? "★ Unfavorite" : "☆ Favorite"))
            {
                if (isFav) building.favorites.Remove(selectedDef.defName);
                else building.favorites.Add(selectedDef.defName);
            }

            y += 70f;

            if (!selectedDef.description.NullOrEmpty())
            {
                string desc = selectedDef.description.Length > 160
                    ? selectedDef.description.Substring(0, 157) + "..."
                    : selectedDef.description;
                Widgets.Label(new Rect(rect.x, y, w, 56f), desc);
                y += 60f;
            }

            Widgets.DrawLineHorizontal(rect.x, y, w);
            y += 6f;

            // Stuff
            if (validStuffs != null && validStuffs.Count > 0)
            {
                Widgets.Label(new Rect(rect.x, y, w, 20f), "Material:");
                y += 22f;
                int stuffCols = Mathf.Max(1, (int)(w / 88f));
                for (int i = 0; i < validStuffs.Count; i++)
                {
                    Rect sr = new Rect(rect.x + (i % stuffCols) * 88f, y + (i / stuffCols) * 26f, 84f, 24f);
                    if (selectedStuff == validStuffs[i]) GUI.color = Color.cyan;
                    if (Widgets.ButtonText(sr, (validStuffs[i].label ?? validStuffs[i].defName).CapitalizeFirst()))
                        selectedStuff = validStuffs[i];
                    GUI.color = Color.white;
                }

                y += Mathf.CeilToInt((float)validStuffs.Count / stuffCols) * 26f + 4f;
                Widgets.DrawLineHorizontal(rect.x, y, w);
                y += 6f;
            }

            // Quality
            if (selectedDef.HasComp(typeof(CompQuality)))
            {
                Widgets.Label(new Rect(rect.x, y, w, 20f), "Quality:");
                y += 22f;
                QualityCategory[] quals = (QualityCategory[])Enum.GetValues(typeof(QualityCategory));
                int qCols = 4;
                float qW = w / qCols - 2f;
                for (int i = 0; i < quals.Length; i++)
                {
                    Rect qr = new Rect(rect.x + (i % qCols) * (qW + 2f), y + (i / qCols) * 26f, qW, 24f);
                    if (selectedQuality == quals[i]) GUI.color = Color.cyan;
                    if (Widgets.ButtonText(qr, quals[i].GetLabel())) selectedQuality = quals[i];
                    GUI.color = Color.white;
                }

                y += Mathf.CeilToInt((float)quals.Length / qCols) * 26f + 4f;
                Widgets.DrawLineHorizontal(rect.x, y, w);
                y += 6f;
            }

            // Production mode
            Widgets.Label(new Rect(rect.x, y, w, 20f), "Mode:");
            y += 22f;
            if (Widgets.RadioButtonLabeled(new Rect(rect.x, y, w / 2f - 2f, 24f), "Fixed Count",
                    productionMode == ProductionMode.FixedCount))
                productionMode = ProductionMode.FixedCount;
            if (Widgets.RadioButtonLabeled(new Rect(rect.x + w / 2f, y, w / 2f - 2f, 24f), "Maintain Stock",
                    productionMode == ProductionMode.MaintainStock))
                productionMode = ProductionMode.MaintainStock;
            y += 28f;

            if (productionMode == ProductionMode.FixedCount)
            {
                Widgets.Label(new Rect(rect.x, y, 60f, 24f), "Count:");
                string cs = Widgets.TextField(new Rect(rect.x + 62f, y, 55f, 24f), craftCount.ToString());
                if (int.TryParse(cs, out int cp) && cp > 0) craftCount = cp;
                if (Widgets.ButtonText(new Rect(rect.x + 120f, y, 24f, 24f), "+")) craftCount++;
                if (craftCount > 1 && Widgets.ButtonText(new Rect(rect.x + 146f, y, 24f, 24f), "-")) craftCount--;
            }
            else
            {
                Widgets.Label(new Rect(rect.x, y, 100f, 24f), "Target Stock:");
                string ms = Widgets.TextField(new Rect(rect.x + 102f, y, 55f, 24f), maintainCount.ToString());
                if (int.TryParse(ms, out int mp) && mp > 0) maintainCount = mp;
                if (Widgets.ButtonText(new Rect(rect.x + 160f, y, 24f, 24f), "+")) maintainCount++;
                if (maintainCount > 1 && Widgets.ButtonText(new Rect(rect.x + 186f, y, 24f, 24f), "-")) maintainCount--;
            }

            y += 28f;

            // Output mode
            Widgets.Label(new Rect(rect.x, y, w, 20f), "Output:");
            y += 22f;
            if (Widgets.RadioButtonLabeled(new Rect(rect.x, y, w, 24f), "Drop Near", outputMode == OutputMode.DropNear))
                outputMode = OutputMode.DropNear;
            y += 26f;
            if (Widgets.RadioButtonLabeled(new Rect(rect.x, y, w, 24f), "Send To Storage",
                    outputMode == OutputMode.SendToStorage))
                outputMode = OutputMode.SendToStorage;
            y += 30f;

            Widgets.DrawLineHorizontal(rect.x, y, w);
            y += 6f;

            // Current stock
            int currentStock = OmniCrafterCache.CountOnMap(selectedDef, building.Map);
            Widgets.Label(new Rect(rect.x, y, w, 20f), $"On Map: {currentStock}");
            y += 22f;

            // Power cost
            CompPowerTrader pwr = building.GetComp<CompPowerTrader>();
            float stored = OmniPowerCost.TotalStoredEnergy(pwr?.PowerNet);
            int countForCost;
            if (productionMode == ProductionMode.FixedCount)
                countForCost = craftCount;
            else
                countForCost = Mathf.Max(0, maintainCount - currentStock);
            // 库存满或无需生产时费用为0，避免误导性显示
            float cost = countForCost > 0
                ? OmniPowerCost.CostWd(selectedDef, selectedStuff, selectedQuality, countForCost)
                : 0f;
            bool canAfford = countForCost <= 0 || stored >= cost;

            GUI.color = canAfford ? Color.white : Color.red;
            string costLabel = countForCost <= 0
                ? $"✔ Stock full ({currentStock}/{maintainCount})\nStored: {stored:F0} Wd"
                : $"Power Cost: {cost:F0} Wd  /  Stored: {stored:F0} Wd";
            if (!canAfford) costLabel += "\n⚠ Insufficient power!";
            Widgets.Label(new Rect(rect.x, y, w, 44f), costLabel);
            GUI.color = Color.white;
            y += 48f;

            // Building 提示：每次仅能生产1个（MinifiedThing不可堆叠）
            if (selectedDef.category == ThingCategory.Building && selectedDef.Minifiable && countForCost > 1)
            {
                GUI.color = new Color(1f, 0.85f, 0.2f);
                Widgets.Label(new Rect(rect.x, y, w, 20f), "⚠ Buildings craft 1 at a time (minified)");
                GUI.color = Color.white;
                y += 22f;
            }

            // Action button
            if (productionMode == ProductionMode.FixedCount)
            {
                Color btnColor = canAfford ? Color.white : new Color(0.5f, 0.5f, 0.5f);
                GUI.color = btnColor;
                if (Widgets.ButtonText(new Rect(rect.x, y, w, 32f), $"Craft x{craftCount}"))
                {
                    if (canAfford)
                    {
                        OmniPowerCost.TryDrainPower(pwr?.PowerNet, cost);
                        building.SpawnItems(selectedDef, selectedStuff, selectedQuality, craftCount, outputMode);
                        building.AddRecent(selectedDef);
                        SoundDefOf.ExecuteTrade.PlayOneShotOnCamera();
                    }
                    else
                        Messages.Message("OmniCrafter_NotEnoughPower".Translate(), building,
                            MessageTypeDefOf.RejectInput, false);
                }

                GUI.color = Color.white;
                y += 36f;
            }
            else
            {
                bool hasOrder = building.autoOrders.Any(o => o.thingDef == selectedDef);
                if (hasOrder)
                {
                    if (Widgets.ButtonText(new Rect(rect.x, y, w, 32f), "Remove Auto Order"))
                        building.autoOrders.RemoveAll(o => o.thingDef == selectedDef);
                }
                else
                {
                    if (Widgets.ButtonText(new Rect(rect.x, y, w, 32f), "Add Auto Order"))
                    {
                        building.autoOrders.Add(new AutoOrder
                        {
                            thingDef = selectedDef, stuffDef = selectedStuff,
                            quality = selectedQuality, targetCount = maintainCount, outputMode = outputMode
                        });
                        building.AddRecent(selectedDef);
                    }
                }

                y += 36f;
            }

            // Auto order list
            if (building.autoOrders.Count > 0)
            {
                Widgets.DrawLineHorizontal(rect.x, y, w);
                y += 4f;
                Widgets.Label(new Rect(rect.x, y, w, 20f), $"Auto Orders ({building.autoOrders.Count}):");
                y += 22f;
                float listH = rect.yMax - y;
                if (listH > 20f)
                {
                    Rect lr = new Rect(rect.x, y, w, listH);
                    Rect lv = new Rect(0f, 0f, w - 16f, building.autoOrders.Count * 22f);
                    Widgets.BeginScrollView(lr, ref rightScroll, lv);
                    for (int i = 0; i < building.autoOrders.Count; i++)
                    {
                        AutoOrder ao = building.autoOrders[i];
                        int onMap = OmniCrafterCache.CountOnMap(ao.thingDef, building.Map);
                        bool full = onMap >= ao.targetCount;
                        GUI.color = full ? Color.green : Color.white;
                        string lbl = (ao.thingDef?.LabelCap ?? "?") +
                                     $" {onMap}/{ao.targetCount} [{ao.quality.GetLabel()}]";
                        Widgets.Label(new Rect(0f, i * 22f, lv.width - 24f, 20f), lbl);
                        GUI.color = Color.white;
                        if (Widgets.ButtonText(new Rect(lv.width - 22f, i * 22f, 22f, 20f), "X"))
                        {
                            building.autoOrders.RemoveAt(i);
                            break;
                        }
                    }

                    Widgets.EndScrollView();
                }
            }
        }
    }
}