using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 携带 autoblink 同步（核心联动）：
    /// AutoBlink 的两个最终执行点（ExecuteBlink / BlinkToCellDirect）只移动携带者本身，
    /// 被携带的 Pawn（如倒地的主人）不会跟着瞬移。本 Postfix 在两个执行点之后把
    /// carryTracker 中的被携带 Pawn 一并同步到目标格，实现"携带主人一起 blink"。
    /// 性能：两个方法仅在真正 blink 时调用（低频），Postfix 首行空判定即返回，每 tick 零开销。
    /// 兼容：csproj 已强引用 AutoBlink，直接访问 API；不触碰其冷却/排除/路径逻辑。
    /// </summary>
    [HarmonyPatch(typeof(AutoBlink.CompAutoBlink), "ExecuteBlink")]
    public static class Patch_AutoBlink_ExecuteBlink_CarrySync
    {
        [HarmonyPostfix]
        public static void Postfix(AutoBlink.CompAutoBlink __instance, IntVec3 target)
        {
            ArtificialMaidBlinkCarrySyncUtility.SyncCarriedPawn(__instance, target);
        }
    }

    /// <summary>手动/女仆主动 blink 的同步入口（BlinkToCellDirect，含 CompArtificialMaid.TryBlinkToTarget）。</summary>
    [HarmonyPatch(typeof(AutoBlink.CompAutoBlink), "BlinkToCellDirect")]
    public static class Patch_AutoBlink_BlinkToCellDirect_CarrySync
    {
        [HarmonyPostfix]
        public static void Postfix(AutoBlink.CompAutoBlink __instance, IntVec3 cell)
        {
            ArtificialMaidBlinkCarrySyncUtility.SyncCarriedPawn(__instance, cell);
        }
    }

    /// <summary>把携带者身上的被携带 Pawn 同步到目标格（携带同步核心逻辑）。</summary>
    internal static class ArtificialMaidBlinkCarrySyncUtility
    {
        public static void SyncCarriedPawn(AutoBlink.CompAutoBlink blinkComp, IntVec3 targetCell)
        {
            if (blinkComp == null || !(blinkComp.parent is Pawn carrier))
            {
                return;
            }

            if (carrier.carryTracker == null || !(carrier.carryTracker.CarriedThing is Pawn carried))
            {
                return; // 未携带 Pawn（携带物品时由 ThingOwner 跟随，无需同步）
            }

            if (carried.Destroyed)
            {
                return;
            }

            // 校验 blink 实际发生（BlinkToCellDirect 可能因冷却/不可达提前返回）：携带者到位才同步被携带者
            if (carrier.Position != targetCell)
            {
                return;
            }

            carried.Position = targetCell;
            try
            {
                carried.Notify_Teleported(false);
            }
            catch (Exception ex)
            {
                // 防御：Notify_Teleported 会被第三方 mod（如 WeatherApparelFramework）的 Postfix 挂载，
                // 其内部未判空时可能抛 NRE。位置已在上面同步完毕，异常不影响本次 blink 结果，
                // 仅记录一次日志便于排查，避免每次 blink 都刷屏。
                Log.WarningOnce("[OuterrealmTechRobot] 同步被携带者 " + carried.LabelShort +
                    " 的 Notify_Teleported 时被第三方 mod 的 Postfix 抛出异常，已忽略: " + ex, 85321001);
            }
        }
    }
}
