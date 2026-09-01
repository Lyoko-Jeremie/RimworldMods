using HarmonyLib;
using RimWorld;
using System.Runtime.CompilerServices;
using System.Threading;
using Verse;
using UnityEngine;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{

    [HarmonyPatch(typeof(OrbitalStrike), "Tick")]
    public static class Patch_OrbitalStrike_Tick
    {
        // ThreadLocal：每线程独立缓存，无锁无竞争。patch 可能被任意线程调用（tick/其他 Mod 线程）。
        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();

        public static void Postfix(OrbitalStrike __instance)
        {
            if (!__instance.Spawned) return;
            Map map = __instance.Map;
            if (map == null) return;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return;

            float radius = 0f;
            if (__instance is PowerBeam) radius = 15f;
            else if (__instance is Bombardment b) radius = b.impactAreaRadius + 8f; // 加上爆炸半径

            if (cachedTracker.Value.IsAreaProtected(__instance.Position, radius, __instance.instigator, out _))
            {
                __instance.Destroy();
            }
        }
    }


    [HarmonyPatch(typeof(Verb), "CanHitTargetFrom")]
    public static class Patch_Verb_LaunchProjectile_CanHitTargetFrom
    {
        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();

        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || !(__instance is Verb_LaunchProjectile)) return;
            
            Thing caster = __instance.caster;
            if (caster == null) return;
            Map map = caster.Map;
            if (map == null) return;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return;

            // 如果攻击者是敌人，且目标位置受保护，拦截
            if (cachedTracker.Value.IsTargetProtected(targ.Thing, caster) || 
                (targ.HasThing == false && cachedTracker.Value.IsCellProtected(targ.Cell, caster, out _)))
            {
                __result = false;
                return;
            }

            // 如果攻击者自己就在受保护位置（敌方护盾内），也不能向外射击
            if (cachedTracker.Value.IsCellProtected(root, caster, out _))
            {
                __result = false;
                return;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();

        public static void Postfix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (__result >= 10000f) return;

            Map map = pawn.Map;
            if (map == null) return;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return;

            if (cachedTracker.Value.IsCellProtected(c, pawn, out _))
            {
                __result = 10000f;
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "BestAttackTarget")]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();

        public static void Postfix(ref IAttackTarget __result, IAttackTargetSearcher searcher)
        {
            if (__result == null) return; // 性能优化：如果没有目标，不需要过滤

            Thing searcherThing = searcher.Thing;
            if (searcherThing == null) return;
            Map map = searcherThing.Map;
            if (map == null) return;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return;

            // 如果已经有了目标，检查目标是否受保护（如果是敌人在攻击，则拦截）
            if (cachedTracker.Value.IsTargetProtected(__result.Thing, searcherThing))
            {
                __result = null;
                return;
            }

            // 额外逻辑：如果攻击者自己就在某个敌方护盾内，他也不能攻击任何人
            if (cachedTracker.Value.IsCellProtected(searcherThing.Position, searcherThing, out _))
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(Explosion), "StartExplosion")]
    public static class Patch_Explosion_StartExplosion
    {
        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();

        public static bool Prefix(Explosion __instance)
        {
            Map map = __instance.Map;
            if (map == null) return true;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return true;

            // 如果爆炸范围涉及受保护区域（且来源是敌人/无主），则抑制
            if (cachedTracker.Value.IsAreaProtected(__instance.Position, __instance.radius, __instance.instigator, out _))
            {
                return false;
            }

            return true;
        }
    }

    internal enum OmniSkyfallerOrigin
    {
        Unknown,
        Friendly,
        Hostile,
        BenignUnowned,
        HazardousUnowned
    }

    /// <summary>
    /// 记录原版 Thing/Faction 无法表达的投送来源。弱引用不会延长货物或 Pawn 的生命周期。
    /// </summary>
    internal static class OmniSkyfallerOriginRegistry
    {
        private sealed class OriginEntry
        {
            public OmniSkyfallerOrigin origin;
        }

        private static readonly ConditionalWeakTable<Thing, OriginEntry> origins =
            new ConditionalWeakTable<Thing, OriginEntry>();

        public static void Mark(Thing thing, OmniSkyfallerOrigin origin)
        {
            if (thing == null || origin == OmniSkyfallerOrigin.Unknown)
            {
                return;
            }

            origins.GetValue(thing, CreateEntry).origin = origin;
        }

        public static bool TryGet(Thing thing, out OmniSkyfallerOrigin origin)
        {
            if (thing != null && origins.TryGetValue(thing, out OriginEntry entry))
            {
                origin = entry.origin;
                return origin != OmniSkyfallerOrigin.Unknown;
            }

            origin = OmniSkyfallerOrigin.Unknown;
            return false;
        }

        private static OriginEntry CreateEntry(Thing thing)
        {
            return new OriginEntry();
        }
    }

    /// <summary>轨道商船交付没有阵营字段，必须在权威入口记录为合法无主投送。</summary>
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.SpawnDropPod))]
    public static class Patch_TradeUtility_SpawnDropPod
    {
        public static void Prefix(Thing t)
        {
            OmniSkyfallerOriginRegistry.Mark(t, OmniSkyfallerOrigin.BenignUnowned);
        }
    }

    /// <summary>任务空投可能没有阵营；在任务信号实际触发时记录其语义来源。</summary>
    [HarmonyPatch(typeof(QuestPart_DropPods), nameof(QuestPart_DropPods.Notify_QuestSignalReceived))]
    public static class Patch_QuestPart_DropPods_Notify_QuestSignalReceived
    {
        public static void Prefix(QuestPart_DropPods __instance, Signal signal)
        {
            if (signal.tag != __instance.inSignal)
            {
                return;
            }

            OmniSkyfallerOrigin origin;
            if (__instance.joinPlayer || __instance.makePrisoners)
            {
                origin = OmniSkyfallerOrigin.Friendly;
            }
            else if (__instance.faction != null)
            {
                origin = __instance.faction.HostileTo(Faction.OfPlayer)
                    ? OmniSkyfallerOrigin.Hostile
                    : OmniSkyfallerOrigin.Friendly;
            }
            else
            {
                origin = OmniSkyfallerOrigin.BenignUnowned;
            }

            foreach (Thing thing in __instance.Things)
            {
                OmniSkyfallerOriginRegistry.Mark(thing, origin);
            }
        }
    }

    [HarmonyPatch(typeof(Skyfaller), "Tick")]
    public static class Patch_Skyfaller_Tick
    {
        private enum SkyfallerRelation
        {
            Pending,
            Friendly,
            Hostile,
            BenignUnowned,
            HazardousUnowned,
            Ambiguous
        }

        private sealed class SkyfallerRelationCacheEntry
        {
            public SkyfallerRelation relation = SkyfallerRelation.Pending;
            public int nextRetryAge;
            public int retryInterval = 1;
            public bool terminal;
        }

        // 只给可能被其他 Mod 延迟初始化的对象一个很短观察窗口；落点仍有充足时间被重定向。
        private const int PendingGraceTicks = 8;

        private static readonly ThreadLocal<Map> cachedMap = new ThreadLocal<Map>();
        private static readonly ThreadLocal<OmniInterceptorTracker> cachedTracker = new ThreadLocal<OmniInterceptorTracker>();
        private static readonly ConditionalWeakTable<Skyfaller, SkyfallerRelationCacheEntry> relationCache =
            new ConditionalWeakTable<Skyfaller, SkyfallerRelationCacheEntry>();

        public static void Postfix(Skyfaller __instance)
        {
            if (!__instance.Spawned) return;
            Map map = __instance.Map;
            if (map == null) return;

            if (map != cachedMap.Value || cachedTracker.Value == null)
            {
                cachedMap.Value = map;
                cachedTracker.Value = map.GetComponent<OmniInterceptorTracker>();
            }

            if (cachedTracker.Value == null || cachedTracker.Value.allInterceptors.Count == 0) return;

            // Skyfaller 同时覆盖降落和起飞动画。明确属于玩家或非敌对派系的运输物必须放行。
            SkyfallerRelation relation = GetCachedRelation(__instance);
            if (relation == SkyfallerRelation.Pending
                || relation == SkyfallerRelation.Friendly
                || relation == SkyfallerRelation.BenignUnowned)
            {
                return;
            }

            // 敌对、危险无主物和最终仍无法确认来源的物体采用保守策略拦截。
            if (cachedTracker.Value.IsCellWithinShield(__instance.Position, out var protector))
            {
                if (protector.interceptSkyfallers)
                {
                    RedirectOrDestroy(__instance, protector);
                }
            }
        }

        private static SkyfallerRelationCacheEntry CreateRelationCacheEntry(Skyfaller skyfaller)
        {
            return new SkyfallerRelationCacheEntry();
        }

        private static SkyfallerRelation GetCachedRelation(Skyfaller skyfaller)
        {
            SkyfallerRelationCacheEntry entry = relationCache.GetValue(
                skyfaller,
                CreateRelationCacheEntry);
            if (entry.terminal)
            {
                return entry.relation;
            }

            if (skyfaller.ageTicks < entry.nextRetryAge)
            {
                return SkyfallerRelation.Pending;
            }

            SkyfallerRelation resolved = ResolveSkyfallerRelation(skyfaller);
            if (resolved == SkyfallerRelation.Pending || resolved == SkyfallerRelation.Ambiguous)
            {
                if (skyfaller.ageTicks < PendingGraceTicks)
                {
                    entry.relation = SkyfallerRelation.Pending;
                    entry.nextRetryAge = skyfaller.ageTicks + entry.retryInterval;
                    entry.retryInterval = entry.retryInterval < 4 ? entry.retryInterval * 2 : 4;
                    return SkyfallerRelation.Pending;
                }

                resolved = SkyfallerRelation.Ambiguous;
            }

            entry.relation = resolved;
            entry.terminal = true;
            return resolved;
        }

        private static SkyfallerRelation ResolveSkyfallerRelation(Skyfaller skyfaller)
        {
            // reversed 表示离开地图；任何离场动画都不应被防空护盾重定向。
            if (skyfaller?.def?.skyfaller != null && skyfaller.def.skyfaller.reversed)
            {
                return SkyfallerRelation.BenignUnowned;
            }

            SkyfallerRelation directRelation = RelationFromFaction(skyfaller?.Faction);
            if (directRelation != SkyfallerRelation.Ambiguous)
            {
                return directRelation;
            }

            ThingOwner container = skyfaller?.innerContainer;
            if (container == null || container.Count == 0)
            {
                SkyfallerRelation emptyDefRelation = RelationFromFactionDropPodDef(skyfaller?.def, true);
                if (emptyDefRelation != SkyfallerRelation.Ambiguous)
                {
                    return emptyDefRelation;
                }

                return skyfaller is IActiveTransporter
                    ? SkyfallerRelation.Pending
                    : SkyfallerRelation.HazardousUnowned;
            }

            SkyfallerRelation contentsRelation = SkyfallerRelation.Ambiguous;
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
                else if (relation == SkyfallerRelation.BenignUnowned
                         && contentsRelation == SkyfallerRelation.Ambiguous)
                {
                    contentsRelation = SkyfallerRelation.BenignUnowned;
                }
            }

            if (contentsRelation != SkyfallerRelation.Ambiguous)
            {
                return contentsRelation;
            }

            SkyfallerRelation defRelation = RelationFromFactionDropPodDef(skyfaller?.def, true);
            if (defRelation != SkyfallerRelation.Ambiguous)
            {
                return defRelation;
            }

            // 原版的正常 ShuttleIncoming 不承担敌袭；显式敌对阵营已优先判定，载荷可能是俘虏，不能作为敌意依据。
            if (skyfaller is ShuttleIncoming)
            {
                return SkyfallerRelation.BenignUnowned;
            }

            return skyfaller is IActiveTransporter
                ? SkyfallerRelation.Ambiguous
                : SkyfallerRelation.HazardousUnowned;
        }

        private static SkyfallerRelation ResolveTransportThingRelation(Thing thing, bool isCarrier)
        {
            if (thing == null)
            {
                return SkyfallerRelation.Ambiguous;
            }

            if (OmniSkyfallerOriginRegistry.TryGet(thing, out OmniSkyfallerOrigin origin))
            {
                return RelationFromOrigin(origin);
            }

            SkyfallerRelation directRelation = RelationFromFaction(thing.Faction);
            if (directRelation != SkyfallerRelation.Ambiguous)
            {
                return directRelation;
            }

            CompShuttle shuttleComp = thing.TryGetComp<CompShuttle>();
            if (shuttleComp != null)
            {
                // requiredPawns/实际载荷可能包含待押送的敌对俘虏，不能据此把穿梭机判为敌对。
                return shuttleComp.IsPlayerShuttle || shuttleComp.permitShuttle
                    ? SkyfallerRelation.Friendly
                    : SkyfallerRelation.Ambiguous;
            }

            if (!(thing is ActiveTransporter transporter) || transporter.Contents == null)
            {
                return SkyfallerRelation.Ambiguous;
            }

            ActiveTransporterInfo info = transporter.Contents;
            Thing shuttle = info.GetShuttle();
            if (shuttle != null)
            {
                SkyfallerRelation shuttleRelation = ResolveTransportThingRelation(shuttle, true);
                if (shuttleRelation != SkyfallerRelation.Ambiguous
                    && shuttleRelation != SkyfallerRelation.Pending)
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
                return SkyfallerRelation.Pending;
            }

            SkyfallerRelation contentsRelation = SkyfallerRelation.Ambiguous;
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
                else if (relation == SkyfallerRelation.BenignUnowned
                         && contentsRelation == SkyfallerRelation.Ambiguous)
                {
                    contentsRelation = SkyfallerRelation.BenignUnowned;
                }
            }

            if (contentsRelation != SkyfallerRelation.Ambiguous)
            {
                return contentsRelation;
            }

            return RelationFromFactionDropPodDef(thing.def, false);
        }

        private static SkyfallerRelation RelationFromOrigin(OmniSkyfallerOrigin origin)
        {
            switch (origin)
            {
                case OmniSkyfallerOrigin.Friendly:
                    return SkyfallerRelation.Friendly;
                case OmniSkyfallerOrigin.Hostile:
                    return SkyfallerRelation.Hostile;
                case OmniSkyfallerOrigin.BenignUnowned:
                    return SkyfallerRelation.BenignUnowned;
                case OmniSkyfallerOrigin.HazardousUnowned:
                    return SkyfallerRelation.HazardousUnowned;
                default:
                    return SkyfallerRelation.Ambiguous;
            }
        }

        private static SkyfallerRelation RelationFromFaction(Faction faction)
        {
            if (faction == null)
            {
                return SkyfallerRelation.Ambiguous;
            }

            return faction.HostileTo(Faction.OfPlayer)
                ? SkyfallerRelation.Hostile
                : SkyfallerRelation.Friendly;
        }

        private static SkyfallerRelation RelationFromFactionDropPodDef(ThingDef thingDef, bool incoming)
        {
            if (thingDef == null || Find.FactionManager == null)
            {
                return SkyfallerRelation.Ambiguous;
            }

            SkyfallerRelation result = SkyfallerRelation.Ambiguous;
            var factions = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < factions.Count; i++)
            {
                Faction faction = factions[i];
                ThingDef factionThingDef = incoming
                    ? faction.def.dropPodIncoming
                    : faction.def.dropPodActive;
                if (factionThingDef != thingDef)
                {
                    continue;
                }

                SkyfallerRelation relation = RelationFromFaction(faction);
                if (relation == SkyfallerRelation.Hostile)
                {
                    return SkyfallerRelation.Hostile;
                }

                if (relation == SkyfallerRelation.Friendly)
                {
                    result = SkyfallerRelation.Friendly;
                }
            }

            return result;
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
