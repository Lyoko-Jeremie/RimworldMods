# CCC 与 VTE 的 AO 化重设计：从现实原型到 AO 接口

> 本文汇总"把 CCC / VTE 放进 AO 真实物理体系"的全部结论，供完全重写补丁时作为设计依据。
>
> 配套文档：
> - [AO-CCC与VTE温度建筑分析.md](AO-CCC与VTE温度建筑分析.md) —— 逐个建筑的源码级公式与 Def 数值
> - [AO源码全景阅读报告.md](AO源码全景阅读报告.md) —— AO 本地/世界两套引擎
> - [AO兼容层重构设计.md](AO兼容层重构设计.md) —— 零 transpiler 的兼容层架构
>
> 证据来源：dotPeek 反编译源码 `f:\RimworldCodeIL\AtmosphereOverhaul\`、`f:\RimworldCodeIL\CentralizedClimateControl\`、`f:\RimworldCodeIL\VanillaTemperatureExpanded\`

---

## 第一章 结论摘要

1. **CCC 是现实里的全空气系统（All-Air）**，VTE 是冷媒直膨系统（DX/VRF）。前者用空气搬热量，后者用冷媒搬热量。两者的守恒量不同，公式不能互换。
2. **CCC 的三色管是三条完全独立的空气回路**，不是"双风管末端混合"。玩家用进风口/出风口的数量与位置自由组合，形成新风、回风、排风、闭环、正压、负压等任何拓扑。设计上**不能限制玩家组合方式**。
3. **CCC 现在缺"存量"**：`AirFlowNet` 只有瞬时流量与全局平均，没有回路质量/压力。重写必须给每条回路一个守恒的"气袋"。
4. **AO 已有全部所需的空气搬运原语**，不需要新造：
   - `RegisterAirMover(ConditionExhaust=false)` = 主动通风口（体积+动量+温度+湿+雾+氧+风机背压+限流+完全混合）
   - `RegisterAirMover(ConditionExhaust=true)` = 可调温调湿调氧的送风口
   - `AtmosphereAirMoverSettings` = 12 个字段的完整配置
   - `AirMoverTransport` = 混合/限流/风机曲线的现成工具
5. **`JetTransport` 不是通风口的实现载体**，它是 `ConditionExhaust=true` 分支用来做"喷射气柱推进"的。通风口不需要它。
6. **VTE 的四个温控建筑已被 AO 自动接管**（`FacilityCompInjection` + `TempControlSubclassPatches`），温控部分在 AO 下是死代码。VTE 真正缺的只有吊扇的风场与通风口的成对交换。
7. **两者在 AO 下会自然组合成 DOAS + VRF**：VTE 承担显热负荷，CCC 的新风回路承担新风、除湿与压力。不需要写死耦合逻辑。

---

## 第二章 现实原型：两套 HVAC 系统的工程血统

### 2.1 CCC = 全空气系统

现实里的 CAV（定风量）、VAV（变风量）、双风管/三风管系统。核心特征：

- 「空气是载热介质」
- 供冷量 `Q = ρ·V̇·c_p·ΔT`，工程式 `Q_s = 1.2 × V̇(L/s) × ΔT`
- 舒适性要求送风温差 8–12 K，所以只能「大风量、小温差」
- 能同时处理温度、湿度、新风、过滤、房间压力梯度
- 代价：风管占空间、风机能耗、漏风、末端风压不足

### 2.2 VTE = 冷媒直膨系统

现实里的分体机、VRF 多联机、风机盘管。核心特征：

- 「只有热流，没有空气质量流」
- 供冷量 `Q = ṁ_ref × Δh_evap`
- 室内机只有风机 + 盘管 + 导流片，做的是「室内空气再循环换热」
- 不引入新风（必须另配 DOAS）
- 湿度控制弱（显热比高）
- 优点：无风管、分区灵活、部分负荷效率高

### 2.3 血统差异如何解释模组里那些"奇怪"的公式

| 现象 | 现实解释 |
|---|---|
| CCC 的 `ThermalEfficiency = 热容/风量`（900/300 = 3，大于 1） | 它根本不是"效率"，是**盘管容量与风量的比值**。风量过大时空气来不及换热就穿过去了（coil bypass），现实中用 bypass factor 描述，倒过来就是这个比值。叫"效率"是误称。 |
| CCC 的 `FlowEfficiency²`（平方惩罚） | 末端风量需求超过总送风量时，风管静压不足，末端风量按平方衰减——现实里风机 `P ∝ Q²` 的直接后果 |
| CCC 的 `min(|ΔT|, 10)` 上限 | 送风温差过大时的射流不适与结露风险，被硬编码成 10 K |
| VTE 的 AC 管网传 `resource` 不传空气 | 就是**冷媒管**。`PipeSystem` 的 resource 是冷媒当量 |
| VTE 的 `AcPipeNet.Efficiency`（供/耗比，0–1.5） | 冷媒充注量/管长限制导致的能力衰减；效率低时电费 `×(2−E)` 对应压缩机功耗上升 |
| VTE 的 `1 − T_hot/130` | 冷凝温度升高 → COP 下降。现实中风冷冷凝温度 45–55 ℃ 就已经很差，130 ℃ 是夸张尺度 |
| VTE 吊扇只有 `−11 J/s` 吸热、没有风 | 现实中吊扇本来也不引入新风，只搅动室内空气。模组把它简化成了「冷源」而不是「气流组织器」 |

---

## 第三章 CCC 的正确理解：空气回路 + 气袋

### 3.1 三色管 = 三条独立回路

CCC 的 `AirFlowNet` 每条管网是一个**独立的、守恒的空气回路**：

```
进风口（从所在格取气）→ 回路气袋 → 温控机（对回路空气加热/制冷）→ 出风口（注入所在格）
```

三色管（`redAirPipe` / `blueAirPipe` / `cyanAirPipe`）只是三条互不连通的回路，`AirFlowType` 为 `Hot/Cold/Frozen`。玩家用进风口/出风口的数量与位置自由组合出任何拓扑。

**这推翻了"三风管末端混合"的理解**：不是末端按比例混合，而是玩家自己用多个进/出风口搭出送风、回风、排风、闭环、正压、负压。

### 3.2 现有实现的缺口：只有流量，没有存量

```csharp
// AirFlowNet.cs 每个 tick
CurrentIntakeAir         = Σ Producer.CurrentAirFlow          // 瞬时流量
AverageIntakeTemperature = Σ Producer.IntakeTemperature / n    // 全局算术平均
ThermalCapacity          = Σ TempControl.ThermalCapacity
AverageConvertedTemperature = Σ TempControl.ConvertedTemperature / n

