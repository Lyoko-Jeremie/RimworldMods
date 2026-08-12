using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 主人心情：拥有专属侍奉女仆（+8）。
    /// 由 ThoughtDef AM_Thought_HasServant 引用。
    /// </summary>
    public class ThoughtWorker_HasServant : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
            return mgr == null || !mgr.IsMaster(p) ? ThoughtState.Inactive : ThoughtState.ActiveAtStage(0);
        }
    }
}
