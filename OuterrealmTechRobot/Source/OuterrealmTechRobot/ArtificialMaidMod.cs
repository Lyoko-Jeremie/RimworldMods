using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    [StaticConstructorOnStartup]
    public static class ArtificialMaidMod
    {
        static ArtificialMaidMod()
        {
            var harmony = new Harmony("Jeremie.Outerrealm.Tech.Robot");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("ArtificialMaidMod initialized.");
        }
    }

    public class CompProperties_ArtificialMaid : CompProperties
    {
        public CompProperties_ArtificialMaid()
        {
            this.compClass = typeof(CompArtificialMaid);
        }
    }

    public class CompArtificialMaid : ThingComp
    {
        private Pawn Pawn => (Pawn)this.parent;

        public override void CompTick()
        {
            base.CompTick();
            if (this.parent.IsHashIntervalTick(60))
            {
                this.ReplenishResources();
                this.EnsureRecoveryHediff();
            }
        }

        private void EnsureRecoveryHediff()
        {
            if (Pawn == null || Pawn.Dead) return;
            var def = HediffDef.Named("ArtificialMaidRecovery");
            if (!Pawn.health.hediffSet.HasHediff(def))
            {
                Pawn.health.AddHediff(def);
            }
        }

        private void ReplenishResources()
        {
            if (Pawn == null) return;

            // 保持各种需求最高
            if (Pawn.needs != null)
            {
                if (Pawn.needs.mood != null) Pawn.needs.mood.CurLevel = 1.0f;
                if (Pawn.needs.rest != null) Pawn.needs.rest.CurLevel = 1.0f;
                if (Pawn.needs.joy != null) Pawn.needs.joy.CurLevel = 1.0f;
                if (Pawn.needs.beauty != null) Pawn.needs.beauty.CurLevel = 1.0f;
                if (Pawn.needs.comfort != null) Pawn.needs.comfort.CurLevel = 1.0f;
                if (Pawn.needs.outdoors != null) Pawn.needs.outdoors.CurLevel = 1.0f;
            }

            // 血源质保持最高
            if (ModsConfig.BiotechActive && Pawn.genes != null)
            {
                foreach (var gene in Pawn.genes.GenesListForReading)
                {
                    if (gene is Gene_Hemogen hemogen)
                    {
                        hemogen.Value = hemogen.Max;
                        break;
                    }
                }
            }
            
            // 精神熵消除
            if (ModsConfig.RoyaltyActive && Pawn.psychicEntropy != null)
            {
                Pawn.psychicEntropy.RemoveAllEntropy();
                Pawn.psychicEntropy.RechargePsyfocus();
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "PreApplyDamage")]
    public static class Patch_Pawn_PreApplyDamage
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = false;
            if (__instance.def.defName == "ArtificialMaid")
            {
                absorbed = true;
                return false;
            }
            return true;
        }
    }

    // Method 1: Intercept Pawn.Kill
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            if (__instance.def.defName == "ArtificialMaid")
            {
                __instance.health.Reset();
                Find.LetterStack.ReceiveLetter("ArtificialMaid_DeathLetter_Label".Translate(), "ArtificialMaid_DeathLetter_Text".Translate(__instance.LabelShort), LetterDefOf.Death, __instance);
                return false;
            }
            return true;
        }
    }

    // Method 2: Automatic Resurrection Hediff Logic
    public class Hediff_ArtificialMaidRecovery : HediffWithComps
    {
        public override void PostTick()
        {
            base.PostTick();
            if (pawn.IsHashIntervalTick(250))
            {
                this.ManualTickRare();
            }
        }

        public void ManualTickRare()
        {
            if (pawn.Dead)
            {
                ResurrectionUtility.TryResurrect(pawn, new ResurrectionParams
                {
                    gettingScarsChance = 0f,
                    canKidnap = false,
                    canTimeoutOrFlee = false,
                    useAvoidGridSmart = true,
                    canSteal = false,
                    invisibleStun = false
                });
                pawn.health.Reset();
                Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionLetter_Label".Translate(), "ArtificialMaid_ResurrectionLetter_Text".Translate(pawn.LabelShort), LetterDefOf.PositiveEvent, pawn);
            }
            else
            {
                // 修复伤害和肢体缺失
                bool changed = false;

                // 恢复缺失的肢体
                var missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
                if (missingParts.Count > 0)
                {
                    foreach (var mp in missingParts)
                    {
                        pawn.health.RestorePart(mp.Part);
                    }
                    changed = true;
                }

                // 治愈所有伤口（包括永久性伤害）
                var hediffs = pawn.health.hediffSet.hediffs;
                for (int i = hediffs.Count - 1; i >= 0; i--)
                {
                    if (hediffs[i] is Hediff_Injury injury)
                    {
                        pawn.health.RemoveHediff(injury);
                        changed = true;
                    }
                }

                if (changed)
                {
                    pawn.health.Notify_HediffChanged(null);
                    Messages.Message("ArtificialMaid_RepairMessage".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.PositiveEvent);
                }
            }
        }

        public override bool ShouldRemove => false;
    }

    // Method 2 Supplemental: Patch Corpse to trigger recovery if dead
    [HarmonyPatch(typeof(Corpse), nameof(Corpse.TickRare))]
    public static class Patch_Corpse_TickRare
    {
        [HarmonyPostfix]
        public static void Postfix(Corpse __instance)
        {
            Pawn pawn = __instance.InnerPawn;
            if (pawn != null && pawn.def.defName == "ArtificialMaid")
            {
                var hediff = pawn.health.hediffSet.GetFirstHediff<Hediff_ArtificialMaidRecovery>();
                hediff?.ManualTickRare();
            }
        }
    }

    // Method 3: Strengthening - Patch CheckForStateChange to prevent death state
    [HarmonyPatch(typeof(Pawn_HealthTracker), "CheckForStateChange")]
    public static class Patch_Pawn_HealthTracker_CheckForStateChange
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_HealthTracker __instance, Pawn ___pawn)
        {
            if (___pawn != null && ___pawn.def.defName == "ArtificialMaid")
            {
                if (__instance.ShouldBeDead())
                {
                    __instance.Reset();
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), "CombinedDisabledWorkTags", MethodType.Getter)]
    public static class Patch_Pawn_CombinedDisabledWorkTags
    {
        public static void Postfix(Pawn __instance, ref WorkTags __result)
        {
            if (__instance.def.defName == "ArtificialMaid")
            {
                __result = WorkTags.None;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetDisabledWorkTypes))]
    public static class Patch_Pawn_GetDisabledWorkTypes
    {
        public static void Postfix(Pawn __instance, List<WorkTypeDef> __result)
        {
            if (__instance.def.defName == "ArtificialMaid")
            {
                __result.Clear();
            }
        }
    }

    public class RecipeWorker_FabricateArtificialMaid : RecipeWorker
    {
        public const float PowerRequired = 10000f;

        public override void Notify_IterationCompleted(Pawn billDoer, List<Thing> ingredients)
        {
            // 获取工作台
            Thing bench = billDoer.CurJob?.targetA.Thing;
            CompPowerTrader powerTrader = bench?.TryGetComp<CompPowerTrader>();
            PowerNet net = powerTrader?.PowerNet;

            if (net == null || net.CurrentStoredEnergy() < PowerRequired)
            {
                Messages.Message("ArtificialMaid_NotEnoughPower".Translate(PowerRequired), MessageTypeDefOf.RejectInput);
                return;
            }

            // 扣除电量
            ConsumePowerFromNet(net, PowerRequired);

            base.Notify_IterationCompleted(billDoer, ingredients);
            PawnGenerationRequest request = new PawnGenerationRequest(
                PawnKindDef.Named("ArtificialMaidKind"),
                billDoer.Faction,
                PawnGenerationContext.NonPlayer,
                null, // tile (地块)
                true, // forceGenerateNewPawn (强制生成新Pawn)
                false, // allowDead (允许死亡)
                false, // allowDowned (允许倒地)
                false, // canGeneratePawnRelations (可以生成人际关系)
                false, // mustBeCapableOfViolence (必须具备暴力能力)
                1f, // colonistRelationChanceFactor (殖民者关系概率因子)
                false, // forceAddFreeWarmLayerIfNeeded (需要时强制添加免费保暖层)
                false, // allowGay (允许同性恋)
                false, // allowPregnant (允许怀孕)
                false, // allowFood (允许食物)
                false, // allowAddictions (允许上瘾)
                false, // inhabitant (居住者)
                false, // certainlyBeenInCryptosleep (肯定曾在低温休眠中)
                false, // forceRedressWorldPawnIfFormerColonist (如果是前殖民者，强制重新打扮世界Pawn)
                false, // worldPawnFactionDoesntMatter (世界Pawn派系不重要)
                0f, // biocodeWeaponChance (生物编码武器概率)
                0f, // biocodeApparelChance (生物编码服饰概率)
                null, // extraPawnForExtraRelationChance (额外关系的额外Pawn)
                1f, // relationWithExtraPawnChanceFactor (与额外Pawn关系的概率因子)
                null, // validatorPreGear (装备前验证器)
                null, // validatorPostGear (装备后验证器)
                null, // forcedTraits (强制特质)
                null, // prohibitedTraits (禁止特质)
                null, // minChanceToRedressWorldPawn (重新打扮世界Pawn的最小概率)
                null, // fixedBiologicalAge (固定生理年龄)
                null, // fixedChronologicalAge (固定实际年龄)
                Gender.Female
            );
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            GenSpawn.Spawn(pawn, billDoer.Position, billDoer.Map);
            
            // 确保技能全满且双火
            if (pawn.skills != null)
            {
                foreach (var skill in pawn.skills.skills)
                {
                    skill.Level = 99;
                    skill.passion = Passion.Major;
                }
            }

            Messages.Message("ArtificialMaidFabricated".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.PositiveEvent);
        }

        private void ConsumePowerFromNet(PowerNet net, float amount)
        {
            if (net == null || amount <= 0) return;

            float totalStored = net.CurrentStoredEnergy();
            if (totalStored <= 0) return;

            var batteries = net.batteryComps;
            float actualToDraw = amount;
            if (actualToDraw > totalStored) actualToDraw = totalStored;

            foreach (var battery in batteries)
            {
                if (battery.StoredEnergy > 0)
                {
                    float proportion = battery.StoredEnergy / totalStored;
                    float drawAmount = actualToDraw * proportion;
                    battery.DrawPower(drawAmount);
                }
            }
        }
    }
}
