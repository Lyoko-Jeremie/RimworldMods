using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_OmniForceFieldDome : CompProperties_CompOmniRectangleProjectileInterceptor
    {
        public bool applyRoof = true;
        public RoofDef roofDef;

        public bool maintainTemperature = true;
        public float targetTemperature = 21f;

        public bool blockVacuum = true;
        public bool clearGas = true;
        public bool clearPollution = true;
        public bool cleanFilth = true;
        public bool extinguishFire = true;

        public bool applyFriendlyHediff = true;
        public HediffDef friendlyHediffDef;
        public string friendlyHediffDefName = "StatusAllocationTerminal_SuperMan";
        public bool removeFriendlyHediffWhenLeaving = true;

        public int environmentTickInterval = 250;
        public int pawnTickInterval = 60;

        public CompProperties_OmniForceFieldDome()
        {
            compClass = typeof(CompOmniForceFieldDome);
            isStatic = true;
        }

        public RoofDef RoofDefToUse => roofDef ?? RoofDefOf.RoofConstructed;

        public HediffDef FriendlyHediffDefToUse
        {
            get
            {
                if (friendlyHediffDef != null)
                {
                    return friendlyHediffDef;
                }
                if (!friendlyHediffDefName.NullOrEmpty())
                {
                    friendlyHediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(friendlyHediffDefName);
                }
                return friendlyHediffDef;
            }
        }
    }

    /// <summary>
    /// 一种力场穹顶建筑，用于在荒野或太空环境下建立一个方形安全区域。
    /// 投射物、爆炸、轨道攻击、空投和敌方寻路/攻击目标保护复用 OmniProjectileInterceptor 体系。
    /// 环境层效果由 OmniForceFieldDomeNetworkManager 按合并后的穹顶网络处理。
    /// </summary>
    public class CompOmniForceFieldDome : CompOmniRectangleProjectileInterceptor
    {
        public new CompProperties_OmniForceFieldDome Props => (CompProperties_OmniForceFieldDome)props;

        public CellRect DomeRect => OccupiedRect;

        private float cachedNetworkWidth = -1f;
        private float cachedNetworkHeight = -1f;
        private bool cachedNetworkActive;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>()?.Register(this);
            RememberNetworkState();
        }

        public override void PostMapInit()
        {
            base.PostMapInit();
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            map.GetComponent<OmniForceFieldDomeNetworkManager>()?.Deregister(this);
            base.PostDeSpawn(map, mode);
        }

        public override void SetRadius(float newRadius)
        {
            base.SetRadius(newRadius);
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
            RememberNetworkState();
        }

        public new void SetSize(float width, float height)
        {
            base.SetSize(width, height);
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
            RememberNetworkState();
        }

        public override void CompTick()
        {
            base.CompTick();
            DirtyNetworkIfStateChanged();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            DirtyNetworkIfStateChanged();
        }

        public override string CompInspectStringExtra()
        {
            string str = base.CompInspectStringExtra();
            if (parent.Spawned)
            {
                OmniForceFieldDomeNetwork network;
                if (parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetNetworkAt(parent.Position, out network))
                {
                    if (!str.NullOrEmpty())
                    {
                        str += "\n";
                    }
                    str += "OmniForceFieldDome_NetworkCells".Translate(network.CellCount, network.Domes.Count);
                }
            }
            return str;
        }

        private void DirtyNetworkIfStateChanged()
        {
            if (!parent.Spawned || parent.Map == null)
            {
                return;
            }

            if (cachedNetworkActive != Active
                || !Mathf.Approximately(cachedNetworkWidth, Width)
                || !Mathf.Approximately(cachedNetworkHeight, Height))
            {
                parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
                RememberNetworkState();
            }
        }

        private void RememberNetworkState()
        {
            cachedNetworkActive = Active;
            cachedNetworkWidth = Width;
            cachedNetworkHeight = Height;
        }
    }

    public sealed class OmniForceFieldDomeNetwork
    {
        public readonly List<CompOmniForceFieldDome> Domes = new List<CompOmniForceFieldDome>();
        public readonly List<IntVec3> Cells = new List<IntVec3>();

        public int CellCount => Cells.Count;

        public CompOmniForceFieldDome PrimaryDome
        {
            get
            {
                for (int i = 0; i < Domes.Count; i++)
                {
                    if (Domes[i]?.Active == true)
                    {
                        return Domes[i];
                    }
                }
                return Domes.Count > 0 ? Domes[0] : null;
            }
        }

        public CompProperties_OmniForceFieldDome PrimaryProps => PrimaryDome?.Props;
    }

    /// <summary>
    /// 负责合并所有相邻或重叠的力场穹顶，并对合并后的区域执行环境维护。
    /// </summary>
    public class OmniForceFieldDomeNetworkManager : MapComponent
    {
        private readonly List<CompOmniForceFieldDome> domes = new List<CompOmniForceFieldDome>();
        private readonly List<OmniForceFieldDomeNetwork> networks = new List<OmniForceFieldDomeNetwork>();
        private readonly HashSet<Pawn> pawnsGrantedHediff = new HashSet<Pawn>();
        private readonly List<Pawn> tmpPawnsToRemove = new List<Pawn>();

        private OmniForceFieldDomeNetwork[] networkCache;
        private bool cacheDirty = true;

        public OmniForceFieldDomeNetworkManager(Map map) : base(map)
        {
            networkCache = new OmniForceFieldDomeNetwork[map.cellIndices.NumGridCells];
        }

        public void Register(CompOmniForceFieldDome dome)
        {
            if (dome == null || domes.Contains(dome))
            {
                return;
            }
            domes.Add(dome);
            DirtyCache();
        }

        public void Deregister(CompOmniForceFieldDome dome)
        {
            if (domes.Remove(dome))
            {
                DirtyCache();
            }
        }

        public void DirtyCache()
        {
            cacheDirty = true;
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (cacheDirty)
            {
                RebuildNetworks();
            }
            if (networks.Count == 0)
            {
                return;
            }

            if (map.IsHashIntervalTick(GetEnvironmentTickInterval()))
            {
                MaintainEnvironment();
            }
            if (map.IsHashIntervalTick(GetPawnTickInterval()))
            {
                MaintainPawnEffects();
            }
        }

        public bool IsDomeCell(IntVec3 c)
        {
            OmniForceFieldDomeNetwork network;
            return TryGetNetworkAt(c, out network);
        }

        public bool TryGetNetworkAt(IntVec3 c, out OmniForceFieldDomeNetwork network)
        {
            network = null;
            if (!c.InBounds(map))
            {
                return false;
            }
            if (cacheDirty)
            {
                RebuildNetworks();
            }
            network = networkCache[map.cellIndices.CellToIndex(c)];
            return network != null;
        }

        public bool TryGetPropsAt(IntVec3 c, out CompProperties_OmniForceFieldDome props)
        {
            props = null;
            OmniForceFieldDomeNetwork network;
            if (!TryGetNetworkAt(c, out network))
            {
                return false;
            }
            props = network.PrimaryProps;
            return props != null;
        }

        public bool IsProtectedFrom(Thing searcher, IntVec3 c)
        {
            OmniForceFieldDomeNetwork network;
            if (!TryGetNetworkAt(c, out network))
            {
                return false;
            }
            CompOmniForceFieldDome dome = network.PrimaryDome;
            return dome != null && dome.IsEnemy(searcher);
        }

        private void RebuildNetworks()
        {
            if (networkCache == null || networkCache.Length != map.cellIndices.NumGridCells)
            {
                networkCache = new OmniForceFieldDomeNetwork[map.cellIndices.NumGridCells];
            }
            Array.Clear(networkCache, 0, networkCache.Length);
            networks.Clear();

            bool[] visited = new bool[domes.Count];
            for (int i = 0; i < domes.Count; i++)
            {
                CompOmniForceFieldDome start = domes[i];
                if (visited[i] || start == null || !start.Active || !start.parent.Spawned || start.parent.Map != map)
                {
                    continue;
                }

                OmniForceFieldDomeNetwork network = new OmniForceFieldDomeNetwork();
                Queue<int> queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int currentIndex = queue.Dequeue();
                    CompOmniForceFieldDome current = domes[currentIndex];
                    if (current == null || !current.Active)
                    {
                        continue;
                    }

                    network.Domes.Add(current);
                    CellRect currentRect = current.DomeRect;

                    for (int j = 0; j < domes.Count; j++)
                    {
                        if (visited[j])
                        {
                            continue;
                        }
                        CompOmniForceFieldDome other = domes[j];
                        if (other == null || !other.Active || !other.parent.Spawned || other.parent.Map != map)
                        {
                            continue;
                        }
                        if (RectsTouchOrOverlap(currentRect, other.DomeRect))
                        {
                            visited[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }

                CacheNetworkCells(network);
                networks.Add(network);
            }

            cacheDirty = false;
        }

        private void CacheNetworkCells(OmniForceFieldDomeNetwork network)
        {
            HashSet<IntVec3> seenCells = new HashSet<IntVec3>();
            for (int i = 0; i < network.Domes.Count; i++)
            {
                CellRect rect = network.Domes[i].DomeRect;
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map) || !seenCells.Add(c))
                        {
                            continue;
                        }
                        network.Cells.Add(c);
                        networkCache[map.cellIndices.CellToIndex(c)] = network;
                    }
                }
            }
        }

        private static bool RectsTouchOrOverlap(CellRect a, CellRect b)
        {
            return a.minX <= b.maxX + 1
                   && a.maxX + 1 >= b.minX
                   && a.minZ <= b.maxZ + 1
                   && a.maxZ + 1 >= b.minZ;
        }

        private int GetEnvironmentTickInterval()
        {
            int interval = 250;
            for (int i = 0; i < networks.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = networks[i].PrimaryProps;
                if (props != null)
                {
                    interval = Mathf.Max(30, Mathf.Min(interval, props.environmentTickInterval));
                }
            }
            return interval;
        }

        private int GetPawnTickInterval()
        {
            int interval = 60;
            for (int i = 0; i < networks.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = networks[i].PrimaryProps;
                if (props != null)
                {
                    interval = Mathf.Max(10, Mathf.Min(interval, props.pawnTickInterval));
                }
            }
            return interval;
        }

        private void MaintainEnvironment()
        {
            for (int i = 0; i < networks.Count; i++)
            {
                OmniForceFieldDomeNetwork network = networks[i];
                CompProperties_OmniForceFieldDome props = network.PrimaryProps;
                if (props == null)
                {
                    continue;
                }

                for (int j = 0; j < network.Cells.Count; j++)
                {
                    IntVec3 c = network.Cells[j];

                    if (props.applyRoof && props.RoofDefToUse != null && !map.roofGrid.Roofed(c))
                    {
                        map.roofGrid.SetRoof(c, props.RoofDefToUse);
                    }

                    if (props.clearGas && map.gasGrid != null && map.gasGrid.AnyGasAt(c))
                    {
                        map.gasGrid.ClearCellUnsafe(c);
                    }

                    if (props.clearPollution && ModsConfig.BiotechActive && map.pollutionGrid != null && map.pollutionGrid.IsPolluted(c))
                    {
                        map.pollutionGrid.SetPolluted(c, false, true);
                    }

                    if (props.cleanFilth)
                    {
                        FilthMaker.RemoveAllFilth(c, map);
                    }

                    if (props.extinguishFire)
                    {
                        ExtinguishFiresAt(c);
                    }
                }
            }
        }

        private void ExtinguishFiresAt(IntVec3 c)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                if (things[i] is Fire)
                {
                    things[i].Destroy();
                }
            }
        }

        private void MaintainPawnEffects()
        {
            tmpPawnsToRemove.Clear();
            foreach (Pawn pawn in pawnsGrantedHediff)
            {
                if (pawn == null || !pawn.Spawned || pawn.Map != map || !IsDomeCell(pawn.Position))
                {
                    tmpPawnsToRemove.Add(pawn);
                }
            }

            for (int i = 0; i < tmpPawnsToRemove.Count; i++)
            {
                Pawn pawn = tmpPawnsToRemove[i];
                pawnsGrantedHediff.Remove(pawn);
                RemoveDomeHediffIfNeeded(pawn);
            }

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.health == null || !IsFriendlyPawn(pawn))
                {
                    continue;
                }

                OmniForceFieldDomeNetwork network;
                if (!TryGetNetworkAt(pawn.Position, out network))
                {
                    continue;
                }

                CompProperties_OmniForceFieldDome props = network.PrimaryProps;
                HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                if (props == null || !props.applyFriendlyHediff || hediffDef == null)
                {
                    continue;
                }

                if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef) == null)
                {
                    pawn.health.AddHediff(hediffDef);
                }
                pawnsGrantedHediff.Add(pawn);
            }
        }

        private void RemoveDomeHediffIfNeeded(Pawn pawn)
        {
            if (pawn == null || pawn.health == null)
            {
                return;
            }

            for (int i = 0; i < domes.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = domes[i]?.Props;
                HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                if (props == null || !props.removeFriendlyHediffWhenLeaving || hediffDef == null)
                {
                    continue;
                }
                Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                if (hediff != null)
                {
                    pawn.health.RemoveHediff(hediff);
                }
            }
        }

        private static bool IsFriendlyPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || pawn.HostFaction == Faction.OfPlayer || pawn.IsPrisonerOfColony;
        }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.TryGetTemperatureForCell))]
    public static class Patch_OmniForceFieldDome_Temperature
    {
        public static void Postfix(IntVec3 c, Map map, ref float tempResult, ref bool __result)
        {
            if (map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.maintainTemperature)
            {
                tempResult = props.targetTemperature;
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(VacuumUtility), nameof(VacuumUtility.GetVacuum))]
    public static class Patch_OmniForceFieldDome_Vacuum
    {
        public static void Postfix(IntVec3 cell, Map map, ref float __result)
        {
            if (map == null || !cell.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(cell, out props)
                && props.blockVacuum)
            {
                __result = 0f;
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt))]
    public static class Patch_OmniForceFieldDome_TransparentRoofGlow
    {
        private static readonly AccessTools.FieldRef<GlowGrid, Map> MapField =
            AccessTools.FieldRefAccess<GlowGrid, Map>("map");

        public static void Postfix(GlowGrid __instance, IntVec3 c, bool ignoreSky, ref float __result)
        {
            if (ignoreSky)
            {
                return;
            }

            Map map = MapField(__instance);
            if (map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.applyRoof)
            {
                __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);
            }
        }
    }

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.WithinRangeOfRoofHolder))]
    public static class Patch_OmniForceFieldDome_RoofSupportRange
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.applyRoof)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.ConnectedToRoofHolder))]
    public static class Patch_OmniForceFieldDome_RoofConnected
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.applyRoof)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom))]
    public static class Patch_OmniForceFieldDome_VerbCanHitTargetFrom
    {
        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || __instance?.caster == null)
            {
                return;
            }

            Map map = __instance.caster.Map;
            if (map == null)
            {
                return;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager == null)
            {
                return;
            }

            Thing caster = __instance.caster;
            if (targ.HasThing && targ.Thing != null && manager.IsProtectedFrom(caster, targ.Thing.Position))
            {
                __result = false;
                return;
            }

            if (!targ.HasThing && manager.IsProtectedFrom(caster, targ.Cell))
            {
                __result = false;
                return;
            }

            if (manager.IsProtectedFrom(caster, root))
            {
                __result = false;
            }
        }
    }
}
