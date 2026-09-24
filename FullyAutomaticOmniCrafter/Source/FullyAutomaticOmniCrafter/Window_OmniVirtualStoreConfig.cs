using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 虚拟存储配置窗口：左侧分类树、中间物品列表、右侧目标数量清单。
    /// 布局与交互参考万能制造机（Dialog_OmniCrafter），并复用其分类树控件 Listing_TreeCategorySelect。
    /// 清单里的目标数量不设上限；取走多少，组件会在后续 tick 自动补回。
    /// </summary>
    public class Window_OmniVirtualStoreConfig : Window
    {
        private const float RowHeight = 28f;
        private const float TopBarHeight = 34f;

        private static readonly List<ThingDef> EmptyDefList = new List<ThingDef>();

        private readonly CompOmniVirtualStore store;
        private ThingCategoryDef selectedCategory;
        private bool showAll = true;
        private string searchText = "";
        private Vector2 categoryScroll;
        private Vector2 listScroll;
        private Vector2 wantedScroll;

        private HashSet<ThingCategoryDef> validCategories;
        private List<ThingDef> cachedList;
        private string cachedSearch = "\0";
        private bool cachedShowAll = true;
        private ThingCategoryDef cachedCategory;

        /// <summary>数量输入框的文本缓冲：按条目对象保存，避免清单增删后错位。</summary>
        private readonly Dictionary<VirtualStoreItem, string> countBuffers = new Dictionary<VirtualStoreItem, string>();

        /// <summary>待移除的 def：遍历清单期间不能直接改集合，先记下来。</summary>
        private ThingDef pendingRemoval;

        public override Vector2 InitialSize => new Vector2(1060f, 660f);

        public Window_OmniVirtualStoreConfig(CompOmniVirtualStore store)
        {
            this.store = store;
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (store?.parent == null || store.parent.Destroyed)
            {
                Close(true);
                return;
            }

            Rect top = new Rect(inRect.x, inRect.y, inRect.width, TopBarHeight);
            DrawTopBar(top);

            Rect body = new Rect(inRect.x, top.yMax + 4f, inRect.width, inRect.height - top.height - 8f);
            const float leftWidth = 230f;
            const float rightWidth = 340f;
            const float gap = 6f;

            Rect leftRect = new Rect(body.x, body.y, leftWidth, body.height);
            Rect rightRect = new Rect(body.xMax - rightWidth, body.y, rightWidth, body.height);
            Rect midRect = new Rect(leftRect.xMax + gap, body.y,
                Mathf.Max(160f, rightRect.x - leftRect.xMax - gap * 2f), body.height);

            DrawCategoryPanel(leftRect);
            DrawDefPanel(midRect);
            DrawWantedPanel(rightRect);
        }

        // ── 顶栏 ────────────────────────────────────────────────────────────────
        private void DrawTopBar(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width - 420f, rect.height),
                "OmniVirtualStore_WindowTitle".Translate());
            Text.Font = GameFont.Small;

            Rect toggleRect = new Rect(rect.xMax - 404f, rect.y + 5f, 150f, 26f);
            if (Widgets.ButtonText(toggleRect, store.Enabled
                    ? "OmniVirtualStore_Enabled".Translate()
                    : "OmniVirtualStore_Disabled".Translate()))
                store.SetEnabled(!store.Enabled);

            Rect clearRect = new Rect(rect.xMax - 246f, rect.y + 5f, 120f, 26f);
            if (Widgets.ButtonText(clearRect, "OmniVirtualStore_ClearList".Translate()))
            {
                store.ClearEntries();
                countBuffers.Clear();
                cachedList = null;
            }

            Rect reloadRect = new Rect(rect.xMax - 118f, rect.y + 5f, 118f, 26f);
            if (Widgets.ButtonText(reloadRect, "OmniVirtualStore_RefreshHint".Translate()))
                Messages.Message("OmniVirtualStore_RefreshHintText".Translate(), store.parent,
                    MessageTypeDefOf.NeutralEvent, false);
        }

        // ── 左：分类树 ──────────────────────────────────────────────────────────
        private void DrawCategoryPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);

            Rect allRect = new Rect(inner.x, inner.y, inner.width, 26f);
            if (showAll) Widgets.DrawHighlight(allRect);
            else Widgets.DrawHighlightIfMouseover(allRect);
            Widgets.Label(allRect.ContractedBy(4f, 0f), "OmniVirtualStore_All".Translate());
            if (Widgets.ButtonInvisible(allRect))
            {
                showAll = true;
                selectedCategory = null;
                cachedList = null;
            }

            Rect treeOuter = new Rect(inner.x, allRect.yMax + 4f, inner.width,
                inner.height - allRect.height - 4f);
            HashSet<ThingCategoryDef> valid = validCategories ?? (validCategories = BuildValidCategories());
            const float lineHeight = 24f;
            float treeHeight = CalcTreeHeight(ThingCategoryDefOf.Root.treeNode, valid, lineHeight);

            Rect view = new Rect(0f, 0f, treeOuter.width - 16f, Mathf.Max(treeHeight, 1f));
            Widgets.BeginScrollView(treeOuter, ref categoryScroll, view);
            Rect visible = new Rect(0f, categoryScroll.y, treeOuter.width, treeOuter.height);
            Listing_TreeCategorySelect listing = new Listing_TreeCategorySelect(valid, selectedCategory, cat =>
            {
                selectedCategory = cat;
                showAll = false;
                cachedList = null;
            });
            listing.SetVisibleRect(visible);
            listing.Begin(view);
            foreach (TreeNode_ThingCategory child in ThingCategoryDefOf.Root.treeNode.ChildCategoryNodes)
                listing.DoCategoryNode(child, 0, 1);
            listing.End();
            Widgets.EndScrollView();
        }

        private HashSet<ThingCategoryDef> BuildValidCategories()
        {
            HashSet<ThingCategoryDef> set = new HashSet<ThingCategoryDef>();
            foreach (KeyValuePair<ThingCategoryDef, List<ThingDef>> pair in OmniCrafterCache.ByCategory)
            {
                ThingCategoryDef cat = pair.Key;
                while (cat != null && set.Add(cat)) cat = cat.parent;
            }
            set.Add(ThingCategoryDefOf.Root);
            return set;
        }

        private static float CalcTreeHeight(TreeNode_ThingCategory node, HashSet<ThingCategoryDef> valid,
            float lineHeight)
        {
            if (node == null || !valid.Contains(node.catDef)) return 0f;
            float height = lineHeight + 2f;
            IEnumerable<TreeNode_ThingCategory> children = node.ChildCategoryNodes;
            if (children != null)
                foreach (TreeNode_ThingCategory child in children)
                    height += CalcTreeHeight(child, valid, lineHeight);
            return height;
        }

        // ── 中：物品列表 ────────────────────────────────────────────────────────
        private void DrawDefPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);

            Rect searchRect = new Rect(inner.x, inner.y, inner.width, 26f);
            searchText = Widgets.TextField(searchRect, searchText) ?? "";
            if (searchText != cachedSearch) cachedList = null;

            Rect listOuter = new Rect(inner.x, searchRect.yMax + 4f, inner.width,
                inner.height - searchRect.height - 4f);
            List<ThingDef> list = CurrentList();
            Rect view = new Rect(0f, 0f, listOuter.width - 16f, Mathf.Max(list.Count * RowHeight, 1f));
            Widgets.BeginScrollView(listOuter, ref listScroll, view);
            for (int i = 0; i < list.Count; i++)
            {
                ThingDef def = list[i];
                if (def == null) continue;
                Rect row = new Rect(0f, i * RowHeight, view.width, RowHeight - 2f);
                int target = store.GetTargetCount(def);
                if (target > 0) Widgets.DrawBoxSolid(row, new Color(0.18f, 0.34f, 0.18f, 0.55f));
                else Widgets.DrawHighlightIfMouseover(row);

                Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 2f, 22f, 22f), def);
                Widgets.Label(new Rect(row.x + 28f, row.y, row.width - 96f, row.height), def.LabelCap);
                if (target > 0)
                {
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(new Rect(row.xMax - 64f, row.y, 60f, row.height), target.ToString("N0"));
                    Text.Anchor = TextAnchor.UpperLeft;
                }

                if (Widgets.ButtonInvisible(row)) ToggleDef(def);
            }
            Widgets.EndScrollView();
        }

        private List<ThingDef> CurrentList()
        {
            if (cachedList != null && cachedSearch == searchText
                && cachedShowAll == showAll && cachedCategory == selectedCategory)
                return cachedList;

            cachedSearch = searchText;
            cachedShowAll = showAll;
            cachedCategory = selectedCategory;

            List<ThingDef> source;
            if (showAll || selectedCategory == null)
            {
                source = OmniCrafterCache.AllCraftable;
            }
            else
            {
                List<ThingDef> catList;
                OmniCrafterCache.ByCategory.TryGetValue(selectedCategory, out catList);
                source = catList ?? EmptyDefList;
            }

            string query = searchText?.Trim().ToLowerInvariant();
            if (query.NullOrEmpty())
            {
                cachedList = source;
                return cachedList;
            }

            bool pinyin = OmniCrafterMod.Settings != null && OmniCrafterMod.Settings.enablePinyinSearch
                && PinyinSearchEngine.IsReady;
            List<ThingDef> filtered = new List<ThingDef>();
            for (int i = 0; i < source.Count; i++)
            {
                ThingDef def = source[i];
                if (def == null) continue;
                bool match = (def.label != null && def.label.ToLowerInvariant().Contains(query))
                    || (def.defName != null && def.defName.ToLowerInvariant().Contains(query));
                if (!match && pinyin) match = PinyinSearchEngine.MatchesPinyin(def, query);
                if (match) filtered.Add(def);
            }
            cachedList = filtered;
            return cachedList;
        }

        /// <summary>点击列表行：已在清单中则移除，否则按一整堆的数量加入清单。</summary>
        private void ToggleDef(ThingDef def)
        {
            if (def == null) return;
            if (store.GetTargetCount(def) > 0) store.RemoveEntry(def);
            else store.SetTargetCount(def, def.stackLimit > 0 ? def.stackLimit : 1);
            countBuffers.Clear();
        }

        // ── 右：目标数量清单 ────────────────────────────────────────────────────
        private void DrawWantedPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), "OmniVirtualStore_WantedTitle".Translate());

            Rect hintRect = new Rect(inner.x, inner.yMax - 44f, inner.width, 44f);
            Rect listOuter = new Rect(inner.x, inner.y + 26f, inner.width,
                Mathf.Max(1f, inner.height - 26f - 46f));

            IReadOnlyList<VirtualStoreItem> items = store.Wanted;
            if (items.Count == 0)
            {
                Widgets.Label(listOuter, "OmniVirtualStore_WantedEmpty".Translate());
            }
            else
            {
                Rect view = new Rect(0f, 0f, listOuter.width - 16f, Mathf.Max(items.Count * RowHeight, 1f));
                Widgets.BeginScrollView(listOuter, ref wantedScroll, view);
                for (int i = 0; i < items.Count; i++)
                {
                    VirtualStoreItem item = items[i];
                    if (item?.thingDef == null) continue;
                    Rect row = new Rect(0f, i * RowHeight, view.width, RowHeight - 2f);
                    if (i % 2 == 1) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.04f));

                    Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 2f, 22f, 22f), item.thingDef);
                    Widgets.Label(new Rect(row.x + 28f, row.y, row.width - 134f, row.height),
                        item.thingDef.LabelCap);

                    Rect countRect = new Rect(row.xMax - 128f, row.y + 2f, 78f, 24f);
                    string buffer;
                    countBuffers.TryGetValue(item, out buffer);
                    int count = item.targetCount;
                    Widgets.TextFieldNumeric(countRect, ref count, ref buffer, 1f, 1E+09f);
                    countBuffers[item] = buffer;
                    if (count != item.targetCount) item.targetCount = Mathf.Max(1, count);

                    Rect removeRect = new Rect(row.xMax - 44f, row.y + 2f, 42f, 24f);
                    if (Widgets.ButtonText(removeRect, "OmniVirtualStore_Remove".Translate()))
                        pendingRemoval = item.thingDef;
                }
                Widgets.EndScrollView();
            }

            if (pendingRemoval != null)
            {
                store.RemoveEntry(pendingRemoval);
                pendingRemoval = null;
                countBuffers.Clear();
            }

            Widgets.Label(hintRect, "OmniVirtualStore_Hint".Translate());
        }
    }
}
