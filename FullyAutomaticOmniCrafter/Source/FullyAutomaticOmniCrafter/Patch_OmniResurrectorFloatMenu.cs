using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能重生平台的右键快捷菜单。
    /// 先选中一个（尚未死亡的）Pawn，再右键点击万能重生平台建筑，
    /// 浮动菜单中会出现 [登记/取消登记] 操作，并直接显示该 Pawn 当前的登记状态，
    /// 无需打开操作界面即可完成登记，方便提前预约保护。
    /// 本类继承原版 FloatMenuOptionProvider，由 FloatMenuMakerMap.Init()
    /// 通过反射自动发现并注册，无需额外 Harmony Patch。
    /// </summary>
    public class FloatMenuOptionProvider_OmniResurrectorRegister : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => true;

        /// <summary>仅当右键点击的目标中包含万能重生平台建筑时生效。</summary>
        protected override bool AppliesInt(FloatMenuContext context)
        {
            foreach (Thing t in context.ClickedThings)
            {
                if (t.TryGetComp<CompOmniResurrector>() != null)
                {
                    return true;
                }
            }
            return false;
        }

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
        {
            if (clickedThing.TryGetComp<CompOmniResurrector>() == null)
            {
                yield break;
            }
            GameComponent_OmniResurrector mgr = GameComponent_OmniResurrector.Instance;
            foreach (Pawn pawn in context.ValidSelectedPawns)
            {
                // 仅对尚未死亡、未被回收的 Pawn 提供快捷登记/取消登记。
                if (pawn == null || pawn.Dead || pawn.Discarded)
                {
                    continue;
                }
                bool registered = mgr != null && mgr.Registered.Contains(pawn);
                string label = registered
                    ? "OmniResurrector_ContextUnregister".Translate(pawn.LabelCap)
                    : "OmniResurrector_ContextRegister".Translate(pawn.LabelCap);
                yield return new FloatMenuOption(label, () =>
                {
                    if (mgr == null)
                    {
                        return;
                    }
                    if (registered)
                    {
                        mgr.Unregister(pawn);
                        Messages.Message(
                            "OmniResurrector_ContextUnregistered".Translate(pawn.LabelCap),
                            MessageTypeDefOf.NeutralEvent);
                    }
                    else
                    {
                        mgr.Register(pawn);
                        Messages.Message(
                            "OmniResurrector_ContextRegistered".Translate(pawn.LabelCap),
                            MessageTypeDefOf.NeutralEvent);
                    }
                }, MenuOptionPriority.High);
            }
        }
    }
}
