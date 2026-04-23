using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompOmniCrafterSmartInfiniteBattery : CompPowerBattery
    {
        // 挂载在我们建筑上的“主动吸收器”
        private CompPowerTrader trader;
        
        // 维持UI美观的基础容量下限
        private const float BaseCapacity = 1000f;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // 获取同伴组件
            this.trader = this.parent.GetComp<CompPowerTrader>();

            // 【克隆独立属性】剥离全局XML配置，防止膨胀污染其他同类电池
            var originalProps = this.Props;
            this.props = new CompProperties_Battery
            {
                compClass = originalProps.compClass,
                storedEnergyMax = originalProps.storedEnergyMax,
                efficiency = 1.0f, // 100% 转化率，无废热损耗
                shortCircuitInRain = false, // 关闭雨天短路
                transmitsPower = originalProps.transmitsPower
            };
        }

        public override void CompTick()
        {
            base.CompTick();
            if (this.PowerNet == null || this.trader == null) return;

            // 1. 计算“真实”的电网盈余
            // (必须减去我们 Trader 自己当前的输出，否则会算错)
            float trueSurplus = this.PowerNet.CurrentEnergyGainRate() - this.trader.PowerOutput;

            if (trueSurplus > 0)
            {
                // 【适配无穷大发电机】
                if (float.IsInfinity(trueSurplus) || float.IsNaN(trueSurplus) || trueSurplus > 10000000f)
                {
                    // 帮电网中和无限功率，防止游戏崩溃
                    this.trader.PowerOutput = float.NegativeInfinity;
                    // 但电池内部只安全吸收一个巨大但合理的数值
                    this.AddEnergy(10000f / 60000f);
                }
                else
                {
                    // 2. 让 Trader 变为耗电设备，吃干抹净所有的盈余
                    this.trader.PowerOutput = -trueSurplus;
                    // 3. 将吃掉的电量手动转化为 Wd 存入电池
                    this.AddEnergy(trueSurplus / 60000f);
                }

                // 4. 【核心：完美 100%】
                // 将最大容量严格锁定为当前电量，并且保证最小容量为基础值
                float newMax = Mathf.Max(BaseCapacity, this.StoredEnergy);
                ((CompProperties_Battery)this.props).storedEnergyMax = newMax;
            }
            else
            {
                // 电网平衡或缺电时，停止主动吸收
                // 【放电悖论处理 / 棘轮机制】
                // 当电池正在放电，电量下降时，不再缩小 storedEnergyMax。
                this.trader.PowerOutput = 0f;
            }
        }

        // UI 文本优化：超过百万电量时显示无穷大，防止文字重叠
        public override string CompInspectStringExtra()
        {
            if (this.StoredEnergy > 1000000f)
            {
                return "电网储能: 已饱和 (∞) \n[自适应膨胀核心: 运行中]";
            }

            return base.CompInspectStringExtra() + "\n[自适应膨胀核心: 运行中]";
        }
    }

    // 拦截《短路事件》 (Zzztt...) 以防止毁灭性的核弹级爆炸
    [HarmonyPatch(typeof(ShortCircuitUtility), "DoShortCircuit")]
    public static class Patch_DoShortCircuit_Smart
    {
        [HarmonyPrefix]
        public static void Prefix(Building culprit, out Dictionary<CompPowerBattery, float> __state)
        {
            __state = new Dictionary<CompPowerBattery, float>();
            PowerNet net = culprit.PowerComp?.PowerNet;
            if (net == null) return;

            foreach (CompPowerBattery battery in net.batteryComps)
            {
                // 认准我们的智能电池类
                if (battery is CompOmniCrafterSmartInfiniteBattery smartBattery)
                {
                    // 1. 记录当前巨额电量
                    float currentEnergy = smartBattery.StoredEnergy;
                    __state.Add(smartBattery, currentEnergy);
                    
                    // 2. 强行抽干电量，骗过原版的爆炸威力计算逻辑
                    smartBattery.DrawPower(currentEnergy);
                }
            }
        }

        [HarmonyPostfix]
        public static void Postfix(Dictionary<CompPowerBattery, float> __state)
        {
            if (__state == null || __state.Count == 0) return;

            // 3. 爆炸计算结束后，将没收的电量全额归还
            foreach (var kvp in __state)
            {
                kvp.Key.AddEnergy(kvp.Value);
            }
        }
    }

    // public class OmniCrafterSmartInfiniteBattery : Building
    // {
    //     // 作为备用的自定义 Building 外壳，实际核心逻辑都在同前缀的 Comp 中实现
    // }
}