# AO 视角下的 CCC 与 VTE 温度建筑全面分析

> 证据来源：dotPeek 反编译源码
> - CCC：`f:\RimworldCodeIL\CentralizedClimateControl\`（MVID `5EB3120A-988A-4377-8F49-B68D29AEEEDF`）
> - VTE：`f:\RimworldCodeIL\VanillaTemperatureExpanded\`（MVID `37137F71-AA22-483B-B498-A2571293C2DD`）
> - Def 数值：`294100/2007268173/1.6/Defs/`、`294100/3202046258/1.6/Defs/`
> - AO 侧接口：`AO源码全景阅读报告.md`、`AO-建筑体系与四Mod定位分析.md`
>
> 本文只做静态分析，不涉及代码修改。

---

## 第一章 两套系统的世界观差异

这是理解全部公式的前提，也是不能把两者套进同一个 AO 适配器的根本原因。

### 1.1 CCC：一台"空气输送系统"

CCC 的物理链路是**连续流动**的：

```
IntakeFan（抽室外/室内空气）
   → AirFlowNet（网络级风量/温度汇总）
   → AirThermal（给通过网络的空气调温）
   → AirPipe（三种颜色的管道，各是一张独立网络）
   → AirVent（把网络空气吹进房间）
```

关键点：

- 温度只是**空气携带的属性**。`ConvertedTemperature` 是"管网供气温度"，不是房间温度。
- 每个 tick，网络把**所有**运行中的 IntakeFan 的风量与进气温度做**算术平均**，把所有 AirThermal 的输出温度也做**算术平均**。距离、管长、拓扑完全不参与计算。
- 房间温度的改变发生在 AirVent 里，且是"一次性加法"，不是持续热流。
- 效率有两个：`ThermalEfficiency`（热容/风量）与 `FlowEfficiency`（进风/出风），后者在 AirVent 里被**平方**使用。

一句话：**CCC 的主变量是风量，温度是附着量。**

### 1.2 VTE：一套"温度施加器"

VTE 的物理链路是**能量注入**：

```
HeaterWithOffset / TwoCellHeater / TwoCellCooler / AcUnit
   → 直接对目标格算出一个温度变化
   → room.Temperature += a
```

关键点：

- 没有"空气"这个中间载体。每个设备独立算能量，直接改房间温度。
- 主变量是 `energyPerSecond`（J/s），经 `× 4.1666665`（= 250/60）转成每 tick 的温度变化量。
- AC 管网（PipeSystem）只传两样东西：**目标温度的广播** 和 **一个效率系数**。它不传空气、不传冷媒质量。
- 吊扇本质是"局部吸热器"，不是"吹风器"（见 3.6）。

一句话：**VTE 的主变量是能量，空气只是被扰动的结果。**

### 1.3 这个差异具体体现在哪

| 维度 | CCC | VTE |
|---|---|---|
| 中间载体 | 空气（有体积、有风量） | 无 |
| 主变量 | 风量 m³/s | 能量 J/s |
| 温度作用点 | 管网供气温度 → 出风口写房间 | 每个设备直接写房间 |
| 房间大小的影响 | 没有直接体现（`room.Temperature += a`，与格数无关） | 通过 `ControlTemperatureTempChange` 内部折入 |
| 管网拓扑 | 决定哪些设备同网 | 只决定目标温度广播给谁 |
| 效率来源 | 热容/进风比、进风/出风比 | 管网供/耗比（0～1.5） |
| 是否搬运氧气/湿度 | 否 | 否 |

---

## 第二章 CCC 的全部温度相关建筑

### 2.1 建筑清单

| defName | 类 | 尺寸 | 电 | 关键参数 | 角色 |
|---|---|---|---|---|---|
| `IntakeFan` | `Building_IntakeFan` | 1×1 | 150 W | `baseAirFlow=300` | 进气扇（Producer） |
| `LargeIntakeFan` | `Building_IntakeFan` | 2×2 | 450 W | `baseAirFlow=1500` | 大型进气扇 |
| `AirThermal` | `Building_AirThermal` | 1×1 | 200 W | `thermalCapacity=900`，`energyPerSecond=21`（默认） | 温控机组（TempControl） |
| `LargeAirThermal` | `Building_AirThermal` | 2×2 | 600 W | `thermalCapacity=4500` | 大型温控机组 |
| `WallAirVentSmall` | `Building_AirVent` | 1×1 | — | `baseAirExhaust=50` | 小型出风口（Consumer） |
| `WallAirVent` | `Building_AirVent` | 1×1 | — | `baseAirExhaust=100` | 出风口 |
| `SurroundAirVent` | `Building_AirVent` | 1×1 | — | `baseAirExhaust=300` | 环绕出风口 |
| `SurroundAirVentWide` | `Building_AirVent` | 2×1 | — | `baseAirExhaust=600` | 宽环绕出风口 |
| `redAirPipe` / `blueAirPipe` / `cyanAirPipe` | `Building_AirPipe` | 1×1 | — | `flowType=Hot/Cold/Frozen` | 三种网络管道 |
| `AirFlowControl` | `Building_AirFlowControl` | 1×1 | — | 只有开关 | 控制阀 |

> `CompProperties_AirFlow` 只有 5 个字段：`baseAirExhaust`、`baseAirFlow`、`flowType`、`thermalCapacity`、`transmitsAir`。
> `CentralizedClimateControlSettings` 只有两个玩家可调倍率：`AirFlowMultiplier`、`AirExhaustMultiplier`（默认 1.0）。

### 2.2 AirFlowNet：网络级汇总（每个 tick 一次）

```csharp
tickProducers():
    CurrentIntakeAir        = Σ 运行中 Producer.CurrentAirFlow
    AverageIntakeTemperature = Σ Producer.IntakeTemperature / 数量