ThermalEfficiency = CurrentIntakeAir <= 0 ? 0 : ThermalCapacity / CurrentIntakeAir
FlowEfficiency    = CurrentExhaustAir > 0 ? min(1, CurrentIntakeAir / CurrentExhaustAir) : 0
```

问题：

- `CurrentIntakeAir` 是"这一 tick 流进来多少"，不是"回路里现在有多少空气"。
- 算术平均抹掉了所有拓扑信息——10 台进气扇分布在基地两端，与 1 台在机组旁边，对网络完全等价。
- 没有回路压力，所以无法表达"进得多、出得少 → 回路升压"。

### 3.3 重写模型：回路气袋

CCC 自己持有一个回路状态（不放进 AO）：

```csharp
class AirLoop {
    float volumeM3;       // 管路容积，由管道格数决定（每格 5 m³）
    float massKg;         // 回路里的空气质量
    float temperature;    // 回路空气温度
    float vapor;          // 含湿量
    float oxygenPercent;  // 氧浓度
    float pressurePa;     // 回路压力
}
```

每 tick 的三段：

**进风口**

```csharp
read(cell, out T_c, out q_c, out O2_c, out p_c);

// 风机背压：回路压力高于该格时流量衰减
float dp = loop.pressurePa - p_c;
float V_in = AirMoverTransport.Flow(fanFlowM3PerSecond, dp, fanMaxRisePa) * dt;
float cellVol = V_in / 5f;                    // 转成格体积

