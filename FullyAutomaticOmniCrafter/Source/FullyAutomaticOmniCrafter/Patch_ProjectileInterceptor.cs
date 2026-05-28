using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{

    [HarmonyPatch(typeof(OrbitalStrike), "Tick")]
    public static class Patch_OrbitalStrike_Tick
    {
        public static void Postfix(OrbitalStrike __instance)
        {
            if (!__instance.Spawned || __instance.Map == null) return;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            float radius = 0f;
            if (__instance is PowerBeam) radius = 15f;
            else if (__instance is Bombardment b) radius = b.impactAreaRadius + 8f; // 加上爆炸半径

            if (tracker.IsAreaProtected(__instance.Position, radius, __instance.instigator, out _))
            {
                __instance.Destroy();
            }
        }
    }


    [HarmonyPatch(typeof(Verb), "CanHitTargetFrom")]
    public static class Patch_Verb_LaunchProjectile_CanHitTargetFrom
    {
        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || !(__instance is Verb_LaunchProjectile) || __instance.caster?.Map == null) return;

            var tracker = __instance.caster.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 如果攻击者是敌人，且目标位置受保护，拦截
            if (tracker.IsTargetProtected(targ.Thing, __instance.caster) || 
                (targ.HasThing == false && tracker.IsCellProtected(targ.Cell, __instance.caster, out _)))
            {
                __result = false;
                return;
            }

            // 如果攻击者自己就在受保护位置（敌方护盾内），也不能向外射击
            if (tracker.IsCellProtected(root, __instance.caster, out _))
            {
                __result = false;
                return;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (__result >= 10000f || pawn?.Map == null) return;

            var tracker = pawn.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker != null && tracker.IsCellProtected(c, pawn, out _))
            {
                __result = 10000f;
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "BestAttackTarget")]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        public static void Postfix(ref IAttackTarget __result, IAttackTargetSearcher searcher)
        {
            if (searcher?.Thing?.Map == null) return;

            var tracker = searcher.Thing.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 如果已经有了目标，检查目标是否受保护（如果是敌人在攻击，则拦截）
            if (__result != null && tracker.IsTargetProtected(__result.Thing, searcher.Thing))
            {
                __result = null;
            }

            // 额外逻辑：如果攻击者自己就在某个敌方护盾内，他也不能攻击任何人
            if (tracker.IsCellProtected(searcher.Thing.Position, searcher.Thing, out _))
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(Explosion), "StartExplosion")]
    public static class Patch_Explosion_StartExplosion
    {
        public static bool Prefix(Explosion __instance)
        {
            if (__instance.Map == null) return true;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return true;

            // 如果爆炸范围涉及受保护区域（且来源是敌人/无主），则抑制
            if (tracker.IsAreaProtected(__instance.Position, __instance.radius, __instance.instigator, out _))
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Skyfaller), "Tick")]
    public static class Patch_Skyfaller_Tick
    {
        public static void Postfix(Skyfaller __instance)
        {
            if (!__instance.Spawned || __instance.Map == null) return;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 检查落点是否受保护
            if (tracker.IsCellProtected(__instance.Position, null, out var protector))
            {
                if (protector.interceptSkyfallers)
                {
                    RedirectOrDestroy(__instance, protector);
                }
            }
        }

        private static void RedirectOrDestroy(Skyfaller skyfaller, CompOmniProjectileInterceptor protector)
        {
            Map map = skyfaller.Map;
            float radius = protector.Radius;
            IntVec3 center = protector.parent.Position;

            // 尝试在护盾半径外寻找最近的有效空地
            // 简单逻辑：沿着从中心到落点的向量往外推，直到超出半径
            Vector3 direction = (skyfaller.Position - center).ToVector3();
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();

            IntVec3 targetCell = center + (direction * (radius + 2f)).ToIntVec3();

            // 如果超出地图边界，尝试在地图边缘寻找
            if (!targetCell.InBounds(map))
            {
                targetCell = skyfaller.Position; // 重置，准备进行更广泛的搜索
            }

            // 在目标位置附近寻找最近的落点
            var trackerLocal = map.GetComponent<OmniInterceptorTracker>();
            if (CellFinder.TryFindRandomCellNear(targetCell, map, Mathf.CeilToInt(radius) + 10,
                    c => c.InBounds(map) && (trackerLocal == null || !trackerLocal.IsCellProtected(c, null, out _)), out var foundCell))
            {
                skyfaller.Position = foundCell;
                Messages.Message("OmniInterceptor_SkyfallerRedirected".Translate(), skyfaller, MessageTypeDefOf.NeutralEvent);
            }
            else
            {
                // 实在找不到地方落（可能是全图盾），直接销毁
                Messages.Message("OmniInterceptor_SkyfallerDestroyed".Translate(skyfaller.LabelCap), MessageTypeDefOf.CautionInput);
                skyfaller.Destroy();
            }
        }
    }

}