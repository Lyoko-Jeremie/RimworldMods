using System;
using AutoBlink;
using Verse;

namespace OuterrealmTechRobot.AutoBlink17
{
    /// <summary>
    /// 1.7 AutoBlink 观察者：携带同步。
    /// 1.7 的 AutoBlink 只移动携带者本身，被携带的 Pawn（如倒地的主人）不会跟随；
    /// RimWorldMovementAdapter.Teleport 在 finally 中通过 AutoBlinkApi.NotifyBlinked 回调 observer，
    /// 此时 context.Destination 就是携带者的最终位置，据此把被携带 Pawn 一并同步过去。
    /// 重要：observer 内绝不能抛异常，否则 AutoBlink 会把本 observer 标记 fault 并禁用（直到重载）。
    /// </summary>
    public sealed class AutoBlinkCarrySyncObserver : IAutoBlinkObserver
    {
        /// <summary>WarningOnce 的 key：同一问题只记录一次，避免每次 blink 都刷屏。</summary>
        private const int WarningKeyCarrySyncFailed = 85321011;

        /// <summary>AutoBlink 瞬移成功后的回调。</summary>
        public void OnBlinked(in BlinkEffectContext context)
        {
            try
            {
                Pawn carrier = context.Pawn;
                if (carrier == null || carrier.carryTracker == null)
                {
                    return;
                }

                // 只处理被携带的是 Pawn 的情况；携带物品时由 ThingOwner 跟随，无需同步。
                if (!(carrier.carryTracker.CarriedThing is Pawn carried) || carried.Destroyed)
                {
                    return;
                }

                carried.Position = context.Destination;
                // 与 1.6 参考实现一致：不中断被携带者的当前 job，但刷新其寻路/绘制位置。
                carried.Notify_Teleported(false);
            }
            catch (Exception ex)
            {
                // 捕获所有异常（包括第三方 mod 对 Notify_Teleported 的 Postfix 抛错），
                // 位置能同步多少算多少，绝不把异常抛回 AutoBlink（否则 observer 会被禁用）。
                Log.WarningOnce(
                    "[OuterrealmTechRobot] 同步被携带 Pawn 到 AutoBlink 目的地失败（已忽略，避免禁用 observer）: " + ex,
                    WarningKeyCarrySyncFailed);
            }
        }
    }
}
