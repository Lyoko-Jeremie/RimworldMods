using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using System;
using Verse;

namespace OuterrealmTechRobot
{
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

            base.Notify_IterationCompleted(billDoer, ingredients);
            List<TraitDef> prohibitedTraits = new List<TraitDef>();
            foreach (var t in DefDatabase<TraitDef>.AllDefs)
            {
                if (t.defName != null && !t.defName.StartsWith("ArtificialMaidTrait_"))
                {
                    prohibitedTraits.Add(t);
                }
            }

            Pawn pawn = null;
            try
            {
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
                prohibitedTraits, // prohibitedTraits (禁止特质)
                null, // minChanceToRedressWorldPawn (重新打扮世界Pawn的最小概率)
                null, // fixedBiologicalAge (固定生理年龄)
                null, // fixedChronologicalAge (固定实际年龄)
                Gender.Female
            );
            pawn = PawnGenerator.GeneratePawn(request);

            // 强制背景限制 (虽然XML已有过滤，但这里做最终确保)
            if (pawn.story != null)
            {
                if (pawn.story.Childhood != null)
                {
                    bool isMaidBackstory = false;
                    if (pawn.story.Childhood.spawnCategories != null)
                    {
                        for (int i = 0; i < pawn.story.Childhood.spawnCategories.Count; i++)
                        {
                            if (pawn.story.Childhood.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                isMaidBackstory = true;
                                break;
                            }
                        }
                    }

                    if (!isMaidBackstory)
                    {
                        List<BackstoryDef> possibleChildhoods = new List<BackstoryDef>();
                        foreach (var b in DefDatabase<BackstoryDef>.AllDefs)
                        {
                            if (b.slot == BackstorySlot.Childhood && b.spawnCategories != null)
                            {
                                for (int i = 0; i < b.spawnCategories.Count; i++)
                                {
                                    if (b.spawnCategories[i] == "ArtificialMaidBackstory")
                                    {
                                        possibleChildhoods.Add(b);
                                        break;
                                    }
                                }
                            }
                        }

                        if (possibleChildhoods.Count > 0)
                        {
                            pawn.story.Childhood = possibleChildhoods.RandomElement();
                        }
                    }
                }

                if (pawn.story.Adulthood != null)
                {
                    bool isMaidBackstory = false;
                    if (pawn.story.Adulthood.spawnCategories != null)
                    {
                        for (int i = 0; i < pawn.story.Adulthood.spawnCategories.Count; i++)
                        {
                            if (pawn.story.Adulthood.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                isMaidBackstory = true;
                                break;
                            }
                        }
                    }

                    if (!isMaidBackstory)
                    {
                        List<BackstoryDef> possibleAdulthoods = new List<BackstoryDef>();
                        foreach (var b in DefDatabase<BackstoryDef>.AllDefs)
                        {
                            if (b.slot == BackstorySlot.Adulthood && b.spawnCategories != null)
                            {
                                for (int i = 0; i < b.spawnCategories.Count; i++)
                                {
                                    if (b.spawnCategories[i] == "ArtificialMaidBackstory")
                                    {
                                        possibleAdulthoods.Add(b);
                                        break;
                                    }
                                }
                            }
                        }

                        if (possibleAdulthoods.Count > 0)
                        {
                            pawn.story.Adulthood = possibleAdulthoods.RandomElement();
                        }
                    }
                }
            }

