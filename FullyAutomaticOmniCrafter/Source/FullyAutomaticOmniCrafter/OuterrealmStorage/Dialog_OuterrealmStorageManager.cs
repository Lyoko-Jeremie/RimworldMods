using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 全局存储管理器（§6.4，必须实现）：无视任何建筑 filter 查看超维空间全部内容，
    /// 是"全禁用死锁"（§6.2）的唯一逃生口。功能：内容列表（图标/名称/long 数量/可见建筑数）、
    /// 搜索（QuickSearchWidget）、"仅显示不可见条目"快捷筛选（死锁定位）、按地图强制弹出（限速队列）、
    /// 左侧原版风格树状分类（参考万能制造机 OmniCrafterUi 的 Listing_TreeCategorySelect），
    /// 每项右侧显示该分类（含子分类）的条目总数（随全局内容版本号自动刷新）。
    /// </summary>
    public class Dialog_OuterrealmStorageManager : Window
    {
        private readonly Vector2 initialSize = new Vector2(1500f, 700f);
        public override Vector2 InitialSize => initialSize;

        private Vector2 scrollPosition;
        private Vector2 categoryScroll;
        private readonly QuickSearchWidget searchWidget = new QuickSearchWidget();
        private bool showOnlyUnseen;
        /// <summary>是否显示条目的终端可见性或唯一物品所在仓；默认关闭以精简列表。</summary>
        private bool showEntryDetails;
        private bool dirtyUnseenFlag;
        private bool batchBindingMode;
        private bool batchOnlyUnbound = true;
        private bool dirty = true;
        private int selectedMapIndex;
        /// <summary>随身弹出目标（§v3）：null = 按地图默认锚点弹出。</summary>
        private readonly Pawn ejectTarget;
        private readonly List<OuterrealmEntry> visibleEntries = new List<OuterrealmEntry>();
        /// <summary>批量模式的显式选择与已见集合；新进入当前筛选结果的条目默认选中。</summary>
        private readonly HashSet<OuterrealmEntry> batchSelected = new HashSet<OuterrealmEntry>();
        private readonly HashSet<OuterrealmEntry> batchSeen = new HashSet<OuterrealmEntry>();

        /// <summary>分类树缓存：有效分类集合 + 各分类（含子分类）条目总数。仅内容版本变化时重建（复用字段避免每帧分配）。</summary>
        private readonly HashSet<ThingCategoryDef> validCategories = new HashSet<ThingCategoryDef>();
        private readonly Dictionary<ThingCategoryDef, long> categoryCounts = new Dictionary<ThingCategoryDef, long>();
        /// <summary>分类树三态聚合缓存（原版 AllowanceStateOf 语义）：按 ThingDef 的直接允许状态
        /// 累加每个分类（含祖先）的允许数/总数，聚合为 绿✓=全部允许 / 红×=全部禁止 /
        /// 黄~=部分允许。dirty 或内容变化时重建。</summary>
        private readonly Dictionary<ThingCategoryDef, MultiCheckboxState> categoryStates = new Dictionary<ThingCategoryDef, MultiCheckboxState>();
        private readonly Dictionary<ThingCategoryDef, int> catShownCounts = new Dictionary<ThingCategoryDef, int>();
        private readonly Dictionary<ThingCategoryDef, int> catTotalCounts = new Dictionary<ThingCategoryDef, int>();
        /// <summary>上次已同步的全局内容版本号（不等比较抗回绕，与 GameComponent 注释一致）。</summary>
        private int lastSeenVersion = int.MinValue;

        /// <summary>当前分类筛选（null = 全部分类）。</summary>
        private ThingCategoryDef selectedCategory;

        private const float CategoryWidth = 400f;
        private const float DetailedRowHeight = 46f;
        private const float CompactRowHeight = 36f;
        private const float CategoryLineHeight = 24f;

        private float CurrentRowHeight => showEntryDetails ? DetailedRowHeight : CompactRowHeight;

        public Dialog_OuterrealmStorageManager()
        {
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            selectedMapIndex = Find.CurrentMap != null ? Find.Maps.IndexOf(Find.CurrentMap) : 0;
            if (selectedMapIndex < 0)
            {
                selectedMapIndex = 0;
            }
        }

        /// <summary>随身弹出（§v3）：授权 pawn 打开时，弹出锚点 = pawn 位置；否则按地图默认锚点。</summary>
        public Dialog_OuterrealmStorageManager(Pawn ejectTarget) : this()
        {
            this.ejectTarget = ejectTarget;
        }

        public override void DoWindowContents(Rect inRect)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                Widgets.Label(inRect, "OuterrealmStorageManager_NoEntries".Translate());
                return;
            }
            if (dirty || gs.Version != lastSeenVersion)
            {
                if (gs.Version != lastSeenVersion)
                {
                    // 全局内容变化（存入/取出/弹出）：分类集合与数量一并重建，列表也随弹出实时刷新
                    lastSeenVersion = gs.Version;
                    RebuildCategoryCache(gs);
                }
                RebuildCategoryStates(gs);
                RebuildVisible(gs);
                dirty = false;
            }

            Text.Font = GameFont.Small;
            float y = inRect.y;

            // 标题 + 统计
            gs.GetSummary(out int entryCount, out long totalCount);
            long ejecting = 0;
            for (int i = 0; i < gs.EjectQueueForReading.Count; i++)
            {
                ejecting += gs.EjectQueueForReading[i].Remaining;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "OuterrealmStorageManagerTitle".Translate());
            y += 30f;
            Text.Font = GameFont.Small;
            string stat = "OuterrealmStorageManager_Total".Translate(entryCount, totalCount.ToString("N0"));
            if (ejecting > 0)
            {
                stat += "   " + "OuterrealmStorageManager_EjectProgress".Translate(ejecting.ToString("N0"));
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 250f, 24f), stat);
            Rect primaryActionRect = new Rect(inRect.x + inRect.width - 250f, y, 250f, 26f);
            if (batchBindingMode)
            {
                int selectedVisibleCount = CountSelectedVisible();
                if (Widgets.ButtonText(primaryActionRect,
                    "OuterrealmStorageManager_BatchBindSelected".Translate(selectedVisibleCount), true, false, true))
                {
                    if (selectedVisibleCount <= 0)
                    {
                        Messages.Message("OuterrealmStorageManager_BatchBindNoSelection".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                    }
                    else
                    {
                        OpenBatchBindMenu(gs);
                    }
                }
            }
            // 普通模式下保留原有“全部取出”逃生口。
            else if (Widgets.ButtonText(primaryActionRect, "OuterrealmStorageManager_EjectAllEntries".Translate(), true, false, true))
            {
                List<OuterrealmEntry> all = gs.EntriesForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    OuterrealmEntry e = all[i];
                    if (e.Count > 0)
                    {
                        gs.EnqueueEject(e, TargetMap(), e.Count, TargetAnchor());
                    }
                }
            }
            y += 30f;

            // 搜索 + 批量绑定模式/二级筛选 + 地图选择
            searchWidget.OnGUI(new Rect(inRect.x, y, 260f, 28f), () => dirty = true, () => dirty = true);
            Rect batchModeRect = new Rect(inRect.x + 270f, y, 210f, 28f);
            bool previousBatchMode = batchBindingMode;
            Widgets.CheckboxLabeled(batchModeRect, "OuterrealmStorageManager_BatchBindingMode".Translate(), ref batchBindingMode);
            TooltipHandler.TipRegion(batchModeRect, "OuterrealmStorageManager_BatchBindingModeDesc".Translate());
            if (batchBindingMode != previousBatchMode)
            {
                batchSelected.Clear();
                batchSeen.Clear();
                dirty = true;
            }
            Rect secondaryFilterRect = new Rect(inRect.x + 490f, y, 300f, 28f);
            if (batchBindingMode)
            {
                bool previousOnlyUnbound = batchOnlyUnbound;
                Widgets.CheckboxLabeled(secondaryFilterRect,
                    "OuterrealmStorageManager_BatchOnlyUnbound".Translate(), ref batchOnlyUnbound);
                if (batchOnlyUnbound != previousOnlyUnbound)
                {
                    batchSelected.Clear();
                    batchSeen.Clear();
                    dirty = true;
                }
            }
            else
            {
                Widgets.CheckboxLabeled(secondaryFilterRect,
                    "OuterrealmStorageManager_ShowOnlyUnseen".Translate(), ref showOnlyUnseen);
                if (showOnlyUnseen != dirtyUnseenFlag)
                {
                    dirtyUnseenFlag = showOnlyUnseen;
                    dirty = true;
                }
            }
            Rect detailToggleRect = new Rect(inRect.x + 800f, y, 300f, 28f);
            Widgets.CheckboxLabeled(detailToggleRect,
                "OuterrealmStorageManager_ShowEntryDetails".Translate(), ref showEntryDetails,
                placeCheckboxNearText: true);
            TooltipHandler.TipRegion(detailToggleRect,
                "OuterrealmStorageManager_ShowEntryDetailsDesc".Translate());
            if (Find.Maps.Count > 1)
            {
                string mapLabel = selectedMapIndex >= 0 && selectedMapIndex < Find.Maps.Count
                    ? Find.Maps[selectedMapIndex].info.parent.Label
                    : "-";
                Rect mapRect = new Rect(inRect.x + inRect.width - 240f, y, 240f, 28f);
                if (Widgets.ButtonText(mapRect, "OuterrealmStorageManager_SelectMap".Translate() + ": " + mapLabel, true, false, true))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    for (int i = 0; i < Find.Maps.Count; i++)
                    {
                        Map m = Find.Maps[i];
                        int index = i;
                        options.Add(new FloatMenuOption(m.info.parent.Label, () => selectedMapIndex = index));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            y += 34f;

            // 当前存档的全局贸易规则：开启后超维库存无需信标或任何终端即可被轨道商人枚举。
            bool exposeAllToTrade = gs.ExposeAllToOrbitalTrade;
            Rect tradeToggleRect = new Rect(inRect.x, y, inRect.width, 28f);
            Widgets.CheckboxLabeled(
                tradeToggleRect,
                "OuterrealmStorageManager_ExposeAllToTrade".Translate(),
                ref exposeAllToTrade,
                placeCheckboxNearText: true);
            TooltipHandler.TipRegion(
                tradeToggleRect,
                "OuterrealmStorageManager_ExposeAllToTradeDesc".Translate());
            if (exposeAllToTrade != gs.ExposeAllToOrbitalTrade)
            {
                gs.ExposeAllToOrbitalTrade = exposeAllToTrade;
            }
            y += 30f;

            // 主体：左侧原版树状分类 + 右侧条目列表（参考万能制造机界面布局）
            float bodyH = inRect.height - y - 40f;
            if (bodyH <= 0f)
            {
                return;
            }
            Rect bodyRect = new Rect(inRect.x, y, inRect.width, bodyH);
            DrawCategoryPanel(new Rect(bodyRect.x, bodyRect.y, CategoryWidth, bodyRect.height));
            DrawEntryList(gs, new Rect(bodyRect.x + CategoryWidth + 4f, bodyRect.y, bodyRect.width - CategoryWidth - 4f, bodyRect.height));
        }

        /// <summary>左侧分类面板：顶部"全部分类"导航项 + 原版风格树状分类（与万能制造机一致）。</summary>
        private void DrawCategoryPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            rect = rect.ContractedBy(3f);

            HashSet<ThingCategoryDef> validCats = validCategories;
            float treeH = ComputeTreeHeight(ThingCategoryDefOf.Root.treeNode, validCats, CategoryLineHeight);
            float totalH = CategoryLineHeight + 2f + 6f + treeH;

            Rect view = new Rect(0f, 0f, rect.width - 16f, totalH);
            Widgets.BeginScrollView(rect, ref categoryScroll, view);
            float y = 0f;

            // 全部分类
            DrawNavItem(new Rect(0f, y, view.width, CategoryLineHeight), "OuterrealmStorageManager_AllCategories".Translate(),
                selectedCategory == null, () =>
                {
                    selectedCategory = null;
                    dirty = true;
                });
            y += CategoryLineHeight + 2f + 6f;

            // 原版树状分类菜单（复用万能制造机的 Listing_TreeCategorySelect）
            float treeAreaH = Mathf.Max(treeH, 1f);
            Rect treeRect = new Rect(0f, y, view.width, treeAreaH);
            // 可视区域（相对于树的局部坐标）
            Rect visibleRect = new Rect(0f, categoryScroll.y - y, view.width, rect.height);
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            Listing_TreeCategorySelect listing = new Listing_TreeCategorySelect(
                validCats,
                selectedCategory,
                cat =>
                {
                    selectedCategory = cat;
                    dirty = true;
                },
                categoryCounts,
                cat =>
                {
                    MultiCheckboxState st;
                    return categoryStates.TryGetValue(cat, out st) ? st : MultiCheckboxState.On;
                },
                (cat, show) =>
                {
                    gs.SetManagerCatShow(cat, show);
                    dirty = true;
                });
            listing.SetVisibleRect(visibleRect);
            listing.Begin(treeRect);
            foreach (TreeNode_ThingCategory child in ThingCategoryDefOf.Root.treeNode.ChildCategoryNodes)
            {
                listing.DoCategoryNode(child, 0, 1);
            }
            listing.End();

            Widgets.EndScrollView();
        }

        /// <summary>导航项：选中高亮 + hover 高亮 + 整行点击（参考万能制造机 DrawNavItem）。</summary>
        private static void DrawNavItem(Rect r, string label, bool selected, Action onClick)
        {
            if (selected)
            {
                Widgets.DrawHighlight(r);
            }
            else if (Mouse.IsOver(r))
            {
                Widgets.DrawHighlightIfMouseover(r);
            }
            Widgets.Label(r.ContractedBy(2f, 0f), label);
            if (Widgets.ButtonInvisible(r))
            {
                onClick();
            }
        }

        /// <summary>重建分类树缓存：收集当前有内容条目的分类及其全部祖先（树只显示这些分类），
        /// 并顺带把每条目的 Count 累加到其所属分类链每一层（父分类数量天然含子分类），
        /// 供分类树每项右侧显示数量。复用字段 Clear 后重填，避免每帧分配容器。</summary>
        private void RebuildCategoryCache(GameComponent_OuterrealmStorage gs)
        {
            validCategories.Clear();
            categoryCounts.Clear();
            List<OuterrealmEntry> all = gs.EntriesForReading;
            for (int i = 0; i < all.Count; i++)
            {
                OuterrealmEntry e = all[i];
                if (e == null || e.Count <= 0 || e.Key.Def == null)
                {
                    continue;
                }
                ThingDef def = e.Key.Def;
                if (def.thingCategories == null)
                {
                    continue;
                }
                for (int ci = 0; ci < def.thingCategories.Count; ci++)
                {
                    ThingCategoryDef c = def.thingCategories[ci];
                    while (c != null)
                    {
                        validCategories.Add(c);
                        long prev;
                        categoryCounts.TryGetValue(c, out prev);
                        categoryCounts[c] = prev + e.Count;
                        c = c.parent;
                    }
                }
            }
        }

        /// <summary>重建分类树三态聚合缓存（原版 Listing_TreeThingFilter.AllowanceStateOf 语义）：
        /// 遍历当前存储中的 ThingDef，直接检查其允许状态，并沿分类链累加到每个分类（含祖先）。
        /// 父分类计数天然含子分类物品。聚合：全允许→绿✓、全禁止→红×、混合→黄~。
        /// 复用字段 Clear 后重填，避免每帧分配；dirty（点击筛选）与内容变化时重建。</summary>
        private void RebuildCategoryStates(GameComponent_OuterrealmStorage gs)
        {
            categoryStates.Clear();
            catShownCounts.Clear();
            catTotalCounts.Clear();
            List<OuterrealmEntry> all = gs.EntriesForReading;
            for (int i = 0; i < all.Count; i++)
            {
                OuterrealmEntry e = all[i];
                if (e == null || e.Count <= 0 || e.Key.Def == null || e.Key.Def.thingCategories == null)
                {
                    continue;
                }
                // 原版按 ThingDef 的允许集合统计，不按分类链 OR 后的“最终可见性”统计。
                bool allowed = gs.ManagerAllows(e.Key.Def);
                for (int ci = 0; ci < e.Key.Def.thingCategories.Count; ci++)
                {
                    ThingCategoryDef c = e.Key.Def.thingCategories[ci];
                    while (c != null)
                    {
                        int total;
                        catTotalCounts.TryGetValue(c, out total);
                        catTotalCounts[c] = total + 1;
                        if (allowed)
                        {
                            int shown;
                            catShownCounts.TryGetValue(c, out shown);
                            catShownCounts[c] = shown + 1;
                        }
                        c = c.parent;
                    }
                }
            }
            foreach (KeyValuePair<ThingCategoryDef, int> kv in catTotalCounts)
            {
                int shown;
                catShownCounts.TryGetValue(kv.Key, out shown);
                categoryStates[kv.Key] = shown <= 0
                    ? MultiCheckboxState.Off
                    : (shown >= kv.Value ? MultiCheckboxState.On : MultiCheckboxState.Partial);
            }
        }

        /// <summary>递归计算分类树的总虚拟高度（用于滚动视图，参考万能制造机）。</summary>
        private static float ComputeTreeHeight(TreeNode_ThingCategory node, HashSet<ThingCategoryDef> validCats, float lh)
        {
            float h = 0f;
            foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
            {
                if (!validCats.Contains(child.catDef))
                {
                    continue;
                }
                h += lh + 2f;
                if (child.IsOpen(1))
                {
                    h += ComputeTreeHeight(child, validCats, lh);
                }
            }
            return h;
        }

        /// <summary>右侧条目列表（滚动视图）。</summary>
        private void DrawEntryList(GameComponent_OuterrealmStorage gs, Rect outRect)
        {
            float rowHeight = CurrentRowHeight;
            float contentHeight = visibleEntries.Count * rowHeight;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(contentHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            // 虚拟化：列表仍保留轻量索引，但仅构造/绘制当前视口及上下各一行。
            int first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / rowHeight) - 1);
            int last = Mathf.Min(visibleEntries.Count, Mathf.CeilToInt((scrollPosition.y + outRect.height) / rowHeight) + 1);
            float curY = first * rowHeight;
            for (int i = first; i < last; i++)
            {
                DoEntryRow(gs, visibleEntries[i], viewRect.width, rowHeight, ref curY, i % 2 == 0);
            }
            if (visibleEntries.Count == 0)
            {
                Widgets.NoneLabel(ref curY, viewRect.width, "OuterrealmStorageManager_NoEntries".Translate());
            }
            Widgets.EndScrollView();
        }

        /// <summary>按搜索/筛选/分类重建可见条目列表（行数 = L1 组合级，几十~几百）。</summary>
        private void RebuildVisible(GameComponent_OuterrealmStorage gs)
        {
            visibleEntries.Clear();
            List<OuterrealmEntry> all = gs.EntriesForReading;
            for (int i = 0; i < all.Count; i++)
            {
                OuterrealmEntry e = all[i];
                if (e.Count <= 0)
                {
                    continue;
                }
                if (batchBindingMode)
                {
                    // 批量绑定只处理不能安全合并的唯一对象；普通堆叠条目没有逐对象默认仓语义。
                    if (!OuterrealmIdentityRouting.IsUnique(e)
                        || (batchOnlyUnbound && !OuterrealmIdentityRouting.NeedsHomeBinding(e)))
                    {
                        continue;
                    }
                }
                else if (showOnlyUnseen && CountVisibleBuildings(gs, e) > 0)
                {
                    continue;
                }
                // 分类筛选：条目所属分类链（含子分类）上存在选中分类才显示
                if (selectedCategory != null && !IsInCategory(e, selectedCategory))
                {
                    continue;
                }
                // 视图筛选直接采用原版 ThingFilter.Allows(ThingDef) 语义。
                if (!CategoryFilterAllows(gs, e))
                {
                    continue;
                }
                bool matches = searchWidget.filter.Matches(e.Proto.def.label)
                    || searchWidget.filter.Matches(e.Proto.def.defName);
                if (matches)
                {
                    visibleEntries.Add(e);
                    if (batchBindingMode && batchSeen.Add(e))
                    {
                        batchSelected.Add(e);
                    }
                }
            }
        }

        /// <summary>条目 def 的任一所属分类链（含子分类语义）上是否包含目标分类。</summary>
        private static bool IsInCategory(OuterrealmEntry e, ThingCategoryDef cat)
        {
            ThingDef def = e.Key.Def;
            if (def == null || def.thingCategories == null)
            {
                return false;
            }
            for (int ci = 0; ci < def.thingCategories.Count; ci++)
            {
                ThingCategoryDef c = def.thingCategories[ci];
                while (c != null)
                {
                    if (c == cat)
                    {
                        return true;
                    }
                    c = c.parent;
                }
            }
            return false;
        }

        /// <summary>视图筛选：与原版 ThingFilter.Allows(ThingDef) 一致，物品是否显示只取决于
        /// 该 ThingDef 的直接允许状态；多分类物品不会因另一条分类链而抵消本次切换。</summary>
        private static bool CategoryFilterAllows(GameComponent_OuterrealmStorage gs, OuterrealmEntry e)
        {
            ThingDef def = e.Key.Def;
            return def == null || gs.ManagerAllows(def);
        }

        /// <summary>该条目当前被几座终端可见（帮助定位死锁条目，§6.4）。
        /// 可见性直接以 filter 判定（§filter 视图过滤简化）：filter 是视图过滤语义，
        /// 副本物化仅影响取用/查询投影，不参与可见性判定——副本可能因"刚允许尚未帧末物化"
        /// 而缺失，但条目仍应计为可见；用 CanShow（含 frozen）与"副本存在 ⟺ 可见"等价且更稳。</summary>
        private static int CountVisibleBuildings(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry)
        {
            int count = 0;
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v == null || v.view == null)
                {
                    continue;
                }
                bool visible = v.CanShow(entry.Proto);
                if (visible)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>单行渲染：隔行条纹 + 鼠标划过整行高亮 + 图标 + 名称 + long 数量 + 可见建筑数/不可见标记 + 弹出/全部弹出按钮。</summary>
        private void DoEntryRow(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry, float width, float rowHeight, ref float curY, bool evenRow)
        {
            Rect rect = new Rect(0f, curY, width, rowHeight);
            // 隔行条纹 + 鼠标划过整行高亮背景（参考万能制造机内容列表风格）
            if (evenRow)
            {
                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.03f));
            }
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlightIfMouseover(rect);
            }
            int visibleBuildings = showEntryDetails ? CountVisibleBuildings(gs, entry) : 0;
            float buttonY = curY + (rowHeight - 24f) / 2f;
            float iconY = curY + (rowHeight - 28f) / 2f;

            bool unique = OuterrealmIdentityRouting.IsUnique(entry);
            float contentLeft = 4f;
            if (batchBindingMode)
            {
                bool selected = batchSelected.Contains(entry);
                Widgets.Checkbox(new Vector2(4f, curY + (rowHeight - 24f) / 2f), ref selected, 24f);
                if (selected)
                {
                    batchSelected.Add(entry);
                }
                else
                {
                    batchSelected.Remove(entry);
                }
                contentLeft = 34f;
            }
            else
            {
                if (Widgets.ButtonText(new Rect(rect.x + rect.width - 90f, buttonY, 86f, 24f), "OuterrealmStorageManager_EjectAll".Translate(), true, false, true))
                {
                    gs.EnqueueEject(entry, TargetMap(), entry.Count, TargetAnchor());
                }
                if (Widgets.ButtonText(new Rect(rect.x + rect.width - 180f, buttonY, 86f, 24f), "OuterrealmStorageManager_Eject".Translate(), true, false, true))
                {
                    int max = (int)Mathf.Min(entry.Count, int.MaxValue);
                    if (max > 0)
                    {
                        string label = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto);
                        Find.WindowStack.Add(new Dialog_Slider(
                            (int v) => label + " x" + v.ToString("N0"),
                            1,
                            max,
                            (int v) => gs.EnqueueEject(entry, TargetMap(), v, TargetAnchor())));
                    }
                }
                if (unique && Widgets.ButtonText(
                    new Rect(rect.x + rect.width - 270f, buttonY, 86f, 24f),
                    "OuterrealmStorageManager_MoveHome".Translate(), true, false, true))
                {
                    OpenMoveHomeMenu(gs, entry);
                }
                rect.width -= unique ? 280f : 190f;
            }

            if (entry.Proto is Corpse protoCorpse && protoCorpse.Bugged)
            {
                Widgets.InfoCardButton(rect.width - 24f, buttonY, entry.Proto.def);
            }
            else
            {
                Widgets.InfoCardButton(rect.width - 24f, buttonY, entry.Proto);
            }
            rect.width -= 24f;
            OuterrealmVaultUtil.ThingIconSafe(new Rect(contentLeft, iconY, 28f, 28f), entry.Proto);

            string text = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto) + " x" + entry.Count.ToString("N0");
            Text.Anchor = TextAnchor.MiddleLeft;
            float textLeft = contentLeft + 32f;
            float nameWidth = rect.width - textLeft;
            if (nameWidth < 20f)
            {
                nameWidth = 20f;
            }
            if (showEntryDetails)
            {
                string flagText = visibleBuildings == 0
                    ? "OuterrealmStorageManager_Unseen".Translate()
                    : "OuterrealmStorageManager_VisibleBuildings".Translate(visibleBuildings);
                string locationText = unique ? IdentityLocationText(entry) : flagText;
                // 开启详情后，第二行显示唯一物品默认/当前仓，普通条目显示可见终端统计。
                Widgets.Label(new Rect(textLeft, curY, nameWidth, 23f), text.StripTags().Truncate(nameWidth));
                GUI.color = unique ? new Color(0.72f, 0.85f, 1f) : Color.gray;
                Widgets.Label(new Rect(textLeft, curY + 21f, nameWidth, 22f), locationText.StripTags().Truncate(nameWidth));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(rect, text + "\n" + locationText + "\n" + flagText);
            }
            else
            {
                Widgets.Label(new Rect(textLeft, curY, nameWidth, rowHeight), text.StripTags().Truncate(nameWidth));
                TooltipHandler.TipRegion(rect, text);
            }
            Text.Anchor = TextAnchor.UpperLeft;
            curY += rowHeight;
        }

        /// <summary>唯一物品位置：当前临时出口优先；未建立锚点时仍显示持久默认仓。</summary>
        private static string IdentityLocationText(OuterrealmEntry entry)
        {
            Building_OuterrealmVault current = OuterrealmIdentityRouting.CurrentVault(entry);
            Building_OuterrealmVault home = entry.HomeVault;
            if (current != null && current != home)
            {
                return "OuterrealmStorageManager_CurrentTemporary".Translate(
                    OuterrealmIdentityRouting.VaultDisplayName(current),
                    OuterrealmIdentityRouting.VaultDisplayName(home));
            }
            if (current != null)
            {
                return "OuterrealmStorageManager_CurrentHome".Translate(
                    OuterrealmIdentityRouting.VaultDisplayName(current));
            }
            if (home != null)
            {
                return "OuterrealmStorageManager_HomeUnavailable".Translate(
                    OuterrealmIdentityRouting.VaultDisplayName(home));
            }
            return "OuterrealmStorageManager_NoHomeVault".Translate();
        }

        /// <summary>列出全部地图上的终端；选择后迁移持久默认仓并立即把空闲锚点归位。</summary>
        private void OpenMoveHomeMenu(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (vault == null || !vault.Spawned || vault.Destroyed)
                {
                    continue;
                }
                string label = OuterrealmIdentityRouting.VaultDisplayName(vault);
                if (vault == entry.HomeVault)
                {
                    label = "✓ " + label;
                }
                if (!vault.CanShow(entry.Proto))
                {
                    options.Add(new FloatMenuOption(
                        label + " (" + "OuterrealmStorageManager_VaultRejectsItem".Translate() + ")", null));
                    continue;
                }
                Building_OuterrealmVault selected = vault;
                options.Add(new FloatMenuOption(label, () =>
                {
                    string reason;
                    if (!OuterrealmIdentityRouting.TrySetHomeVault(entry, selected, out reason))
                    {
                        Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    }
                    else
                    {
                        Messages.Message(
                            "OuterrealmStorageManager_MoveHomeSucceeded".Translate(
                                OuterrealmIdentityRouting.VaultDisplayName(selected)),
                            MessageTypeDefOf.TaskCompletion, false);
                        dirty = true;
                    }
                }));
            }
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("OuterrealmStorageManager_NoMigrationTargets".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>当前筛选结果中被勾选的唯一条目数；批量操作严格只作用于用户眼前的结果。</summary>
        private int CountSelectedVisible()
        {
            int count = 0;
            for (int i = 0; i < visibleEntries.Count; i++)
            {
                if (batchSelected.Contains(visibleEntries[i]))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>跨地图列出全部可用存储仓。具体物品筛选在执行时逐项校验，允许部分成功。</summary>
        private void OpenBatchBindMenu(GameComponent_OuterrealmStorage gs)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (vault == null || !vault.Spawned || vault.Destroyed)
                {
                    continue;
                }
                Building_OuterrealmVault selected = vault;
                options.Add(new FloatMenuOption(
                    OuterrealmIdentityRouting.VaultDisplayName(vault),
                    () => BatchBindVisibleToVault(selected)));
            }
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("OuterrealmStorageManager_NoMigrationTargets".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void BatchBindVisibleToVault(Building_OuterrealmVault vault)
        {
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < visibleEntries.Count; i++)
            {
                OuterrealmEntry entry = visibleEntries[i];
                if (!batchSelected.Contains(entry))
                {
                    continue;
                }
                string reason;
                if (OuterrealmIdentityRouting.TrySetHomeVault(entry, vault, out reason))
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }
            Messages.Message(
                "OuterrealmStorageManager_BatchBindResult".Translate(
                    succeeded, OuterrealmIdentityRouting.VaultDisplayName(vault), failed),
                succeeded > 0 ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput,
                false);
            dirty = true;
        }

        private Map TargetMap()
        {
            if (ejectTarget != null && ejectTarget.Map != null)
            {
                return ejectTarget.Map;
            }
            if (selectedMapIndex >= 0 && selectedMapIndex < Find.Maps.Count)
            {
                return Find.Maps[selectedMapIndex];
            }
            return Find.CurrentMap;
        }

        /// <summary>弹出锚点：随身弹出 = pawn 位置；否则 Invalid（走 FindEjectAnchor 默认）。</summary>
        private IntVec3 TargetAnchor()
        {
            return ejectTarget != null ? ejectTarget.Position : IntVec3.Invalid;
        }
    }
}
