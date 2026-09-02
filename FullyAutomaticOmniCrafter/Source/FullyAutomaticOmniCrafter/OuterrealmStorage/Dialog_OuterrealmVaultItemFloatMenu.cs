using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储仓两级右键菜单。
    /// 第一层只枚举轻量物品目标并虚拟化绘制；选择目标后，第二层才通过原版
    /// FloatMenuMakerMap 为该目标生成操作，从根源移除“全部物品 × 全部 Provider/WorkGiver”的首开成本。
    /// </summary>
    internal sealed class Dialog_OuterrealmVaultItemFloatMenu : Window
    {
        private sealed class MenuTarget
        {
            public Thing Thing;
            public OuterrealmEntry Entry;
            public string Label;
        }

        private const float MarginSize = 10f;
        private const float HeaderHeight = 34f;
        private const float SearchHeight = 30f;
        private const float CountHeight = 22f;
        private const float TargetRowHeight = 42f;
        private const float OptionRowHeight = 30f;
        private const float Gap = 6f;
        private const float TargetIconSize = 34f;
        private const float OptionIconSize = 28f;
        private const float CategoryWidth = 340f;
        private const float CategoryLineHeight = 24f;

        private readonly Building_OuterrealmVault vault;
        private readonly List<Pawn> selectedPawns;
        private readonly Vector3 clickPos;
        private readonly List<MenuTarget> targets = new List<MenuTarget>();
        private readonly List<MenuTarget> filteredTargets = new List<MenuTarget>();
        private readonly List<Thing> anchorBuffer = new List<Thing>();
        private readonly HashSet<Thing> seenTargets = new HashSet<Thing>();
        private readonly HashSet<ThingCategoryDef> validCategories = new HashSet<ThingCategoryDef>();
        private readonly Dictionary<ThingCategoryDef, long> categoryCounts =
            new Dictionary<ThingCategoryDef, long>();

        private Vector2 targetScroll;
        private Vector2 categoryScroll;
        private Vector2 optionScroll;
        private string searchBuffer = string.Empty;
        private string searchText = string.Empty;
        private ThingCategoryDef selectedCategory;
        private MenuTarget selectedTarget;
        private List<FloatMenuOption> selectedOptions;

        public override Vector2 InitialSize => new Vector2(
            Mathf.Min(1100f, UI.screenWidth * 0.72f),
            Mathf.Min(760f, UI.screenHeight * 0.78f));

        public Dialog_OuterrealmVaultItemFloatMenu(
            Building_OuterrealmVault vault, List<Pawn> selectedPawns, Vector3 clickPos)
        {
            this.vault = vault;
            this.selectedPawns = selectedPawns ?? new List<Pawn>();
            this.clickPos = clickPos;
            doCloseButton = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            preventCameraMotion = true;
            forcePause = true;
            CustomFloatMenuUtil.ResetSearchCache();
            RebuildTargets();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (vault == null || vault.Destroyed || !vault.Spawned)
            {
                Close(false);
                return;
            }
            Text.Font = GameFont.Small;
            Rect content = inRect.ContractedBy(MarginSize);
            // 给 Window 自带的关闭按钮留出底部空间。
            content.height = Mathf.Max(1f, content.height - 38f);
            if (selectedTarget == null)
            {
                DrawTargetStage(content);
            }
            else
            {
                DrawOptionStage(content);
            }
        }

        /// <summary>第一层：仓库操作入口 + 临时分类树 + 搜索框 + 物品虚拟列表。</summary>
        private void DrawTargetStage(Rect rect)
        {
            float y = rect.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, y, rect.width - 250f, HeaderHeight),
                "OuterrealmItemMenu_Title".Translate(vault.LabelCap));
            Text.Font = GameFont.Small;
            Rect vaultActionsRect = new Rect(rect.xMax - 240f, y, 240f, HeaderHeight - 2f);
            if (Widgets.ButtonText(vaultActionsRect, "OuterrealmItemMenu_VaultActions".Translate()))
            {
                OpenOptions(new MenuTarget
                {
                    Thing = vault,
                    Label = "OuterrealmItemMenu_VaultActions".Translate()
                });
                return;
            }
            y += HeaderHeight + Gap;

            float bodyHeight = Mathf.Max(1f, rect.yMax - y);
            float categoryWidth = Mathf.Min(CategoryWidth, Mathf.Max(180f, rect.width * 0.34f));
            Rect categoryRect = new Rect(rect.x, y, categoryWidth, bodyHeight);
            Rect rightRect = new Rect(
                categoryRect.xMax + Gap,
                y,
                Mathf.Max(1f, rect.width - categoryWidth - Gap),
                bodyHeight);
            DrawCategoryPanel(categoryRect);

            float rightY = rightRect.y;
            string edited = Widgets.TextField(new Rect(rightRect.x, rightY, rightRect.width, SearchHeight), searchBuffer);
            if (edited != searchBuffer)
            {
                searchBuffer = edited;
                searchText = edited.Trim().ToLowerInvariant();
                RebuildFilteredTargets();
            }
            rightY += SearchHeight + Gap;

            List<MenuTarget> visible = searchText.Length == 0 && selectedCategory == null
                ? targets
                : filteredTargets;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rightRect.x, rightY, rightRect.width, CountHeight),
                "OuterrealmItemMenu_Count".Translate(visible.Count, targets.Count));
            GUI.color = Color.white;
            rightY += CountHeight + Gap;

            Rect outRect = new Rect(
                rightRect.x,
                rightY,
                rightRect.width,
                Mathf.Max(1f, rightRect.yMax - rightY));
            if (visible.Count == 0)
            {
                Widgets.Label(outRect, "OuterrealmItemMenu_NoItems".Translate());
                return;
            }
            float contentHeight = visible.Count * TargetRowHeight;
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - 16f), contentHeight);
            Widgets.BeginScrollView(outRect, ref targetScroll, viewRect);
            int first = Mathf.Max(0, Mathf.FloorToInt(targetScroll.y / TargetRowHeight) - 1);
            int last = Mathf.Min(visible.Count,
                Mathf.CeilToInt((targetScroll.y + outRect.height) / TargetRowHeight) + 1);
            for (int i = first; i < last; i++)
            {
                DrawTargetRow(new Rect(0f, i * TargetRowHeight, viewRect.width, TargetRowHeight), visible[i], i);
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 绘制只影响本次菜单的分类树。分类集合来自当前仓实际可浏览目标，
        /// 不读写建筑 StorageSettings，也不改变超维库存可见性。
        /// </summary>
        private void DrawCategoryPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Rect outRect = rect.ContractedBy(3f);
            float treeHeight = ComputeTreeHeight(
                ThingCategoryDefOf.Root.treeNode, validCategories, CategoryLineHeight);
            float totalHeight = CategoryLineHeight + 2f + Gap + treeHeight;
            Rect viewRect = new Rect(
                0f,
                0f,
                Mathf.Max(1f, outRect.width - 16f),
                Mathf.Max(1f, totalHeight));

            Widgets.BeginScrollView(outRect, ref categoryScroll, viewRect);
            float y = 0f;
            Rect allRect = new Rect(0f, y, viewRect.width, CategoryLineHeight);
            DrawCategoryNavItem(
                allRect,
                "OuterrealmStorageManager_AllCategories".Translate(),
                selectedCategory == null,
                () =>
                {
                    selectedCategory = null;
                    RebuildFilteredTargets();
                });
            y += CategoryLineHeight + 2f + Gap;

            Rect treeRect = new Rect(0f, y, viewRect.width, Mathf.Max(1f, treeHeight));
            Rect visibleRect = new Rect(0f, categoryScroll.y - y, viewRect.width, outRect.height);
            Listing_TreeCategorySelect listing = new Listing_TreeCategorySelect(
                validCategories,
                selectedCategory,
                category =>
                {
                    selectedCategory = category;
                    RebuildFilteredTargets();
                },
                categoryCounts);
            listing.SetVisibleRect(visibleRect);
            listing.Begin(treeRect);
            foreach (TreeNode_ThingCategory child in ThingCategoryDefOf.Root.treeNode.ChildCategoryNodes)
            {
                listing.DoCategoryNode(child, 0, 1);
            }
            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawCategoryNavItem(Rect rect, string label, bool selected, Action onClick)
        {
            if (selected)
            {
                Widgets.DrawHighlight(rect);
            }
            else if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlightIfMouseover(rect);
            }
            Widgets.Label(rect.ContractedBy(2f, 0f), label);
            if (Widgets.ButtonInvisible(rect))
            {
                onClick();
            }
        }

        private static float ComputeTreeHeight(
            TreeNode_ThingCategory node,
            HashSet<ThingCategoryDef> categories,
            float lineHeight)
        {
            float height = 0f;
            foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
            {
                if (!categories.Contains(child.catDef))
                {
                    continue;
                }
                height += lineHeight + 2f;
                if (child.IsOpen(1))
                {
                    height += ComputeTreeHeight(child, categories, lineHeight);
                }
            }
            return height;
        }

        private void DrawTargetRow(Rect row, MenuTarget target, int index)
        {
            if ((index & 1) == 0)
            {
                Widgets.DrawAltRect(row);
            }
            if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }
            Rect iconRect = new Rect(row.x + 4f, row.y + (row.height - TargetIconSize) * 0.5f,
                TargetIconSize, TargetIconSize);
            OuterrealmVaultUtil.ThingIconSafe(iconRect, target.Thing);

            long count = target.Entry != null ? target.Entry.Count : 0L;
            Rect countRect = new Rect(row.xMax - 150f, row.y, 140f, row.height);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(countRect, "OuterrealmItemMenu_ItemCount".Translate(count.ToString("N0")));
            Text.Anchor = TextAnchor.UpperLeft;
            Rect labelRect = new Rect(iconRect.xMax + 8f, row.y, countRect.xMin - iconRect.xMax - 12f, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, target.Label);
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(row))
            {
                if (!TargetStillValid(target))
                {
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                    RebuildTargets();
                    return;
                }
                OpenOptions(target);
            }
        }

        /// <summary>第二层：仅显示所选目标刚生成的操作；不使用 FloatMenuMap，避免全仓重新验证。</summary>
        private void DrawOptionStage(Rect rect)
        {
            float y = rect.y;
            Rect backRect = new Rect(rect.x, y, 150f, HeaderHeight - 2f);
            if (Widgets.ButtonText(backRect, "OuterrealmItemMenu_Back".Translate()))
            {
                selectedTarget = null;
                selectedOptions = null;
                optionScroll = Vector2.zero;
                return;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(backRect.xMax + Gap, y, rect.width - backRect.width - Gap, HeaderHeight),
                "OuterrealmItemMenu_ActionsTitle".Translate(selectedTarget.Label));
            Text.Font = GameFont.Small;
            y += HeaderHeight + Gap;

            Rect outRect = new Rect(rect.x, y, rect.width, Mathf.Max(1f, rect.yMax - y));
            if (selectedOptions == null || selectedOptions.Count == 0)
            {
                Widgets.Label(outRect, "OuterrealmItemMenu_NoActions".Translate());
                return;
            }
            float contentHeight = selectedOptions.Count * OptionRowHeight;
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - 16f), contentHeight);
            Widgets.BeginScrollView(outRect, ref optionScroll, viewRect);
            int first = Mathf.Max(0, Mathf.FloorToInt(optionScroll.y / OptionRowHeight) - 1);
            int last = Mathf.Min(selectedOptions.Count,
                Mathf.CeilToInt((optionScroll.y + outRect.height) / OptionRowHeight) + 1);
            bool executed = false;
            for (int i = first; i < last; i++)
            {
                FloatMenuOption option = selectedOptions[i];
                if (option != null && CustomFloatMenuUtil.DrawRow(
                    new Rect(0f, i * OptionRowHeight, viewRect.width, OptionRowHeight), option, OptionIconSize))
                {
                    executed = true;
                    break;
                }
            }
            Widgets.EndScrollView();
            if (executed)
            {
                Close();
            }
        }

        private void OpenOptions(MenuTarget target)
        {
            selectedTarget = target;
            optionScroll = Vector2.zero;
            try
            {
                using (VaultTargetOnlyFloatMenuScope.Enter(target.Thing))
                {
                    selectedOptions = FloatMenuMakerMap.GetOptions(
                        new List<Pawn>(selectedPawns), clickPos, out FloatMenuContext _);
                }
                if (selectedOptions == null)
                {
                    selectedOptions = new List<FloatMenuOption>();
                }
                selectedOptions.RemoveAll(option => option == null);
                selectedOptions.Sort(CompareOptions);
            }
            catch (Exception ex)
            {
                Log.Error("[FAOC] 为超维存储两级菜单目标生成操作失败: " + ex);
                selectedOptions = new List<FloatMenuOption>();
            }
        }

        private static int CompareOptions(FloatMenuOption a, FloatMenuOption b)
        {
            int priority = b.Priority.CompareTo(a.Priority);
            return priority != 0 ? priority : b.orderInPriority.CompareTo(a.orderInPriority);
        }

        private void RebuildTargets()
        {
            targets.Clear();
            filteredTargets.Clear();
            anchorBuffer.Clear();
            seenTargets.Clear();
            validCategories.Clear();
            categoryCounts.Clear();
            if (vault == null || vault.view == null)
            {
                return;
            }

            List<Thing> copies = vault.view.InnerListForReading;
            for (int i = 0; i < copies.Count; i++)
            {
                Thing thing = copies[i];
                OuterrealmEntry entry = vault.view.GetEntryOf(thing);
                AddTarget(thing, entry);
            }
            OuterrealmIdentityRouting.AppendMenuAnchors(vault, anchorBuffer);
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            for (int i = 0; i < anchorBuffer.Count; i++)
            {
                Thing thing = anchorBuffer[i];
                OuterrealmEntry entry;
                if (gs != null && gs.TryGetCanonicalEntry(thing, out entry))
                {
                    AddTarget(thing, entry);
                }
            }
            RebuildCategoryCache();
            if (searchText.Length != 0 || selectedCategory != null)
            {
                RebuildFilteredTargets();
            }
            targetScroll = Vector2.zero;
        }

        /// <summary>收集目标所属分类及全部祖先，并缓存各分类（含子分类）的目标数量。</summary>
        private void RebuildCategoryCache()
        {
            validCategories.Clear();
            categoryCounts.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                ThingDef def = targets[i].Thing.def;
                if (def == null || def.thingCategories == null)
                {
                    continue;
                }
                for (int categoryIndex = 0; categoryIndex < def.thingCategories.Count; categoryIndex++)
                {
                    ThingCategoryDef category = def.thingCategories[categoryIndex];
                    while (category != null)
                    {
                        validCategories.Add(category);
                        long count;
                        categoryCounts.TryGetValue(category, out count);
                        categoryCounts[category] = count + 1L;
                        category = category.parent;
                    }
                }
            }
            if (selectedCategory != null && !validCategories.Contains(selectedCategory))
            {
                selectedCategory = null;
            }
        }

        private void AddTarget(Thing thing, OuterrealmEntry entry)
        {
            if (thing == null || entry == null || entry.Count <= 0 || !seenTargets.Add(thing))
            {
                return;
            }
            targets.Add(new MenuTarget
            {
                Thing = thing,
                Entry = entry,
                Label = OuterrealmVaultUtil.SafeLabelCapNoCount(thing)
            });
        }

        private void RebuildFilteredTargets()
        {
            filteredTargets.Clear();
            if (searchText.Length == 0 && selectedCategory == null)
            {
                targetScroll = Vector2.zero;
                return;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                MenuTarget target = targets[i];
                if (selectedCategory != null && !IsInCategory(target.Thing.def, selectedCategory))
                {
                    continue;
                }
                if (searchText.Length == 0
                    || CustomFloatMenuUtil.Matches(target.Label, searchText)
                    || target.Thing.def.defName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filteredTargets.Add(target);
                }
            }
            targetScroll = Vector2.zero;
        }

        private static bool IsInCategory(ThingDef def, ThingCategoryDef category)
        {
            if (def == null || def.thingCategories == null)
            {
                return false;
            }
            for (int i = 0; i < def.thingCategories.Count; i++)
            {
                ThingCategoryDef current = def.thingCategories[i];
                while (current != null)
                {
                    if (current == category)
                    {
                        return true;
                    }
                    current = current.parent;
                }
            }
            return false;
        }

        private bool TargetStillValid(MenuTarget target)
        {
            if (target == null || target.Thing == null || target.Entry == null || target.Entry.Count <= 0
                || !vault.CanShow(target.Entry.Proto))
            {
                return false;
            }
            if (target.Thing.holdingOwner is OuterrealmVaultViewThingOwner view)
            {
                return ReferenceEquals(view.Context, vault) && ReferenceEquals(view.GetEntryOf(target.Thing), target.Entry);
            }
            return OuterrealmIdentityRouting.IsAnchor(target.Thing)
                && OuterrealmIdentityRouting.TryGetAnchor(
                    target.Thing, out Building_OuterrealmVault anchorVault, out IntVec3 _)
                && ReferenceEquals(anchorVault, vault);
        }
    }
}
