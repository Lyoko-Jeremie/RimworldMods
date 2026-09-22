using System;
using System.Collections.Generic;

// 隔离游戏运行时；被测组件和补丁直接编译生产文件，不复制温控算法。
namespace HarmonyLib
{
    public enum MethodType { Getter }
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string member) { }
        public HarmonyPatch(Type type, string member, MethodType method) { }
    }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int Last = 0; }
}
namespace Verse
{
    public enum DestroyMode { Vanish }
    public enum LoadSaveMode { Inactive, Saving, LoadingVars, PostLoadInit }
    public enum LookMode { Deep }
    public interface IExposable { void ExposeData(); }
    public class CompProperties { public Type compClass; }
    public class Thing
    {
        public bool Spawned = true;
        public bool Destroyed;
        public Map Map;
        public Map MapHeld => Map;
        public IntVec3 PositionHeld = new IntVec3(10, 10);
        public bool SpawnedOrAnyParentSpawned => Map != null;
        public float AmbientTemperature => 35f;
        public int thingIDNumber;
    }
    public class ThingWithComps : Thing
    {
        public RimWorld.CompPowerTrader power = new RimWorld.CompPowerTrader();
        public RimWorld.CompFlickable flick = new RimWorld.CompFlickable();
        public T GetComp<T>() where T : class => (typeof(T) == typeof(RimWorld.CompPowerTrader) ? (object)power : flick) as T;
    }
    public class Pawn : Thing
    {
        public bool Dead;
        public FloatRange comfort = new FloatRange(10f, 30f);
    }
    public class ThingComp
    {
        public ThingWithComps parent;
        public CompProperties props;
        public virtual void PostSpawnSetup(bool loading) { }
        public virtual void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish) { }
        public virtual void PostDestroy(DestroyMode mode, Map map) { }
        public virtual void PreSwapMap() { }
        public virtual void PostSwapMap() { }
        public virtual void PostExposeData() { }
        public virtual IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
    }
    public class Map
    {
        private MapComponent component;
        public T GetComponent<T>() where T : MapComponent
        {
            if (component == null) component = (T)Activator.CreateInstance(typeof(T), this);
            return (T)component;
        }
    }
    public class MapComponent
    {
        protected Map map;
        public MapComponent(Map map) { this.map = map; }
        public virtual void FinalizeInit() { }
        public virtual void ExposeData() { }
    }
    public struct IntVec3
    {
        private int x, z;
        public IntVec3(int x, int z) { this.x = x; this.z = z; }
        public bool InBounds(Map map) => map != null && x >= 0 && z >= 0 && x < 100 && z < 100;
    }
    public struct FloatRange
    {
        public float min, max;
        public FloatRange(float min, float max) { this.min = min; this.max = max; }
        public bool Includes(float value) => value >= min && value <= max;
    }
    public static class GenTemperature
    {
        public static float GetTemperatureForCell(IntVec3 cell, Map map) => 35f;
        public static FloatRange ComfortableTemperatureRange(this Pawn pawn) => pawn.comfort;
        public static FloatRange SafeTemperatureRange(this Pawn pawn) => new FloatRange(pawn.comfort.min - 10, pawn.comfort.max + 10);
        public static bool SafeTemperatureAtCell(Pawn p, IntVec3 cell, Map map) => false;
        public static bool ComfortableTemperatureAtCell(Pawn p, IntVec3 cell, Map map) => false;
    }
    public class Region { }
    public class Gizmo { }
    public class Command_Toggle : Gizmo
    {
        public string defaultLabel, defaultDesc;
        public object icon;
        public Func<bool> isActive;
        public Action toggleAction;
    }
    public class Command_Action : Gizmo
    {
        public string defaultLabel, defaultDesc;
        public object icon;
        public Action action;
    }
    public static class TranslateExtensions { public static string Translate(this string s) => s; }
    public static class Find { public static readonly WindowStack WindowStack = new WindowStack(); }
    public class WindowStack { public void Add(object window) { } }
    public static class Scribe
    {
        public static LoadSaveMode mode;
        public static Dictionary<string, object> node = new Dictionary<string, object>();
    }
    public static class Scribe_Values
    {
        public static void Look<T>(ref T value, string key, T fallback = default(T))
        {
            if (Scribe.mode == LoadSaveMode.Saving) Scribe.node[key] = value;
            if (Scribe.mode == LoadSaveMode.LoadingVars) value = Scribe.node.TryGetValue(key, out object saved) ? (T)saved : fallback;
        }
    }
    public static class Scribe_References
    {
        public static void Look(ref Pawn pawn, string key) { Scribe_Values.Look(ref pawn, key); }
    }
    public static class Scribe_Collections
    {
        public static void Look<T>(ref List<T> list, string key, LookMode mode) where T : IExposable, new()
        {
            var root = Scribe.node;
            try
            {
                if (Scribe.mode == LoadSaveMode.Saving)
                {
                    var records = new List<Dictionary<string, object>>();
                    foreach (T item in list)
                    {
                        Scribe.node = new Dictionary<string, object>();
                        item.ExposeData();
                        records.Add(Scribe.node);
                    }
                    root[key] = records;
                }
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    list = new List<T>();
                    if (root.TryGetValue(key, out object saved))
                        foreach (var record in (List<Dictionary<string, object>>)saved)
                        {
                            Scribe.node = record;
                            var item = new T();
                            item.ExposeData();
                            list.Add(item);
                        }
                }
            }
            finally { Scribe.node = root; }
        }
    }
}
namespace RimWorld
{
    public class CompPowerTrader { public bool PowerOn = true; }
    public class CompFlickable { public bool SwitchIsOn = true; }
    public class Building_Bed : Verse.ThingWithComps
    {
        public List<Verse.Pawn> OwnersForReading = new List<Verse.Pawn>();
        public string GetInspectString() => "";
    }
    public class JobGiver_SeekSafeTemperature { public void ClosestRegionWithinTemperatureRange() { } }
    public static class ChildcareUtility
    {
        public static void SafePlaceForBaby() { }
        public static void BabyNeedsMovingForTemperatureReasons() { }
    }
}
namespace FullyAutomaticOmniCrafter
{
    public static class CompBiosphereTex
    {
        public static readonly object IconTemperature = null;
        public static readonly object IconTemperatureChange = null;
    }
    public class Dialog_PersonalTemperature { public Dialog_PersonalTemperature(CompPersonalTemperature comp) { } }
}