tickTempControllers():
    ThermalCapacity          = Σ 运行中 TempControl.ThermalCapacity
    AverageConvertedTemperature = Σ TempControl.ConvertedTemperature / 数量
    // 没有 TempControl 时：ThermalCapacity = CurrentIntakeAir，
    //                     AverageConvertedTemperature = AverageIntakeTemperature

tickConsumers():
    CurrentExhaustAir = Σ 运行中 Consumer.ExhaustAirFlow

ThermalEfficiency = CurrentIntakeAir <= 0 ? 0 : ThermalCapacity / CurrentIntakeAir
FlowEfficiency    = CurrentExhaustAir > 0 ? min(1, CurrentIntakeAir / CurrentExhaustAir) : 0
```

**算术平均是这套系统的核心简化**：10 台进气扇分布在基地两端，与 1 台在温控机旁边，对网络来说完全等价。这也是它为什么在 AO 下必须重做的原因——AO 的温度是空间场，不会接受"全局平均温度"。

### 2.3 IntakeFan（进气扇）

```csharp
// 8 邻格全部可通行才工作
foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(Position, Rotation, def.Size)):
    if (GenGrid.Impassable(c, Map)) { CurrentAirFlow = 0; IsBlocked = true; return; }
    sumTemp += GridsUtility.GetTemperature(c, Map);
    sumVac  += VacuumUtility.GetVacuum(c, Map);

IsBlocked = false;
IntakeTemperature = sumTemp / 格数;
CurrentAirFlow = (baseAirFlow − 被挡格数 × (1/130)) × AirFlowMultiplier;
if (sumVac > 0) CurrentAirFlow *= (1 − sumVac / 格数);
```

- 进气温度 = 8 邻格温度算术平均。
- 风量随"被挡格数"线性衰减（每格扣 1/130），随真空度线性衰减。
- **在 AO 下**：`GetTemperature` 读的是 AO 的格温，这部分是"正确"的；`VacuumUtility.GetVacuum` 在 AO 地图上恒为 0（AO 不接管该 API），所以真空衰减这一分支永远不触发。

### 2.4 AirThermal（温控机组）—— CCC 的核心公式

```csharp
public const float DeltaSmooth = 96f;

TickRare(compTempControl):
    IntakeTemperature    = AirFlowNet.AverageIntakeTemperature;
    TargetTemperature    = compTempControl.targetTemperature;
    ConvertedTemperature = IntakeTemperature + DeltaTemperature;
    generateDelta(compTempControl);

