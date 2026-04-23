# RimWorld 终极自适应无限电池 (Smart Infinite Battery) 设计方案

## 一、 核心设计目标
1. **无限吞吐**：自适应吸收电网所有盈余，并按需全额释放。吸收时让电网保留有少量剩余功率。
2. **防核爆机制**：完美免疫原版“短路 (Zzztt...)”事件造成的毁灭性爆炸。
3. **自动化模组兼容**：在自动化 Mod 面前始终伪装成“100% 已充满”状态，确保相关逻辑正常触发。
4. **统计模组兼容**：在 UI 和数据面板（如 Power Tab 2）中真实显示已存储的巨量数值。
5. **极端异常处理**：完美适配第三方 Mod 中的“无穷大 (Infinity)”发电机，防止游戏逻辑和 UI 崩溃。

---
---

## 三、 C# 核心代码实现

### 1. 电池主逻辑 (CompSmartInfiniteBattery.cs)

继承原版蓄电池，克隆独立属性，并在 `CompTick` 中处理动态膨胀与无穷大钳位（Clamping）。

```csharp
using RimWorld;
using UnityEngine;
using Verse;

namespace MySmartBatteryMod
{
    public class CompSmartInfiniteBattery : CompPowerBattery
    {
        // 维持UI美观的基础容量下限
        private const float BaseCapacity = 1000f; 
        
        // 永远保持的“空余容量”缓冲值 (100 Wd 足以应对极高的常规瞬间盈余)
        private const float BufferSpace = 100f; 

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
                shortCircuitInRain = originalProps.shortCircuitInRain,
                transmitsPower = originalProps.transmitsPower
            };
        }

        public override void CompTick()
        {
            base.CompTick();
            if (this.PowerNet == null) return;

            // 【异常发电机适配】获取盈余并进行数值钳位
            float surplus = this.PowerNet.CurrentEnergyGainRate;
            if (float.IsInfinity(surplus) || float.IsNaN(surplus) || surplus > 10000000f)
            {
                // 遇到无限发电机时，转化为每Tick安全吸收的最大值
                surplus = 10000f; 
            }

            // 【防溢出机制】设定名义上的物理极限 (例如 1亿 Wd)
            if (this.StoredEnergy < 99999999f && surplus > 0)
            {
                float energyToStore = (surplus / 60000f);
                this.AddEnergy(energyToStore);
            }

            // 【动态膨胀逻辑】更新专属的容量上限：当前真实电量 + 缓冲值
            float targetMax = Mathf.Max(BaseCapacity, this.StoredEnergy + BufferSpace);
            ((CompProperties_Battery)this.props).storedEnergyMax = targetMax;
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
}
```

### 2. 短路事件拦截补丁 (Patch_ShortCircuitUtility.cs)

使用 Harmony 拦截 `ShortCircuitUtility.DoShortCircuit`。在结算爆炸前将电量“藏”起来，结算后再还给电池。

```csharp
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MySmartBatteryMod
{
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
                if (battery is CompSmartInfiniteBattery smartBattery)
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
```


### 3、 双组件主动吸收架构 (Active Sink)

这个建筑挂载两个组件：一个发电机/耗电器（CompPowerTrader）和一个电池（CompPowerBattery）。

* 伪装成耗电器： 用 Trader 实时扫描电网，电网多出多少电，Trader 就瞬间变成一个耗电量同等巨大的“吃电怪兽”，把电网盈余直接抹平。
* 手动搬运： 把 Trader 吃掉的电量，在代码里手动转换并塞进 Battery 里。
* 锁死 100%： 因为我们不再依赖原版电网来充电，所以我们可以肆无忌惮地把电池的 StoredEnergyMax 严格等同于 StoredEnergy，实现完美的 100%！

C# 核心代码实现

```csharp
using RimWorld;
using UnityEngine;
using Verse;

namespace MySmartBatteryMod
{
    public class CompSmartInfiniteBattery : CompPowerBattery
    {
        // 挂载在我们建筑上的“主动吸收器”
        private CompPowerTrader trader; 
        
        private const float BaseCapacity = 1000f; 

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            
            // 获取同伴组件
            this.trader = this.parent.GetComp<CompPowerTrader>();

            // 克隆电池属性（同之前的逻辑）
            var originalProps = this.Props;
            this.props = new CompProperties_Battery
            {
                compClass = originalProps.compClass,
                storedEnergyMax = originalProps.storedEnergyMax,
                efficiency = 1.0f,
                shortCircuitInRain = originalProps.shortCircuitInRain,
                transmitsPower = originalProps.transmitsPower
            };
        }

        public override void CompTick()
        {
            base.CompTick();
            if (this.PowerNet == null || this.trader == null) return;

            // 1. 计算“真实”的电网盈余 
            // (必须减去我们 Trader 自己当前的输出，否则会算错)
            float trueSurplus = this.PowerNet.CurrentEnergyGainRate - this.trader.PowerOutput;

            if (trueSurplus > 0)
            {
                // 【适配无穷大发电机】
                if (float.IsInfinity(trueSurplus))
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
                // 将最大容量严格锁定为当前电量，没有任何缓冲空间
                float newMax = Mathf.Max(BaseCapacity, this.StoredEnergy);
                ((CompProperties_Battery)this.props).storedEnergyMax = newMax;
            }
            else
            {
                // 电网平衡或缺电时，停止主动吸收
                this.trader.PowerOutput = 0f;

                // 【放电悖论处理】
                // 当电池正在放电，电量下降时，我们不再缩小 storedEnergyMax。
                // 这被称为“棘轮机制(Ratchet)”。
            }
        }
    }
}

```

为什么要在放电时采用“棘轮机制 (Ratchet)”？
请注意代码的 else 分支。当电网缺电、你的电池开始对外输出时，我没有继续让 Max = Current。

* 如果你强制维持 100%： 假设你的电池存了 10万 瓦，放电放到只剩 5 瓦了，它的容量上限也会变成 5 瓦，系统显示的依然是 5 / 5 = 100%。这会导致自动化 Mod 永远不知道你快没电了，备用柴油发电机永远不会自动启动，基地会突然在半夜断电。
* 棘轮机制（我写的逻辑）： 电池的容量上限“只涨不跌”。在吸收电量时，它是完美的 100%。当它开始放电时（比如从 10万 掉到 5万），上限保留在 10万，UI 会真实显示 50%。直到重新来电，它冲破 10万 大关时，它又会继续维持在完美的 100%。

这种架构不仅在数学上实现了你的 100% 强迫症需求，还在游戏性上完美契合了自动化 Mod 和基地生存的预警逻辑。


---

## 四、 XML 定义建议 (ThingDef)

在 XML 配置文件中，像配置普通电池一样配置它即可。关键点如下：

```xml
<comps>
  <li Class="CompProperties_Battery">
    <efficiency>1.0</efficiency> 
    <shortCircuitInRain>false</shortCircuitInRain> 
    <transmitsPower>true</transmitsPower>
  </li>
    <li Class="CompProperties_Power">
        <compClass>CompPowerTrader</compClass>
        <basePowerConsumption>0</basePowerConsumption>
        <transmitsPower>true</transmitsPower>
    </li>
</comps>
```