// 从该格抽气（负体积，不改成分——物理上抽走同成分空气）
AddVolumeSource(map, cell, -cellVol, 0f, 0f);

// 混入回路气袋（完全混合）
float f = AirMoverTransport.MixingFraction(cellVol);   // 1 - e^(-V)
loop.temperature   = Mix(loop.temperature, T_c, f);
loop.vapor         = Mix(loop.vapor, q_c, f);
loop.oxygenPercent = Mix(loop.oxygenPercent, O2_c, f);
loop.massKg       += Density(T_c, p_c) * V_in;
```

**温控机**

```csharp
float Q = capacity * (targetTemperature - loop.temperature) * efficiency;   // W
loop.temperature += Q / (loop.massKg * cp);                                 // 回路升温
// 环境侧反向：制冷时向设备周围格排热，加热时从周围格吸热——成对守恒
AddTemperatureDelta(deviceCell, -Q / (Density(envT, envP) * 5f * cp) * dt);
```

现有 CCC 的 `GenerateDelta` 只改 `DeltaTemperature`，然后靠 `PushHeat` 单向排热，两边不对账。重写必须成对。

**出风口**

```csharp
float V_out = min(loop.massKg / Density(loop.temperature, loop.pressurePa), fanFlowM3PerSecond * dt);
float cellVol = V_out / 5f;

// 注入该格：体积
AddVolumeSource(map, cell, +cellVol, loop.oxygenPercent, 0f);
// 温度与湿度用 delta 精确混合
float f = AirMoverTransport.MixingFraction(cellVol);
AddTemperatureDelta(cell, (loop.temperature - T_c) * f);
AddMoistureDelta(cell,   (loop.vapor - q_c) * f);

loop.massKg -= Density(loop.temperature, loop.pressurePa) * V_out;
```

**回路压力（自然涌现）**

```csharp
loop.pressurePa = loop.massKg * R * (loop.temperature + 273.15f) / (loop.volumeM3 * M_air);
```

进 > 出 → 质量增加 → 压力升高 → 下一 tick `Flow()` 自动限流；出 > 进 → 质量减少 → 压力下降 → 进风口更容易进气。**正压/负压不需要特殊代码。**

### 3.4 玩家自由组合的映射

| 玩家组合 | 回路行为 |
|---|---|
| 进风口在室外 + 出风口在室内 | 全新风（氧 21%、湿按室外） |
| 进风口在室内 + 出风口在室内 | 全回风（成分不变，只调温） |
| 进风口在室内 + 出风口在室外 | 排风（室内被抽成负压） |
| 进风口多、出风口少 | 回路升压 → 出风口风速升高 |
| 进风口少、出风口多 | 回路抽空 → 进风口风量下降 |
| 三条回路各自布置 | 分区送风，互不影响 |
| 两条回路在同一房间进出 | 室内循环 + 独立新风，等价于 DOAS |

**没有任何一种组合需要特判。**

---

## 第四章 三个技术点：已在 AO 源码中确认

### 4.1 `AddAirMoverVolumeRate` 不搬标量

`LocalAtmosphereEngine.cs:801-805`：

```csharp
internal void AddAirMoverVolumeRate(int inlet, int outlet, float rate)
{
    this.externalDivergence[inlet]  -= rate / this.ObstacleMask[inlet];
    this.externalDivergence[outlet] += rate / this.ObstacleMask[outlet];
}
```

只往 `externalDivergence` 写一对散度源，让压力求解器推动空气。**温度、氧、湿完全不跟着走**，必须由调用方自己搬。

### 4.2 `cellVolumes` 是格体积，1 单位 = 5 m³

`1GasPump.cs:226`：

```csharp
AtmosphereInterface.AddVolumeSourceAtTemperature(map, position,
    (float)(num15 * 101.32499694824219 / Mathf.Max(1f, kPa) / 5.0), ...);