generateDelta(compTempControl):
    err = TargetTemperature − IntakeTemperature;      // 目标偏差
    cur = ConvertedTemperature − IntakeTemperature;  // 当前已加偏差 = DeltaTemperature
    IsHeating = TargetTemperature > IntakeTemperature;

    if (|err − cur| < 1.0f):
        DeltaTemperature += err − cur;                // 进入 1 K 死区：直接吸附到目标
        IsStable = true;
    else:
        IsStable = false;
        DeltaTemperature += energyPerSecond × ThermalEfficiency × ((err − cur) / 96);
```

要点：

1. **比例控制器，时间常数 96 tick**（约 1.6 秒游戏时间）。
2. `energyPerSecond` 取 `CompProperties_TempControl` 的默认值 **21**（CCC 的 Def 里是空的 `<li Class="CompProperties_TempControl" />`）。
3. `ThermalEfficiency = Σ thermalCapacity / Σ intakeAir`：
   - `AirThermal`(900) + `IntakeFan`(300) → **3.0**
   - `LargeAirThermal`(4500) + `LargeIntakeFan`(1500) → **3.0**
   - 多台混搭时按总量比值。
4. 所以单台机组的实际增益是 `21 × 3.0 / 96 ≈ 0.656 K/tick`（每 tick 向目标推进的幅度）。
5. **加热时不排热**；**制冷时**向设备南侧那一格排热：

```csharp
if (IsHeating) return;
IntVec3 cell = Position + RotatedBy(South);
float d = min(|Converted − Intake|, 20);
GenTemperature.PushHeat(cell, Map, (25 + d) × 1.25);
```

即制冷时固定排 `(25 + ΔT) × 1.25` 的热到室外侧——**这就是它的"冷凝器"**。

6. 前置条件：整排南侧必须可通行（被堵则 `IsBlocked = true` 停机）。

### 2.5 AirVent（出风口）—— CCC 的"搬运"落点

```csharp
TickRare():
    CompAirFlowConsumer.TickRare();                  // 取网络供气温度
    if (!IsOperating() || !IsActive()) return;

    IntVec3 cell = Position + RotatedBy(North);
    if (GenGrid.Impassable(cell, Map)) return;

    ConvertedTemperature = CompAirFlowConsumer.ConvertedTemperature;
    d     = ConvertedTemperature − GetTemperature(cell);
    mag   = min(|d|, 10);
    sign  = d < 0 ? −1 : +1;
    therm = ThermalEfficiency > 0 ? 1 : 0;
    eff   = FlowEfficiency² × therm;

    energy = mag × 0.25 × (ExhaustAirFlow/100) × eff × 4.1666665 × 12 × sign;
    a = GenTemperature.ControlTemperatureTempChange(cell, Map, energy, ConvertedTemperature);

    Room room = GetRoomOrAdjacent(cell, RegionType.Set_All);
    float vacuum = room.Vacuum;
    if (a ≈ 0) return;
    if (vacuum > 0):
        float recover = (100 / room.CellCount × AirPerSecondPerHundredCells × 4.1666665) × FlowEfficiency;
        room.Vacuum = Max(0, room.Vacuum − recover);
        if (room.Vacuum > 0) a *= (1 − vacuum);

    room.Temperature += a;                            // ← 直接写房间
