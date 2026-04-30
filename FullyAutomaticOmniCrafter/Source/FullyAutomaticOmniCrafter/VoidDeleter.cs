using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    // ─── VoidDeleter Building ─────────────────────────────────────────────────
    /// <summary>
    /// 虚空删除器：立即执行指定区域内所有被标记拆除的建筑/物体，以及被标记挖掘的矿产。
    /// 挖矿产物率 100%，产物可就近放置或送入存储区（开关可选）。
    /// </summary>
    public class Building_VoidDeleter : Building
    {
        private CompPowerTrader _powerComp;

        // Per-building state
        private Area    _targetArea  = null;
        private bool    _enabled     = false;
        private OutputMode _outputMode = OutputMode.DropNear;

        public bool IsPowered => _powerComp == null || _powerComp.PowerOn;

        // ── Lifecycle ──────────────────────────────────────────────────────────
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            _powerComp = GetComp<CompPowerTrader>();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref _targetArea, "targetArea");
            Scribe_Values.Look(ref _enabled,    "enabled",    false);
            Scribe_Values.Look(ref _outputMode, "outputMode", OutputMode.DropNear);
        }

        // ── Tick ───────────────────────────────────────────────────────────────
        public override void TickRare()
        {
            base.TickRare();
            if (!IsPowered || !_enabled) return;
            ProcessDeconstructions();
            ProcessMining();
        }

        // ── Deconstruct pass ───────────────────────────────────────────────────
        private void ProcessDeconstructions()
        {
            var dm = Map.designationManager;
            // Snapshot to avoid modifying-while-iterating
            var desigs = dm.SpawnedDesignationsOfDef(DesignationDefOf.Deconstruct).ToList();

            foreach (Designation des in desigs)
            {
                try
                {
                    Thing target = des.target.Thing;
                    if (target == null || target.Destroyed || !target.Spawned) continue;
                    if (_targetArea != null && !_targetArea[target.Position])  continue;

                    // Compute what will be returned before destroying
                    List<(ThingDef def, int count)> products = ComputeDeconstructLeavings(target);

                    // Remove designation, then erase silently
                    dm.RemoveDesignation(des);
                    SoundDefOf.Building_Deconstructed.PlayOneShot(new TargetInfo(target.Position, Map));
                    target.Destroy(DestroyMode.Vanish);

                    // Spawn products near the VoidDeleter
                    SpawnProducts(products, Position);
                }
                catch (Exception ex)
                {
                    Log.Error($"[VoidDeleter] ProcessDeconstructions error: {ex}");
                }
            }
        }

        // ── Mining pass ────────────────────────────────────────────────────────
        private void ProcessMining()
        {
            var dm = Map.designationManager;

            var mineDesigs = new List<Designation>();
            mineDesigs.AddRange(dm.SpawnedDesignationsOfDef(DesignationDefOf.Mine).ToList());
            mineDesigs.AddRange(dm.SpawnedDesignationsOfDef(DesignationDefOf.MineVein).ToList());

            var processed = new HashSet<IntVec3>();

            foreach (Designation des in mineDesigs)
            {
                try
                {
                    IntVec3 cell = des.target.Cell;
                    if (!processed.Add(cell)) continue; // already handled this cell
                    if (_targetArea != null && !_targetArea[cell]) continue;

                    Mineable mineable = cell.GetFirstMineable(Map);
                    if (mineable == null || mineable.Destroyed) continue;

                    // 100% yield: full EffectiveMineableYield, ignore mineableDropChance
                    var products = new List<(ThingDef def, int count)>();
                    ThingDef mineableThing = mineable.def.building?.mineableThing;
                    if (mineableThing != null)
                    {
                        int yield = Mathf.Max(1, mineable.def.building.EffectiveMineableYield);
                        products.Add((mineableThing, yield));
                    }

                    // Remove all mining designations at this cell
                    dm.TryRemoveDesignation(cell, DesignationDefOf.Mine);
                    dm.TryRemoveDesignation(cell, DesignationDefOf.MineVein);

                    // Destroy the rock silently (no double-spawn of vanilla yield)
                    mineable.Destroy(DestroyMode.Vanish);

                    // Spawn products near VoidDeleter
                    SpawnProducts(products, Position);
                }
                catch (Exception ex)
                {
                    Log.Error($"[VoidDeleter] ProcessMining error: {ex}");
                }
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the items that would be returned by deconstructing <paramref name="thing"/>.
        /// Handles both normal buildings (using resourcesFractionWhenDeconstructed) and frames
        /// (full refund of invested materials).
        /// </summary>
        private static List<(ThingDef def, int count)> ComputeDeconstructLeavings(Thing thing)
        {
            var result = new List<(ThingDef, int)>();
            if (!(thing is Building)) return result;

            // Frame: refund exactly what has already been invested
            if (thing is Frame frame)
            {
                for (int i = 0; i < frame.resourceContainer.Count; i++)
                {
                    Thing res = frame.resourceContainer[i];
                    if (res != null && res.stackCount > 0)
                        result.Add((res.def, res.stackCount));
                }
                return result;
            }

            // Regular building: apply resourcesFractionWhenDeconstructed (default 0.5)
            float fraction = thing.def.resourcesFractionWhenDeconstructed;
            if (fraction <= 0f) return result;

            List<ThingDefCountClass> costList = thing.CostListAdjusted();
            List<ThingDef> blacklist = thing.def.building?.leavingsBlacklist;

            foreach (ThingDefCountClass cost in costList)
            {
                if (blacklist != null && blacklist.Contains(cost.thingDef)) continue;
                int count = Mathf.Min(GenMath.RoundRandom(cost.count * fraction), cost.count);
                if (count > 0)
                    result.Add((cost.thingDef, count));
            }
            return result;
        }

        /// <summary>
        /// Spawns all items in <paramref name="products"/> near <paramref name="dropPos"/>.
        /// When _outputMode == SendToStorage, attempts to relocate each spawned stack to the
        /// nearest suitable storage cell (same logic as Building_OmniCrafter.SpawnItems).
        /// </summary>
        private void SpawnProducts(List<(ThingDef def, int count)> products, IntVec3 dropPos)
        {
            foreach (var (def, total) in products)
            {
                int remaining = total;
                while (remaining > 0)
                {
                    int stackMax  = def.stackLimit > 0 ? def.stackLimit : 1;
                    int stackSize = Mathf.Min(remaining, stackMax);

                    Thing item = ThingMaker.MakeThing(def);
                    item.stackCount = stackSize;

                    if (GenPlace.TryPlaceThing(item, dropPos, Map, ThingPlaceMode.Near, out Thing placed))
                    {
                        if (_outputMode == OutputMode.SendToStorage)
                        {
                            IntVec3 storeCell;
                            if (StoreUtility.TryFindBestBetterStoreCellFor(
                                    placed, null, Map, StoragePriority.Unstored,
                                    Faction.OfPlayer, out storeCell))
                            {
                                placed.DeSpawn();

                                Thing existing = storeCell.GetFirstThing(Map, placed.def);
                                if (existing != null && existing.CanStackWith(placed))
                                    existing.TryAbsorbStack(placed, true);

                                if (!placed.Destroyed && placed.stackCount > 0 && !placed.Spawned)
                                {
                                    if (storeCell.GetItemCount(Map) < storeCell.GetMaxItemsAllowedInCell(Map))
                                        GenSpawn.Spawn(placed, storeCell, Map);
                                }

                                // Fallback: could not store → drop near VoidDeleter
                                if (!placed.Destroyed && placed.stackCount > 0 && !placed.Spawned)
                                    GenPlace.TryPlaceThing(placed, dropPos, Map, ThingPlaceMode.Near);
                            }
                        }
                    }

                    remaining -= stackSize;
                }
            }
        }

        // ── Gizmos ─────────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos()) yield return g;

            // ── 1. Enable / Disable toggle ────────────────────────────────────
            yield return new Command_Toggle
            {
                defaultLabel = "VoidDeleter_Toggle".Translate(),
                defaultDesc  = "VoidDeleter_ToggleDesc".Translate(),
                icon         = VoidDeleterTex.IconToggle,
                isActive     = () => _enabled,
                toggleAction = () => _enabled = !_enabled
            };

            // ── 2. Area selector ──────────────────────────────────────────────
            string curAreaLabel = _enabled
                ? (_targetArea?.Label ?? (string)"VoidDeleter_AnyArea".Translate())
                : (string)"VoidDeleter_Disabled".Translate();

            yield return new Command_Action
            {
                defaultLabel = "VoidDeleter_SelectArea".Translate(),
                defaultDesc  = "VoidDeleter_SelectAreaDesc".Translate(curAreaLabel),
                icon         = VoidDeleterTex.IconSelectArea,
                action       = () =>
                {
                    var opts = new List<FloatMenuOption>();

                    opts.Add(new FloatMenuOption("VoidDeleter_Disabled".Translate(), () =>
                    {
                        _enabled    = false;
                        _targetArea = null;
                    }));
                    opts.Add(new FloatMenuOption("VoidDeleter_AnyArea".Translate(), () =>
                    {
                        _enabled    = true;
                        _targetArea = null;
                    }));

                    foreach (Area area in Map.areaManager.AllAreas)
                    {
                        Area captured = area;
                        opts.Add(new FloatMenuOption(area.Label, () =>
                        {
                            _enabled    = true;
                            _targetArea = captured;
                        }));
                    }

                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            };

            // ── 3. Output mode toggle ─────────────────────────────────────────
            string modeName = _outputMode == OutputMode.DropNear
                ? (string)"VoidDeleter_DropNear".Translate()
                : (string)"VoidDeleter_SendToStorage".Translate();

            yield return new Command_Action
            {
                defaultLabel = "VoidDeleter_OutputMode".Translate(modeName),
                defaultDesc  = "VoidDeleter_OutputModeDesc".Translate(modeName),
                icon         = VoidDeleterTex.IconOutputMode,
                action       = () =>
                {
                    _outputMode = _outputMode == OutputMode.DropNear
                        ? OutputMode.SendToStorage
                        : OutputMode.DropNear;
                }
            };
        }
    }

    // ─── Texture cache ────────────────────────────────────────────────────────
    [StaticConstructorOnStartup]
    public static class VoidDeleterTex
    {
        public static readonly Texture2D IconToggle =
            ContentFinder<Texture2D>.Get("UI/Commands/VoidDeleter_Toggle", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconSelectArea =
            ContentFinder<Texture2D>.Get("UI/Commands/VoidDeleter_SelectArea", false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_SelectArea", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconOutputMode =
            ContentFinder<Texture2D>.Get("UI/Commands/VoidDeleter_OutputMode", false)
            ?? BaseContent.WhiteTex;
    }
}