```

先按气压换算、再除以 5。`5GasTanks.cs:60` 同样 `/5.0`。所以 AO 的"一格空气"就是 5 m³。

### 4.3 抽气原语存在：`AddVolumeSource` 传负值

`AtmosphereInterface.cs:197-206`：

```csharp
if (!IsFinite(cellVolumes) || cellVolumes == 0) return false;
c.SubmitPointVolume(index, cellVolumes);
if (cellVolumes > 0)
    c.MixReleasedGas(index, cellVolumes, oxygenPercent, temperatureOffsetCelsius);
```

负值只写散度/压力，不碰成分。物理上正确：**抽走的是该格本身的空气，浓度不变**。

### 4.4 额外发现：整套工具函数已存在

`AirMoverTransport.cs`：

```csharp
MixingFraction(v) = 1 − exp(−v)                    // 完全混合的剩余比例
Mix(receiver, donor, f) = receiver + (donor−receiver)·f
LimitedVolumes(req, inDemand, outDemand) = req · min(1, 0.5/max(inDem, outDem))   // 每子步最多半格
Flow(req, outletMinusInletPa, maxRisePa) = req · max(0, 1 − max(0, Δp)/maxRisePa)  // 风机背压曲线
LimitedGasAmount(req, donor, receiver, capacity)
ActiveTicks(lastTick, tick, validUntil)
```

`MixingFraction = 1 − e^{−V}` 是标准的"换气 V 倍后剩余比例"。`Flow` 就是风机的 P-Q 特性曲线。

---

## 第五章 主动通风口：用 `RegisterAirMover`，不用 `JetTransport`

### 5.1 配置结构

`AtmosphereAirMover.cs:13-52` 的 `AtmosphereAirMoverSettings`：

```csharp
public float CubicMetresPerTick;        // 每 tick 风量
public float AirColumnHeightMetres;     // 气柱高度（换算用，必须 > 0）
public Vector2 InletDirection;          // 入口方向
public Vector2 OutletDirection;         // 出口方向
public float MaxPressureRisePascals;    // 风机压头
public bool ConditionExhaust;           // false = 搬运；true = 喷射
public float ExhaustSpeed;              // 喷射速度
public float ExhaustTemperatureFloor;   // 排气温度下限
public float ExhaustPressureRatio;      // 排气压力比
public float InletPressureRatio;        // 入口压力比
public float ExhaustOxygenPercent;      // 排气氧浓度
public float WaterKgPerTick;            // 每 tick 注水量
```

`ConditionExhaust` 是分水岭。

### 5.2 `ConditionExhaust = false`：这就是主动通风口

`AtmosphereMapComponent.cs:472-552` 的 `ApplyAirMoversUnsafe`：

```csharp
// 1. 风量 = 风机曲线 × 时间 × 高度换算，带 1000 上限
float amount = Mathf.Min(1000f,
    AirMoverTransport.Flow(settings.CubicMetresPerTick,
        (engine.Pressure.Snapshot[outlet] - engine.Pressure.Snapshot[inlet]) * 1000.0,   // Δp → Pa
        settings.MaxPressureRisePascals)
    * (elapsedTicks * stepFraction / settings.AirColumnHeightMetres));

// 2. 限流
float exchanged = AirMoverTransport.LimitedVolumes(amount, demand[inlet], demand[outlet]);

// 3. 体积：成对散度
engine.AddAirMoverVolumeRate(inlet, outlet, exchanged / dt / pressureAccelerationScale);

// 4. 动量：两端各施加
AddAirMoverImpulse(impulses, inlet,  engine.AirMoverPortImpulse(inlet,  settings.InletDirection,  speed, blend));
AddAirMoverImpulse(impulses, outlet, engine.AirMoverPortImpulse(outlet, settings.OutletDirection, speed, blend));

// 5. 成分：按体积加权收集 inlet 的温度/湿/雾/氧
outletMixture.Temperature += exchanged * engine.Temperature.Read[inlet];
outletMixture.Moisture    += exchanged * engine.Moisture.Read[inlet];
outletMixture.Mist        += exchanged * engine.Mist.Read[inlet];
outletMixture.Oxygen      += exchanged * (float)engine.Oxygen.Read[inlet];