```

拆解：

- `mag = min(|供气温度 − 当前格温度|, 10)`——最大驱动温差 10 K。
- `0.25 × (ExhaustAirFlow/100) × 12 = 0.03 × ExhaustAirFlow`——风量的线性系数。
- `FlowEfficiency²`——出风受阻时**平方惩罚**。
- `4.1666665 = 250/60`——能量→温度。
- 最终 `room.Temperature += a`：**整房间加法，不做质量守恒**。

`ExhaustAirFlow` 与 `AirPerSecondPerHundredCells`：

```csharp
ExhaustAirFlow = Props.baseAirExhaust × AirExhaustMultiplier;   // 默认 1.0
AirPerSecondPerHundredCells = ExhaustAirFlow / 5000;
```

### 2.6 CCC 在 AO 下的实际行为

| 环节 | 在 AO 下 | 判定 |
|---|---|---|
| IntakeFan 读格温 | 读 AO 温度场 | ✅ 正确 |
| IntakeFan 读真空 | `VacuumUtility.GetVacuum` 恒 0 | ❌ 失效 |
| AirThermal 算供气温度 | 纯网络算术 | ✅ 可跑 |
| AirThermal 制冷排热 | `PushHeat` 被 AO patch 成 localized delta | ✅ 可跑 |
| AirVent 写房间 | `room.Temperature += a` 被 `RoomTemperatureWriteHook` 捕获 | ⚠️ 变成"整房间逐格平移"，>3000 格静默忽略 |
| 风场 | 完全没有 | ❌ 缺失 |
| 氧气/湿度 | 完全没有 | ❌ 缺失 |
| 质量守恒 | 没有（房间凭空多/少能量） | ❌ 缺失 |

**结论：CCC 在 AO 上"温度能进房间，但不是以空气的方式进去"。** 它现在是一条"读 AO 格温 → 网络平均 → 整房间加温"的链路，中间丢掉了风量、成分、动量。

---

## 第三章 VTE 的全部温度相关建筑

### 3.1 建筑清单

| defName | 类 | 尺寸 | 电 | energyPerSecond | 角色 |
|---|---|---|---|---|---|
| `VTE_WallHeater` | `Building_HeaterWithOffset` | 1×1 | 175 W | +21 | 单格壁挂加热器 |
| `VTE_WallMountedHeater` | `Building_HeaterWithOffset` | 1×1 | 175 W | +21 | 壁挂加热器（贴墙） |
| `VTE_IndustrialHeater` | `Building_TwoCellHeater` | 2×1 | 250 W | +63 | 双格工业加热器 |
| `VTE_WallMountedCooler` | `Building_TwoCellCooler` | 1×1 | 200 W | −21 | 双格壁挂冷却器 |
| `VTE_IndustrialCooler` | `Building_TwoCellCooler` | 2×1 | 300 W | −63 | 双格工业冷却器 |
| `VTE_ACUnit` | `Building_AcUnit` | 2×2 | 100 W | −21 | 管网空调室内机 |
| `VTE_ACUnitWall` | `Building_AcUnit` | — | — | −21 | 壁挂式管网空调 |
| `VTE_ACControlUnit` | `Building_AcControlUnit` | 2×2 | 200 W | — | 管网目标温度控制器 |
| `VTE_SmallCeilingFan` | `Building_CeilingFan` | 1×1 | 30 W | heatPerSecond = −11 | 小吊扇 |
| `VTE_LargeCeilingFan` | `Building_CeilingFan` | 2×2 | 50 W | heatPerSecond = −32 | 大吊扇 |
| `VTE_WallMountedVent` | 原版 `Building_Vent` | 1×1 | — | — | 壁挂通风口 |
| `VTE_StandingCooler` | `Building` + HeatPusher | 1×1 | 135 W（待机 13） | heatPerSecond = −21 | 落地冷风机 |

> `energyPerSecond` 的符号约定：**正 = 加热能力，负 = 制冷能力**。VTE 用符号本身表达方向，只有 `Building_AcUnit` 会再乘一个 ±1 翻转（见 3.5）。

### 3.2 Building_HeaterWithOffset（单格加热基类）

```csharp
SpawnSetup:
    outputOffsetNorth = def.HasModExtension<TemperatureOutputPositionModExtension>()
        ? def.GetModExtension<...>().offsetNorth
        : IntVec3.North;

TickRare():
    if (!PowerOn) return;
    bool ok = PushHeat(Position + RotatedBy(outputOffsetNorth));
    PowerOutput = ok ? −PowerConsumption
                     : −PowerConsumption × lowPowerConsumptionFactor;

PushHeat(cell, maxTemperature = 120):
    T = GetTemperature(cell);
    // 温度衰减因子
    if (T < 20)  factor = 1;
    else if (T > maxTemperature) factor = 0;
    else factor = InverseLerp(maxTemperature, 20, T);     // 20→max 线性降到 0

    energy = energyPerSecond × factor × 4.1666665;
    a = GenTemperature.ControlTemperatureTempChange(cell, Map, energy, TargetTemperature);
    if (a ≈ 0) return false;
    GridsUtility.GetRoom(cell, Map).Temperature += a;      // ← 直接写房间
    return true;
