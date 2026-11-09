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

        /// <summary>Good Climate 真空联动（关阀 → AO 透气面限速归零）。</summary>
        public bool EnableGcVacuumLink = true;

        /// <summary>Vanilla Temperature Expanded 模块开关。</summary>
        public bool EnableVanillaTemperatureExpanded = true;

        /// <summary>Centralized Climate Control 模块开关。</summary>
        public bool EnableCentralizedClimateControl = true;

        /// <summary>超维科技（当前 Mod）模块开关。</summary>
        public bool EnableOmniCrafter = true;

        /// <summary>详细日志。</summary>
        public bool VerboseLogging = false;

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref MasterEnabled, "masterEnabled", true);
            Scribe_Values.Look(ref EnableGoodClimate, "enableGoodClimate", true);
            Scribe_Values.Look(ref EnableGcVacuumLink, "enableGcVacuumLink", true);
            Scribe_Values.Look(ref EnableVanillaTemperatureExpanded, "enableVanillaTemperatureExpanded", true);
            Scribe_Values.Look(ref EnableCentralizedClimateControl, "enableCentralizedClimateControl", true);
            Scribe_Values.Look(ref EnableOmniCrafter, "enableOmniCrafter", true);
            Scribe_Values.Look(ref VerboseLogging, "verboseLogging", false);
        }
    }
}
