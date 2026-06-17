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
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static void Postfix(OrbitalStrike __instance)
        {
            if (!__instance.Spawned) return;
            Map map = __instance.Map;
            if (map == null) return;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return;

            float radius = 0f;
            if (__instance is PowerBeam) radius = 15f;
            else if (__instance is Bombardment b) radius = b.impactAreaRadius + 8f; // 加上爆炸半径

            if (cachedTracker.IsAreaProtected(__instance.Position, radius, __instance.instigator, out _))
            {
                __instance.Destroy();
            }
        }
    }


    [HarmonyPatch(typeof(Verb), "CanHitTargetFrom")]
    public static class Patch_Verb_LaunchProjectile_CanHitTargetFrom
    {
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || !(__instance is Verb_LaunchProjectile)) return;
            
            Thing caster = __instance.caster;
            if (caster == null) return;
            Map map = caster.Map;
            if (map == null) return;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return;

            // 如果攻击者是敌人，且目标位置受保护，拦截
            if (cachedTracker.IsTargetProtected(targ.Thing, caster) || 
                (targ.HasThing == false && cachedTracker.IsCellProtected(targ.Cell, caster, out _)))
            {
                __result = false;
                return;
            }

            // 如果攻击者自己就在受保护位置（敌方护盾内），也不能向外射击
            if (cachedTracker.IsCellProtected(root, caster, out _))
            {
                __result = false;
                return;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static void Postfix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (__result >= 10000f) return;

            Map map = pawn.Map;
            if (map == null) return;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return;

            if (cachedTracker.IsCellProtected(c, pawn, out _))
            {
                __result = 10000f;
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "BestAttackTarget")]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static void Postfix(ref IAttackTarget __result, IAttackTargetSearcher searcher)
        {
            if (__result == null) return; // 性能优化：如果没有目标，不需要过滤

            Thing searcherThing = searcher.Thing;
            if (searcherThing == null) return;
            Map map = searcherThing.Map;
            if (map == null) return;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return;

            // 如果已经有了目标，检查目标是否受保护（如果是敌人在攻击，则拦截）
            if (cachedTracker.IsTargetProtected(__result.Thing, searcherThing))
            {
                __result = null;
                return;
            }

            // 额外逻辑：如果攻击者自己就在某个敌方护盾内，他也不能攻击任何人
            if (cachedTracker.IsCellProtected(searcherThing.Position, searcherThing, out _))
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(Explosion), "StartExplosion")]
    public static class Patch_Explosion_StartExplosion
    {
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static bool Prefix(Explosion __instance)
        {
            Map map = __instance.Map;
            if (map == null) return true;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return true;

            // 如果爆炸范围涉及受保护区域（且来源是敌人/无主），则抑制
            if (cachedTracker.IsAreaProtected(__instance.Position, __instance.radius, __instance.instigator, out _))
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Skyfaller), "Tick")]
    public static class Patch_Skyfaller_Tick
    {
        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;

        public static void Postfix(Skyfaller __instance)
        {
            if (!__instance.Spawned) return;
            Map map = __instance.Map;
            if (map == null) return;

            if (map != cachedMap || cachedTracker == null)
            {
                cachedMap = map;
                cachedTracker = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker == null || cachedTracker.allInterceptors.Count == 0) return;

            // 尝试获取派系信息
            Thing searcher = null;
            if (__instance.innerContainer != null && __instance.innerContainer.Count > 0)
            {
                searcher = __instance.innerContainer[0];
                // 如果是 DropPodIncoming，内容物通常在 ActiveTransporter 里
                if (searcher is ActiveTransporter at && at.Contents != null && at.Contents.innerContainer.Count > 0)
                {
                    searcher = at.Contents.innerContainer[0];
                }
            }

            // 检查落点是否受保护
            if (cachedTracker.IsCellProtected(__instance.Position, searcher, out var protector))
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
            if (CellFinder.TryFindRandomCellNear(targetCell, map, Mathf.CeilToInt(radius) + 15,
                    c => c.InBounds(map) 
                         && c.Standable(map) // 必须可以站立（排除墙壁、山岩）
                         && !c.Roofed(map)   // 排除有屋顶的地方（空投仓不应落在屋顶下）
                         && (trackerLocal == null || !trackerLocal.IsCellProtected(c, null, out _)), // 不在任何护盾保护范围内（保守起见，searcher传null检查是否有敌方护盾拦截，或者说不落在任何护盾里）
                    out var foundCell))
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