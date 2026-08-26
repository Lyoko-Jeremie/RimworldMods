using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using ToolGood.Words;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 自制右键菜单（仅 vault 菜单）：大列表 + 搜索（含拼音）+ 暂停 + 虚拟化视口裁剪。
    /// 由 Patch_OuterrealmFloatMenu 的 3 个 patch 驱动：
    ///   · FloatMenu.SetInitialSizeAndPosition Postfix —— 改成大窗口形态；
    ///   · FloatMenuMap.DoWindowContents Prefix —— 完全接管绘制（本类 Draw）；
    ///   · FloatMenu.PostClose Postfix —— 恢复打开前的暂停状态（NotifyClosed）。
    /// 显示内容 = FloatMenu.options（与原版同一份，构造时已按 Priority 排序）：
    /// 同文本（opt.Label）、同灰度（opt.Disabled）、同执行（opt.Chosen）、同图标（opt.iconThing）。
    /// 打开即暂停 → 暂停中游戏状态不变 → 快照天然一致，无需原版逐帧重验。
    /// </summary>
    internal static class CustomFloatMenuUtil
    {
        private const float Margin = 12f;
        private const float TitleHeight = 28f;
        private const float SearchBoxHeight = 30f;
        private const float CountLabelHeight = 20f;
        private const float RowHeightNormal = 30f;
        private const float RowHeightTiny = 24f;
        private const float IconSizeNormal = 28f;
        private const float IconSizeTiny = 16f;
        private const float Gap = 6f;
        private const float CategoryWidth = 340f;
        private const float CategoryLineHeight = 24f;

        private static readonly FieldInfo optionsField = AccessTools.Field(typeof(FloatMenu), "options");
        private static readonly FieldInfo titleField = AccessTools.Field(typeof(FloatMenu), "title");
        // 第三方 provider 常用构造只设置私有字段（shownItem / iconTex），反射读取以对齐原版绘制；
        // 仅在绘制无 iconThing 或需优先 shownItem 时读取，FieldInfo 静态缓存避免重复反射。
        private static readonly FieldInfo shownItemField = AccessTools.Field(typeof(FloatMenuOption), "shownItem");
        private static readonly FieldInfo iconTexField = AccessTools.Field(typeof(FloatMenuOption), "iconTex");
        private static readonly FieldInfo drawPlaceHolderIconField = AccessTools.Field(typeof(FloatMenuOption), "drawPlaceHolderIcon");
        private static readonly FieldInfo thingStyleField = AccessTools.Field(typeof(FloatMenuOption), "thingStyle");
        private static readonly FieldInfo forceBasicStyleField = AccessTools.Field(typeof(FloatMenuOption), "forceBasicStyle");
        private static readonly FieldInfo graphicIndexOverrideField = AccessTools.Field(typeof(FloatMenuOption), "graphicIndexOverride");
        private static readonly FieldInfo forceThingColorField = AccessTools.Field(typeof(FloatMenuOption), "forceThingColor");

        /// <summary>每个 vault 菜单实例的自制列表状态（实例级，CWT 自动回收，无泄漏）。</summary>
        private sealed class CustomMenuState
        {
            public bool Initialized;
            public string Title;
            public string SearchBuffer = ""; // 搜索框输入缓冲（防重绘吞字）
            public string SearchText = "";   // 当前生效的搜索词（小写）
            public List<FloatMenuOption> Filtered; // 过滤后的可见列表；null = 全部
            public Vector2 ScrollPosition;
            public Vector2 CategoryScrollPosition;
            public bool Tiny;                // 行高/图标尺寸模式（与 FloatMenu SizeMode 一致）
            public Building_OuterrealmVault Vault;
            public ThingCategoryDef SelectedCategory; // null = 全部分类；仅当前菜单生效
            public readonly HashSet<ThingCategoryDef> ValidCategories = new HashSet<ThingCategoryDef>();
            public readonly Dictionary<ThingCategoryDef, long> CategoryCounts = new Dictionary<ThingCategoryDef, long>();
            public readonly HashSet<Thing> CandidateTargets = new HashSet<Thing>();
        }

        private static readonly ConditionalWeakTable<FloatMenuMap, CustomMenuState> States =
            new ConditionalWeakTable<FloatMenuMap, CustomMenuState>();

        private struct PinyinPair
        {
            public string Full;     // 全拼小写无声调，如"超维存储仓"→"chaoweicunchucang"
            public string Initials; // 首字母小写，如"超维存储仓"→"cwccc"
        }

        /// <summary>Label → 拼音缓存（菜单打开时清空一次；选项列表打开期间固定，命中率高）。</summary>
        private static readonly Dictionary<string, PinyinPair> PinyinCache = new Dictionary<string, PinyinPair>();

        /// <summary>自制大列表是否对指定 vault 菜单生效：菜单对应建筑的实例模式为 CustomList。
        /// 每建筑独立（原全局 Mod 设置已迁移到建筑字段，随存档保存）；
        /// 非 vault 菜单 / 该建筑为原版模式 → 返回 false，走原版绘制与降频 patch。</summary>
        internal static bool IsCustomVaultMenuActive(FloatMenuMap menu)
        {
            Building_OuterrealmVault vault =
                Patch_FloatMenuMap_DoWindowContents_VaultSlowRefresh.GetMenuVault(menu);
            return vault != null && vault.RightClickMenuMode == RightClickMenuMode.CustomList;
        }

        /// <summary>供 FloatMenu 层面 patch 使用（如 SetInitialSizeAndPosition / PostClose）。</summary>
        internal static bool IsCustomVaultMenuActive(FloatMenu menu)
        {
            return menu is FloatMenuMap map && IsCustomVaultMenuActive(map);
        }

        /// <summary>接管绘制：暂停管理 + 布局（标题/搜索框/计数）+ 虚拟化列表。</summary>
        internal static void Draw(FloatMenuMap menu, Rect inRect)
        {
            CustomMenuState state = States.GetOrCreateValue(menu);
            List<FloatMenuOption> options = (List<FloatMenuOption>)optionsField.GetValue(menu);
            if (options == null || options.Count == 0)
            {
                return;
            }
            if (!state.Initialized)
            {
                state.Initialized = true;
                // 暂停由 Window.forcePause 机制保证（Patch_FloatMenu_SetInitialSizeAndPosition_CustomMenu 设置）：
                // 窗口在 WindowStack 期间 TickManager.ForcePaused → Paused，关闭后自动恢复，无需手动改速度。
                state.Tiny = options.Count > 60;
                state.Title = (string)titleField.GetValue(menu);
                state.Vault = Patch_FloatMenuMap_DoWindowContents_VaultSlowRefresh.GetMenuVault(menu);
                RebuildCategoryCache(options, state);
                PinyinCache.Clear();
            }

            float rowH = state.Tiny ? RowHeightTiny : RowHeightNormal;
            float y = inRect.y + Margin;
            float innerX = inRect.x + Margin;
            float innerW = inRect.width - Margin * 2f;

            // ── 标题（原版 title，如 pawn.LabelCap） ──
            if (!string.IsNullOrEmpty(state.Title))
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(innerX, y, innerW, TitleHeight), state.Title);
                Text.Font = GameFont.Small;
                y += TitleHeight + Gap;
            }

            // ── 主体：左侧临时分类树 + 右侧搜索和菜单列表 ──
            float bodyH = Mathf.Max(1f, inRect.yMax - Margin - y);
            float categoryW = Mathf.Min(CategoryWidth, Mathf.Max(180f, innerW * 0.34f));
            Rect categoryRect = new Rect(innerX, y, categoryW, bodyH);
            Rect rightRect = new Rect(categoryRect.xMax + Gap, y, Mathf.Max(1f, innerW - categoryW - Gap), bodyH);
            DrawCategoryPanel(categoryRect, options, state);

            float rightY = rightRect.y;
            Rect searchRect = new Rect(rightRect.x, rightY, rightRect.width, SearchBoxHeight);
            string edited = Widgets.TextField(searchRect, state.SearchBuffer);
            if (edited != state.SearchBuffer)
            {
                state.SearchBuffer = edited;
                state.SearchText = edited.Trim().ToLowerInvariant();
                RebuildFiltered(options, state);
            }
            rightY += SearchBoxHeight + Gap;

            // ── 计数 ──
            int shown = state.Filtered != null ? state.Filtered.Count : options.Count;
            GUI.color = Color.gray;
            Widgets.Label(
                new Rect(rightRect.x, rightY, rightRect.width, CountLabelHeight),
                "OuterrealmFloatMenu_Count".Translate(shown, options.Count));
            GUI.color = Color.white;
            rightY += CountLabelHeight + Gap;

            // ── 虚拟化列表（视口裁剪） ──
            Rect listRect = new Rect(rightRect.x, rightY, rightRect.width, Mathf.Max(1f, rightRect.yMax - rightY));
            List<FloatMenuOption> visible = state.Filtered != null ? state.Filtered : options;
            if (visible.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(listRect, "OuterrealmFloatMenu_NoMatch".Translate());
                GUI.color = Color.white;
                return;
            }
            float contentH = visible.Count * rowH;
            // viewRect 宽度让出垂直滚动条，避免出现多余的水平滚动条（与原版/项目其他对话框一致）。
            float contentW = Mathf.Max(1f, listRect.width - 16f);
            Widgets.BeginScrollView(listRect, ref state.ScrollPosition, new Rect(0f, 0f, contentW, contentH));
            float scrollY = state.ScrollPosition.y;
            int first = Mathf.FloorToInt(scrollY / rowH);
            int last = Mathf.Min(visible.Count - 1, Mathf.CeilToInt((scrollY + listRect.height) / rowH));
            float iconSize = state.Tiny ? IconSizeTiny : IconSizeNormal;
            bool executed = false;
            for (int i = first; i <= last; i++)
            {
                FloatMenuOption opt = visible[i];
                if (opt == null)
                {
                    continue;
                }
                // BeginScrollView 已按 scrollPosition 平移+裁剪，行坐标必须用内容绝对坐标（i * rowH），
                // 不能再减 scrollY，否则滚动后行会被二次偏移并裁出可视区，导致列表后面部分不显示。
                Rect row = new Rect(0f, i * rowH, contentW, rowH);
                if (DrawRow(row, opt, iconSize))
                {
                    executed = true;
                    break;
                }
            }
            Widgets.EndScrollView();
            if (executed)
            {
                Find.WindowStack.TryRemove(menu); // 点击执行后关闭（PostClose 恢复暂停）
            }
        }

        /// <summary>
        /// 绘制一行选项；返回 true 表示该行被点击且执行了 action（调用方应关闭菜单）。
        /// Disabled 项铺原版同款深灰背景（ColorBGDisabled）并以淡灰文字/图标显示，
        /// 点击仅播 ClickReject（与原版 Chosen 行为一致）。
        /// </summary>
        private static bool DrawRow(Rect row, FloatMenuOption opt, float iconSize)
        {
            bool disabled = opt.Disabled;
            bool hover = !disabled && Mouse.IsOver(row);
            if (hover)
            {
                Widgets.DrawHighlight(row);
            }
            if (disabled)
            {
                // 与原版 FloatMenuOption.DoGUI 一致：禁用项先铺深灰背景（ColorBGDisabled）
                // 提示禁用，文字/图标随后以淡灰（ColorTextDisabled）绘制。
                GUI.color = FloatMenuOption.ColorBGDisabled;
                GUI.DrawTexture(row, BaseContent.WhiteTex);
                GUI.color = FloatMenuOption.ColorTextDisabled;
            }
            // 图标（与原版 FloatMenuOption.DoGUI 同优先级：shownItem/占位 → iconTex → iconThing → 灰占位）。
            // 第三方 provider 若只设置私有字段（shownItem / iconTex）也能正确显示，与原版菜单视觉一致。
            Rect iconRect = new Rect(row.x + 4f, row.y + (row.height - iconSize) / 2f, iconSize, iconSize);
            ThingDef shownItem = (ThingDef)shownItemField.GetValue(opt);
            bool drawPlaceholder = (bool)drawPlaceHolderIconField.GetValue(opt);
            if (shownItem != null || drawPlaceholder)
            {
                ThingStyleDef style = (ThingStyleDef)thingStyleField.GetValue(opt);
                if ((bool)forceBasicStyleField.GetValue(opt))
                {
                    style = null;
                }
                Color? thingColor = (Color?)forceThingColorField.GetValue(opt);
                if (!thingColor.HasValue)
                {
                    thingColor = shownItem == null
                        ? Color.white
                        : (shownItem.MadeFromStuff
                            ? shownItem.GetColorForStuff(GenStuff.DefaultStuffFor(shownItem))
                            : shownItem.uiIconColor);
                }
                Widgets.DefIcon(
                    iconRect,
                    shownItem,
                    thingStyleDef: style,
                    drawPlaceholder: drawPlaceholder,
                    color: thingColor,
                    graphicIndexOverride: (int?)graphicIndexOverrideField.GetValue(opt));
            }
            else
            {
                Texture2D iconTex = (Texture2D)iconTexField.GetValue(opt);
                if (iconTex != null)
                {
                    GUI.color = opt.iconColor;
                    Widgets.DrawTextureFitted(iconRect, iconTex, 1f, new Vector2(1f, 1f), opt.iconTexCoords);
                    GUI.color = Color.white;
                }
                else if (opt.iconThing != null)
                {
                    Widgets.ThingIcon(iconRect, opt.iconThing);
                }
                else
                {
                    GUI.DrawTexture(iconRect, BaseContent.GreyTex);
                }
            }
            // 文本（与原版同 Label，自动裁切）
            Rect labelRect = new Rect(iconRect.xMax + 8f, row.y, row.xMax - iconRect.xMax - 8f - 8f, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, opt.Label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            // tooltip
            if (opt.tooltip.HasValue)
            {
                TooltipHandler.TipRegion(row, opt.tooltip.Value);
            }
            // 点击
            if (Widgets.ButtonInvisible(row))
            {
                if (disabled)
                {
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                }
                else
                {
                    opt.Chosen(true, null); // colonistOrdering=true 与原版 givesColonistOrders 一致；null 跳过 PreOptionChosen（暂停下无需校验）
                    return true;
                }
            }
            return false;
        }

        /// <summary>窗口关闭时清理状态。暂停恢复由 Window.forcePause 机制自动完成（窗口移出 WindowStack 后不再强制暂停）。</summary>
        internal static void NotifyClosed(FloatMenuMap menu)
        {
            States.Remove(menu);
        }

        /// <summary>
        /// 左侧临时分类树。分类集合只来自本次菜单中实际产生了选项的 vault 视图目标，
        /// 因而天然位于建筑 StorageSettings 已筛选后的物品集合之上；选择状态不写回任何持久筛选。
        /// </summary>
        private static void DrawCategoryPanel(Rect rect, List<FloatMenuOption> options, CustomMenuState state)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Rect outRect = rect.ContractedBy(3f);
            float treeH = ComputeTreeHeight(ThingCategoryDefOf.Root.treeNode, state.ValidCategories, CategoryLineHeight);
            float totalH = CategoryLineHeight + 2f + Gap + treeH;
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - 16f), Mathf.Max(1f, totalH));

            Widgets.BeginScrollView(outRect, ref state.CategoryScrollPosition, viewRect);
            float y = 0f;
            Rect allRect = new Rect(0f, y, viewRect.width, CategoryLineHeight);
            DrawCategoryNavItem(allRect, "OuterrealmStorageManager_AllCategories".Translate(), state.SelectedCategory == null, () =>
            {
                state.SelectedCategory = null;
                RebuildFiltered(options, state);
            });
            y += CategoryLineHeight + 2f + Gap;

            Rect treeRect = new Rect(0f, y, viewRect.width, Mathf.Max(1f, treeH));
            Rect visibleRect = new Rect(0f, state.CategoryScrollPosition.y - y, viewRect.width, outRect.height);
            Listing_TreeCategorySelect listing = new Listing_TreeCategorySelect(
                state.ValidCategories,
                state.SelectedCategory,
                cat =>
                {
                    state.SelectedCategory = cat;
                    RebuildFiltered(options, state);
                },
                state.CategoryCounts);
            listing.SetVisibleRect(visibleRect);
            listing.Begin(treeRect);
            foreach (TreeNode_ThingCategory child in ThingCategoryDefOf.Root.treeNode.ChildCategoryNodes)
            {
                listing.DoCategoryNode(child, 0, 1);
            }
            listing.End();
            Widgets.EndScrollView();
        }

        /// <summary>绘制“全部分类”导航项，行为与存储管理器的分类树一致。</summary>
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

        /// <summary>
        /// 从菜单选项反向收集实际 vault 目标。原版 GetProviderOptions 会在目标选项未自带图标时
        /// 把 iconThing 设置为目标 Thing；同一目标可生成多个操作，因此用 HashSet 去重后再累计分类。
        /// </summary>
        private static void RebuildCategoryCache(List<FloatMenuOption> options, CustomMenuState state)
        {
            state.ValidCategories.Clear();
            state.CategoryCounts.Clear();
            state.CandidateTargets.Clear();
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption option = options[i];
                ThingDef def;
                Thing target;
                if (!TryGetVaultTarget(option, state, out target, out def) || !state.CandidateTargets.Add(target)
                    || def.thingCategories == null)
                {
                    continue;
                }
                for (int ci = 0; ci < def.thingCategories.Count; ci++)
                {
                    ThingCategoryDef category = def.thingCategories[ci];
                    while (category != null)
                    {
                        state.ValidCategories.Add(category);
                        long count;
                        state.CategoryCounts.TryGetValue(category, out count);
                        state.CategoryCounts[category] = count + 1L;
                        category = category.parent;
                    }
                }
            }
        }

        /// <summary>递归计算当前有效分类树的滚动内容高度。</summary>
        private static float ComputeTreeHeight(TreeNode_ThingCategory node, HashSet<ThingCategoryDef> validCategories, float lineHeight)
        {
            float height = 0f;
            foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
            {
                if (!validCategories.Contains(child.catDef))
                {
                    continue;
                }
                height += lineHeight + 2f;
                if (child.IsOpen(1))
                {
                    height += ComputeTreeHeight(child, validCategories, lineHeight);
                }
            }
            return height;
        }

        /// <summary>搜索词或临时分类变化时重建过滤列表（O(N) 一次，保持原排序）。</summary>
        private static void RebuildFiltered(List<FloatMenuOption> options, CustomMenuState state)
        {
            if (state.SearchText.Length == 0 && state.SelectedCategory == null)
            {
                state.Filtered = null;
                state.ScrollPosition.y = 0f;
                return;
            }
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption opt = options[i];
                if (opt == null)
                {
                    continue;
                }
                ThingDef targetDef;
                Thing target;
                if (state.SelectedCategory != null
                    && TryGetVaultTarget(opt, state, out target, out targetDef)
                    && !IsInCategory(targetDef, state.SelectedCategory))
                {
                    continue;
                }
                if (state.SearchText.Length == 0 || Matches(opt.Label, state.SearchText))
                {
                    list.Add(opt);
                }
            }
            state.Filtered = list;
            state.ScrollPosition.y = 0f;
        }

        /// <summary>
        /// 识别由当前建筑视图副本生成的目标选项。非 vault 目标（建筑自身、地面对象、通用 provider 项）
        /// 不参与分类筛选并始终保留，避免分类导航误删同一格上的其他合法命令。
        /// </summary>
        private static bool TryGetVaultTarget(FloatMenuOption option, CustomMenuState state, out Thing target, out ThingDef def)
        {
            target = option != null ? option.iconThing : null;
            def = null;
            if (target == null || state.Vault == null)
            {
                return false;
            }
            OuterrealmVaultViewThingOwner view = target.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view == null || !ReferenceEquals(view.Context, state.Vault))
            {
                return false;
            }
            def = target.def;
            return def != null;
        }

        /// <summary>ThingDef 的任一直接分类或其祖先是否命中所选分类。</summary>
        private static bool IsInCategory(ThingDef def, ThingCategoryDef selectedCategory)
        {
            if (def == null || def.thingCategories == null)
            {
                return false;
            }
            for (int ci = 0; ci < def.thingCategories.Count; ci++)
            {
                ThingCategoryDef category = def.thingCategories[ci];
                while (category != null)
                {
                    if (category == selectedCategory)
                    {
                        return true;
                    }
                    category = category.parent;
                }
            }
            return false;
        }

        /// <summary>
        /// 匹配规则（keyword 已小写）：
        ///   1. Label 子串（忽略大小写，覆盖中文/英文直接输入）；
        ///   2. 拼音首字母子串（如 "cwcc" 命中 "超维存储仓"）；
        ///   3. 拼音全拼子串（如 "cunchu" 命中 "存储"）。
        /// 拼音转换经 ToolGood.Words，结果按 Label 缓存。
        /// </summary>
        private static bool Matches(string label, string keyword)
        {
            if (string.IsNullOrEmpty(label) || keyword.Length == 0)
            {
                return keyword.Length == 0;
            }
            if (label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (!WordsHelper.HasChinese(label))
            {
                return false;
            }
            PinyinPair pair;
            if (!PinyinCache.TryGetValue(label, out pair))
            {
                try
                {
                    string full = WordsHelper.GetPinyin(label, "", false);
                    string ini = WordsHelper.GetFirstPinyin(label);
                    pair = new PinyinPair
                    {
                        Full = full != null ? full.ToLowerInvariant() : string.Empty,
                        Initials = ini != null ? ini.ToLowerInvariant() : string.Empty
                    };
                }
                catch
                {
                    pair = new PinyinPair();
                }
                PinyinCache[label] = pair;
            }
            return (pair.Initials.Length > 0 && pair.Initials.Contains(keyword))
                || (pair.Full.Length > 0 && pair.Full.Contains(keyword));
        }
    }
}
