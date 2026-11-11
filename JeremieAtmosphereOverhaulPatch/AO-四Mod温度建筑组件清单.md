# AO · 四 Mod 温度建筑组件清单

> 状态：设计稿，未实现代码。
> 日期：2026-11-11。
> 依据：AO 1.7 源码（MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、VTE 1.6（MVID `37137F71-AA22-483B-B498-A2571293C2DD`）、CCC 1.6（MVID `5EB3120A-988A-4377-8F49-B68D29AEEEDF`）、Good Climate、OmniCrafter。
> 配套：`AO-ProxyHeat接管与三模式设计.md`、`AO-CCC与VTE重设计.md`、`AO-建筑体系与四Mod定位分析.md`。

---

## 0. 结论摘要

1. **AO 已经自动接管了 8 个建筑**，不需要任何 XML：VTE 的 5 个温控（`IndustrialHeater`/`IndustrialCooler`/`WallHeater`/`WallMountedHeater`/`WallMountedCooler`）与 GC patch 过的原版 `Heater`/`Cooler` 走 `CompTempControlAtmosphere`；`VTE_StandingCooler` 走 `CompHeatSourceAtmosphere`（原版 `CompHeatPusher` 被 Prefix 抑制，无双重）。
2. **AO 完全没接管的 4 类**：`Building_Vent`（原版通风被 `EqualizeTemperaturesThroughBuilding` 前缀禁用，VTE/GC 各一个）、`Building_CeilingFan`（自己声明了 `CompTick`，`OwnsHeatTick` 返回 true，跳过注入）、CCC 的全部 14 个（不继承 `Building_TempControl`）、Omni 的穹顶与生物圈（不继承任何温度类，靠读值 Postfix 伪装）。
   唯一"已经是正确形态"的是 `OmniPersonalTemperatureController`——它的读值代理覆盖 `AmbientTemperature` + `Safe/ComfortableTemperatureAtCell` 三点，语义上也本就该是"个人装备"而非环境改造，不需要动。
3. **最严重的两个隐患**：`VTE_CeilingFan` 绕过了 AO 的热源接管（它自己的 `CompCeilingFan.CompTick` 是 override 且不调 base），于是走 AO 的 `QueueLocalizedTemperatureDelta`，把本该摊到整房间的 `energy/CellCount` 集中到最多 5 格，局部强度约 5 倍；`AirThermal` 的 `CompProperties_TempControl` 是**裸标签**（`<li Class="CompProperties_TempControl" />`，`AirThermal` 段第 381 行），`energyPerSecond` 取默认 **12**、`min/maxTargetTemperature` 取默认 **−50..50** —— 在 −200 ℃ 环境下这个温控范围根本设不到室温，这是 CCC 在极寒地图上"怎么调都没用"的直接原因。
4. **需要新增 6 个组件**：`CompAirLoop`（CCC 回路）、`CompFieldStabilizer`（Omni/GC 区域恒温）、`CompRoomFieldExchange`（Vent 类）、`CompProxyHeatBridge`（Frostpunk 三模式）、`CompVacuumSeal`（Omni 真空）、`CompFacilityProfile`（通用参数覆盖 + 单位修正）。
5. **共 31 个建筑**要处理：VTE 9、CCC 14、GC 3（原版）、OT 5。总表 29 行是因为 CCC 的三个 `*Hidden` 管道与 OT 的两个幻影墙变体各合并成一行。

---

## 1. 组件库

### 1.1 AO 自带（自动注入，不新增）

**`CompTempControlAtmosphere`** —— 注入条件（`Buildings/5VanillaFacilities.cs:20-40`）：

```csharp
typeof(Building_Heater).IsAssignableFrom(thingClass)
|| typeof(Building_Cooler).IsAssignableFrom(thingClass)
|| (typeof(Building_TempControl).IsAssignableFrom(thingClass)
    && thingDef.GetCompProperties<CompProperties_TempControl>() != null)
```

且 `!HasComp(CompTempControlAtmosphere)`。

加热（`RunHeater`）：
```
fullRate   = |energyPerSecond| * heatFactor / 60        // ℃/tick
num8       = clamp01((TargetTemperature + outletToleranceK − T) / 1)
efficiency = T <= 200 ? 1 : clamp01((300 − T)/100)
TemperaturePerTick = num8 * efficiency * fullRate
```

制冷（`RunCooler`）：
```
efficiency = 1/(1 + max(T_hot − T_cold, T_hot − 40)/65) * clamp01((T_cold + 273.15)/150)
num4       = clamp01((T_cold − (Target − tol))/1) * efficiency * fullRate
冷端 −num4（前方有空间再 ×0.5），热端 +num4 × 1.25
```

**`CompHeatSourceAtmosphere`** —— 注入条件（`Buildings/2HeatSources.cs:26-42`）：

```
category == Building && designationCategory != null && !defName.StartsWith("AO_")
&& GetCompProperties<CompProperties_HeatPusher>() != null
&& heatPerSecond != 0
&& !OwnsHeatTick(compClass)      // 第三方 comp 自己声明了 CompTick/CompTickRare 就跳过
```

每 60 tick 写中心格：
```
num3 = heatPerSecond * (pusherHeatFactor > 0 ? pusherHeatFactor : 3) / 60
num5 = heatPerSecond < 0 ? clamp01((T+273.15)/150)
                         : (T <= 200 ? 1 : clamp01((300−T)/100))
TemperaturePerTick = ShouldPushHeatNow ? num3 * num5 : 0
```

### 1.2 本 patch 新增（6 个）

