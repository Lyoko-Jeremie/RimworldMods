using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 「建立侍奉关系」入口：为玩家阵营人类 Pawn 追加 Gizmo。
    /// - 选中女仆 → 列出自由殖民者作为「主人」候选（认主）
    /// - 选中殖民者 → 列出人工女仆作为「侍奉者」候选（收女仆）
    /// - 主人身份 → 解除单个/全部侍奉者；侍奉者身份 → 解除自己的侍奉
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos_Servitude
    {
        [HarmonyPostfix]
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo gizmo in __result)
            {
                yield return gizmo;
            }

            // 仅玩家阵营、人类、存活
            if (__instance.RaceProps.Humanlike && __instance.Faction == Faction.OfPlayer && !__instance.Dead)
            {
                ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
                if (mgr == null)
                {
                    yield break;
                }

                bool isMaid = __instance.def == ArtificialMaidDefOf.ArtificialMaid;
                bool isMaster = mgr.IsMaster(__instance);
                bool isServant = mgr.IsServant(__instance);

                // 建立侍奉关系（无侍奉身份限制：主人/普通殖民者/女仆皆可发起）
                Command_Action bind = new Command_Action
                {
                    defaultLabel = "AM_Servitude_BindLabel".Translate(),
                    defaultDesc = "AM_Servitude_BindDesc".Translate(),
                    icon = ArtificialMaidTex.IconServitudeBond,
                    action = delegate
                    {
                        List<FloatMenuOption> options = new List<FloatMenuOption>();
                        Map map = __instance.Map;
                        if (map != null)
                        {
                            foreach (Pawn candidate in map.mapPawns.FreeColonists)
                            {
                                if (candidate == null || candidate == __instance || candidate.Dead)
                                {
                                    continue;
                                }

                                if (isMaid)
                                {
                                    // 女仆认主：候选为自由殖民者（排除其他女仆，主人不应是女仆）
                                    if (candidate.def == ArtificialMaidDefOf.ArtificialMaid)
                                    {
                                        continue;
                                    }

                                    AddMasterCandidateOption(options, mgr, __instance, candidate);
                                }
                                else
                                {
                                    // 殖民者收女仆：候选为自由殖民者中的人造人女仆
                                    if (candidate.def == ArtificialMaidDefOf.ArtificialMaid)
                                    {
                                        AddServantCandidateOption(options, mgr, __instance, candidate);
                                    }
                                }
                            }
                        }

                        if (options.Count == 0)
                        {
                            options.Add(new FloatMenuOption((string)"AM_Servitude_BindNone".Translate(), null));
                        }

                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                };
                yield return bind;

                // 解除侍奉关系
                if (isMaster)
                {
                    Command_Action unbind = new Command_Action
                    {
                        defaultLabel = "AM_Servitude_UnbindLabel".Translate(),
                        defaultDesc = "AM_Servitude_UnbindDesc_Master".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Dismiss", false) ?? BaseContent.WhiteTex,
                        action = delegate
                        {
                            List<FloatMenuOption> options = new List<FloatMenuOption>();
                            List<Pawn> servants = mgr.GetServants(__instance);
                            foreach (Pawn servant in servants)
                            {
                                Pawn target = servant;
                                options.Add(new FloatMenuOption(
                                    (string)"AM_Servitude_UnbindOption".Translate(target.LabelShort),
                                    delegate { mgr.Unbind(target); }));
                            }

                            if (servants.Count > 1)
                            {
                                options.Add(new FloatMenuOption(
                                    (string)"AM_Servitude_UnbindAll".Translate(),
                                    delegate { mgr.UnbindAll(__instance); }));
                            }

                            if (options.Count == 0)
                            {
                                options.Add(new FloatMenuOption((string)"AM_Servitude_BindNone".Translate(), null));
                            }

                            Find.WindowStack.Add(new FloatMenu(options));
                        }
                    };
                    yield return unbind;
                }
                else if (isServant)
                {
                    Command_Action unbind = new Command_Action
                    {
                        defaultLabel = "AM_Servitude_UnbindLabel".Translate(),
                        defaultDesc = "AM_Servitude_UnbindDesc_Servant".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Dismiss", false) ?? BaseContent.WhiteTex,
                        action = delegate
                        {
                            Pawn oldMaster = mgr.GetMaster(__instance);
                            mgr.Unbind(__instance);
                            Messages.Message(
                                "AM_Servitude_UnboundMessage".Translate(__instance.LabelShort, oldMaster?.LabelShort ?? ""),
                                __instance, MessageTypeDefOf.NeutralEvent);
                        }
                    };
                    yield return unbind;
                }
            }
        }

        /// <summary>候选成为主人的自由殖民者。</summary>
        private static void AddMasterCandidateOption(
            List<FloatMenuOption> options, ArtificialMaidServitudeManager mgr, Pawn maid, Pawn candidate)
        {
            Pawn existingMaster = mgr.GetMaster(maid);
            if (existingMaster == candidate)
            {
                return; // 已是该主人，无需重复
            }

            options.Add(new FloatMenuOption(
                candidate.LabelShort,
                delegate
                {
                    if (mgr.TryBind(candidate, maid))
                    {
                        Messages.Message(
                            "AM_Servitude_BoundMessage".Translate(maid.LabelShort, candidate.LabelShort),
                            maid, MessageTypeDefOf.PositiveEvent);
                    }
                }));
        }

        /// <summary>候选成为侍奉者的女仆。</summary>
        private static void AddServantCandidateOption(
            List<FloatMenuOption> options, ArtificialMaidServitudeManager mgr, Pawn master, Pawn candidate)
        {
            Pawn existingMaster = mgr.GetMaster(candidate);
            if (existingMaster != null)
            {
                // 已有主人的女仆显示状态且不可选（一仆一主）
                options.Add(new FloatMenuOption(
                    (string)"AM_Servitude_BindOption".Translate(candidate.LabelShort, existingMaster.LabelShort),
                    null));
                return;
            }

            options.Add(new FloatMenuOption(
                candidate.LabelShort,
                delegate
                {
                    if (mgr.TryBind(master, candidate))
                    {
                        Messages.Message(
                            "AM_Servitude_BoundMessage".Translate(candidate.LabelShort, master.LabelShort),
                            candidate, MessageTypeDefOf.PositiveEvent);
                    }
                }));
        }
    }
}
