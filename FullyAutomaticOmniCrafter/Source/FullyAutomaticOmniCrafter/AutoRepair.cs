using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ─── UltimateAutoRepair Building ──────────────────────────────────────────────────
    public class Building_UltimateAutoRepair : Building
    {
        private CompPowerTrader _powerComp;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            _powerComp = GetComp<CompPowerTrader>();
        }

        public bool IsPowered => _powerComp == null || _powerComp.PowerOn;

        public override void TickRare()
        {
            base.TickRare();
            if (!IsPowered) return;
            Map.GetComponent<RepairTrackerMapComponent>()?.TryDoRepair(Map);
        }

        public override string GetInspectString()
        {
            var sb = new System.Text.StringBuilder();
            string baseStr = base.GetInspectString();
            if (!baseStr.NullOrEmpty()) sb.Append(baseStr);
            var tracker = Map?.GetComponent<RepairTrackerMapComponent>();
            if (tracker != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                if (!tracker.RepairEnabled)
                    sb.Append("UltimateAutoRepair_InspectDisabled".Translate());
                else
                {
                    string areaName = tracker.SharedTargetArea?.Label ?? (string)"UltimateAutoRepair_AnyArea".Translate();
                    sb.Append("UltimateAutoRepair_InspectEnabled".Translate(areaName));
                }
            }
            return sb.ToString();
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            var tracker = Map?.GetComponent<RepairTrackerMapComponent>();
            if (tracker == null || !tracker.RepairEnabled) return;

            // 若设定了目标区域，则高亮显示该区域
            tracker.SharedTargetArea?.MarkForDraw();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            var tracker = Map?.GetComponent<RepairTrackerMapComponent>();
            if (tracker == null) yield break;

            // ── Gizmo 0: toggle repair enabled ───────────────────────────────────
            yield return new Command_Toggle
            {
                defaultLabel = "UltimateAutoRepair_Toggle".Translate(),
                defaultDesc = "UltimateAutoRepair_ToggleDesc".Translate(),
                icon = UltimateAutoRepairTex.IconToggle,
                isActive = () => tracker.RepairEnabled,
                toggleAction = () => tracker.RepairEnabled = !tracker.RepairEnabled
            };

            // ── Gizmo 1: select target area ──────────────────────────────────
            string currentAreaLabel = tracker.RepairEnabled
                ? (tracker.SharedTargetArea?.Label ?? (string)"UltimateAutoRepair_AnyArea".Translate())
                : (string)"UltimateAutoRepair_None".Translate();
            yield return new Command_Action
            {
                defaultLabel = "UltimateAutoRepair_SelectArea".Translate() + ": " + currentAreaLabel,
                defaultDesc = "UltimateAutoRepair_SelectAreaDesc".Translate(currentAreaLabel),
                icon = UltimateAutoRepairTex.IconSelectArea,
                action = delegate
                {
                    var options = new List<FloatMenuOption>();
                    // "None" option – disable repair entirely
                    options.Add(new FloatMenuOption("UltimateAutoRepair_None".Translate(), () =>
                    {
                        tracker.RepairEnabled = false;
                        tracker.SharedTargetArea = null;
                    }));
                    // "Entire Map" option
                    options.Add(new FloatMenuOption("UltimateAutoRepair_AnyArea".Translate(), () =>
                    {
                        tracker.RepairEnabled = true;
                        tracker.SharedTargetArea = null;
                    }));
                    // Individual areas (only user-visible Area_Allowed areas)
                    // foreach (Area area in Map.areaManager.AllAreas.Where(a => a is Area_Allowed))
                    // Individual areas
                    foreach (Area area in Map.areaManager.AllAreas)
                    {
                        Area capturedArea = area;
                        options.Add(new FloatMenuOption(area.Label, () =>
                        {
                            tracker.RepairEnabled = true;
                            tracker.SharedTargetArea = capturedArea;
                        }));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // ── Gizmo 2: view repair stats ────────────────────────────────────
            yield return new Command_Action
            {
                defaultLabel = "UltimateAutoRepair_ViewStats".Translate(),
                defaultDesc = "UltimateAutoRepair_ViewStatsDesc".Translate(),
                icon = UltimateAutoRepairTex.IconViewStats,
                action = () => Find.WindowStack.Add(new Window_RepairStats(Map))
            };
        }
    }

    // ─── Texture cache ────────────────────────────────────────────────────────
    [StaticConstructorOnStartup]
    public static class UltimateAutoRepairTex
    {
        public static readonly Texture2D IconSelectArea =
            ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_SelectArea", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconToggle =
            ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_Toggle", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconViewStats =
            ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_ViewStats", false)
            ?? BaseContent.WhiteTex;
    }

    // ─── Map Component: shared state for all UltimateAutoRepair buildings ─────────────
    public class RepairTrackerMapComponent : MapComponent
    {
        // Shared across all Building_UltimateAutoRepair instances on this map
        private Area _sharedTargetArea;
        private bool _repairEnabled = false; // default: do not repair any area
        public Dictionary<string, int> SharedRepairStats = new Dictionary<string, int>();


        // Minimum ticks between two repair passes (matches vanilla TickRare = 250)
        private const int TickRareInterval = 250;
        private int _lastRepairTick = -9999;

        public Area SharedTargetArea
        {
            get => _sharedTargetArea;
            set => _sharedTargetArea = value;
        }

        public bool RepairEnabled
        {
            get => _repairEnabled;
            set => _repairEnabled = value;
        }

        public RepairTrackerMapComponent(Map map) : base(map)
        {
        }

        /// <summary>
        /// Called by each powered Building_UltimateAutoRepair every TickRare.
        /// Deduplicated so repair actually runs at most once per TickRare interval,
        /// even when multiple buildings are powered.
        /// </summary>
        public void TryDoRepair(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (now - _lastRepairTick < TickRareInterval)
                return;
            _lastRepairTick = now;
            DoRepair(map);
        }

        private void DoRepair(Map map)
        {
            if (!_repairEnabled) return;

            // ── Repair colony buildings ───────────────────────────────────────
            // Take a snapshot count – we must NOT iterate a live list that could
            // change under us, but allBuildingsColonist is read-only during TickRare.
            var buildings = map.listerBuildings.allBuildingsColonist;
            int bCount = buildings.Count;
            for (int i = 0; i < bCount; i++)
            {
                Building b = buildings[i];
                if (b == null || b.Destroyed || !b.Spawned) continue;
                if (!b.def.useHitPoints) continue;
                if (b.HitPoints >= b.MaxHitPoints) continue;
                if (_sharedTargetArea != null && !_sharedTargetArea[b.Position]) continue;

                int repaired = RepairThing(b);
                if (repaired > 0) RecordRepair(b.def.LabelCap, repaired);
            }

            // ── Repair haulable items ─────────────────────────────────────────
            List<Thing> things = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways);
            int tCount = things.Count;
            for (int i = 0; i < tCount; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed || !t.Spawned) continue;
                if (!t.def.useHitPoints) continue;
                if (t.HitPoints >= t.MaxHitPoints) continue;
                if (_sharedTargetArea != null && !_sharedTargetArea[t.Position]) continue;

                int repaired = RepairThing(t);
                if (repaired > 0) RecordRepair(t.def.LabelCap, repaired);
            }
        }

        private static int RepairThing(Thing thing)
        {
            int before = thing.HitPoints;
            thing.HitPoints = thing.MaxHitPoints;
            return thing.HitPoints - before;
        }

        public void RecordRepair(string label, int amount)
        {
            if (SharedRepairStats.TryGetValue(label, out int current))
                SharedRepairStats[label] = current + amount;
            else
                SharedRepairStats[label] = amount;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref _sharedTargetArea, "sharedTargetArea");
            Scribe_Values.Look(ref _repairEnabled, "repairEnabled", false);
            Scribe_Collections.Look(ref SharedRepairStats, "sharedRepairStats",
                LookMode.Value, LookMode.Value);
            if (SharedRepairStats == null)
                SharedRepairStats = new Dictionary<string, int>();
        }
    }

    // ─── Stats window ─────────────────────────────────────────────────────────
    public class Window_RepairStats : Window
    {
        private readonly Map _map;
        private Vector2 _scroll = Vector2.zero;

        public Window_RepairStats(Map map)
        {
            _map = map;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
        }

        public override Vector2 InitialSize => new Vector2(480f, 560f);

        public override void DoWindowContents(Rect inRect)
        {
            float y = 0f;
            const float lineH = 26f;

            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, 36f), "UltimateAutoRepair_StatsTitle".Translate());
            y += 38f;
            Text.Font = GameFont.Small;

            var tracker = _map.GetComponent<RepairTrackerMapComponent>();
            string areaName = tracker == null ? (string)"UltimateAutoRepair_None".Translate()
                : !tracker.RepairEnabled ? (string)"UltimateAutoRepair_None".Translate()
                : tracker.SharedTargetArea?.Label ?? (string)"UltimateAutoRepair_AnyArea".Translate();
            Widgets.Label(new Rect(0f, y, inRect.width, lineH),
                "UltimateAutoRepair_CurrentArea".Translate() + ": " + areaName);

            // Reset button (top-right of the area row)
            Rect resetBtn = new Rect(inRect.width - 110f, y, 110f, lineH - 2f);
            if (tracker != null && Widgets.ButtonText(resetBtn, "UltimateAutoRepair_ResetStats".Translate()))
            {
                tracker.SharedRepairStats.Clear();
                return;
            }

            y += lineH + 4f;

            if (tracker == null || tracker.SharedRepairStats.Count == 0)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, lineH), "UltimateAutoRepair_NoRecords".Translate());
                return;
            }

            // Column headers
            Widgets.DrawLineHorizontal(0f, y, inRect.width);
            y += 2f;
            float col1W = inRect.width * 0.68f;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(0f, y, col1W, lineH), "UltimateAutoRepair_HeaderItem".Translate());
            Widgets.Label(new Rect(col1W, y, inRect.width - col1W, lineH), "UltimateAutoRepair_HeaderHP".Translate());
            GUI.color = Color.white;
            y += lineH;
            Widgets.DrawLineHorizontal(0f, y, inRect.width);
            y += 2f;

            // Scrollable rows
            var sorted = tracker.SharedRepairStats
                .OrderByDescending(kvp => kvp.Value)
                .ToList();
            Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, sorted.Count * (lineH + 2f));

            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float ry = 0f;
            bool alt = false;
            foreach (var kvp in sorted)
            {
                Rect row = new Rect(0f, ry, viewRect.width, lineH);
                if (alt) Widgets.DrawLightHighlight(row);
                alt = !alt;
                Widgets.Label(new Rect(0f, ry, col1W, lineH), kvp.Key);
                Widgets.Label(new Rect(col1W, ry, viewRect.width - col1W, lineH),
                    "UltimateAutoRepair_HpRestored".Translate(kvp.Value));
                ry += lineH + 2f;
            }

            Widgets.EndScrollView();
        }
    }
}