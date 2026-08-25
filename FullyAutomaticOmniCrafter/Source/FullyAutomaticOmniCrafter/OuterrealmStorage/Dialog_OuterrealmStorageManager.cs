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
        private readonly Vector2 initialSize = new Vector2(1000f, 700f);
        public override Vector2 InitialSize => initialSize;

        private Vector2 scrollPosition;
        private Vector2 categoryScroll;
        private readonly QuickSearchWidget searchWidget = new QuickSearchWidget();
        private bool showOnlyUnseen;
        private bool dirtyUnseenFlag;
        private bool dirty = true;
        private int selectedMapIndex;
        /// <summary>随身弹出目标（§v3）：null = 按地图默认锚点弹出。</summary>
        private readonly Pawn ejectTarget;
        private readonly List<OuterrealmEntry> visibleEntries = new List<OuterrealmEntry>();

        /// <summary>分类树缓存：有效分类集合 + 各分类（含子分类）条目总数。仅内容版本变化时重建（复用字段避免每帧分配）。</summary>
        private readonly HashSet<ThingCategoryDef> validCategories = new HashSet<ThingCategoryDef>();
        private readonly Dictionary<ThingCategoryDef, long> categoryCounts = new Dictionary<ThingCategoryDef, long>();
        /// <summary>分类树三态聚合缓存（原版 AllowanceStateOf 语义）：按条目遍历累加每个分类（含祖先）的
        /// 显示/总数，聚合为 绿✓=全部显示 / 红×=全部隐藏 / 黄~=部分显示部分不显示。dirty 或内容变化时重建。</summary>
        private readonly Dictionary<ThingCategoryDef, MultiCheckboxState> categoryStates = new Dictionary<ThingCategoryDef, MultiCheckboxState>();
        private readonly Dictionary<ThingCategoryDef, int> catShownCounts = new Dictionary<ThingCategoryDef, int>();
        private readonly Dictionary<ThingCategoryDef, int> catTotalCounts = new Dictionary<ThingCategoryDef, int>();
        /// <summary>上次已同步的全局内容版本号（不等比较抗回绕，与 GameComponent 注释一致）。</summary>
        private int lastSeenVersion = int.MinValue;

        /// <summary>当前分类筛选（null = 全部分类）。</summary>
        private ThingCategoryDef selectedCategory;

        private const float CategoryWidth = 200f;
        private const float RowHeight = 28f;
        private const float CategoryLineHeight = 24f;

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
            // 全部取出所有物品（追加到弹出队列，逐 tick 限速执行）
            if (Widgets.ButtonText(new Rect(inRect.x + inRect.width - 250f, y, 250f, 26f), "OuterrealmStorageManager_EjectAllEntries".Translate(), true, false, true))
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

            // 搜索 + 不可见筛选 + 地图选择
            searchWidget.OnGUI(new Rect(inRect.x, y, 260f, 28f), () => dirty = true, () => dirty = true);
            Widgets.CheckboxLabeled(new Rect(inRect.x + 270f, y, 240f, 28f), "OuterrealmStorageManager_ShowOnlyUnseen".Translate(), ref showOnlyUnseen);
            if (showOnlyUnseen != dirtyUnseenFlag)
            {
                dirtyUnseenFlag = showOnlyUnseen;
                dirty = true;
            }
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

        /// <summary>重建分类树三态聚合缓存（原版 AllowanceStateOf 语义）：遍历所有条目，把每个条目的
        /// 实际显示与否（CategoryFilterAllows，依赖显式设置）沿其分类链累加到每个分类（含祖先）的
        /// 显示数/总数——父分类计数天然含子分类条目。聚合：全显示→绿✓、全隐藏→红×、混合→黄~。
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
                bool allowed = CategoryFilterAllows(gs, e);
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
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, visibleEntries.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0f;
            for (int i = 0; i < visibleEntries.Count; i++)
            {
                DoEntryRow(gs, visibleEntries[i], viewRect.width, ref curY, i % 2 == 0);
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
                if (showOnlyUnseen && CountVisibleBuildings(gs, e) > 0)
                {
                    continue;
                }
                // 分类筛选：条目所属分类链（含子分类）上存在选中分类才显示
                if (selectedCategory != null && !IsInCategory(e, selectedCategory))
                {
                    continue;
                }
                // 视图三态筛选（绿✓/红×/黄~）：分类被显式隐藏（且无其他分类链允许）则不显示
                if (!CategoryFilterAllows(gs, e))
                {
                    continue;
                }
                bool matches = searchWidget.filter.Matches(e.Proto.def.label)
                    || searchWidget.filter.Matches(e.Proto.def.defName);
                if (matches)
                {
                    visibleEntries.Add(e);
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

        /// <summary>视图三态筛选（绿✓/红×/黄~）：条目的每条所属分类链按实际效果（EffectiveManagerCatShow：
        /// 子分类显式设置覆盖父分类，未设置时跟随上级，根默认显示）判定——任一链允许即显示，全部链隐藏才隐藏。
        /// 与原版 ThingFilter 的允许语义一致（多分类条目按 OR 合并）。</summary>
        private static bool CategoryFilterAllows(GameComponent_OuterrealmStorage gs, OuterrealmEntry e)
        {
            ThingDef def = e.Key.Def;
            if (def == null || def.thingCategories == null || def.thingCategories.Count == 0)
            {
                return true;
            }
            for (int ci = 0; ci < def.thingCategories.Count; ci++)
            {
                if (gs.EffectiveManagerCatShow(def.thingCategories[ci]))
                {
                    return true;
                }
            }
            return false;
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
        private void DoEntryRow(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry, float width, ref float curY, bool evenRow)
        {
            Rect rect = new Rect(0f, curY, width, RowHeight);
            // 隔行条纹 + 鼠标划过整行高亮背景（参考万能制造机内容列表风格）
            if (evenRow)
            {
                Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.03f));
            }
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlightIfMouseover(rect);
            }
            int visibleBuildings = CountVisibleBuildings(gs, entry);

            if (Widgets.ButtonText(new Rect(rect.x + rect.width - 90f, curY + 2f, 86f, 24f), "OuterrealmStorageManager_EjectAll".Translate(), true, false, true))
            {
                gs.EnqueueEject(entry, TargetMap(), entry.Count, TargetAnchor());
            }
            if (Widgets.ButtonText(new Rect(rect.x + rect.width - 180f, curY + 2f, 86f, 24f), "OuterrealmStorageManager_Eject".Translate(), true, false, true))
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
            rect.width -= 190f;

            if (entry.Proto is Corpse protoCorpse && protoCorpse.Bugged)
            {
                Widgets.InfoCardButton(rect.width - 24f, curY, entry.Proto.def);
            }
            else
            {
                Widgets.InfoCardButton(rect.width - 24f, curY, entry.Proto);
            }
            rect.width -= 24f;
            OuterrealmVaultUtil.ThingIconSafe(new Rect(4f, curY, 28f, 28f), entry.Proto);

            string text = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto) + " x" + entry.Count.ToString("N0");
            string flagText = visibleBuildings == 0
                ? "OuterrealmStorageManager_Unseen".Translate()
                : "OuterrealmStorageManager_VisibleBuildings".Translate(visibleBuildings);
            // 名称左对齐、可见性标志右对齐：标志与名称分离显示，避免与长名称挤在一起难以查看
            Text.Anchor = TextAnchor.MiddleLeft;
            float flagWidth = Text.CalcSize(flagText).x;
            float nameWidth = rect.width - 36f - flagWidth - 12f;
            if (nameWidth < 20f)
            {
                nameWidth = 20f;
            }
            Widgets.Label(new Rect(36f, curY, nameWidth, rect.height), text.StripTags().Truncate(nameWidth));
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(rect.width - flagWidth, curY, flagWidth, rect.height), flagText);
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, text + "  " + flagText);
            curY += RowHeight;
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
