using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// AutoBlink 联动桥接接口。
    /// 主程序集不直接引用 AutoBlink.dll，而是由 1.6 / 1.7 各自的桥接程序集实现本接口，
    /// 从而避免同一份程序集被绑定到某一版本的 AutoBlink API（1.7 的 AutoBlink 已重写 API）。
    /// </summary>
    public interface IAutoBlinkBridge
    {
        /// <summary>
        /// 注册被动钩子（1.6 用 Harmony Postfix 挂 ExecuteBlink / BlinkToCellDirect；
        /// 1.7 用 AutoBlinkApi.RegisterObserver）。返回是否成功。
        /// </summary>
        bool Initialize(Harmony harmony);

        /// <summary>
        /// 尝试让 pawn 瞬移到 cell（包含 AutoBlink 自身的冷却/可达/成本判断）。
        /// 返回 true 表示 AutoBlink 已接受并执行了瞬移。
        /// </summary>
        bool TryBlinkToCell(Pawn pawn, IntVec3 cell);

        /// <summary>
        /// 查询该 pawn 的 AutoBlink 剩余冷却。
        /// 返回 false 表示该 pawn 没有 AutoBlink 能力（没有对应组件/来源）。
        /// </summary>
        bool TryGetCooldownRemaining(Pawn pawn, out int remainingTicks);
    }
}
