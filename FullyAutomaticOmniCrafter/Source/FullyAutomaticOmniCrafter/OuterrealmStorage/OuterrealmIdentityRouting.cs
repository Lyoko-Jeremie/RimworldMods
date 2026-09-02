using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static readonly FieldInfo MapIndexOrStateField =
            AccessTools.Field(typeof(Thing), "mapIndexOrState");

        private const int SearchLeaseTicks = 30;

        private static OuterrealmIdentityRuntimeState CurrentRuntime =>
            GameComponent_OuterrealmStorage.Instance?.Runtime.Identity;

        private static Dictionary<OuterrealmEntry, OuterrealmAnchorState> States =>
            CurrentRuntime?.States;

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
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            object marker;
            return thing != null && runtime != null
                && runtime.Anchors.TryGetValue(thing, out marker);
        }

        /// <summary>新条目建立默认仓；回滚重存优先恢复取出前的 HomeVault。</summary>
        public static void OnEntryAdded(OuterrealmEntry entry, Thing depositedThing, Building_OuterrealmVault preferredHome)
        {
            if (!IsUnique(entry))
            {
                return;
            }
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (runtime == null)
            {
                return;
            }
            OuterrealmRememberedHome remembered;
            if (preferredHome != null)
            {
                entry.HomeVault = preferredHome;
                entry.HomeMapId = preferredHome.Map?.uniqueID ?? -1;
                if (depositedThing != null)
                {
                    runtime.RememberedHomes.Remove(depositedThing);
                }
            }
            else if (depositedThing != null
                && runtime.RememberedHomes.TryGetValue(depositedThing, out remembered))
            {
                entry.HomeVault = remembered.Vault;
                entry.HomeMapId = remembered.MapId;
                runtime.RememberedHomes.Remove(depositedThing);
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
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (runtime == null)
            {
                return;
            }
            runtime.RememberedHomes.Remove(actual);
            runtime.RememberedHomes.Add(actual, new OuterrealmRememberedHome
            {
                Vault = entry.HomeVault,
                MapId = entry.HomeMapId
            });
        }

        public static void OnEntryRemoving(OuterrealmEntry entry)
        {
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            OuterrealmAnchorState state;
            if (entry != null && states != null && states.TryGetValue(entry, out state))
            {
                UnregisterAnchor(entry, state);
                states.Remove(entry);
            }
        }

        /// <summary>正式 Withdraw 前撤销查询锚点；条目仍保留到 Withdraw 成功提交。</summary>
        public static void PrepareCheckout(OuterrealmEntry entry)
        {
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            OuterrealmAnchorState state;
            if (entry != null && states != null && states.TryGetValue(entry, out state))
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
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            OuterrealmAnchorState state;
            if (gs != null && states != null && gs.TryGetCanonicalEntry(thing, out entry)
                && states.TryGetValue(entry, out state))
            {
                UnregisterAnchor(entry, state);
            }
            else
            {
                CurrentRuntime?.Anchors.Remove(thing);
                gs?.Runtime.DetachRegistration(thing);
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
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            if (states == null)
            {
                return;
            }
            foreach (KeyValuePair<OuterrealmEntry, OuterrealmAnchorState> pair in states)
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

        /// <summary>地图移除时使用已记录的注册 Map 清理，不访问已失效 Thing.Map/Vault.Map。</summary>
        public static void OnMapRemoved(Map map, List<Building_OuterrealmVault> removedVaults)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (gs == null || runtime == null || map == null)
            {
                return;
            }
            List<OuterrealmRuntimeRegistration> registrations =
                gs.Runtime.RegistrationsOnMapSnapshot(map);
            for (int i = 0; i < registrations.Count; i++)
            {
                OuterrealmRuntimeRegistration registration = registrations[i];
                if (registration == null
                    || registration.Kind != OuterrealmRuntimeRegistrationKind.IdentityAnchor)
                {
                    continue;
                }
                Thing thing = registration.Thing;
                if (thing != null)
                {
                    runtime.Anchors.Remove(thing);
                }
                OuterrealmAnchorState state;
                if (registration.Entry != null
                    && runtime.States.TryGetValue(registration.Entry, out state))
                {
                    state.CurrentVault = null;
                    state.SoftClaimant = null;
                    state.SoftUntilTick = 0;
                }
            }
            if (removedVaults != null && removedVaults.Count > 0)
            {
                List<OuterrealmEntry> entries = gs.EntriesForReading;
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry entry = entries[i];
                    if (entry != null && entry.HomeVault != null
                        && removedVaults.Contains(entry.HomeVault))
                    {
                        entry.HomeVault = null;
                        entry.HomeMapId = -1;
                    }
                }
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
                OuterrealmAnchorState state;
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
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (runtime == null)
            {
                return;
            }
            int now = Find.TickManager?.TicksGame ?? 0;
            List<OuterrealmLeaseToken> bucket =
                runtime.LeaseWheel[now & (OuterrealmIdentityRuntimeState.LeaseWheelSize - 1)];
            if (bucket.Count == 0)
            {
                return;
            }
            // 倒序移除已到期令牌；尚未到期的令牌必须留在桶中。后者通常只会在
            // tick 回拨或未来调整租约跨度时出现，但保留它可避免时间轮静默丢任务。
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                OuterrealmLeaseToken token = bucket[i];
                if (token != null && token.DueTick > now)
                {
                    continue;
                }
                bucket.RemoveAt(i);
                OuterrealmAnchorState state;
                if (token == null || token.Entry == null
                    || !runtime.States.TryGetValue(token.Entry, out state)
                    || state.SoftClaimant == null || state.SoftUntilTick > now)
                {
                    continue;
                }
                if (IsReserved(token.Entry, state))
                {
                    runtime.ScheduleLease(token.Entry, now + SearchLeaseTicks);
                    continue;
                }
                state.SoftClaimant = null;
                state.SoftUntilTick = 0;
                MoveToHome(token.Entry, state);
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
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (runtime == null || runtime.States.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<OuterrealmEntry, OuterrealmAnchorState> pair in runtime.States)
            {
                OuterrealmEntry entry = pair.Key;
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
            OuterrealmAnchorState state = GetState(entry);
            if (state == null)
            {
                return;
            }
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
            CurrentRuntime?.ScheduleLease(entry, state.SoftUntilTick);
        }

        public static bool CanAccessAnchor(Thing thing, Pawn claimant)
        {
            if (!IsAnchor(thing) || claimant?.Map == null)
            {
                return false;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            OuterrealmAnchorState state;
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            return gs != null && gs.TryGetCanonicalEntry(thing, out entry)
                && states != null && states.TryGetValue(entry, out state)
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
            OuterrealmAnchorState state;
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            if (gs == null || !gs.TryGetCanonicalEntry(thing, out entry)
                || states == null || !states.TryGetValue(entry, out state)
                || state.CurrentVault == null)
            {
                return false;
            }
            vault = state.CurrentVault;
            cell = thing.Position;
            return true;
        }

        /// <summary>
        /// 把当前路由到指定终端的唯一物品权威锚点加入右键菜单命中列表。
        /// 锚点不进入 thingGrid/ThingOwner，原版鼠标命中无法自行发现它们。
        /// </summary>
        public static void AppendMenuAnchors(Building_OuterrealmVault vault, List<Thing> clickedThings)
        {
            if (vault == null || clickedThings == null)
            {
                return;
            }
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            if (states == null)
            {
                return;
            }
            foreach (KeyValuePair<OuterrealmEntry, OuterrealmAnchorState> pair in states)
            {
                OuterrealmAnchorState state = pair.Value;
                Thing thing = pair.Key?.Proto;
                if (state.CurrentVault == vault && IsAnchor(thing))
                {
                    clickedThings.Add(thing);
                }
            }
        }

        public static Building_OuterrealmVault CurrentVault(OuterrealmEntry entry)
        {
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            OuterrealmAnchorState state;
            return entry != null && states != null && states.TryGetValue(entry, out state)
                ? state.CurrentVault
                : null;
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
            OuterrealmAnchorState state = GetState(entry);
            if (state == null)
            {
                reason = "OuterrealmStorageManager_VaultUnavailable".Translate();
                return false;
            }
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
            string storageGroupName = vault.Group?.RenamableLabel;
            if (storageGroupName.NullOrEmpty())
            {
                storageGroupName = "OuterrealmVault_DefaultStorageGroupName".Translate(
                    vault.Position.x, vault.Position.z);
            }
            return storageGroupName + " · " + mapLabel;
        }

        private static OuterrealmAnchorState GetState(OuterrealmEntry entry)
        {
            Dictionary<OuterrealmEntry, OuterrealmAnchorState> states = States;
            if (states == null)
            {
                return null;
            }
            OuterrealmAnchorState state;
            if (!states.TryGetValue(entry, out state))
            {
                state = new OuterrealmAnchorState();
                states.Add(entry, state);
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
            OuterrealmAnchorState state = GetState(entry);
            if (state == null)
            {
                return;
            }
            int now = Find.TickManager?.TicksGame ?? 0;
            if (state.SoftClaimant != null && now < state.SoftUntilTick && CanServe(state.CurrentVault, entry))
            {
                return;
            }
            state.SoftClaimant = null;
            state.SoftUntilTick = 0;
            MoveToHome(entry, state);
        }

        private static void MoveToHome(OuterrealmEntry entry, OuterrealmAnchorState state)
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

        private static bool IsReserved(OuterrealmEntry entry, OuterrealmAnchorState state)
        {
            Thing thing = entry?.Proto;
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmRuntimeRegistration registration;
            Map map = gs != null && thing != null
                && gs.Runtime.TryGetRegistration(thing, out registration)
                ? registration.RegisteredMap
                : null;
            return thing != null && map != null && thing.Spawned
                && map.reservationManager.IsReserved(thing);
        }

        private static void MoveAnchor(OuterrealmEntry entry, OuterrealmAnchorState state, Building_OuterrealmVault vault)
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
            OuterrealmIdentityRuntimeState runtime = CurrentRuntime;
            if (runtime == null)
            {
                thing.ForceSetStateToUnspawned();
                return;
            }
            runtime.Anchors.Remove(thing);
            runtime.Anchors.Add(thing, null);
            GameComponent_OuterrealmStorage.Instance?.Runtime.TrackRegistration(
                thing, map, vault, entry, OuterrealmRuntimeRegistrationKind.IdentityAnchor);
            state.CurrentVault = vault;
        }

        private static void UnregisterAnchor(OuterrealmEntry entry, OuterrealmAnchorState state)
        {
            Thing thing = entry?.Proto;
            if (thing != null && IsAnchor(thing))
            {
                CurrentRuntime?.Anchors.Remove(thing);
                if (!(GameComponent_OuterrealmStorage.Instance?.Runtime.DetachRegistration(thing) ?? false))
                {
                    thing.ForceSetStateToUnspawned();
                }
            }
            if (state != null)
            {
                state.CurrentVault = null;
            }
        }
    }
}
