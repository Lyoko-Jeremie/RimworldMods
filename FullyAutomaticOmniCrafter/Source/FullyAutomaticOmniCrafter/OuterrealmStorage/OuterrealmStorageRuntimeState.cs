using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    internal enum OuterrealmRuntimeRegistrationKind
    {
        Projection,
        IdentityAnchor
    }

    internal sealed class OuterrealmRuntimeRegistration
    {
        public Thing Thing;
        public Map RegisteredMap;
        public Building_OuterrealmVault Vault;
        public OuterrealmEntry Entry;
        public OuterrealmRuntimeRegistrationKind Kind;
        public bool Active;
        public bool SuspendedForSave;
    }

    internal sealed class OuterrealmAnchorState
    {
        public Building_OuterrealmVault CurrentVault;
        public Pawn SoftClaimant;
        public int SoftUntilTick;
        public int LastPreparedTick = int.MinValue;
    }

    internal sealed class OuterrealmRememberedHome
    {
        public Building_OuterrealmVault Vault;
        public int MapId;
    }

    internal sealed class OuterrealmLeaseToken
    {
        public OuterrealmEntry Entry;
        public int DueTick;
    }

    internal sealed class OuterrealmDemandCursorState
    {
        public int Cursor;
        public int LastProcessedTick = int.MinValue;
    }

    internal sealed class OuterrealmIdentityRuntimeState
    {
        internal const int LeaseWheelSize = 64;

        public readonly Dictionary<OuterrealmEntry, OuterrealmAnchorState> States =
            new Dictionary<OuterrealmEntry, OuterrealmAnchorState>();
        public readonly ConditionalWeakTable<Thing, object> Anchors =
            new ConditionalWeakTable<Thing, object>();
        public readonly ConditionalWeakTable<Thing, OuterrealmRememberedHome> RememberedHomes =
            new ConditionalWeakTable<Thing, OuterrealmRememberedHome>();
        public readonly List<OuterrealmLeaseToken>[] LeaseWheel =
            new List<OuterrealmLeaseToken>[LeaseWheelSize];

        public OuterrealmIdentityRuntimeState()
        {
            for (int i = 0; i < LeaseWheel.Length; i++)
            {
                LeaseWheel[i] = new List<OuterrealmLeaseToken>();
            }
        }

        public void ScheduleLease(OuterrealmEntry entry, int dueTick)
        {
            if (entry == null)
            {
                return;
            }
            LeaseWheel[dueTick & (LeaseWheelSize - 1)].Add(new OuterrealmLeaseToken
            {
                Entry = entry,
                DueTick = dueTick
            });
        }
    }

    internal sealed class OuterrealmSubspaceRuntimeState
    {
        public readonly HashSet<Thing> PendingCheckouts = new HashSet<Thing>();
        public readonly List<Thing> PendingReturnBuffer = new List<Thing>();
    }

    /// <summary>
    /// 单个 Game 的全部运行时状态。任何 Thing/Map/Pawn/仓库引用都必须归属这里，
    /// 使切换存档时旧对象图可随旧 Game 自然回收。
    /// </summary>
    internal sealed class OuterrealmStorageRuntimeState
    {
        private static readonly FieldInfo MapIndexOrStateField =
            AccessTools.Field(typeof(Thing), "mapIndexOrState");

        private readonly Game ownerGame;
        private readonly Dictionary<Thing, OuterrealmRuntimeRegistration> registrationsByThing =
            new Dictionary<Thing, OuterrealmRuntimeRegistration>();
        private readonly Dictionary<Map, HashSet<OuterrealmRuntimeRegistration>> registrationsByMap =
            new Dictionary<Map, HashSet<OuterrealmRuntimeRegistration>>();
        private readonly Dictionary<Building_OuterrealmVault, Map> vaultMaps =
            new Dictionary<Building_OuterrealmVault, Map>();
        private readonly List<OuterrealmRuntimeRegistration> saveSnapshot =
            new List<OuterrealmRuntimeRegistration>();
        private readonly Dictionary<Map, Dictionary<ThingDef, OuterrealmDemandCursorState>> demandCursors =
            new Dictionary<Map, Dictionary<ThingDef, OuterrealmDemandCursorState>>();
        private int saveIsolationDepth;

        public readonly OuterrealmIdentityRuntimeState Identity = new OuterrealmIdentityRuntimeState();
        public readonly OuterrealmSubspaceRuntimeState Subspace = new OuterrealmSubspaceRuntimeState();
        public readonly OuterrealmBillResourceLedger Bills = new OuterrealmBillResourceLedger();

        public bool SaveIsolationActive => saveIsolationDepth > 0;

        public OuterrealmStorageRuntimeState(Game ownerGame)
        {
            this.ownerGame = ownerGame;
        }

        public void TrackVault(Building_OuterrealmVault vault, Map map)
        {
            if (vault != null && map != null)
            {
                vaultMaps[vault] = map;
            }
        }

        public void UntrackVault(Building_OuterrealmVault vault)
        {
            if (vault != null)
            {
                vaultMaps.Remove(vault);
            }
        }

        /// <summary>同一地图、同一 Def 每个游戏 Tick 最多执行一次需求物化，
        /// 防止 UI 的按帧重生成把投影恢复速度错误地绑定到 FPS。</summary>
        public bool TryBeginDemandMaterialization(Map map, ThingDef def, int currentTick, out int cursor)
        {
            cursor = 0;
            if (map == null || def == null)
            {
                return false;
            }
            Dictionary<ThingDef, OuterrealmDemandCursorState> cursors;
            if (!demandCursors.TryGetValue(map, out cursors))
            {
                cursors = new Dictionary<ThingDef, OuterrealmDemandCursorState>();
                demandCursors.Add(map, cursors);
            }
            OuterrealmDemandCursorState state;
            if (!cursors.TryGetValue(def, out state))
            {
                state = new OuterrealmDemandCursorState();
                cursors.Add(def, state);
            }
            if (state.LastProcessedTick == currentTick)
            {
                return false;
            }
            state.LastProcessedTick = currentTick;
            cursor = state.Cursor;
            return true;
        }

        public void CompleteDemandMaterialization(Map map, ThingDef def, int cursor)
        {
            Dictionary<ThingDef, OuterrealmDemandCursorState> cursors;
            OuterrealmDemandCursorState state;
            if (map != null && def != null && demandCursors.TryGetValue(map, out cursors)
                && cursors.TryGetValue(def, out state))
            {
                state.Cursor = cursor;
            }
        }

        public Map RegisteredMapOf(Building_OuterrealmVault vault)
        {
            Map map;
            return vault != null && vaultMaps.TryGetValue(vault, out map) ? map : null;
        }

        public void TrackRegistration(
            Thing thing,
            Map map,
            Building_OuterrealmVault vault,
            OuterrealmEntry entry,
            OuterrealmRuntimeRegistrationKind kind)
        {
            if (thing == null || map == null)
            {
                return;
            }
            OuterrealmRuntimeRegistration registration;
            if (!registrationsByThing.TryGetValue(thing, out registration))
            {
                registration = new OuterrealmRuntimeRegistration { Thing = thing };
                registrationsByThing.Add(thing, registration);
            }
            else if (registration.RegisteredMap != null && registration.RegisteredMap != map)
            {
                RemoveFromMapIndex(registration);
            }
            registration.RegisteredMap = map;
            registration.Vault = vault;
            registration.Entry = entry;
            registration.Kind = kind;
            registration.Active = true;
            registration.SuspendedForSave = false;

            HashSet<OuterrealmRuntimeRegistration> set;
            if (!registrationsByMap.TryGetValue(map, out set))
            {
                set = new HashSet<OuterrealmRuntimeRegistration>();
                registrationsByMap.Add(map, set);
            }
            set.Add(registration);
        }

        public bool TryGetRegistration(Thing thing, out OuterrealmRuntimeRegistration registration)
        {
            registration = null;
            return thing != null && registrationsByThing.TryGetValue(thing, out registration)
                && registration.Active;
        }

        public bool DetachRegistration(Thing thing)
        {
            OuterrealmRuntimeRegistration registration;
            if (thing == null || !registrationsByThing.TryGetValue(thing, out registration))
            {
                return false;
            }
            if (registration.Active && !registration.SuspendedForSave)
            {
                DetachFromMap(registration, registration.Kind == OuterrealmRuntimeRegistrationKind.Projection);
            }
            registration.Active = false;
            registration.SuspendedForSave = false;
            registrationsByThing.Remove(thing);
            RemoveFromMapIndex(registration);
            return true;
        }

        public List<OuterrealmRuntimeRegistration> RegistrationsOnMapSnapshot(Map map)
        {
            List<OuterrealmRuntimeRegistration> result = new List<OuterrealmRuntimeRegistration>();
            HashSet<OuterrealmRuntimeRegistration> set;
            if (map != null && registrationsByMap.TryGetValue(map, out set))
            {
                foreach (OuterrealmRuntimeRegistration registration in set)
                {
                    if (registration != null && registration.Active)
                    {
                        result.Add(registration);
                    }
                }
            }
            return result;
        }

        public List<Building_OuterrealmVault> VaultsOnMapSnapshot(Map map)
        {
            List<Building_OuterrealmVault> result = new List<Building_OuterrealmVault>();
            foreach (KeyValuePair<Building_OuterrealmVault, Map> pair in vaultMaps)
            {
                if (pair.Value == map)
                {
                    result.Add(pair.Key);
                }
            }
            return result;
        }

        public void ForgetMap(Map map)
        {
            HashSet<OuterrealmRuntimeRegistration> set;
            if (map != null && registrationsByMap.TryGetValue(map, out set))
            {
                List<OuterrealmRuntimeRegistration> snapshot =
                    new List<OuterrealmRuntimeRegistration>(set);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    OuterrealmRuntimeRegistration registration = snapshot[i];
                    if (registration?.Thing != null)
                    {
                        registration.Thing.ForceSetStateToUnspawned();
                        registrationsByThing.Remove(registration.Thing);
                    }
                    if (registration != null)
                    {
                        registration.Active = false;
                        registration.SuspendedForSave = false;
                    }
                }
                registrationsByMap.Remove(map);
            }

            List<Building_OuterrealmVault> removedVaults = VaultsOnMapSnapshot(map);
            for (int i = 0; i < removedVaults.Count; i++)
            {
                vaultMaps.Remove(removedVaults[i]);
            }
            demandCursors.Remove(map);
        }

        public void BeginSaveIsolation()
        {
            saveIsolationDepth++;
            if (saveIsolationDepth != 1)
            {
                return;
            }
            saveSnapshot.Clear();
            foreach (OuterrealmRuntimeRegistration registration in registrationsByThing.Values)
            {
                if (registration == null || !registration.Active || registration.SuspendedForSave)
                {
                    continue;
                }
                SuspendForSave(registration);
                saveSnapshot.Add(registration);
            }
        }

        public void EndSaveIsolation()
        {
            if (saveIsolationDepth <= 0)
            {
                return;
            }
            saveIsolationDepth--;
            if (saveIsolationDepth != 0)
            {
                return;
            }
            for (int i = 0; i < saveSnapshot.Count; i++)
            {
                ResumeAfterSave(saveSnapshot[i]);
            }
            saveSnapshot.Clear();
        }

        public List<OuterrealmRuntimeRegistration> SuspendMapForSave(Map map)
        {
            List<OuterrealmRuntimeRegistration> snapshot = RegistrationsOnMapSnapshot(map);
            for (int i = 0; i < snapshot.Count; i++)
            {
                SuspendForSave(snapshot[i]);
            }
            return snapshot;
        }

        public void ResumeMapAfterSave(List<OuterrealmRuntimeRegistration> snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            for (int i = 0; i < snapshot.Count; i++)
            {
                ResumeAfterSave(snapshot[i]);
            }
        }

        private void SuspendForSave(OuterrealmRuntimeRegistration registration)
        {
            if (registration == null || !registration.Active || registration.SuspendedForSave)
            {
                return;
            }
            DetachFromMap(registration, registration.Kind == OuterrealmRuntimeRegistrationKind.Projection);
            registration.SuspendedForSave = true;
        }

        private void ResumeAfterSave(OuterrealmRuntimeRegistration registration)
        {
            if (registration == null || !registration.Active || !registration.SuspendedForSave)
            {
                return;
            }
            Map map = registration.RegisteredMap;
            Thing thing = registration.Thing;
            if (map == null || thing == null || thing.Destroyed || ownerGame == null
                || !ownerGame.Maps.Contains(map))
            {
                registration.Active = false;
                registration.SuspendedForSave = false;
                if (thing != null)
                {
                    registrationsByThing.Remove(thing);
                }
                RemoveFromMapIndex(registration);
                return;
            }
            int mapIndex = ownerGame.Maps.IndexOf(map);
            if (mapIndex < 0 || mapIndex > sbyte.MaxValue || MapIndexOrStateField == null)
            {
                registration.Active = false;
                registration.SuspendedForSave = false;
                registrationsByThing.Remove(thing);
                RemoveFromMapIndex(registration);
                return;
            }
            MapIndexOrStateField.SetValue(thing, (sbyte)mapIndex);
            if (!map.listerThings.Contains(thing))
            {
                map.listerThings.Add(thing);
            }
            RegionListersUpdater.RegisterInRegions(thing, map);
            if (registration.Kind == OuterrealmRuntimeRegistrationKind.Projection)
            {
                map.listerHaulables.Notify_AddedThing(thing);
            }
            registration.SuspendedForSave = false;
        }

        private static void DetachFromMap(OuterrealmRuntimeRegistration registration, bool removeHaulable)
        {
            Map map = registration.RegisteredMap;
            Thing thing = registration.Thing;
            if (map != null && thing != null)
            {
                RegionListersUpdater.DeregisterInRegions(thing, map);
                if (map.listerThings.Contains(thing))
                {
                    map.listerThings.Remove(thing);
                }
                if (removeHaulable)
                {
                    map.listerHaulables.Notify_DeSpawned(thing);
                }
            }
            thing?.ForceSetStateToUnspawned();
        }

        private void RemoveFromMapIndex(OuterrealmRuntimeRegistration registration)
        {
            Map map = registration?.RegisteredMap;
            HashSet<OuterrealmRuntimeRegistration> set;
            if (map != null && registrationsByMap.TryGetValue(map, out set))
            {
                set.Remove(registration);
                if (set.Count == 0)
                {
                    registrationsByMap.Remove(map);
                }
            }
        }
    }
}
