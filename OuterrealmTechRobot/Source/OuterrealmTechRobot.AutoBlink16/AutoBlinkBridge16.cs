using System;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot.AutoBlink16
{
    /// <summary>
    /// AutoBlink 1.6 桥接实现。
    /// 1.6 版 AutoBlink 的 CompAutoBlink 仍提供 BlinkToCellDirect / lastBlinkTick / Props，
    /// 因此这里直接强引用 1.6 的 AutoBlink.dll 完成主动瞬移与冷却查询；
    /// 1.7 版由 OuterrealmTechRobot.AutoBlink17 另行实现，主程序集通过 IAutoBlinkBridge 隔离两版差异。
    /// </summary>
    public sealed class AutoBlinkBridge16 : OuterrealmTechRobot.IAutoBlinkBridge
    {
        /// <summary>
        /// 注册本程序集内的 Harmony 补丁（携带同步的两个 Postfix）。
        /// 由主程序集在启动时传入全局 Harmony 实例调用，只对本程序集 PatchAll，不影响主程序集的补丁注册。
        /// </summary>
        public bool Initialize(Harmony harmony)
        {
            harmony.PatchAll(typeof(AutoBlinkBridge16).Assembly);
            return true;
        }

        /// <summary>
        /// 尝试让 pawn 瞬移到 cell。
        /// 先做与 AutoBlink 自身一致的冷却判定（blinkIntervalTicks / lastBlinkTick），
        /// 冷却中直接拒绝；否则调用 BlinkToCellDirect（内含可达性、碰撞、成本等完整判定），
        /// 以「位置是否真的改变」作为是否瞬移成功的判定。
        /// </summary>
        public bool TryBlinkToCell(Pawn pawn, IntVec3 cell)
        {
            if (pawn == null || pawn.Destroyed)
            {
                return false;
            }

            AutoBlink.CompAutoBlink comp = pawn.GetComp<AutoBlink.CompAutoBlink>();
            if (comp == null)
            {
                return false;
            }

            // 冷却判定：距离上次 blink 不足间隔时不做无谓调用，避免 BlinkToCellDirect 内部提前返回。
            int ticksGame = Find.TickManager.TicksGame;
            int interval = comp.Props.blinkIntervalTicks;
            if (interval > 0 && ticksGame - comp.lastBlinkTick < interval)
            {
                return false;
            }

            IntVec3 oldPos = pawn.Position;
            comp.BlinkToCellDirect(cell);
            return pawn.Position != oldPos;
        }

        /// <summary>
        /// 查询 pawn 的 AutoBlink 剩余冷却。没有 CompAutoBlink（即没有 AutoBlink 能力）时返回 false。
        /// </summary>
        public bool TryGetCooldownRemaining(Pawn pawn, out int remainingTicks)
        {
            remainingTicks = 0;
            if (pawn == null || pawn.Destroyed)
            {
                return false;
            }

            AutoBlink.CompAutoBlink comp = pawn.GetComp<AutoBlink.CompAutoBlink>();
            if (comp == null)
            {
                return false;
            }

            remainingTicks = Math.Max(0,
                comp.Props.blinkIntervalTicks - (Find.TickManager.TicksGame - comp.lastBlinkTick));
            return true;
        }
    }
}
