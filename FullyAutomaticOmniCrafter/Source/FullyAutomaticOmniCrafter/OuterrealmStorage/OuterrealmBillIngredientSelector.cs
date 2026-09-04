using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>只在含超维候选的制作选料中使用条目预算；不修改投影或权威物的 stackCount。</summary>
    internal static class OuterrealmBillIngredientSelector
    {
        [ThreadStatic] internal static Pawn CurrentPawn;

        private sealed class Candidate
        {
            public Thing Thing;
            public object Key;
        }

        public static bool Choose(List<Thing> available, Bill bill, List<ThingCount> chosen,
            IntVec3 root, bool alreadySorted, List<IngredientCount> missing, Pawn pawn)
        {
            var candidates = new List<Candidate>(available.Count);
            var budget = new OuterrealmQuantityBudget<object>();
            var known = new HashSet<object>();
            GameComponent_OuterrealmStorage storage = GameComponent_OuterrealmStorage.Instance;
            for (int i = 0; i < available.Count; i++)
            {
                Thing thing = available[i];
                if (thing == null || thing.Destroyed) continue;
                OuterrealmSource source;
                object key = thing;
                long count = thing.stackCount;
                if (OuterrealmSourceResolver.TryResolve(thing, out source))
                {
                    if (!OuterrealmBillJobUtility.CanUse(source, pawn)
                        || (source.IsVaultQuery && !pawn.CanReach(thing, Verse.AI.PathEndMode.ClosestTouch, Danger.Some))) continue;
                    key = source.Entry;
                    count = OuterrealmQuantityBudget<OuterrealmEntry>.Available(source.Entry.Count, storage.ReservedCountOf(source.Entry));
                }
                else if (OuterrealmVaultUtil.IsProjection(thing)) continue;
                if (count <= 0) continue;
                if (known.Add(key)) budget.Add(key, Math.Min(count, int.MaxValue));
                candidates.Add(new Candidate { Thing = thing, Key = key });
            }

            RecipeDef recipe = bill.recipe;
            if (recipe.allowMixingIngredients)
            {
                candidates.Sort((a, b) =>
                {
                    int value = recipe.IngredientValueGetter.ValuePerUnitOf(a.Thing.def).CompareTo(recipe.IngredientValueGetter.ValuePerUnitOf(b.Thing.def));
                    return value != 0 ? value : (a.Thing.Position - root).LengthHorizontalSquared.CompareTo((b.Thing.Position - root).LengthHorizontalSquared);
                });
            }
            else if (!alreadySorted)
                candidates.Sort((a, b) => (a.Thing.PositionHeld - root).LengthHorizontalSquared.CompareTo((b.Thing.PositionHeld - root).LengthHorizontalSquared));

            chosen.Clear();
            missing?.Clear();
            var defs = new List<ThingDef>();
            var seenDefs = new HashSet<ThingDef>();
            for (int i = 0; i < candidates.Count; i++) if (seenDefs.Add(candidates[i].Thing.def)) defs.Add(candidates[i].Thing.def);
            var counted = new HashSet<object>();
            for (int ingredientIndex = 0; ingredientIndex < recipe.ingredients.Count; ingredientIndex++)
            {
                IngredientCount ingredient = recipe.ingredients[ingredientIndex];
                bool satisfied = false;
                if (recipe.allowMixingIngredients)
                {
                    // 保留原版混料按价值排序和 GetBaseCount 语义，并扣除前面原料槽已经占用的预算。
                    float needed = ingredient.GetBaseCount();
                    for (int i = 0; i < candidates.Count && needed > 0.0001f; i++)
                    {
                        Candidate candidate = candidates[i];
                        if (!Allows(ingredient, bill, candidate.Thing)) continue;
                        float value = recipe.IngredientValueGetter.ValuePerUnitOf(candidate.Thing.def);
                        if (value <= 0 || float.IsNaN(value)) continue;
                        int take = (int)Math.Min(budget.Get(candidate.Key), Mathf.CeilToInt(needed / value));
                        if (take <= 0 || !budget.TrySpend(candidate.Key, take)) continue;
                        ThingCountUtility.AddToList(chosen, candidate.Thing, take);
                        needed -= take * value;
                    }
                    satisfied = needed <= 0.0001f;
                }
                else
                {
                    for (int defIndex = 0; defIndex < defs.Count && !satisfied; defIndex++)
                    {
                        ThingDef def = defs[defIndex];
                        if (!ingredient.filter.Allows(def) || (!ingredient.IsFixedIngredient && !bill.ingredientFilter.Allows(def))) continue;
                        int needed = ingredient.CountRequiredOfFor(def, recipe, bill);
                        long total = 0;
                        counted.Clear();
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            Candidate candidate = candidates[i];
                            if (candidate.Thing.def == def && Allows(ingredient, bill, candidate.Thing) && counted.Add(candidate.Key))
                                total += budget.Get(candidate.Key);
                        }
                        if (!recipe.ignoreIngredientCountTakeEntireStacks && total < needed) continue;
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            Candidate candidate = candidates[i];
                            if (candidate.Thing.def != def || !Allows(ingredient, bill, candidate.Thing)) continue;
                            int take = (int)Math.Min(budget.Get(candidate.Key), recipe.ignoreIngredientCountTakeEntireStacks ? int.MaxValue : needed);
                            if (take <= 0 || !budget.TrySpend(candidate.Key, take)) continue;
                            ThingCountUtility.AddToList(chosen, candidate.Thing, take);
                            if (recipe.ignoreIngredientCountTakeEntireStacks) return true;
                            needed -= take;
                            if (needed == 0) { satisfied = true; break; }
                        }
                        if (needed <= 0) satisfied = true;
                    }
                }
                if (!satisfied)
                {
                    if (missing == null) { chosen.Clear(); return false; }
                    missing.Add(ingredient);
                }
            }
            return missing == null || missing.Count == 0;
        }

        private static bool Allows(IngredientCount ingredient, Bill bill, Thing thing)
        {
            return ingredient.filter.Allows(thing) && (ingredient.IsFixedIngredient || bill.ingredientFilter.Allows(thing));
        }
    }

    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredientsInSet")]
    internal static class Patch_OuterrealmBillIngredientSet
    {
        private static bool Prefix(List<Thing> availableThings, Bill bill, List<ThingCount> chosen,
            IntVec3 rootCell, bool alreadySorted, List<IngredientCount> missingIngredients, ref bool __result)
        {
            Pawn pawn = OuterrealmBillIngredientSelector.CurrentPawn;
            if (pawn == null || GameComponent_OuterrealmStorage.Instance == null) return true;
            for (int i = 0; i < availableThings.Count; i++)
            {
                OuterrealmSource source;
                if (OuterrealmSourceResolver.TryResolve(availableThings[i], out source) || OuterrealmVaultUtil.IsProjection(availableThings[i]))
                {
                    __result = OuterrealmBillIngredientSelector.Choose(availableThings, bill, chosen, rootCell, alreadySorted, missingIngredients, pawn);
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients")]
    internal static class Patch_OuterrealmBillRetryGuard
    {
        private static bool Prefix(Bill bill, Pawn pawn, List<ThingCount> chosen, ref bool __result)
        {
            if (OuterrealmBillJobUtility.Ledger?.IsBlocked(pawn, bill) != true) return true;
            chosen.Clear();
            __result = false;
            return false;
        }
    }
}
