using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 激光（Verb_ShootBeam）穿墙 + 视觉一致。
    ///
    /// 已核实：GenSight.LastPointOnLineOfSight 的唯一调用者就是 Verb_ShootBeam 的三处
    ///   • TryGetHitCell —— 求命中格
    ///   • BurstingTick  —— 求光束视觉终点
    ///   • ApplyDamage   —— 求点火位置
    /// 因此只在该方法上单点挂钩，三处结果天然一致，视觉与命中/伤害完全对齐
    /// （伤害本身不经过 LOS 校验，只要命中格落在墙后即可生效）。
    ///
    /// 上下文用 [ThreadStatic] 保存「当前 beam 的射手与目标」，只在 TryCastShot /
    /// BurstingTick 的调用窗口内有效，并额外校验：
    ///   • depth > 0（位于窗口内）
    ///   • tick == Find.TickManager.TicksGame（跨 tick 残留自动失效）
    ///   • MayPierce(caster, target)（射手属于我方且目标不属于我方）
    /// 即使发生异常残留，也不可能放行敌方；爆炸走 GenSight.LineOfSight，与本挂钩点无关。
    /// </summary>
    internal static class PhantomWallBeamContext
    {
        [ThreadStatic] private static Thing caster;
        [ThreadStatic] private static LocalTargetInfo target;
        [ThreadStatic] private static int tick;
        [ThreadStatic] private static int depth;

        internal static void Enter(Thing beamCaster, LocalTargetInfo beamTarget)
        {
            if (depth++ == 0)
            {
                caster = beamCaster;
                target = beamTarget;
                tick = Find.TickManager.TicksGame;
            }
        }

        internal static void Exit()
        {
            if (depth > 0 && --depth == 0)
            {
                caster = null;
                target = default(LocalTargetInfo);
                tick = -1;
            }
        }

        internal static bool TryGet(out Thing outCaster, out LocalTargetInfo outTarget, out Map outMap)
        {
            outCaster = caster;
            outTarget = target;
            outMap = caster != null ? caster.Map : null;

            return depth > 0
                && outCaster != null
                && outMap != null
                && tick == Find.TickManager.TicksGame
                && PhantomWallCombatRules.MayPierce(outCaster, outTarget);
        }
    }

    /// <summary>Verb_ShootBeam 单次射击窗口（TryGetHitCell / ApplyDamage 都在其调用栈内）。</summary>
    [HarmonyPatch(typeof(Verb_ShootBeam), "TryCastShot")]
    internal static class Patch_VerbShootBeam_TryCastShot_PhantomWindow
    {
        internal static void Prefix(Verb_ShootBeam __instance)
            => PhantomWallBeamContext.Enter(__instance.caster, __instance.CurrentTarget);

        internal static void Postfix()
            => PhantomWallBeamContext.Exit();

        internal static Exception Finalizer(Exception __exception)
        {
            PhantomWallBeamContext.Exit();
            return __exception;
        }
    }

    /// <summary>Verb_ShootBeam 光束推进窗口。</summary>
    [HarmonyPatch(typeof(Verb_ShootBeam), nameof(Verb_ShootBeam.BurstingTick))]
    internal static class Patch_VerbShootBeam_BurstingTick_PhantomWindow
    {
        internal static void Prefix(Verb_ShootBeam __instance)
            => PhantomWallBeamContext.Enter(__instance.caster, __instance.CurrentTarget);

        internal static void Postfix()
            => PhantomWallBeamContext.Exit();

        internal static Exception Finalizer(Exception __exception)
        {
            PhantomWallBeamContext.Exit();
            return __exception;
        }
    }

    /// <summary>
    /// 激光视线判定：处于我方穿墙窗口时，幻影墙格不算视线阻挡。
    /// </summary>
    [HarmonyPatch(typeof(GenSight), nameof(GenSight.LastPointOnLineOfSight))]
    internal static class Patch_GenSight_LastPointOnLineOfSight_PhantomWall
    {
        internal static bool Prefix(
            IntVec3 start,
            IntVec3 end,
            Func<IntVec3, bool> validator,
            bool skipFirstCell,
            ref IntVec3 __result)
        {
            if (validator == null)
                return true;

            Thing caster;
            LocalTargetInfo target;
            Map map;
            if (!PhantomWallBeamContext.TryGet(out caster, out target, out map))
                return true;

            __result = LastVisibleCellThrough(start, end, validator, skipFirstCell, map);
            return false;
        }

        /// <summary>
        /// 逐行复刻 GenSight.PointsOnLineOfSight 的遍历与 LastPointOnLineOfSight 的语义，
        /// 唯一区别：幻影墙格不算视线阻挡。使用局部变量，零堆分配。
        /// </summary>
        private static IntVec3 LastVisibleCellThrough(
            IntVec3 start,
            IntVec3 end,
            Func<IntVec3, bool> validator,
            bool skipFirstCell,
            Map map)
        {
            bool sideOnEqual = start.x != end.x ? start.x < end.x : start.z < end.z;
            int num1 = Mathf.Abs(end.x - start.x);
            int num2 = Mathf.Abs(end.z - start.z);
            int x = start.x;
            int z = start.z;
            int num3 = 1 + num1 + num2;
            int num4 = end.x > start.x ? 1 : -1;
            int num5 = end.z > start.z ? 1 : -1;
            int num6 = num1 - num2;
            int num7 = num1 * 2;
            int num8 = num2 * 2;

            IntVec3 c = default(IntVec3);
            for (; num3 > 0; --num3)
            {
                c.x = x;
                c.z = z;

                if (!skipFirstCell || c != start)
                {
                    if (c == end)
                        return end;

                    if (!validator(c) && !PhantomWallCombatRules.IsPhantomWallAt(c, map))
                        return c;
                }

                if (num6 > 0 || (num6 == 0 && sideOnEqual))
                {
                    x += num4;
                    num6 -= num8;
                }
                else
                {
                    z += num5;
                    num6 += num7;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
