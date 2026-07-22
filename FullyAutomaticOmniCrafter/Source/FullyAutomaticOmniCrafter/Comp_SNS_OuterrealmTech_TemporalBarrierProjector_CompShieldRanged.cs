using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Comp_SNS_OuterrealmTech_TemporalBarrierProjector_CompShieldRanged : CompShield
    {
        // 永远不会妨碍穿戴者攻击
        public override bool CompAllowVerbCast(Verb verb) => true;

        public override void PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            // 不受EMP攻击破盾
            if (dinfo.Def == DamageDefOf.EMP)
            {
                // 仅在护盾有效时吸收 EMP；重置或禁用期间仍允许 EMP 命中
                absorbed = ShieldState == ShieldState.Active && PawnOwner != null;
                // absorbed = false：护盾不会破，但 EMP 会继续作用于 Pawn，机械体等仍可能被击晕。
                // absorbed = true：有效护盾直接吃掉 EMP，护盾不掉能量，Pawn 也不会受到这次 EMP。
                if (absorbed)
                {
                    KeepDisplaying();
                }
                return;
            }
            base.PostPreApplyDamage(ref dinfo, out absorbed);
        }
        
    }
}
