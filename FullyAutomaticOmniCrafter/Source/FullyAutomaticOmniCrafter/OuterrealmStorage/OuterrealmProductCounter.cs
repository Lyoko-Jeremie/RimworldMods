using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>账单产品统计按条目计数；保留原版各分支过滤，不提升整张地图的投影数量。</summary>
    internal static class OuterrealmProductCounter
    {
        internal static bool TryCount(RecipeWorkerCounter counter, Bill_Production bill, out int result)
        {
            result = 0;
            if (bill?.Map == null || GameComponent_OuterrealmStorage.Instance?.HasVaultOnMap(bill.Map) != true) return false;
            ThingDef product = counter.recipe.products[0].thingDef;
            ISlotGroup slot = bill.GetIncludeSlotGroup();
            // 原版快速分支已经使用本 Mod 修正过的 ResourceCounter；不能再加一次全局库存。
            if (product.CountAsResource && !bill.includeEquipped
                && (bill.includeTainted || !product.IsApparel || !product.apparel.careIfWornByCorpse)
                && slot == null && bill.hpRange.min == 0f && bill.hpRange.max == 1f
                && bill.qualityRange.min == QualityCategory.Awful && bill.qualityRange.max == QualityCategory.Legendary
                && !bill.limitToAllowedStuff) return false;

            long total = 0;
            var seen = new HashSet<OuterrealmEntry>();
            void Add(Thing query, Thing inspected, long ordinaryCount)
            {
                if (query == null || inspected == null || !counter.CountValidThing(inspected, bill, product)) return;
                OuterrealmSource source;
                long count = ordinaryCount;
                if (OuterrealmSourceResolver.TryResolve(query, out source))
                {
                    if (!seen.Add(source.Entry)) return;
                    count = source.Entry.Count;
                    if (query != inspected) count = Math.Min(int.MaxValue, count) * Math.Max(0, inspected.stackCount);
                }
                else if (OuterrealmVaultUtil.IsProjection(query)) return;
                total += Math.Min(int.MaxValue - total, Math.Max(0, count));
            }

            if (slot == null)
            {
                List<Thing> things = bill.Map.listerThings.ThingsOfDef(product);
                // 原版 List 计数分支对普通目标按实例 +1；本适配不顺带改变其语义。
                for (int i = 0; i < things.Count; i++) Add(things[i], things[i], 1);
                if (product.Minifiable)
                {
                    List<Thing> minified = bill.Map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing);
                    for (int i = 0; i < minified.Count; i++)
                    {
                        var thing = minified[i] as MinifiedThing;
                        if (thing != null) Add(thing, thing.InnerThing, (long)thing.stackCount * thing.InnerThing.stackCount);
                    }
                }
                foreach (Pawn pawn in bill.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
                {
                    Thing carried = pawn.carryTracker?.CarriedThing;
                    if (carried != null) Add(carried, carried.GetInnerIfMinified(), carried.stackCount);
                }
                foreach (IHaulSource haulSource in bill.Map.haulDestinationManager.AllHaulSourcesListForReading)
                {
                    ThingOwner owner = haulSource.GetDirectlyHeldThings();
                    for (int i = 0; i < owner.Count; i++) Add(owner[i], owner[i], owner[i].stackCount);
                }
            }
            else
            {
                foreach (Thing thing in slot.HeldThings)
                {
                    Thing inner = thing.GetInnerIfMinified();
                    Add(thing, inner, inner.stackCount);
                }
            }
            if (bill.includeEquipped)
            {
                foreach (Pawn pawn in bill.Map.mapPawns.FreeColonistsSpawned)
                {
                    List<ThingWithComps> equipment = pawn.equipment.AllEquipmentListForReading;
                    for (int i = 0; i < equipment.Count; i++) Add(equipment[i], equipment[i], equipment[i].stackCount);
                    List<Apparel> worn = pawn.apparel.WornApparel;
                    for (int i = 0; i < worn.Count; i++) Add(worn[i], worn[i], worn[i].stackCount);
                    ThingOwner inventory = pawn.inventory.GetDirectlyHeldThings();
                    for (int i = 0; i < inventory.Count; i++) Add(inventory[i], inventory[i], inventory[i].stackCount);
                }
            }
            result = (int)total;
            return true;
        }
    }
}
