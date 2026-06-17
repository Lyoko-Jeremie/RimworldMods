using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
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

        public string serialNumber;
        public int manufactureTick = -1;
        public int joinPlayerTick = -1;
        public bool isDuplicate = false;
        public int originPawnId = -1;
        public int originSerialNumber = -1;

        public override void PostPostMake()
        {
            base.PostPostMake();
            if (string.IsNullOrEmpty(serialNumber))
            {
                serialNumber = GenerateSerialNumber();
                manufactureTick = Find.TickManager.TicksGame;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref serialNumber, "serialNumber");
            Scribe_Values.Look(ref manufactureTick, "manufactureTick", -1);
            Scribe_Values.Look(ref joinPlayerTick, "joinPlayerTick", -1);
            Scribe_Values.Look(ref isDuplicate, "isDuplicate", false);
            Scribe_Values.Look(ref originPawnId, "originPawnId", -1);
            Scribe_Values.Look(ref originSerialNumber, "originSerialNumber", -1);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (string.IsNullOrEmpty(serialNumber))
                {
                    serialNumber = GenerateSerialNumber();
                    if (manufactureTick < 0) manufactureTick = Find.TickManager.TicksGame;
                }
            }
        }

        public override void Notify_DuplicatedFrom(Pawn source)
        {
            base.Notify_DuplicatedFrom(source);
            isDuplicate = true;
            originPawnId = source.thingIDNumber;
            originSerialNumber = source.originSerialNumber;
            // 复制体生成新的序列号以便区分
            serialNumber = GenerateSerialNumber();
            manufactureTick = Find.TickManager.TicksGame;
        }

        private string GenerateSerialNumber()
        {
            return $"AM-{Rand.Range(1000, 9999)}-{Rand.Range(1000, 9999)}"; // + Find.TickManager.TicksGame last 4 number
        }

        public override string CompInspectStringExtra()
        {
            string str = base.CompInspectStringExtra();
            if (!string.IsNullOrEmpty(str)) str += "\n";

            string duplicateSuffix = isDuplicate ? " (" + (string)"ArtificialMaidDuplicate".Translate() + ")" : "";
            str += (string)"ArtificialMaidSerialNumber".Translate() + ": " + serialNumber + duplicateSuffix;
            if (manufactureTick > 0)
            {
                str += "\n" + (string)"ArtificialMaidManufactureDate".Translate() + ": " + GetDateString(manufactureTick);
            }
            if (joinPlayerTick > 0)
            {
                str += "\n" + (string)"ArtificialMaidJoinDate".Translate() + ": " + GetDateString(joinPlayerTick);
            }
            return str;
        }

        private string GetDateString(int tick)
        {
            if (tick < 0) return "Unknown";
            long absTicks = (long)GenDate.TickGameToAbs(tick);
            return GenDate.DateReadoutStringAt(absTicks, Vector2.zero);
        }

        public override void CompTick()
        {
            base.CompTick();

            // 立即转化逻辑：如果不是我方派系，立即转化
            if (Pawn != null && !Pawn.Dead && Pawn.Faction != Faction.OfPlayer)
            {
                this.AutoConvertFaction();
            }

            if (this.parent.IsHashIntervalTick(60))
            {
                this.ReplenishResources();
                this.EnsureRecoveryHediff();
                this.AutoConvertFaction();
                this.EnsureMaidProperties();
            }

            if (this.parent.IsHashIntervalTick(250))
            {
                this.ApplyEmotionalSupport();
            }
        }

        private void ApplyEmotionalSupport()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Dead) return;

            // 检查是否有“情感同步”特性
            if (Pawn.story?.traits != null &&
                Pawn.story.traits.HasTrait(ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony))
            {
                // 获取周围 50 格内的 Pawn
                float radius = 50f;
                IntVec3 pos = Pawn.Position;
                Map map = Pawn.Map;
                Faction faction = Pawn.Faction;

                foreach (var thing in GenRadial.RadialDistinctThingsAround(pos, map, radius, true))
                {
                    if (thing is Pawn other && other != Pawn && other.Faction == faction && other.RaceProps.Humanlike)
                    {
                        other.needs?.mood?.thoughts?.memories?.TryGainMemory(ArtificialMaidDefOf.MaidEmotionalSupport,
                            Pawn);
                    }
                }
            }
        }

        public void EnsureMaidProperties()
        {
            if (Pawn == null) return;

            if (ModsConfig.BiotechActive && Pawn.genes != null)
            {
                var xenotypeDef = ArtificialMaidDefOf.ArtificialMaidXenotype;
                if (xenotypeDef != null && Pawn.genes.Xenotype != xenotypeDef)
                {
                    Pawn.genes.SetXenotype(xenotypeDef);
                }

                var coreGeneDef = ArtificialMaidDefOf.ArtificialMaid_Core;
                if (coreGeneDef != null && !Pawn.genes.HasActiveGene(coreGeneDef))
                {
                    Pawn.genes.AddGene(coreGeneDef, false);
                }
            }

            // 确保情感同步特性
            if (Pawn.story?.traits != null)
            {
                var traitDef = ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony;
                if (traitDef != null && !Pawn.story.traits.HasTrait(traitDef))
                {
                    Pawn.story.traits.GainTrait(new Trait(traitDef));
                }
            }

            this.EnsureSkinColor();
        }

        public void EnsureSkinColor()
        {
            if (Pawn?.story == null) return;

            // 确保肤色锁定为 Pale (250, 240, 240)
            Color pale = ArtificialMaidTex.PaleSkinColor;

            bool changed = false;
            if (Pawn.story.skinColorOverride == null || !Pawn.story.skinColorOverride.Value.IndistinguishableFrom(pale))
            {
                Pawn.story.skinColorOverride = pale;
                changed = true;
            }

            // HAR 可能会使用 SkinColorBase，所以也强制它
            if (!Pawn.story.SkinColorBase.IndistinguishableFrom(pale))
            {
                Pawn.story.SkinColorBase = pale;
                changed = true;
            }

            if (changed)
            {
                // 强制刷新渲染
                Pawn.Drawer.renderer.SetAllGraphicsDirty();
            }
        }

        public void EnsureRecoveryHediff()
        {
            if (Pawn == null || Pawn.Dead) return;
            var def = ArtificialMaidDefOf.ArtificialMaidRecovery;
            if (def != null && !Pawn.health.hediffSet.HasHediff(def))
            {
                Pawn.health.AddHediff(def);
            }
        }

        public void FullRepair()
        {
            if (Pawn == null) return;

            // 1. 修复所有损伤和缺失
            // 先恢复所有缺失部位
            foreach (var part in Pawn.health.hediffSet.GetMissingPartsCommonAncestors())
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

            // 2. 去除不属于 ArtificialMaid 的特质并确保情感同步特性
            if (Pawn.story?.traits != null)
            {
                var traits = Pawn.story.traits.allTraits;
                for (int i = traits.Count - 1; i >= 0; i--)
                {
                    var trait = traits[i];
                    if (trait.def != null && !trait.def.defName.StartsWith("ArtificialMaidTrait_"))
                    {
                        Pawn.story.traits.RemoveTrait(trait);
                    }
                }

                var syncTraitDef = ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony;
                if (syncTraitDef != null && !Pawn.story.traits.HasTrait(syncTraitDef))
                {
                    Pawn.story.traits.GainTrait(new Trait(syncTraitDef));
                }
            }

            // 替换不属于 ArtificialMaid 的背景故事
            if (Pawn.story != null)
            {
                if (Pawn.story.Childhood != null)
                {
                    bool isMaidBackstory = false;
                    if (Pawn.story.Childhood.spawnCategories != null)
                    {
                        for (int i = 0; i < Pawn.story.Childhood.spawnCategories.Count; i++)
                        {
                            if (Pawn.story.Childhood.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                isMaidBackstory = true;
                                break;
                            }
                        }
                    }

                    if (!isMaidBackstory)
                    {
                        Pawn.story.Childhood = ArtificialMaidMod.MaidChildhood;
                    }
                }

                if (Pawn.story.Adulthood != null)
                {
                    bool isMaidBackstory = false;
                    if (Pawn.story.Adulthood.spawnCategories != null)
                    {
                        for (int i = 0; i < Pawn.story.Adulthood.spawnCategories.Count; i++)
                        {
                            if (Pawn.story.Adulthood.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                isMaidBackstory = true;
                                break;
                            }
                        }
                    }

                    if (!isMaidBackstory)
                    {
                        Pawn.story.Adulthood = ArtificialMaidMod.MaidAdulthood;
                    }
                }
            }

            // 4. 修正需求系统和技能系统
            if (Pawn.needs != null)
            {
                Pawn.needs.AddOrRemoveNeedsAsAppropriate();
            }

            this.ReplenishResources();
            this.EnsureRecoveryHediff();
            this.AutoConvertFaction();
            this.EnsureMaidProperties();

            // RJW 支持：修复/确保性器官存在
            RJWCompatibility.InitializeMaidOrgans(Pawn);
        }

        public void ReplenishResources()
        {
            if (Pawn == null) return;

            // 修复所有损伤和缺失
            foreach (var mp in Pawn.health.hediffSet.GetMissingPartsCommonAncestors())
            {
                Pawn.health.RestorePart(mp.Part);
            }

            var hediffs = Pawn.health.hediffSet.hediffs;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                var hediff = hediffs[i];
                if (hediff is Hediff_Injury || hediff.def.isBad || hediff.def.IsAddiction || hediff.def.chronic)
                {
                    Pawn.health.RemoveHediff(hediff);
                }
            }

            // 清除文化
            if (ModsConfig.IdeologyActive && Pawn.ideo != null && Pawn.ideo.Ideo != null)
            {
                Pawn.ideo.SetIdeo(null);
            }

            // 保持各种需求最高
            if (Pawn.needs != null)
            {
                if (Pawn.needs.mood != null)
                {
                    Pawn.needs.mood.CurLevel = 1.0f;
                    if (Pawn.needs.mood.thoughts != null && Pawn.needs.mood.thoughts.memories != null)
                    {
                        var memories = Pawn.needs.mood.thoughts.memories.Memories;
                        for (int i = memories.Count - 1; i >= 0; i--)
                        {
                            if (memories[i].MoodOffset() < 0)
                            {
                                Pawn.needs.mood.thoughts.memories.RemoveMemory(memories[i]);
                            }
                        }
                    }
                }
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

                if (joinPlayerTick < 0)
                {
                    joinPlayerTick = Find.TickManager.TicksGame;
                }

                this.FullRepair();
                this.EnsureRecoveryHediff();

                string label = "ArtificialMaidRecruitedLabel".Translate(Pawn.LabelShort);
                string text = "ArtificialMaidRecruitedText".Translate(Pawn.LabelShort);
                Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, Pawn);
            }
        }
    }
}