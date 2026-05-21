using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 捕捉后的附加效果枚举
    /// </summary>
    public enum CaptureEffect
    {
        /// <summary> 仅传送 </summary>
        TeleportOnly,
        /// <summary> 传送并击晕 </summary>
        Stun,
        /// <summary> 传送并尝试驯服(动物)或招募(人类) </summary>
        TameOrRecruit,
        /// <summary> 传送并将敌对人类转为囚犯 </summary>
        HostileToPrisoner
    }
    
    public class CompProperties_AutomatedCapturer : CompProperties
    {
        public CompProperties_AutomatedCapturer()
        {
            this.compClass = typeof(CompAutomatedCapturer);
        }
    }

    /// <summary>
    /// 自动捕捉器
    /// 能够自动捕捉地图上所有的指定类型的pawn（不包括当前所在的room区域），将其移动到建筑当前所在的room区域中（包括建筑本身所在的格子）
    /// 用于实现自动捕捉功能，包括将敌人捕捉到KillBox，或是将动物捕捉到动物圈养区，将囚犯捕捉待监狱，将攻击者从基地中传送到基地外，等等
    /// </summary>
    public class CompAutomatedCapturer : ThingComp
    {
        public CompProperties_AutomatedCapturer Props => (CompProperties_AutomatedCapturer)this.props;
        
        /// <summary> 是否激活自动扫描捕捉 </summary>
        public bool isActive = true;
        /// <summary> 筛选设置（复用 OmniPhantomWall2 的通行证设置结构） </summary>
        public OmniPhantomWall2_PassabilitySettings settings = new OmniPhantomWall2_PassabilitySettings();
        /// <summary> 捕捉时应用的额外效果 </summary>
        public CaptureEffect captureEffect = CaptureEffect.TeleportOnly;
        /// <summary> 是否显示传送视觉和音效 </summary>
        public bool showVisualEffects = true;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref isActive, "isActive", true);
            Scribe_Deep.Look(ref settings, "settings");
            if (settings == null) settings = new OmniPhantomWall2_PassabilitySettings();
            Scribe_Values.Look(ref captureEffect, "captureEffect", CaptureEffect.TeleportOnly);
            Scribe_Values.Look(ref showVisualEffects, "showVisualEffects", true);
        }

        // --- 无敌逻辑 ---
        /// <summary>
        /// 在受到伤害前拦截，实现绝对无敌。
        /// </summary>
        public void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true;
        }

        /// <summary>
        /// 受到伤害后的回调，强制回满血作为第二重保险。
        /// </summary>
        public override void PostPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostPostApplyDamage(dinfo, totalDamageDealt);
            if (parent.HitPoints < parent.MaxHitPoints)
            {
                parent.HitPoints = parent.MaxHitPoints;
            }
        }

        /// <summary>
        /// 稀疏 Tick (每250tick执行一次)，用于定期扫描地图上的目标。
        /// </summary>
        public override void CompTickRare()
        {
            base.CompTickRare();
            if (!isActive || !parent.Spawned || parent.Map == null) return;

            ScanAndCapture();
        }

        /// <summary>
        /// 扫描全地图符合条件的 Pawn 并执行捕捉。
        /// </summary>
        private void ScanAndCapture()
        {
            if(parent.Map == null) return;

            Map map = parent.Map;
            Room targetRoom = parent.GetRoom();

            var targets = map.mapPawns.AllPawnsSpawned
                .Where(p => IsValidTarget(p))
                .ToList();

            foreach (var pawn in targets)
            {
                IntVec3 targetCell = GetTargetCell(targetRoom);
                ExecuteCapture(pawn, targetCell);
            }
        }

        /// <summary>
        /// 判断一个 Pawn 是否为合法的捕捉目标。
        /// </summary>
        public bool IsValidTarget(Pawn p)
        {
            if (p == null || p.Dead || !p.Spawned || p.Map != parent.Map) return false;

            // 检查是否符合筛选条件
            if (!CanPass(p)) return false;

            // 排除当前所在的room区域 (已经在目标房间内则不需要捕捉)
            Room pawnRoom = p.GetRoom();
            Room parentRoom = parent.GetRoom();

            // 如果都在同一个房间，不需要捕捉
            if (pawnRoom != null && parentRoom != null && pawnRoom == parentRoom) return false;

            // 对于在室外的情况，如果同时都在室外，则不进行传送
            bool pawnOutdoors = pawnRoom == null || pawnRoom.PsychologicallyOutdoors;
            bool parentOutdoors = parentRoom == null || parentRoom.PsychologicallyOutdoors;
            if (pawnOutdoors && parentOutdoors) return false;

            return true;
        }

        /// <summary>
        /// 核心筛选逻辑：判断 Pawn 是否符合 settings 中定义的"允许通过"类型。
        /// 逻辑上与 OmniPhantomWall2 保持一致。
        /// </summary>
        private bool CanPass(Pawn pawn)
        {
            // 复用 OmniPhantomWall2 的逻辑
            if (pawn == null) return false;
            if (!settings.allowHostiles && pawn.HostileTo(Faction.OfPlayer)) return false;
            if (pawn.IsPrisonerOfColony) return settings.allowColonyPrisoners;
            if (pawn.IsPrisoner) return settings.allowPrisoners;
            if (pawn.Faction == Faction.OfPlayer)
            {
                if (pawn.RaceProps.Humanlike) return settings.allowColonists;
                if (pawn.RaceProps.IsAnomalyEntity) return settings.allowEntities;
                if (pawn.RaceProps.IsMechanoid) return settings.allowMechanoids;
                if (pawn.RaceProps.Dryad) return settings.allowDryad;
                if (pawn.RaceProps.Insect) return settings.allowInsectoids;
                if (pawn.RaceProps.Animal) return settings.allowPets;
            }
            if (pawn.HostileTo(Faction.OfPlayer)) return settings.allowHostiles;
            if (settings.allowTraders && !pawn.HostileTo(Faction.OfPlayer) && pawn.Faction != null && pawn.Faction != Faction.OfPlayer && pawn.GetLord() != null) return settings.allowTraders;
            if (pawn.RaceProps.IsAnomalyEntity) return settings.allowEntities;
            if (pawn.RaceProps.IsMechanoid) return settings.allowMechanoids;
            if (pawn.RaceProps.Dryad) return settings.allowDryad;
            if (pawn.RaceProps.Insect) return settings.allowInsectoids;
            if (pawn.RaceProps.Animal && pawn.Faction == null) return settings.allowWildAnimals;
            if (settings.allowHumanlikes && pawn.RaceProps.Humanlike) return true;
            if (settings.allowToolUsers && pawn.RaceProps.ToolUser) return true;
            if (settings.allowFactioned && pawn.Faction != null) return true;
            if (settings.allowLords && pawn.GetLord() != null) return true;
            if (settings.allowUnfactions && pawn.Faction == null && pawn.GetLord() == null) return true;
            if (pawn.Faction == Faction.OfPlayer) return true;
            return false;
        }

        /// <summary>
        /// 获取传送的目标坐标。如果是室内则随机选一格，否则默认传送到建筑本身位置。
        /// </summary>
        private IntVec3 GetTargetCell(Room room)
        {
            if (room != null && !room.PsychologicallyOutdoors)
            {
                return room.Cells.Where(c => c.Standable(parent.Map)).RandomElementWithFallback(parent.Position);
            }
            return parent.Position;
        }

        /// <summary>
        /// 执行具体的捕捉传送逻辑，包含视觉效果和后续影响。
        /// </summary>
        private void ExecuteCapture(Pawn pawn, IntVec3 targetCell)
        {
            Map map = parent.Map;
            try
            {
                if (showVisualEffects)
                {
                    FleckMaker.Static(pawn.Position, map, FleckDefOf.ExplosionFlash);
                    SoundDefOf.Thunder_OffMap.PlayOneShot(new TargetInfo(pawn.Position, map));
                }

                pawn.DeSpawn();
                GenSpawn.Spawn(pawn, targetCell, map);

                if (showVisualEffects)
                {
                    FleckMaker.Static(targetCell, map, FleckDefOf.ExplosionFlash);
                    SoundDefOf.Thunder_OffMap.PlayOneShot(new TargetInfo(targetCell, map));
                }

                ApplyCaptureEffects(pawn);
            }
            catch (Exception e)
            {
                Log.Error($"[CompAutomatedCapturer] Error capturing {pawn.LabelShort}: {e}");
                if (!pawn.Spawned) GenSpawn.Spawn(pawn, targetCell, map);
            }
        }

        /// <summary>
        /// 应用捕捉后的额外效果（如击晕、招募等）。
        /// </summary>
        private void ApplyCaptureEffects(Pawn pawn)
        {
            switch (captureEffect)
            {
                case CaptureEffect.Stun:
                    pawn.stances.stunner.StunFor(600, parent);
                    break;
                case CaptureEffect.TameOrRecruit:
                    if (pawn.RaceProps.Animal && pawn.Faction != Faction.OfPlayer)
                    {
                        InteractionWorker_RecruitAttempt.DoRecruit(parent.Faction.IsPlayer ? pawn.Map.mapPawns.FreeColonists.RandomElement() : null, pawn);
                    }
                    else if (pawn.RaceProps.Humanlike && pawn.Faction != Faction.OfPlayer)
                    {
                        pawn.SetFaction(Faction.OfPlayer);
                    }
                    break;
                case CaptureEffect.HostileToPrisoner:
                    if (pawn.RaceProps.Humanlike && pawn.HostileTo(Faction.OfPlayer))
                    {
                        pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                    }
                    break;
            }
        }

        /// <summary>
        /// 添加建筑额外的交互图标（Gizmos）。
        /// </summary>
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "AutomatedCapturer_OpenSettings".Translate(),
                defaultDesc = "AutomatedCapturer_OpenSettingsDesc".Translate(),
                icon = CompAutomatedCapturerTex.IconOpenSettings,
                action = () => Find.WindowStack.Add(new Dialog_AutomatedCapturerSettings(this))
            };

            yield return new Command_Toggle
            {
                defaultLabel = "AutomatedCapturer_Active".Translate(),
                defaultDesc = "AutomatedCapturer_ActiveDesc".Translate(),
                icon = CompAutomatedCapturerTex.IconActive,
                isActive = () => isActive,
                toggleAction = () => isActive = !isActive
            };

            yield return new Command_Action
            {
                defaultLabel = "AutomatedCapturer_ManualCapture".Translate(),
                defaultDesc = "AutomatedCapturer_ManualCaptureDesc".Translate(),
                icon = CompAutomatedCapturerTex.IconManualCapture,
                action = () => ScanAndCapture()
            };
        }
    }
    

    [StaticConstructorOnStartup]
    public static class CompAutomatedCapturerTex
    {
        public static readonly Texture2D IconOpenSettings =
            ContentFinder<Texture2D>.Get("UI/Commands/AutomatedCapturer_OpenSettings", true) ?? BaseContent.WhiteTex;
        
        public static readonly Texture2D IconActive =
            ContentFinder<Texture2D>.Get("UI/Commands/AutomatedCapturer_Active", true) ?? BaseContent.WhiteTex;
        
        public static readonly Texture2D IconManualCapture =
            ContentFinder<Texture2D>.Get("UI/Commands/AutomatedCapturer_ManualCapture", true) ?? BaseContent.WhiteTex;
    }
    
}
