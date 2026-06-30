using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_AbsorbAllDamage : CompProperties
    {
        public CompProperties_AbsorbAllDamage()
        {
            compClass = typeof(CompAbsorbAllDamage);
        }
    }

    public class CompAbsorbAllDamage : ThingComp
    {
        public override void PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true;
        }
    }
}
