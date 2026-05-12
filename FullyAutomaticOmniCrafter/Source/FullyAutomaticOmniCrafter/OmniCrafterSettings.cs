using System.Collections.Generic;
using Verse;
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

}
