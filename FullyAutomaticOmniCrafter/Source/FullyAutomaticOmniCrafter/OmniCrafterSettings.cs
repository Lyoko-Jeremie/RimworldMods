using System.Collections.Generic;
using Verse;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 超维存储仓右键菜单的重新生成模式（§4 右键菜单性能优化）。
    /// 右键 vault 建筑时全部视图副本进入点击目标集，全量重生成开销 O(副本数×workgiver数)，
    /// 此设置控制菜单打开期间的重新生成频率以缓解卡顿。
    /// </summary>
    public enum VaultMenuRefreshMode
    {
        /// <summary>快照模式：打开后永不重新生成，点击选项时由 PreOptionChosen 校验兜底。性能最好，默认。</summary>
        Lazy,
        /// <summary>每 N 渲染帧全量重新生成一次（默认 60 帧）。</summary>
        Periodic,
        /// <summary>每 N 真实秒全量重新生成一次（最少 4 帧间隔），帧率无关。</summary>
        Adaptive,
    }

    /// <summary>
    /// 超维存储仓右键菜单的显示形态（每建筑独立，见 Building_OuterrealmVault.RightClickMenuMode）。
    /// 自定义模式：右键该 vault 时打开大型可搜索列表界面并暂停游戏（仅该 vault 的菜单生效）。
    /// </summary>
    public enum RightClickMenuMode
    {
        /// <summary>原版右键菜单（默认）。</summary>
        Vanilla,
        /// <summary>自制大列表（搜索 + 暂停 + 视口裁剪）。</summary>
        CustomList,
    }

    /// <summary>
    /// 超维存储仓启用自定义右键菜单后的内部样式（每建筑独立）。
    /// </summary>
    public enum VaultCustomMenuMode
    {
        /// <summary>兼容旧实现：一次生成全部目标的全部操作，再显示可搜索大列表。</summary>
        FullOptionList,
        /// <summary>性能模式：先显示轻量物品列表，选择物品后只生成该目标的操作。</summary>
        ItemThenOption,
    }

    // ─── Global Settings (cross-save favorites) ───────────────────────────────
    public class OmniCrafterSettings : ModSettings
    {
        public List<string> globalFavorites = new List<string>();
        public List<SurgeryTemplate> globalSurgeryTemplates = new List<SurgeryTemplate>();

        /// <summary>
        /// 超维存储仓右键菜单重新生成模式（§4）。
        /// </summary>
        public VaultMenuRefreshMode vaultMenuRefreshMode = VaultMenuRefreshMode.Lazy;

        /// <summary>Periodic 模式的重新生成间隔（渲染帧数，默认 60 = 1 秒 @60FPS）。</summary>
        public int vaultMenuRefreshFrames = 60;

        /// <summary>Adaptive 模式的重新生成间隔（百分之一秒，默认 100 = 1.0 秒）。</summary>
        public int vaultMenuRefreshHundredths = 100;

        /// <summary>
        /// 超维存储仓中的物品是否不计入殖民地财富（默认开启）。
        /// 开启后，vault 视图副本的市值从 WealthWatcher.CalculateWealthItems 结果中排除，
        /// 不影响袭击点数计算（借出/正在搬运中的物化副本仍正常计入，因其已真 Spawned 在地图上）。
        /// </summary>
        public bool vaultExcludeFromWealth = true;

        /// <summary>
        /// 是否启用拼音搜索（支持全拼和首字母缩写）。
        /// 可在搜索栏旁的"拼"按钮中切换，也可在 Mod 设置页面切换。
        /// </summary>
        public bool enablePinyinSearch = false;

        /// <summary>
        /// 万能重生平台操作界面是否加载并显示 Pawn 图像（头像）。
        /// 默认关闭：界面打开时不加载 Pawn 图像，显著加速界面打开速度；
        /// 打开界面后可在界面内或 Mod 设置页打开此开关，此时才加载并显示图像。
        /// </summary>
        public bool resurrectorShowPawnIcons = false;

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
        
        public OmniPhantomWall2_PassabilitySettings customPassabilitySettings = new OmniPhantomWall2_PassabilitySettings();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref globalFavorites, "globalFavorites", LookMode.Value);
            if (globalFavorites == null) globalFavorites = new List<string>();

            Scribe_Collections.Look(ref globalSurgeryTemplates, "globalSurgeryTemplates", LookMode.Deep);
            if (globalSurgeryTemplates == null) globalSurgeryTemplates = new List<SurgeryTemplate>();

            Scribe_Values.Look(ref enablePinyinSearch, "enablePinyinSearch", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars) enablePinyinSearch = false;
            Scribe_Values.Look(ref resurrectorShowPawnIcons, "resurrectorShowPawnIcons", false);

            // ── 超维存储仓物品是否计入殖民地财富（默认不计入） ──
            Scribe_Values.Look(ref vaultExcludeFromWealth, "vaultExcludeFromWealth", true);

            // ── §4 超维存储仓右键菜单刷新模式 ──
            Scribe_Values.Look(ref vaultMenuRefreshMode, "vaultMenuRefreshMode", VaultMenuRefreshMode.Lazy);
            Scribe_Values.Look(ref vaultMenuRefreshFrames, "vaultMenuRefreshFrames", 60);
            Scribe_Values.Look(ref vaultMenuRefreshHundredths, "vaultMenuRefreshHundredths", 100);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                vaultMenuRefreshFrames = Mathf.Max(1, vaultMenuRefreshFrames);
                vaultMenuRefreshHundredths = Mathf.Max(0, vaultMenuRefreshHundredths);
            }
            // ── §4 右键 vault 菜单显示形态：已迁移为每建筑独立字段（Building_OuterrealmVault），
            // 随存档保存，不再作为全局 Mod 设置（旧配置文件中的遗留节点由 Scribe 自动忽略）。
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
            
            Scribe_Deep.Look(ref customPassabilitySettings, "customPassabilitySettings");
            if (customPassabilitySettings == null) customPassabilitySettings = new OmniPhantomWall2_PassabilitySettings();
        }
    }

}
