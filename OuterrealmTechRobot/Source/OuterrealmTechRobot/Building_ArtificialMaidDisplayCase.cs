using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class Building_ArtificialMaidDisplayCase : Building_Casket
    {
        public bool autoHibernate = false;
        public bool autoWake = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autoHibernate, "autoHibernate", false);
            Scribe_Values.Look(ref autoWake, "autoWake", false);
        }

        public override bool Accepts(Thing thing)
        {
            if (!base.Accepts(thing)) return false;
            if (thing is Pawn p && p.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }
            return false;
        }

        protected override void Tick()
        {
            base.Tick();
            if (this.IsHashIntervalTick(250))
            {
                if (autoWake && HasAnyContents)
                {
                    Pawn pawn = ContainedThing as Pawn;
                    if (pawn != null && AnyJobFor(pawn))
                    {
                        EjectContents();
                    }
                }
            }
        }

        private bool AnyJobFor(Pawn pawn)
        {
            if (pawn.MapHeld == null) return false;

            IntVec3 oldPos = pawn.Position;
            pawn.SetPositionDirect(this.Position);

            try
            {
                // We use the pawn's think tree to see if they would do something other than "Wait" or "Wander"
                // Note: We use the MainThinkNodeRoot which includes all work givers.
                ThinkResult thinkResult = pawn.thinker.MainThinkNodeRoot.TryIssueJobPackage(pawn, new JobIssueParams());
                
                if (thinkResult.IsValid && thinkResult.Job != null)
                {
                    JobDef def = thinkResult.Job.def;
                    // Check if it's a "real" job
                    if (def != JobDefOf.Wait && 
                        def != JobDefOf.Wait_MaintainPosture && 
                        def != JobDefOf.Wait_SafeTemperature && 
                        def != JobDefOf.Wait_Wander &&
                        !def.defName.Contains("Wander") &&
                        (ArtificialMaidDefOf.EnterDisplayCase == null || def != ArtificialMaidDefOf.EnterDisplayCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Job searching might throw errors if the pawn is in an invalid state, though unlikely here.
                Log.ErrorOnce("Error during auto-wake job search for " + pawn.LabelShort + ": " + ex.Message, 54321);
            }
            finally
            {
                pawn.SetPositionDirect(oldPos);
            }

            return false;
        }

        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            base.DynamicDrawPhaseAt(phase, drawLoc, flip);
            if (phase == DrawPhase.Draw && HasAnyContents)
            {
                Pawn pawn = ContainedThing as Pawn;
                if (pawn != null)
                {
                    Vector3 pawnDrawLoc = drawLoc;
                    pawnDrawLoc.y += 0.04054054f; // Just above the building
                    
                    pawn.Drawer.renderer.DynamicDrawPhaseAt(phase, pawnDrawLoc, new Rot4?(Rot4.South), true);
                }
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            yield return new Command_Toggle
            {
                defaultLabel = "AutoHibernateLabel".Translate(),
                defaultDesc = "AutoHibernateDesc".Translate(),
                isActive = () => autoHibernate,
                toggleAction = () => autoHibernate = !autoHibernate,
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AutoHibernate", false) ?? BaseContent.WhiteTex
            };

            yield return new Command_Toggle
            {
                defaultLabel = "AutoWakeLabel".Translate(),
                defaultDesc = "AutoWakeDesc".Translate(),
                isActive = () => autoWake,
                toggleAction = () => autoWake = !autoWake,
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AutoWake", false) ?? BaseContent.WhiteTex
            };
            
            if (HasAnyContents && Faction == Faction.OfPlayer)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandPodEject".Translate(),
                    defaultDesc = "CommandPodEjectDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/PodEject"),
                    action = () => EjectContents()
                };
            }
        }
        
        public override void EjectContents()
        {
            foreach (Thing thing in (IEnumerable<Thing>) this.innerContainer)
            {
                if (thing is Pawn pawn)
                {
                    PawnComponentsUtility.AddComponentsForSpawn(pawn);
                }
            }
            base.EjectContents();
        }
    }

    public class JobDriver_EnterDisplayCase : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(TargetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            
            Toil prepare = new Toil();
            prepare.initAction = delegate
            {
                Pawn actor = prepare.actor;
                Building_ArtificialMaidDisplayCase pod = (Building_ArtificialMaidDisplayCase)actor.CurJob.targetA.Thing;
                if (!pod.HasAnyContents)
                {
                    pod.TryAcceptThing(actor);
                }
            };
            prepare.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return prepare;
        }
    }

    public class JobGiver_ArtificialMaidHibernate : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.def != ArtificialMaidDefOf.ArtificialMaid) return null;

            // Only hibernate if the pawn is idle and auto-hibernate is enabled on some display case.
            // This JobGiver should be placed in the idle section of the think tree.

            Building_ArtificialMaidDisplayCase displayCase = (Building_ArtificialMaidDisplayCase)GenClosest.ClosestThingReachable(
                pawn.Position, pawn.Map, 
                ThingRequest.ForDef(ArtificialMaidDefOf.ArtificialMaidDisplayCase), 
                PathEndMode.InteractionCell, 
                TraverseParms.For(pawn), 
                9999f, 
                t => 
                {
                    var dc = (Building_ArtificialMaidDisplayCase)t;
                    return dc.autoHibernate && !dc.HasAnyContents && dc.Faction == pawn.Faction;
                }
            );

            if (displayCase != null)
            {
                return JobMaker.MakeJob(ArtificialMaidDefOf.EnterDisplayCase, displayCase);
            }

            return null;
        }
    }
}