| 组件 | 用途 | 关键字段 |
|---|---|---|
| `CompFacilityProfile` | 覆盖 AO 的默认系数、修正单位、指定输出格 | `heatFactor`、`outletToleranceK`、`maxOutletCelsius`、`outputCellOffset`、`suppressVanillaHeat`、`energyUnit`（`KelvinPerRoom`/`KelvinPerCell`）、`spreadCells`（`fullRoom`/`local5`/`outputCells`）、`stirRadius`/`stirStrength` |
| `CompAirLoop` | CCC 的回路气袋，替代 `AirFlowNet` 的算术平均 | `volumeM3`、`massKg`、`temperature`、`vapor`、`oxygenPercent`、`pressurePa` |
| `CompFieldStabilizer` | 把一片区域锁到目标温度，写场 | `areaSize`、`targetCelsius`、`strength`、`maxPower`、`includeGround` |
| `CompRoomFieldExchange` | 两个房间之间按面积/温差交换热量，写场 | `exchangeRate`、`minDeltaK`、`volumeLimit`、`respectWalls` |
| `CompProxyHeatBridge` | ProxyHeat 三模式（地暖/空气源/喷流） | `enableFloorHeating`、`floorTargetCelsius`、`enableAirSource`、`airTargetCelsius`、`enableJet`、`jetCelsius`、`jetVolumePerTick` |
| `CompVacuumSeal` | 维持区域内真空/气压 | `targetPressurePa`、`areaSize`、`clearGas` |

---

## 2. 总表

| # | Mod | 建筑 | 类 | AO 现状 | 需要 |
|---|---|---|---|---|---|
| 1 | VTE | `VTE_IndustrialHeater` | `Building_TwoCellHeater` | ✅ `CompTempControlAtmosphere` | `CompFacilityProfile`（双格出热） |
| 2 | VTE | `VTE_IndustrialCooler` | `Building_TwoCellCooler` | ✅ 同上 | `CompFacilityProfile`（双格） |
| 3 | VTE | `VTE_WallHeater` | `Building_HeaterWithOffset` | ✅ 同上 | `CompFacilityProfile`（上限） |
| 4 | VTE | `VTE_WallMountedHeater` | `Building_HeaterWithOffset` | ✅ 同上 | `CompFacilityProfile`（输出格偏移） |
| 5 | VTE | `VTE_WallMountedCooler` | `Building_Cooler` | ✅ 同上 | 无 |
| 6 | VTE | `VTE_WallMountedVent` | `Building_Vent` | ❌ 通风被禁 | `CompRoomFieldExchange` |
| 7 | VTE | `VTE_StandingCooler` | `Building` + `CompHeatPusher` | ✅ `CompHeatSourceAtmosphere`，原版被 Prefix 抑制，无双重 | 无（可选 `CompFacilityProfile` 调强度） |
| 8 | VTE | `VTE_SmallCeilingFan` | `Building_CeilingFan` | ❌ 未注入（`OwnsHeatTick`）；走 AO 局部 5 格 | `CompFacilityProfile`（`spreadCells`+`stirRadius`） |
| 9 | VTE | `VTE_LargeCeilingFan` | `Building_CeilingFan` | ❌ 同上 | 同上 |
| 10 | CCC | `IntakeFan` | `Building_IntakeFan` | ❌ 无 | `CompAirLoop` |
| 11 | CCC | `LargeIntakeFan` | `Building_IntakeFan` | ❌ 无 | `CompAirLoop` |
| 12 | CCC | `AirThermal` | `Building_AirThermal` | ❌ 无（不是 TempControl） | `CompAirLoop` + `CompFacilityProfile` |
| 13 | CCC | `LargeAirThermal` | `Building_AirThermal` | ❌ 同上 | 同上 |
| 14 | CCC | `WallAirVentSmall` | `Building_AirVent` | ⚠️ 走 `Room.Temperature` | `CompRoomFieldExchange` |
| 15 | CCC | `WallAirVent` | `Building_AirVent` | ⚠️ 同上 | 同上 |
| 16 | CCC | `SurroundAirVent` | `Building_AirVent` | ⚠️ 同上 | 同上 |
| 17 | CCC | `SurroundAirVentWide` | `Building_AirVent` | ⚠️ 同上 | 同上 |
| 18 | CCC | `redAirPipe` | `Building_HotAirPipe` | — | `CompAirLoop`（管道容量） |
| 19 | CCC | `blueAirPipe` | `Building_ColdAirPipe` | — | 同上 |
| 20 | CCC | `cyanAirPipe` | `Building_FrozenAirPipe` | — | 同上 |
| 21 | CCC | 三个 `*Hidden` | 同上 | — | 同上 |
| 22 | GC | 原版 `Building_Heater` | `Building_Heater` | ✅ 接管（GC 设 21 J/s） | 无 |
| 23 | GC | 原版 `Building_Cooler` | `Building_Cooler` | ✅ 接管（−21） | 无 |
| 24 | GC | 原版 `Building_Vent` | `Building_Vent` | ❌ 通风被禁 | `CompRoomFieldExchange` |
| 25 | OT | `OmniForceFieldDome` | `Building` | ❌ 无 | `CompFieldStabilizer` + `CompVacuumSeal` |
| 26 | OT | `OmniBiosphere` | `Building` | ❌ 无 | 同上 |
| 27 | OT | `OmniBiosphere_mini` | 同上 | ❌ 无 | 同上 |
| 28 | OT | `OmniPersonalTemperatureController` | `Building` | ✅ 自身三个 Postfix 读值代理完整，不写场也正确 | 无 |
| 29 | OT | `OmniPhantomWall` / `2` | `Building_OmniPhantomWall` | ✅ 透风（`fillage` 未设 = `None`，`AirCanMoveThrough` 返回 true） | 无 |

---

## 3. VTE（9 个）

### 3.1 `VTE_IndustrialHeater`