// 6. 完全混合到 outlet
float fraction = AirMoverTransport.MixingFraction(outletMixture.Volumes);
engine.Temperature.Read[key] = Mix(current, outletMixture.Temperature / Volumes, fraction);
engine.Moisture.Read[key]    = Mix(current, outletMixture.Moisture / Volumes, fraction);
engine.Mist.Read[key]        = Mix(current, outletMixture.Mist / Volumes, fraction);
engine.Oxygen.Read[key]      = Mix(current, outletMixture.Oxygen / Volumes, fraction);
engine.RelativeHumidity.Read[key] = clamp01(Moisture / VaporCapacity(T));
```

它自带：风机背压曲线、限流、指数混合、氧的 byte 余数累积、连通性检查。

### 5.3 `ConditionExhaust = true`：可调温调湿调氧的送风口

`AtmosphereMapComponent.cs:609-694`：

```csharp
JetTransport.Source source1 = new JetTransport.Source() {
    Inlet = inlet, Outlet = outlet,
    Direction = settings.OutletDirection.normalized,
    Volumes   = Mathf.Min(1000f, CubicMetresPerTick * elapsed / AirColumnHeightMetres),
    Speed     = Mathf.Min(stepSettings.MaxWindSpeed, settings.ExhaustSpeed),
    Temperature = Mathf.Max(ambientOrNeighborT, settings.ExhaustTemperatureFloor),   // 可控温度
    Oxygen      = settings.ExhaustOxygenPercent,                                     // 可控氧
    PressureHead = num7 / SolverPascalsPerUnit(stepSettings),
    Water = WaterKgPerTick * elapsed * 461.5 * (T + 273.15) / (100 * AirColumnHeightMetres)   // 可控加湿
};
```

**这条分支不看 inlet 的温度**：出口温度 = `max(环境或邻格温度, ExhaustTemperatureFloor)`。它是"注入设定状态的气体"，不是"搬运"。

### 5.4 `JetTransport` 的定位

只在上面这条 `ConditionExhaust = true` 分支里被用。职责：

1. 沿 `OutletDirection` 建一条最多 128 格的路径（`BuildPath`）
2. 分子步（`LastSubsteps = clamp(ceil(max(speed·dt·2, demand·2)), 1, 512)`）
3. 沿路径逐格 `Exchange`，模拟气柱推进
4. 最后 `AddAirMoverVolumeRate`

**它是"喷射"的实现载体，不是"通风口"的实现载体。**

### 5.5 分工表

| 需求 | 路径 | 关键参数 | 成分来源 |
|---|---|---|---|
| 墙上通风口交换 | `RegisterAirMover` + `ConditionExhaust=false` | `CubicMetresPerTick`、`MaxPressureRisePascals`、方向 | 从 inlet 格读温度/湿/雾/氧，混合到 outlet |
| 送风（调温调湿调氧） | `RegisterAirMover` + `ConditionExhaust=true` | `ExhaustSpeed`、`ExhaustTemperatureFloor`、`ExhaustOxygenPercent`、`WaterKgPerTick` | 温度/氧/水由参数指定，不看 inlet |
| 长距离风管 | 参照 `JetTransport` 的路径逻辑 | `Volumes`、`Speed`、`Direction`、`Path` | 沿路径逐格 `Exchange` |
| 高温涡喷 | `ConditionExhaust=true` + `JetTransport` | 全套 | 燃烧后的高温气体参数 |

### 5.6 五个必须注意的坑

1. **`IsAirMoverPortOpen(cell)` 必须为真**——格是空气格（`ObstacleMask > 0`）且四个面开度之和 > 0。墙上通风口要把所在墙格的 `FaceOpen` 打开。（`LocalAtmosphereEngine.cs:792-799`）
2. **`AirMoverHasReturnPath(inlet, outlet)` 必须为真**——两端必须有回流路径，否则整台设备不工作（`ApplyAirMoversUnsafe:485` 的前置条件）。这防的是"只吹不回"造成压力无限堆积。
3. **单位是 m³/tick，不是格体积**。`Volumes = CubicMetresPerTick × elapsed / AirColumnHeightMetres`，内部还会再除以 `TimeStep` 和 `PressureAccelerationScale` 变成散度。
4. **`AirColumnHeightMetres` 必须 > 0**，否则 `Valid` 返回 false，设备被静默移除。
5. **`ApplyAirMoversUnsafe` 在 `AdvanceProvisional` 之后、`ProjectAndFinalize` 之前执行**——它写的散度会进入当步的压力求解，时序正确。

### 5.7 不建议自己实现的原因

| 自己实现要写 | `RegisterAirMover` 已有 |
|---|---|
| 体积成对散度 | `AddAirMoverVolumeRate` |
| 两端动量冲量 | `AirMoverPortImpulse` + `AddAirMoverImpulse` |
| 成分按体积加权 + 指数混合 | `OutletMixture` + `MixingFraction` |
| 风机背压曲线 | `AirMoverTransport.Flow` |
| 子步限流 | `LimitedVolumes` |
| 氧的 byte 余数累积 | `airMoverOxygenRemainder` |
| 连通性检查 | `IsAirMoverPortOpen` / `AirMoverHasReturnPath` |

唯一值得从 `JetTransport` 借的，是**长距离风管的路径逐格交换**。通风口本身是一格对一格，不需要。

---

## 第六章 VTE 的 AO 化模型

### 6.1 VTE 需要的是能量注入，不是体积输运

```csharp
// 室内机：局部换热，不改变房间质量
T_air = read(cell)
Q = min(capacity, hA * (T_air - T_evap)) * partLoad
AddTemperatureDelta(cell, -Q / (ρ·V_cell·c_p))

