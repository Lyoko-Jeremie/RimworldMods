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

        // Graph display range / scale options
        private float _powerGraphXMax = 500f;
        private float _powerGraphYMax = 500f;
        private bool  _powerLogX      = false;
        private bool  _powerLogY      = false;

        private float _mecGraphXMax = 500f;
        private float _mecGraphYMax = 500f;
        private bool  _mecLogX      = false;
        private bool  _mecLogY      = false;

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
        /// Draws a first-quadrant preview of the polynomial.
        /// Supports optional log10 scaling on both axes.
        /// </summary>
        private static void DrawFormulaGraph(
            Rect rect,
            float a, float b, float c, float d, float e, float g, float n,
            float xMax, float yMax, bool logX, bool logY)
        {
            const int   samples = 300;
            const float padL    = 52f;
            const float padB    = 22f;
            const float padR    = 8f;
            const float padT    = 8f;

            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f, 0.9f));

            float plotW = rect.width  - padL - padR;
            float plotH = rect.height - padT - padB;
            if (plotW <= 0f || plotH <= 0f) return;

            // Log-scale domain bounds (avoid log(0))
            float xOrigin = logX ? Mathf.Max(xMax / 10000f, 0.001f) : 0f;
            float yOrigin = logY ? Mathf.Max(yMax / 10000f, 0.001f) : 0f;

            float lxMin = logX ? Mathf.Log10(xOrigin) : 0f;
            float lxMax = logX ? Mathf.Log10(xMax)    : 1f;
            float lyMin = logY ? Mathf.Log10(yOrigin) : 0f;
            float lyMax = logY ? Mathf.Log10(yMax)    : 1f;

            // Normalised [0,1] mapping
            float NormX(float x)
            {
                if (logX) return x <= 0f ? 0f : (Mathf.Log10(x) - lxMin) / (lxMax - lxMin);
                return x / xMax;
            }
            float NormY(float y)
            {
                if (logY) return y <= 0f ? -1f : (Mathf.Log10(y) - lyMin) / (lyMax - lyMin);
                return y / yMax;
            }

            Vector2 ToScreen(float x, float y) => new Vector2(
                rect.x + padL + Mathf.Clamp01(NormX(x)) * plotW,
                rect.y + padT + plotH - Mathf.Clamp(NormY(y), 0f, 1f) * plotH
            );

            // Sample positions uniform in linear / log space
            float SampleX(int i)
            {
                float t = (float)i / samples;
                return logX ? Mathf.Pow(10f, lxMin + (lxMax - lxMin) * t) : xMax * t;
            }

            // ── Grid lines ────────────────────────────────────────────────
            Color gridColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            for (int gi = 1; gi <= 4; gi++)
            {
                float t  = gi / 4f;
                float gx = logX ? Mathf.Pow(10f, lxMin + (lxMax - lxMin) * t) : xMax * t;
                float gy = logY ? Mathf.Pow(10f, lyMin + (lyMax - lyMin) * t) : yMax * t;

                Vector2 gxB = ToScreen(gx, yOrigin);
                Widgets.DrawLine(gxB, new Vector2(gxB.x, rect.y + padT), gridColor, 1f);

                Vector2 gyL = ToScreen(xOrigin, gy);
                Widgets.DrawLine(gyL, new Vector2(rect.x + padL + plotW, gyL.y), gridColor, 1f);
            }

            // ── Axes ──────────────────────────────────────────────────────
            Color axisColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            Vector2 origin = ToScreen(xOrigin, yOrigin);
            Widgets.DrawLine(origin, ToScreen(xMax, yOrigin), axisColor, 1.5f);
            Widgets.DrawLine(origin, new Vector2(origin.x, rect.y + padT), axisColor, 1.5f);

            // ── Tick labels ───────────────────────────────────────────────
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            for (int ti = 0; ti <= 4; ti++)
            {
                float t  = ti / 4f;
                float tx = logX ? Mathf.Pow(10f, lxMin + (lxMax - lxMin) * t) : xMax * t;
                Vector2 tp = ToScreen(tx, yOrigin);
                Widgets.Label(new Rect(tp.x - 18f, tp.y + 2f, 36f, 16f), tx.ToString("G3"));
            }
            for (int ti = 1; ti <= 4; ti++)
            {
                float t  = ti / 4f;
                float ty = logY ? Mathf.Pow(10f, lyMin + (lyMax - lyMin) * t) : yMax * t;
                Vector2 tp = ToScreen(xOrigin, ty);
                Widgets.Label(new Rect(rect.x, tp.y - 8f, padL - 2f, 16f), ty.ToString("G3"));
            }
            GUI.color = Color.white;

            // ── Curve (clipped to plot area) ──────────────────────────────
            GUI.BeginClip(new Rect(rect.x + padL, rect.y + padT, plotW, plotH));
            Vector2? prev = null;
            for (int i = 0; i <= samples; i++)
            {
                float x  = SampleX(i);
                float y  = EvalPoly(a, b, c, d, e, g, n, x);
                float ny = NormY(y);
                if (ny < 0f) { prev = null; continue; }
                Vector2 cur = new Vector2(
                    Mathf.Clamp01(NormX(x)) * plotW,
                    plotH - Mathf.Clamp(ny, 0f, 1f) * plotH
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
                checkH + 12f                           // pinyin
                + 48f + 4f                             // OmniCrafter section headers
                + checkH * 2 + 4f                      // X toggles
                + lineH * 7 + 4f                       // 7 coeff rows
                + graphH + 4f + lineH * 2 + checkH * 2 // graph + xMax + yMax sliders + logX/logY
                + 12f + 30f + 16f                      // gap + reset btn + separator line
                + 48f + 4f                             // MEC section headers
                + checkH * 2 + 4f                      // MEC X toggles
                + lineH * 7 + 4f                       // 7 MEC coeff rows
                + graphH + 4f + lineH * 2 + checkH * 2 // MEC graph + xMax + yMax sliders + logX/logY
                + 12f + 30f;                           // gap + MEC reset btn

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
                Settings.powerCostN, _powerGraphXMax, _powerGraphYMax, _powerLogX, _powerLogY);

            // X-axis range slider
            {
                Rect row = listing.GetRect(lineH);
                float lw = 120f, gap = 6f;
                Widgets.Label(new Rect(row.x, row.y, lw, row.height),
                    "OmniCrafter_GraphXMax".Translate());
                _powerGraphXMax = Widgets.HorizontalSlider(
                    new Rect(row.x + lw + gap, row.y, row.width - lw - gap, row.height),
                    _powerGraphXMax, 1f, 10000f, middleAlignment: false,
                    label: _powerGraphXMax.ToString("G4"),
                    leftAlignedLabel: "1", rightAlignedLabel: "10000");
            }
            // Y-axis range slider
            {
                Rect row = listing.GetRect(lineH);
                float lw = 120f, gap = 6f;
                Widgets.Label(new Rect(row.x, row.y, lw, row.height),
                    "OmniCrafter_GraphYMax".Translate());
                _powerGraphYMax = Widgets.HorizontalSlider(
                    new Rect(row.x + lw + gap, row.y, row.width - lw - gap, row.height),
                    _powerGraphYMax, 1f, 1000000f, middleAlignment: false,
                    label: _powerGraphYMax.ToString("G4"),
                    leftAlignedLabel: "1", rightAlignedLabel: "1E6");
            }
            // Log-scale toggles
            listing.CheckboxLabeled("OmniCrafter_GraphLogX".Translate(), ref _powerLogX,
                "OmniCrafter_GraphLogXDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_GraphLogY".Translate(), ref _powerLogY,
                "OmniCrafter_GraphLogYDesc".Translate());

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
                Settings.mecEnergyN, _mecGraphXMax, _mecGraphYMax, _mecLogX, _mecLogY);

            // X-axis range slider
            {
                Rect row = listing.GetRect(lineH);
                float lw = 120f, gap = 6f;
                Widgets.Label(new Rect(row.x, row.y, lw, row.height),
                    "OmniCrafter_GraphXMax".Translate());
                _mecGraphXMax = Widgets.HorizontalSlider(
                    new Rect(row.x + lw + gap, row.y, row.width - lw - gap, row.height),
                    _mecGraphXMax, 1f, 10000f, middleAlignment: false,
                    label: _mecGraphXMax.ToString("G4"),
                    leftAlignedLabel: "1", rightAlignedLabel: "10000");
            }
            // Y-axis range slider
            {
                Rect row = listing.GetRect(lineH);
                float lw = 120f, gap = 6f;
                Widgets.Label(new Rect(row.x, row.y, lw, row.height),
                    "OmniCrafter_GraphYMax".Translate());
                _mecGraphYMax = Widgets.HorizontalSlider(
                    new Rect(row.x + lw + gap, row.y, row.width - lw - gap, row.height),
                    _mecGraphYMax, 1f, 1000000f, middleAlignment: false,
                    label: _mecGraphYMax.ToString("G4"),
                    leftAlignedLabel: "1", rightAlignedLabel: "1E6");
            }
            // Log-scale toggles
            listing.CheckboxLabeled("OmniCrafter_GraphLogX".Translate(), ref _mecLogX,
                "OmniCrafter_GraphLogXDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_GraphLogY".Translate(), ref _mecLogY,
                "OmniCrafter_GraphLogYDesc".Translate());

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
