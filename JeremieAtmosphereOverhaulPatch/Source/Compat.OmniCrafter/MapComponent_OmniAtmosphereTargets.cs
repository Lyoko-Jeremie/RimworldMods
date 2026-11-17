using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 每张地图一份的「穹顶 / 生物圈大气目标值」。
    ///
    /// 设计取舍：目标值按地图共享，而不是每个穹顶各存一份。
    /// 理由是同图多个穹顶连成一片时，AO 场里只有一个连通域，
    /// 若每个穹顶各写各的目标，先写者会被后写者覆盖，结果取决于遍历顺序。
    /// 共享一份目标值后，同图所有穹顶天然一致。
    ///
    /// 气压单位跟随 AO 的对外读数习惯用 hPa（1 hPa = 100 Pa），
    /// 并提供「跟随地图大气」开关：开启时直接用引擎的环境基准气压，
    /// 避免玩家手填一个与环境差很多的值，导致穹顶边缘出现巨大的面通量。
    /// </summary>
    public class MapComponent_OmniAtmosphereTargets : MapComponent
    {
        public const float DefaultTemperature = 21f;
        public const float DefaultHumidity = 60f;
        public const float DefaultOxygenPercent = 21f;
        public const float DefaultPressureHpa = 1013.25f;

        /// <summary>目标气温，摄氏度。</summary>
        public float TargetTemperature = DefaultTemperature;

        /// <summary>目标相对湿度，百分比 0-100。</summary>
        public float TargetHumidity = DefaultHumidity;

        /// <summary>目标氧气体积百分比 0-100。</summary>
        public float TargetOxygenPercent = DefaultOxygenPercent;

        /// <summary>目标气压，hPa（AO 的对外单位）。</summary>
        public float TargetPressureHpa = DefaultPressureHpa;

        /// <summary>是否跟随地图大气环境基准气压。开启时忽略 TargetPressureHpa。</summary>
        public bool PressureFollowsAmbient = true;

        public MapComponent_OmniAtmosphereTargets(Map map) : base(map)
        {
        }

        public static MapComponent_OmniAtmosphereTargets Get(Map map)
        {
            return map == null ? null : map.GetComponent<MapComponent_OmniAtmosphereTargets>();
        }

        public float TargetPressurePa
        {
            get { return TargetPressureHpa * 100f; }
        }

        private object cachedEngine;

        /// <summary>地图环境基准气压（hPa）。引擎尚未就绪时返回默认值。</summary>
        public float AmbientPressureHpa
        {
            get
            {
                if (cachedEngine == null)
                {
                    return DefaultPressureHpa;
                }
                float pa = AtmosphereFieldBridge.GetAmbientSurfacePressurePa(cachedEngine);
                return pa > 0f ? pa / 100f : 0f;
            }
        }

        /// <summary>本步实际生效的目标气压（Pa）。跟随时取环境基准；真空图或引擎未就绪时退回玩家设定，避免把穹顶抽成真空。</summary>
        public float ResolveTargetPressurePa(float ambientPa)
        {
            return PressureFollowsAmbient && ambientPa > 0f ? ambientPa : TargetPressureHpa * 100f;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref TargetTemperature, "aoOmniTargetTemperature", DefaultTemperature);
            Scribe_Values.Look(ref TargetHumidity, "aoOmniTargetHumidity", DefaultHumidity);
            Scribe_Values.Look(ref TargetOxygenPercent, "aoOmniTargetOxygen", DefaultOxygenPercent);
            Scribe_Values.Look(ref PressureFollowsAmbient, "aoOmniPressureFollowsAmbient", true);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 旧版本存的是 kPa（key: aoOmniTargetPressureKpa），读到就换算成 hPa。
                float legacyKpa = 0f;
                Scribe_Values.Look(ref legacyKpa, "aoOmniTargetPressureKpa", 0f);
                Scribe_Values.Look(ref TargetPressureHpa, "aoOmniTargetPressureHpa", DefaultPressureHpa);
                if (legacyKpa > 0.001f)
                {
                    TargetPressureHpa = legacyKpa * 10f;
                }
            }
            else
            {
                Scribe_Values.Look(ref TargetPressureHpa, "aoOmniTargetPressureHpa", DefaultPressureHpa);
            }
        }

        private int nextPrepareTick;

        /// <summary>
        /// 主线程预热：把引擎上下文与穹顶格数组准备好。
        /// AO 的模拟在独立 Task 线程跑，Task 里不能安全枚举建筑列表，
        /// 所以所有「查地图 / 遍历建筑」的工作都放在这里，模拟线程只读缓存。
        /// </summary>
        public override void MapComponentTick()
        {
            base.MapComponentTick();

            int now = Find.TickManager.TicksGame;
            if (now < nextPrepareTick)
            {
                return;
            }
            nextPrepareTick = now + 30;

            AtmosphereFieldBridge.Resolve();
            object component = AtmosphereFieldBridge.FindComponent(map);
            if (component == null)
            {
                return;
            }

            object engine = AtmosphereFieldBridge.GetEngine(component);
            if (engine == null)
            {
                return;
            }

            cachedEngine = engine;
            Patch_AbsoluteIsolation.Register(engine, component, map, this);

            // 主线程预热温度补丁的组件引用，避免模拟线程首次查询时遍历地图组件列表。
            Patch_OmniAtmosphereTemperature.Warm(map);

            // 穹顶矩形与生物圈区域都可能被玩家随时改动，没有可靠的失效通知，
            // 这里每 30 tick 主动重建一次。判定集合很小，成本可忽略。
            OmniDomeZoneCache.MarkDirty(map);
            OmniDomeZoneCache.EnsureBuilt(map);
        }
    }
}
