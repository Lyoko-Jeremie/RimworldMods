using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OuterrealmTechRobot
{
    public static class ArtificialMaidEscortUtility
    {
        public static bool CanDismissEscortLeader(Pawn leader)
        {
            if (leader == null || !leader.Spawned || leader.Dead || leader.Downed)
            {
                return false;
            }

            Lord lord = leader.GetLord();
            return lord?.LordJob is LordJob_ArtificialMaidEscortVisit escortJob && escortJob.IsLeader(leader);
        }

        public static bool TryDismissEscort(Pawn leader)
        {
            if (!CanDismissEscortLeader(leader))
            {
                return false;
            }

            Lord oldLord = leader.GetLord();
            Map map = leader.Map;
            Faction faction = oldLord.faction;
            List<Pawn> pawns = oldLord.ownedPawns
                .Where(p => p != null && !p.Dead && p.Spawned && p.Map == map)
                .ToList();

            if (pawns.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                oldLord.RemovePawn(pawns[i]);
            }

            map.lordManager.RemoveLord(oldLord);
            LordMaker.MakeNewLord(faction, new LordJob_ExitMapBest(LocomotionUrgency.Walk, true, true), map, pawns);
            Messages.Message("ArtificialMaidEscortDismissedMessage".Translate(faction.Name), pawns, MessageTypeDefOf.NeutralEvent);
            return true;
        }
    }
}
