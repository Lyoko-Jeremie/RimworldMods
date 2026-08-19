using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 女仆 Gizmo：玩家手动命令"抱起主人"（公主抱）与"放下主人"。
    /// 与 Patch_Pawn_GetGizmos_Servitude 共存（均为 Postfix 追加 gizmo，互不冲突）。
    /// 图标兜底用 TexCommand.ClearPrioritizedWork，不强制要求额外纹理。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos_CarryMaster
    {
        private static readonly Texture2D IconCarryMaster =
            ContentFinder<Texture2D>.Get("UI/Commands/AM_CarryMaster", false) ?? TexCommand.ClearPrioritizedWork;

        private static readonly Texture2D IconDropMaster =
            ContentFinder<Texture2D>.Get("UI/Commands/AM_DropMaster", false) ?? TexCommand.ClearPrioritizedWork;

        [HarmonyPostfix]
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo gizmo in __result)
            {
                yield return gizmo;
            }

            // 仅女仆、存活、在地图上
            if (__instance.def != ArtificialMaidDefOf.ArtificialMaid || __instance.Dead || __instance.Map == null)
            {
                yield break;
            }

            ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
            Pawn master = mgr?.GetMaster(__instance);
            if (master == null || master.Dead)
            {
                yield break;
            }

            // 已抱着主人：显示"放下"（被抱主人已 DeSpawn，Map 恒为 null，跳过同图检查）
            if (ArtificialMaidCarryUtility.IsCarryingMaster(__instance))
            {
                yield return new Command_Action
                {
                    defaultLabel = "AM_CarryMaster_DropLabel".Translate(),
                    defaultDesc = "AM_CarryMaster_DropDesc".Translate(),
                    icon = IconDropMaster,
                    action = delegate { ArtificialMaidCarryUtility.DropCarriedMaster(__instance); }
                };
                yield break;
            }

            // 未携带：主人须在地图上同图、清醒、未倒地（倒地/昏迷由 JobGiver_AMCarryFollow 自动接管）
            if (master.Map != __instance.Map || master.Downed || !master.Spawned || master.InMentalState)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "AM_CarryMaster_Label".Translate(),
                defaultDesc = "AM_CarryMaster_Desc".Translate(),
                icon = IconCarryMaster,
                action = delegate
                {
                    if (__instance.carryTracker != null && __instance.carryTracker.CarriedThing == null)
                    {
                        Job job = JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_CarryMaster, master);
                        __instance.jobs.TryTakeOrderedJob(job);
                    }
                }
            };
        }
    }
}
