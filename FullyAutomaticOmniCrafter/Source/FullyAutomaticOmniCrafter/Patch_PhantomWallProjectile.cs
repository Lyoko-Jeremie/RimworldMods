using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 敌方/中立投射物「吞弹」与激光视线隔离。
    ///
    /// 关键机制（已核实原版）：
    ///   • Projectile.TickInterval → CheckForFreeInterceptBetween → CheckForFreeIntercept(c)
    ///     —— 每一帧检查飞行路径上的格子；这是唯一入口，且发生在 CanHit 判定之前。
    ///   • Projectile_Explosive 没有覆写 Destroy，爆炸只发生在 Impact / TickInterval 中，
    ///     因此在这里直接 Destroy(Vanish) 可以静默吞掉炮弹：不爆炸、不伤害、不穿墙。
    ///
    /// 行为：
    ///   • 我方发射且目标非我方 → 不吞，交给 CanHit 穿透（由 Projectile_CanHit_Patch 处理）。
    ///   • 其余（敌方、中立、无发射者、我方打我方）→ 在幻影墙格吞掉。
    ///   • 天然覆盖曲射弹（flyOverhead 的 HitFlags 为 None，原版本就不会被任何东西拦下）。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "CheckForFreeIntercept")]
    public static class Patch_Projectile_CheckForFreeIntercept_PhantomWall
    {
        // Projectile.destination 是 protected 字段；缓存一次 FieldRef，之后零反射开销。
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        public static bool Prefix(Projectile __instance, IntVec3 c, ref bool __result)
        {
            Map map = __instance.Map;
            if (map == null)
                return true;

            // 保留原版语义：弹的落点格不参与拦截检查
            if (destinationRef != null && destinationRef(__instance).ToIntVec3() == c)
                return true;

            if (!PhantomWallCombatRules.IsPhantomWallAt(c, map))
                return true;

            // 我方弹（目标非我方）→ 交回原版继续飞行，由 CanHit 补丁放行穿墙
            if (PhantomWallCombatRules.MayPierce(__instance.Launcher, __instance.intendedTarget))
                return true;

            // 吞掉：直接销毁，不触发 Impact / Explode（无爆炸、无伤害）
            __instance.Destroy(DestroyMode.Vanish);
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 补漏：当弹「一 tick 位移超过 1 格」且「新位置距发射点 ≤ 5 格」时，
    /// 原版 CheckForFreeInterceptBetween 会直接 return false（跳过全部格子检查），
    /// 于是敌方高速弹可能整体穿过幻影墙而不被处理（既不吞、也不撞墙）。
    /// 这里只在「原版确定会跳过」的情形下，沿线段按 0.2 格采样，补上幻影墙格的判定。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "CheckForFreeInterceptBetween")]
    public static class Patch_Projectile_CheckForFreeInterceptBetween_PhantomWall
    {
        // Projectile.origin 是 protected 字段；缓存一次 FieldRef，之后零反射开销。
        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");

        public static bool Prefix(Projectile __instance, Vector3 lastExactPos, Vector3 newExactPos, ref bool __result)
        {
            Map map = __instance.Map;
            if (map == null)
                return true;

            IntVec3 from = lastExactPos.ToIntVec3();
            IntVec3 to = newExactPos.ToIntVec3();

            // 这些情形原版会自行逐格检查，交回原版处理
            if (to == from || !from.InBounds(map) || !to.InBounds(map))
                return true;
            if (to.AdjacentToCardinal(from))
                return true;
            if (originRef == null)
                return true;
            if (VerbUtility.InterceptChanceFactorFromDistance(originRef(__instance), to) > 0f)
                return true;

            // 走到这里说明原版会直接跳过检查：仅补上“路径经过幻影墙格”这一种情况
            Vector3 delta = newExactPos - lastExactPos;
            Vector3 step = delta.normalized * 0.2f;
            int steps = (int)(delta.MagnitudeHorizontal() / 0.2f);
            Vector3 pos = lastExactPos;

            for (int i = 0; i <= steps; i++)
            {
                pos += step;
                IntVec3 cell = pos.ToIntVec3();
                if (!cell.InBounds(map) || !PhantomWallCombatRules.IsPhantomWallAt(cell, map))
                    continue;

                // 我方弹（目标非我方）继续飞，由 CanHit 补丁放行穿墙
                if (PhantomWallCombatRules.MayPierce(__instance.Launcher, __instance.intendedTarget))
                    continue;

                __instance.Destroy(DestroyMode.Vanish);
                __result = true;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 落点结算加固：非我方弹的落点恰好是幻影墙格时，静默吞掉整发弹。
    ///
    /// 原版 CheckForFreeIntercept 会显式跳过「落点格」（见该方法第一行判断），
    /// 因此落点正好压在幻影墙上的敌弹不会被吞；原版随后会把它当"撞墙"处理
    /// （爆炸弹则在墙格引爆）。这里补上按格判定，使「进入/穿过幻影墙格」这一条
    /// 在落点情形下也成立，且不留下墙格爆炸。
    ///
    /// 我方弹保持原版落点语义：脱靶落在墙上就是打空，不做越墙修正。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "ImpactSomething")]
    public static class Patch_Projectile_ImpactSomething_PhantomWall
    {
        // 【临时诊断】定位「我方弹停在墙上」用；最多打印 40 条，定位完成后可整段删除。
        private static int diagCount;

        public static bool Prefix(Projectile __instance)
        {
            Map map = __instance.Map;
            if (map == null)
                return true;

            if (!PhantomWallCombatRules.IsPhantomWallAt(__instance.Position, map))
                return true;

            bool mayPierce = PhantomWallCombatRules.MayPierce(__instance.Launcher, __instance.intendedTarget);

            if (diagCount < 40)
            {
                diagCount++;
                Log.Message(
                    $"[PhantomWallDiag] ImpactSomething on wall cell: mayPierce={mayPierce}, " +
                    $"launcher={__instance.Launcher?.ToStringSafe() ?? "null"}, " +
                    $"launcherFaction={__instance.Launcher?.Faction?.ToStringSafe() ?? "null"}, " +
                    $"intendedTarget={__instance.intendedTarget}, " +
                    $"usedTarget={__instance.usedTarget}, pos={__instance.Position}");
            }

            // 我方弹（目标非我方）→ 保持原版落点行为（脱靶落在墙上即打空）
            if (mayPierce)
                return true;

            // 敌方 / 中立 / 无主 / 我方打我方 → 静默吞掉，避免在墙格引爆
            __instance.Destroy(DestroyMode.Vanish);
            return false;
        }
    }

    /// <summary>
    /// 脱靶落点修正：让幻影墙不参与「两格是否互相可见」的判定。
    ///
    /// 这是「我方射击时子弹有概率落到墙上、穿不过去」的第二条来源（第一条是掩体，
    /// 已由 Patch_CoverUtility_BaseBlockChance_PhantomWall 修掉）：
    ///   • Verb_LaunchProjectile.TryCastShot 的脱靶分支（ToWild）会调用
    ///     ShootLine.ChangeDestToMissWild 重新选择一个脱靶落点；
    ///   • 该方法先判定 ShootLeanUtility.CellCanSeeCell(source, dest, map)，
    ///     若为 false，就沿射手到脱靶点的路径逐步推进落点，遇到第一个
    ///     `Filled(map)` 的格子（Fillage == Full，即幻影墙）就停下 ——
    ///     于是脱靶弹的落点被"吸附"到幻影墙上，表现为子弹在墙上消失、穿不过去。
    ///   • CellCanSeeCell 实质只有两个调用者（ChangeDestToMissWild 与一个 Dev 绘制工具），
    ///     因此在这里忽略幻影墙是安全的。
    ///
    /// 修正后：幻影墙对"脱靶落点可见性"是透明的 → 落点保持原版的随机脱靶点，
    /// 不再被吸附到墙上；普通墙 / 岩石的吸附行为保持不变。
    /// </summary>
    [HarmonyPatch(typeof(ShootLeanUtility), nameof(ShootLeanUtility.CellCanSeeCell))]
    public static class Patch_ShootLeanUtility_CellCanSeeCell_PhantomWall
    {
        private static readonly List<IntVec3> tempSourceList = new List<IntVec3>();
        private static readonly List<IntVec3> tempDestList = new List<IntVec3>();

        public static bool Prefix(IntVec3 source, IntVec3 dest, Map map, ref bool __result)
        {
            if (map == null)
                return true;

            // 与原版一致：出界、或存在「非幻影墙」的视线阻挡时，直接判定不可见
            if (!source.InBounds(map) || !dest.InBounds(map))
            {
                __result = false;
                return false;
            }
            if (!CanBeSeenOverIgnoringPhantomWall(source, map) || !CanBeSeenOverIgnoringPhantomWall(dest, map))
            {
                __result = false;
                return false;
            }

            ShootLeanUtility.LeanShootingSourcesFromTo(dest, source, map, tempDestList);
            for (int i = 0; i < tempDestList.Count; i++)
            {
                if (PhantomWallSightUtility.LineOfSight(source, dest, map, true))
                {
                    __result = true;
                    return false;
                }
            }

            ShootLeanUtility.LeanShootingSourcesFromTo(source, dest, map, tempSourceList);
            for (int i = 0; i < tempSourceList.Count; i++)
            {
                for (int j = 0; j < tempDestList.Count; j++)
                {
                    if (PhantomWallSightUtility.LineOfSight(tempSourceList[i], tempDestList[j], map, true))
                    {
                        __result = true;
                        return false;
                    }
                }
            }

            __result = false;
            return false;
        }

        /// <summary>CanBeSeenOver 的等价判定，但幻影墙格视为可见。</summary>
        private static bool CanBeSeenOverIgnoringPhantomWall(IntVec3 c, Map map)
            => c.CanBeSeenOver(map) || PhantomWallCombatRules.IsPhantomWallAt(c, map);
    }

    /// <summary>
    /// 落点修正：让幻影墙对「我方弹的落点」当作不存在。
    ///
    /// 原版 Verb_LaunchProjectile.TryCastShot 有两条会把落点写到幻影墙格上的路：
    ///   1) ToWild 脱靶 —— ShootLine.ChangeDestToMissWild 会沿 source→脱靶点的路径推进落点，
    ///      遇到第一个满格建筑（Fillage == Full，幻影墙就是）就停下 break；
    ///      这一条已由 Patch_ShootLeanUtility_CellCanSeeCell_PhantomWall 关闭（不再吸附）。
    ///   2) 脱靶点 / forcedMiss 散布点本身就落在幻影墙格里 —— 这是纯随机坐标的结果，
    ///      落点计算层面已经没有"墙"可去掉，只能在发射瞬间把落点沿弹道推到墙后第一格，
    ///      这才是「幻影墙对落点完全不存在」的语义（弹从发射起就朝墙后飞，不做事后搬动）。
    ///
    /// 只处理「我方发射且目标非我方」的弹；敌弹 / 中立弹一律保持原版落点（照常被墙拦下）。
    /// 性能：每次发射只执行一次，且仅当落点确实压在幻影墙格时才继续，开销可忽略。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new System.Type[]
    {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
    })]
    internal static class Patch_Projectile_Launch_PhantomWall
    {
        // destination / ticksToImpact / lifetime 都是 protected 字段，缓存 FieldRef 零反射开销。
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static readonly AccessTools.FieldRef<Projectile, int> ticksToImpactRef =
            AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");

        private static readonly AccessTools.FieldRef<Projectile, int> lifetimeRef =
            AccessTools.FieldRefAccess<Projectile, int>("lifetime");

        // 【临时诊断】定位「我方弹停在墙上」用；最多打印 40 条，定位完成后可整段删除。
        private static int diagCount;

        internal static void Postfix(
            Projectile __instance,
            Thing launcher,
            Vector3 origin,
            LocalTargetInfo intendedTarget)
        {
            if (destinationRef == null)
                return;

            Map map = __instance.Map;
            if (map == null)
                return;

            // 只处理我方弹（目标非我方），敌弹 / 中立弹保持原版落点
            if (!PhantomWallCombatRules.MayPierce(launcher, intendedTarget))
                return;

            Vector3 dest = destinationRef(__instance);
            IntVec3 destCell = dest.ToIntVec3();
            if (!destCell.InBounds(map) || !PhantomWallCombatRules.IsPhantomWallAt(destCell, map))
                return;

            // 弹道水平方向
            float dx = dest.x - origin.x;
            float dz = dest.z - origin.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 0.001f)
                return;
            dx /= len;
            dz /= len;

            int stepX = dx > 0.3f ? 1 : (dx < -0.3f ? -1 : 0);
            int stepZ = dz > 0.3f ? 1 : (dz < -0.3f ? -1 : 0);
            if (stepX == 0 && stepZ == 0)
                return;

            IntVec3 candidate = destCell;
            for (int i = 0; i < 6; i++)
            {
                candidate += new IntVec3(stepX, 0, stepZ);
                if (!candidate.InBounds(map))
                    return;
                if (PhantomWallCombatRules.IsPhantomWallAt(candidate, map))
                    continue;

                // 找到墙后第一个非幻影墙格：把落点搬到该格中心，并重算剩余飞行时间
                Vector3 newDest = candidate.ToVector3Shifted();
                destinationRef(__instance) = newDest;
                __instance.usedTarget = new LocalTargetInfo(candidate);

                float speed = __instance.def?.projectile?.SpeedTilesPerTick ?? 0f;
                if (speed > 0f)
                {
                    int ticks = Mathf.Max(1, Mathf.CeilToInt((origin - newDest).magnitude / speed));
                    ticksToImpactRef(__instance) = ticks;
                    if (lifetimeRef != null)
                        lifetimeRef(__instance) = ticks;
                }

                if (diagCount < 40)
                {
                    diagCount++;
                    Log.Message(
                        $"[PhantomWallDiag] Launch moved dest off wall: {destCell} -> {candidate}, " +
                        $"launcher={launcher?.ToStringSafe() ?? "null"}, intendedTarget={intendedTarget}");
                }
                return;
            }
        }
    }
}