- 类：`Building_TwoCellHeater : Building_HeaterWithOffset : Building_TempControl`
- Def：2×1、250 W、`energyPerSecond=63`、`CompProperties_Glower`
- **AO 现状**：`CompTempControlAtmosphere` 接管。`fullRate = 63 × 1 / 60 = 1.05 ℃/tick`，1 K 带宽比例控制，出口目标 `TargetTemperature + 2`。
- **问题**：`RunHeater`（`Buildings/4VanillaFacilities.cs`）里只有一句 `this.slots.Set(this.parent, 0, position, rates)`，`position = parent.Position`。2×1 的第二格完全没有热源，且 **`fullRate` 不按格数摊薄**——全部 1.05 ℃/tick 压在 1 格上。`Building_TwoCellHeater` 的原版逻辑是两格都参与。
- **需要**：
  - `CompFacilityProfile`：`outputCells` 支持多格（`parent.OccupiedRect().Cells` 全写，每格 `rates / N`）。
  - `heatFactor` 保持 1（默认）。
- **公式**（修后）：`TemperaturePerTick = num8 × efficiency × fullRate / N`，N = 占用格数。当前 N 被硬编码为 1。

### 3.2 `VTE_IndustrialCooler`

- 类：`Building_TwoCellCooler : Building_TempControl`
- Def：2×1、300 W、`energyPerSecond=-63`
- **AO 现状**：`RunCooler`，`fullRate = 1.05`，冷端 `pos+South`、热端 `pos+North`（**不是 `parent.Position` 本身**），`efficiency = 1/(1 + max(T_hot−T_cold, T_hot−40)/65) × clamp01((T_cold+273.15)/150)`。
  - 每端最多写 2 格：本端格 + 再向外一格（若可通行）。向外那格分 0.5，本端分 1.0：
    ```csharp
    num6 = flag1 ? 0.5f : 1f;   // 外侧可否通行
    TemperaturePerTick = -num4 * num6;   // 冷端
    TemperaturePerTick =  num4 * 1.25f * num7;   // 热端，固定 1.25 倍
    ```
  - 冷热端都带 `MaintainTargetWind=true`、`TargetWind = 方向 × outletWindSpeed(15)`、`BlendPerTick=0.1` —— **AO 的空调自己会吹风**，这点比原版好。
- **问题**：2×1 的另一个格（垂直于冷热轴的那格）不参与；`1.25` 的排热系数硬编码在代码里，`AO_FacilityExtension` 无对应字段。
- **需要**：`CompFacilityProfile`：`outputCells`（冷/热各扩展为两格）、`heatRejectionFactor`（默认 1.25，暴露给 XML）。

### 3.3 `VTE_WallHeater`

- 类：`Building_HeaterWithOffset : Building_TempControl`
- Def：175 W、`energyPerSecond=21`
- **AO 现状**：`fullRate = 0.35 ℃/tick`，`TargetTemperature + 2`，无温度上限。
- **问题**：原版 `PushHeat` 有 20–120 ℃ 的线性衰减（`Building_HeaterWithOffset.PushHeat`），AO 的 `RunHeater` 只读 `energyPerSecond`，不看这个上限 → AO 下墙暖可以烧到 300 ℃。
- **需要**：`CompFacilityProfile.maxOutletCelsius = 120`（或按建筑 Ext 配置），`RunHeater` 的 `efficiency` 乘上 `clamp01((maxOutlet − T)/band)`。

### 3.4 `VTE_WallMountedHeater`

- 类：`Building_HeaterWithOffset` + `TemperatureOutputPositionModExtension`
- Def：175 W、`energyPerSecond=21`、`shortCircuitInRain=true`
- **AO 现状**：同 3.3，但 **`RunHeater` 不读 `TemperatureOutputPositionModExtension`**，热量永远写 `parent.Position`。
- **问题**：挂墙加热器应该往墙的另一侧出热，AO 写在墙里（`ObstacleMask <= 0` 的格会被 `ApplyContinuousSourcesUnsafe` 跳过？需确认）。
- **需要**：`CompFacilityProfile.outputCellOffset` 读该 ModExtension 的 `outputPosition`，覆盖 `RunHeater` 的目标格。

### 3.5 `VTE_WallMountedCooler`

- 类：`Building_Cooler`（原版）
- Def：200 W、`energyPerSecond=-21`、`CompProperties_MountableOnWall`
- **AO 现状**：标准 `RunCooler`，`fullRate = 0.35`，冷端 south、热端 north。
- **需要**：无。这是四个里最干净的。

### 3.6 `VTE_WallMountedVent`

- 类：`Building_Vent`（原版）
- Def：无 Power（手动开关）、`CompProperties_Flickable`
- **AO 现状**：**完全不工作**。`Building_Vent.TickRare` → `GenTemperature.EqualizeTemperaturesThroughBuilding` → 被 `GenTemperature_EqualizeThroughBuilding_Patch` 前缀返回 false（`Patches/GenTemperature_EqualizeThroughBuilding_Patch.cs:13`）。
- **需要**：`CompRoomFieldExchange`
  - 找两端房间（`parent.Position` 与 `parent.Position + Rot4`）
  - 按 `min(房间A格数, 房间B格数) × exchangeRate` 交换，写 `pendingTemperatureDeltas`
  - `exchangeRate` 默认 0.15，可被 `AO_FacilityExtension` 覆盖
  - 门开时 `exchangeRate = 1.0`（等价于完全连通）

### 3.7 `VTE_StandingCooler`

