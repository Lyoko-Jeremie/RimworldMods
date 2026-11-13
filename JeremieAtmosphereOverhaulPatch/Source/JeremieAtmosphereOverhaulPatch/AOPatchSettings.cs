using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// 本 Mod 的设置。
    ///
    /// 字段名一旦发布就不要改，否则老存档的设置会丢。
    /// </summary>
    public class AOPatchSettings : ModSettings
    {
        /// <summary>总开关。关闭时任何模块都不安装补丁。</summary>
        public bool MasterEnabled = true;

        // ---------------------------------------------------- ProxyHeat 替代机制

        /// <summary>范围效果模块总开关。</summary>
        public bool ProxyHeatRelayEnabled = true;

        /// <summary>空气模式：把范围温差注入 AO 的气温场。</summary>
        public bool AirModeEnabled = true;

        /// <summary>地暖模式：把范围温差注入 AO 的地温场。</summary>
        public bool GroundModeEnabled;

        /// <summary>喷流模式：向建筑四周八个径向注册 AO 原生 AirMover。</summary>
        public bool JetModeEnabled;

        /// <summary>
        /// 范围强度（℃/秒）。默认 8，即每 60 tick 补 8℃（每 tick 0.133℃）。
        /// 闭环会在接近目标温度时自动减速，所以调大不会冲过头。
        /// 注意 key 用 <c>rangeStrength</c>：旧版的 1℃/秒存在另一个 key 下，
        /// 换 key 是为了让老存档也能拿到新的默认值。
        /// </summary>
        public float RangeStrengthCelsiusPerSecond = 8f;

        /// <summary>喷流出风速度（米/秒）。AO 自己的喷流 default 是 halfSpeed 30 / maxSpeed 90。</summary>
        public float JetSpeedMetresPerSecond = 60f;

        /// <summary>多源重叠时是否累加温差。关闭则取最大值。</summary>
        public bool AllowTemperatureStacking = true;

        /// <summary>是否允许制冷方向产生负增量。</summary>
        public bool AllowActiveCooling;

        /// <summary>制冷方向的速率上限（℃/tick）。</summary>
        public float MaxCoolPerTick = 0.3f;

        /// <summary>新生成的源默认是否包含室内格。</summary>
        public bool DefaultIndoorEffect;

        /// <summary>VTE-ProxyHeatPatch 蓝信只发一次。</summary>
        public bool WarnedVtePatch;

        /// <summary>在日志里打印范围效果的状态摘要，用于排查「没生效」。</summary>
        public bool ProxyHeatDebugLog = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref MasterEnabled, "masterEnabled", true);

            Scribe_Values.Look(ref ProxyHeatRelayEnabled, "proxyHeatRelayEnabled", true);
            Scribe_Values.Look(ref AirModeEnabled, "airModeEnabled", true);
            Scribe_Values.Look(ref GroundModeEnabled, "groundModeEnabled", false);
            Scribe_Values.Look(ref JetModeEnabled, "jetModeEnabled", false);
            Scribe_Values.Look(ref RangeStrengthCelsiusPerSecond, "rangeStrength", 8f);
            Scribe_Values.Look(ref JetSpeedMetresPerSecond, "jetSpeedMetresPerSecond", 60f);
            Scribe_Values.Look(ref AllowTemperatureStacking, "allowTemperatureStacking", true);
            Scribe_Values.Look(ref AllowActiveCooling, "allowActiveCooling", false);
            Scribe_Values.Look(ref MaxCoolPerTick, "maxCoolPerTick", 0.3f);
            Scribe_Values.Look(ref DefaultIndoorEffect, "defaultIndoorEffect", false);
            Scribe_Values.Look(ref WarnedVtePatch, "warnedVtePatch", false);
            Scribe_Values.Look(ref ProxyHeatDebugLog, "proxyHeatDebugLog", true);
        }
    }
}
