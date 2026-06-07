using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Omni发电机的模式枚举。
    /// </summary>
    public enum OmniPowerMode
    {
        /// <summary>
        /// 手动模式：允许玩家自由调整功率输出。
        /// </summary>
        Manual = 1,
        
        /// <summary>
        /// 自动平衡模式：自动调整功率输出，使电网净消耗为零（发电量刚好等于消耗量）。
        /// </summary>
        AutoBalance = 2,
        
        /// <summary>
        /// 自动平衡+模式：在自动平衡的基础上，额外提供一定功率（用于给电池充电）。
        /// </summary>
        AutoBalancePlus = 3,
        
        /// <summary>
        /// 无限模式：每帧自动填满电网中所有普通电池。
        /// </summary>
        Infinite = 4
    }

    /// <summary>
    /// 无限电能发电机。
    /// 有如下几种模式
    /// 1 完全自由调节功率输出。
    /// 2 自动调节功率输出，以绝对满足电网需求（使得电网耗电量等于电网发电量）。
    /// 3 在平衡电网功率的基础上设置一个超出的值，以实现给其他电池充电。
    /// 4 提供无限大的电量（Infinite），使得电网中所有电池都瞬间充满电。（注意，需要避免OmniCrafterSmartInfiniteBattery超载）
    /// </summary>
    public class CompOmniPowerGenerator : CompPowerPlant
    {
        /// <summary>
        /// 当前发电机的工作模式。
        /// </summary>
        public OmniPowerMode mode = OmniPowerMode.Manual;

        /// <summary>
        /// 手动模式下的设定功率（W）。
        /// </summary>
        public float manualPower = 1000f;

        /// <summary>
        /// 自动平衡+模式下的额外功率（W）。
        /// </summary>
        public float extraPower = 1000f;

        /// <summary>
        /// 发电机期望的输出功率。
        /// 根据当前模式计算：
        /// - 手动：返回固定值。
        /// - 平衡：计算电网其他部分的净消耗。
        /// - 加上额外功率的平衡：平衡值 + 增量。
        /// - 无限：返回平衡值（充能由FillBatteries处理）。
        /// </summary>
        protected override float DesiredPowerOutput
        {
            get
            {
                // 如果电网不存在或开关已关闭，不输出能量
                if (PowerNet == null || !FlickUtility.WantsToBeOn(parent)) return 0f;

                switch (mode)
                {
                    case OmniPowerMode.Manual:
                        return manualPower;
                    case OmniPowerMode.AutoBalance:
                        return GetBalancePower();
                    case OmniPowerMode.AutoBalancePlus:
                        return GetBalancePower() + extraPower;
                    case OmniPowerMode.Infinite:
                        // 在Infinite模式下，通过Tick直接给电池充能，
                        // 基础输出功率保持平衡即可，避免电网显示异常。
                        return GetBalancePower();
                    default:
                        return 0f;
                }
            }
        }

        /// <summary>
        /// 计算实现电网平衡所需的功率。
        /// 逻辑：遍历电网所有组件，计算（总消耗 - 其他组件总发电量）。
        /// </summary>
        /// <returns>所需的平衡功率（W），最小为0。</returns>
        private float GetBalancePower()
        {
            if (PowerNet == null) return 0f;
            
            // RimWorld 电力计算说明：
            // PowerOutput 为正：正在发电
            // PowerOutput 为负：正在耗电
            
            float otherProduction = 0f;
            float consumption = 0f;
            
            foreach (var cp in PowerNet.powerComps)
            {
                // 跳过发电机自身
                if (cp == this) continue;
                // 仅统计已通电的组件
                if (!cp.PowerOn) continue;
                
                float outPut = cp.PowerOutput;
                if (outPut > 0) otherProduction += outPut;
                else consumption -= outPut; // 取绝对值累加消耗量
            }
            
            // 最终需要输出 = 需求量 - 其他供应量
            return Mathf.Max(0f, consumption - otherProduction);
        }

        /// <summary>
        /// 每帧执行的逻辑。
        /// 在无限模式下，如果开关开启，则尝试填充电池。
        /// </summary>
        public override void CompTick()
        {
            base.CompTick();
            if (mode == OmniPowerMode.Infinite && PowerNet != null && FlickUtility.WantsToBeOn(parent))
            {
                FillBatteries();
            }
        }

        /// <summary>
        /// 更新实际的功率输出。
        /// 检查各种导致停机的状态（故障、燃料、开关等）。
        /// </summary>
        public override void UpdateDesiredPowerOutput()
        {
            if (!FlickUtility.WantsToBeOn(parent)// || 
                // autoPoweredComp != null && !autoPoweredComp.WantsToBeOn || 
                // breakdownableComp != null && breakdownableComp.BrokenDown || 
                // refuelableComp != null && !refuelableComp.HasFuel || 
                // toxifier != null && !toxifier.CanPolluteNow || 
                // !PowerOn
                )
            {
                PowerOutput = 0f;
            }
            else
            {
                PowerOutput = DesiredPowerOutput;
            }
        }

        /// <summary>
        /// 无限模式下的电池填充逻辑。
        /// 仅填充普通电池，跳过 SmartInfiniteBattery 以免触发其容量膨胀逻辑。
        /// </summary>
        private void FillBatteries()
        {
            if (PowerNet == null) return;
            foreach (var battery in PowerNet.batteryComps)
            {
                // 不给 SmartInfiniteBattery 充电，因为它有自适应容量逻辑，
                // 强行填充会导致其认为电网有无限盈余，从而导致其存储上限异常膨胀。
                if (battery is CompOmniCrafterSmartInfiniteBattery) continue;

                // 直接将普通电池充满
                battery.AddEnergy(battery.AmountCanAccept);
            }
        }

        /// <summary>
        /// 保存/读取存档数据。
        /// </summary>
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref mode, "mode", OmniPowerMode.Manual);
            Scribe_Values.Look(ref manualPower, "manualPower", 1000f);
            Scribe_Values.Look(ref extraPower, "extraPower", 1000f);
        }

        /// <summary>
        /// 状态栏额外信息显示。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            string str = base.CompInspectStringExtra();
            str += "\n" + "OmniPower_Mode".Translate() + ": " + $"OmniPower_Mode_{mode}".Translate();
            if (mode == OmniPowerMode.Manual)
            {
                str += "\n" + "OmniPower_ManualPower".Translate() + ": " + manualPower.ToString("F0") + " W";
            }
            else if (mode == OmniPowerMode.AutoBalancePlus)
            {
                str += "\n" + "OmniPower_ExtraPower".Translate() + ": " + extraPower.ToString("F0") + " W";
            }
            return str;
        }

        /// <summary>
        /// 添加交互按钮（Gizmos）。
        /// 包括：切换模式按钮、手动模式调整按钮、自动平衡+模式调整按钮。
        /// </summary>
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 切换模式按钮
            yield return new Command_Action
            {
                defaultLabel = "OmniPower_SwitchMode".Translate(),
                defaultDesc = "OmniPower_SwitchModeDesc".Translate(),
                icon = CompOmniPowerGeneratorTex.IconMode,
                action = delegate
                {
                    List<FloatMenuOption> list = new List<FloatMenuOption>();
                    foreach (OmniPowerMode m in Enum.GetValues(typeof(OmniPowerMode)))
                    {
                        list.Add(new FloatMenuOption($"OmniPower_Mode_{m}".Translate(), delegate
                        {
                            mode = m;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(list));
                }
            };

            // 调整手动功率按钮
            if (mode == OmniPowerMode.Manual)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OmniPower_AdjustManual".Translate(),
                    icon = CompOmniPowerGeneratorTex.IconAdjust,
                    action = delegate
                    {
                        Find.WindowStack.Add(new Dialog_Slider(val => "OmniPower_ManualPower".Translate() + ": " + val + " W", 0, 100000, delegate(int val)
                        {
                            manualPower = val;
                        }, (int)manualPower));
                    }
                };
            }

            // 调整额外功率按钮
            if (mode == OmniPowerMode.AutoBalancePlus)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OmniPower_AdjustExtra".Translate(),
                    icon = CompOmniPowerGeneratorTex.IconAdjust,
                    action = delegate
                    {
                        Find.WindowStack.Add(new Dialog_Slider(val => "OmniPower_ExtraPower".Translate() + ": " + val + " W", 0, 100000, delegate(int val)
                        {
                            extraPower = val;
                        }, (int)extraPower));
                    }
                };
            }
        }
    }

    /// <summary>
    /// 静态纹理资源持有类。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CompOmniPowerGeneratorTex
    {
        public static readonly Texture2D IconMode = ContentFinder<Texture2D>.Get("UI/Gizmos/OmniPower_Mode", true) ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconAdjust = ContentFinder<Texture2D>.Get("UI/Gizmos/OmniPower_Adjust", true) ?? BaseContent.WhiteTex;
    }
}
