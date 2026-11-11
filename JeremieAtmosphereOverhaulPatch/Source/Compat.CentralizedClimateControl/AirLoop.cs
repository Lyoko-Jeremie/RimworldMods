using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// 一条气流回路的集总气袋。
    ///
    /// 保存四个独立状态量：质量、温度、氧百分比、水汽含量。压力由理想气体方程派生，
    /// 不单独积分，所以永远不会出现「质量没变、压力却漂了」的伪状态。
    ///
    /// 单位约定：
    ///   VolumeM3           立方米
    ///   MassKg             千克
    ///   TemperatureK       开尔文
    ///   Rated*KgPerSec     千克每秒
    ///   PressurePa         帕斯卡（绝对压力）
    ///   OxygenPercent      体积百分比 0-100
    ///   VaporContent       与 AO 的 Moisture 场同量纲（即 VaporCapacity 的量纲），
    ///                      因为出气时直接把这个差值交给 AddMoistureDelta，不做换算
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

        /// <summary>回路内氧体积百分比（0-100）。与温度一起按质量加权混合。</summary>
        public float OxygenPercent = 21f;

        /// <summary>回路内水汽含量，量纲同 AO 的 Moisture 场。</summary>
        public float VaporContent;

        /// <summary>
        /// 水汽是否已从现场标定过。不存档：读档后为 false，会触发一次懒标定，
        /// 避免用默认 0 去「吸干」房间湿度。
        /// </summary>
        public bool VaporValid;

        /// <summary>全部进气风机的额定质量流量（kg/s）。</summary>
        public float RatedIntakeKgPerSec;

        /// <summary>全部出风口在满压差下的额定质量流量（kg/s）。</summary>
        public float RatedExhaustKgPerSec;

        /// <summary>风机能顶住的最大反向压差（Pa）。超过它进气降到 0。</summary>
        public float FanMaxRisePa = 2000f;

        /// <summary>出风口达到额定流量所需的回路-房间压差（Pa）。</summary>
        public float RatedPressurePa = 300f;

        /// <summary>把管道沿程损失折进额定压差后的有效值（Pa）。由拓扑重算，不入存档。</summary>
        public float EffectivePressurePa = 300f;

        /// <summary>把管道沿程损失折进风机扬程后的有效值（Pa）。由拓扑重算，不入存档。</summary>
        public float EffectiveFanMaxRisePa = 2000f;

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
            Scribe_Values.Look(ref OxygenPercent, "oxygenPercent", 21f);
            Scribe_Values.Look(ref VaporContent, "vaporContent", 0f);
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
        /// <param name="intakeCapacityKgPerSec">当前可用风机的实际质量流量上限（kg/s）。</param>
        /// <param name="intakeTemperatureK">进气温度（K），由调用方从 AO 场读出。</param>
        /// <param name="intakeOxygenPercent">进气氧百分比（0-100）。</param>
        /// <param name="intakeVapor">进气水汽含量，量纲同 AO Moisture 场。</param>
        /// <param name="tempControlGainPerSecond">温控器的一阶惯性增益（1/s）。0 表示无温控。</param>
        /// <param name="targetTemperatureK">温控目标温度（K）。</param>
        /// <param name="roomPressurePa">出风口所在房间的静态气压（Pa）。</param>
        public LoopStep Step(
            float dt,
            float intakeCapacityKgPerSec,
            float intakeTemperatureK,
            float intakeOxygenPercent,
            float intakeVapor,
            float tempControlGainPerSecond,
            float targetTemperatureK,
            float roomPressurePa)
        {
            LoopStep result = default(LoopStep);
            if (dt <= 0f)
            {
                return result;
            }

            float massBefore = MassKg;

            // 1. 进气：风机背压曲线。回路压力低于房间时全速，高于房间时线性衰减到 0。
            //    有效扬程已被管道沿程损失压低（EffectiveFanMaxRisePa）。
            float dp = roomPressurePa - PressurePa;
            float intakeRate = Mathf.Max(0f, intakeCapacityKgPerSec) *
                               Mathf.Clamp01(1f + dp / Mathf.Max(1f, EffectiveFanMaxRisePa));

            // 出气驱动力。
            //
            // CCC 的风机是「主动送风设备」，不是「被动泄压阀」：它把空气压进房间，
            // 房间气压高它也要顶着压降继续送。所以这里给一个 0.5~1.5 的工作区间，
            // 而不是 0~1 —— 一旦允许取 0，回路压力只要比房间低一个沿程损失，
            // 出气就永久关死，进气又被质量守恒一起锁住，面板表现为
            // 「总流速 0、变温效率爆表（除零）、空气流动效率 0%」且不回弹。
            float drive = Mathf.Clamp(1f + (PressurePa - roomPressurePa) / Mathf.Max(1f, EffectivePressurePa), 0.5f, 1.5f);

            float massIn = Mathf.Min(intakeRate * dt, VolumeM3 * AirDensityKgPerM3 * 0.25f);

            // 质量守恒：回路存不住气，进气不可能超过出气能力。
            //
            // CCC 的入风口和出风口规格可以差很多（常见 9 台大入风口配 1 个出风口），
            // 若进气按自己的额定满速灌，多余的质量会把回路压力顶到「进气门和出气门
            // 同时关闭」的区间，然后彻底锁死 —— 就是流速逐渐归零、效率归零且不回弹。
            //
            // 注意这里不能乘 drive：drive 会因为压力偏离变小，乘进去就变成
            // 「压力低 → 不许进气 → 压力更低」的正反馈死锁。
            float exhaustCapKg = RatedExhaustKgPerSec > 1e-9f
                ? RatedExhaustKgPerSec * dt
                : float.MaxValue;
            massIn = Mathf.Min(massIn, exhaustCapKg);

            if (massIn < 0f)
            {
                massIn = 0f;
            }

            // 2. 进气与回路存量混合：温度、氧、水汽都按质量加权。
            float massAfterIntake = massBefore + massIn;
            if (massAfterIntake > 0.001f)
            {
                float weight = massIn / massAfterIntake;
                TemperatureK += (intakeTemperatureK - TemperatureK) * weight;
                OxygenPercent += (intakeOxygenPercent - OxygenPercent) * weight;
                if (VaporValid)
                {
                    VaporContent += (intakeVapor - VaporContent) * weight;
                }
                MassKg = massAfterIntake;
            }

            // 3. 温控：一阶惯性。增益由 CompTempControl.energyPerSecond 换算而来，
            //    时间常数与 CCC 原版 DeltaSmooth=96 的响应对齐，但按 AO 步长连续积分。
            if (tempControlGainPerSecond > 0f)
            {
                float pressureBefore = MassKg * GasConstantDryAir * TemperatureK / Mathf.Max(0.1f, VolumeM3);
                float alpha = 1f - Mathf.Exp(-tempControlGainPerSecond * dt);
                TemperatureK += (targetTemperatureK - TemperatureK) * alpha;

                // 等压修正：管道是定压流动系统，不是密封罐。
                //
                // 不修正的话，目标温度远低于室温时压力会按 PV = mRT 一路塌到房间压力以下：
                // 出气压差变成 0（气出不去），进气却仍按背压曲线满速灌入，质量堆积又把
                // 进气门压死 —— 表现就是「流速逐渐归零、效率归零」而且再也回不来。
                // 反过来加热时压力虚高，同样会让进气长期关闭。
                //
                // 定压意味着降温时密度上升、系统自动补气，这正是风管的真实行为。
                if (TemperatureK > 1f && pressureBefore > 0f)
                {
                    MassKg = pressureBefore * VolumeM3 / (GasConstantDryAir * TemperatureK);
                }
            }

            // 4. 出气。drive 已在第 1 步算出，这里直接复用，保证「本轮进的气」与
            //    「本轮出的气」是同一个驱动力。
            float massOut = Mathf.Min(RatedExhaustKgPerSec * drive * dt, MassKg * 0.5f);
            if (massOut < 0f)
            {
                massOut = 0f;
            }
            MassKg -= massOut;

            // 5. 定压兜底。管道是定压流动系统：风机在持续补气，回路不该像真空罐那样
            //    随着温度或体积变化把压力甩到房间压力之外。
            //
            // 这一条专门兜住三类会让压力「一次性跑飞」的意外：
            //   a) 加载存档/切图后第一次推进，dt 是几千秒（实测 6754 s），
            //      一步就能把质量抽空；
            //   b) RefreshLoopShape 重算 VolumeM3（管道增减），质量没跟着调；
            //   c) 极端目标温度配合大 dt，PV = mRT 直接把压力压到房间压力的几分之一。
            //
            // 压力一旦低于房间压力一个 EffectivePressurePa，出气驱动就贴到下限，
            // 而质量守恒又让进气跟着走低，两边一起锁死 —— 就是面板上的
            // 「总流速 0 / 变温效率 340000000% / 流动效率 0%」。
            if (roomPressurePa > 1000f && TemperatureK > 1f)
            {
                float targetMassKg = roomPressurePa * VolumeM3 / (GasConstantDryAir * TemperatureK);
                if (targetMassKg > 0f)
                {
                    MassKg = Mathf.Clamp(MassKg, targetMassKg * 0.9f, targetMassKg * 1.1f);
                }
            }

            // 6. 数值夹紧。质量下限保证压力不会塌到 0 导致下一步除零。
            MassKg = Mathf.Max(MassKg, VolumeM3 * 0.02f);
            TemperatureK = Mathf.Clamp(TemperatureK, 1f, 2000f);
            OxygenPercent = Mathf.Clamp(OxygenPercent, 0f, 100f);
            if (VaporContent < 0f || float.IsNaN(VaporContent) || float.IsInfinity(VaporContent))
            {
                VaporContent = 0f;
            }
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
            result.ExhaustOxygenPercent = OxygenPercent;
            result.ExhaustVapor = VaporContent;
            return result;
        }
    }

    /// <summary>一步推进的结果，供调用方换算成 AO 的体积源。</summary>
    public struct LoopStep
    {
        public float IntakeMassKg;
        public float ExhaustMassKg;
        public float ExhaustTemperatureK;
        public float ExhaustOxygenPercent;
        public float ExhaustVapor;
    }
}
