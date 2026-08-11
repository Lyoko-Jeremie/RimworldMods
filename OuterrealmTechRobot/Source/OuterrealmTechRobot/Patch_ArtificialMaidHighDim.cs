using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维转换模式的全部 Harmony patch。
    /// 所有 patch 均为守卫式：仅对处于高维状态的人造人女仆生效，其余情况完全放行原版逻辑，
    /// 与天使机及其他 Mod 互不干扰。
    /// </summary>
    public static class Patch_ArtificialMaidHighDim
    {
        // ==================== 寻路：切换到高维网格 ====================

        /// <summary>
        /// Pawn.GetPathContext：高维时返回自定义高维网格（必须优先于原版 Flying 分支，
        /// 否则会落到原版飞行网格——它不能穿墙）。
        /// </summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetPathContext))]
        public static class Patch_Pawn_GetPathContext_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn __instance, Pathing pathing, ref PathingContext __result)
            {
                if (!ArtificialMaidHighDimUtility.IsHighDim(__instance) ||
                    ArtificialMaidDefOf.AM_HighDimPathGrid == null)
                {
                    return true;
                }

                __result = pathing.Get(ArtificialMaidDefOf.AM_HighDimPathGrid);
                return false;
            }
        }

        /// <summary>
        /// Pathing.For(TraverseParms)：覆盖 Reachability/WorkGiver 等通过 TraverseParms 取网格的调用点。
        /// （Pathing.For(Pawn) 内部走 Pawn.GetPathContext，已被上面的 patch 覆盖。）
        /// </summary>
        [HarmonyPatch(typeof(Pathing), nameof(Pathing.For), new Type[] { typeof(TraverseParms) })]
        public static class Patch_Pathing_For_TraverseParms_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pathing __instance, TraverseParms parms, ref PathingContext __result)
            {
                Pawn pawn = parms.pawn;
                if (pawn == null || !ArtificialMaidHighDimUtility.IsHighDim(pawn) ||
                    ArtificialMaidDefOf.AM_HighDimPathGrid == null)
                {
                    return true;
                }

                __result = __instance.Get(ArtificialMaidDefOf.AM_HighDimPathGrid);
                return false;
            }
        }

        /// <summary>
        /// PathFinderMapData.ParameterizeGridJob：真正给寻路 worker 的 pathGridDirect 赋高维网格。
        /// 只 patch 这一步，寻路路径才会实际穿越不可通行格。
        /// </summary>
        [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.ParameterizeGridJob))]
        public static class Patch_PathFinderMapData_ParameterizeGridJob_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(PathRequest request, ref PathGridJob job, Map ___map)
            {
                Pawn pawn = request.pawn;
                if (pawn == null || !ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    return;
                }

                if (ArtificialMaidDefOf.AM_HighDimPathGrid == null)
                {
                    return;
                }

                PathingContext ctx = ___map.pathing.Get(ArtificialMaidDefOf.AM_HighDimPathGrid);
                job.pathGridDirect = ctx.pathGrid.Grid_Unsafe.AsReadOnly();
            }
        }

        // ==================== 移动行为：无视建筑/门/占用限制 ====================

        /// <summary>不因建筑挡路触发破墙 Job。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), "BuildingBlockingNextPathCell")]
        public static class Patch_BuildingBlockingNextPathCell_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(ref Building __result, Pawn ___pawn)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(___pawn))
                {
                    __result = null;
                }
            }
        }

        /// <summary>不等门、不手动开门。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.NextCellDoorToWaitForOrManuallyOpen))]
        public static class Patch_NextCellDoorToWaitForOrManuallyOpen_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(ref Building_Door __result, Pawn ___pawn)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(___pawn))
                {
                    __result = null;
                }
            }
        }

        /// <summary>高维时可占用任意格子（含墙内、山体、深水）。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), "PawnCanOccupy")]
        public static class Patch_PawnCanOccupy_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(ref bool __result, Pawn ___pawn)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(___pawn))
                {
                    __result = true;
                }
            }
        }

        // ==================== 可达性与可站立：全可达、可停留任意格 ====================

        [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
            new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
        public static class Patch_Reachability_CanReach_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(LocalTargetInfo dest, TraverseParms traverseParams, ref bool __result, Map ___map)
            {
                Pawn pawn = traverseParams.pawn;
                if (pawn != null && ArtificialMaidHighDimUtility.IsHighDim(pawn) &&
                    (!dest.HasThing || dest.Thing.Map == ___map))
                {
                    __result = true;
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(ReachabilityUtility), nameof(ReachabilityUtility.CanReach),
            new Type[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(Danger), typeof(bool), typeof(bool), typeof(TraverseMode) })]
        public static class Patch_ReachabilityUtility_CanReach_PawnDest_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn pawn, LocalTargetInfo dest, ref bool __result)
            {
                if (!ArtificialMaidHighDimUtility.IsHighDim(pawn) ||
                    (dest.HasThing && dest.Thing.Map != pawn.Map))
                {
                    return true;
                }

                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(ReachabilityUtility), nameof(ReachabilityUtility.CanReach),
            new Type[] { typeof(Pawn), typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(Danger), typeof(bool), typeof(bool), typeof(TraverseMode) })]
        public static class Patch_ReachabilityUtility_CanReach_PawnStartDest_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn pawn, LocalTargetInfo dest, ref bool __result)
            {
                if (!ArtificialMaidHighDimUtility.IsHighDim(pawn) ||
                    (dest.HasThing && dest.Thing.Map != pawn.Map))
                {
                    return true;
                }

                __result = true;
                return false;
            }
        }

        /// <summary>高维女仆可停留在任意格子（深水、墙内、真空无地面区域）。</summary>
        [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.StandableBy))]
        public static class Patch_GenGrid_StandableBy_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn pawn, ref bool __result)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    __result = true;
                }
            }
        }

        /// <summary>不强制移动到“可站立格”。</summary>
        [HarmonyPatch(typeof(JobGiver_MoveToStandable), "TryGiveJob")]
        public static class Patch_JobGiver_MoveToStandable_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn pawn, ref Job __result)
            {
                if (!ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    return true;
                }

                __result = null;
                return false;
            }
        }

        // ==================== 玩家操作：征召右键移动可直接到达任意点击格 ====================

        /// <summary>
        /// 征召状态下右键移动：跳过原版的 StandableCellNear 目的地修正与 PawnCanGoto 可达性检查，
        /// 直接以点击格为目的地（可移动到墙内/深水等任意位置）。
        /// </summary>
        [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), "GetSingleOption")]
        public static class Patch_FloatMenuOptionProvider_DraftedMove_GetSingleOption_HighDim
        {
            private static readonly List<Pawn> tmpPawns = new List<Pawn>();

            [HarmonyPrefix]
            public static bool Prefix(FloatMenuContext context, ref FloatMenuOption __result)
            {
                Pawn first = context.FirstSelectedPawn;
                if (first == null || !ArtificialMaidHighDimUtility.IsHighDim(first) || !context.ClickedCell.IsValid)
                {
                    return true;
                }

                FloatMenuOption option;
                if (!context.IsMultiselect)
                {
                    if (context.ClickedCell == first.Position)
                    {
                        return true;
                    }

                    option = new FloatMenuOption("GoHere".Translate(), () =>
                        FloatMenuOptionProvider_DraftedMove.PawnGotoAction(context.ClickedCell, first, context.ClickedCell),
                        MenuOptionPriority.GoHere);
                }
                else
                {
                    tmpPawns.Clear();
                    foreach (Pawn valid in context.ValidSelectedPawns)
                    {
                        if (ArtificialMaidHighDimUtility.IsHighDim(valid))
                        {
                            tmpPawns.Add(valid);
                        }
                    }

                    if (tmpPawns.Count == 0)
                    {
                        __result = null;
                        return false;
                    }

                    option = new FloatMenuOption("GoHere".Translate(), () =>
                    {
                        Find.Selector.gotoController.StartInteraction(context.ClickedCell);
                        foreach (Pawn p in tmpPawns)
                        {
                            Find.Selector.gotoController.AddPawn(p);
                        }

                        Find.Selector.gotoController.FinalizeInteraction();
                    }, MenuOptionPriority.GoHere);
                }

                option.isGoto = true;
                option.autoTakeable = true;
                option.autoTakeablePriority = 10f;
                __result = option;
                return false;
            }
        }

        // ==================== 陷阱豁免 ====================

        /// <summary>
        /// Pawn.Flying getter：高维女仆视为飞行单位。
        /// 原版 Building_Trap.Tick 使用 !p.Flying 过滤触发对象，从而天然豁免陷阱。
        /// </summary>
        [HarmonyPatch(typeof(Pawn), "Flying", MethodType.Getter)]
        public static class Patch_Pawn_Flying_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn __instance, ref bool __result)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(__instance))
                {
                    __result = true;
                }
            }
        }

        /// <summary>兜底：直接跳过陷阱触发判定（防其他 Mod 的陷阱不走 Flying 检查）。</summary>
        [HarmonyPatch(typeof(Building_Trap), "CheckSpring")]
        public static class Patch_Building_Trap_CheckSpring_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn p)
            {
                return !ArtificialMaidHighDimUtility.IsHighDim(p);
            }
        }

        // ==================== 单向攻击：AI 不索敌 ====================

        /// <summary>ThreatDisabled 恒为 true：所有 AI/炮塔自动索敌与仇恨回路均不选择高维女仆。</summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.ThreatDisabled))]
        public static class Patch_Pawn_ThreatDisabled_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn __instance, ref bool __result)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(__instance))
                {
                    __result = true;
                }
            }
        }

        // ==================== 单向攻击：规则层无法被瞄准/命中 ====================

        /// <summary>远程射击判定（AI、玩家手动、炮塔）：以高维女仆为目标的射击一律判定为不可命中。</summary>
        [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom), new Type[] { typeof(IntVec3), typeof(LocalTargetInfo) })]
        public static class Patch_Verb_CanHitTargetFrom_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(LocalTargetInfo targ, ref bool __result)
            {
                if (targ.Thing is Pawn pawn && ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    __result = false;
                    return false;
                }

                return true;
            }
        }

        /// <summary>近战/能力通用命中判定：以高维女仆为目标的 Verb 一律不可命中。</summary>
        [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTarget), new Type[] { typeof(LocalTargetInfo) })]
        public static class Patch_Verb_CanHitTarget_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(LocalTargetInfo targ, ref bool __result)
            {
                if (targ.Thing is Pawn pawn && ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    __result = false;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 心灵能力/异象能力施法前校验：以高维女仆为目标的施法一律无效（覆盖 berserk、skip、眩晕等不走伤害的效果）。
        /// 同时 patch 能力统一入口 <see cref="Ability.CanApplyOn"/> 与各效果组件的 Valid，双保险覆盖 override 情况。
        /// </summary>
        [HarmonyPatch(typeof(CompAbilityEffect), nameof(CompAbilityEffect.Valid))]
        public static class Patch_CompAbilityEffect_Valid_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(LocalTargetInfo target, ref bool __result)
            {
                if (target.Thing is Pawn pawn && ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    __result = false;
                    return false;
                }

                return true;
            }
        }

        /// <summary>能力统一施法入口：目标为高维女仆时能力不可施放（玩家与 AI 共用此校验）。</summary>
        [HarmonyPatch(typeof(Ability), nameof(Ability.CanApplyOn), new Type[] { typeof(LocalTargetInfo) })]
        public static class Patch_Ability_CanApplyOn_HighDim
        {
            [HarmonyPrefix]
            public static bool Prefix(LocalTargetInfo target, ref bool __result)
            {
                if (target.Thing is Pawn pawn && ArtificialMaidHighDimUtility.IsHighDim(pawn))
                {
                    __result = false;
                    return false;
                }

                return true;
            }
        }

        // ==================== 视觉：高维幻影（玩家可见的半透明渐变） ====================

        /// <summary>
        /// GetAlpha 下限抬到 HighDimAlphaBase：高维状态下玩家看到半透明幻影而非完全消失，
        /// 同时保留渐出渐入（进入 1→0.2、退出 0.2→1）。
        /// </summary>
        [HarmonyPatch(typeof(HediffComp_Invisibility), nameof(HediffComp_Invisibility.GetAlpha))]
        public static class Patch_HediffComp_Invisibility_GetAlpha_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(HediffComp_Invisibility __instance, ref float __result)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(__instance.Pawn))
                {
                    __result = Mathf.Max(__result, ArtificialMaidHighDimUtility.HighDimAlphaBase);
                }
            }
        }

        /// <summary>
        /// ForcedVisible 强制显形豁免：高维女仆不受倒地/眩晕/燃烧/泡沫覆盖/DisruptorFlash 等
        /// 原版强制显形条件影响（女仆免伤不倒地，主要是防止灭火泡沫覆盖导致的视觉穿帮）。
        /// </summary>
        [HarmonyPatch(typeof(HediffComp_Invisibility), "ForcedVisible", MethodType.Getter)]
        public static class Patch_HediffComp_Invisibility_ForcedVisible_HighDim
        {
            [HarmonyPostfix]
            public static void Postfix(HediffComp_Invisibility __instance, ref bool __result)
            {
                if (ArtificialMaidHighDimUtility.IsHighDim(__instance.Pawn))
                {
                    __result = false;
                }
            }
        }
    }
}