```

- `4.1666665 = 250/60`：把"每秒能量"换成"每 tick 温度"的经验系数。
- 衰减因子让加热器在接近上限时自动降功率（模拟"加热到 120 ℃ 就无效"）。
- `TwoCellHeater` 把 `maxTemperature` 传成 **1000**，所以几乎不衰减。

### 3.3 Building_TwoCellHeater（双格工业加热器）

```csharp
TickRare():
    cell1 = Position + RotatedBy(North);
    cell2 = cell1 + RotatedBy(East);
    ok1 = PushHeat(cell1, 1000f);
    ok2 = PushHeat(cell2, 1000f);
    PowerOutput = (ok1 | ok2) ? −PowerConsumption
                              : −PowerConsumption × lowPowerConsumptionFactor;
```

两格各吃一份完整 `energyPerSecond`，即**总功率翻倍**（63 J/s 在每格都算一遍）。这是 VTE 的"工业级"设计：2×1 建筑，两格都加热。

### 3.4 Building_TwoCellCooler（双格冷却器）

```csharp
TickRare():
    coldA = Position + RotatedBy(South);
    coldB = coldA + RotatedBy(East);
    hotA  = Position + RotatedBy(North);
    hotB  = hotA  + RotatedBy(East);
    ok1 = PushCoolAir(coldA, hotA, 100f);
    ok2 = PushCoolAir(coldB, hotB, 100f);

PushCoolAir(cold, hot, minTemperature = 100):
    if (Impassable(hot) || Impassable(cold)) return false;

    T_hot = GetTemperature(hot);
    // 效率：热侧越热越差，130 ℃ 归零
    factor = clamp01(1 − T_hot/130);

    energy = energyPerSecond × factor × 4.1666665;          // energyPerSecond 为负
    a = GenTemperature.ControlTemperatureTempChange(cold, Map, energy, TargetTemperature);
    if (a ≈ 0) return false;

    GetRoom(cold).Temperature += a;                          // 冷侧写房间
    GenTemperature.PushHeat(hot, Map, −energy × 1.25);       // 热侧排 1.25 倍
    return true;
```

对比 AO 自己的 `RunCooler`：

| 项 | VTE TwoCellCooler | AO CompTempControlAtmosphere |
|---|---|---|
| 效率 | `1 − T_hot/130` | `1/(1 + max(0, max(ΔT, T_hot−40))/65) × clamp01((T_cold+273)/150)` |
| 热侧排热 | `1.25 ×` 冷侧能量 | `1.25 ×` 且带额外因子 |
| 冷侧写房间 | 直接 `+=` | `TemperaturePerTick` 连续源 |
| 出风 | 无 | `TargetWind = 冷侧外法向 × 15` |

**同样的"冷热两侧"拓扑，两套效率公式完全不同。** VTE 的效率只看热侧绝对温度；AO 的效率看温差、热侧过热度和低温效率三重因子。

### 3.5 Building_AcUnit（管网空调室内机）

```csharp
TickRare():
    if (!PowerOn || !ResourceOn) return;
    cell = Position + RotatedBy(outputOffsetNorth);
    if (Impassable(cell)) { ... 低功率 ... return; }

    T = GetTemperature(cell);
    // 注意：energyPerSecond 为 −21，再乘一个由目标与当前温度比较得到的 ±1
    energy = energyPerSecond × 4.16666651
           × (TargetTemperature < T ? +1 : −1);
    if (T > 120) energy = 0;

    a = GenTemperature.ControlTemperatureTempChange(cell, Map, energy, TargetTemperature);
    if (a ≈ 0) return;

    GetRoom(cell).Temperature += a;
    PowerOutput = −PowerConsumption × (2 − AcPipeNet.Efficiency);   // 高功率
    // 低功率分支再乘 lowPowerConsumptionFactor
```

- **双向机**：`energyPerSecond = −21` 是"制冷能力"，乘 ±1 后可以变成加热。这是 VTE 里唯一显式翻转方向的建筑。
- **电费与管网效率挂钩**：`2 − Efficiency`。效率 1.0 时电费 100%；效率 1.5 时 50%；效率 0 时 200%。
- 它是 `CompAcTempControl : CompTempControl`，所以有 `TargetTemperature` 和 `IndependentTemp`（是否脱离网络广播）。

### 3.6 Building_AcControlUnit（管网控制器）

```csharp
TargetNetworkTemperature = 21f;      // Scribe_Values 持久化

