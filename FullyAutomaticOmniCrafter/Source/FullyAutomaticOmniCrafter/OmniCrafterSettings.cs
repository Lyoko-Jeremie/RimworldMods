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
        // Y = a + b*X + c*X^2 + d*X^3 + e*X^4 + g*log10(X) + n*ln(X)
        // Final cost = Y * qualityMultiplier * count
        public float powerCostA = 0f;
        public float powerCostB = 1f;
        public float powerCostC = 0f;
        public float powerCostD = 0f;
        public float powerCostE = 0f;
        public float powerCostG = 0f;
        public float powerCostN = 0f;

        /// <summary>是否将物品重量（Mass）加入 X 的计算。</summary>
        public bool xIncludeMass = false;
        /// <summary>是否将物品最大耐久（MaxHitPoints）加入 X 的计算。</summary>
        public bool xIncludeHitPoints = false;

        // ─── MEC energy polynomial coefficients ───────────────────────────────
        // X = marketValue [+ mass if mecXIncludeMass] [+ maxHP if mecXIncludeHitPoints]
        // Y = a + b*X + c*X^2 + d*X^3 + e*X^4 + g*log10(X) + n*ln(X)
        // Energy per item = Y * qualityMultiplier * stackCount
        public float mecEnergyA = 0f;
        public float mecEnergyB = 1f;
        public float mecEnergyC = 0f;
        public float mecEnergyD = 0f;
        public float mecEnergyE = 0f;
        public float mecEnergyG = 0f;
        public float mecEnergyN = 0f;

        /// <summary>是否将物品重量（Mass）加入 MEC 转化公式的 X。</summary>
        public bool mecXIncludeMass = true;
        /// <summary>是否将物品最大耐久（MaxHitPoints）加入 MEC 转化公式的 X。</summary>
        public bool mecXIncludeHitPoints = true;

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
            Scribe_Values.Look(ref powerCostE, "powerCostE", 0f);
            Scribe_Values.Look(ref powerCostG, "powerCostG", 0f);
            Scribe_Values.Look(ref powerCostN, "powerCostN", 0f);
            Scribe_Values.Look(ref xIncludeMass, "xIncludeMass", false);
            Scribe_Values.Look(ref xIncludeHitPoints, "xIncludeHitPoints", false);
            Scribe_Values.Look(ref mecEnergyA, "mecEnergyA", 0f);
            Scribe_Values.Look(ref mecEnergyB, "mecEnergyB", 1f);
            Scribe_Values.Look(ref mecEnergyC, "mecEnergyC", 0f);
            Scribe_Values.Look(ref mecEnergyD, "mecEnergyD", 0f);
            Scribe_Values.Look(ref mecEnergyE, "mecEnergyE", 0f);
            Scribe_Values.Look(ref mecEnergyG, "mecEnergyG", 0f);
            Scribe_Values.Look(ref mecEnergyN, "mecEnergyN", 0f);
            Scribe_Values.Look(ref mecXIncludeMass, "mecXIncludeMass", true);
            Scribe_Values.Look(ref mecXIncludeHitPoints, "mecXIncludeHitPoints", true);
        }
    }

    public class OmniCrafterMod : Mod
    {
        public static OmniCrafterSettings Settings;
        private Vector2 _scrollPos = Vector2.zero;

        public OmniCrafterMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OmniCrafterSettings>();

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("Jeremie.Fully.Automatic.OmniCrafter");
            harmony.PatchAll();
        }

        public override string SettingsCategory() => "FullyAutomaticOmniCrafter";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Estimate total content height so the scroll view is big enough
            const float lineH = 28f;
            const float checkH = 30f;
            float contentH =
                checkH + 12f        // pinyin
                + 48f + 4f          // OmniCrafter section headers
                + checkH * 2 + 4f   // X toggles
                + lineH * 7 + 4f    // 7 coeff rows
                + 12f + 30f + 16f   // gap + reset btn + separator line
                + 48f + 4f          // MEC section headers
                + checkH * 2 + 4f   // MEC X toggles
                + lineH * 7 + 4f    // 7 MEC coeff rows
                + 12f + 30f;        // gap + MEC reset btn

            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, contentH);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

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
                Rect rowRect = listing.GetRect(lineH);
                float labelWidth = 100f;
                float fieldWidth = 70f;
                float gap = 6f;

                Rect labelRect  = new Rect(rowRect.x, rowRect.y, labelWidth, rowRect.height);
                Rect fieldRect  = new Rect(labelRect.xMax + gap, rowRect.y, fieldWidth, rowRect.height);
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

            CoeffRow("OmniCrafter_PowerCostA", ref Settings.powerCostA, -1000000f, 1000000f);
            CoeffRow("OmniCrafter_PowerCostB", ref Settings.powerCostB, -100f, 100f);
            CoeffRow("OmniCrafter_PowerCostC", ref Settings.powerCostC, -100f, 100f);
            CoeffRow("OmniCrafter_PowerCostD", ref Settings.powerCostD, -100f, 10f);
            CoeffRow("OmniCrafter_PowerCostE", ref Settings.powerCostE, -100f, 10f);
            CoeffRow("OmniCrafter_PowerCostG", ref Settings.powerCostG, -1000f, 1000f);
            CoeffRow("OmniCrafter_PowerCostN", ref Settings.powerCostN, -1000f, 1000f);

            listing.Gap();
            if (listing.ButtonText("OmniCrafter_PowerCostReset".Translate()))
            {
                Settings.powerCostA = 0f;
                Settings.powerCostB = 1f;
                Settings.powerCostC = 0f;
                Settings.powerCostD = 0f;
                Settings.powerCostE = 0f;
                Settings.powerCostG = 0f;
                Settings.powerCostN = 0f;
                Settings.xIncludeMass = false;
                Settings.xIncludeHitPoints = false;
            }

            // ── MEC energy formula ────────────────────────────────────────
            listing.GapLine(16f);
            listing.Label("OmniCrafter_MecEnergyFormula".Translate());
            listing.Label("OmniCrafter_MecEnergyFormulaDesc".Translate());
            listing.Gap(4f);

            listing.CheckboxLabeled(
                "OmniCrafter_MecXIncludeMass".Translate(),
                ref Settings.mecXIncludeMass,
                "OmniCrafter_MecXIncludeMassDesc".Translate());
            listing.CheckboxLabeled(
                "OmniCrafter_MecXIncludeHitPoints".Translate(),
                ref Settings.mecXIncludeHitPoints,
                "OmniCrafter_MecXIncludeHitPointsDesc".Translate());
            listing.Gap(4f);

            CoeffRow("OmniCrafter_MecEnergyA", ref Settings.mecEnergyA, -10000f, 10000f);
            CoeffRow("OmniCrafter_MecEnergyB", ref Settings.mecEnergyB, 0f, 100f);
            CoeffRow("OmniCrafter_MecEnergyC", ref Settings.mecEnergyC, 0f, 100f);
            CoeffRow("OmniCrafter_MecEnergyD", ref Settings.mecEnergyD, 0f, 10f);
            CoeffRow("OmniCrafter_MecEnergyE", ref Settings.mecEnergyE, 0f, 10f);
            CoeffRow("OmniCrafter_MecEnergyG", ref Settings.mecEnergyG, 0f, 100f);
            CoeffRow("OmniCrafter_MecEnergyN", ref Settings.mecEnergyN, 0f, 100f);

            listing.Gap();
            if (listing.ButtonText("OmniCrafter_MecEnergyReset".Translate()))
            {
                Settings.mecEnergyA = 0f;
                Settings.mecEnergyB = 1f;
                Settings.mecEnergyC = 0f;
                Settings.mecEnergyD = 0f;
                Settings.mecEnergyE = 0f;
                Settings.mecEnergyG = 0f;
                Settings.mecEnergyN = 0f;
                Settings.mecXIncludeMass = true;
                Settings.mecXIncludeHitPoints = true;
            }

            listing.End();
            Widgets.EndScrollView();
            Settings.Write();
        }
    }

}
