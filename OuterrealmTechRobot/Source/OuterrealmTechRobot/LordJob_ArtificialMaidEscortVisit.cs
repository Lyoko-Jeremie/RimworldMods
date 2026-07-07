using RimWorld;
using Verse;
using Verse.AI.Group;

namespace OuterrealmTechRobot
{
    public class LordJob_ArtificialMaidEscortVisit : LordJob_VisitColony
    {
        private Pawn leader;

        public LordJob_ArtificialMaidEscortVisit()
        {
        }

        public LordJob_ArtificialMaidEscortVisit(Faction faction, IntVec3 chillSpot, Pawn leader)
            : base(faction, chillSpot)
        {
            this.leader = leader;
        }

        public bool IsLeader(Pawn pawn)
        {
            return pawn != null && pawn == leader;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref leader, "leader");
        }
    }
}
