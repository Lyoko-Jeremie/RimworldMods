using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// 一条气流回路的集总气袋。
    ///
    /// 只保存两个独立状态量：质量与温度。压力由理想气体方程派生，不单独积分，
    /// 所以永远不会出现「质量没变、压力却漂了」的伪状态。
    ///
    /// 单位约定：
    ///   VolumeM3           立方米
    ///   MassKg             千克
    ///   TemperatureK       开尔文
    ///   Rated*KgPerSec     千克每秒
    ///   PressurePa         帕斯卡（绝对压力）
    ///   1 AirFlow 单位 = 1 CFM = 0.000472 m³/s（CCC 的 baseAirFlow 就是 CFM 量级）
    /// </summary>
    public class AirLoop : IExposable
    {
        public const float GasConstantDryAir = 287f;
        public const float AirDensityKgPerM3 = 1.2f;
        public const float CubicMetresPerAirFlowUnit = 0.000472f;
        public const float KelvinAtTwentyOneCelsius = 294.15f;

        /// <summary>回路指纹：流量类型 + 回路内最小格索引。拓扑不变则指纹不变。</summary>
        public long Key;

        /// <summary>AirFlowType 的整数值，仅用于显示与区分冷热管。</summary>
        public int FlowType;

        /// <summary>回路总容积（m³）。管道 0.6 m³/格，设备 1.5 m³/台。</summary>
        public float VolumeM3 = 18f;

        /// <summary>回路内空气质量（kg）。唯一的质量账本。</summary>
        public float MassKg = 21.6f;

        /// <summary>质量加权温度（K）。</summary>
        public float TemperatureK = KelvinAtTwentyOneCelsius;

        /// <summary>全部进气风机的额定质量流量（kg/s）。</summary>
        public float RatedIntakeKgPerSec;

        /// <summary>全部出风口在满压差下的额定质量流量（kg/s）。</summary>
        public float RatedExhaustKgPerSec;

        /// <summary>风机能顶住的最大反向压差（Pa）。超过它进气降到 0。</summary>
        public float FanMaxRisePa = 2000f;

        /// <summary>出风口达到额定流量所需的回路-房间压差（Pa）。</summary>
        public float RatedPressurePa = 300f;

        public int ProducerCount;
        public int ConsumerCount;
        public int TempControlCount;

        /// <summary>由质量、温度、容积派生的绝对压力（Pa）。</summary>
        public float PressurePa
        {
            get
            {
                float volume = Mathf.Max(0.1f, VolumeM3);
                return MassKg * GasConstantDryAir * TemperatureK / volume;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Key, "key", 0L);
            Scribe_Values.Look(ref FlowType, "flowType", 0);
            Scribe_Values.Look(ref VolumeM3, "volumeM3", 18f);
            Scribe_Values.Look(ref MassKg, "massKg", 21.6f);
            Scribe_Values.Look(ref TemperatureK, "temperatureK", KelvinAtTwentyOneCelsius);
            Scribe_Values.Look(ref RatedIntakeKgPerSec, "ratedIntake", 0f);
            Scribe_Values.Look(ref RatedExhaustKgPerSec, "ratedExhaust", 0f);
            Scribe_Values.Look(ref FanMaxRisePa, "fanMaxRisePa", 2000f);
            Scribe_Values.Look(ref RatedPressurePa, "ratedPressurePa", 300f);
            Scribe_Values.Look(ref ProducerCount, "producerCount", 0);
            Scribe_Values.Look(ref ConsumerCount, "consumerCount", 0);
            Scribe_Values.Look(ref TempControlCount, "tempControlCount", 0);
        }

        /// <summary>按当前容积重设质量，保持温度。用于首次建立或读档后补账。</summary>
        public void ResetMassForVolume()
        {
            MassKg = Mathf.Max(0.1f, VolumeM3 * AirDensityKgPerM3);
            if (TemperatureK < 1f || TemperatureK > 2000f || float.IsNaN(TemperatureK))
            {
                TemperatureK = KelvinAtTwentyOneCelsius;
            }
        }

        /// <summary>
        /// 推进一个 AO 模拟步。
        /// </summary>
        /// <param name="dt">步长（秒），取 AO 实际推进的 tick 数 / 60。</param>
        /// <param name="intakeTemperatureK">进气温度（K），由调用方从 AO 场读出。</param>
        /// <param name="tempControlGainPerSecond">温控器的一阶惯性增益（1/s）。0 表示无温控。</param>
        /// <param name="targetTemperatureK">温控目标温度（K）。</param>
        /// <param name="roomPressurePa">出风口所在房间的静态气压（Pa）。</param>
        public LoopStep Step(
            float dt,
            float intakeTemperatureK,
            float tempControlGainPerSecond,
            float targetTemperatureK,
            float roomPressurePa)
        {
            LoopStep result = default(LoopStep);
            if (dt <= 0f)
            {
                return result;
            }

            // 1. 进气：风机背压曲线。回路压力低于房间时全速，高于房间时线性衰减到 0。
            float dp = roomPressurePa - PressurePa;
            float intakeRate = RatedIntakeKgPerSec * Mathf.Clamp01(1f + dp / Mathf.Max(1f, FanMaxRisePa));
            float massIn = Mathf.Min(intakeRate * dt, VolumeM3 * AirDensityKgPerM3 * 0.25f);
            if (massIn < 0f)
            {
                massIn = 0f;
            }

            // 2. 进气与回路存量混合。
            float massAfterIntake = MassKg + massIn;
            if (massAfterIntake > 0.001f)
            {
                TemperatureK = (MassKg * TemperatureK + massIn * intakeTemperatureK) / massAfterIntake;
                MassKg = massAfterIntake;
            }

            // 3. 温控：一阶惯性。增益由 CompTempControl.energyPerSecond 换算而来，
            //    时间常数与 CCC 原版 DeltaSmooth=96 的响应对齐，但按 AO 步长连续积分。
            if (tempControlGainPerSecond > 0f)
            {
                float alpha = 1f - Mathf.Exp(-tempControlGainPerSecond * dt);
                TemperatureK += (targetTemperatureK - TemperatureK) * alpha;
            }

            // 4. 出气：压力驱动。回路相对房间正压时才有流量，负压自动截止。
            float drive = Mathf.Clamp01((PressurePa - roomPressurePa) / Mathf.Max(1f, RatedPressurePa));
            float massOut = Mathf.Min(RatedExhaustKgPerSec * drive * dt, MassKg * 0.5f);
            if (massOut < 0f)
            {
                massOut = 0f;
            }
            MassKg -= massOut;

            // 5. 数值夹紧。质量下限保证压力不会塌到 0 导致下一步除零。
            MassKg = Mathf.Max(MassKg, VolumeM3 * 0.02f);
            TemperatureK = Mathf.Clamp(TemperatureK, 1f, 2000f);
            if (float.IsNaN(MassKg) || float.IsInfinity(MassKg))
            {
                MassKg = VolumeM3 * AirDensityKgPerM3;
            }
            if (float.IsNaN(TemperatureK) || float.IsInfinity(TemperatureK))
            {
                TemperatureK = KelvinAtTwentyOneCelsius;
            }

            result.IntakeMassKg = massIn;
            result.ExhaustMassKg = massOut;
            result.ExhaustTemperatureK = TemperatureK;
            return result;
        }
    }

    /// <summary>一步推进的结果，供调用方换算成 AO 的体积源。</summary>
    public struct LoopStep
    {
        public float IntakeMassKg;
        public float ExhaustMassKg;
        public float ExhaustTemperatureK;
    }
}
