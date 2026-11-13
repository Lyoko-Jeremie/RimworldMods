namespace Jeremie.AtmosphereOverhaulPatch.Compat.GoodClimate
{
    /// <summary>
    /// Good Climate 兼容模块的入口占位。
    ///
    /// 实际实现已拆到：
    /// - <c>GcAirRelay.cs</c>：反射桥接、AirMover 注册/注销、真空关断；
    /// - <c>GcPatches.cs</c>：放宽 IE 按钮门控、抑制 GC 房间级交换、StaticConstructorOnStartup 自举。
    ///
    /// 设计说明见仓库根目录的 <c>AO-主动气体转移装置设计分析.md</c> §11。
    /// </summary>
    internal static class GoodClimateCompatPlaceholder
    {
    }
}
