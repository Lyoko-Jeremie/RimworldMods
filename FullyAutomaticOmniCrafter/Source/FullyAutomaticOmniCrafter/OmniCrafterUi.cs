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
    // ─── Category Tree Listing ────────────────────────────────────────────────
    /// <summary>
    /// 原版风格树状菜单，用于单选 ThingCategoryDef。
    /// 继承自游戏原版 Listing_Tree，复用其展开/折叠小部件与缩进逻辑。
    /// </summary>
    public class Listing_TreeCategorySelect : Listing_Tree
    {
        private readonly ThingCategoryDef _selected;
        private readonly Action<ThingCategoryDef> _onSelect;
        private readonly HashSet<ThingCategoryDef> _validCats;
        private Rect _visibleRect;

        public Listing_TreeCategorySelect(
            HashSet<ThingCategoryDef> validCats,
            ThingCategoryDef selected,
            Action<ThingCategoryDef> onSelect)
        {
            _validCats = validCats;
            _selected = selected;
            _onSelect = onSelect;
            lineHeight = 24f;
        }

        public void SetVisibleRect(Rect r)
        {
            _visibleRect = r;
        }

        public void DoCategoryNode(TreeNode_ThingCategory node, int indentLevel, int openMask)
        {
            if (!_validCats.Contains(node.catDef)) return;

            bool hasChildren = node.catDef.childCategories.Any(c => _validCats.Contains(c));
            bool onScreen = curY + lineHeight >= _visibleRect.yMin && curY <= _visibleRect.yMax;

            if (onScreen)
            {
                bool isSelected = _selected == node.catDef;
                if (isSelected)
                    Widgets.DrawHighlight(new Rect(0f, curY, ColumnWidth, lineHeight));

                if (hasChildren)
                    OpenCloseWidget(node, indentLevel, openMask);

                LabelLeft(node.LabelCap, node.catDef.description, indentLevel);

                // 点击整行选中该分类
                float xMin = XAtIndentLevel(indentLevel);
                Rect clickRect = new Rect(xMin, curY, ColumnWidth - xMin, lineHeight);
                if (Widgets.ButtonInvisible(clickRect))
                    _onSelect(node.catDef);
            }

            EndLine();

            if (IsOpen(node, openMask) && hasChildren)
            {
                foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
                    DoCategoryNode(child, indentLevel + 1, openMask);
            }
        }
    }

    // ─── Dialog ───────────────────────────────────────────────────────────────
    public class Dialog_OmniCrafter : Window
    {
        private readonly Building_OmniCrafter building;

        private ThingCategoryDef selectedCategory;
        private bool showAll = true; // default: show all
        private bool showFavorites;
        private bool showRecent;


        private string searchText = "";
        private List<ThingDef> searchCache;
        private string lastSearch = "";

        // Favorites that reference defNames which no longer exist in the current game
        private List<string> orphanedFavorites = new List<string>();

        private Vector2 middleScroll;
        private Vector2 leftScroll;
        private Vector2 rightPanelScroll;
        private Vector2 farRightScroll;

        private ThingDef selectedDef;
        private AutoOrder selectedAutoOrder; // highlighted row in far-right panel
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
        private bool storageOnly = false;
        private List<ThingDef> validStuffs;

        public override Vector2 InitialSize => new Vector2(1350f, 700f);

        public Dialog_OmniCrafter(Building_OmniCrafter building)
        {
            this.building = building;
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
        }


        /// <summary>当前选中的是一条自动订单时，将面板中的设置同步回该订单。</summary>
        private void SyncSelectedAutoOrder()
        {
            if (selectedAutoOrder == null) return;
            selectedAutoOrder.stuffDef = selectedStuff;
            selectedAutoOrder.quality = selectedQuality;
            selectedAutoOrder.targetCount = maintainCount;
            selectedAutoOrder.outputMode = outputMode;
            selectedAutoOrder.storageOnly = storageOnly;
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
            float farRightW = 300f;
            float midRightW = 360f;
            float midLeftW = bodyRect.width - leftW - midRightW - farRightW - 12f;

            float x0 = bodyRect.x;
            float x1 = x0 + leftW + 4f;
            float x2 = x1 + midLeftW + 4f;
            float x3 = x2 + midRightW + 4f;

            DrawLeftPanel(new Rect(x0, bodyRect.y, leftW, bodyRect.height));
            DrawMiddlePanel(new Rect(x1, bodyRect.y, midLeftW, bodyRect.height));
            DrawMidRightPanel(new Rect(x2, bodyRect.y, midRightW, bodyRect.height));
            DrawFarRightPanel(new Rect(x3, bodyRect.y, farRightW, bodyRect.height));
        }

        private void DrawTopBar(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 220f, rect.height), "OmniCrafter_Title".Translate());
            Text.Font = GameFont.Small;

            float sx = rect.x + 230f;
            Widgets.Label(new Rect(sx, rect.y + 4f, 50f, 26f), "OmniCrafter_Search".Translate());
            string ns = Widgets.TextField(new Rect(sx + 52f, rect.y + 4f, 280f, 26f), searchText);
            if (ns != searchText)
            {
                searchText = ns;
                searchCache = null;
                currentList = null;
            }

            // Debug: free-craft switch (God mode only)
            if (DebugSettings.godMode)
            {
                float dbX = sx + 52f + 280f + 16f;
                bool dbFlag = Building_OmniCrafter.debugNoPowerRequired;
                GUI.color = dbFlag ? new Color(1f, 0.5f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
                Widgets.CheckboxLabeled(
                    new Rect(dbX, rect.y + 4f, 200f, 26f),
                    "⚡ " + "OmniCrafter_DebugNoPower".Translate(),
                    ref dbFlag);
                Building_OmniCrafter.debugNoPowerRequired = dbFlag;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(
                    new Rect(dbX, rect.y + 4f, 200f, 26f),
                    "OmniCrafter_DebugNoPowerTip".Translate());
            }
            else
            {
                // 退出 God 模式时自动关闭
                Building_OmniCrafter.debugNoPowerRequired = false;
            }
        }

        // ── Left panel helpers ───────────────────────────────────────────────

        /// <summary>返回包含可制造物品的所有分类及其祖先分类的集合。</summary>
        private HashSet<ThingCategoryDef> GetValidCategorySet()
        {
            var set = new HashSet<ThingCategoryDef>();
            foreach (ThingCategoryDef cat in OmniCrafterCache.ByCategory.Keys)
            {
                ThingCategoryDef c = cat;
                while (c != null)
                {
                    set.Add(c);
                    c = c.parent;
                }
            }

            return set;
        }

        /// <summary>递归计算树的总虚拟高度（用于滚动视图）。</summary>
        private float ComputeTreeHeight(TreeNode_ThingCategory node, HashSet<ThingCategoryDef> validCats, float lh)
        {
            float h = 0f;
            foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
            {
                if (!validCats.Contains(child.catDef)) continue;
                h += lh + 2f; // lineHeight + verticalSpacing
                if (child.IsOpen(1))
                    h += ComputeTreeHeight(child, validCats, lh);
            }

            return h;
        }

        private void DrawLeftPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            rect = rect.ContractedBy(3f);
            float lh = 24f;

            var validCats = GetValidCategorySet();
            float treeH = ComputeTreeHeight(ThingCategoryDefOf.Root.treeNode, validCats, lh);
            float totalH = (lh + 2f) * 3 + 6f + treeH;

            Rect view = new Rect(0f, 0f, rect.width - 16f, totalH);
            Widgets.BeginScrollView(rect, ref leftScroll, view);
            float y = 0f;

            // 全部
            DrawNavItem(new Rect(0f, y, view.width, lh), "OmniCrafter_All".Translate(),
                showAll, () =>
                {
                    showAll = true;
                    showFavorites = false;
                    showRecent = false;
                    selectedCategory = null;
                    currentList = null;
                    searchCache = null;
                });
            y += lh + 2f;

            // 收藏
            DrawNavItem(new Rect(0f, y, view.width, lh), "★ " + "OmniCrafter_Favorites".Translate(),
                showFavorites, () =>
                {
                    showFavorites = true;
                    showRecent = false;
                    showAll = false;
                    selectedCategory = null;
                    currentList = null;
                    searchCache = null;
                });
            y += lh + 2f;

            // 最近
            DrawNavItem(new Rect(0f, y, view.width, lh), "⟳ " + "OmniCrafter_Recent".Translate(),
                showRecent, () =>
                {
                    showRecent = true;
                    showFavorites = false;
                    showAll = false;
                    selectedCategory = null;
                    currentList = null;
                    searchCache = null;
                });
            y += lh + 2f + 6f;

            // 原版树状分类菜单
            float treeAreaH = Mathf.Max(treeH, 1f);
            Rect treeRect = new Rect(0f, y, view.width, treeAreaH);
            // 可视区域（相对于树的局部坐标）
            Rect visibleRect = new Rect(0f, leftScroll.y - y, view.width, rect.height);

            var listing = new Listing_TreeCategorySelect(
                validCats,
                selectedCategory,
                cat =>
                {
                    selectedCategory = cat;
                    showFavorites = false;
                    showRecent = false;
                    showAll = false;
                    currentList = null;
                    searchCache = null;
                });
            listing.SetVisibleRect(visibleRect);
            listing.Begin(treeRect);
            foreach (TreeNode_ThingCategory child in ThingCategoryDefOf.Root.treeNode.ChildCategoryNodes)
                listing.DoCategoryNode(child, 0, 1);
            listing.End();

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
            orphanedFavorites.Clear();
            List<ThingDef> source;
            if (showFavorites)
            {
                source = new List<ThingDef>();
                foreach (string name in OmniCrafterMod.Settings.globalFavorites)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def != null) source.Add(def);
                    else orphanedFavorites.Add(name);
                }
            }
            else if (showRecent)
                source = building.recentCrafted.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
                    .Where(d => d != null).ToList();
            else if (selectedCategory != null && !showAll)
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

            Rect listArea = new Rect(rect.x, rect.y + 64f, rect.width, rect.height - 64f);
            List<ThingDef> list = CurrentList;

            float rowH = 36f;
            float iconSize = 30f;
            float infoBtnW = 26f;
            float orphanRowH = 30f;
            float totalH = list.Count * rowH + orphanedFavorites.Count * orphanRowH;

            Rect view = new Rect(0f, 0f, listArea.width - 16f, totalH);
            Widgets.BeginScrollView(listArea, ref middleScroll, view);
            float scrollY = middleScroll.y;
            float visH = listArea.height;
            float viewW = view.width;

            for (int i = 0; i < list.Count; i++)
            {
                float y = i * rowH;
                if (y + rowH < scrollY || y > scrollY + visH) continue;

                ThingDef def = list[i];
                Rect rowRect = new Rect(0f, y, viewW, rowH);

                // Background highlight
                if (selectedDef == def) Widgets.DrawHighlight(rowRect);
                else if (i % 2 == 0) Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.03f));
                if (Mouse.IsOver(rowRect) && selectedDef != def) Widgets.DrawHighlightIfMouseover(rowRect);

                // Icon
                float iconX = 3f;
                float iconY = y + (rowH - iconSize) / 2f;
                Rect iconRect = new Rect(iconX, iconY, iconSize, iconSize);
                try
                {
                    Widgets.ThingIcon(iconRect, def);
                }
                catch
                {
                    GUI.color = Color.gray;
                    Widgets.DrawBox(iconRect);
                    GUI.color = Color.white;
                }

                // Favorite star
                if (OmniCrafterMod.Settings.globalFavorites.Contains(def.defName))
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(new Rect(iconX, iconY, 12f, 12f), "★");
                    GUI.color = Color.white;
                }

                // Label
                float labelX = iconX + iconSize + 4f;
                float labelW = viewW - labelX - infoBtnW - 4f;
                Rect labelRect = new Rect(labelX, y + (rowH - 20f) / 2f, labelW, 20f);
                Widgets.Label(labelRect, def.LabelCap);

                // Info "i" button
                Rect infoRect = new Rect(viewW - infoBtnW, y + (rowH - 24f) / 2f, infoBtnW, 24f);
                if (Widgets.ButtonText(infoRect, "i"))
                {
                    ThingDef stuffForInfo = def.MadeFromStuff
                        ? (selectedDef == def && selectedStuff != null ? selectedStuff : GenStuff.DefaultStuffFor(def))
                        : null;
                    Find.WindowStack.Add(new Dialog_InfoCard(def, stuffForInfo));
                }

                // Tooltip
                try
                {
                    string tip = def.LabelCap + (def.description.NullOrEmpty() ? "" : "\n" + def.description);
                    TooltipHandler.TipRegion(labelRect, tip);
                }
                catch
                {
                    TooltipHandler.TipRegion(labelRect, def?.defName ?? "?");
                }

                // Click row to select
                Rect clickRect = new Rect(0f, y, viewW - infoBtnW - 2f, rowH);
                if (Widgets.ButtonInvisible(clickRect)) SelectDef(def);
            }

            // ── Orphaned favorites (defName exists in favorites but ThingDef is missing) ──
            if (showFavorites && orphanedFavorites.Count > 0)
            {
                float orphanY = list.Count * rowH;
                for (int i = 0; i < orphanedFavorites.Count; i++)
                {
                    float y = orphanY + i * orphanRowH;
                    if (y + orphanRowH < scrollY || y > scrollY + visH) continue;

                    string defName = orphanedFavorites[i];
                    Rect rowRect = new Rect(0f, y, viewW, orphanRowH);

                    if (i % 2 == 0) Widgets.DrawBoxSolid(rowRect, new Color(1f, 0.3f, 0.3f, 0.08f));
                    if (Mouse.IsOver(rowRect)) Widgets.DrawHighlightIfMouseover(rowRect);

                    // Gray placeholder icon
                    float iconX = 3f;
                    float iconY2 = y + (orphanRowH - 20f) / 2f;
                    GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);
                    Widgets.DrawBox(new Rect(iconX, y + (orphanRowH - 20f) / 2f, 20f, 20f));
                    GUI.color = Color.white;

                    // Label (defName + missing hint)
                    float labelX = iconX + 24f;
                    float labelW = viewW - labelX - 60f;
                    GUI.color = new Color(0.7f, 0.5f, 0.5f);
                    Widgets.Label(new Rect(labelX, iconY2, labelW, 20f),
                        $"[?] {defName}");
                    GUI.color = Color.white;

                    // Tooltip
                    TooltipHandler.TipRegion(rowRect, "OmniCrafter_MissingDef".Translate(defName));

                    // Unfavorite button
                    float btnW = 54f;
                    if (Widgets.ButtonText(new Rect(viewW - btnW, y + (orphanRowH - 22f) / 2f, btnW, 22f),
                            "OmniCrafter_Unfavorite".Translate()))
                    {
                        OmniCrafterMod.Settings.globalFavorites.Remove(defName);
                        OmniCrafterMod.Settings.Write();
                        orphanedFavorites.RemoveAt(i);
                        currentList = null;
                        break;
                    }
                }
            }

            Widgets.EndScrollView();
        }

        private void DrawModFilterBar(Rect rect)
        {
            float x = rect.x + 4f;
            Widgets.Label(new Rect(x, rect.y + 2f, 44f, 24f), "OmniCrafter_Mod".Translate());
            x += 46f;

            // "All" button
            if (selectedModFilter == null) GUI.color = Color.cyan;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 48f, 24f), "OmniCrafter_ModAll".Translate()))
            {
                selectedModFilter = null;
                currentList = null;
                searchCache = null;
            }

            GUI.color = Color.white;
            x += 50f;

            // Show current filter name button (opens FloatMenu to pick a mod)
            string currentLabel = selectedModFilter ?? "OmniCrafter_ModAll".Translate();
            string btnLabel = currentLabel.Length > 20 ? currentLabel.Substring(0, 18) + "…" : currentLabel;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 180f, 24f), $"▼ {btnLabel}"))
            {
                var modNames = OmniCrafterCache.AllModNames;
                var options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("OmniCrafter_AllMods".Translate(), () =>
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
            Widgets.Label(new Rect(x, rect.y + 2f, 36f, 24f), "OmniCrafter_Sort".Translate());
            x += 38f;
            foreach (SortMode sm in Enum.GetValues(typeof(SortMode)))
            {
                if (sortMode == sm) GUI.color = Color.cyan;
                string smLabel;
                switch (sm)
                {
                    case SortMode.Value: smLabel = "OmniCrafter_SortValue".Translate(); break;
                    case SortMode.Weight: smLabel = "OmniCrafter_SortWeight".Translate(); break;
                    default: smLabel = "OmniCrafter_SortName".Translate(); break;
                }

                if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 72f, 24f), smLabel))
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
            selectedAutoOrder = null;
            validStuffs = def.MadeFromStuff ? OmniCrafterCache.GetValidStuffs(def) : null;
            selectedStuff = validStuffs != null && validStuffs.Count > 0 ? validStuffs[0] : null;
            selectedQuality = QualityCategory.Normal;
            craftCount = 1;
        }

        private void SelectDefFromOrder(AutoOrder ao)
        {
            selectedAutoOrder = ao;
            selectedDef = ao.thingDef;
            validStuffs = selectedDef != null && selectedDef.MadeFromStuff
                ? OmniCrafterCache.GetValidStuffs(selectedDef)
                : null;
            selectedStuff = ao.stuffDef ?? (validStuffs != null && validStuffs.Count > 0 ? validStuffs[0] : null);
            selectedQuality = ao.quality;
            maintainCount = ao.targetCount;
            outputMode = ao.outputMode;
            storageOnly = ao.storageOnly;
            productionMode = ProductionMode.MaintainStock;
            craftCount = 1;
        }

        private void DrawMidRightPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.12f, 0.12f, 0.12f, 0.6f));
            rect = rect.ContractedBy(6f);

            if (selectedDef == null)
            {
                Widgets.Label(rect, "OmniCrafter_SelectItem".Translate());
                return;
            }

            float w = rect.width;
            float viewW = w - 16f;

            // Estimate content height for the virtual scroll view
            float contentH = 70f + 22f; // icon + name + fav + mod source
            if (!selectedDef.description.NullOrEmpty()) contentH += 60f;
            contentH += 6f + 6f; // separators
            if (validStuffs != null && validStuffs.Count > 0) contentH += 22f + 28f + 12f;
            if (selectedDef.HasComp(typeof(CompQuality))) contentH += 22f + 28f + 12f;
            contentH += 22f + 28f + 28f + 22f + 26f + 30f + 6f;
            contentH += 22f + 70f + 22f + 36f;

            Rect viewRect = new Rect(0f, 0f, viewW, contentH);
            Widgets.BeginScrollView(rect, ref rightPanelScroll, viewRect);

            float y = 0f;

            // Icon + Name + Favorite
            Rect ir = new Rect(0f, y, 64f, 64f);
            try
            {
                Widgets.ThingIcon(ir, selectedDef, selectedStuff);
            }
            catch
            {
                Widgets.DrawBox(ir);
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(70f, y, viewW - 70f, 32f), selectedDef.LabelCap);
            Text.Font = GameFont.Small;
            bool isFav = OmniCrafterMod.Settings.globalFavorites.Contains(selectedDef.defName);
            if (Widgets.ButtonText(new Rect(70f, y + 34f, 120f, 24f),
                    isFav ? "OmniCrafter_Unfavorite".Translate() : "OmniCrafter_Favorite".Translate()))
            {
                if (isFav) OmniCrafterMod.Settings.globalFavorites.Remove(selectedDef.defName);
                else OmniCrafterMod.Settings.globalFavorites.Add(selectedDef.defName);
                OmniCrafterMod.Settings.Write();
                currentList = null;
            }

            y += 70f;

            // Mod source
            string modName = OmniCrafterCache.GetModName(selectedDef);
            GUI.color = new Color(0.7f, 0.85f, 1f);
            Widgets.Label(new Rect(0f, y, viewW, 20f), "OmniCrafter_FromMod".Translate(modName));
            GUI.color = Color.white;
            y += 22f;

            if (!selectedDef.description.NullOrEmpty())
            {
                string desc = selectedDef.description.Length > 160
                    ? selectedDef.description.Substring(0, 157) + "..."
                    : selectedDef.description;
                Widgets.Label(new Rect(0f, y, viewW, 56f), desc);
                y += 60f;
            }

            Widgets.DrawLineHorizontal(0f, y, viewW);
            y += 6f;

            // Stuff — dropdown
            if (validStuffs != null && validStuffs.Count > 0)
            {
                Widgets.Label(new Rect(0f, y, 80f, 22f), "OmniCrafter_Material".Translate());
                string stuffLabel = selectedStuff != null
                    ? (selectedStuff.label ?? selectedStuff.defName).CapitalizeFirst()
                    : "None";
                if (Widgets.ButtonText(new Rect(84f, y, viewW - 84f, 24f), $"▼ {stuffLabel}"))
                {
                    var stuffOptions = new List<FloatMenuOption>();
                    foreach (ThingDef s in validStuffs)
                    {
                        ThingDef captured = s;
                        stuffOptions.Add(new FloatMenuOption(
                            (captured.label ?? captured.defName).CapitalizeFirst(),
                            () =>
                            {
                                selectedStuff = captured;
                                SyncSelectedAutoOrder();
                            }));
                    }

                    Find.WindowStack.Add(new FloatMenu(stuffOptions));
                }

                y += 28f;
                Widgets.DrawLineHorizontal(0f, y, viewW);
                y += 6f;
            }

            // Quality — dropdown
            if (selectedDef.HasComp(typeof(CompQuality)))
            {
                Widgets.Label(new Rect(0f, y, 80f, 22f), "OmniCrafter_Quality".Translate());
                if (Widgets.ButtonText(new Rect(84f, y, viewW - 84f, 24f), $"▼ {selectedQuality.GetLabel()}"))
                {
                    var qualOptions = new List<FloatMenuOption>();
                    foreach (QualityCategory q in (QualityCategory[])Enum.GetValues(typeof(QualityCategory)))
                    {
                        QualityCategory captured = q;
                        qualOptions.Add(new FloatMenuOption(
                            captured.GetLabel(),
                            () =>
                            {
                                selectedQuality = captured;
                                SyncSelectedAutoOrder();
                            }));
                    }

                    Find.WindowStack.Add(new FloatMenu(qualOptions));
                }

                y += 28f;
                Widgets.DrawLineHorizontal(0f, y, viewW);
                y += 6f;
            }

            // Production mode
            Widgets.Label(new Rect(0f, y, viewW, 20f), "OmniCrafter_Mode".Translate());
            y += 22f;
            if (Widgets.RadioButtonLabeled(new Rect(0f, y, viewW / 2f - 2f, 24f), "OmniCrafter_FixedCount".Translate(),
                    productionMode == ProductionMode.FixedCount))
                productionMode = ProductionMode.FixedCount;
            if (Widgets.RadioButtonLabeled(new Rect(viewW / 2f, y, viewW / 2f - 2f, 24f),
                    "OmniCrafter_MaintainStock".Translate(),
                    productionMode == ProductionMode.MaintainStock))
                productionMode = ProductionMode.MaintainStock;
            y += 28f;

            if (productionMode == ProductionMode.FixedCount)
            {
                Widgets.Label(new Rect(0f, y, 60f, 24f), "OmniCrafter_Count".Translate());
                string cs = Widgets.TextField(new Rect(62f, y, 55f, 24f), craftCount.ToString());
                if (int.TryParse(cs, out int cp) && cp > 0) craftCount = cp;
                if (Widgets.ButtonText(new Rect(120f, y, 24f, 24f), "+")) craftCount++;
                if (craftCount > 1 && Widgets.ButtonText(new Rect(146f, y, 24f, 24f), "-")) craftCount--;
            }
            else
            {
                Widgets.Label(new Rect(0f, y, 100f, 24f), "OmniCrafter_TargetStock".Translate());
                string ms = Widgets.TextField(new Rect(102f, y, 55f, 24f), maintainCount.ToString());
                if (int.TryParse(ms, out int mp) && mp > 0)
                {
                    maintainCount = mp;
                    SyncSelectedAutoOrder();
                }

                if (Widgets.ButtonText(new Rect(160f, y, 24f, 24f), "+"))
                {
                    maintainCount++;
                    SyncSelectedAutoOrder();
                }

                if (maintainCount > 1 && Widgets.ButtonText(new Rect(186f, y, 24f, 24f), "-"))
                {
                    maintainCount--;
                    SyncSelectedAutoOrder();
                }
            }

            y += 28f;

            // Output mode
            Widgets.Label(new Rect(0f, y, viewW, 20f), "OmniCrafter_Output".Translate());
            y += 22f;
            if (Widgets.RadioButtonLabeled(new Rect(0f, y, viewW, 24f), "OmniCrafter_DropNear".Translate(),
                    outputMode == OutputMode.DropNear))
            {
                outputMode = OutputMode.DropNear;
                SyncSelectedAutoOrder();
            }

            y += 26f;
            if (Widgets.RadioButtonLabeled(new Rect(0f, y, viewW, 24f), "OmniCrafter_SendToStorage".Translate(),
                    outputMode == OutputMode.SendToStorage))
            {
                outputMode = OutputMode.SendToStorage;
                SyncSelectedAutoOrder();
            }

            y += 30f;

            // Storage-only toggle（仅在维持库存模式下显示）
            if (productionMode == ProductionMode.MaintainStock)
            {
                bool newStorageOnly = storageOnly;
                Widgets.CheckboxLabeled(new Rect(0f, y, viewW, 24f),
                    "OmniCrafter_StorageOnly".Translate(), ref newStorageOnly);
                if (newStorageOnly != storageOnly)
                {
                    storageOnly = newStorageOnly;
                    SyncSelectedAutoOrder();
                }

                y += 28f;
            }

            Widgets.DrawLineHorizontal(0f, y, viewW);
            y += 6f;

            // Current stock
            int currentStock = storageOnly && productionMode == ProductionMode.MaintainStock
                ? OmniCrafterCache.CountInStorage(selectedDef, building.Map)
                : OmniCrafterCache.CountOnMap(selectedDef, building.Map);
            string onMapKey = storageOnly && productionMode == ProductionMode.MaintainStock
                ? "OmniCrafter_InStorage"
                : "OmniCrafter_OnMap";
            Widgets.Label(new Rect(0f, y, viewW, 20f), onMapKey.Translate(currentStock));
            y += 22f;

            // Power cost
            PowerNet net = building.GetWorkingPowerNet();

            float totalStored = OmniPowerCost.TotalStoredEnergy(net);
            float extStored = OmniPowerCost.ExternalStoredEnergy(net);
            float intStored = OmniPowerCost.InternalStoredEnergy(net);

            string customStoredStr;
            if (float.IsInfinity(totalStored))
            {
                customStoredStr = "∞";
            }
            else
            {
                customStoredStr = $"{totalStored:N0}Wd ({extStored:N0}Wd + {intStored:N0}Wd)";
            }

            int countForCost;
            if (productionMode == ProductionMode.FixedCount)
                countForCost = craftCount;
            else
                countForCost = Mathf.Max(0, maintainCount - currentStock);
            float cost = countForCost > 0
                ? OmniPowerCost.CostWd(selectedDef, selectedStuff, selectedQuality, countForCost)
                : 0f;
            bool canAfford = countForCost <= 0 || totalStored >= cost ||
                             OmniPowerCost.SurplusEnergyWdPerTick(net) >= cost;
            // Debug: God mode free-craft bypass
            bool godDebugUI = Building_OmniCrafter.debugNoPowerRequired && DebugSettings.godMode;
            if (godDebugUI) canAfford = true;

            GUI.color = canAfford ? Color.white : Color.red;
            string costLabel = countForCost <= 0
                ? "OmniCrafter_StockFull".Translate(currentStock, maintainCount, customStoredStr)
                : "OmniCrafter_PowerCostLabel".Translate(cost.ToString("N0"), customStoredStr);
            // 当前供电量
            string powerSupportNow = (OmniPowerCost.SurplusEnergyWdPerTick(net) * 60000f).ToString("N0");
            costLabel += "OmniCrafter_PowerCostLabelSupportNow".Translate(powerSupportNow);
            
            if (!canAfford)
            {
                costLabel += "OmniCrafter_InsufficientPowerWarning".Translate();
            }

            Widgets.Label(new Rect(0f, y, viewW, 66f), costLabel);
            GUI.color = Color.white;
            y += 70f;

            // Building 提示
            if (selectedDef.category == ThingCategory.Building && selectedDef.Minifiable && countForCost > 1)
            {
                GUI.color = new Color(1f, 0.85f, 0.2f);
                Widgets.Label(new Rect(0f, y, viewW, 20f), "OmniCrafter_BuildingOneAtATime".Translate());
                GUI.color = Color.white;
                y += 22f;
            }

            // Action button
            if (productionMode == ProductionMode.FixedCount)
            {
                GUI.color = canAfford ? Color.white : new Color(0.5f, 0.5f, 0.5f);
                if (Widgets.ButtonText(new Rect(0f, y, viewW, 32f), "OmniCrafter_CraftX".Translate(craftCount)))
                {
                    if (canAfford)
                    {
                        if (!godDebugUI)
                            OmniPowerCost.TryDrainPower(net, cost);
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
                    if (Widgets.ButtonText(new Rect(0f, y, viewW, 32f), "OmniCrafter_RemoveAutoOrder".Translate()))
                    {
                        building.autoOrders.RemoveAll(o => o.thingDef == selectedDef);
                        if (selectedAutoOrder != null && selectedAutoOrder.thingDef == selectedDef)
                            selectedAutoOrder = null;
                    }
                }
                else
                {
                    if (Widgets.ButtonText(new Rect(0f, y, viewW, 32f), "OmniCrafter_AddAutoOrder".Translate()))
                    {
                        building.autoOrders.Add(new AutoOrder
                        {
                            thingDef = selectedDef, stuffDef = selectedStuff,
                            quality = selectedQuality, targetCount = maintainCount,
                            outputMode = outputMode, storageOnly = storageOnly
                        });
                        building.AddRecent(selectedDef);
                    }
                }

                y += 36f;
            }

            Widgets.EndScrollView();
        }

        private void DrawFarRightPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.14f, 0.14f, 0.14f, 0.6f));
            rect = rect.ContractedBy(6f);

            if (building.autoOrders.Count == 0)
            {
                Widgets.Label(rect, "OmniCrafter_NoAutoOrders".Translate());
                return;
            }

            float w = rect.width;
            float viewW = w - 16f;

            // Estimate content height for the virtual scroll view
            float contentH = building.autoOrders.Count * 36f;

            Rect viewRect = new Rect(0f, 0f, viewW, contentH);
            Widgets.BeginScrollView(rect, ref farRightScroll, viewRect);

            float y = 0f;

            float rowH = 36f;

            for (int i = 0; i < building.autoOrders.Count; i++)
            {
                AutoOrder ao = building.autoOrders[i];
                int onMap = ao.storageOnly
                    ? OmniCrafterCache.CountInStorage(ao.thingDef, building.Map)
                    : OmniCrafterCache.CountOnMap(ao.thingDef, building.Map);
                bool full = onMap >= ao.targetCount;

                Rect rowRect = new Rect(0f, y, viewW, rowH);

                // Highlight selected
                if (selectedAutoOrder == ao)
                    Widgets.DrawHighlight(rowRect);
                else if (i % 2 == 0)
                    Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.03f));
                if (Mouse.IsOver(rowRect) && selectedAutoOrder != ao)
                    Widgets.DrawHighlightIfMouseover(rowRect);

                GUI.color = ao.paused ? Color.gray : (full ? Color.green : Color.white);
                string storageFlag = ao.storageOnly ? " 🏪" : "";
                string lbl = (ao.thingDef?.LabelCap ?? "?") +
                             $" {onMap}/{ao.targetCount} [{ao.quality.GetLabel()}]{storageFlag}";
                Widgets.Label(new Rect(0f, y, viewW - 50f, rowH), lbl);
                GUI.color = Color.white;

                // Pause button
                string pauseLbl = ao.paused ? "▶" : "P";
                if (Widgets.ButtonText(new Rect(viewW - 48f, y + 2f, 22f, 20f), pauseLbl))
                {
                    ao.paused = !ao.paused;
                }

                // Delete button
                if (Widgets.ButtonText(new Rect(viewW - 24f, y + 2f, 22f, 20f), "X"))
                {
                    if (selectedAutoOrder == ao) selectedAutoOrder = null;
                    building.autoOrders.RemoveAt(i);
                    break;
                }

                // Click row → populate mid-right panel
                Rect clickRect = new Rect(0f, y, viewW - 50f, rowH);
                if (Widgets.ButtonInvisible(clickRect))
                    SelectDefFromOrder(ao);

                y += rowH;
            }

            Widgets.EndScrollView();
        }
    }
}