// 除湿：低温盘管的副产物
q_sat_evap = q_sat(T_evap)
Δq = min(max(0, q_air - q_sat_evap), Q_latent / (ρ·V_cell·L))
AddMoistureDelta(cell, -Δq)
water += Δq · ρ · V_cell / HpaCellPerKg(T_air)

// 冷凝热：必须排到室外
Q_cond = Q * (1 + 1/COP)
AddTemperatureDelta(outdoorCell, Q_cond / (ρ·V_cell·c_p))

// 气流：导流片
TargetWind = supplyDir * (fanFlow / ventArea)
TargetWindBlendPerTick = 0.1
```

**注意没有 `AddVolumeSource`**。房间的质量、压力、氧浓度都不变。这正是 VTE 在 AO 下"自动接管反而更正确"的原因。

### 6.2 吊扇：气流组织器，不是冷源

```csharp
TargetWind = tangent(-radial) * fanSpeed       // 切向旋转
TargetWindBlendPerTick = 0.05
```

配合 AO 的 `FeelsLikeTemperature`，风会降低体感温度——这才是吊扇的真实作用（空气流动加速汗液蒸发），而不是"吸热"。

### 6.3 通风口：成对房间交换

```csharp
q = k * (Ta - Tb) * min(mA, mB) * cp
AddTemperatureDelta(aCell, -q / (mA * cp))
AddTemperatureDelta(bCell, +q / (mB * cp))
// 湿度同构成对
```

不能用原版 `EqualizeTemperaturesThroughBuilding`——AO 在地图上把它 Prefix 禁用了。

### 6.4 冷媒管网：保留，语义重定义

- `Production` = 压缩机能力
- `Consumption` = 各室内机的冷媒需求
- `Efficiency` = 冷媒输送效率（管长、充注量、部分负荷）
- 电费 `×(2 − E)` 保留

`AcControlUnit` 的"目标温度广播"就有意义了：它是"系统设定点"，所有室内机跟随（除非独立）。

### 6.5 VTE 的致命缺陷：无新风

现实里 VRF 必须配 DOAS。在 AO 里这个缺陷会自然暴露：VTE 房间的氧会被消耗、湿度会累积、压力会失衡。

**这不是 bug，是真实特性。** 玩家需要配一台"新风机组"——那正好是 CCC 的 IntakeFan 或 AO 的通风。

---

## 第七章 最重要的洞察：DOAS + VRF 的互补

原版里 CCC 和 VTE 是**互相替代**的两套温控方案。在 AO 的真实物理下，它们会变成现实里的标准组合：

| | CCC（DOAS 新风回路） | VTE（VRF 室内机） |
|---|---|---|
| 承担负荷 | 新风 + 全部潜热（除湿） + 压力 | 显热负荷 |
| 风量 | 小（只需满足新风量与正压） | 无（室内循环） |
| 氧/湿度 | 提供 | 不提供 |
| 能效 | 风机能耗 | COP 高 |

**这个组合会在 AO 的物理下自然涌现，不需要写死耦合逻辑**：VTE 处理显热，CCC 以最小风量提供新风与除湿，双方都不越界。

---

## 第八章 实现路径

### 8.1 MVP（1–2 周）

**CCC**：

1. 给每条 `AirFlowNet` 加气袋状态（`volumeM3`/`massKg`/`temperature`/`vapor`/`oxygenPercent`/`pressurePa`）
2. IntakeFan：读格 → `Flow()` 背压 → 负 `AddVolumeSource` + 混合进气袋
3. AirThermal：对气袋换热 + 环境侧成对 `AddTemperatureDelta`
4. AirVent：`AddVolumeSource(+V)` + `AddTemperatureDelta` + `AddMoistureDelta` + `AddOxygenPercentDelta`
5. 保留三色管为三条独立回路
6. 不做：风管阻力曲线、末端诱导、VAV、压力控制

**VTE**：

1. ACUnit：`AddTemperatureDelta` + `AddMoistureDelta` + `TargetWind` + 室外机排热
2. 吊扇：`TargetWind` 切向 + 混合增强
3. 通风口：成对房间交换（`CompAoRoomExchange`）
4. 保留 `AcPipeNet` 的效率语义

### 8.2 完整版（额外）

- CCC：风管阻力曲线、末端诱导比、VAV 变风量、房间压力控制、热回收交叉换热
- VTE：冷媒流量分配、蒸发/冷凝温度耦合、部分负荷曲线
- 两者：DOAS + VRF 的自动协同（VTE 检测到新风回路时自动切换显热模式）

### 8.3 待定的设计参数

- 回路容积 `volumeM3`：按管道格数 × 5 m³，还是给一个可配置常数？前者更真实，后者更简单。
- 风量标定：CCC 的 `baseAirFlow=300` 单位不明。建议以"换气次数 ACH"为锚点反推（住宅 0.5–1 ACH、办公室 4–6 ACH、医院病房 6 ACH、手术室 15–25 ACH）。
- `AirColumnHeightMetres`：建议取 3 m（RimWorld 房间层高）。

---

## 第九章 风险与边界

1. **压力读写不对称**：读压力必须用 `TryGetStaticPressurePascals`（internal，反射）或 `TryGetSample` + `SolverPascalsPerUnit` 自算。`TryGetPressurePascals` 在可压缩模式下会放大 83 倍。
2. **可压缩模式默认开启**，且 `ScaleForInterval` 强制 `EnableTemporalPatches = 0`。不能用 `AddPressureDelta` 维持压力（`ApplyThermodynamicPressureBaseline` 每步重算 `Pressure.Read`）。
3. **`ApplyDivergenceDeltas` 是覆盖写不是累加**——同一步内多次调用只有最后一次生效。
4. **`AddDeltas` 同一步多次调用会叠加过冲**——建筑的 `AtmosphereSourceRates` 走 `RegisterContinuousSource`，按 tick 差量积分，不会过冲。
5. **房间 > 3000 格时 `RoomTemperatureWriteHook` 静默忽略**——CCC 的 `room.Temperature += a` 在大房间会失效。
6. **`AddVolumeSource` 会让压力升高**——CCC 的送风口会升压，回风口要成对抽气，否则房间会被越吹越胀。
7. **`IsAirMoverPortOpen` / `AirMoverHasReturnPath` 是硬前置**——风道被堵、两端无回流路径时设备静默不工作。

---

## 附录 A API 速查

| API | 语义 | 单位 |
|---|---|---|
| `RegisterAirMover(map, owner, key, inlet, outlet, settings, activeTicks)` | 注册空气搬运器 | — |
| `UpdateAirMover(...)` / `UnregisterAirMover(s)` | 更新/注销 | — |
| `AddVolumeSource(map, cell, cellVolumes, oxygenPercent, temperatureOffsetCelsius)` | 体积源（负值=抽气） | 格体积（1 = 5 m³） |
| `AddVolumeSourceAtTemperature(map, cell, cellVolumes, oxygenPercent, gasCelsius)` | 体积源（绝对温度） | 同上 |
| `AddTemperatureDelta(map, cell, ΔC)` | 温度增量 | ℃ |
| `AddMoistureDelta(map, cell, Δq)` | 绝对水汽增量 | hPa·cell 量级 |
| `AddMistDelta(map, cell, Δm)` | 雾滴增量 | 同上 |
| `AddOxygenPercentDelta(map, cell, Δ%)` | 氧体积分数增量 | 百分点 |
| `AddPressureDelta(map, cell, Δsolver)` | 压力增量 | solver 单位（1 ≈ 1000 Pa） |
| `AddWindImpulse(map, cell, Δv)` | 风冲量 | m/s |
| `AddDeltas(map, cell|CellRect, AtmosphereDeltas)` | 组合增量 | 各字段自带单位 |
| `TryGetTemperature` / `TryGetStaticPressurePascals` / `TryGetAbsoluteVapor` / `TryGetOxygenPercent` / `TryGetWind` | 读 | — |

## 附录 B 关键行号索引

| 文件 | 行 | 内容 |
|---|---|---|
| `Simulation/LocalAtmosphereEngine.cs` | 801-805 | `AddAirMoverVolumeRate` 只写散度 |
| `Simulation/LocalAtmosphereEngine.cs` | 792-799 | `IsAirMoverPortOpen` |
| `Simulation/LocalAtmosphereEngine.cs` | 835-866 | `AddPointVolume` 三种组件分支 |
| `Simulation/AirMoverTransport.cs` | 全文 | `Mix`/`MixingFraction`/`LimitedVolumes`/`Flow` |
| `Simulation/JetTransport.cs` | 23-106 | `Advance` 完整搬运流程 |
| `RimWorld/AtmosphereAirMover.cs` | 13-52 | `AtmosphereAirMoverSettings` 12 字段 |
| `RimWorld/AtmosphereMapComponent.cs` | 472-552 | `ApplyAirMoversUnsafe`（搬运分支） |
| `RimWorld/AtmosphereMapComponent.cs` | 609-694 | `ApplyJetTransportUnsafe`（喷射分支） |
| `RimWorld/AtmosphereInterface.cs` | 190-226 | `AddVolumeSource` / `AddVolumeSourceAtTemperature` |
| `Buildings/1GasPump.cs` | 226 | `cellVolumes` 单位证据（/5.0） |
| `CentralizedClimateControl/AirFlowNet.cs` | 全文 | CCC 网络汇总 |
| `CentralizedClimateControl/CompAirFlowTempControl.cs` | 全文 | CCC 96 tick 比例控制 |
| `CentralizedClimateControl/Building_AirVent.cs` | 全文 | CCC `room.Temperature += a` |
| `VanillaTemperatureExpanded/Buildings/Building_HeaterWithOffset.cs` | 37-59 | VTE 加热公式 |
| `VanillaTemperatureExpanded/Buildings/Building_TwoCellCooler.cs` | 34-57 | VTE 冷却公式 |
| `VanillaTemperatureExpanded/Buildings/Building_AcUnit.cs` | 29-51 | VTE 双向 ACUnit |
| `VanillaTemperatureExpanded/AcPipeNet.cs` | 128-141 | VTE 管网效率 |
| `VanillaTemperatureExpanded/Comps/CompCeilingFan.cs` | 21-30 | VTE 吊扇无风 |
