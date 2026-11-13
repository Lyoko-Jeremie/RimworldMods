using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// 逐建筑开关专用的 <see cref="Command_Toggle"/>。
    ///
    /// 原版实现有两个会干扰「一排同类开关」的行为：
    /// - <c>InheritInteractionsFrom</c>：同一组里状态相同的 toggle 会互相转发点击；
    /// - <c>GroupsWith</c>：同类型 command 可能被并成一组，只画组里第一个。
    ///
    /// 我们的三个模式开关状态组合很多（全关、单开、多开），一旦被判成同组，
    /// 点击会落到别的实例上，表现出来就是「点了没反应」。这里直接关掉这两个行为。
    /// </summary>
    public sealed class ProxyHeatToggleCommand : Command_Toggle
    {
        public override bool InheritInteractionsFrom(Gizmo other)
        {
            return false;
        }

        public override bool GroupsWith(Gizmo other)
        {
            return false;
        }
    }
}
