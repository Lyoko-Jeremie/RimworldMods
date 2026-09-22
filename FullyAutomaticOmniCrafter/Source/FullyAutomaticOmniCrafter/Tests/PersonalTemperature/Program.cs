using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FullyAutomaticOmniCrafter;
using Verse;

static class Program
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }
    private static CompPersonalTemperature Create(Map map, int id)
    {
        var comp = new CompPersonalTemperature
        {
            props = new CompProperties_PersonalTemperature(),
            parent = new ThingWithComps { Map = map, thingIDNumber = id }
        };
        comp.PostSpawnSetup(false);
        return comp;
    }
    private static bool Read(Pawn pawn, out float value) => PersonalTemperatureUtility.TryGetOverride(pawn, pawn.PositionHeld, pawn.MapHeld, out value);
    private static void Main()
    {
        var map = new Map();
        var a = new Pawn { Map = map };
        var b = new Pawn { Map = map, comfort = new FloatRange(-30, -10) };
        var source = Create(map, 20);
        source.SetTemperature(a, 21);
        source.SetTemperature(b, -20);
        Check(!Read(a, out _), "Editing a target must not enable a pawn.");
        source.SetEnabledFor(a, true);
        source.SetEnabledFor(b, true);
        Check(Read(a, out float value) && value == 21, "Pawn A target.");
        Check(Read(b, out value) && value == -20, "Pawn B independent target.");
        bool safe = false;
        Patch_PersonalTemperature_ComfortableCell.Postfix(b, b.PositionHeld, map, ref safe);
        Check(safe, "Pawn-specific comfort range.");
        source.SetTemperature(b, 21);
        Patch_PersonalTemperature_SafeCell.Postfix(b, b.PositionHeld, map, ref safe);
        Check(!safe, "A comfortable target for A must remain unsafe for B.");
        source.SetEnabledFor(a, false);
        Check(!Read(a, out _) && source.TryGetSetting(a, out value) && value == 21, "Disabling must preserve target.");
        source.SetEnabledFor(a, true);
        source.parent.power.PowerOn = false;
        Check(!Read(a, out _), "Power loss must take effect without a tick.");
        source.parent.power.PowerOn = true;
        Check(Read(a, out _), "Power restoration.");
        foreach (Gizmo gizmo in source.CompGetGizmosExtra())
            if (gizmo is Command_Toggle toggle)
            {
                toggle.toggleAction();
                Check(!Read(a, out _), "Building switch disables all targets.");
                toggle.toggleAction();
            }
        source.SetTemperature(a, float.NaN);
        source.SetTemperature(a, float.PositiveInfinity);
        source.SetTemperature(a, -274);
        Check(Read(a, out value) && value == 21, "Invalid input must not overwrite valid settings.");
        var priority = Create(map, 10);
        priority.SetTemperature(a, 15);
        priority.SetEnabledFor(a, true);
        Check(Read(a, out value) && value == 15 && source.TryGetSetting(a, out value) && value == 15, "Edits from either terminal are shared.");
        source.SetEnabledFor(a, false);
        Check(!priority.IsEnabledFor(a) && !Read(a, out _), "Individual toggle is shared.");
        priority.SetEnabledFor(a, true);
        priority.parent.power.PowerOn = false;
        Check(Read(a, out value) && value == 15, "Any powered terminal supplies the same shared target.");
        source.SetTemperature(a, 21);
        priority.PostDeSpawn(map);
        priority.parent.Spawned = false;
        source.PostDeSpawn(map);
        source.parent.Spawned = false;
        Check(!Read(a, out _), "Uninstall removes effect.");
        source.parent.Spawned = true;
        source.PostSpawnSetup(false);
        Check(Read(a, out value) && value == 21, "Reinstall restores target.");
        var otherMap = new Map();
        a.Map = otherMap;
        Check(!Read(a, out _), "No cross-map leakage.");
        a.Map = map;
        Check(Read(a, out _), "Returning pawn resumes effect.");
        a.Dead = true;
        Check(!Read(a, out _), "Dead pawns are excluded.");
        a.Dead = false;
        source.SetEnabledFor(b, false);
        Scribe.node = new Dictionary<string, object>();
        Scribe.mode = LoadSaveMode.Saving;
        map.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        source.PostDeSpawn(map);
        map = new Map();
        a.Map = map;
        b.Map = map;
        Scribe.mode = LoadSaveMode.LoadingVars;
        map.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        Scribe.mode = LoadSaveMode.PostLoadInit;
        map.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        Scribe.mode = LoadSaveMode.Inactive;
        Check(!Read(a, out _), "Map settings alone must not supply temperature without a terminal.");
        var loaded = Create(map, 20);
        map.GetComponent<MapComponent_PersonalTemperature>().FinalizeInit();
        Check(Read(a, out value) && value == 21 && !Read(b, out _), "Round trip retains individual toggles.");
        Check(loaded.TryGetSetting(b, out value) && value == 21, "Disabled target survives round trip.");
        loaded.PreSwapMap();
        loaded.parent.Map = otherMap;
        loaded.PostSwapMap();
        Check(!Read(a, out _), "Moving controller removes old map registration.");
        a.Map = otherMap;
        Check(!Read(a, out _), "Moving controller must not carry the old map's settings.");
        loaded.SetTemperature(a, 21);
        loaded.SetEnabledFor(a, true);
        Check(Read(a, out _), "Destination map uses its own settings.");
        var replacement = Create(map, 50);
        Check(replacement.TryGetSetting(a, out value) && value == 21 && replacement.IsEnabledFor(a), "Original map retains settings after the last terminal moves.");
        var range = new FloatRange(-30, -10);
        Region region = new Region();
        Check(!Patch_PersonalTemperature_SeekRegion.Prefix(a.PositionHeld, otherMap, a, ref range, ref region) && region == null,
            "No safe region exists when a map-wide target is unsafe.");
        range = new FloatRange(10, 30);
        Check(Patch_PersonalTemperature_SeekRegion.Prefix(a.PositionHeld, otherMap, a, ref range, ref region)
            && range.Includes(-100), "A safe map-wide target preserves vanilla traversal irrespective of room temperature.");
        Patch_PersonalTemperature_BabyPlace.Prefix(a, out Pawn outer);
        Patch_PersonalTemperature_BabyMove.Prefix(b, out Pawn inner);
        Check(PersonalTemperatureReadContext.Pawn == b, "Nested context enters.");
        Patch_PersonalTemperature_BabyMove.Finalizer(inner);
        Check(PersonalTemperatureReadContext.Pawn == a, "Nested context restores.");
        Patch_PersonalTemperature_Ambient.Prefix(b, out Pawn ambientContext);
        Check(PersonalTemperatureReadContext.Pawn == null, "Another pawn must not inherit the baby's temperature context.");
        Patch_PersonalTemperature_Ambient.Finalizer(ambientContext);
        Check(PersonalTemperatureReadContext.Pawn == a, "Ambient read restores the outer context.");
        Patch_PersonalTemperature_SafeCell.Prefix(out Pawn safeContext);
        Check(PersonalTemperatureReadContext.Pawn == null, "Pawn-aware cell query isolates its original read.");
        Patch_PersonalTemperature_SafeCell.Finalizer(safeContext);
        Check(Task.Run(() => PersonalTemperatureReadContext.Pawn == null).Result, "Context must be thread-local.");
        Patch_PersonalTemperature_BabyPlace.Finalizer(outer);
        value = 35;
        Patch_PersonalTemperature_ContextCell.Postfix(a.PositionHeld, otherMap, ref value);
        Check(value == 35, "Environmental reads remain unchanged outside context.");
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
                if (Read(a, out float current) && current != 21 && current != 22) throw new Exception("Torn snapshot.");
        });
        for (int i = 0; i < 2000; i++) loaded.SetTemperature(a, 21 + i % 2);
        reader.GetAwaiter().GetResult();
        Check(true, "Concurrent reads and snapshot publication.");
        loaded.SetTemperature(a, null);
        Check(!Read(a, out _) && !loaded.IsEnabledFor(a), "Clearing removes the setting.");
        TestLegacyMigration();
        Console.WriteLine("PASS: " + assertions + " personal-temperature assertions.");
    }

    private static CompPersonalTemperature LoadLegacy(Map map, Pawn pawn, int id, float target, bool enabled)
    {
        var source = new CompPersonalTemperature { parent = new ThingWithComps { Map = map, thingIDNumber = id } };
        Scribe.node = new Dictionary<string, object>
        {
            ["personalTemperatureEnabled"] = true,
            ["personalTemperatures"] = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["pawn"] = pawn, ["temperature"] = target, ["enabled"] = enabled }
            }
        };
        Scribe.mode = LoadSaveMode.LoadingVars;
        source.PostExposeData();
        Scribe.mode = LoadSaveMode.PostLoadInit;
        source.PostExposeData();
        Scribe.mode = LoadSaveMode.Inactive;
        source.PostSpawnSetup(true);
        return source;
    }
    private static void TestLegacyMigration()
    {
        var map = new Map();
        var pawn = new Pawn { Map = map };
        var high = LoadLegacy(map, pawn, 30, 40, true);
        var low = LoadLegacy(map, pawn, 10, 17, false);
        map.GetComponent<MapComponent_PersonalTemperature>().FinalizeInit();
        Check(high.TryGetSetting(pawn, out float value) && value == 17 && !high.IsEnabledFor(pawn), "Legacy conflicts resolve by stable building ID, preserving disabled values.");
        Check(high.LegacySettings == null && low.LegacySettings == null, "Migrated building records are consumed.");
        high.SetTemperature(pawn, 25);
        map.GetComponent<MapComponent_PersonalTemperature>().FinalizeInit();
        Check(low.TryGetSetting(pawn, out value) && value == 25, "Migration must not replay after an edit.");
        var another = LoadLegacy(map, pawn, 1, 100, true);
        map.GetComponent<MapComponent_PersonalTemperature>().FinalizeInit();
        Check(another.TryGetSetting(pawn, out value) && value == 25, "Existing shared map setting wins over later legacy terminal.");
        Scribe.node = new Dictionary<string, object>();
        Scribe.mode = LoadSaveMode.Saving;
        high.PostExposeData();
        Scribe.mode = LoadSaveMode.Inactive;
        Check(!Scribe.node.ContainsKey("personalTemperatures"), "New building saves must not duplicate map settings.");
        foreach (Gizmo gizmo in high.CompGetGizmosExtra())
            if (gizmo is Command_Toggle toggle)
            {
                toggle.toggleAction();
                Check(!low.Enabled && !another.Enabled, "Master toggle is shared by all terminals.");
            }
        Scribe.node = new Dictionary<string, object>();
        Scribe.mode = LoadSaveMode.Saving;
        map.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        var restoredMap = new Map();
        Scribe.mode = LoadSaveMode.LoadingVars;
        restoredMap.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        Scribe.mode = LoadSaveMode.PostLoadInit;
        restoredMap.GetComponent<MapComponent_PersonalTemperature>().ExposeData();
        Scribe.mode = LoadSaveMode.Inactive;
        var restored = Create(restoredMap, 70);
        Check(!restored.Enabled && restored.TryGetSetting(pawn, out value) && value == 25, "Shared master switch and targets survive map save/load.");
    }
}
