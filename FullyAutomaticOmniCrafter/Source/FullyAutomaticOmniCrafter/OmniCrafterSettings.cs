using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    // ─── Global Settings (cross-save favorites) ───────────────────────────────
    public class OmniCrafterSettings : ModSettings
    {
        public List<string> globalFavorites = new List<string>();

        /// <summary>
        /// 是否启用拼音搜索（支持全拼和首字母缩写）。
        /// 可在搜索栏旁的"拼"按钮中切换，也可在 Mod 设置页面切换。
        /// </summary>
        public bool enablePinyinSearch = false;

        // ─── Power cost polynomial coefficients ───────────────────────────
        // X = marketValue [+ mass if xIncludeMass] [+ maxHP if xIncludeHitPoints]
        // Y = a + b*X + c*X^2 + d*X^3
        // Final cost = Y * qualityMultiplier * count
        public float powerCostA = 0f;
        public float powerCostB = 1f;
        public float powerCostC = 0f;
        public float powerCostD = 0f;

        /// <summary>是否将物品重量（Mass）加入 X 的计算。</summary>
        public bool xIncludeMass = false;
        /// <summary>是否将物品最大耐久（MaxHitPoints）加入 X 的计算。</summary>
        public bool xIncludeHitPoints = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref globalFavorites, "globalFavorites", LookMode.Value);
            if (globalFavorites == null) globalFavorites = new List<string>();
            Scribe_Values.Look(ref enablePinyinSearch, "enablePinyinSearch", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars) enablePinyinSearch = false;
            Scribe_Values.Look(ref powerCostA, "powerCostA", 0f);
            Scribe_Values.Look(ref powerCostB, "powerCostB", 1f);
            Scribe_Values.Look(ref powerCostC, "powerCostC", 0f);
            Scribe_Values.Look(ref powerCostD, "powerCostD", 0f);
            Scribe_Values.Look(ref xIncludeMass, "xIncludeMass", false);
            Scribe_Values.Look(ref xIncludeHitPoints, "xIncludeHitPoints", false);
        }
    }

    public class OmniCrafterMod : Mod
    {
        public static OmniCrafterSettings Settings;

        public OmniCrafterMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OmniCrafterSettings>();

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("Jeremie.Fully.Automatic.OmniCrafter");
            harmony.PatchAll();
        }

        public override string SettingsCategory() => "FullyAutomaticOmniCrafter";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            // ── Pinyin search ────────────────────────────────────────────
            listing.CheckboxLabeled(
                "OmniCrafter_EnablePinyinSearch".Translate(),
                ref Settings.enablePinyinSearch);

            listing.Gap();
            listing.Label("OmniCrafter_PowerCostFormula".Translate());
            listing.Label("OmniCrafter_PowerCostFormulaDesc".Translate());
            listing.Gap(4f);

            // ── X composition toggles ─────────────────────────────────────
            listing.CheckboxLabeled(
                "OmniCrafter_XIncludeMass".Translate(),
                ref Settings.xIncludeMass,
                "OmniCrafter_XIncludeMassDesc".Translate());
            listing.CheckboxLabeled(
                "OmniCrafter_XIncludeHitPoints".Translate(),
                ref Settings.xIncludeHitPoints,
                "OmniCrafter_XIncludeHitPointsDesc".Translate());
            listing.Gap(4f);

            // Helper to draw a coefficient row: label + text field + slider
            void CoeffRow(string labelKey, ref float val, float min, float max)
            {
                Rect rowRect = listing.GetRect(28f);
                float labelWidth = 40f;
                float fieldWidth = 70f;
                float gap = 6f;

                Rect labelRect = new Rect(rowRect.x, rowRect.y, labelWidth, rowRect.height);
                Rect fieldRect = new Rect(labelRect.xMax + gap, rowRect.y, fieldWidth, rowRect.height);
                Rect sliderRect = new Rect(fieldRect.xMax + gap, rowRect.y,
                    rowRect.width - labelWidth - fieldWidth - gap * 2f, rowRect.height);

                Widgets.Label(labelRect, labelKey.Translate());

                string valStr = val.ToString("G4");
                string edited = Widgets.TextField(fieldRect, valStr);
                if (edited != valStr && float.TryParse(edited, out float parsed))
                    val = Mathf.Clamp(parsed, min, max);

                val = Widgets.HorizontalSlider(sliderRect, val, min, max, middleAlignment: false,
                    label: null, leftAlignedLabel: min.ToString("G3"), rightAlignedLabel: max.ToString("G3"));
                val = Mathf.Clamp(val, min, max);
            }

            CoeffRow("OmniCrafter_PowerCostA", ref Settings.powerCostA, -100f, 100f);
            CoeffRow("OmniCrafter_PowerCostB", ref Settings.powerCostB, -10f, 10f);
            CoeffRow("OmniCrafter_PowerCostC", ref Settings.powerCostC, -1f, 1f);
            CoeffRow("OmniCrafter_PowerCostD", ref Settings.powerCostD, -0.01f, 0.01f);

            listing.Gap();
            if (listing.ButtonText("OmniCrafter_PowerCostReset".Translate()))
            {
                Settings.powerCostA = 0f;
                Settings.powerCostB = 1f;
                Settings.powerCostC = 0f;
                Settings.powerCostD = 0f;
                Settings.xIncludeMass = false;
                Settings.xIncludeHitPoints = false;
            }

            listing.End();
            Settings.Write();
        }
    }

}
