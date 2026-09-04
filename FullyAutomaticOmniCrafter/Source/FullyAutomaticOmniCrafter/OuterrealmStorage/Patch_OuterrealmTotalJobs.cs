using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    [HarmonyPatch(typeof(JobDriver_Reload), "TryMakePreToilReservations")]
    internal static class Patch_OuterrealmReloadReservations
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(JobDriver_Reload __instance, bool errorOnFailed, ref bool __result)
        {
            bool result;
            if (!OuterrealmTotalJobUtility.Prepare(__instance, __instance.job.count, errorOnFailed, out result)) return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_RefuelAtomic), "TryMakePreToilReservations")]
    internal static class Patch_OuterrealmAtomicReservations
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(JobDriver_RefuelAtomic __instance, bool errorOnFailed, ref bool __result)
        {
            CompRefuelable comp = __instance.job.targetA.Thing?.TryGetComp<CompRefuelable>();
            if (comp == null) return true;
            bool result;
            if (!OuterrealmTotalJobUtility.Prepare(__instance, comp.GetFuelCountToFullyRefuel(), errorOnFailed, out result)) return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobGiver_Reload), "MakeReloadJob")]
    internal static class Patch_OuterrealmReloadCount
    {
        private static bool Prefix(IReloadableComp reloadable, ref Job __result)
        {
            if (OuterrealmBillJobUtility.Ledger?.IsTotalBlocked(ReloadableUtility.OwnerOf(reloadable), reloadable.ReloadableThing) != true) return true;
            __result = null;
            return false;
        }

        private static void Postfix(IReloadableComp reloadable, List<Thing> chosenAmmo, Job __result)
        {
            if (__result == null || !OuterrealmTotalJobUtility.HasSource(__result, false)) return;
            Pawn pawn = ReloadableUtility.OwnerOf(reloadable);
            if (pawn == null) return;
            int limit = reloadable.MaxAmmoNeeded(true);
            long total = 0;
            var seen = new HashSet<object>();
            for (int i = 0; i < chosenAmmo.Count && total < limit; i++)
            {
                Thing thing = chosenAmmo[i];
                OuterrealmSource source;
                bool storage = OuterrealmSourceResolver.TryResolve(thing, out source);
                if (!seen.Add(storage ? (object)source.Entry : thing)) continue;
                long count = storage ? OuterrealmBillJobUtility.Available(source, pawn, null) : thing.stackCount;
                total += Math.Min(limit - total, count);
            }
            __result.count = (int)total;
        }
    }

    [HarmonyPatch(typeof(RefuelWorkGiverUtility), "RefuelJob")]
    internal static class Patch_OuterrealmRefuelRetry
    {
        private static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (OuterrealmBillJobUtility.Ledger?.IsTotalBlocked(pawn, t) != true) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(RefuelWorkGiverUtility), "FindEnoughReservableThings")]
    internal static class Patch_OuterrealmFuelSelection
    {
        private static bool Prefix(Pawn pawn, IntVec3 rootCell, IntRange desiredQuantity, Predicate<Thing> validThing, ref List<Thing> __result)
        {
            if (pawn?.Map == null || GameComponent_OuterrealmStorage.Instance?.HasVaultOnMap(pawn.Map) != true) return true;
            Region root = rootCell.GetRegion(pawn.Map);
            if (root == null || desiredQuantity.max <= 0) return true;
            TraverseParms traverse = TraverseParms.For(pawn);
            var chosen = new List<Thing>();
            var seen = new HashSet<object>();
            long accumulated = 0;
            bool Process(List<Thing> things, Region region)
            {
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing.Fogged() || thing.IsForbidden(pawn) || !pawn.CanReserve(thing) || !validThing(thing)
                        || !ReachabilityWithinRegion.ThingFromRegionListerReachable(thing, region, PathEndMode.ClosestTouch, pawn)) continue;
                    OuterrealmSource source;
                    bool storage = OuterrealmSourceResolver.TryResolve(thing, out source);
                    if (!storage && OuterrealmVaultUtil.IsProjection(thing)) continue;
                    long quantity = storage ? OuterrealmBillJobUtility.Available(source, pawn, null) : thing.stackCount;
                    if (quantity <= 0 || !seen.Add(storage ? (object)source.Entry : thing)) continue;
                    chosen.Add(thing);
                    accumulated += Math.Min(desiredQuantity.max - accumulated, quantity);
                    if (accumulated >= desiredQuantity.max) return true;
                }
                return false;
            }
            // 保留原版区域遍历、候选过滤和区域内可达性，只替换数量与去重规则。
            Process(rootCell.GetThingList(root.Map), root);
            if (accumulated < desiredQuantity.max)
                RegionTraverser.BreadthFirstTraverse(root, (from, to) => to.Allows(traverse, false),
                    region => Process(region.ListerThings.ThingsMatching(ThingRequest.ForGroup(ThingRequestGroup.HaulableEver)), region), 99999);
            __result = accumulated >= desiredQuantity.min ? chosen : null;
            return false;
        }
    }
}
