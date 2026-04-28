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

        // X-axis range for the two formula preview graphs
        private float _powerGraphXMax = 500f;
        private float _mecGraphXMax   = 500f;

        public OmniCrafterMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OmniCrafterSettings>();

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("Jeremie.Fully.Automatic.OmniCrafter");
            harmony.PatchAll();
        }

        // ── Formula evaluation ────────────────────────────────────────────────
        private static float EvalPoly(float a, float b, float c, float d, float e, float g, float n, float x)
        {
            float logTerm = (g != 0f && x > 0f) ? g * Mathf.Log10(x) : 0f;
            float lnTerm  = (n != 0f && x > 0f) ? n * Mathf.Log(x)   : 0f;
            return a + b * x + c * x * x + d * x * x * x + e * x * x * x * x + logTerm + lnTerm;
        }

        // ── Graph drawing ─────────────────────────────────────────────────────
        /// <summary>
        /// Draws a first-quadrant (X≥0, Y≥0) preview of the polynomial.
        /// <paramref name="xMax"/> controls the visible X range.
        /// </summary>
        private static void DrawFormulaGraph(
            Rect rect,
            float a, float b, float c, float d, float e, float g, float n,
            float xMax)
        {
            const int samples  = 300;
            const float padL   = 42f;
            const float padB   = 22f;
            const float padR   = 8f;
            const float padT   = 8f;

            // Background
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f, 0.9f));

            float plotW = rect.width  - padL - padR;
            float plotH = rect.height - padT - padB;
            if (plotW <= 0f || plotH <= 0f) return;

            // Sample to find positive yMax
            float yMax = float.NegativeInfinity;
            float yMin = float.PositiveInfinity;
            for (int i = 0; i <= samples; i++)
            {
                float x = xMax * i / samples;
                float y = EvalPoly(a, b, c, d, e, g, n, x);
                if (y > yMax) yMax = y;
                if (y < yMin) yMin = y;
            }
            // Clamp to first quadrant: Y axis from 0 to yMax
            if (yMax <= 0f) yMax = 1f;
            float displayYMax = yMax;

            // Map data coords → screen coords (first quadrant only; Y<0 clipped)
            Vector2 ToScreen(float x, float y) => new Vector2(
                rect.x + padL + (x / xMax) * plotW,
                rect.y + padT + plotH - Mathf.Clamp(y / displayYMax, 0f, 1f) * plotH
            );

            Vector2 origin = ToScreen(0f, 0f);
            Vector2 xEnd   = ToScreen(xMax, 0f);
            Vector2 yEnd   = new Vector2(origin.x, rect.y + padT);

            // Grid lines (light)
            Color gridColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            for (int gi = 1; gi <= 4; gi++)
            {
                float gx = xMax * gi / 4f;
                float gy = displayYMax * gi / 4f;
                Vector2 gxTop = ToScreen(gx, 0f);
                Vector2 gxBot = new Vector2(gxTop.x, rect.y + padT);
                Widgets.DrawLine(gxTop, gxBot, gridColor, 1f);
                Vector2 gyLeft  = ToScreen(0f, gy);
                Vector2 gyRight = new Vector2(rect.x + padL + plotW, gyLeft.y);
                Widgets.DrawLine(gyLeft, gyRight, gridColor, 1f);
            }

            // Axes
            Color axisColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            Widgets.DrawLine(origin, xEnd, axisColor, 1.5f);
            Widgets.DrawLine(origin, yEnd, axisColor, 1.5f);

            // Axis labels
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            // X-axis ticks
            for (int ti = 0; ti <= 4; ti++)
            {
                float tx = xMax * ti / 4f;
                Vector2 tp = ToScreen(tx, 0f);
                Widgets.Label(new Rect(tp.x - 18f, tp.y + 2f, 36f, 16f),
                    tx.ToString("G3"));
            }
            // Y-axis ticks
            for (int ti = 1; ti <= 4; ti++)
            {
                float ty = displayYMax * ti / 4f;
                Vector2 tp = ToScreen(0f, ty);
                Widgets.Label(new Rect(rect.x, tp.y - 8f, padL - 2f, 16f),
                    ty.ToString("G3"));
            }
            GUI.color = Color.white;

            // Clip curve drawing to plot area
            GUI.BeginClip(new Rect(rect.x + padL, rect.y + padT, plotW, plotH));
            Vector2? prev = null;
            for (int i = 0; i <= samples; i++)
            {
                float x = xMax * i / samples;
                float y = EvalPoly(a, b, c, d, e, g, n, x);
                if (y < 0f) { prev = null; continue; }   // first-quadrant only
                // local coords inside clip
                Vector2 cur = new Vector2(
                    (x / xMax) * plotW,
                    plotH - Mathf.Clamp(y / displayYMax, 0f, 1f) * plotH
                );
                if (prev.HasValue)
                    Widgets.DrawLine(prev.Value, cur, Color.cyan, 1.5f);
                prev = cur;
            }
            GUI.EndClip();
        }

        public override string SettingsCategory() => "FullyAutomaticOmniCrafter";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Estimate total content height so the scroll view is big enough
            const float lineH  = 28f;
            const float checkH = 30f;
            const float graphH = 160f;
            float contentH =
                checkH + 12f              // pinyin
                + 48f + 4f               // OmniCrafter section headers
                + checkH * 2 + 4f        // X toggles
                + lineH * 7 + 4f         // 7 coeff rows
                + graphH + 4f + lineH    // graph + gap + xMax slider
                + 12f + 30f + 16f        // gap + reset btn + separator line
                + 48f + 4f               // MEC section headers
                + checkH * 2 + 4f        // MEC X toggles
                + lineH * 7 + 4f         // 7 MEC coeff rows
                + graphH + 4f + lineH    // MEC graph + gap + xMax slider
                + 12f + 30f;             // gap + MEC reset btn

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

            // ── Power formula graph ───────────────────────────────────────
            listing.Gap(4f);
            DrawFormulaGraph(listing.GetRect(graphH),
                Settings.powerCostA, Settings.powerCostB, Settings.powerCostC,
                Settings.powerCostD, Settings.powerCostE, Settings.powerCostG,
                Settings.powerCostN, _powerGraphXMax);

            // X-axis range slider
            {
                Rect xSliderRow = listing.GetRect(lineH);
                float labelW = 100f, gap = 6f;
                Widgets.Label(new Rect(xSliderRow.x, xSliderRow.y, labelW, xSliderRow.height),
                    "OmniCrafter_GraphXMax".Translate());
                _powerGraphXMax = Widgets.HorizontalSlider(
                    new Rect(xSliderRow.x + labelW + gap, xSliderRow.y,
                             xSliderRow.width - labelW - gap, xSliderRow.height),
                    _powerGraphXMax, 10f, 10000f, middleAlignment: false,
                    label: _powerGraphXMax.ToString("G4"),
                    leftAlignedLabel: "10", rightAlignedLabel: "10000");
            }

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

            // ── MEC formula graph ─────────────────────────────────────────
            listing.Gap(4f);
            DrawFormulaGraph(listing.GetRect(graphH),
                Settings.mecEnergyA, Settings.mecEnergyB, Settings.mecEnergyC,
                Settings.mecEnergyD, Settings.mecEnergyE, Settings.mecEnergyG,
                Settings.mecEnergyN, _mecGraphXMax);

            // X-axis range slider
            {
                Rect xSliderRow = listing.GetRect(lineH);
                float labelW = 100f, gap = 6f;
                Widgets.Label(new Rect(xSliderRow.x, xSliderRow.y, labelW, xSliderRow.height),
                    "OmniCrafter_GraphXMax".Translate());
                _mecGraphXMax = Widgets.HorizontalSlider(
                    new Rect(xSliderRow.x + labelW + gap, xSliderRow.y,
                             xSliderRow.width - labelW - gap, xSliderRow.height),
                    _mecGraphXMax, 10f, 10000f, middleAlignment: false,
                    label: _mecGraphXMax.ToString("G4"),
                    leftAlignedLabel: "10", rightAlignedLabel: "10000");
            }

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