- 类：`Building` + `CompProperties_HeatPusher`（`compClass = CompHeatPusherPoweredWithIdleDraw : CompHeatPusherPowered`）
- Def：135 W、`heatPerSecond=-21`、`heatPushMinTemperature=16`、`idlePowerDraw=13`
- **AO 现状**：`HeatSourceInjection` 检查 `OwnsHeatTick(CompHeatPusherPoweredWithIdleDraw)` —— 该类**没有**声明 `CompTick`/`CompTickRare` → `OwnsHeatTick` 返回 false → **注入** `CompHeatSourceAtmosphere`。
  - `num3 = -21 × 3 / 60 = -1.05 ℃/tick`，`num5 = clamp01((T+273.15)/150)`
  - `ShouldPushHeatNow` 反射调用 VTE 的 override，`heatPushMinTemperature=16` 生效
- **已确认无双重**：`CompHeatPusher.CompTick` 和 `CompTickRare` 都被 `CompHeatPusher_CompTick_SourcePatch` / `CompHeatPusher_CompTickRare_SourcePatch` Prefix 拦截（`Buildings/3HeatSources.cs:18`、`Buildings/4HeatSources.cs:18`）：
  ```csharp
  CompHeatSourceAtmosphere comp = __instance.parent.GetComp<CompHeatSourceAtmosphere>();
  return comp == null || !comp.Replaces;    // Replaces = Spawned && 有 AO 组件
  ```
  所以 AO 地图上原版 `CompHeatPusher` 不跑，只有 `CompHeatSourceAtmosphere` 生效。
- **需要**：无。`CompHeatSourceAtmosphere` 已正确处理负 `heatPerSecond`。
- **可选**：`CompFacilityProfile.heatFactor` 调整 `-21` 的实际强度（默认 `pusherHeatFactor=3`，即每 tick `-1.05 ℃`，每 60 tick 累计 `-63 ℃`）。

### 3.8 / 3.9 `VTE_SmallCeilingFan` / `VTE_LargeCeilingFan`

- 类：`Building_CeilingFan : Building` + `CompCeilingFan : CompHeatPusherPoweredWithIdleDraw`
- Def：30 W / `heatPerSecond=-11`；2×2、50 W / `-32`；`heatPushMinTemperature=17`、`minSpinRate=0.5`、`maxSpinRate=10`
- **AO 现状**：**未注入**。`CompCeilingFan` 声明了 `CompTick`（`Comps/CompCeilingFan.cs:25`），且 **override 不调 base** → `OwnsHeatTick` 返回 true → 跳过注入；`CompHeatPusher.CompTick` 的 Prefix 也拦不到它（虚调用走子类）。
  - 它自己每 60 tick 调 `GenTemperature.PushHeat(pos, map, heatPerSecond)` → AO 的 `PushHeat(cell, energy)` → `QueueLocalizedTemperatureDelta(cell, room, energy)`。
- **单位是对的，扩散范围不对**：原版 `Room.PushHeat`（`Verse/Room.cs:480`）是
  ```csharp
  this.Temperature += energy / (float)this.CellCount;
  ```
  `energy` 就是"整房间温度增量 × 格数"（K·格），不是 J。AO 的 `QueueLocalizedTemperatureDelta` 把它分给"origin + 十字邻居"**最多 5 格**，于是：
  - 原版 25 格房间：每格 `-11/25 = -0.44 ℃`
  - AO：每格 `-11/5 = -2.2 ℃`，**局部强度约 5 倍**
- **真正的问题**：风扇的本职是搅动空气，不是制冷。`-11` 和 `-32` 的热量可以忽略，但 AO 下它完全没产生风。
- **需要**：`CompFacilityProfile`
  - `energyUnit = KelvinPerRoom`（沿用原版语义），`spreadCells = 全房间或 5 格`（建议 `fullRoom` 以避免局部热点）
  - 新增 `stirRadius`（小型 3、大型 5）、`stirStrength`（`2`），用 `AtmosphereSourceRates.MaintainTargetWind` + `TargetWind` 产生局部环流
  - `stirStrength` 的效果：每 60 tick 向 `stirRadius` 内的格写 `TargetWind = 旋转向量 × stirStrength`，`TargetWindBlendPerTick = 0.3`

---

## 4. CCC（14 个）

### 4.0 共同前提

CCC 的所有建筑**都不继承 `Building_TempControl`**：

```
Building_AirFlowControl : Building
Building_AirThermal    : Building_AirFlowControl
Building_IntakeFan     : Building_AirFlowControl
Building_AirVent       : Building
Building_AirPipe       : Building
```

所以 `FacilityCompInjection` 一个都不会注入。CCC 完全走自己的 `CompAirFlowNet` 算术平均 + `Room.Temperature +=`。

**这是 CCC 在 AO 下失真的根源**：没有质量守恒、没有压力、没有回路状态。

### 4.1 `IntakeFan` / `LargeIntakeFan`

- Def：1×1 / 150 W / `baseAirFlow=300`；2×2 / 450 W / `1500`
- **AO 现状**：`CompAirFlowProducer`，取 8 邻平均温度作为 `IntakeTemperature`，风量随挡格与真空衰减。
- **需要**：`CompAirLoop`
  - 进气扇把**所在格**的空气抽进回路：`loop.massKg += ṁ·dt`，`loop.temperature = (loop.m·T + ṁ·dt·T_cell)/(loop.m + ṁ·dt)`
  - 同时向场写负散度（抽气）：`AddVolumeSource(cell, -flow/3000)`（`flow` 是 CCC 的抽象单位，需标定：`baseAirFlow=300` 对应多少 m³/s？建议 `300 → 0.5 m³/s`）
  - 真空检测：`AmbientSurfacePressurePa < 1000` 时 `flow *= 0.1`（与原版 `VacuumUtility` 一致）

### 4.2 `AirThermal` / `LargeAirThermal`

