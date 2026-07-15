using HarmonyLib;
using RimWorld;
using System.Runtime.CompilerServices;
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
        private enum SkyfallerRelation
        {
            Unknown,
            Friendly,
            Hostile
        }

        private sealed class SkyfallerRelationCacheEntry
        {
            public readonly SkyfallerRelation relation;

            public SkyfallerRelationCacheEntry(Skyfaller skyfaller)
            {
                relation = ResolveSkyfallerRelation(skyfaller);
            }
        }

        private static Map cachedMap;
        private static OmniInterceptorTracker cachedTracker;
        private static readonly ConditionalWeakTable<Skyfaller, SkyfallerRelationCacheEntry> relationCache =
            new ConditionalWeakTable<Skyfaller, SkyfallerRelationCacheEntry>();

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

            // Skyfaller 同时覆盖降落和起飞动画。明确属于玩家或非敌对派系的运输物必须放行。
            SkyfallerRelation relation = relationCache.GetValue(
                __instance,
                CreateRelationCacheEntry).relation;
            if (relation == SkyfallerRelation.Friendly)
            {
                return;
            }

            // 敌对和无法确认来源的物体继续采用保守策略拦截。
            if (cachedTracker.IsCellWithinShield(__instance.Position, out var protector))
            {
                if (protector.interceptSkyfallers)
                {
                    RedirectOrDestroy(__instance, protector);
                }
            }
        }

        private static SkyfallerRelationCacheEntry CreateRelationCacheEntry(Skyfaller skyfaller)
        {
            return new SkyfallerRelationCacheEntry(skyfaller);
        }

        private static SkyfallerRelation ResolveSkyfallerRelation(Skyfaller skyfaller)
        {
            SkyfallerRelation directRelation = RelationFromFaction(skyfaller?.Faction);
            if (directRelation != SkyfallerRelation.Unknown)
            {
                return directRelation;
            }

            ThingOwner container = skyfaller?.innerContainer;
            if (container == null || container.Count == 0)
            {
                return SkyfallerRelation.Unknown;
            }

            SkyfallerRelation contentsRelation = SkyfallerRelation.Unknown;
            for (int i = 0; i < container.Count; i++)
            {
                SkyfallerRelation relation = ResolveTransportThingRelation(container[i], true);
                if (relation == SkyfallerRelation.Hostile)
                {
                    return SkyfallerRelation.Hostile;
                }

                if (relation == SkyfallerRelation.Friendly)
                {
                    contentsRelation = SkyfallerRelation.Friendly;
                }
            }

            return contentsRelation;
        }

        private static SkyfallerRelation ResolveTransportThingRelation(Thing thing, bool isCarrier)
        {
            if (thing == null)
            {
                return SkyfallerRelation.Unknown;
            }

            SkyfallerRelation directRelation = RelationFromFaction(thing.Faction);
            if (directRelation != SkyfallerRelation.Unknown)
            {
                return directRelation;
            }

            CompShuttle shuttleComp = thing.TryGetComp<CompShuttle>();
            if (shuttleComp != null && shuttleComp.IsPlayerShuttle)
            {
                return SkyfallerRelation.Friendly;
            }

            if (!(thing is ActiveTransporter transporter) || transporter.Contents == null)
            {
                return SkyfallerRelation.Unknown;
            }

            ActiveTransporterInfo info = transporter.Contents;
            Thing shuttle = info.GetShuttle();
            if (shuttle != null)
            {
                SkyfallerRelation shuttleRelation = ResolveTransportThingRelation(shuttle, true);
                if (shuttleRelation != SkyfallerRelation.Unknown)
                {
                    return shuttleRelation;
                }
            }

            // 原版玩家运输仓在 CompLaunchable.TryLaunch 中记录发射器 Def，
            // 敌袭空投则通过派系的 dropPodIncoming 创建，不设置该字段。
            if (isCarrier && info.sentTransporterDef != null)
            {
                return SkyfallerRelation.Friendly;
            }

            ThingOwner innerContainer = info.innerContainer;
            if (innerContainer == null)
            {
                return SkyfallerRelation.Unknown;
            }

            SkyfallerRelation contentsRelation = SkyfallerRelation.Unknown;
            for (int i = 0; i < innerContainer.Count; i++)
            {
                Thing containedThing = innerContainer[i];
                if (containedThing == shuttle)
                {
                    continue;
                }

                SkyfallerRelation relation = ResolveTransportThingRelation(containedThing, false);
                if (relation == SkyfallerRelation.Hostile)
                {
                    return SkyfallerRelation.Hostile;
                }

                if (relation == SkyfallerRelation.Friendly)
                {
                    contentsRelation = SkyfallerRelation.Friendly;
                }
            }

            return contentsRelation;
        }

        private static SkyfallerRelation RelationFromFaction(Faction faction)
        {
            if (faction == null)
            {
                return SkyfallerRelation.Unknown;
            }

            return faction.HostileTo(Faction.OfPlayer)
                ? SkyfallerRelation.Hostile
                : SkyfallerRelation.Friendly;
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
