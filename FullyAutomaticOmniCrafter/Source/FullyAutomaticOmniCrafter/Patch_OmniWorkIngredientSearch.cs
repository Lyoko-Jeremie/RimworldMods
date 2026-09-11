using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 制作选料有独立的区域 BFS，不能只修 GenClosest。代理只替换候选收集，
    /// 原料配比、账单限制和超维数量预算仍交给原版传入的选择回调。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
    internal static class Patch_OmniWorkIngredientSearch
    {
        private sealed class Scratch
        {
            internal readonly List<Thing> candidates = new List<Thing>();
            internal readonly List<Thing> held = new List<Thing>();
            internal readonly HashSet<Thing> seen = new HashSet<Thing>();
            internal void Clear() { candidates.Clear(); held.Clear(); seen.Clear(); }
        }

        // 回调可能重入选料，线程局部栈保证每次调用有独立缓冲且不跨线程共享集合。
        [ThreadStatic] private static Stack<Scratch> pool;
        private static readonly Action<Pawn, Thing, List<Thing>, Predicate<Thing>, Map> AddMedicine =
            AccessTools.MethodDelegate<Action<Pawn, Thing, List<Thing>, Predicate<Thing>, Map>>(
                AccessTools.Method(typeof(WorkGiver_DoBill), "AddEveryMedicineToRelevantThings"));

        [HarmonyPrefix]
        internal static bool Prefix(Predicate<Thing> thingValidator,
            Predicate<List<Thing>> foundAllIngredientsAndChoose, List<IngredientCount> ingredients,
            Pawn pawn, Thing billGiver, List<ThingCount> chosen, float searchRadius, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            chosen.Clear();
            __result = false;
            if (!pawn.Spawned || billGiver == null || billGiver.Destroyed || billGiver.Map != pawn.Map)
                return false;
            if (ingredients.Count == 0) { __result = true; return false; }
            if (pool == null) pool = new Stack<Scratch>();
            Scratch scratch = pool.Count > 0 ? pool.Pop() : new Scratch();
            Pawn previous = OuterrealmBillIngredientSelector.CurrentPawn;
            OuterrealmBillIngredientSelector.CurrentPawn = pawn;
            try
            {
                OmniWorkProxyUtility.TryGetStation(pawn, out Building_OmniWorkstation station);
                OmniWorkFailureCache failures = OmniWorkFailureCache.For(pawn);
                float radiusSquared = searchRadius * searchRadius;
                Predicate<Thing> valid = thing => thing != null && !thing.Destroyed &&
                    thing.PositionHeld.InHorDistOf(billGiver.Position, searchRadius) &&
                    (station == null || station.Covers(thing.PositionHeld)) &&
                    !thing.IsForbidden(pawn) && pawn.CanReserve(thing) &&
                    (failures == null || failures.Allows(pawn, null, thing)) && thingValidator(thing);

                // 随身权威候选只读注入，不能在导航或选料阶段 Checkout。
                SubspaceAccessUtility.InjectGlobalEntries(thingValidator, pawn, billGiver, scratch.candidates);
                bool medical = billGiver is Pawn;
                if (medical)
                    AddMedicine(pawn, billGiver, scratch.candidates,
                        thing => thing.Spawned && valid(thing), pawn.Map);
                if (billGiver is Building_WorkTableAutonomous autonomous)
                    scratch.candidates.AddRange(autonomous.innerContainer);
                if (scratch.candidates.Count > 0 && foundAllIngredientsAndChoose(scratch.candidates))
                {
                    __result = true;
                    return false;
                }
                for (int i = 0; i < scratch.candidates.Count; i++) scratch.seen.Add(scratch.candidates[i]);

                List<Thing> ground = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                for (int i = 0; i < ground.Count; i++)
                {
                    Thing thing = ground[i];
                    if (!thing.Spawned || medical && thing.def.IsMedicine || scratch.seen.Contains(thing) ||
                        (thing.Position - billGiver.Position).LengthHorizontalSquared >= radiusSquared ||
                        !valid(thing) || !OmniWorkProxyNavigation.TryFindEnd(pawn, pawn.Map, pawn.Position,
                            thing, PathEndMode.ClosestTouch, out _)) continue;
                    scratch.seen.Add(thing);
                    scratch.candidates.Add(thing);
                }
                foreach (IHaulSource source in pawn.Map.haulDestinationManager.AllHaulSourcesListForReading)
                {
                    if (!source.HaulSourceEnabled || !(source is Thing holder) || !holder.Spawned ||
                        !holder.Position.InHorDistOf(billGiver.Position, searchRadius) || holder.IsForbidden(pawn) ||
                        station != null && !station.Covers(holder.Position) ||
                        !OmniWorkProxyNavigation.TryFindEnd(pawn, pawn.Map, pawn.Position,
                            holder, PathEndMode.Touch, out _)) continue;
                    scratch.held.Clear();
                    ThingOwnerUtility.GetAllThingsRecursively((IThingHolder)source, scratch.held);
                    for (int i = 0; i < scratch.held.Count; i++)
                    {
                        Thing thing = scratch.held[i];
                        if (scratch.seen.Contains(thing) || !valid(thing)) continue;
                        scratch.seen.Add(thing);
                        scratch.candidates.Add(thing);
                    }
                }
                __result = foundAllIngredientsAndChoose(scratch.candidates);
                if (!__result) chosen.Clear();
                return false;
            }
            finally
            {
                OuterrealmBillIngredientSelector.CurrentPawn = previous;
                scratch.Clear();
                pool.Push(scratch);
            }
        }
    }
}