            // 强制特质限制
            if (pawn.story?.traits != null)
            {
                // 移除所有不符合要求的特质
                var allTraits = pawn.story.traits.allTraits;
                for (int i = allTraits.Count - 1; i >= 0; i--)
                {
                    var trait = allTraits[i];
                    if (trait.def != null && !trait.def.defName.StartsWith("ArtificialMaidTrait_"))
                    {
                        pawn.story.traits.RemoveTrait(trait);
                    }
                }

                // 确保有“情感同步”特性 (ArtificialMaidTrait_EmotionalSynchrony)
                if (!pawn.story.traits.HasTrait(ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony))
                {
                    pawn.story.traits.GainTrait(new Trait(ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony));
                }

                // 如果除了情感同步之外没有其他特质，随机再给一个符合要求的特质
                if (pawn.story.traits.allTraits.Count <= 1)
                {
                    List<TraitDef> possibleTraits = new List<TraitDef>();
                    foreach (var t in DefDatabase<TraitDef>.AllDefs)
                    {
                        if (t.defName != null && t.defName.StartsWith("ArtificialMaidTrait_") &&
                            t != ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony)
                        {
                            possibleTraits.Add(t);
                        }
                    }

                    if (possibleTraits.Count > 0)
                    {
                        TraitDef maidTraitDef =
                            possibleTraits.RandomElementByWeightWithDefault(
                                t => t.GetGenderSpecificCommonality(pawn.gender), 0f);
                        if (maidTraitDef != null)
                        {
                            pawn.story.traits.GainTrait(new Trait(maidTraitDef));
                        }
                    }
                }
            }

            // 清理所有初始状态，确保刚制造出来时是完美状态
            pawn.health.Reset();

            // 补上机械师能力 (Biotech)
            if (ModsConfig.BiotechActive && pawn.health != null)
            {
                if (!pawn.health.hediffSet.HasHediff(HediffDefOf.MechlinkImplant))
                {
                    pawn.health.AddHediff(HediffDefOf.MechlinkImplant, pawn.health.hediffSet.GetBrain());
                    PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
                }
            }

            // RJW 支持：添加性器官
            RJWCompatibility.InitializeMaidOrgans(pawn);

            bool spawned = false;
            try
            {
                spawned = ArtificialMaidTransferUtility.TrySpawnNear(
                    pawn, billDoer.Map, billDoer.Position, out _);
            }
            catch (Exception ex)
            {
                Log.Error("[OuterrealmTechRobot] Exception while spawning fabricated Artificial Maid: " + ex);
            }

            // 不能只相信 GenSpawn 的返回值；SpawnSetup 也可能因无效 Pawn 状态将其再次反生成。
            if (!spawned ||
                !ArtificialMaidTransferUtility.IsSafelySpawned(pawn, billDoer.Map) ||
                pawn.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                string spawnState =
                    ArtificialMaidTransferUtility.DescribeSpawnState(pawn, billDoer.Map);
                DiscardFailedPawn(pawn);
                Log.Error("[OuterrealmTechRobot] Fabricated Artificial Maid failed final spawn validation. " +
                          "trySpawnResult=" + spawned + ", " + spawnState);
                Messages.Message("ArtificialMaidFabricationFailed".Translate(),
                    MessageTypeDefOf.RejectInput);
                return;
            }

            // 确保技能全满且双火
            if (pawn.skills != null)
            {
                foreach (var skill in pawn.skills.skills)
                {
                    skill.Level = 99;
                    skill.passion = Passion.Major;
                }
            }

            // 只有 Pawn 已确认生成并完成初始化后，才提交电量消耗和成功消息。
            ConsumePowerFromNet(net, PowerRequired);
            Messages.Message("ArtificialMaidFabricated".Translate(pawn.LabelShort), pawn,
                MessageTypeDefOf.PositiveEvent);
            }
            catch (Exception ex)
            {
                DiscardFailedPawn(pawn);
                Log.Error("[OuterrealmTechRobot] Failed to fabricate Artificial Maid: " + ex);
                Messages.Message("ArtificialMaidFabricationFailed".Translate(),
                    MessageTypeDefOf.RejectInput);
            }
        }

        private static void DiscardFailedPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }

            try
            {
                if (pawn.Spawned)
                {
                    pawn.DeSpawnOrDeselect();
                }

                // 不调用 Pawn.Destroy，避免为从未成功制造的 Pawn 建立云端备份。
                if (Find.WorldPawns.Contains(pawn))
                {
                    Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
                }
                else if (!pawn.Discarded && pawn.ParentHolder == null)
                {
                    Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[OuterrealmTechRobot] Failed to discard invalid fabricated Artificial Maid: " + ex);
            }
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
