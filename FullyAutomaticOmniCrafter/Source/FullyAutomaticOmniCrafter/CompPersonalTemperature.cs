using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_PersonalTemperature : CompProperties
    {
        public CompProperties_PersonalTemperature() { compClass = typeof(CompPersonalTemperature); }
    }

    public class PersonalTemperatureSetting : IExposable
    {
        public Pawn pawn;
        public float temperature = 21f;
        public bool enabled;
        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref temperature, "temperature", 21f);
            Scribe_Values.Look(ref enabled, "enabled", false);
        }
    }

    public class CompPersonalTemperature : ThingComp
    {
        // 仅为旧存档保留；迁移到地图后清空，新的建筑不保存角色配置。
        private List<PersonalTemperatureSetting> legacySettings;
        internal bool LegacyEnabled = true;
        private CompPowerTrader power;
        private CompFlickable flick;
        private MapComponent_PersonalTemperature manager;
        internal List<PersonalTemperatureSetting> LegacySettings => legacySettings;
        internal List<PersonalTemperatureSetting> Settings => manager.Settings;
        internal int SettingsRevision => manager.Revision;
        public bool Enabled => manager != null && manager.Enabled;
        public bool Operational => parent.Spawned && !parent.Destroyed
            && (power == null || power.PowerOn) && (flick == null || flick.SwitchIsOn);
        public bool MapOperational => manager != null && manager.Operational;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            power = parent.GetComp<CompPowerTrader>();
            flick = parent.GetComp<CompFlickable>();
            manager = parent.Map.GetComponent<MapComponent_PersonalTemperature>();
            manager.Register(this, respawningAfterLoad);
        }
        internal void ClearLegacySettings() { legacySettings = null; }
        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            manager?.Unregister(this);
            manager = null;
        }
        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            manager?.Unregister(this);
            manager = null;
        }
        public override void PreSwapMap()
        {
            manager?.Unregister(this);
            manager = null;
        }
        public override void PostSwapMap()
        {
            if (parent.Spawned) PostSpawnSetup(false);
        }
        public override void PostExposeData()
        {
            // 未生成的旧版建筑可能仍携带待迁移数据，必须允许继续保存。
            if (Scribe.mode != LoadSaveMode.Saving || legacySettings != null)
            {
                Scribe_Values.Look(ref LegacyEnabled, "personalTemperatureEnabled", true);
                Scribe_Collections.Look(ref legacySettings, "personalTemperatures", LookMode.Deep);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit && legacySettings != null)
                legacySettings.RemoveAll(s => s == null || s.pawn == null || !ValidTemperature(s.temperature));
        }
        public static bool ValidTemperature(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= -273.15f && value <= 10000f;
        public bool TryGetSetting(Pawn pawn, out float temperature)
        {
            temperature = 21f;
            return manager != null && manager.TryGetSetting(pawn, out temperature);
        }
        public bool IsEnabledFor(Pawn pawn) => manager != null && manager.IsEnabledFor(pawn);
        public void SetEnabledFor(Pawn pawn, bool value) { manager?.SetEnabledFor(pawn, value); }
        public void SetTemperature(Pawn pawn, float? temperature) { manager?.SetTemperature(pawn, temperature); }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Toggle
            {
                defaultLabel = "PersonalTemperature_Enable".Translate(),
                defaultDesc = "PersonalTemperature_Description".Translate(),
                icon = CompBiosphereTex.IconTemperature,
                isActive = () => Enabled,
                toggleAction = () => { if (manager != null) manager.Enabled = !manager.Enabled; }
            };
            yield return new Command_Action
            {
                defaultLabel = "PersonalTemperature_Settings".Translate(),
                defaultDesc = "PersonalTemperature_Description".Translate(),
                icon = CompBiosphereTex.IconTemperatureChange,
                action = () => Find.WindowStack.Add(new Dialog_PersonalTemperature(this))
            };
        }
    }

    public class MapComponent_PersonalTemperature : MapComponent
    {
        private List<PersonalTemperatureSetting> settings = new List<PersonalTemperatureSetting>();
        private readonly List<CompPersonalTemperature> sources = new List<CompPersonalTemperature>();
        private volatile CompPersonalTemperature[] sourceSnapshot = new CompPersonalTemperature[0];
        private struct Entry
        {
            public float temperature;
            public bool enabled;
        }
        // 快照发布后不再修改，游戏线程负责设置和存档，其他线程仅查询。
        private volatile Dictionary<Pawn, Entry> index = new Dictionary<Pawn, Entry>();
        private volatile bool enabled = true;
        private bool hasSharedSettings;
        internal List<PersonalTemperatureSetting> Settings => settings;
        internal int Revision { get; private set; }
        public bool Enabled
        {
            get => enabled;
            set { enabled = value; hasSharedSettings = true; }
        }
        public bool Operational
        {
            get
            {
                if (!enabled) return false;
                var current = sourceSnapshot;
                for (int i = 0; i < current.Length; i++)
                    if (current[i].parent.Map == map && current[i].Operational) return true;
                return false;
            }
        }
        public MapComponent_PersonalTemperature(Map map) : base(map) { }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) hasSharedSettings = true;
            Scribe_Values.Look(ref hasSharedSettings, "hasSharedPersonalTemperatures", false);
            bool savedEnabled = enabled;
            Scribe_Values.Look(ref savedEnabled, "personalTemperatureEnabled", true);
            enabled = savedEnabled;
            Scribe_Collections.Look(ref settings, "personalTemperatures", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (settings == null) settings = new List<PersonalTemperatureSetting>();
                settings.RemoveAll(s => s == null || s.pawn == null || !CompPersonalTemperature.ValidTemperature(s.temperature));
                Rebuild();
            }
        }
        internal void Register(CompPersonalTemperature source, bool loading)
        {
            if (!sources.Contains(source)) sources.Add(source);
            sourceSnapshot = sources.ToArray();
            // 读档时延后统一迁移，保证冲突处理不依赖建筑加载顺序。
            if (!loading) ImportLegacy(source);
        }
        internal void Unregister(CompPersonalTemperature source)
        {
            sources.Remove(source);
            sourceSnapshot = sources.ToArray();
        }
        public override void FinalizeInit()
        {
            sources.Sort((a, b) => a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber));
            for (int i = 0; i < sources.Count; i++) ImportLegacy(sources[i]);
            sourceSnapshot = sources.ToArray();
            Rebuild();
        }
        private void ImportLegacy(CompPersonalTemperature source)
        {
            var legacy = source.LegacySettings;
            if (legacy == null) return;
            // 旧版冲突采用编号较小建筑的值；已有共享记录始终优先。
            if (!hasSharedSettings) enabled = source.LegacyEnabled;
            hasSharedSettings = true;
            var known = new HashSet<Pawn>();
            for (int i = 0; i < settings.Count; i++) known.Add(settings[i].pawn);
            for (int i = 0; i < legacy.Count; i++)
            {
                var entry = legacy[i];
                if (entry != null && entry.pawn != null && CompPersonalTemperature.ValidTemperature(entry.temperature) && known.Add(entry.pawn))
                    settings.Add(new PersonalTemperatureSetting { pawn = entry.pawn, temperature = entry.temperature, enabled = entry.enabled });
            }
            source.ClearLegacySettings();
            Rebuild();
        }
        public bool TryGetSetting(Pawn pawn, out float temperature)
        {
            temperature = 21f;
            if (pawn == null || !index.TryGetValue(pawn, out var entry)) return false;
            temperature = entry.temperature;
            return true;
        }
        public bool IsEnabledFor(Pawn pawn) => pawn != null && index.TryGetValue(pawn, out var entry) && entry.enabled;
        public void SetEnabledFor(Pawn pawn, bool value)
        {
            if (pawn == null) return;
            hasSharedSettings = true;
            for (int i = 0; i < settings.Count; i++)
                if (settings[i].pawn == pawn)
                {
                    settings[i].enabled = value;
                    Rebuild();
                    return;
                }
            settings.Add(new PersonalTemperatureSetting { pawn = pawn, enabled = value });
            Rebuild();
        }
        public void SetTemperature(Pawn pawn, float? temperature)
        {
            if (pawn == null || (temperature.HasValue && !CompPersonalTemperature.ValidTemperature(temperature.Value))) return;
            hasSharedSettings = true;
            bool wasEnabled = IsEnabledFor(pawn);
            for (int i = settings.Count - 1; i >= 0; i--)
                if (settings[i].pawn == pawn) settings.RemoveAt(i);
            if (temperature.HasValue) settings.Add(new PersonalTemperatureSetting { pawn = pawn, temperature = temperature.Value, enabled = wasEnabled });
            Rebuild();
        }
        internal void Rebuild()
        {
            var snapshot = new Dictionary<Pawn, Entry>();
            if (settings != null)
                for (int i = 0; i < settings.Count; i++)
                {
                    var entry = settings[i];
                    if (entry != null && entry.pawn != null && !entry.pawn.Destroyed && CompPersonalTemperature.ValidTemperature(entry.temperature))
                        snapshot[entry.pawn] = new Entry { temperature = entry.temperature, enabled = entry.enabled };
                }
            index = snapshot;
            Revision++;
        }
        internal bool TryGet(Pawn pawn, IntVec3 cell, out float temperature)
        {
            temperature = 0f;
            if (pawn == null || pawn.Dead || !cell.InBounds(map) || !index.TryGetValue(pawn, out var entry) || !entry.enabled || !Operational) return false;
            temperature = entry.temperature;
            return true;
        }
    }

    public static class PersonalTemperatureUtility
    {
        public static bool TryGetOverride(Pawn pawn, IntVec3 cell, Map map, out float temperature)
        {
            temperature = 0f;
            if (pawn == null || map == null) return false;
            var manager = map.GetComponent<MapComponent_PersonalTemperature>();
            return manager != null && manager.TryGet(pawn, cell, out temperature);
        }
        public static float GetEffectiveTemperature(Pawn pawn, IntVec3 cell, Map map)
        {
            return TryGetOverride(pawn, cell, map, out float value) ? value : GenTemperature.GetTemperatureForCell(cell, map);
        }
    }
}

