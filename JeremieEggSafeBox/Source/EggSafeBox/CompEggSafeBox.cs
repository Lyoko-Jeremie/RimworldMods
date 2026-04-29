
using RimWorld;
using System.Linq;
using Verse;

namespace EggSafeBox
{
    internal class CompEggSafeBox : ThingComp
    {
        private CompPowerTrader powerComp;

        public CompProperties_EggSafeBox Props
        {
            get => (CompProperties_EggSafeBox) this.props;
        }

        public Building_Storage Storage => (Building_Storage) this.parent;

        public Thing ContainedThing
        {
            get
            {
                return this.Storage.storageGroup.HeldThings == null ? (Thing) null : this.Storage.storageGroup.HeldThings.First<Thing>();
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            this.powerComp = this.parent.GetComp<CompPowerTrader>();
        }
    }
}
