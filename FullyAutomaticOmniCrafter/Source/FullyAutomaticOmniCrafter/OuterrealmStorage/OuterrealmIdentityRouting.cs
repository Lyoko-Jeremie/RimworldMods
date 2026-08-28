using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 唯一物品路由：持久 HomeVault + 运行时单一权威锚点 + 搜索期软租约。
    /// 唯一物品不再制造语义不完整的查询副本；原始权威 Thing 本身被注册到一座终端的
    /// lister/region 中。它不进入 thingGrid、不渲染、不 tick，正式预留后才从账本取出并 Spawn。
    /// </summary>
    internal static class OuterrealmIdentityRouting
    {
        private sealed class AnchorState
        {
            public Building_OuterrealmVault CurrentVault;
            public Pawn SoftClaimant;
            public int SoftUntilTick;
        }

        private sealed class RememberedHome
        {
            public Building_OuterrealmVault Vault;
            public int MapId;
        }

        private static readonly Dictionary<OuterrealmEntry, AnchorState> States =
            new Dictionary<OuterrealmEntry, AnchorState>();
        private static ConditionalWeakTable<Thing, object> anchors =
            new ConditionalWeakTable<Thing, object>();
        private static ConditionalWeakTable<Thing, RememberedHome> rememberedHomes =
            new ConditionalWeakTable<Thing, RememberedHome>();
        private static readonly FieldInfo MapIndexOrStateField =
            AccessTools.Field(typeof(Thing), "mapIndexOrState");

        private const int SearchLeaseTicks = 30;

        /// <summary>
        /// 当前通用安全边界：实际堆上限为 1 的 Item 视为不可替代实例。
        /// Corpse 含 Pawn 地图生命周期，暂不建立伪 Spawn 锚点，但仍保留默认仓路由元数据。
        /// </summary>
        public static bool IsUnique(OuterrealmEntry entry)
        {
            Thing proto = entry?.Proto;
            return proto?.def != null
                && proto.def.category == ThingCategory.Item
                && proto.def.stackLimit <= 1;
        }

        public static bool CanAnchor(OuterrealmEntry entry)
        {
            return IsUnique(entry) && !(entry.Proto is Corpse);
        }

        public static bool IsAnchor(Thing thing)
        {
            object marker;
            return thing != null && anchors.TryGetValue(thing, out marker);
        }

        public static void ResetRuntimeState()
        {
            foreach (KeyValuePair<OuterrealmEntry, AnchorState> pair in States)
            {
                UnregisterAnchor(pair.Key, pair.Value);
            }
            States.Clear();
            anchors = new ConditionalWeakTable<Thing, object>();
            rememberedHomes = new ConditionalWeakTable<Thing, RememberedHome>();
        }

        /// <summary>新条目建立默认仓；回滚重存优先恢复取出前的 HomeVault。</summary>
        public static void OnEntryAdded(OuterrealmEntry entry, Thing depositedThing, Building_OuterrealmVault preferredHome)
        {
            if (!IsUnique(entry))
            {
                return;
            }
            RememberedHome remembered;
            if (preferredHome != null)
            {
                entry.HomeVault = preferredHome;
                entry.HomeMapId = preferredHome.Map?.uniqueID ?? -1;
                if (depositedThing != null)
                {
                    rememberedHomes.Remove(depositedThing);
                }
            }
            else if (depositedThing != null && rememberedHomes.TryGetValue(depositedThing, out remembered))
            {
                entry.HomeVault = remembered.Vault;
                entry.HomeMapId = remembered.MapId;
                rememberedHomes.Remove(depositedThing);
            }
            EnsureHome(entry, null);
            Reconcile(entry);
        }

        /// <summary>取出前记住默认仓，使 PendingCheckout 失败重存时恢复原绑定。</summary>
        public static void RememberHomeForCheckout(OuterrealmEntry entry, Thing actual)
        {
            if (!IsUnique(entry) || actual == null)
            {
                return;
            }
            rememberedHomes.Remove(actual);
            rememberedHomes.Add(actual, new RememberedHome
            {
                Vault = entry.HomeVault,
                MapId = entry.HomeMapId
            });
        }

        public static void OnEntryRemoving(OuterrealmEntry entry)
        {
            AnchorState state;
            if (entry != null && States.TryGetValue(entry, out state))
            {
                UnregisterAnchor(entry, state);
                States.Remove(entry);
            }
        }

        /// <summary>正式 Withdraw 前撤销查询锚点；条目仍保留到 Withdraw 成功提交。</summary>
        public static void PrepareCheckout(OuterrealmEntry entry)
        {
            AnchorState state;
            if (entry != null && States.TryGetValue(entry, out state))
            {
                UnregisterAnchor(entry, state);
                state.SoftClaimant = null;
                state.SoftUntilTick = 0;
            }
        }

        /// <summary>Withdraw 未能取得权威物时恢复默认锚点。</summary>
        public static void CancelCheckout(OuterrealmEntry entry)
        {
            if (entry != null && IsUnique(entry))
            {
                Reconcile(entry);
            }
        }

        /// <summary>Deposit 在检查 Spawned 前调用，避免对伪锚点执行原版完整 DeSpawn。</summary>
        public static void DetachAnchorForDeposit(Thing thing)
        {
            if (!IsAnchor(thing))
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            AnchorState state;
            if (gs != null && gs.TryGetCanonicalEntry(thing, out entry) && States.TryGetValue(entry, out state))
            {
                UnregisterAnchor(entry, state);
            }
            else
            {
                anchors.Remove(thing);
                thing.ForceSetStateToUnspawned();
            }
        }

        public static void OnVaultRegistered(Building_OuterrealmVault vault)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || vault == null)
            {
                return;
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                if (!IsUnique(entry))
                {
                    continue;
                }
                EnsureHome(entry, vault);
                Reconcile(entry);
            }
        }

        public static void OnVaultUnregistered(Building_OuterrealmVault vault)
        {
            if (vault == null)
            {
                return;
            }
            foreach (KeyValuePair<OuterrealmEntry, AnchorState> pair in States)
            {
                if (pair.Value.CurrentVault == vault)
                {
                    UnregisterAnchor(pair.Key, pair.Value);
                    pair.Value.SoftClaimant = null;
                    pair.Value.SoftUntilTick = 0;
                }
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                if (!IsUnique(entry))
                {
                    continue;
                }
                if (entry.HomeVault == vault && vault.Destroyed)
                {
                    entry.HomeVault = null;
                }
                EnsureHome(entry, null);
                Reconcile(entry);
            }
        }

        public static void OnVaultSettingsChanged(Building_OuterrealmVault vault)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || vault == null)
            {
                return;
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                AnchorState state;
                if (IsUnique(entry) && (entry.HomeVault == vault
                    || (States.TryGetValue(entry, out state) && state.CurrentVault == vault)))
                {
                    Reconcile(entry);
                }
            }
        }

        public static void ReconcileAll()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                if (IsUnique(entries[i]))
                {
                    EnsureHome(entries[i], null);
                    Reconcile(entries[i]);
                }
            }
        }

        /// <summary>软租约到期且没有预留时，权威锚点回到默认仓。</summary>
        public static void Tick()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            foreach (KeyValuePair<OuterrealmEntry, AnchorState> pair in States)
            {
                AnchorState state = pair.Value;
                if (state.SoftClaimant == null || now < state.SoftUntilTick || IsReserved(pair.Key, state))
                {
                    continue;
                }
                state.SoftClaimant = null;
                state.SoftUntilTick = 0;
                MoveToHome(pair.Key, state);
            }
        }

        /// <summary>按 ThingDef 准备同地图候选；仅遍历该 Def 的条目粗索引。</summary>
        public static void PrepareForSearch(ThingDef def, Map map, IntVec3 root, Pawn claimant)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            List<OuterrealmEntry> entries = gs?.EntriesOfDefForReading(def);
            if (entries == null || map == null)
            {
                return;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (CanAnchor(entries[i]))
                {
                    PrepareEntry(entries[i], map, root, claimant);
                }
            }
        }

        /// <summary>
        /// WorkGiver_DoBill 的 HaulSource 分支只枚举 ThingOwner；唯一锚点不属于任何视图容器，
        /// 因此在同一原版 relevantThings 注入点补入可访问的权威实例。
        /// </summary>
        public static void InjectBillCandidates(Predicate<Thing> validator, Pawn pawn, Thing billGiver, List<Thing> relevantThings)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || pawn?.Map == null || relevantThings == null)
            {
                return;
            }
            IntVec3 root = billGiver != null && billGiver.Map == pawn.Map ? billGiver.Position : pawn.Position;
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                if (!CanAnchor(entry))
                {
                    continue;
                }
                PrepareEntry(entry, pawn.Map, root, pawn);
                Thing candidate = entry.Proto;
                if (IsAnchor(candidate) && candidate.Map == pawn.Map
                    && (validator == null || validator(candidate)) && pawn.CanReserve(candidate)
                    && !relevantThings.Contains(candidate))
                {
                    relevantThings.Add(candidate);
                }
            }
        }

        /// <summary>已知权威对象的直接可达检查（如 Bill.BoundUft）在原版判断前就近迁移。</summary>
        public static void PrepareForTarget(Pawn claimant, Thing target)
        {
            if (claimant?.Map == null || target == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            if (gs != null && gs.TryGetCanonicalEntry(target, out entry) && CanAnchor(entry))
            {
                PrepareEntry(entry, claimant.Map, claimant.Position, claimant);
            }
        }

        private static void PrepareEntry(OuterrealmEntry entry, Map map, IntVec3 root, Pawn claimant)
        {
            AnchorState state = GetState(entry);
            int now = Find.TickManager?.TicksGame ?? 0;
            if (IsReserved(entry, state))
            {
                return;
            }
            if (state.SoftClaimant != null && state.SoftClaimant != claimant && now < state.SoftUntilTick)
            {
                return;
            }
            Building_OuterrealmVault best = FindBestOutlet(entry, map, root, claimant);
            if (best == null)
            {
                return;
            }
            MoveAnchor(entry, state, best);
            state.SoftClaimant = claimant;
            state.SoftUntilTick = now + SearchLeaseTicks;
        }

        public static bool CanAccessAnchor(Thing thing, Pawn claimant)
        {
            if (!IsAnchor(thing) || claimant?.Map == null)
            {
                return false;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            AnchorState state;
            return gs != null && gs.TryGetCanonicalEntry(thing, out entry)
                && States.TryGetValue(entry, out state)
                && state.CurrentVault != null
                && state.CurrentVault.Map == claimant.Map
                && CanServe(state.CurrentVault, entry);
        }

        public static bool TryGetAnchor(Thing thing, out Building_OuterrealmVault vault, out IntVec3 cell)
        {
            vault = null;
            cell = IntVec3.Invalid;
            if (!IsAnchor(thing))
            {
                return false;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            AnchorState state;
            if (gs == null || !gs.TryGetCanonicalEntry(thing, out entry)
                || !States.TryGetValue(entry, out state) || state.CurrentVault == null)
            {
                return false;
            }
            vault = state.CurrentVault;
            cell = thing.Position;
            return true;
        }

        public static Building_OuterrealmVault CurrentVault(OuterrealmEntry entry)
        {
            AnchorState state;
            return entry != null && States.TryGetValue(entry, out state) ? state.CurrentVault : null;
        }

        public static bool IsTemporarilyRouted(OuterrealmEntry entry)
        {
            Building_OuterrealmVault current = CurrentVault(entry);
            return current != null && entry != null && current != entry.HomeVault;
        }

        /// <summary>是否仍需要用户指定一个有效的持久默认存储仓。</summary>
        public static bool NeedsHomeBinding(OuterrealmEntry entry)
        {
            return IsUnique(entry)
                && (entry.HomeVault == null || entry.HomeVault.Destroyed);
        }

        public static bool TrySetHomeVault(OuterrealmEntry entry, Building_OuterrealmVault vault, out string reason)
        {
            reason = null;
            if (!IsUnique(entry))
            {
                reason = "OuterrealmStorageManager_NotUnique".Translate();
                return false;
            }
            if (vault == null || !vault.Spawned || vault.Destroyed)
            {
                reason = "OuterrealmStorageManager_VaultUnavailable".Translate();
                return false;
            }
            if (!vault.CanShow(entry.Proto))
            {
                reason = "OuterrealmStorageManager_VaultRejectsItem".Translate();
                return false;
            }
            AnchorState state = GetState(entry);
            if (IsReserved(entry, state))
            {
                reason = "OuterrealmStorageManager_ItemReserved".Translate();
                return false;
            }
            entry.HomeVault = vault;
            entry.HomeMapId = vault.Map?.uniqueID ?? -1;
            state.SoftClaimant = null;
            state.SoftUntilTick = 0;
            MoveToHome(entry, state);
            return true;
        }

        public static string VaultDisplayName(Building_OuterrealmVault vault)
        {
            if (vault == null)
            {
                return "OuterrealmStorageManager_NoHomeVault".Translate();
            }
            string mapLabel = vault.Map?.info?.parent?.Label ?? "-";
            return vault.LabelCap + " · " + mapLabel;
        }

        private static AnchorState GetState(OuterrealmEntry entry)
        {
            AnchorState state;
            if (!States.TryGetValue(entry, out state))
            {
                state = new AnchorState();
                States.Add(entry, state);
            }
            return state;
        }

        private static void EnsureHome(OuterrealmEntry entry, Building_OuterrealmVault preferred)
        {
            if (entry.HomeVault != null && !entry.HomeVault.Destroyed)
            {
                entry.HomeMapId = entry.HomeVault.Map?.uniqueID ?? entry.HomeMapId;
                return;
            }
            entry.HomeVault = FindDefaultHome(entry, preferred);
            if (entry.HomeVault != null)
            {
                entry.HomeMapId = entry.HomeVault.Map?.uniqueID ?? -1;
            }
        }

        private static Building_OuterrealmVault FindDefaultHome(OuterrealmEntry entry, Building_OuterrealmVault preferred)
        {
            if (preferred != null && CanHost(preferred, entry))
            {
                return preferred;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            List<Building_OuterrealmVault> vaults = gs?.VaultsForReading;
            if (vaults == null)
            {
                return null;
            }
            Building_OuterrealmVault fallback = null;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (!CanHost(vault, entry))
                {
                    continue;
                }
                if (vault.Map != null && vault.Map.uniqueID == entry.HomeMapId)
                {
                    return vault;
                }
                if (fallback == null || vault.thingIDNumber < fallback.thingIDNumber)
                {
                    fallback = vault;
                }
            }
            return fallback;
        }

        private static void Reconcile(OuterrealmEntry entry)
        {
            if (!CanAnchor(entry))
            {
                return;
            }
            AnchorState state = GetState(entry);
            int now = Find.TickManager?.TicksGame ?? 0;
            if (state.SoftClaimant != null && now < state.SoftUntilTick && CanServe(state.CurrentVault, entry))
            {
                return;
            }
            state.SoftClaimant = null;
            state.SoftUntilTick = 0;
            MoveToHome(entry, state);
        }

        private static void MoveToHome(OuterrealmEntry entry, AnchorState state)
        {
            Building_OuterrealmVault home = CanServe(entry.HomeVault, entry) ? entry.HomeVault : null;
            MoveAnchor(entry, state, home);
        }

        private static Building_OuterrealmVault FindBestOutlet(OuterrealmEntry entry, Map map, IntVec3 root, Pawn claimant)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            List<Building_OuterrealmVault> vaults = gs?.VaultsForReading;
            Building_OuterrealmVault best = null;
            int bestDistance = int.MaxValue;
            if (vaults == null)
            {
                return null;
            }
            TraverseParms parms = claimant != null
                ? TraverseParms.For(claimant, Danger.Deadly)
                : TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (vault?.Map != map || !CanServe(vault, entry))
                {
                    continue;
                }
                IntVec3 cell = vault.Position;
                int distance = (root - cell).LengthHorizontalSquared;
                if (distance > bestDistance)
                {
                    continue;
                }
                if (!map.reachability.CanReach(root, cell, PathEndMode.Touch, parms))
                {
                    continue;
                }
                if (distance < bestDistance || best == null || vault.thingIDNumber < best.thingIDNumber)
                {
                    best = vault;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool CanHost(Building_OuterrealmVault vault, OuterrealmEntry entry)
        {
            return vault != null && vault.Spawned && !vault.Destroyed && entry?.Proto != null
                && vault.CanShow(entry.Proto);
        }

        private static bool CanServe(Building_OuterrealmVault vault, OuterrealmEntry entry)
        {
            return CanHost(vault, entry)
                && (vault.HaulSourceEnabled || (vault.AllowTakeForUse && !vault.Frozen));
        }

        private static bool IsReserved(OuterrealmEntry entry, AnchorState state)
        {
            Thing thing = entry?.Proto;
            Map map = state?.CurrentVault?.Map;
            return thing != null && map != null && thing.Spawned && map.reservationManager.IsReserved(thing);
        }

        private static void MoveAnchor(OuterrealmEntry entry, AnchorState state, Building_OuterrealmVault vault)
        {
            if (state.CurrentVault == vault && (vault == null || IsAnchor(entry.Proto)))
            {
                return;
            }
            UnregisterAnchor(entry, state);
            if (vault == null || !CanAnchor(entry))
            {
                return;
            }
            Thing thing = entry.Proto;
            Map map = vault.Map;
            if (thing == null || thing.Destroyed || thing.holdingOwner != null || map == null)
            {
                return;
            }
            if (thing.Spawned)
            {
                // 非本系统生成的真实 Spawned 物品不能被当成查询锚点迁移。
                return;
            }
            thing.Position = vault.Position;
            if (MapIndexOrStateField == null)
            {
                return;
            }
            MapIndexOrStateField.SetValue(thing, (sbyte)map.Index);
            if (!map.listerThings.Contains(thing))
            {
                map.listerThings.Add(thing);
            }
            RegionListersUpdater.RegisterInRegions(thing, map);
            anchors.Remove(thing);
            anchors.Add(thing, null);
            state.CurrentVault = vault;
        }

        private static void UnregisterAnchor(OuterrealmEntry entry, AnchorState state)
        {
            Thing thing = entry?.Proto;
            Building_OuterrealmVault vault = state?.CurrentVault;
            Map map = vault?.Map;
            if (thing != null && IsAnchor(thing))
            {
                if (map != null)
                {
                    RegionListersUpdater.DeregisterInRegions(thing, map);
                    if (map.listerThings.Contains(thing))
                    {
                        map.listerThings.Remove(thing);
                    }
                }
                anchors.Remove(thing);
                thing.ForceSetStateToUnspawned();
            }
            if (state != null)
            {
                state.CurrentVault = null;
            }
        }
    }
}