- Def：1×1 / 200 W / `thermalCapacity=900`；2×2 / 600 W / `4500`；`<li Class="CompProperties_TempControl" />` **裸标签**（`Buildings_Temperature.xml:381` / `:512`）
- **AO 现状**：**不被注入**（不是 `Building_TempControl` 子类）。走 `CompAirFlowTempControl.TickRare(CompTempControl)`（`CompAirFlowTempControl.cs:81-108`）：
  ```csharp
  IntakeTemperature = AirFlowNet.AverageIntakeTemperature;
  TargetTemperature = compTempControl.targetTemperature;      // 读 CompTempControl
  ConvertedTemperature = IntakeTemperature + DeltaTemperature;
  // generateDelta:
  num1 = TargetTemperature - IntakeTemperature;   // 目标误差
  num2 = ConvertedTemperature - IntakeTemperature; // 当前输出
  if (|num1 - num2| < 1)  DeltaTemperature += num1 - num2;                       // 死区，直接对齐
  else                   DeltaTemperature += energyPerSecond * ThermalEfficiency * ((num1-num2)/96);
  ```
  注意 `num2` 是**空调自身的输出温差**，不是房间温度——所以这是个"输出温差伺服"，不是"房间温度闭环"。
- **两个被忽略的默认值**：
  - `energyPerSecond = 12`（`CompProperties_TempControl` 默认，不是 0）
  - **`minTargetTemperature = -50`、`maxTargetTemperature = 50`** —— CCC 的 UI 只能把目标设在 −50..50 ℃。`-200 ℃` 地图上玩家想设 21 ℃ 可以设，但 AO 环境下 `CompTempControl` 的 `targetTemperature` 会被这段范围夹住，**无法设到 60 ℃ 以上**。
- **真正的问题**：`CompAirFlowTempControl` 的输出 `DeltaTemperature` 经 `AirFlowNet` 的**全局算术平均**后才到 Vent，再被 `Room.Temperature +=` 整房间平移。既没有质量流，也没有回路容量 → 无论 `thermalCapacity` 多大，房间温度变化速率与"有多少台 Vent、房间多大"完全脱钩。
- **需要**：`CompAirLoop` + `CompFacilityProfile`
  - `CompAirLoop` 持有回路气袋，`AirThermal` 改为改 `loop.temperature`：`loop.temperature += (Target − loop.temperature) × (1 − exp(−thermalCapacity/900 × dt))`（用指数趋近而不是线性 0.05，避免大机瞬间跳变）
  - `CompFacilityProfile`：`minTargetCelsius = -273`、`maxTargetCelsius = +1000`，覆盖 `CompProperties_TempControl` 的 −50..50 默认；`heatFactor = 1`；`maxOutlet = 300`（AO 的软顶）
  - 保留 `CompTempControl` 作为 UI 与 `targetTemperature` 的载体（玩家仍用原 UI 调温），`CompAirLoop` 只读它
  - 不再写 `Room.Temperature`；出口由 `CompAirLoop` 按气袋状态注入

### 4.3 四个 Vent（`WallAirVentSmall` / `WallAirVent` / `SurroundAirVent` / `SurroundAirVentWide`）

- Def：`baseAirExhaust = 50 / 100 / 300 / 600`，`flowType=Any`
- **AO 现状**：`Building_AirVent.TickRare` → `CompAirFlowTempControl` 的 `num5` 公式 → `ControlTemperatureTempChange` → **`Room.Temperature += a`**（整房间平移，`MaxDeltaKelvin=200` 封顶，>3000 格忽略）。
- **需要**：`CompRoomFieldExchange`
  - 读 `CompAirLoop` 的气袋状态（温度、湿、氧、压力）
  - 每 tick 向所在格注入 `rates = { TemperaturePerTick = (loop.temperature − T_cell) × rate, MoisturePerTick, OxygenPercentPerTick, PressurePerTick }`
  - `rate = baseAirExhaust / 600`（归一化到 0..1）
  - 同时从场抽气（负散度）以维持质量守恒

### 4.4 三色管道（`redAirPipe` / `blueAirPipe` / `cyanAirPipe` + 3 个 Hidden）

- Def：`CompProperties_AirFlow` + `flowType=Hot/Cold/Frozen`
- **AO 现状**：只是 `CompAirFlowTank` 的载体，不产热。`flowType` 在 CCC 里用来限制"哪种温度的空气能过"。
- **需要**：`CompAirLoop` 的管道容量
  - `CompAirLoop.pipeVolume` 按管道格数累加（每格 5 m³）
  - 管道本身有热容：`loop.temperature` 会被管道格的环境温度轻微影响（`k = 0.001/格`）
  - 三色区分：`Hot` 只能接 `AirThermal` 的加热端，`Cold` 只能接制冷端，`Frozen` 接 `-20 ℃` 以下

### 4.5 `Building_AirFlowControl`（阀）

- 22 行，只有开关。不产热，不需要组件。

---

## 5. GC（3 个，都是原版建筑）

Good Climate **不添加任何 ThingDef**，只 patch 原版 `Building_Heater` / `Building_Cooler` / `Building_Vent`。

### 5.1 原版 `Building_Heater`

- GC 的 `GC_DefModExtension` 改：`energyPerSecond=21`、`targetTemperature=21`、范围 19..105、步长 21、175 W
- **AO 现状**：`Building_Heater` 子类 → 注入 `CompTempControlAtmosphere` → `RunHeater`，`fullRate = 0.35 ℃/tick`
- **需要**：无。GC 的 `energyPerSecond` 与 AO 口径一致。

### 5.2 原版 `Building_Cooler`

- GC 改：`energyPerSecond=-21`、−105..−21、200 W
- **AO 现状**：`RunCooler`，`fullRate = 0.35`
- **需要**：无。

### 5.3 原版 `Building_Vent`

