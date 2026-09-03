using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 可发射资源的超维来源适配。余额枚举和实际支付必须共用这里的可见性与去重规则，
    /// 避免“检查能看见投影、扣款只能看见 thingGrid 实物”的来源不一致。
    /// </summary>
    internal static class OuterrealmLaunchableResourceUtility
    {
        private sealed class CheckedOutPiece
        {
            public Thing Thing;
            public Building_OuterrealmVault PreferredHome;
        }

        /// <summary>
        /// 收集当前地图可用于轨道贸易/费用支付的全局条目。多个终端共享同一条目，必须按
        /// OuterrealmEntry 引用去重，不能按各终端的投影 Thing 去重。
        /// </summary>
        public static void CollectAccessibleEntries(
            Map map,
            ITrader trader,
            ThingDef requiredDef,
            List<OuterrealmEntry> result)
        {
            result.Clear();
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || map == null)
            {
                return;
            }

            HashSet<OuterrealmEntry> seen = new HashSet<OuterrealmEntry>();
            if (gs.ExposeAllToOrbitalTrade)
            {
                List<OuterrealmEntry> entries = gs.EntriesForReading;
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry entry = entries[i];
                    Thing source = entry?.Proto;
                    if (source == null || entry.Count <= 0
                        || (requiredDef != null && source.def != requiredDef)
                        || !TradeUtility.PlayerSellableNow(source, trader))
                    {
                        continue;
                    }
                    if (seen.Add(entry))
                    {
                        result.Add(entry);
                    }
                }
                return;
            }

            HashSet<IntVec3> beaconCells = new HashSet<IntVec3>();
            foreach (Building_OrbitalTradeBeacon beacon in Building_OrbitalTradeBeacon.AllPowered(map))
            {
                foreach (IntVec3 cell in beacon.TradeableCells)
                {
                    beaconCells.Add(cell);
                }
            }
            if (beaconCells.Count == 0)
            {
                return;
            }

            HashSet<Building_OuterrealmVault> coveredVaults = new HashSet<Building_OuterrealmVault>();
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (vault == null || !vault.Spawned || vault.Map != map || vault.view == null
                    || !OverlapsBeacon(vault, beaconCells))
                {
                    continue;
                }

                coveredVaults.Add(vault);
                List<Thing> copies = vault.view.InnerListForReading;
                for (int j = 0; j < copies.Count; j++)
                {
                    Thing copy = copies[j];
                    if (copy == null || (requiredDef != null && copy.def != requiredDef)
                        || !TradeUtility.PlayerSellableNow(copy, trader))
                    {
                        continue;
                    }
                    OuterrealmEntry entry = vault.view.GetEntryOf(copy);
                    if (entry != null && entry.Count > 0 && seen.Add(entry))
                    {
                        result.Add(entry);
                    }
                }
            }

            // 唯一物品没有普通视图投影，只在其当前路由终端被信标覆盖时作为来源。
            List<OuterrealmEntry> allEntries = gs.EntriesForReading;
            for (int i = 0; i < allEntries.Count; i++)
            {
                OuterrealmEntry entry = allEntries[i];
                Thing canonical = entry?.Proto;
                Building_OuterrealmVault currentVault = OuterrealmIdentityRouting.CurrentVault(entry);
                if (canonical == null || entry.Count <= 0 || !OuterrealmIdentityRouting.IsAnchor(canonical)
                    || (requiredDef != null && canonical.def != requiredDef)
                    || currentVault == null || currentVault.Map != map || !coveredVaults.Contains(currentVault)
                    || !TradeUtility.PlayerSellableNow(canonical, trader))
                {
                    continue;
                }
                if (seen.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }

        /// <summary>为余额统计建立每个全局条目唯一的临时来源；来源仅用于查询，不拥有库存。</summary>
        public static Thing CreateCountingSource(OuterrealmEntry entry)
        {
            if (entry?.Proto == null || entry.Count <= 0)
            {
                return null;
            }
            if (OuterrealmIdentityRouting.IsUnique(entry))
            {
                OuterrealmTradeSourceRegistry.Register(entry.Proto, entry);
                return entry.Proto;
            }
            Thing source = GameComponent_OuterrealmStorage.MaterializeProjection(entry.Proto);
            if (source == null)
            {
                return null;
            }
            source.stackCount = (int)Math.Min(entry.Count, int.MaxValue);
            OuterrealmTradeSourceRegistry.Register(source, entry);
            return source;
        }

        /// <summary>
        /// 在原版 LaunchThingsOfType 看不见超维投影时执行精确支付。先完整预检并把超维部分
        /// Checkout 到本地暂存，全部成功后才销毁地图实物；Checkout 失败会把暂存实物存回全局。
        /// </summary>
        public static bool TryConsumeWithVault(
            ThingDef resourceDef,
            int debt,
            Map map,
            List<OuterrealmEntry> vaultEntries,
            out bool handled)
        {
            handled = vaultEntries != null && vaultEntries.Count > 0;
            if (!handled)
            {
                return false;
            }
            if (resourceDef == null || debt <= 0 || map == null)
            {
                return true;
            }

            List<Thing> physical = new List<Thing>();
            HashSet<Thing> seenPhysical = new HashSet<Thing>();
            long physicalCount = 0L;
            foreach (Building_OrbitalTradeBeacon beacon in Building_OrbitalTradeBeacon.AllPowered(map))
            {
                foreach (IntVec3 cell in beacon.TradeableCells)
                {
                    foreach (Thing thing in map.thingGrid.ThingsAt(cell))
                    {
                        if (thing != null && thing.def == resourceDef && seenPhysical.Add(thing))
                        {
                            physical.Add(thing);
                            physicalCount += thing.stackCount;
                        }
                    }
                }
            }

            long vaultCount = 0L;
            for (int i = 0; i < vaultEntries.Count; i++)
            {
                OuterrealmEntry entry = vaultEntries[i];
                if (entry?.Proto != null && entry.Proto.def == resourceDef && entry.Count > 0)
                {
                    vaultCount += entry.Count;
                }
            }
            if (physicalCount + vaultCount < debt)
            {
                Log.Error("Could not find enough " + resourceDef + " to transfer to trader.");
                return false;
            }

            long physicalToConsume = Math.Min(physicalCount, debt);
            long vaultRemaining = debt - physicalToConsume;
            List<CheckedOutPiece> checkedOut = new List<CheckedOutPiece>();
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            for (int i = 0; i < vaultEntries.Count && vaultRemaining > 0; i++)
            {
                OuterrealmEntry entry = vaultEntries[i];
                if (entry?.Proto == null || entry.Proto.def != resourceDef || entry.Count <= 0)
                {
                    continue;
                }
                long fromEntry = Math.Min(entry.Count, vaultRemaining);
                Building_OuterrealmVault preferredHome = entry.HomeVault;
                while (fromEntry > 0)
                {
                    int stackLimit = Math.Max(1, entry.Proto?.def?.stackLimit ?? 1);
                    int request = (int)Math.Min(fromEntry, stackLimit);
                    Thing piece = gs?.Withdraw(entry, request);
                    if (piece == null || piece.Destroyed || piece.stackCount != request)
                    {
                        if (piece != null && !piece.Destroyed && piece.stackCount > 0)
                        {
                            checkedOut.Add(new CheckedOutPiece { Thing = piece, PreferredHome = preferredHome });
                        }
                        RollBackCheckedOut(gs, checkedOut);
                        Log.Error("[OuterrealmStorage] Exact launch payment checkout failed for "
                            + resourceDef + ": requested=" + debt + ".");
                        return false;
                    }
                    checkedOut.Add(new CheckedOutPiece { Thing = piece, PreferredHome = preferredHome });
                    fromEntry -= request;
                    vaultRemaining -= request;
                }
            }
            if (vaultRemaining > 0)
            {
                RollBackCheckedOut(gs, checkedOut);
                Log.Error("[OuterrealmStorage] Exact launch payment became unavailable for "
                    + resourceDef + ": requested=" + debt + ".");
                return false;
            }

            long remainingPhysical = physicalToConsume;
            for (int i = 0; i < physical.Count && remainingPhysical > 0; i++)
            {
                Thing source = physical[i];
                int take = (int)Math.Min(remainingPhysical, source.stackCount);
                source.SplitOff(take).Destroy();
                remainingPhysical -= take;
            }
            for (int i = 0; i < checkedOut.Count; i++)
            {
                Thing piece = checkedOut[i].Thing;
                if (piece != null && !piece.Destroyed)
                {
                    piece.Destroy();
                }
            }
            return true;
        }

        private static void RollBackCheckedOut(
            GameComponent_OuterrealmStorage gs,
            List<CheckedOutPiece> checkedOut)
        {
            if (gs == null)
            {
                return;
            }
            for (int i = 0; i < checkedOut.Count; i++)
            {
                CheckedOutPiece item = checkedOut[i];
                if (item.Thing != null && !item.Thing.Destroyed && item.Thing.stackCount > 0)
                {
                    gs.Deposit(item.Thing, item.PreferredHome);
                }
            }
        }

        private static bool OverlapsBeacon(
            Building_OuterrealmVault vault,
            HashSet<IntVec3> beaconCells)
        {
            CellRect rect = vault.OccupiedRect();
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                for (int z = rect.minZ; z <= rect.maxZ; z++)
                {
                    if (beaconCells.Contains(new IntVec3(x, 0, z)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
