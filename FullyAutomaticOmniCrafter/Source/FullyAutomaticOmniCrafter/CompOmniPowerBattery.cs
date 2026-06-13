using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using HarmonyLib;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 无限容量电池组件。
    /// 开启后锁定电量为设定值，且屏蔽所有负面特性。
    /// 
    /// 使用方法：
    /// 在 ThingDef 的 comps 列表中添加此组件。
    /// 注意：由于它继承自 CompPowerBattery，需要使用对应的 Properties 类型（通常是 CompProperties_Battery）。
    /// 示例 XML:
    /// ```
    /// <comps>
    ///   <li Class="FullyAutomaticOmniCrafter.CompProperties_Battery">
    ///     <compClass>FullyAutomaticOmniCrafter.CompOmniPowerBattery</compClass>
    ///     <storedEnergyMax>1000</storedEnergyMax> <!-- 初始显示容量，开启后会被 targetCapacity 覆盖 -->
    ///     <efficiency>1.0</efficiency>
    ///     <shortCircuitInRain>false</shortCircuitInRain>
    ///     <transmitsPower>true</transmitsPower>
    ///   </li>
    /// </comps>
    /// ```
    /// </summary>
    public class CompOmniPowerBattery : CompPowerBattery
    {
        public bool isEnabled = false;
        public float targetCapacity = 1000000f; // 默认 100万 Wd

        private CompProperties_Battery cachedProps;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            InitializeProps();
        }

        private void InitializeProps()
        {
            if (cachedProps == null)
            {
                var originalProps = this.Props;
                cachedProps = new CompProperties_Battery
                {
                    compClass = originalProps.compClass,
                    storedEnergyMax = isEnabled ? targetCapacity : 0f,
                    efficiency = 1.0f,
                    shortCircuitInRain = false,
                    transmitsPower = originalProps.transmitsPower
                };
                this.props = cachedProps;
            }
        }

        public override void PostExposeData()
        {
            // 在加载前初始化 props 结构，防止基类加载时报错
            InitializeProps();
            
            base.PostExposeData();
            Scribe_Values.Look(ref isEnabled, "isEnabled", false);
            Scribe_Values.Look(ref targetCapacity, "targetCapacity", 1000000f);

            ApplyStatus();
        }

        public override void CompTick()
        {
            // 不调用 base.CompTick() 以避开原版 5W 的自放电逻辑
            ApplyStatus();
        }

        public void ApplyStatus()
        {
            var p = (CompProperties_Battery)this.props;
            if (isEnabled)
            {
                p.storedEnergyMax = targetCapacity;
                Traverse.Create(this).Field("storedEnergy").SetValue(targetCapacity);
            }
            else
            {
                p.storedEnergyMax = 0f;
                Traverse.Create(this).Field("storedEnergy").SetValue(0f);
            }
        }

        public override void ReceiveCompSignal(string signal)
        {
            // 屏蔽 Breakdown 信号导致的电量清空
            if (signal == "Breakdown") return;
            base.ReceiveCompSignal(signal);
        }

        public override string CompInspectStringExtra()
        {
            string statusStr = isEnabled ? "OmniPower_InfiniteBattery_On".Translate() : "OmniPower_InfiniteBattery_Off".Translate();
            string str = "OmniPower_InfiniteBattery_Status".Translate(statusStr);
            if (isEnabled)
            {
                str += "\n" + base.CompInspectStringExtra();
            }
            return str;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 切换开关按钮
            yield return new Command_Toggle
            {
                defaultLabel = "OmniPower_InfiniteBattery_Label".Translate(),
                defaultDesc = "OmniPower_InfiniteBattery_Desc".Translate(),
                icon = CompOmniPowerBatteryTex.IconBattery,
                isActive = () => isEnabled,
                toggleAction = () =>
                {
                    isEnabled = !isEnabled;
                    ApplyStatus();
                }
            };

            // 调整容量按钮
            if (isEnabled)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OmniPower_InfiniteBattery_SetCapacity".Translate(),
                    icon = CompOmniPowerGeneratorTex.IconAdjust,
                    action = () =>
                    {
                        Find.WindowStack.Add(new Dialog_OmniPowerAdjust(
                            "OmniPower_InfiniteBattery_SetCapacity".Translate(),
                            targetCapacity,
                            0f,
                            1000000000000f,
                            (val) =>
                            {
                                targetCapacity = val;
                                ApplyStatus();
                            }
                        ));
                    }
                };
            }

            // 开发工具辅助按钮（仅调试模式可见）
            if (DebugSettings.ShowDevGizmos)
            {
                foreach (var g in base.CompGetGizmosExtra())
                    yield return g;
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class CompOmniPowerBatteryTex
    {
        public static readonly Texture2D IconBattery = ContentFinder<Texture2D>.Get("UI/Commands/OmniPower_Battery", true) ?? BaseContent.WhiteTex;
    }
}