- GC 的 `GC_CompProperties`：`ventActivePowerOutput=-100`、`ventIntensiveExchangeRate=0.8`、10 W
- **AO 现状**：`EqualizeTemperaturesThroughBuilding` 被禁 → GC 的 `GC_MapComponent.GC_IE_ventIntensiveExchange()` 仍然每 60 tick 跑：
  ```csharp
  room2.Temperature += (room.Temperature - room2.Temperature) * 0.8f;
  ```
  这是**直接写房间**，AO 在 `Patches/5TemperaturePatches.cs:13` 拦截了 `RoomTempTracker.set_Temperature`，转到 `RoomTemperatureWriteHook.OnSet` → `ApplyRoomTemperatureWrite`，整房间逐格平移。
- **问题**：不守恒（A 房间不减少），0.8 的系数在 AO 下被 `MaxDeltaKelvin=200` 截断，但仍然是"魔法交换"。
- **需要**：`CompRoomFieldExchange`，并 patch 掉 `GC_IE_ventIntensiveExchange`
  - `exchangeRate = 0.8 × (min(cellsA, cellsB) / 25)`（按格数缩放，避免大房间瞬间拉平）
  - 写场而不是 `Room.Temperature`

---

## 6. OT（OmniCrafter，5 个）

### 6.1 `OmniForceFieldDome`

- 类：`Building` + `CompProperties_OmniForceFieldDome`（`width=11`、`height=11`、`interceptGroundProjectiles`、`interceptAirProjectiles`、`isStatic`）
- C# 默认：`maintainTemperature=true`、`targetTemperature=21`、`blockVacuum=true`、`clearGas`、`environmentTickInterval=250`
- **AO 现状**：**纯读值欺骗，不写场**。`Patch_OmniForceFieldDome_Temperature`（`OmniForceFieldDome.cs:1348`）Postfix `GenTemperature.TryGetTemperatureForCell`，直接返回 `targetTemperature`（源码注释："直接覆盖单格温度查询结果，让穹顶像独立恒温房间，但不污染 `Room.Temperature`"）；`Patch_OmniForceFieldDome_Vacuum`（`:1369`）Postfix `VacuumUtility.GetVacuum` 返回 0。
  - 所以 AO 的空气场/地温场**完全不受影响**：穹顶内的 21 ℃ 是假的，`AmbientSurfacePressurePa` 仍是真实值，风照样吹进来、地温照样按户外算。
  - 它绕过的机制比 ProxyHeat 更彻底：ProxyHeat 至少还走 `Thing.AmbientTemperature` 的 Postfix，而这里直接改底层查询函数，连 `CompTempControlAtmosphere` 的输出都会被覆盖。
- **问题**：
  - 读值边界生硬：11×11 之外立刻回到真实温度，没有过渡
  - `blockVacuum` 只骗 `VacuumUtility.GetVacuum`，不骗 `AtmosphereMapComponent.AmbientSurfacePressurePa`
  - 每 250 tick 一次的 `environmentTickInterval` 对 AO 无意义（AO 的温度不是 tick 驱动的，是每帧积分）
- **需要**：`CompFieldStabilizer` + `CompVacuumSeal`
  - `CompFieldStabilizer`：`areaSize=11`、`targetCelsius=21`、`strength=0.02`（每 tick 向目标收敛 2%）、`includeGround=false`、`maxPower=5000 W`
  - `CompVacuumSeal`：把区域内 `AmbientSurfacePressurePa` 提到 101325（或 `targetPressurePa=101325`），`clearGas=true` 时清空 `AtmosphereGasDef` 的密度场

### 6.2 `OmniBiosphere` / `OmniBiosphere_mini`

- 类：`Building` + `CompProperties_CompBiosphere`（3×3 / 1×1）
- C# 默认：`controlTemperature`、`targetTemperature`、`ensureNoVacuum`、`SelectedArea`
- **AO 现状**：以读值欺骗为主，写场只有象征性一笔。`CompBiosphere.ApplyEffects`（`CompBiosphere.cs:390-405`）对 `SelectedArea.ActiveCells` 每格调 `GenTemperature.PushHeat(cell, map, 0.01f)`，源码注释写明"象征性的推一点热量，为了让原版系统注意到；实际强制设置温度将通过 Patch 实现"。
  - `0.01` 是 K·格 语义 → AO 的 `QueueLocalizedTemperatureDelta` 只写中心格 + 十字 5 格，每格 **0.002 ℃**，区域边界格连这 0.002 都拿不到。
  - 真正维持 21 ℃ 的仍是 Patch 层（与 6.1 同族的 `TryGetTemperatureForCell` 覆盖）。
  - 另外 `CompBiosphere` 有 `ensureNoVacuum`，但同样只挡读值。
- **问题**：3×3 小区靠 0.002 ℃/tick 根本压不住 AO 的对流项；玩家会看到"生物圈里植物在长、温度却是假的"，一旦离开 Area 立刻冻结。
- **需要**：`CompFieldStabilizer` + `CompVacuumSeal`，参数同 6.1 但 `areaSize=3`（mini 为 1），`strength=0.05`（小区域可以更强）。

### 6.3 `OmniPersonalTemperatureController`

- 类：`Building` + `CompProperties_PersonalTemperature`（100 W）
- **功能**：给范围内的 pawn 一个"个人温度"，不改环境。
- **AO 现状**：**纯读值，且已经做得比 ProxyHeat 完整**。`Patch_PersonalTemperature` 三个 Postfix：
  - `Thing.AmbientTemperature` Getter（`Patch_PersonalTemperature.cs:8`）
  - `GenTemperature.SafeTemperatureAtCell`（`:27`）
  - `GenTemperature.ComfortableTemperatureAtCell`（`:43`）
  三条都走 `PersonalTemperatureUtility.TryGetOverride(pawn, pos, map, out value)`，带 `PersonalTemperatureReadContext` 的 Prefix/Finalizer 防止重入。
