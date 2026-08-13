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
    /// 左侧原版风格树状分类（参考万能制造机 OmniCrafterUi 的 Listing_TreeCategorySelect）。
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
        private readonly List<OuterrealmEntry> visibleEntries = new List<OuterrealmEntry>();

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

        public override void DoWindowContents(Rect inRect)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                Widgets.Label(inRect, "OuterrealmStorageManager_NoEntries".Translate());
                return;
            }
            if (dirty)
            {
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
                        gs.EnqueueEject(e.Key, TargetMap(), e.Count);
                    }
                }
            }
            y += 30f;

            // 搜索 + 不可见筛选 + 地图选择
            searchWidget.OnGUI(new Rect(inRect.x, y, 260f, 28f), () => dirty = true, () => dirty = true);
            Widgets.CheckboxLabeled(new Rect(inRect.x + 270f, y, 240f, 28f), "OuterrealmStorageManager_ShowOnlyUnseen".Translate(), ref showOnlyUnseen, true);
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

            HashSet<ThingCategoryDef> validCats = GetValidCategorySet();
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
            Listing_TreeCategorySelect listing = new Listing_TreeCategorySelect(
                validCats,
                selectedCategory,
                cat =>
                {
                    selectedCategory = cat;
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

        /// <summary>收集当前有内容条目的分类及其全部祖先（树只显示这些分类）。</summary>
        private HashSet<ThingCategoryDef> GetValidCategorySet()
        {
            HashSet<ThingCategoryDef> set = new HashSet<ThingCategoryDef>();
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            List<OuterrealmEntry> all = gs != null ? gs.EntriesForReading : null;
            if (all == null)
            {
                return set;
            }
            for (int i = 0; i < all.Count; i++)
            {
                OuterrealmEntry e = all[i];
                if (e == null || e.Count <= 0 || e.Key.Def == null)
                {
                    continue;
                }
                ThingDef def = e.Key.Def;
                if (def == null || def.thingCategories == null)
                {
                    continue;
                }
                for (int ci = 0; ci < def.thingCategories.Count; ci++)
                {
                    ThingCategoryDef c = def.thingCategories[ci];
                    while (c != null)
                    {
                        set.Add(c);
                        c = c.parent;
                    }
                }
            }
            return set;
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

        /// <summary>该条目当前被几座终端可见（帮助定位死锁条目，§6.4）。</summary>
        private static int CountVisibleBuildings(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry)
        {
            int count = 0;
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.view != null && v.view.FindCopy(entry.Key) != null)
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
                gs.EnqueueEject(entry.Key, TargetMap(), entry.Count);
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
                        (int v) => gs.EnqueueEject(entry.Key, TargetMap(), v)));
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
            if (visibleBuildings == 0)
            {
                text += "  (" + "OuterrealmStorageManager_Unseen".Translate() + ")";
            }
            else
            {
                text += "  " + "OuterrealmStorageManager_VisibleBuildings".Translate(visibleBuildings);
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(36f, curY, rect.width - 36f, rect.height), text.StripTags().Truncate(rect.width - 36f));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, text);
            curY += RowHeight;
        }

        private Map TargetMap()
        {
            if (selectedMapIndex >= 0 && selectedMapIndex < Find.Maps.Count)
            {
                return Find.Maps[selectedMapIndex];
            }
            return Find.CurrentMap;
        }
    }
}
