using System.Collections.Generic;
using Verse;

namespace OuterrealmTechRobot
{
    public class ArtificialMaidMapComponent : MapComponent
    {
        private HashSet<Pawn> registeredMaids = new HashSet<Pawn>();

        public int MaidCount => registeredMaids.Count;

        public ArtificialMaidMapComponent(Map map) : base(map)
        {
        }

        public void RegisterMaid(Pawn pawn)
        {
            if (pawn != null && !registeredMaids.Contains(pawn))
            {
                registeredMaids.Add(pawn);
            }
        }

        public void UnregisterMaid(Pawn pawn)
        {
            if (pawn != null && registeredMaids.Contains(pawn))
            {
                registeredMaids.Remove(pawn);
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            registeredMaids.Clear();
        }
    }
}
