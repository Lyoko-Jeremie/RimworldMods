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
            var harmony = new Harmony("Jeremie.Outerrealm.Tech.ArtificialMaid");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("ArtificialMaidMod initialized.");
        }
    }

    [StaticConstructorOnStartup]
    public static class ArtificialMaidTex
    {
        public static readonly Texture2D IconModifyMaid =
            ContentFinder<Texture2D>.Get("UI/Commands/ModifyMaid", false) ?? BaseContent.WhiteTex; 
        // BaseContent.BadTex
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
                this.AutoConvertFaction();
                this.EnsureCoreGene();
            }

            if (this.parent.IsHashIntervalTick(250))
            {
                this.ApplyEmotionalSupport();
            }
        }

        private void ApplyEmotionalSupport()
        {
            if (Pawn == null || Pawn.Dead || !Pawn.Spawned) return;

            // 检查是否有“情感同步”特性
            if (Pawn.story?.traits != null)
            {
                var traitDef = TraitDef.Named("ArtificialMaidTrait_EmotionalSynchrony");
                if (Pawn.story.traits.HasTrait(traitDef))
                {
                    // 获取周围 10 格内的 Pawn
                    float radius = 10f;
                    foreach (var thing in GenRadial.RadialDistinctThingsAround(Pawn.Position, Pawn.Map, radius, true))
                    {
                        if (thing is Pawn other && other != Pawn && other.RaceProps.Humanlike &&
                            other.Faction == Pawn.Faction)
                        {
                            other.needs?.mood?.thoughts?.memories?.TryGainMemory(
                                ThoughtDef.Named("MaidEmotionalSupport"), Pawn);
                        }
                    }
                }
            }
        }

        public void EnsureCoreGene()
        {
            if (!ModsConfig.BiotechActive || Pawn == null || Pawn.genes == null) return;
            var geneDef = DefDatabase<GeneDef>.GetNamed("ArtificialMaid_Core");
            if (!Pawn.genes.HasGene(geneDef))
            {
                Pawn.genes.AddGene(geneDef, false);
            }
        }

        public void EnsureRecoveryHediff()
        {
            if (Pawn == null || Pawn.Dead) return;
            var def = HediffDef.Named("ArtificialMaidRecovery");
            if (!Pawn.health.hediffSet.HasHediff(def))
            {
                Pawn.health.AddHediff(def);
            }
        }

        public void FullRepair()
        {
            if (Pawn == null) return;

            // 1. 修复所有损伤和缺失
            // 先恢复所有缺失部位
            var missingParts = Pawn.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
            foreach (var part in missingParts)
            {
                Pawn.health.RestorePart(part.Part);
            }

            // 移除所有坏的 Hediff
            List<Hediff> toRemove = new List<Hediff>();
            foreach (var hediff in Pawn.health.hediffSet.hediffs)
            {
                if (hediff is Hediff_Injury || hediff.def.isBad ||
                    hediff.def.IsAddiction || hediff.def.chronic)
                {
                    toRemove.Add(hediff);
                }
            }
            foreach (var hediff in toRemove)
            {
                Pawn.health.RemoveHediff(hediff);
            }

            // 2. 去除不属于 ArtificialMaid 的特质
            if (Pawn.story?.traits != null)
            {
                var traitsToRemove = Pawn.story.traits.allTraits
                    .Where(t => !t.def.defName.StartsWith("ArtificialMaidTrait_"))
                    .ToList();
                foreach (var trait in traitsToRemove)
                {
                    Pawn.story.traits.RemoveTrait(trait);
                }
            }

            // 3. 替换不属于 ArtificialMaid 的背景故事
            if (Pawn.story != null)
            {
                if (Pawn.story.Childhood != null && !Pawn.story.Childhood.spawnCategories.Contains("ArtificialMaidBackstory"))
                {
                    Pawn.story.Childhood = DefDatabase<BackstoryDef>.AllDefs
                        .FirstOrDefault(b => b.slot == BackstorySlot.Childhood && b.spawnCategories.Contains("ArtificialMaidBackstory"));
                }
                if (Pawn.story.Adulthood != null && !Pawn.story.Adulthood.spawnCategories.Contains("ArtificialMaidBackstory"))
                {
                    Pawn.story.Adulthood = DefDatabase<BackstoryDef>.AllDefs
                        .FirstOrDefault(b => b.slot == BackstorySlot.Adulthood && b.spawnCategories.Contains("ArtificialMaidBackstory"));
                }
            }

            // 4. 修正需求系统和技能系统
            if (Pawn.needs != null)
            {
                Pawn.needs.AddOrRemoveNeedsAsAppropriate();
            }
            this.ReplenishResources();
        }

        public void ReplenishResources()
        {
            if (Pawn == null) return;

            // 清除文化
            if (ModsConfig.IdeologyActive && Pawn.ideo != null && Pawn.ideo.Ideo != null)
            {
                Pawn.ideo.SetIdeo(null);
            }

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

            // 保持技能不低于 99 且双火
            if (Pawn.skills != null)
            {
                foreach (var skill in Pawn.skills.skills)
                {
                    if (skill.Level < 99) skill.Level = 99;
                    if (skill.passion != Passion.Major) skill.passion = Passion.Major;
                }
            }
        }

        private void AutoConvertFaction()
        {
            if (Pawn == null || Pawn.Dead || !Pawn.Spawned) return;
            if (Pawn.Faction != Faction.OfPlayer)
            {
                Pawn.SetFaction(Faction.OfPlayer);
                this.FullRepair();
                this.EnsureRecoveryHediff();

                string label = "ArtificialMaidRecruitedLabel".Translate(Pawn.LabelShort);
                string text = "ArtificialMaidRecruitedText".Translate(Pawn.LabelShort);
                Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, Pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "ShouldHaveIdeo", MethodType.Getter)]
    public static class Patch_Pawn_ShouldHaveIdeo
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__instance.def.defName == "ArtificialMaid")
            {
                __result = false;
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
                Find.LetterStack.ReceiveLetter("ArtificialMaid_DeathLetter_Label".Translate(),
                    "ArtificialMaid_DeathLetter_Text".Translate(__instance.LabelShort), LetterDefOf.Death, __instance);
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
                Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionLetter_Label".Translate(),
                    "ArtificialMaid_ResurrectionLetter_Text".Translate(pawn.LabelShort), LetterDefOf.PositiveEvent,
                    pawn);
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
                    Messages.Message("ArtificialMaid_RepairMessage".Translate(pawn.LabelShort), pawn,
                        MessageTypeDefOf.PositiveEvent);
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
                Messages.Message("ArtificialMaid_NotEnoughPower".Translate(PowerRequired),
                    MessageTypeDefOf.RejectInput);
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
                DefDatabase<TraitDef>.AllDefs.Where(t => !t.defName.StartsWith("ArtificialMaidTrait_"))
                    .ToList(), // prohibitedTraits (禁止特质)
                null, // minChanceToRedressWorldPawn (重新打扮世界Pawn的最小概率)
                null, // fixedBiologicalAge (固定生理年龄)
                null, // fixedChronologicalAge (固定实际年龄)
                Gender.Female
            );
            Pawn pawn = PawnGenerator.GeneratePawn(request);

            // 强制背景限制 (虽然XML已有过滤，但这里做最终确保)
            if (pawn.story != null)
            {
                if (pawn.story.Childhood != null &&
                    !pawn.story.Childhood.spawnCategories.Contains("ArtificialMaidBackstory"))
                {
                    pawn.story.Childhood = DefDatabase<BackstoryDef>.AllDefs
                        .Where(b => b.slot == BackstorySlot.Childhood &&
                                    b.spawnCategories.Contains("ArtificialMaidBackstory"))
                        .RandomElement();
                }

                if (pawn.story.Adulthood != null &&
                    !pawn.story.Adulthood.spawnCategories.Contains("ArtificialMaidBackstory"))
                {
                    pawn.story.Adulthood = DefDatabase<BackstoryDef>.AllDefs
                        .Where(b => b.slot == BackstorySlot.Adulthood &&
                                    b.spawnCategories.Contains("ArtificialMaidBackstory"))
                        .RandomElement();
                }
            }

            // 强制特质限制
            if (pawn.story?.traits != null)
            {
                // 移除所有不符合要求的特质
                var allTraits = pawn.story.traits.allTraits.ToList();
                foreach (var trait in allTraits)
                {
                    if (!trait.def.defName.StartsWith("ArtificialMaidTrait_"))
                    {
                        pawn.story.traits.RemoveTrait(trait);
                    }
                }

                // 如果没有任何特质，随机给一个符合要求的
                if (pawn.story.traits.allTraits.Count == 0)
                {
                    var maidTraitDef = DefDatabase<TraitDef>.AllDefs
                        .Where(t => t.defName.StartsWith("ArtificialMaidTrait_"))
                        .RandomElementByWeight(t => t.GetGenderSpecificCommonality(pawn.gender));
                    if (maidTraitDef != null)
                    {
                        pawn.story.traits.GainTrait(new Trait(maidTraitDef));
                    }
                }
            }

            // 清理所有初始状态，确保刚制造出来时是完美状态
            pawn.health.Reset();

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

            Messages.Message("ArtificialMaidFabricated".Translate(pawn.LabelShort), pawn,
                MessageTypeDefOf.PositiveEvent);
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

    public class CompProperties_ArtificialMaidTerminal : CompProperties
    {
        public CompProperties_ArtificialMaidTerminal()
        {
            this.compClass = typeof(CompArtificialMaidTerminal);
        }
    }

    public class CompArtificialMaidTerminal : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra()) yield return g;

            yield return new Command_Action
            {
                defaultLabel = "ModifyArtificialMaidLabel".Translate(),
                defaultDesc = "ModifyArtificialMaidDesc".Translate(),
                icon = ArtificialMaidTex.IconModifyMaid,
                action = delegate
                {
                    List<FloatMenuOption> list = new List<FloatMenuOption>();
                    foreach (Pawn pawn in this.parent.Map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn.def.defName == "ArtificialMaid")
                        {
                            Pawn localPawn = pawn;
                            list.Add(new FloatMenuOption(localPawn.LabelCap,
                                delegate { OpenModificationMenu(localPawn); }));
                        }
                    }

                    if (list.Count == 0)
                    {
                        list.Add(new FloatMenuOption("NoArtificialMaidFound".Translate(), null));
                    }

                    Find.WindowStack.Add(new FloatMenu(list));
                }
            };
        }

        private void OpenModificationMenu(Pawn pawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();

            list.Add(new FloatMenuOption("ModifyChildhoodLabel".Translate(),
                delegate { OpenBackstoryMenu(pawn, BackstorySlot.Childhood); }));

            list.Add(new FloatMenuOption("ModifyAdulthoodLabel".Translate(),
                delegate { OpenBackstoryMenu(pawn, BackstorySlot.Adulthood); }));

            list.Add(new FloatMenuOption("ModifyTraitsLabel".Translate(), delegate { OpenTraitMenu(pawn); }));

            list.Add(new FloatMenuOption("AutofixReplenishLabel".Translate(), delegate
            {
                var comp = pawn.TryGetComp<CompArtificialMaid>();
                if (comp != null)
                {
                    comp.FullRepair();
                    comp.EnsureRecoveryHediff();
                    Messages.Message("ArtificialMaidFixedMessage".Translate(pawn.LabelShort),
                        MessageTypeDefOf.PositiveEvent);
                }
            }));

            Find.WindowStack.Add(new FloatMenu(list));
        }

        private void OpenBackstoryMenu(Pawn pawn, BackstorySlot slot)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            var backstories = DefDatabase<BackstoryDef>.AllDefs
                .Where(b => b.slot == slot && b.spawnCategories.Contains("ArtificialMaidBackstory"));

            foreach (var bs in backstories)
            {
                list.Add(new FloatMenuOption(bs.title, delegate
                {
                    if (slot == BackstorySlot.Childhood) pawn.story.Childhood = bs;
                    else pawn.story.Adulthood = bs;
                    Messages.Message(
                        "ArtificialMaidBackstoryUpdated".Translate(pawn.LabelShort, slot.ToString(), bs.title),
                        MessageTypeDefOf.PositiveEvent);
                }));
            }

            Find.WindowStack.Add(new FloatMenu(list));
        }

        private void OpenTraitMenu(Pawn pawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            var traits = DefDatabase<TraitDef>.AllDefs
                .Where(t => t.defName.StartsWith("ArtificialMaidTrait_"));

            foreach (var trait in traits)
            {
                list.Add(new FloatMenuOption(trait.degreeDatas[0].label, delegate
                {
                    if (pawn.story.traits.HasTrait(trait))
                    {
                        Messages.Message("ArtificialMaidAlreadyHasTrait".Translate(pawn.LabelShort),
                            MessageTypeDefOf.RejectInput);
                        return;
                    }

                    pawn.story.traits.GainTrait(new Trait(trait));
                    Messages.Message("ArtificialMaidTraitAdded".Translate(trait.degreeDatas[0].label, pawn.LabelShort),
                        MessageTypeDefOf.PositiveEvent);
                }));
            }

            list.Add(new FloatMenuOption("ClearArtificialMaidTraitsLabel".Translate(), delegate
            {
                var toRemove = pawn.story.traits.allTraits.Where(t => t.def.defName.StartsWith("ArtificialMaidTrait_"))
                    .ToList();
                foreach (var t in toRemove) pawn.story.traits.RemoveTrait(t);
                Messages.Message("ArtificialMaidTraitsCleared".Translate(pawn.LabelShort),
                    MessageTypeDefOf.PositiveEvent);
            }));

            Find.WindowStack.Add(new FloatMenu(list));
        }
    }
    [HarmonyPatch(typeof(Pawn_GeneTracker), "AddGene", new System.Type[] { typeof(GeneDef), typeof(bool) })]
    public static class Patch_Pawn_GeneTracker_AddGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, GeneDef geneDef)
        {
            if (geneDef.defName == "ArtificialMaid_Core")
            {
                if (__instance.pawn?.def?.defName != "ArtificialMaid")
                {
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "RemoveGene")]
    public static class Patch_Pawn_GeneTracker_RemoveGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, Gene gene)
        {
            if (gene.def.defName == "ArtificialMaid_Core")
            {
                if (__instance.pawn?.def?.defName == "ArtificialMaid")
                {
                    return false;
                }
            }
            return true;
        }
    }
}