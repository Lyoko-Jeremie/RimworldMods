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
}
