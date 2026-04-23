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

        // 是否正在吸收溢出电量
        private bool isAbsorbing = false;

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
            if (this.trader.transNet != this.PowerNet)
            {
                this.trader.transNet = this.PowerNet;
            }
            
            // CurrentEnergyGainRate 返回的是每日瓦特(Wd/tick)，必须乘以60000转换回瓦特(Watts)
            float netGainWatts = this.PowerNet.CurrentEnergyGainRate() * 60000f;
            
            // 修复反馈循环Bug：如果Trader组件为关闭状态，电网并不会包括它的负输出。所以只在它开启时减去它的输出。
            float currentTraderOutput = this.trader.PowerOn ? this.trader.PowerOutput : 0f;
            float trueSurplus = netGainWatts - currentTraderOutput;

            if (trueSurplus > 1f)
            {
                this.isAbsorbing = true;
                
                // 确保我们的Trader开启，以有效吸收电力
                if (!this.trader.PowerOn)
                {
                    // 避免被损坏或手动关闭时疯狂产生红黄字报错
                    if (this.parent.IsBrokenDown() || !FlickUtility.WantsToBeOn(this.parent))
                    {
                        this.trader.PowerOutput = 0f;
                        this.isAbsorbing = false;
                        return;
                    }
                    this.trader.PowerOn = true;
                }

                // 【适配无穷大发电机】
                if (float.IsInfinity(trueSurplus) || float.IsNaN(trueSurplus) || trueSurplus > 10000000f)
                {
                    this.trader.PowerOutput = float.NegativeInfinity;
                    
                    float safeEnergyAdd = 10000f / 60000f;
                    float potentialNewEnergy = this.StoredEnergy + safeEnergyAdd;
                    ((CompProperties_Battery)this.props).storedEnergyMax = Mathf.Max(BaseCapacity, potentialNewEnergy);
                    
                    this.AddEnergy(safeEnergyAdd);
                }
                else
                {
                    // 2. 让 Trader 变为耗电设备，吃干抹净所有的盈余
                    this.trader.PowerOutput = -trueSurplus;

                    // 【核心修改】必须在添加电量之前扩大容量上限，否则 AddEnergy 会因为容量已满而拒收
                    float potentialNewEnergy = this.StoredEnergy + (trueSurplus / 60000f);
                    float newMax = Mathf.Max(BaseCapacity, potentialNewEnergy);
                    ((CompProperties_Battery)this.props).storedEnergyMax = newMax;

                    // 3. 将吃掉的电量手动转化为 Wd 存入电池
                    this.AddEnergy(trueSurplus / 60000f);
                }

                // 4. 【核心：完美 100%】
                // 将最大容量严格锁定为当前电量，并且保证最小容量为基础值
                float finalMax = Mathf.Max(BaseCapacity, this.StoredEnergy);
                ((CompProperties_Battery)this.props).storedEnergyMax = finalMax;
            }
            else
            {
                // 电网平衡或缺电时，停止主动吸收
                // 【放电悖论处理 / 棘轮机制】
                // 当电池正在放电，电量下降时，不再缩小 storedEnergyMax。
                this.isAbsorbing = false;
                this.trader.PowerOutput = 0f;
            }
        }

        // UI 文本优化：超过百万电量时显示无穷大，防止文字重叠
        public override string CompInspectStringExtra()
        {
            string status = this.isAbsorbing ? "[自适应膨胀核心: 运行中]" : "[自适应膨胀核心: 待机]";
            
            if (this.StoredEnergy > 1000000f)
            {
                return "电网储能: 已饱和 (∞) \n" + status;
            }

            return base.CompInspectStringExtra() + "\n" + status;
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

    // 静默处理 RimWorld 因为单一建筑挂载多个 PowerComp 而产生的红字报错
    [HarmonyPatch(typeof(PowerNet), "RegisterAllComponentsOf")]
    public static class Patch_RegisterAllComponentsOf_Quiet
    {
        [HarmonyPrefix]
        public static bool Prefix(PowerNet __instance, ThingWithComps parentThing)
        {
            // 只要包含了特殊的智能电池组件，就说明是我们双挂载的建筑
            if (parentThing.GetComp<CompOmniCrafterSmartInfiniteBattery>() != null)
            {
                CompPowerTrader comp1 = parentThing.GetComp<CompPowerTrader>();
                if (comp1 != null && !__instance.powerComps.Contains(comp1))
                {
                    __instance.powerComps.Add(comp1);
                }
                
                CompPowerBattery comp2 = parentThing.GetComp<CompPowerBattery>();
                if (comp2 != null && !__instance.batteryComps.Contains(comp2))
                {
                    __instance.batteryComps.Add(comp2);
                }
                return false; // Skip original error-throwing logic
            }
            return true;
        }
    }

    // 为了防止UI面板因为双组件而显示两条电源信息（以及未接入电网的误报），我们创建一个隐藏的Trader供代码吸收电力
    public class CompOmniCrafterPowerTrader_Quiet : CompPowerTrader
    {
        public override string CompInspectStringExtra()
        {
            // 不在面板上显示任何原版电源信息，全部由我们的 Battery 组件去显示
            return null;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 在产生后，若我们没有被原版的电网创建器赋予 transNet，我们要主动获取，以免内部错误
            var battery = this.parent.GetComp<CompOmniCrafterSmartInfiniteBattery>();
            if (battery != null && battery.transNet != null)
            {
                this.transNet = battery.transNet;
            }
        }
    }
}