using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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
        private static readonly ConditionalWeakTable<Pawn, CompArtificialMaid> cache = new ConditionalWeakTable<Pawn, CompArtificialMaid>();

        public static CompArtificialMaid GetCompCached(Pawn pawn)
        {
            if (pawn == null) return null;
            return cache.GetValue(pawn, p => p.TryGetComp<CompArtificialMaid>());
        }

        private Pawn Pawn => (Pawn)this.parent;

        public string serialNumber;
        public int manufactureTick = -1;
        public int joinPlayerTick = -1;
        public bool isDuplicate = false;
        public int originPawnId = -1;
        public string originSerialNumber;
        public bool allowAutoHibernate = true;
        public bool enableHealingProtocol = false;
        public bool hostileResponseInitialized = false;

        public override void PostPostMake()
        {
            base.PostPostMake();
            if (string.IsNullOrEmpty(serialNumber))
            {
                serialNumber = GenerateSerialNumber();
                manufactureTick = Find.TickManager.TicksGame;
            }
            EnsureMechanitorCapabilities();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureMechanitorCapabilities();
        }

        private void EnsureMechanitorCapabilities()
        {
            if (!ModsConfig.BiotechActive) return;
            if (Pawn.Faction != null && Pawn.Faction.IsPlayer && Pawn.health != null)
            {
                if (!Pawn.health.hediffSet.HasHediff(HediffDefOf.MechlinkImplant))
                {
                    Pawn.health.AddHediff(HediffDefOf.MechlinkImplant, Pawn.health.hediffSet.GetBrain());
                    PawnComponentsUtility.AddAndRemoveDynamicComponents(Pawn);
                }
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
            Scribe_Values.Look(ref originSerialNumber, "originSerialNumber");
            Scribe_Values.Look(ref allowAutoHibernate, "allowAutoHibernate", true);
            Scribe_Values.Look(ref enableHealingProtocol, "enableHealingProtocol", false);
            Scribe_Values.Look(ref hostileResponseInitialized, "hostileResponseInitialized", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (string.IsNullOrEmpty(serialNumber))
                {
                    serialNumber = GenerateSerialNumber();
                    if (manufactureTick < 0) manufactureTick = Find.TickManager.TicksGame;
                }
                EnsureMechanitorCapabilities();
            }
        }

        public override void Notify_DuplicatedFrom(Pawn source)
        {
            base.Notify_DuplicatedFrom(source);
            isDuplicate = true;
            originPawnId = source.thingIDNumber;
            CompArtificialMaid sourceComp = GetCompCached(source);
            if (sourceComp != null)
            {
                originSerialNumber = sourceComp.serialNumber;
            }
            // 复制体生成新的序列号以便区分
            serialNumber = GenerateSerialNumber();
            manufactureTick = Find.TickManager.TicksGame;
        }

        private string GenerateSerialNumber()
        {
            int tickLastFour = Find.TickManager.TicksGame % 10000;
            return $"AM-{Rand.Range(1000, 9999)}-{Rand.Range(1000, 9999)}-{tickLastFour:D4}";
        }

        public override string CompInspectStringExtra()
        {
            string str = base.CompInspectStringExtra();
            if (!string.IsNullOrEmpty(str)) str += "\n";

            string duplicateSuffix = isDuplicate ? " (" + (string)"ArtificialMaidDuplicate".Translate() + ")" : "";
            str += (string)"ArtificialMaidSerialNumber".Translate() + ": " + serialNumber + duplicateSuffix;
            if (isDuplicate && !string.IsNullOrEmpty(originSerialNumber))
            {
                str += "\n" + (string)"ArtificialMaidOriginSerialNumber".Translate() + ": " + originSerialNumber;
            }
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

            if (Pawn != null && !Pawn.Dead)
            {
                // 初始反应设置
                if (!hostileResponseInitialized && Pawn.playerSettings != null)
                {
                    if (Pawn.Faction == Faction.OfPlayer)
                    {
                        Pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                    }
                    else
                    {
                        Pawn.playerSettings.hostilityResponse = HostilityResponseMode.Flee;
                    }
                    hostileResponseInitialized = true;
                }

                // 立即转化逻辑：如果不是我方派系，立即转化
                if (Pawn.Faction != Faction.OfPlayer)
                {
                    this.AutoConvertFaction();
                }
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
                if (enableHealingProtocol)
                {
                    this.ApplyHealingProtocol();
                }
            }
        }

        private void ApplyHealingProtocol()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Dead || Pawn.Map == null) return;

            // 检查是否有“超维治疗协议”特性
            if (Pawn.story?.traits != null)
            {
                var masterProtocol = ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol;
                if (masterProtocol != null && Pawn.story.traits.HasTrait(masterProtocol))
                {
                    // 专有性检查：必须是我方派系
                    if (Pawn.Faction != Faction.OfPlayer)
                    {
                        var trait = Pawn.story.traits.GetTrait(masterProtocol);
                        if (trait != null)
                        {
                            Pawn.story.traits.RemoveTrait(trait);
                        }
                        return;
                    }

                    float radiusSq = 50f * 50f;
                    IntVec3 pos = Pawn.Position;
                    Map map = Pawn.Map;
                    Faction faction = Pawn.Faction;

                    var list = map.mapPawns.SpawnedPawnsInFaction(faction);
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            Pawn other = list[i];
                            if (other.Position.DistanceToSquared(pos) <= radiusSq)
                            {
                                // 治疗逻辑
                                
                                // 1. 恢复缺失部位
                                var missingParts = other.health.hediffSet.GetMissingPartsCommonAncestors();
                                if (missingParts.Count > 0)
                                {
                                    for (int j = missingParts.Count - 1; j >= 0; j--)
                                    {
                                        other.health.RestorePart(missingParts[j].Part);
                                    }
                                }

                                // 2. 移除所有损伤和疾病
                                List<Hediff> hediffs = other.health.hediffSet.hediffs;
                                for (int j = hediffs.Count - 1; j >= 0; j--)
                                {
                                    Hediff h = hediffs[j];
                                    // 移除损伤、感染、疾病、失血等
                                    if (h is Hediff_Injury || h.def.isBad)
                                    {
                                        // 排除永久性损伤（除非你想治疗它们）
                                        // 用户的要求是“消除所有疾病和身体损伤”，这通常意味着全部
                                        other.health.RemoveHediff(h);
                                    }
                                }

                                // 添加心情
                                other.needs?.mood?.thoughts?.memories?.TryGainMemory(ArtificialMaidDefOf.ArtificialMaidMasterProtocol_Mood, Pawn);
                            }
                        }
                    }
                }
            }
        }

        private void ApplyEmotionalSupport()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Dead || Pawn.Map == null) return;

            // 检查是否有“情感同步”特性
            if (Pawn.story?.traits != null &&
                Pawn.story.traits.HasTrait(ArtificialMaidDefOf.ArtificialMaidTrait_EmotionalSynchrony))
            {
                // 性能优化：不再使用 GenRadial.RadialDistinctThingsAround 扫描大量格子（半径 50 覆盖约 7800+ 格子），
                // 而是直接遍历地图上本派系的 Pawn（通常只有几十个），显著降低计算开销。
                float radiusSq = 50f * 50f;
                IntVec3 pos = Pawn.Position;
                Map map = Pawn.Map;
                Faction faction = Pawn.Faction;

                var list = map.mapPawns.SpawnedPawnsInFaction(faction);
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn other = list[i];
                        if (other != Pawn && other.RaceProps.Humanlike && other.Position.DistanceToSquared(pos) <= radiusSq)
                        {
                            other.needs?.mood?.thoughts?.memories?.TryGainMemory(ArtificialMaidDefOf.MaidEmotionalSupport, Pawn);
                        }
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

                var masterTraitDef = ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol;
                if (masterTraitDef != null)
                {
                    bool hasMaster = Pawn.story.traits.HasTrait(masterTraitDef);
                    bool isPlayerFaction = Pawn.Faction == Faction.OfPlayer;
                    if (isPlayerFaction)
                    {
                        if (!hasMaster)
                        {
                            Pawn.story.traits.GainTrait(new Trait(masterTraitDef));
                        }
                    }
                    else
                    {
                        if (hasMaster)
                        {
                            var trait = Pawn.story.traits.GetTrait(masterTraitDef);
                            if (trait != null)
                            {
                                Pawn.story.traits.RemoveTrait(trait);
                            }
                        }
                    }
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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            if (Pawn.Faction == Faction.OfPlayer)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "AllowAutoHibernateLabel".Translate(),
                    defaultDesc = "AllowAutoHibernateDesc".Translate(),
                    isActive = () => allowAutoHibernate,
                    toggleAction = () => allowAutoHibernate = !allowAutoHibernate,
                    icon = ArtificialMaidTex.IconAutoHibernate
                };

                yield return new Command_Toggle
                {
                    defaultLabel = "EnableHealingProtocolLabel".Translate(),
                    defaultDesc = "EnableHealingProtocolDesc".Translate(),
                    isActive = () => enableHealingProtocol,
                    toggleAction = () => enableHealingProtocol = !enableHealingProtocol,
                    icon = ArtificialMaidTex.IconHealingProtocol
                };

                yield return new Command_Action
                {
                    defaultLabel = "ImmediateHibernateLabel".Translate(),
                    defaultDesc = "ImmediateHibernateDesc".Translate(),
                    icon = ArtificialMaidTex.IconImmediateHibernate,
                    action = () =>
                    {
                        Building_ArtificialMaidDisplayCase displayCase = (Building_ArtificialMaidDisplayCase)GenClosest.ClosestThingReachable(
                            Pawn.Position, Pawn.Map,
                            ThingRequest.ForDef(ArtificialMaidDefOf.ArtificialMaidDisplayCase),
                            PathEndMode.InteractionCell,
                            TraverseParms.For(Pawn),
                            9999f,
                            t =>
                            {
                                var dc = (Building_ArtificialMaidDisplayCase)t;
                                return !dc.HasAnyContents && dc.Faction == Pawn.Faction && Pawn.CanReserve(dc);
                            }
                        );

                        if (displayCase != null)
                        {
                            displayCase.autoWake = false;
                            Job job = JobMaker.MakeJob(ArtificialMaidDefOf.EnterDisplayCase, displayCase);
                            Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                        }
                        else
                        {
                            Messages.Message("NoEmptyDisplayCaseFound".Translate(), MessageTypeDefOf.RejectInput, false);
                        }
                    }
                };
            }
        }

        private void AutoConvertFaction()
        {
            if (Pawn == null || Pawn.Dead || !Pawn.Spawned) return;
            if (Pawn.Faction != Faction.OfPlayer)
            {
                Pawn.SetFaction(Faction.OfPlayer);

                // 转为我方时初始设置为反击
                if (Pawn.playerSettings != null)
                {
                    Pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                }

                if (joinPlayerTick < 0)
                {
                    joinPlayerTick = Find.TickManager.TicksGame;
                }

                this.FullRepair();
                this.EnsureRecoveryHediff();
                this.EnsureMechanitorCapabilities();

                string label = "ArtificialMaidRecruitedLabel".Translate(Pawn.LabelShort);
                string text = "ArtificialMaidRecruitedText".Translate(Pawn.LabelShort);
                Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, Pawn);
            }
        }
    }
}