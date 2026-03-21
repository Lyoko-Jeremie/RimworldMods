using Verse;

namespace EggSafeBox
{
    internal class CompProperties_EggSafeBox : CompProperties
    {
        public bool frozenSafeBox = true;

        public CompProperties_EggSafeBox()
        {
            this.compClass = typeof(CompEggSafeBox);
        }
    }
}
