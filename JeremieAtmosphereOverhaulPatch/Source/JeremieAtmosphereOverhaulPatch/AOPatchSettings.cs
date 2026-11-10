using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// 本 Mod 的设置。
    ///
    /// 设计目标：把「是否启用某个目标 Mod 的兼容」做成可持久化的独立开关，
    /// 这样 AO 日后自带兼容时，玩家只需关掉对应开关并重启即可回退，
    /// 无需删文件或拆 Mod。
    /// </summary>
    public class AOPatchSettings : ModSettings
    {
        /// <summary>总开关。关闭时所有模块都不安装补丁。</summary>
        public bool MasterEnabled = true;

        /// <summary>Good Climate 模块开关。</summary>
        public bool EnableGoodClimate = true;

        /// <summary>Good Climate 真空联动（关阀 → AO 透气面限速归零，解除后恢复原限速）。</summary>
        public bool EnableGcVacuumLink = true;

        /// <summary>Vanilla Temperature Expanded 模块开关。</summary>
        public bool EnableVanillaTemperatureExpanded = true;

        /// <summary>VTE 温控设备的物理场改写（关闭后只保留诊断）。</summary>
        public bool EnableVtePhysical = true;

        /// <summary>Centralized Climate Control 模块开关。</summary>
        public bool EnableCentralizedClimateControl = true;

        /// <summary>CCC 风口真空判定改用 AO 气压。</summary>
        public bool EnableCccVacuum = true;

        /// <summary>CCC 风口局部化（风口前 2 格额外写 AO 温度/风，默认关闭）。</summary>
        public bool EnableCccLocalizedVent = false;

        /// <summary>超维科技（当前 Mod）模块开关。</summary>
        public bool EnableOmniCrafter = true;

        /// <summary>强制稳定区（生物圈 + 穹顶）总开关。</summary>
        public bool EnableForceField = true;

        /// <summary>强制稳定区的更新间隔（tick），建议与 AO 模拟间隔对齐。</summary>
        public int ForceFieldIntervalTicks = 6;

        /// <summary>每次更新最多处理的格数（逐格路径的预算）。</summary>
        public int ForceFieldCellsPerInterval = 256;

        /// <summary>强制稳定区是否尝试消除风（逐格写，代价较高）。</summary>
        public bool ForceFieldKillWind = false;

        /// <summary>即使检测到 AO 已内置兼容，也强制安装本模块。</summary>
        public bool ForceOverrideBuiltInCompat = false;

        /// <summary>详细日志。</summary>
        public bool VerboseLogging = false;

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref MasterEnabled, "masterEnabled", true);
            Scribe_Values.Look(ref EnableGoodClimate, "enableGoodClimate", true);
            Scribe_Values.Look(ref EnableGcVacuumLink, "enableGcVacuumLink", true);
            Scribe_Values.Look(ref EnableVanillaTemperatureExpanded, "enableVanillaTemperatureExpanded", true);
            Scribe_Values.Look(ref EnableVtePhysical, "enableVtePhysical", true);
            Scribe_Values.Look(ref EnableCentralizedClimateControl, "enableCentralizedClimateControl", true);
            Scribe_Values.Look(ref EnableCccVacuum, "enableCccVacuum", true);
            Scribe_Values.Look(ref EnableCccLocalizedVent, "enableCccLocalizedVent", false);
            Scribe_Values.Look(ref EnableOmniCrafter, "enableOmniCrafter", true);
            Scribe_Values.Look(ref EnableForceField, "enableForceField", true);
            Scribe_Values.Look(ref ForceFieldIntervalTicks, "forceFieldIntervalTicks", 6);
            Scribe_Values.Look(ref ForceFieldCellsPerInterval, "forceFieldCellsPerInterval", 256);
            Scribe_Values.Look(ref ForceFieldKillWind, "forceFieldKillWind", false);
            Scribe_Values.Look(ref ForceOverrideBuiltInCompat, "forceOverrideBuiltInCompat", false);
            Scribe_Values.Look(ref VerboseLogging, "verboseLogging", false);

            // 读档后把可能被外部改坏的数值夹回安全范围
            if (ForceFieldIntervalTicks < 1)
            {
                ForceFieldIntervalTicks = 1;
            }

            if (ForceFieldIntervalTicks > 120)
            {
                ForceFieldIntervalTicks = 120;
            }

            if (ForceFieldCellsPerInterval < 16)
            {
                ForceFieldCellsPerInterval = 16;
            }

            if (ForceFieldCellsPerInterval > 4096)
            {
                ForceFieldCellsPerInterval = 4096;
            }
        }
    }
}
