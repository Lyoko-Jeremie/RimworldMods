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

        private static readonly FieldInfo optionsField = AccessTools.Field(typeof(FloatMenu), "options");
        private static readonly FieldInfo titleField = AccessTools.Field(typeof(FloatMenu), "title");

        /// <summary>每个 vault 菜单实例的自制列表状态（实例级，CWT 自动回收，无泄漏）。</summary>
        private sealed class CustomMenuState
        {
            public bool Initialized;
            public string Title;
            public string SearchBuffer = ""; // 搜索框输入缓冲（防重绘吞字）
            public string SearchText = "";   // 当前生效的搜索词（小写）
            public List<FloatMenuOption> Filtered; // 过滤后的可见列表；null = 全部
            public Vector2 ScrollPosition;
            public bool Tiny;                // 行高/图标尺寸模式（与 FloatMenu SizeMode 一致）
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

        /// <summary>自定义模式是否对指定 vault 菜单生效（模式开关 ∧ 含 vault 选项）。</summary>
        internal static bool IsCustomVaultMenuActive(FloatMenuMap menu)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null || settings.rightClickMenuMode != RightClickMenuMode.CustomList)
            {
                return false;
            }
            return Patch_FloatMenuMap_DoWindowContents_VaultSlowRefresh.IsVaultMenu(menu);
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
                PinyinCache.Clear();
            }

            float rowH = state.Tiny ? RowHeightTiny : RowHeightNormal;
            float x = inRect.x + Margin;
            float y = inRect.y + Margin;
            float w = inRect.width - Margin * 2f;

            // ── 标题（原版 title，如 pawn.LabelCap） ──
            if (!string.IsNullOrEmpty(state.Title))
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(x, y, w, TitleHeight), state.Title);
                Text.Font = GameFont.Small;
                y += TitleHeight + Gap;
            }

            // ── 搜索框 ──
            Rect searchRect = new Rect(x, y, w, SearchBoxHeight);
            string edited = Widgets.TextField(searchRect, state.SearchBuffer);
            if (edited != state.SearchBuffer)
            {
                state.SearchBuffer = edited;
                state.SearchText = edited.Trim().ToLowerInvariant();
                RebuildFiltered(options, state);
            }
            y += SearchBoxHeight + Gap;

            // ── 计数 ──
            int shown = state.Filtered != null ? state.Filtered.Count : options.Count;
            GUI.color = Color.gray;
            Widgets.Label(
                new Rect(x, y, w, CountLabelHeight),
                "OuterrealmFloatMenu_Count".Translate(shown, options.Count));
            GUI.color = Color.white;
            y += CountLabelHeight + Gap;

            // ── 虚拟化列表（视口裁剪） ──
            Rect listRect = new Rect(x, y, w, Mathf.Max(1f, inRect.yMax - Margin - y));
            List<FloatMenuOption> visible = state.Filtered != null ? state.Filtered : options;
            if (visible.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(listRect, "OuterrealmFloatMenu_NoMatch".Translate());
                GUI.color = Color.white;
                return;
            }
            float contentH = visible.Count * rowH;
            Widgets.BeginScrollView(listRect, ref state.ScrollPosition, new Rect(0f, 0f, listRect.width, contentH));
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
                Rect row = new Rect(0f, i * rowH - scrollY, listRect.width, rowH);
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
        /// Disabled 项灰度显示，点击仅播 ClickReject（与原版 Chosen 行为一致）。
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
                GUI.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            }
            // 图标（优先 iconThing；无则灰占位）
            Rect iconRect = new Rect(row.x + 4f, row.y + (row.height - iconSize) / 2f, iconSize, iconSize);
            if (opt.iconThing != null)
            {
                Widgets.ThingIcon(iconRect, opt.iconThing);
            }
            else
            {
                GUI.DrawTexture(iconRect, BaseContent.GreyTex);
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

        /// <summary>搜索词变化时重建过滤列表（O(N) 一次；空搜索 = 全部，保持原排序）。</summary>
        private static void RebuildFiltered(List<FloatMenuOption> options, CustomMenuState state)
        {
            if (state.SearchText.Length == 0)
            {
                state.Filtered = null;
                state.ScrollPosition.y = 0f;
                return;
            }
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption opt = options[i];
                if (opt != null && Matches(opt.Label, state.SearchText))
                {
                    list.Add(opt);
                }
            }
            state.Filtered = list;
            state.ScrollPosition.y = 0f;
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
