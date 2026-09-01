using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 两级菜单生成第二层操作时的线程局部目标限定作用域。
    /// 原版 FloatMenuContext 仍正常创建，但禁止枚举容器全部内容，并在 provider 入口把
    /// ClickedThings 精确替换为当前所选目标，使第三方 provider 仍走原版调用链。
    /// </summary>
    internal static class VaultTargetOnlyFloatMenuScope
    {
        [ThreadStatic]
        private static Thing currentTarget;
        [ThreadStatic]
        private static int depth;

        internal static bool Active => depth > 0 && currentTarget != null;
        internal static Thing CurrentTarget => Active ? currentTarget : null;

        internal static IDisposable Enter(Thing target)
        {
            Thing previousTarget = currentTarget;
            int previousDepth = depth;
            currentTarget = target;
            depth = previousDepth + 1;
            return new Scope(previousTarget, previousDepth);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Thing previousTarget;
            private readonly int previousDepth;
            private bool disposed;

            public Scope(Thing previousTarget, int previousDepth)
            {
                this.previousTarget = previousTarget;
                this.previousDepth = previousDepth;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                currentTarget = previousTarget;
                depth = previousDepth;
            }
        }
    }

    /// <summary>
    /// 两级自定义菜单必须在 Selector 调用 FloatMenuMakerMap.GetOptions 之前接管；否则全仓
    /// Provider/WorkGiver 扫描已经发生，无法改善第一次打开的卡顿。
    /// </summary>
    [HarmonyPatch(typeof(Selector), "HandleMapClicks")]
    internal static class Patch_Selector_HandleMapClicks_VaultItemThenOption
    {
        private static bool Prefix(Selector __instance)
        {
            Event current = Event.current;
            if (current == null || current.type != EventType.MouseDown || current.button != 1)
            {
                return true;
            }
            List<Pawn> selectedPawns = __instance.SelectedPawns;
            if (selectedPawns == null || selectedPawns.Count == 0)
            {
                return true;
            }
            Map map = Find.CurrentMap;
            Vector3 clickPos = UI.MouseMapPosition();
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (map == null || !cell.InBounds(map))
            {
                return true;
            }
            Building_OuterrealmVault vault = cell.GetEdifice(map) as Building_OuterrealmVault;
            if (vault == null
                || vault.RightClickMenuMode != RightClickMenuMode.CustomList
                || vault.CustomMenuMode != VaultCustomMenuMode.ItemThenOption)
            {
                return true;
            }

            Find.WindowStack.Add(new Dialog_OuterrealmVaultItemFloatMenu(
                vault, new List<Pawn>(selectedPawns), clickPos));
            current.Use();
            return false;
        }
    }

    /// <summary>目标限定期间阻止 GenUI.ThingsUnderMouse 展开 vault 的全部直接持有物。</summary>
    [HarmonyPatch(typeof(ContainingSelectionUtility), nameof(ContainingSelectionUtility.SelectableContainedThings))]
    internal static class Patch_ContainingSelectionUtility_SelectableContainedThings_TargetOnly
    {
        private static readonly IEnumerable<Thing> Empty = Array.Empty<Thing>();

        private static bool Prefix(ref IEnumerable<Thing> __result)
        {
            if (!VaultTargetOnlyFloatMenuScope.Active)
            {
                return true;
            }
            __result = Empty;
            return false;
        }
    }

    // ── 自制右键菜单（vault 大列表）patch 组：改窗口形态 / 接管绘制 / 恢复暂停 ──
    // 原版创建链（Selector.HandleMapClicks）不干预：FloatMenuMap 照常构造、照常 Add，
    // 仅自定义模式下改变其窗口表现。全部判定先查模式开关 ∧ 含 vault 选项（白名单），
    // 非 vault 菜单 / 原版模式零影响。
    // 注意：DoWindowContents 同时存在既有 Patch_FloatMenuMap_DoWindowContents_VaultSlowRefresh
    // （多模式降频）的 Prefix——其开头已对自定义模式短路（见该文件），绘制只由本组 Prefix 负责。

    /// <summary>打开时把菜单窗口改为大矩形，并关闭鼠标远离淡出、开启背景、锁相机、吸收外部输入。</summary>
    [HarmonyPatch(typeof(FloatMenu), "SetInitialSizeAndPosition")]
    internal static class Patch_FloatMenu_SetInitialSizeAndPosition_CustomMenu
    {
        private static void Postfix(FloatMenu __instance)
        {
            if (!CustomFloatMenuUtil.IsCustomVaultMenuActive(__instance))
            {
                return; // 原版模式 / 非 vault 菜单：保持原版形态
            }
            float w = UI.screenWidth * 0.7f;
            float h = UI.screenHeight * 0.7f;
            __instance.windowRect = new Rect(
                (UI.screenWidth - w) / 2f,
                (UI.screenHeight - h) / 2f,
                w,
                h);
            __instance.vanishIfMouseDistant = false;   // 禁用"鼠标远离菜单自动淡出/关闭"
            __instance.doWindowBackground = true;      // 大窗口需要背景
            __instance.preventCameraMotion = true;     // 锁相机（暂停中防误操作）
            __instance.absorbInputAroundWindow = true; // 吸收外部输入（暂停中防地图操作）
            __instance.forcePause = true;              // 打开期间强制暂停（原版机制：窗口在栈期间 Paused，关闭自动恢复，不改动 curTimeSpeed）
        }
    }

    /// <summary>完全接管 vault 菜单的绘制：搜索框 + 虚拟化列表，不调用原版绘制与重生成。</summary>
    [HarmonyPatch(typeof(FloatMenuMap), "DoWindowContents")]
    internal static class Patch_FloatMenuMap_DoWindowContents_CustomMenu
    {
        private static bool Prefix(FloatMenuMap __instance, Rect inRect)
        {
            if (!CustomFloatMenuUtil.IsCustomVaultMenuActive(__instance))
            {
                return true; // 原版模式 / 非 vault 菜单：走原版（含既有 vault 降频 patch）
            }
            if (!Find.Selector.AnyPawnSelected)
            {
                Find.WindowStack.TryRemove(__instance); // 与基类一致：无选中 pawn 时关闭菜单
                return false;
            }
            CustomFloatMenuUtil.Draw(__instance, inRect);
            return false; // 完全接管
        }
    }

    /// <summary>菜单关闭（点击选项 / Esc / 点击外部）后恢复打开前的暂停状态。</summary>
    [HarmonyPatch(typeof(FloatMenu), "PostClose")]
    internal static class Patch_FloatMenu_PostClose_CustomMenu
    {
        private static void Postfix(FloatMenu __instance)
        {
            if (__instance is FloatMenuMap map)
            {
                CustomFloatMenuUtil.NotifyClosed(map);
            }
        }
    }
}
