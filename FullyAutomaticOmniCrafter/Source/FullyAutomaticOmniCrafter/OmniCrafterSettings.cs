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
        private float _powerGraphXMin = 0f;
        private float _powerGraphXMax = 500f;
        private float _powerGraphYMin = 0f;
        private float _powerGraphYMax = 500f;
        private bool  _powerLogX      = false;
        private bool  _powerLogY      = false;

        private float _mecGraphXMin = 0f;
        private float _mecGraphXMax = 500f;
        private float _mecGraphYMin = 0f;
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
        /// Draws a preview of the polynomial over [xMin,xMax] × [yMin,yMax].
        /// Supports optional log10 scaling (only when the respective min > 0).
        /// Draws the X-axis (y=0) and Y-axis (x=0) when they fall within the range,
        /// enabling display of all four quadrants.
        /// </summary>
        private static void DrawFormulaGraph(
            Rect rect,
            float a, float b, float c, float d, float e, float g, float n,
            float xMin, float xMax, float yMin, float yMax, bool logX, bool logY)
        {
            const int   samples = 300;
            const float padL    = 56f;
            const float padB    = 22f;
            const float padR    = 8f;
            const float padT    = 8f;

            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f, 0.9f));

            float plotW = rect.width  - padL - padR;
            float plotH = rect.height - padT - padB;
            if (plotW <= 0f || plotH <= 0f) return;

            // Log scale is only valid when the entire range is positive
            bool useLogX = logX && xMin > 0f && xMax > 0f;
            bool useLogY = logY && yMin > 0f && yMax > 0f;

            float lxLo = useLogX ? Mathf.Log10(Mathf.Max(xMin, 1e-6f)) : 0f;
            float lxHi = useLogX ? Mathf.Log10(xMax) : 0f;
            float lyLo = useLogY ? Mathf.Log10(Mathf.Max(yMin, 1e-6f)) : 0f;
            float lyHi = useLogY ? Mathf.Log10(yMax) : 0f;

            // Normalised [0,1] mapping (0 = min edge, 1 = max edge)
            float NormX(float x)
            {
                if (useLogX) return x <= 0f ? 0f : (Mathf.Log10(x) - lxLo) / (lxHi - lxLo);
                return (xMax != xMin) ? (x - xMin) / (xMax - xMin) : 0f;
            }
            float NormY(float y)
            {
                if (useLogY) return y <= 0f ? -1f : (Mathf.Log10(y) - lyLo) / (lyHi - lyLo);
                return (yMax != yMin) ? (y - yMin) / (yMax - yMin) : 0f;
            }

            // Sample X-values uniformly in linear or log space
            float SampleX(int i)
            {
                float t = (float)i / samples;
                if (useLogX) return Mathf.Pow(10f, lxLo + (lxHi - lxLo) * t);
                return xMin + (xMax - xMin) * t;
            }

            // Screen-space helpers (no clamping, let clip handle it)
            float Sx(float nx) => rect.x + padL + nx * plotW;
            float Sy(float ny) => rect.y + padT + plotH - ny * plotH;

            // ── Grid lines ────────────────────────────────────────────────
            Color gridColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            for (int gi = 1; gi <= 4; gi++)
            {
                float t  = gi / 4f;
                float gx = useLogX ? Mathf.Pow(10f, lxLo + (lxHi - lxLo) * t) : xMin + (xMax - xMin) * t;
                float gy = useLogY ? Mathf.Pow(10f, lyLo + (lyHi - lyLo) * t) : yMin + (yMax - yMin) * t;

                float sx = Sx(NormX(gx));
                Widgets.DrawLine(new Vector2(sx, rect.y + padT),
                                 new Vector2(sx, rect.y + padT + plotH), gridColor, 1f);

                float sy = Sy(NormY(gy));
                Widgets.DrawLine(new Vector2(rect.x + padL, sy),
                                 new Vector2(rect.x + padL + plotW, sy), gridColor, 1f);
            }

            // ── Border ────────────────────────────────────────────────────
            Color borderColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);
            float bL = rect.x + padL, bR = rect.x + padL + plotW;
            float bT = rect.y + padT, bB = rect.y + padT + plotH;
            Widgets.DrawLine(new Vector2(bL, bT), new Vector2(bR, bT), borderColor, 1f);
            Widgets.DrawLine(new Vector2(bL, bB), new Vector2(bR, bB), borderColor, 1f);
            Widgets.DrawLine(new Vector2(bL, bT), new Vector2(bL, bB), borderColor, 1f);
            Widgets.DrawLine(new Vector2(bR, bT), new Vector2(bR, bB), borderColor, 1f);

            // ── Axes (drawn at x=0 / y=0 when within the visible range) ──
            Color axisColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            // Horizontal axis (y = 0)
            if (yMin <= 0f && yMax >= 0f)
            {
                float sy = Sy(NormY(0f));
                Widgets.DrawLine(new Vector2(bL, sy), new Vector2(bR, sy), axisColor, 1.5f);
            }
            // Vertical axis (x = 0)
            if (xMin <= 0f && xMax >= 0f)
            {
                float sx = Sx(NormX(0f));
                Widgets.DrawLine(new Vector2(sx, bT), new Vector2(sx, bB), axisColor, 1.5f);
            }

            // ── Tick labels ───────────────────────────────────────────────
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            for (int ti = 0; ti <= 4; ti++)
            {
                float t  = ti / 4f;
                float tx = useLogX ? Mathf.Pow(10f, lxLo + (lxHi - lxLo) * t) : xMin + (xMax - xMin) * t;
                float sx = Sx(NormX(tx));
                Widgets.Label(new Rect(sx - 18f, bB + 2f, 36f, 16f), tx.ToString("G3"));
            }
            for (int ti = 0; ti <= 4; ti++)
            {
                float t  = ti / 4f;
                float ty = useLogY ? Mathf.Pow(10f, lyLo + (lyHi - lyLo) * t) : yMin + (yMax - yMin) * t;
                float sy = Sy(NormY(ty));
                Widgets.Label(new Rect(rect.x, sy - 8f, padL - 4f, 16f), ty.ToString("G3"));
            }
            GUI.color = Color.white;

            // ── Curve (clipped to plot area) ──────────────────────────────
            GUI.BeginClip(new Rect(bL, bT, plotW, plotH));
            Vector2? prev = null;
            for (int i = 0; i <= samples; i++)
            {
                float x  = SampleX(i);
                float y  = EvalPoly(a, b, c, d, e, g, n, x);
                float ny = NormY(y);
                // Discard points far outside the visible band to keep GL clean
                if (ny < -0.1f || ny > 1.1f) { prev = null; continue; }
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
            // Graph is square: height == available listing width
            float graphH = inRect.width - 20f;

            const float lineH  = 28f;
            const float checkH = 30f;

            // Estimate total scrollable content height
            float sectionH =
                48f + 4f                  // 2 section header labels + gap
                + checkH * 2 + 4f         // 2 X-composition toggles + gap
                + lineH * 7 + 4f          // 7 coefficient rows + gap
                + 4f + graphH             // inner gap + square graph
                + lineH * 4               // xMin, xMax, yMin, yMax range rows
                + checkH * 2              // logX + logY toggles
                + 12f + 30f;              // gap + reset button

            float contentH = checkH + 12f + sectionH + 16f + sectionH;

            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, contentH);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            // ── Pinyin search ────────────────────────────────────────────
            listing.CheckboxLabeled(
                "OmniCrafter_EnablePinyinSearch".Translate(),
                ref Settings.enablePinyinSearch);

            // ── Helper: coefficient row (label + text field + slider) ────
            void CoeffRow(string labelKey, ref float val, float min, float max)
            {
                Rect rowRect = listing.GetRect(lineH);
                const float lw = 110f, fw = 70f, gap = 6f;
                Widgets.Label(new Rect(rowRect.x, rowRect.y, lw, rowRect.height), labelKey.Translate());
                string valStr = val.ToString("G4");
                string edited = Widgets.TextField(new Rect(rowRect.x + lw + gap, rowRect.y, fw, rowRect.height), valStr);
                if (edited != valStr && float.TryParse(edited, out float parsed))
                    val = Mathf.Clamp(parsed, min, max);
                val = Widgets.HorizontalSlider(
                    new Rect(rowRect.x + lw + fw + gap * 2f, rowRect.y, rowRect.width - lw - fw - gap * 2f, rowRect.height),
                    val, min, max, middleAlignment: false, label: null,
                    leftAlignedLabel: min.ToString("G3"), rightAlignedLabel: max.ToString("G3"));
                val = Mathf.Clamp(val, min, max);
            }

            // ── Helper: range slider row (label + text field + slider) ───
            void RangeRow(string labelKey, ref float val, float minBound, float maxBound)
            {
                Rect rowRect = listing.GetRect(lineH);
                const float lw = 120f, fw = 80f, gap = 6f;
                Widgets.Label(new Rect(rowRect.x, rowRect.y, lw, rowRect.height), labelKey.Translate());
                string valStr = val.ToString("G5");
                string edited = Widgets.TextField(new Rect(rowRect.x + lw + gap, rowRect.y, fw, rowRect.height), valStr);
                if (edited != valStr && float.TryParse(edited, out float parsed))
                    val = Mathf.Clamp(parsed, minBound, maxBound);
                val = Widgets.HorizontalSlider(
                    new Rect(rowRect.x + lw + fw + gap * 2f, rowRect.y, rowRect.width - lw - fw - gap * 2f, rowRect.height),
                    val, minBound, maxBound, middleAlignment: false, label: null,
                    leftAlignedLabel: minBound.ToString("G3"), rightAlignedLabel: maxBound.ToString("G3"));
                val = Mathf.Clamp(val, minBound, maxBound);
            }

            // ════════════════════════════════════════════════════════════
            // OmniCrafter power-cost formula section
            // ════════════════════════════════════════════════════════════
            listing.Gap();
            listing.Label("OmniCrafter_PowerCostFormula".Translate());
            listing.Label("OmniCrafter_PowerCostFormulaDesc".Translate());
            listing.Gap(4f);

            listing.CheckboxLabeled("OmniCrafter_XIncludeMass".Translate(),
                ref Settings.xIncludeMass, "OmniCrafter_XIncludeMassDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_XIncludeHitPoints".Translate(),
                ref Settings.xIncludeHitPoints, "OmniCrafter_XIncludeHitPointsDesc".Translate());
            listing.Gap(4f);

            CoeffRow("OmniCrafter_PowerCostA", ref Settings.powerCostA, -1000000f, 1000000f);
            CoeffRow("OmniCrafter_PowerCostB", ref Settings.powerCostB, -100f,     100f);
            CoeffRow("OmniCrafter_PowerCostC", ref Settings.powerCostC, -100f,     100f);
            CoeffRow("OmniCrafter_PowerCostD", ref Settings.powerCostD, -100f,     100f);
            CoeffRow("OmniCrafter_PowerCostE", ref Settings.powerCostE, -100f,     100f);
            CoeffRow("OmniCrafter_PowerCostG", ref Settings.powerCostG, -1000f,    1000f);
            CoeffRow("OmniCrafter_PowerCostN", ref Settings.powerCostN, -1000f,    1000f);

            // ── Power formula graph (1:1 square) ─────────────────────────
            listing.Gap(4f);
            DrawFormulaGraph(listing.GetRect(graphH),
                Settings.powerCostA, Settings.powerCostB, Settings.powerCostC,
                Settings.powerCostD, Settings.powerCostE, Settings.powerCostG,
                Settings.powerCostN,
                _powerGraphXMin, _powerGraphXMax,
                _powerGraphYMin, _powerGraphYMax,
                _powerLogX, _powerLogY);

            RangeRow("OmniCrafter_GraphXMin", ref _powerGraphXMin, -10000f,  0f);
            RangeRow("OmniCrafter_GraphXMax", ref _powerGraphXMax,  1f,  10000f);
            RangeRow("OmniCrafter_GraphYMin", ref _powerGraphYMin, -1000000f, 0f);
            RangeRow("OmniCrafter_GraphYMax", ref _powerGraphYMax,  1f,  1000000f);

            listing.CheckboxLabeled("OmniCrafter_GraphLogX".Translate(), ref _powerLogX,
                "OmniCrafter_GraphLogXDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_GraphLogY".Translate(), ref _powerLogY,
                "OmniCrafter_GraphLogYDesc".Translate());

            listing.Gap();
            if (listing.ButtonText("OmniCrafter_PowerCostReset".Translate()))
            {
                Settings.powerCostA = 0f; Settings.powerCostB = 1f;
                Settings.powerCostC = 0f; Settings.powerCostD = 0f;
                Settings.powerCostE = 0f; Settings.powerCostG = 0f;
                Settings.powerCostN = 0f;
                Settings.xIncludeMass = false; Settings.xIncludeHitPoints = false;
            }

            // ════════════════════════════════════════════════════════════
            // MEC energy formula section
            // ════════════════════════════════════════════════════════════
            listing.GapLine(16f);
            listing.Label("OmniCrafter_MecEnergyFormula".Translate());
            listing.Label("OmniCrafter_MecEnergyFormulaDesc".Translate());
            listing.Gap(4f);

            listing.CheckboxLabeled("OmniCrafter_MecXIncludeMass".Translate(),
                ref Settings.mecXIncludeMass, "OmniCrafter_MecXIncludeMassDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_MecXIncludeHitPoints".Translate(),
                ref Settings.mecXIncludeHitPoints, "OmniCrafter_MecXIncludeHitPointsDesc".Translate());
            listing.Gap(4f);

            CoeffRow("OmniCrafter_MecEnergyA", ref Settings.powerCostA, -1000000f, 1000000f);
            CoeffRow("OmniCrafter_MecEnergyB", ref Settings.powerCostB, -100f,     100f);
            CoeffRow("OmniCrafter_MecEnergyC", ref Settings.powerCostC, -100f,     100f);
            CoeffRow("OmniCrafter_MecEnergyD", ref Settings.powerCostD, -100f,     100f);
            CoeffRow("OmniCrafter_MecEnergyE", ref Settings.powerCostE, -100f,     100f);
            CoeffRow("OmniCrafter_MecEnergyG", ref Settings.powerCostG, -1000f,    1000f);
            CoeffRow("OmniCrafter_MecEnergyN", ref Settings.powerCostN, -1000f,    1000f);

            // ── MEC formula graph (1:1 square) ────────────────────────────
            listing.Gap(4f);
            DrawFormulaGraph(listing.GetRect(graphH),
                Settings.mecEnergyA, Settings.mecEnergyB, Settings.mecEnergyC,
                Settings.mecEnergyD, Settings.mecEnergyE, Settings.mecEnergyG,
                Settings.mecEnergyN,
                _mecGraphXMin, _mecGraphXMax,
                _mecGraphYMin, _mecGraphYMax,
                _mecLogX, _mecLogY);

            RangeRow("OmniCrafter_GraphXMin", ref _mecGraphXMin, -10000f,  0f);
            RangeRow("OmniCrafter_GraphXMax", ref _mecGraphXMax,  1f,  10000f);
            RangeRow("OmniCrafter_GraphYMin", ref _mecGraphYMin, -1000000f, 0f);
            RangeRow("OmniCrafter_GraphYMax", ref _mecGraphYMax,  1f,  1000000f);

            listing.CheckboxLabeled("OmniCrafter_GraphLogX".Translate(), ref _mecLogX,
                "OmniCrafter_GraphLogXDesc".Translate());
            listing.CheckboxLabeled("OmniCrafter_GraphLogY".Translate(), ref _mecLogY,
                "OmniCrafter_GraphLogYDesc".Translate());

            listing.Gap();
            if (listing.ButtonText("OmniCrafter_MecEnergyReset".Translate()))
            {
                Settings.mecEnergyA = 0f; Settings.mecEnergyB = 1f;
                Settings.mecEnergyC = 0f; Settings.mecEnergyD = 0f;
                Settings.mecEnergyE = 0f; Settings.mecEnergyG = 0f;
                Settings.mecEnergyN = 0f;
                Settings.mecXIncludeMass = true; Settings.mecXIncludeHitPoints = true;
            }

            listing.End();
            Widgets.EndScrollView();
            Settings.Write();
        }
    }

}