InterfaceChangeTargetNetworkTemperature(offset):
    TargetNetworkTemperature = clamp(TargetNetworkTemperature + offset, −273.15, 1000);
    foreach (CompResource r in AcPipeNet.receivers):
        CompAcTempControl c = r.parent.GetComp<CompAcTempControl>();
        if (c != null && !c.IndependentTemp)
            c.targetTemperature = TargetNetworkTemperature;    // 广播
```

`AcPipeNet.Efficiency` 的分段线性公式：

```csharp
CalculateEfficiency():
    diff = Production − Consumption;
    if (Production == 0) Efficiency = 0;
    else if (diff < 0)   Efficiency = max(0.0f, 1.0f − 0.05f × |diff|);   // 赤字：每单位扣 5%
    else                 Efficiency = min(1.5f, 1.0f + 0.01f × |diff|);   // 盈余：每单位加 1%
```

- 范围 `[0, 1.5]`。
- 赤字惩罚是盈余奖励的 **5 倍**。
- `WouldOverload()` 用同一公式（把待机机组也算进去）判断是否 <= 0，为真则全部停机。
- 只有**恰好 1 个** Controller 在运行时，才会自动开关机组。

### 3.7 Building_CeilingFan（吊扇）—— 注意：它不吹风

```csharp
// CompCeilingFan : CompHeatPusherPoweredWithIdleDraw
CompTick():
    if (!PowerOn) return;
    bool pushing = ShouldPushHeatNow;
    if (pushing && Gen.IsHashIntervalTick(parent, 60))
        GenTemperature.PushHeat(parent.PositionHeld, parent.MapHeld, Props.heatPerSecond);
    spinRate = pushing ? min(maxSpinRate, spinRate + maxSpinRate/180)
                       : max(minSpinRate, spinRate − maxSpinRate/180);
    (parent as Building_CeilingFan).angle += spinRate;      // 只是视觉旋转
