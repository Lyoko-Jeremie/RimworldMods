# AO 建筑体系与四个 Mod 的融入定位

> 对象：
> - `294100/3830470974` — Atmosphere Overhaul（下称 AO）
> - `294100/3836392033` — Explosive Diarrhea from Food Poisoning Propells Long Jump（下称 ED，作者同为 Acutus）
> - `294100/3669891833` — Good Climate（下称 GC）
> - `F:\SteamLibrary\steamapps\common\RimWorld\Mods\FullyAutomaticOmniCrafter` — 超维科技（下称 Omni）
>
> 源码依据：`F:\RimworldCodeIL\AtmosphereOverhaul\Buildings\`（dotPeek，89 个 .cs）、GC `GoodClimate.dll`（MVID `637609F0-5FDF-4E86-A007-71B8D0441AD5`，14 类型 / 48 方法）、Omni `Source\FullyAutomaticOmniCrafter\`。
> 本文件只做分析，不含任何代码修改。

---

## 0. 结论摘要

1. **AO 的建筑不是"房间空调"，是"格点源"。** 每个建筑只做一件事：往自己占的格（或附近几格）写入 `AtmosphereSourceRates`，由求解器负责输运。AO 不提供"设定房间温度"这种接口。
2. **原版加热器/制冷器已被 AO 完全接管**：`Building_Heater.TickRare` / `Building_Cooler.TickRare` 被 Prefix 返回 false，原版逻辑不再执行，改由 `CompTempControlAtmosphere` 逐格注入热量。
3. **加热器是单格点源；制冷器是两格对（冷侧 + 热侧），各再向外延伸一格，带 COP 模型与除湿产水。**
4. **Good Climate 的通风口直接写 `Room.Temperature`**，在 AO 地图上会被 `RoomTemperatureWriteHook` 捕成"整房间逐格平移"，两侧房间之间**能量不守恒**。本 patch 现有"改 `CompAirVent.speedLimit`"的做法**根本关不掉它**（GC 不读那个 comp）。
5. **Good Climate 改的 `CompProperties_TempControl.energyPerSecond` 会被 AO 采纳**（AO 读的就是这个 Props），但 GC 的电量换算按原版 COP，与 AO 的效率模型错位。
6. **超维穹顶目前只伪装读数**：`GenTemperature.TryGetTemperatureForCell` 与 `VacuumUtility.GetVacuum` 两个 Postfix 返回目标值，物理场里没有温度、没有压力、没有氧。生物圈只 `PushHeat(cell, map, 0.01f)`。
7. 要把这些建筑真正"放进 AO 体系"，需要给它们换上**连续源 + 体积源 + 氧气/湿度增量**三件套，而不是继续打读值补丁。

---

## 1. AO 的建筑哲学：源 → 场 → 输运

AO 暴露给建筑的写入面只有 10 个字段（`AtmosphereSourceRates`）：

| 字段 | 单位 | 语义 |
|---|---|---|
| `TemperaturePerTick` | ℃/tick | 显热注入 |
| `MoisturePerTick` | 绝对水汽/tick | 水汽注入 |
| `MistPerTick` | 雾密度/tick | 雾滴注入 |
| `WindPerTick` | m/s per tick | 速度冲量 |
| `PressurePerTick` | solver 单位/tick | 压力增量 |
| `OxygenPercentPerTick` | 百分点/tick | 氧浓度增量 |
| `GroundTemperaturePerTick` | ℃/tick | 地温增量 |
| `MaintainTargetWind` | bool | 是否持续拉向目标风 |
| `TargetWind` | Vector2 m/s | 目标风速 |
| `TargetWindBlendPerTick` | 0..1 | 每 tick 趋近比例 |

写入路径有三条，语义不同：

| 路径 | 行为 | 适用 |
|---|---|---|
| `RegisterContinuousSource` + `UpdateContinuousSource` | 登记"速率"，AO 按 `now - lastAppliedTick` 积分 | **持续设备**（首选） |
| `AddDeltas` / `AddTemperatureDelta` / `AddPressureDelta` | 立即塞进 `incoming*` 缓冲，下一步统一应用 | 一次性事件（爆炸、技能） |
| `AddVolumeSource` / `AddVolumeSourceAtTemperature` | 注入"体积 + 氧 + 温度"，会升压 | 补气、气罐、氧气推杆 |

关键差异：`AddDeltas` 同一步内多次调用会**叠加过冲**；`ContinuousSource` 由 AO 自己按 tick 差量积分，天然跨异步仿真任务，是建筑的正确姿势。AO 自己的 `SourceSlots` 就是这套封装。

---

## 2. AO 提供的建筑原语

### 2.1 温度控制：`CompTempControlAtmosphere`

替代原版 `CompTempControl`，挂在所有 `Building_TempControl` 子类上（`TempControlSubclassPatches` 自动扫全子类，除 Heater/Cooler 单独 Prefix）。

**加热（`RunHeater`）**：
```csharp
aim  = control.TargetTemperature + ext.outletToleranceK;      // ext 默认 2 K
factor  = Clamp01((aim - celsius) / 1f);                      // 1 K 线性带宽
efficiency = celsius <= 200 ? 1 : Clamp01((300 - celsius)/100);  // 超高温失效
TemperaturePerTick = factor * efficiency * fullRate;
fullRate = |energyPerSecond| * ext.heatFactor / 60;
```
- 只写**自身一格**。
- `ext.heatFactor` 来自 `AO_FacilityExtension`，默认 1。
- 温度 > 200 ℃ 开始降效，> 300 ℃ 完全失效——这是"火场里加热器无效"的机制。

**制冷（`RunCooler`）**：
```csharp
cold = pos + RotatedBy(South, rot);   hot = pos + RotatedBy(North, rot);   // 冷/热两侧
efficiency = 1/(1 + max(0, max(hotT - coldT, hotT - 40)) / 65) * Clamp01((coldT + 273.15)/150);
num = Clamp01((coldT - (target - tolerance)) / 1);
q   = num * efficiency * fullRate;
冷侧 TemperaturePerTick = -q * (延伸格存在 ? 0.5 : 1)
热侧 TemperaturePerTick = +q * 1.25 * (...)
风：TargetWind = 冷侧外法向 * 15 m/s，MaintainTargetWind = num>0，blend = 0.1
除湿：冷侧两格，vapor -= max(0, vapor - 0.4*VaporCapacity) * 0.5，潜热加到热侧
```
- **真实的 COP 直觉**：热侧越热、温差越大，效率越低；冷侧接近绝对零度时效率也降。
- **风只在制冷时产生**，加热器没有风。
- 除湿产水：`waterPoolKg`，每 0.25 kg 生成一个 `AO_Water`，单次最多 20 个。
- 冷侧与热侧都必须在地图内且可通行，否则停机。

### 2.2 湿度控制

| 建筑 | 类 | 行为 |
|---|---|---|
| 加湿器 | `CompHumidifier` | 目标 RH 百分比（默认 50%），每 60 tick 向自身与四邻格 `AddMistDelta`，消耗水（`CompRefuelable`），按 `waterKg*0.25` 换算 |
| 除湿器 | `CompDehumidifier` | 目标 RH（默认 40%），把每格水汽压到 `goal * VaporCapacity`，抽走的量转成潜热**放回同一格**，并积攒成水 |
| 蒸发冷却器 | `CompEvaporativeCooler` | 按饱和差 `Clamp01((SaturationHpa - vapor)/deficitReferenceHpa)` 决定强度，每 tick 降温 `heatPerSecond/60` 同时等量加水汽（`MoisturePerTick = num2 / latent`），耗燃料 |

注意三者的湿度语义不同：
- 加湿器加的是**Mist（雾滴）**，不是水汽——雾会先蒸发、再受饱和曲线约束。
- 除湿器减的是**绝对水汽**，并把潜热还给空气（所以除湿会**升温**）。
- 蒸发冷却器是"加水汽 + 降温"的耦合，本质是绝热加湿降温。

### 2.3 气流

| 建筑 | 类 | 行为 |
|---|---|---|
| 通风口 | `CompAirVent` | 面板开度 0..20 m/s（`maxSpeedLimit`/`defaultSpeedLimit`），关闭时等同墙。开度写进 `FaceOpen` 面掩膜，气只能按该速度上限流过 |
| 单场风扇 | `CompSingleFieldFan` | 在 `FieldRect` 内逐格设 `TargetWind`，风速 `ratedFlow/area * (power/maxPower)^(1/3)`——**立方根相似律** |
| 配对喷气 | `CompPairedJet` | 独立入口 + 出口，出口 3×13 格；注入高温（max 500 ℃）、高压（max 2 倍环境）、低氧（13%）、高速（150 m/s）气流，耗燃料 10/s、产水 1.5 kg/燃料 |

风扇的三种供能：`Electricity`、`StoredEnergy`（发条，900 J，180 秒满弦，`CompMainspring` + 自动上弦工作）、`Unlimited`（圣物，100 kW → 90 m/s）。

### 2.4 气体

| 建筑 | 类 | 行为 |
|---|---|---|
| 气泵 | `CompGasPump` | 把地图气体抽进气罐，`WorkGiver_GasPumpHauling` 负责搬运 |
| 空气压缩机 | `CompAirCompressor` | 把环境空气压进气罐（升压） |
| 气罐 | `CompGasTank` | 储存，`GasTankMath.StoredKPa` |
| 释放点 | `Building_GasReleaseSpot` | 把罐内气体按体积源释放 |
| 气罐定价 | `StatPart_OrbitGasPrice` | 轨道贸易价格 |

### 2.5 热源与燃烧

`CompHeatSourceAtmosphere` 接管所有 `CompHeatPusher`：
```csharp
factor = Burns ? Clamp(FireOxygen.Factor(map, pos, out _), 0, 3) : 1;   // 缺氧火弱
fireSize = ext.fireSize > 0 ? ext.fireSize : heatPerSecond / 64;
TemperaturePerTick = on ? (heatPerSecond * 3 / 60) * factor * tempFalloff : 0;
OxygenPercentPerTick = on && burns ? -2.7 * fireSize * factor / 150 : 0;
```
- **火会耗氧**，且耗氧速率随火势线性增长。
- `FireOxygen.Factor` 用 `pO2 = 总压 × 氧分数`，< 13 kPa 熄火，> 21.2 kPa 加速，上限 3 倍。
- 高温时 `tempFalloff = Clamp01((300 - T)/100)`，即 > 300 ℃ 不再加热（避免数值爆炸）。

其余：`CompHeatedFloorMat`（地暖，逐格 `GroundTemperaturePerTick`）、`SteamGeyserRelease`（蒸汽喷泉，注蒸汽 + 雾）、`CompTurbineRotor` + `WindTurbineAtmosphere`（风轮机，按风速发电）。

### 2.6 水与采集

`CompMoisturePumpWater`（抽湿取水，把水汽转成水）、`CompRainCollector`（集雨，读 `LocalRainfall`）。

### 2.7 观测

`AO_DopplerWeatherRadar`：通电且露天时解锁世界地图的 55 个 overlay 与高度选择器；`CompRadarInactiveNote` 负责未通电提示。

---

## 3. 原版建筑在 AO 下的真实行为

| 原版建筑 | AO 处理 | 后果 |
|---|---|---|
| `Heater` | `Building_Heater_TickRare_FacilityPatch` Prefix → `CompTempControlAtmosphere.RunHeater` | 只热自身一格；不再整房间升温；功率受 `energyPerSecond` 与 `heatFactor` 控制 |
| `Cooler` | 同上 → `RunCooler` | 冷/热两侧各一格 + 延伸格；产生 15 m/s 风；除湿产水；效率随热侧温度下降 |
| `Vent` | `Vent_AirFlow.xml` 加 `CompProperties_AirVent` | 从"开关"变成"0-20 m/s 限流阀" |
| `EvaporativeCooler` | `CompEvaporativeCooler` | 加水汽降温，耗燃料 |
| 各种 `CompHeatPusher` | `CompHeatSourceAtmosphere` | 火耗氧、高温失效 |
| `GeothermalGenerator` | ModExtension `airPassable=true, airFillPercent=0.5` | 地热口可以透气 |
| `Cooler`/`Vent` | ModExtension `windToleranceFactor=1.7, pressureToleranceFactor=3` | 更抗风压 |
| `GravshipHull` | `airLeakFactor=0, pressureToleranceFactor=5, heatExchangeFactor=0` | 船体不漏气 |
| `VacBarrier`/`AncientBlastDoor` | `pressureToleranceFactor=1000000` | 几乎不可能被压坏 |

**关键**：`AO_FacilityExtension` 是给任意 mod 建筑挂参数的统一入口：

| 字段 | 默认 | 作用 |
|---|---|---|
| `heatFactor` | 1 | 加热/制冷功率倍率 |
| `outletWindSpeed` | 15 | 制冷出风速度 |
| `outletToleranceK` | 2 | 温度死区 |
| `steamVolumeFactor` | 1 | 蒸汽量倍率 |
| `steamCelsius` | 100 | 蒸汽温度 |
| `steamMistGramsPerCubicMetre` | 5 | 蒸汽雾浓度 |
| `turbineReferenceSpeed` | 5 | 风轮机额定风速 |
| `turbineSafeSpeed` | 30 | 风轮机安全风速 |
| `fireSize` | -1（自动） | 火势 |
| `burnsOxygen` | -1（自动） | 是否耗氧 |
| `pusherHeatFactor` | -1（自动） | 热推子倍率 |

---

## 4. 四个 mod 的建筑清单与 AO 定位

### 4.1 Atmosphere Overhaul 自身（6 个建筑）

| defName | 尺寸 | 定位 | 关键参数 |
|---|---|---|---|
| `AO_IndustrialBlower` | 2×1 | 工业鼓风机 | 600 W / 200 W，120 m³/s，出口 4 m²，风场 2×2 |
| `AO_HouseholdFan` | 1×1 | 家用风扇 | 100 W / 50 W，10 m³/s，出口 1 m²，风场 1×1，可调 |
| `AO_WindUpFan` | 1×1 | 发条风扇 | 5 W，5 m³/s，900 J，满弦 180 s，自动上弦 |
| `AO_ArchotechWindProducer` | 5×5 | 圣物风机 | 100 kW，2250 m³/s，出口 25 m²，风场 5×7，目标 90 m/s |
| `AO_Turbojet` | 3×3 | 涡喷 | 入口 1×1、出口 3×13，燃料 10/s，水 1.5 kg/燃料，150 m/s，500 ℃，2 倍压，氧 13% |
| `AO_DopplerWeatherRadar` | 5×5 | 气象雷达 | 150 W，解锁世界 overlay |

**定位**：这六个是 AO 的"参考实现"，展示了四种输运手段——匀速风场（风扇）、定向射流（涡喷）、限流面（通风口）、全局观测（雷达）。它们不控制温度/湿度/氧，只控制**动量**。

**缺什么**：
- 没有"抽气扇/排风机"（把室内空气排到室外，形成负压）。
- 没有"新风机组"（引入室外空气并调温调湿）。
- 没有"风道"（长距离定向输运）。
- 风扇只吹、不吸；涡喷只喷、不回抽。

### 4.2 Explosive Diarrhea（无建筑，但有 gas 范例）

`ED_Defs.xml` 里没有任何 ThingDef 建筑。它提供的是：

- `AtmosphereGasDef` `ED_Biogas`：`storageMode=ManagedScalar`、`maxDensity=255`、`activeThreshold=1`、`AO_transferSetting`（advection 1 / diffusion 0.4 / iterations 1 / naturalVanish 0.004 / extinction 0.01 / vacuumVanish 25）、`renderSetting`（TintedQuads、`BioGasCloudThickA`、maxRenderDensity 200、opacityFloor 0.2）、`workerClasses`（`ExplosiveDiarrhea.GasWorker_Biogas`）。
- 两个 Ability 用 `CompAbilityEffect_StoolSpew` 注入气体与推力（`pushSpeed=45`、`heat=2`、`gasAmount=1500`）。

**定位**：这是"**自定义气体接入 AO 的最小样例**"。它证明了：
1. 新气体只需一个 `AtmosphereGasDef` + 一个 worker，AO 会自动给它独立的密度场、平流、扩散、消散。
2. 气体可以被渲染（`renderSetting`）、可以有心情/健康影响（`ThoughtWorker_Biogas`）。
3. 气体不参与压力/温度方程——`heat=2` 是能力另外加的。

**对 patch 的意义**：如果将来要做"蒸汽""冷媒""甲烷"，照这个样板即可，不需要改 AO 核心。

### 4.3 Good Climate（无新建筑，改原版三件套）

GC 只有 `1.7/Patches/GoodClimate.xml`（68 行），**不添加任何 ThingDef**，而是：

**通风口**：加 `GC_CompProperties`（`GC_IE_ventActivePowerOutput=-100`、`GC_IE_ventIntensiveExchangeRate=0.8`）+ `CompProperties_Power`（10 W）+ `GC_PlaceWorker`，并把造价改成 30 钢 + 1 组件。

运行时行为（`GC_MapComponent.GC_IE_ventIntensiveExchange`，每 60 tick）：
```csharp
float d = room.Temperature - room2.Temperature;
room2.Temperature += d * 0.8f;   // 直接写房间温度！
```
前置条件（`GC_IE_ventAllowIntensiveExchange`）：通电、开关开、两侧房间存在、都非"心理户外"、**`room.Vacuum <= 0`**。

**加热器/制冷器**：加 `GC_DefModExtension`（默认能量 ±21 J/s、功率 175/200 W、范围 21..105 J/s、步长 21）。运行时 `GC_DefInjector.GC_EE_applySettings` 直接改 `CompProperties_TempControl.energyPerSecond` 和 `CompProperties_Power.basePowerConsumption`。

**在 AO 下的实际行为**：
- 加热/制冷：AO 的 `CompTempControlAtmosphere` 读的就是 `Props.energyPerSecond`，所以 **GC 的功率调整生效**，且 AO 会用它自己的 `efficiency` 模型。但 GC 的 `GC_EE_CalculatePower(energy)` 按原版 COP 算电费，与 AO 的效率模型不一致——高负荷时电费对不上产热。
- 通风口：**AO 完全不接管 `Room.Temperature` 的写**（除了 `RoomTemperatureWriteHook` 这个钩子）。GC 写 `room2.Temperature += d*0.8` 会被钩子捕成"整房间逐格加同一个 Δ"，**没有从 room1 扣除**。结果：
  1. 两个房间之间能量不守恒（每 60 tick 凭空搬运 80% 温差 × room2 体积）；
  2. 若两房间体积差异大，总能量会漂移；
  3. 房间 > 3000 格时钩子静默忽略，GC 的通风口完全失效但不报错；
  4. AO 的 `CompAirVent` 开度对 GC 的这条路径**没有影响**——两条路径独立。

**这是本 patch 最需要修的地方**：现有 `Compat.GoodClimate` 改 `CompAirVent.speedLimit` 关不掉 GC 的直接写温度。

### 4.4 超维科技（穹顶 + 生物圈）

#### 4.4.1 `OmniForceFieldDome`（1×1 建筑，11×11 作用域）

`CompProperties_OmniForceFieldDome` 字段：
```csharp
applyRoof, supportRoof, roofSupportRadius, allowPlantsUnderRoof, allowSkyLightThroughRoof,
formRoom, maintainTemperature(默认 true), targetTemperature(21),
blockVacuum(默认 true), clearGas, clearPollution, cleanFilth, extinguishFire,
environmentTickInterval(250), pawnTickInterval(60),
width(11), height(11), interceptGroundProjectiles, interceptAirProjectiles
```

**当前实现**（`OmniForceFieldDome.cs`）：
- `MaintainEnvironment()`（:1107）只做四件事：`map.gasGrid.ClearCellUnsafe(c)`（清原版气体）、`pollutionGrid.SetPolluted(c,false,true)`、`FilthMaker.RemoveAllFilth`、`ExtinguishFiresAt`。
- 温度：`Patch_OmniForceFieldDome_Temperature`（:1348）Postfix `GenTemperature.TryGetTemperatureForCell`，直接返回 `targetTemperature`。**不写场。**
- 真空：`Patch_OmniForceFieldDome_Vacuum`（:1369）Postfix `VacuumUtility.GetVacuum`，直接返回 0。**不写场。**
- 屋顶：`applyRoof` 通过 `supportsRoof` + `roofGrid` 实现；`formRoom` 让它参与房间划分。

**在 AO 下的实际行为**：
- 穹顶内**温度读数**是 21 ℃，但 AO 场里该格可能是 -20 ℃ 或 50 ℃。任何走 `AtmosphereInterface.TryGetTemperature` 的系统（植物、小人、DCH 设备、本 patch）读到的是**真实场值**，不是 21。
- 真空同理：`VacuumEnvironment.IsVacuumMap` 看的是 biome，不是穹顶；`room.Vacuum` 看的是 AO 的气压场吗？——不是，AO 不接管 `Room.Vacuum`。所以穹顶内的 `Room.Vacuum` 仍是原版计算值。
- **穹顶不维持压力**：`blockVacuum` 只改读数，不阻止 AO 的压力场把穹顶内抽成真空。
- **穹顶不供氧**：`clearGas` 清的是原版 `gasGrid`，AO 的氧场不受影响。

**定位**：穹顶是"**几何 + 读数伪装**"，不是"物理场内的封闭环境"。要进 AO 体系，它必须变成"**边界条件 + 源**"。

#### 4.4.2 `OmniBiosphere`（3×3 建筑，作用域 = 玩家画的活动区 Area）

`CompProperties_CompBiosphere`：`applyFriendlyHediff`、`removeFriendlyHediffWhenLeaving`、`friendlyHediffDefName`（`OmniBiosphere_Protect`）。

`CompBiosphere` 公开状态：`areaName`、`controlTemperature`、`targetTemperature`、`ensureNoVacuum`、`SelectedArea`（`Area`）。

**当前实现**（`ApplyEffects`，:386）：
```csharp
foreach (IntVec3 cell in area.ActiveCells) {
    ApplyPlantGrowth(cell, map);                       // 植物生长速度调整
    if (controlTemperature) GenTemperature.PushHeat(cell, map, 0.01f);   // 象征性
}
```
- 温度、真空、光照全部"将通过 Patch 实现"（注释原话）。
- `Patch_CompBiosphere.cs` 里确实有 `GenTemperature.GetTemperatureForCell` Postfix 与 `Room.OpenRoofCount` 伪装。

**在 AO 下的实际行为**：与穹顶同构——读值被覆盖，场不变。生物圈内实际可能是真空、-40 ℃、0% 氧，但小人和植物"感觉"是 21 ℃、有空气。

**定位**：生物圈是"**区域级的读数伪装 + 植物生长加速**"，作用域是 `Area`（可任意形状），比穹顶的矩形更灵活。要进 AO 体系，它应当变成一个"**逐格连续源**"：遍历 `area.ActiveCells`，每格写温度/氧/湿度/压力。

---

## 5. 每个建筑缺什么

### 5.1 通用缺口

| 能力 | AO 有无 | 说明 |
|---|---|---|
| 设定房间温度 | ❌ | 只能逐格写；房间是原版概念，AO 不认 |
| 抽气/排风（负压） | ❌ | 有 `CompGasPump` 但那是罐装，不是排到室外 |
| 新风（引入室外空气） | ❌ | 没有"按室外温湿度进气"的建筑 |
| 风道 | ❌ | 风只能在开放格传播，没有管道 |
| 区域级（矩形/Area）源 | ⚠️ | `AddDeltas(map, CellRect, ...)` 有，但没有 `CellRect` 版连续源 |
| 氧分压控制 | ❌ | 只有 `OxygenPercentPerTick`，没有"维持 pO2"的闭环 |
| 湿度闭环 | ✅ | 加湿/除湿器有 RH 目标 |

### 5.2 逐建筑

**AO 风扇/涡喷**
- 缺：反向抽吸（把室内空气排出去）、风道、风向随压力自动调整。
- 建议：给 `CompSingleFieldFan` 加 `InletMode`（吸风），出口格写 `PressurePerTick` 负值制造负压。

**AO 通风口**
- 缺：双向流、按压差自动调节、与 GC 通风口互斥。
- 建议：`CompAirVent` 增加 `autoByPressure` 模式，开度由 `|Δp|` 决定。

**原版加热器/制冷器（AO 接管后）**
- 缺：出风口方向性（加热器也有热羽流）、湿度联动（制冷除湿已有，加热加湿没有）。
- 建议：`CompTempControlAtmosphere` 加热分支增加 `MoisturePerTick = -rate * 0.02`（加热空气相对湿度下降），或按 RH 目标联动加湿器。

**Good Climate 通风口**
- 缺：格对格能量守恒。
- 建议：不要写 `room2.Temperature`；改为在 AO 地图上跳过 GC 的这条路径，改用 AO 的 `CompAirVent` 限流面 + 两房间相邻格的 `AddTemperatureDelta` 对（一正一负）。

**Good Climate 加热/制冷**
- 缺：效率语义对齐。
- 建议：保留 GC 改 `energyPerSecond`（AO 会采纳），但在 AO 地图上把 GC 的电量公式替换为 AO 的 `lastEfficiency`。

**超维穹顶**
- 缺：压力、温度、氧、湿度的**真实写场**。
- 建议（最小可行）：
  1. `blockVacuum` → 每 250 tick 对穹顶内格 `AddPressureDelta` 把 `StoredPressurePa` 拉向 0（环境压），用 `AddVolumeSource` 补质量而不是硬改 `StoredPressurePa`。
  2. `maintainTemperature` → 每 250 tick 对每格 `TemperaturePerTick` 拉向 `targetTemperature`，限幅 ±20 K/步。
  3. 氧 → `OxygenPercentPerTick` 拉向 21%。
  4. 湿度 → `AddMoistureDelta` 拉向 50% RH。
  5. 保留两个 Postfix 作为"读数兜底"，但不再是唯一手段。

**超维生物圈**
- 缺：同穹顶，且作用域是 `Area`。
- 建议：把 `ApplyEffects` 改成"遍历 `area.ActiveCells`，每格写 5 个 rates"，并用一个 `SourceSlots` 缓存句柄（面积可能很大，必须限流+游标）。

---

## 6. 优先级与实施路线

### P0：修正现有 patch 的错误路径
1. `Compat.GoodClimate` 不再改 `CompAirVent.speedLimit`；改为在 AO 地图上**跳过 GC 的 `GC_IE_ventIntensiveExchange`**（Transpiler 或 Prefix），让通风口只走 AO 的限流面。
2. `Compat.VanillaTemperatureExpanded` 停止直接写 `Room.Temperature`，改 transpiler 替换其 `GenTemperature.*` 调用。
3. `Compat.OmniCrafter` 从 `AddDeltas` 改为 `RegisterContinuousSource`。

### P1：给穹顶/生物圈接物理场
4. 穹顶：温度/压力/氧/湿度四路连续源，游标轮转，每步预算 ≤ 500 格。
5. 生物圈：同构，按 `Area.ActiveCells` 分片。

### P2：补 AO 缺的建筑类型
6. 排风机（负压）、新风机组（按室外值进气）、风道。
7. 氧分压闭环（`pO2` 目标而非百分比）。

### P3：可选的"真实化"
8. 加热器的热羽流（`WindPerTick` 向上/外）。
9. 制冷器的冷凝水与结霜。
10. 风道中的沿程损失。

---

## 7. 风险清单

| 风险 | 说明 | 缓解 |
|---|---|---|
| `AddDeltas` 同一步叠加 | 多个设备同格写入会过冲 | 改用连续源 |
| 可压缩模式下 `TryGetPressurePascals` 失真 83 倍 | 读压必须用 `TryGetStaticPressurePascals` | 反射 internal 方法 |
| `ApplyThermodynamicPressureBaseline` 覆盖直接加压 | 不可压模式下每步重算 `Pressure.Read` | 用体积源，不用 `AddPressureDelta` |
| `RoomTemperatureWriteHook` 3000 格上限 | 大房间静默忽略 | 分片或改用连续源 |
| 世界帧 5 字段恒 0 | `FrontStrength` 等不可用 | 不要依赖 |
| 穹顶/生物圈面积过大 | 每步 O(cells) 反射调用 | 游标 + 预算 |
| GC 通风口与 AO 通风口双写 | 两条路径独立 | P0 第 1 条 |
| 连续源句柄泄漏 | 建筑销毁未注销 | `PostDeSpawn` 调 `RemoveAll` |

---

## 8. 附：关键类型索引

| 类型 | 文件 | 作用 |
|---|---|---|
| `AO_FacilityExtension` | `VanillaFacilities.cs` | 给任意建筑挂 AO 参数 |
| `CompTempControlAtmosphere` | `4VanillaFacilities.cs` | 加热/制冷替代实现（225 行） |
| `CompEvaporativeCooler` | `8VanillaFacilities.cs` | 蒸发冷却 |
| `CompHumidifier` / `CompDehumidifier` | `3HumidityDevices.cs` / `5HumidityDevices.cs` | 加湿 / 除湿 |
| `CompAirVent` | `1AirVent.cs` | 通风口开度 |
| `CompSingleFieldFan` | `CompSingleFieldFan.cs` | 风扇（385 行） |
| `CompPairedJet` | `CompPairedJet.cs` | 涡喷（290 行） |
| `CompGasPump` / `CompAirCompressor` / `CompGasTank` | `1GasPump.cs` / `4GasTanks.cs` / `2GasTanks.cs` | 气体链 |
| `CompHeatSourceAtmosphere` | `1HeatSources.cs` | 火/热源 + 耗氧 |
| `CompHeatedFloorMat` | `1HeatedFloorMat.cs` | 地暖 |
| `SourceSlots` | `2VanillaFacilities.cs` | 连续源槽位封装 |
| `FacilityMath` | `1VanillaFacilities.cs` | 温度/密度/latent 辅助 |
| `TempControlSubclassPatches` | `6VanillaFacilities.cs` | 自动给所有温控子类打补丁 |
| `GC_ThingComp` / `GC_MapComponent` | GC DLL | GC 通风口强化交换（直接写房间温度） |
| `GC_DefInjector` | GC DLL | 改 `energyPerSecond` 与 `basePowerConsumption` |
| `CompOmniForceFieldDome` | Omni `OmniForceFieldDome.cs`（1985 行） | 穹顶（读数伪装） |
| `CompBiosphere` | Omni `CompBiosphere.cs`（468 行） | 生物圈（Area + 0.01 热） |
