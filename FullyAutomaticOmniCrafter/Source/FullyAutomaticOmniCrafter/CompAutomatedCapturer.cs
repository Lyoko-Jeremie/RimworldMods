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
    public enum CaptureEffect
    {
        TeleportOnly,
        Stun,
        TameOrRecruit,
        HostileToPrisoner
    }

    /// <summary>
    /// 自动捕捉器
    /// 能够自动捕捉地图上所有的指定类型的pawn（不包括当前所在的room区域），将其移动到建筑当前所在的room区域中（包括建筑本身所在的格子）
    /// 用于实现自动捕捉功能，包括将敌人捕捉到KillBox，或是将动物捕捉到动物圈养区，将囚犯捕捉待监狱，将攻击者从基地中传送到基地外，等等
    /// </summary>
    public class CompAutomatedCapturer : ThingComp
    {
        public bool isActive = true;
        public OmniPhantomWall2_PassabilitySettings settings = new OmniPhantomWall2_PassabilitySettings();
        public CaptureEffect captureEffect = CaptureEffect.TeleportOnly;
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
        public void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true;
        }

        public override void PostPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostPostApplyDamage(dinfo, totalDamageDealt);
            if (parent.HitPoints < parent.MaxHitPoints)
            {
                parent.HitPoints = parent.MaxHitPoints;
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            if (!isActive || !parent.Spawned || parent.Map == null) return;

            ScanAndCapture();
        }

        private void ScanAndCapture()
        {
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

        public bool IsValidTarget(Pawn p)
        {
            if (p == null || p.Dead || !p.Spawned || p.Map != parent.Map) return false;

            // 检查是否符合筛选条件
            if (!CanPass(p)) return false;

            // 排除当前所在的room区域
            Room pawnRoom = p.GetRoom();
            Room parentRoom = parent.GetRoom();
            if (pawnRoom != null && parentRoom != null && pawnRoom == parentRoom) return false;
            
            // 如果都不在房间里（户外），且距离很近，也视为已在区域内
            if (pawnRoom == null && parentRoom == null && p.Position.DistanceToEdge(parent.Map) > 0)
            {
                if (p.Position.InHorDistOf(parent.Position, 5f)) return false;
            }

            return true;
        }

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

        private IntVec3 GetTargetCell(Room room)
        {
            if (room != null && !room.PsychologicallyOutdoors)
            {
                return room.Cells.Where(c => c.Standable(parent.Map)).RandomElementWithFallback(parent.Position);
            }
            return parent.Position;
        }

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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "AutomatedCapturer_OpenSettings".Translate(),
                defaultDesc = "AutomatedCapturer_OpenSettingsDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Gizmos/Trade"),
                action = () => Find.WindowStack.Add(new Dialog_AutomatedCapturerSettings(this))
            };

            yield return new Command_Toggle
            {
                defaultLabel = "AutomatedCapturer_Active".Translate(),
                defaultDesc = "AutomatedCapturer_ActiveDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack"),
                isActive = () => isActive,
                toggleAction = () => isActive = !isActive
            };

            yield return new Command_Action
            {
                defaultLabel = "AutomatedCapturer_ManualCapture".Translate(),
                defaultDesc = "AutomatedCapturer_ManualCaptureDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Designators/GatherAnimalProduce"),
                action = () => ScanAndCapture()
            };
        }
    }
}
