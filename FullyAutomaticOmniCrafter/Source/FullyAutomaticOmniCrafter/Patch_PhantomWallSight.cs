using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 「我方射手 + 非我方目标」的视线放行逻辑。
    ///
    /// 只替换「原版判定失败、且失败原因是幻影墙挡视线」这一种情况：
    /// prefix 里先用可穿幻影墙的实现判定；判定失败就直接交回原版，
    /// 因此除了幻影墙之外的一切阻挡（普通墙、validator、lean 出发点等）都保持原版语义。
    ///
    /// 不使用任何全局状态；爆炸（GenExplosion/DamageWorker）、建造、社交、
    /// AI 危险区等系统仍走原版 LOS，不受影响。
    /// </summary>
    internal static class PhantomWallSightHelper
    {
        private static readonly List<IntVec3> tempDestList = new List<IntVec3>();
        private static readonly List<IntVec3> tempSourceList = new List<IntVec3>();
        private static readonly List<IntVec3> tempLeanSources = new List<IntVec3>();

        /// <summary>
        /// 只有射击类 Verb 参与穿墙放行，避免喷火/喷液/传送等区域效果能力意外穿墙。
        /// Verb_Shoot / Verb_ShootOneUse / Verb_AbilityShoot / Verb_LaunchProjectileStatic*
        /// 都派生自 Verb_LaunchProjectile，因此已被覆盖。
        /// </summary>
        internal static bool IsShootVerb(Verb verb)
            => verb is Verb_LaunchProjectile || verb is Verb_ShootBeam;

        /// <summary>
        /// 等价于 AttackTargetFinder.CanSee，但幻影墙不阻挡视线。
        /// </summary>
        internal static bool CanSeeThrough(Thing seer, Thing target, Func<IntVec3, bool> validator)
        {
            Map map = seer.Map;
            if (map == null || target == null || !target.Spawned)
                return false;

            ShootLeanUtility.CalcShootableCellsOf(tempDestList, target, seer.Position);
            for (int i = 0; i < tempDestList.Count; i++)
            {
                if (PhantomWallSightUtility.LineOfSight(seer.Position, tempDestList[i], map, true, validator))
                    return true;
            }

            ShootLeanUtility.LeanShootingSourcesFromTo(seer.Position, target.Position, map, tempSourceList);
            for (int i = 0; i < tempSourceList.Count; i++)
            {
                if (!tempSourceList[i].CanBeSeenOver(map))
                    continue;

                for (int j = 0; j < tempDestList.Count; j++)
                {
                    if (PhantomWallSightUtility.LineOfSight(tempSourceList[i], tempDestList[j], map, true, validator))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 等价于 Verb.TryFindShootLineFromTo，但幻影墙不阻挡射击线。
        /// </summary>
        internal static bool TryFindShootLineThrough(
            Verb verb,
            IntVec3 root,
            LocalTargetInfo targ,
            bool ignoreRange,
            out ShootLine resultingLine)
        {
            Thing caster = verb.caster;
            Map map = caster != null ? caster.Map : null;
            if (map == null)
            {
                resultingLine = new ShootLine();
                return false;
            }

            if (targ.HasThing && targ.Thing.Map != map)
            {
                resultingLine = new ShootLine();
                return false;
            }

            if (verb.verbProps.IsMeleeAttack || verb.EffectiveRange <= 1.42f)
            {
                resultingLine = new ShootLine(root, targ.Cell);
                return ReachabilityImmediate.CanReachImmediate(root, targ, map, PathEndMode.Touch, null);
            }

            CellRect occupiedRect = targ.HasThing
                ? targ.Thing.OccupiedRect()
                : CellRect.SingleCell(targ.Cell);

            if (!ignoreRange && verb.OutOfRange(root, targ, occupiedRect))
            {
                resultingLine = new ShootLine(root, targ.Cell);
                return false;
            }

            if (!verb.verbProps.requireLineOfSight)
            {
                resultingLine = new ShootLine(root, targ.Cell);
                return true;
            }

            IntVec3 goodDest;
            if (verb.CasterIsPawn)
            {
                if (CanHitFromCellThrough(verb, root, targ, out goodDest))
                {
                    resultingLine = new ShootLine(root, goodDest);
                    return true;
                }

                ShootLeanUtility.LeanShootingSourcesFromTo(root, occupiedRect.ClosestCellTo(root), map, tempLeanSources);
                for (int i = 0; i < tempLeanSources.Count; i++)
                {
                    if (CanHitFromCellThrough(verb, tempLeanSources[i], targ, out goodDest))
                    {
                        resultingLine = new ShootLine(tempLeanSources[i], goodDest);
                        return true;
                    }
                }
            }
            else
            {
                foreach (IntVec3 cell in caster.OccupiedRect())
                {
                    if (CanHitFromCellThrough(verb, cell, targ, out goodDest))
                    {
                        resultingLine = new ShootLine(cell, goodDest);
                        return true;
                    }
                }
            }

            resultingLine = new ShootLine(root, targ.Cell);
            return false;
        }

        /// <summary>等价于 Verb.CanHitFromCellIgnoringRange。</summary>
        private static bool CanHitFromCellThrough(Verb verb, IntVec3 sourceCell, LocalTargetInfo targ, out IntVec3 goodDest)
        {
            if (targ.Thing != null)
            {
                if (targ.Thing.Map != verb.caster.Map)
                {
                    goodDest = IntVec3.Invalid;
                    return false;
                }

                ShootLeanUtility.CalcShootableCellsOf(tempDestList, targ.Thing, sourceCell);
                for (int i = 0; i < tempDestList.Count; i++)
                {
                    if (CanHitCellThrough(verb, sourceCell, tempDestList[i], targ.Thing.def.Fillage == FillCategory.Full))
                    {
                        goodDest = tempDestList[i];
                        return true;
                    }
                }
            }
            else if (CanHitCellThrough(verb, sourceCell, targ.Cell))
            {
                goodDest = targ.Cell;
                return true;
            }

            goodDest = IntVec3.Invalid;
            return false;
        }

        /// <summary>等价于 Verb.CanHitCellFromCellIgnoringRange。</summary>
        private static bool CanHitCellThrough(Verb verb, IntVec3 sourceSq, IntVec3 targetLoc, bool includeCorners = false)
        {
            Map map = verb.caster.Map;
            if (map == null || !targetLoc.InBounds(map))
                return false;

            if (verb.verbProps.mustCastOnOpenGround
                && (!targetLoc.Standable(map) || map.thingGrid.CellContains(targetLoc, ThingCategory.Pawn)))
                return false;

            if (verb.verbProps.requireLineOfSight)
            {
                if (!includeCorners)
                {
                    if (!PhantomWallSightUtility.LineOfSight(sourceSq, targetLoc, map, true))
                        return false;
                }
                else if (!PhantomWallSightUtility.LineOfSightToEdges(sourceSq, targetLoc, map, true))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// AI 索敌视线：我方（pawn AI / 炮塔）穿过幻影墙锁定墙后目标。
    /// 覆盖 JobGiver_AIFightEnemy、JobGiver_ConfigurableHostilityResponse
    /// 以及炮塔 Building_TurretGun.TryFindNewTarget 的判定路径。
    /// </summary>
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee))]
    internal static class Patch_AttackTargetFinder_CanSee_PhantomWall
    {
        internal static bool Prefix(Thing seer, Thing target, Func<IntVec3, bool> validator, ref bool __result)
        {
            if (seer == null || target == null)
                return true;

            // 观察者不是我方，或目标是我方 → 完全走原版（被幻影墙挡住）
            if (!PhantomWallCombatRules.IsOurs(seer) || PhantomWallCombatRules.IsOurs(target))
                return true;

            // 只有「幻影墙是唯一遮挡」时才会判定成功，其余情况交回原版
            if (!PhantomWallSightHelper.CanSeeThrough(seer, target, validator))
                return true;

            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 射击线：我方射击类 Verb 可以隔着幻影墙取得射击线（手动指挥、开火准入、warmup 都走这里）。
    /// </summary>
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    internal static class Patch_Verb_TryFindShootLineFromTo_PhantomWall
    {
        internal static bool Prefix(
            Verb __instance,
            IntVec3 root,
            LocalTargetInfo targ,
            ref ShootLine resultingLine,
            bool ignoreRange,
            ref bool __result)
        {
            Thing caster = __instance.caster;
            if (caster == null || !targ.IsValid)
                return true;

            // 只对我方射击类 Verb 生效（喷火/喷液/传送等区域能力保持原版）
            if (!PhantomWallSightHelper.IsShootVerb(__instance))
                return true;

            if (!PhantomWallCombatRules.IsOurs(caster) || PhantomWallCombatRules.TargetIsOurs(targ))
                return true;

            ShootLine line;
            if (!PhantomWallSightHelper.TryFindShootLineThrough(__instance, root, targ, ignoreRange, out line))
                return true;

            resultingLine = line;
            __result = true;
            return false;
        }
    }
}