- **结论：不需要新增组件，也不需要改**。理由：
  - 它的语义是"个人装备"，**本来就不该改变环境**。这与本 patch"让环境读数回归物理场"的原则不冲突——它改的是"这个人感知多少"，不是"这格空气多少"。
  - 它已覆盖热舒适判定，ProxyHeat 只覆盖 `AmbientTemperature` 一个点。
  - AO 下唯一的变化是基准值从原版 `Room.Temperature` 变成 AO 的格温，这恰好是想要的。
- **唯一风险**：它与 ProxyHeat 同时启用时，两层 Postfix 会叠加。若玩家同时装了 ProxyHeat，`CompProxyHeatBridge` 的拦截层只拦 ProxyHeat，不拦 OT。

### 6.4 `OmniPhantomWall` / `OmniPhantomWall2`

- 类：`Building_OmniPhantomWall` / `Building_OmniPhantomWall2`
- 变体：`_Power`（`transmitsPower=true`）、`_PowerLight`（`Glower`）、`_all_`、`_Animal_`、`_Prisoners_`、`_AnomalyEntity_`
- **功能**：透明墙，挡人/动物/囚犯/实体，透电透光。
- **AO 现状**：**已经不挡风，无需处理**。判定在 `MapObstacleMaskBuilder.AirCanMoveThrough(Building)`（`RimWorld/MapObstacleMaskBuilder.cs:180-192`）：
  ```csharp
  if (edifice == null || edifice.def.Fillage != FillCategory.Full) return true;
  if (edifice.def.HasComp(typeof(CompAirVent))) return comp.IsOpen;
  if (BuildingAtmosphereExtension.For(edifice.def).airPassable) return true;
  return edifice is Building_Door d && d.Open && d.ExchangeVacuum;
  ```
  它读的是 **`def.Fillage`**，不是 `def.passability`。幻影墙 XML 只设了 `<passability>Standable</passability>`（`OmniPhantomWall.xml:73`），没设 `<fillage>` → 继承 `ThingDef` 默认 `FillCategory.None` → `!= Full` → `AirCanMoveThrough` 直接返回 true → `ObstacleMask = 1`，**完全透风**。
- **需要**：无。
- **附带结论**：这也解释了为什么 AO 的"墙"要靠 `fillage=Full` 才挡风——`passability` 只管寻路，`fillage` 才管气流。任何想"挡人不挡风"的 mod 建筑，只要不设 `fillage=Full` 就自动透风；反之要给它 `BuildingAtmosphereExtension.airPassable=true`。

---

## 7. 实施顺序

| 优先级 | 内容 | 理由 |
|---|---|---|
| **P0** | `CompFacilityProfile`（多格 + 上限 + 扩散范围） | 2×1 只写一格、`PushHeat` 局部 5 格过热、墙暖无视 120 ℃ 上限 |
| **P0** | `CompRoomFieldExchange` | 三类 Vent（VTE/CCC/GC）全部失效，影响面最大 |
| **P1** | `CompAirLoop`（CCC 全套） | CCC 是本 patch 的核心卖点，但工作量最大 |
| **P1** | `CompFieldStabilizer` + `CompVacuumSeal`（Omni） | 穹顶/生物圈是"人造环境"的代表 |
| **P2** | `CompProxyHeatBridge`（Frostpunk 三模式） | 独立文档 `AO-ProxyHeat接管与三模式设计.md` 已细化 |
| **P3** | `OmniPersonalTemperatureController` | 单点需求，可延后 |

---

## 8. 未决项

> 共 6 条，已关闭 4 条，剩 2 条。

1. **CCC 的 `baseAirFlow=300` 对应多少 m³/s**：需要实测或从 `CompAirFlowNet` 的换算反推，否则 `CompAirLoop` 的 `ṁ` 无从标定。Def 描述写的是 "Throughput: 900 cc/s"（`AirThermal`），`IntakeFan` 写 "300 cc/s"——这个 `cc` 几乎肯定不是立方厘米，可能是 "climate control unit" 或每 tick 的抽象流量，必须反推。
2. **`CompFieldStabilizer` 的功率换算**：`strength=0.02` 的 11×11 区域在 -200 ℃ 下需要多少 W？按 `ΔT × 面积 × 1.2 kg/m³ × 1005 J/(kg·K) × 5 m³` 估算，每 tick 2% 收敛约需 200 kW，可能过于耗电，需要按 `maxPower` 截断。

### 已关闭的四条（供追溯）

- **`Room.PushHeat` 的单位**：`energy` 是"整房间温度增量 × 格数"（K·格），不是 J。`Verse/Room.cs:480` `Temperature += energy / CellCount`。所以 `CompFacilityProfile.energyUnit` 默认取 `KelvinPerRoom`，不需要焦耳换算。
- **`CompHeatPusher` 是否被抑制**：被抑制。`Buildings/3HeatSources.cs:18` 与 `Buildings/4HeatSources.cs:18` 的 Prefix 在 `CompHeatSourceAtmosphere.Replaces == true` 时返回 false。`VTE_StandingCooler` 无双重。`VTE_CeilingFan` 因其 `CompCeilingFan.CompTick` 是 override 且不调 base，绕过了该 Prefix。
- **`AirThermal` 的 `CompProperties_TempControl` 是否被 CCC 读取**：**被读取**。`Building_AirThermal.SpawnSetup` 里 `GetComp<CompTempControl>()`，`TickRare` 传给 `CompAirFlowTempControl.TickRare(compTempControl)`，后者 `TargetTemperature = compTempControl.targetTemperature`。`CompProperties_TempControl` 是裸标签 → `energyPerSecond = 12`（默认）、`min/maxTargetTemperature = -50..50`（默认）。所以 AO 的 `CompTempControlAtmosphere` 若接管这台机器，`fullRate = 12 × 1 / 60 = 0.2 ℃/tick`，**不为 0**；真正的问题是 −50..50 的目标范围。
- **`OmniPhantomWall` 的 `ObstacleMask`**：读 `def.Fillage`，不读 `passability`。幻影墙 XML 只设 `passability=Standable`，`fillage` 未设 → `FillCategory.None` → 透风，无需处理。见 6.4。

