using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompOmniCrafterSmartInfiniteBattery : CompPowerBattery
    {
        // 维持UI美观的基础容量下限
        private const float BaseCapacity = 1000f;

        // 是否正在吸收溢出电量
        private bool isAbsorbing = false;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

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
            UpdateCapacity();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            UpdateCapacity();
        }

        public void UpdateCapacity()
        {
            if (this.PowerNet == null) return;

            // CurrentEnergyGainRate返回当tick产生或消耗的净能量 (Wd per tick)
            float surplusWdPerTick = this.PowerNet.CurrentEnergyGainRate();

            if (surplusWdPerTick > 1e-6f)
            {
                this.isAbsorbing = true;

                // 如果遇到极其变态的发电机，赋予极大的充电量，不再限制 100Wd
                if (float.IsInfinity(surplusWdPerTick) || float.IsNaN(surplusWdPerTick))
                {
                    surplusWdPerTick = 1000000000f; // 一瞬间充满极大的数值 (10亿 Wd)
                }

                // 【完美100%机制】：精准计算出下个 PowerNetTick 将要塞进来的盈余电量。
                // 提前把我们的容量上限撑开正好这么大的缝隙，原版电网就会原封不动全塞进来，达成100%满载状态！
                // 修正：确保不会因为浮点精度问题导致没能撑开足够的容量，额外加一点点缓冲容差
                float potentialNewEnergy = this.StoredEnergy + surplusWdPerTick + 1f;
                float maxAllowedCapacity = int.MaxValue / 2f;
                potentialNewEnergy = Mathf.Min(potentialNewEnergy, maxAllowedCapacity);
                ((CompProperties_Battery)this.props).storedEnergyMax = Mathf.Max(BaseCapacity, potentialNewEnergy);
            }
            else
            {
                // 电网平衡或缺电时，停止主动吸收，并在放电时触发【棘轮机制】（容量不缩小，以发出电量预警）
                this.isAbsorbing = false;

                // 我们仍然要确保最大容量不会低于已存电量，以及维持基本值
                float currentMax = Mathf.Max(BaseCapacity, this.StoredEnergy);
                ((CompProperties_Battery)this.props).storedEnergyMax = currentMax;
            }
        }

        // UI 文本优化：超过百万电量时显示无穷大，防止文字重叠
        public override string CompInspectStringExtra()
        {
            string status = this.isAbsorbing ? "[自适应膨胀核心: 运行中]" : "[自适应膨胀核心: 待机]";

            if (this.StoredEnergy >= 1000000000f)
            {
                return "电网储能: 已饱和 (∞) \n" + status;
            }

            return base.CompInspectStringExtra() + "\n" + status;
        }
    }

    [HarmonyPatch(typeof(CompPowerBattery), "AmountCanAccept", MethodType.Getter)]
    public static class Patch_SmartBattery_AmountCanAccept
    {
        [HarmonyPrefix]
        public static void Prefix(CompPowerBattery __instance)
        {
            // 在原版查询容量之前，强制计算并撑开我们的自适应容量
            // 解决 XML tickerType 未设置导致的 CompTick 不触发问题，以及浮点容差问题
            if (__instance is CompOmniCrafterSmartInfiniteBattery smartBattery)
            {
                smartBattery.UpdateCapacity();
            }
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
}