```

- `heatPerSecond = −11`（小）/ `−32`（大），`heatPushMinTemperature = 17`。
- 所以吊扇实际是"**温度高于 17 ℃ 时，在自身格吸热**"。
- **没有任何风场写入、没有 `AtmosphereInterface` 调用、没有 `TargetWind`。**
- `Building_CeilingFan` 只有 `angle` 字段和 `DrawAt` 旋转绘制。

这是 VTE 与 AO 的吊扇（`CompCeilingFan` vs `AO_HouseholdFan`）最根本的差别：AO 的风扇写 `TargetWind` 与 `TargetWindBlendPerTick`，VTE 的吊扇只是一个"风扇形状的冷源"。

### 3.8 VTE_WallMountedVent（壁挂通风口）

- `thingClass = Building_Vent`，只有 `CompProperties_Flickable` + `VEF.Buildings.CompProperties_MountableOnWall`。
- 原版 `Building_Vent` 靠 `GenTemperature.EqualizeTemperaturesThroughBuilding` 做房间间温度均衡。
- **AO 的 `GenTemperature_EqualizeThroughBuilding_Patch` 在整个 AO 地图上用 Prefix 返回 false**（`return b == null || !RoomTemperatureWriteHook.OurMap(b.Map);`）。
- → **VTE 的壁挂通风口在 AO 地图上完全失效**。

### 3.9 VTE_StandingCooler（落地冷风机）

- `thingClass = Building`（不是 `Building_TempControl`），挂 `CompHeatPusherPoweredWithIdleDraw`，`heatPerSecond = −21`、`heatPushMinTemperature = 16`、待机功耗 13 W。
- **不在 AO 的自动接管范围内**（AO 只接管 `Building_Heater`/`Building_Cooler`/`Building_TempControl` 子类）。
- 它的 `PushHeat` 会被 AO 的 `GenTemperaturePushHeatPatch` 转成 `QueueLocalizedTemperatureDelta`，所以"局部吸热"仍会发生，但同样**没有风**。

---

## 第四章 数值对照

| | CCC IntakeFan | CCC AirThermal | VTE IndustrialHeater | VTE IndustrialCooler | VTE ACUnit |
|---|---|---|---|---|---|
| 电 | 150 W | 200 W | 250 W | 300 W | 100 W |
| 热功率 | — | 21 J/s ×η_thermal | +63 J/s | −63 J/s | ±21 J/s |
| 有效增益 | — | `21×3/96 ≈ 0.66 K/tick` | `63×4.17/格` | `−63×(1−T_hot/130)` | `±21×4.17` |
| 作用范围 | 8 邻格取平均 | 管网供气 | 2 格 | 2 冷 + 2 热 | 1 格 |
| 是否搬空气 | 是（风量 300） | 是（热容 900） | 否 | 否 | 否 |
| 出风 | — | — | 无 | 无 | 无 |

一个值得注意的巧合：CCC 的 `AirThermal` 在"1 进气扇 + 1 温控机"配置下，有效热功率正好是 `21 × 3 = 63`，与 VTE 工业加热器的 `63` 相同。但机制完全不同——前者作用在"网络供气温度"上，后者作用在两格房间温度上。

---

## 第五章 在 AO 地图上的实际行为矩阵

| 建筑 | AO 是否接管 | 实际行为 | 缺什么 |
|---|---|---|---|
| CCC IntakeFan | 否（无 TempControl comp） | 读 AO 格温算风量，但真空判定恒 0 | 真空桥接 |
| CCC AirThermal | 否 | 算供气温度 + 制冷排热（走 PushHeat patch） | 无 |
| CCC AirVent | 否 | `room.Temperature += a` → 整房间平移；>3000 格失效 | 风、氧、湿、质量守恒 |
| VTE WallHeater / IndustrialHeater | **是**（自动注入 `CompTempControlAtmosphere` + TickRare Prefix） | 走 AO 的 `RunHeater`，原版代码不执行 | 无 |
| VTE WallMountedCooler / IndustrialCooler | **是** | 走 AO 的 `RunCooler`，含 15 m/s 出风 + 除湿 | 无 |
| VTE ACUnit | **是** | 走 AO 的 `RunCooler` | 管网效率仍影响电费，但物理上不体现 |
| VTE ACControlUnit | 否（不是温控建筑） | 继续广播 `TargetTemperature`，AO 读这个值 | 无 |
| VTE CeilingFan | 否（HeatPusher） | 每 60 tick 在自身格吸 11/32 J/s | 完全没有风 |
| VTE WallMountedVent | 否（原版 Vent） | `EqualizeTemperaturesThroughBuilding` 被 AO 禁用 | 完全失效 |
| VTE StandingCooler | 否（HeatPusher） | 局部吸热，无风 | 风 |

**这张表就是"VTE 模块不该存在"的直接证据**：VTE 的五个温控建筑里有四个已经被 AO 自动接管，剩下两个（吊扇、通风口）恰好是 AO 最需要重新实现的。

---

## 第六章 重写时的定位建议

### 6.1 CCC 应映射到"体积输运"

CCC 的语义与 AO 的 `AddVolumeSource` / `TargetWind` 天然对应：

| CCC 环节 | AO 对应 | 需要补的量 |
|---|---|---|
| IntakeFan | 读源格的气压/温度/氧/湿 → 算 `V_x` | `V_x = η × baseAirFlow × Δt`，取 8 邻格均值 |
| 管网 | 隐藏的"体积缓冲" | 用 `SourceSlots` 做延迟，或用 tick 计数器模拟传输时间 |
| AirThermal | 对通过的体积做热交换 | `ΔT = (T_supply − T_intake)`，`Δq`、`Δp` 同理 |
| AirVent | 成对写入源/汇格 | `AddVolumeSource(V_x, O₂_supply, T_supply)` + `TargetWind` |
| 制冷排热 | 已在 PushHeat 路径上 | 保持 |
| 真空 | `TryGetStaticPressurePascals` | 替换 `VacuumUtility.GetVacuum` |

关键：CCC 的 AirVent 必须改成"成对"——源格扣、目标格加，否则质量与能量都不守恒。

### 6.2 VTE 应映射到"能量注入 + 局部扰动"

| 建筑 | 建议定位 | 需要的特性 |
|---|---|---|
| WallHeater / IndustrialHeater | 保持 AO 接管 | 无（AO 已有 `RunHeater`） |
| Cooler / ACUnit | 保持 AO 接管 | 无（AO 已有 `RunCooler` + 出风） |
| ACControlUnit | 保持广播 | 可加"管网效率 → 实际功率"的映射 |
| CeilingFan | **改成风场扰动器** | `TargetWind` 切向 + `TargetWindBlendPerTick`，按 `heatPerSecond` 的量级换算成风速 |
| WallMountedVent | **改成成对房间交换** | `CompAoRoomExchange`：`q = k(Ta−Tb)min(mA,mB)cp`，A 扣 B 加 |
| StandingCooler | **改成"冷源 + 出风"** | 局部 `TemperaturePerTick` + 朝向 `TargetWind` |

### 6.3 为什么不能共用一套公式

- CCC 的 `room.Temperature += a` 中，`a` 的大小由**风量**决定（`ExhaustAirFlow/100`），与房间大小无关。若把 CCC 映射成 `TemperaturePerTick`，大风量房间会被"一次性加很多度"，而小房间不会——这不是 CCC 的行为。
- VTE 的 `energyPerSecond × 4.1667` 是**能量**，`ControlTemperatureTempChange` 内部会按房间格数摊薄。若把 VTE 映射成体积输运，会凭空产生空气。
- 所以：**CCC → 体积/质量输运；VTE → 能量注入。** 两条通道在 AO 里都存在（`AddVolumeSource` / `TemperaturePerTick`），但不能互换。

---

## 第七章 现 patch 的错配（供重写参考）

1. **CCC 真空判定**：现 patch 改的是 `CompAirVent.speedLimit` 或直接读 `Room.Vacuum`，但 CCC 的真空判断在 `Building_IntakeFan` 里走 `VacuumUtility.GetVacuum`，与 `Room.Vacuum` 是两条路。要改必须 patch `VacuumUtility.GetVacuum`（Postfix）。
2. **CCC AirVent 写房间**：现 patch 没有拦截 `room.Temperature += a`，所以它会被 AO 的 `RoomTemperatureWriteHook` 捕获成"整房间平移"，房间大于 3000 格时静默失效。
3. **VTE 模块整体多余**：AO 已自动接管四个温控建筑。重写时应删掉 VTE 的温控部分，只保留"吊扇 + 通风口"两个真正缺失的物理能力。
4. **VTE 吊扇**：现 patch 很可能只是改了 `heatPerSecond` 或没动。正确做法是加 `TargetWind`。
5. **VTE 通风口**：`EqualizeTemperaturesThroughBuilding` 被 AO 禁用，必须换成成对交换。

---

## 附录 A 关键常量速查

| 常量 | 值 | 来源 |
|---|---|---|
| CCC `DeltaSmooth` | 96 | `CompAirFlowTempControl` |
| CCC 能量→温度 | 4.1666665 | 250/60 |
| CCC AirVent 温差上限 | 10 K | `Building_AirVent` |
| CCC AirVent 风速系数 | `0.03 × ExhaustAirFlow` | `0.25 × (flow/100) × 12` |
| CCC 制冷排热 | `(25 + min(ΔT,20)) × 1.25` | `Building_AirThermal` |
| VTE 能量→温度 | 4.1666665 / 4.16666651 | 250/60 |
| VTE 加热衰减上限 | 120 ℃（默认）/ 1000 ℃（工业） | `PushHeat` |
| VTE 冷却效率 | `1 − T_hot/130` | `PushCoolAir` |
| VTE 热侧排热 | `1.25 ×` | `PushCoolAir` |
| VTE 管网效率 | `[0, 1.5]`，赤字 −5%/单位，盈余 +1%/单位 | `AcPipeNet.CalculateEfficiency` |
| VTE 电费系数 | `2 − Efficiency` | `Building_AcUnit` |

## 附录 B 未读但可能相关的文件

- CCC：`AirFlowNetManager.cs`（网络重建/`GridID`）、`CompAirFlowTank.cs`（气罐容量）、`CompAirFlowPipe.cs`、`Building_AirPipe.cs`、`AirTypePriority.cs`
- VTE：`CompProperties_CeilingFan.cs`（`heatPerSecond`/`minSpinRate`/`maxSpinRate` 的默认值）、`CompHeatPusherPoweredWithIdleDraw.cs`（待机功耗逻辑）、`CompResourceTrader_AC.cs`（管网资源消耗）、`TemperatureOutputPositionModExtension.cs`
- 原版：`GenTemperature.ControlTemperatureTempChange` 的完整实现（决定 `energy` 的正负与房间格数的关系）