---

## 附录 A. 源码索引

### AO（`f:/RimworldCodeIL/AtmosphereOverhaul/`）

| 文件:行 | 内容 |
|---|---|
| `Buildings/4VanillaFacilities.cs:17` | `class CompTempControlAtmosphere : ThingComp` |
| `Buildings/4VanillaFacilities.cs:31` | `CompTickRare`，`lastRunTick` 去重 + `FacilityMath.Atmosphere` 非空才跑 |
| `Buildings/4VanillaFacilities.cs:113` | `Run(Building_TempControl b)`，`fullRate = \|energyPerSecond\| × heatFactor / 60` |
| `Buildings/4VanillaFacilities.cs:135` | `RunHeater`，**只写 `parent.Position` 一格**，`fullRate` 不摊薄 |
| `Buildings/4VanillaFacilities.cs:151` | `RunCooler`，冷 `pos+South`、热 `pos+North`，每端最多 2 格，外格 0.5 分摊，热端 ×1.25，带 `MaintainTargetWind` |
| `Buildings/5VanillaFacilities.cs:24` | `CompTempControlAtmosphere` 注入条件 |
| `Buildings/2HeatSources.cs:19` | `OwnsHeatTick(Type)`，第三方 comp 自己声明 tick 就跳过 |
| `Buildings/2HeatSources.cs:35` | `CompHeatSourceAtmosphere` 注入条件 |
| `Buildings/3HeatSources.cs:18` | `CompHeatPusher.CompTickRare` Prefix，`Replaces` 时拦截 |
| `Buildings/4HeatSources.cs:18` | `CompHeatPusher.CompTick` Prefix，同上 |
| `Patches/6TemperaturePatches.cs:16` | `GenTemperature.PushHeat` Prefix → `AtmosphereMapComponent.PushHeat` |
| `Patches/5TemperaturePatches.cs:13` | `RoomTempTracker.set_Temperature` Prefix → `RoomTemperatureWriteHook.OnSet` |
| `Patches/RoomTemperatureWriteHook.cs` | `OnSet` → `ApplyRoomTemperatureWrite`，整房间 `+= Δ`，`MaxDeltaKelvin=200`，`MaxRoomCells=3000` |
| `RimWorld/MapObstacleMaskBuilder.cs:180` | `AirCanMoveThrough(Building)`，按 `def.Fillage` 判定，不按 `passability` |
| `RimWorld/AtmosphereMapComponent.cs:1135-1142` | `EncodeInt16` 的六个字段范围 |

### RimWorld 原版（`f:/RimworldCodeIL/Assembly-CSharp/`）

| 文件:行 | 内容 |
|---|---|
| `Verse/Room.cs:480` | `PushHeat(float energy)`：`Temperature += energy / CellCount`，`energy` 是 K·格 |
| `Verse/CompHeatPusher.cs:32` | `CompTick`，每 60 tick `PushHeat(pos, map, heatPerSecond)` |
| `Verse/CompHeatPusher.cs:40` | `CompTickRare`，`heatPerSecond * 4.1666665` |
| `RimWorld/CompProperties_TempControl.cs:14` | `energyPerSecond = 12f`（默认值，**不是 0**） |
| `RimWorld/CompProperties_TempControl.cs:16-17` | `minTargetTemperature = -50f`、`maxTargetTemperature = 50f` |

### CCC（`f:/RimworldCodeIL/CentralizedClimateControl/`）

| 文件:行 | 内容 |
|---|---|
| `CompAirFlowTempControl.cs:81` | `TickRare(CompTempControl)`，读 `AverageIntakeTemperature` 与 `targetTemperature` |
| `CompAirFlowTempControl.cs:94` | `generateDelta`，`ΔT += energyPerSecond × ThermalEfficiency × (err/96)`，死区 1 K 直接对齐 |
| `CompAirFlowTempControl.cs:19` | `const float DeltaSmooth = 96f` |
| `Building_AirThermal.cs:20` | `SpawnSetup` 取 `CompTempControl`（Def 裸标签，默认 12） |

### VTE / GC / Omni

| 文件:行 | 内容 |
|---|---|
| VTE `Comps/CompCeilingFan.cs:25` | `CompTick` override **不调 base** → 绕过 `CompHeatPusher` 的 Prefix |
| GC `GC_MapComponent.cs` | `GC_IE_ventIntensiveExchange()`，`room2.Temperature += (room.Temperature - room2.Temperature) * 0.8f` |
| Omni `OmniForceFieldDome.cs:1348` | `TryGetTemperatureForCell` Postfix，直接返回 `targetTemperature`，不写场 |
| Omni `OmniForceFieldDome.cs:1369` | `VacuumUtility.GetVacuum` Postfix，返回 0 |
| Omni `CompBiosphere.cs:390-405` | `ApplyEffects`，每格 `PushHeat(cell, map, 0.01f)`（象征性） |
| Omni `Patch_PersonalTemperature.cs:8` | `Thing.AmbientTemperature` Getter Postfix |
| Omni `Patch_PersonalTemperature.cs:27` | `GenTemperature.SafeTemperatureAtCell` Postfix |
| Omni `Patch_PersonalTemperature.cs:43` | `GenTemperature.ComfortableTemperatureAtCell` Postfix |
