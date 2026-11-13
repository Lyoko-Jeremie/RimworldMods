using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// Mod 入口。
    ///
    /// 工程约定（沿用旧版，仍然有效）：
    /// 构造函数里只建 Harmony 实例、读取设置，**绝不安装补丁**。
    /// 此时 DefDatabase 尚未填充，Harmony 建立 detour 会提前触发被 patch 类型的静态构造，
    /// 把其 DefDatabase&lt;T&gt;.GetNamed 字段永久钉成 null。
    /// 补丁安装延后到 StaticConstructorOnStartup 阶段，或游戏内首次需要时。
    /// </summary>
    public class AOPatchMod : Mod
    {
        public const string HarmonyId = "Jeremie.AtmosphereOverhaulPatch";

        public static AOPatchMod Instance;
        public static Harmony HarmonyInstance;
        public static AOPatchSettings Settings;

        public AOPatchMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AOPatchSettings>();
            HarmonyInstance = new Harmony(HarmonyId);
        }

        public override string SettingsCategory()
        {
            return "AOPatch_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.CheckboxLabeled(
                "AOPatch_Master".Translate(),
                ref Settings.MasterEnabled,
                "AOPatch_MasterDesc".Translate());

            list.GapLine();
            list.Label("AOPatch_ProxyHeatSection".Translate());
            list.GapLine();

            list.CheckboxLabeled(
                "AOPatch_ProxyHeatEnabled".Translate(),
                ref Settings.ProxyHeatRelayEnabled,
                "AOPatch_ProxyHeatEnabledDesc".Translate());

            if (Settings.ProxyHeatRelayEnabled)
            {
                list.Gap();

                list.Label("AOPatch_ProxyHeatStrength".Translate(Settings.RangeStrengthCelsiusPerSecond.ToString("F1")));
                float strength = list.Slider(Settings.RangeStrengthCelsiusPerSecond, 0.5f, 30f);
                Settings.RangeStrengthCelsiusPerSecond = Mathf.Round(strength * 10f) / 10f;

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatAirMode".Translate(),
                    ref Settings.AirModeEnabled,
                    "AOPatch_ProxyHeatAirModeDesc".Translate());

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatGroundMode".Translate(),
                    ref Settings.GroundModeEnabled,
                    "AOPatch_ProxyHeatGroundModeDesc".Translate());

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatJetMode".Translate(),
                    ref Settings.JetModeEnabled,
                    "AOPatch_ProxyHeatJetModeDesc".Translate());

                // 层数对所有建筑生效（喷流是逐建筑开关的，不能藏在默认值后面）。
                list.Label("AOPatch_ProxyHeatJetLayers".Translate(Settings.JetLayers));
                Settings.JetLayers = (int)list.Slider(Settings.JetLayers, 1f, 3f);
                list.Label("AOPatch_ProxyHeatJetSpeedHint".Translate());

                list.GapLine();

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatStacking".Translate(),
                    ref Settings.AllowTemperatureStacking,
                    "AOPatch_ProxyHeatStackingDesc".Translate());

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatIndoorDefault".Translate(),
                    ref Settings.DefaultIndoorEffect,
                    "AOPatch_ProxyHeatIndoorDefaultDesc".Translate());

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatCooling".Translate(),
                    ref Settings.AllowActiveCooling,
                    "AOPatch_ProxyHeatCoolingDesc".Translate());

                if (Settings.AllowActiveCooling)
                {
                    list.Label("AOPatch_ProxyHeatCoolingRate".Translate(Settings.MaxCoolPerTick.ToString("F2")));
                    float cool = list.Slider(Settings.MaxCoolPerTick, 0.05f, 1f);
                    Settings.MaxCoolPerTick = Mathf.Round(cool * 100f) / 100f;
                }

                list.CheckboxLabeled(
                    "AOPatch_ProxyHeatDebugLog".Translate(),
                    ref Settings.ProxyHeatDebugLog,
                    "AOPatch_ProxyHeatDebugLogDesc".Translate());
            }

            list.End();
        }
    }
}
