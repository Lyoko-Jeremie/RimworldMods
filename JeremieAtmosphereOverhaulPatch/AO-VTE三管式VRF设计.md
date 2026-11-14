# VTE 三管式热回收 VRF：AO 化设计

> 本文把 VTE 中央空调从「每台室内机各自和房间换热、冷凝热凭空消失」的单机模型，重构成「蒸发侧 / 冷凝侧两相 + 网内热回收 + 室外机兜底」的三管式 VRF 模型，并给出落到 AO 格级物理上的完整接口设计。
>
> 本版的核心修正：**效率只由「室外机容量 / 净不平衡量」决定**，热回收量不占用室外机容量；**电费由室内机需求热负荷与室外机实际承担量分别计费**；**不再有管网输送损失**。
>
> 配套文档：
> - [AO-CCC与VTE重设计.md](AO-CCC与VTE重设计.md) —— CCC/VTE 的现实原型与 AO 化总体方案
> - [AO-CCC与VTE温度建筑分析.md](AO-CCC与VTE温度建筑分析.md) —— 逐建筑源码级公式与 Def 数值
> - [AO-四Mod温度建筑组件清单.md](AO-四Mod温度建筑组件清单.md) —— VTE 8 个 ACNet 机组与管路的定位
> - [AO源码全景阅读报告.md](AO源码全景阅读报告.md) —— AO 本地/世界两套引擎
>
> 证据来源：反编译源码 `f:\RimworldCodeIL\AtmosphereOverhaul\`（MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、`f:\RimworldCodeIL\VanillaTemperatureExpanded\`（MVID `37137F71-AA22-483B-B498-A2571293C2DD`）、PipeSystem 1.6 `f:\SteamLibrary\steamapps\workshop\content\294100\2023507013\1.6\Assemblies\PipeSystem.dll`（MVID `2b9fb81324e246b9aaa67e03ce1e0675`）、原版 `Assembly-CSharp`（`Source/Verse/GenTemperature.cs:315`）。

---

## 第一章 结论摘要

1. **VTE 的原模型是单机直膨**：每台 `Building_AcUnit` 每 250 tick 计算一次 `energyPerSecond × 250/60` 的房间级能量，除以房间格数后写 `room.Temperature`；`AcPipeNet.Efficiency` 只乘在电费 `(2 - E)` 上，**不参与任何传热**。
2. **VTE 的网络在物理上是一条总线**：一个 `EfficiencyNet`，一个 `Efficiency` 标量，没有高压气管 / 低压气管 / 液管之分。三管式是我们在**网络账本层**虚拟出来的结构。
3. **三管式要补的只有一件事**：让正在制冷的室内机排出的冷凝热，优先抵消正在制热的室内机的蒸发热，只有净差 `Q_net` 才与室外交换。
4. **效率的唯一定义**：`s = min(1, C / Q_net)`。热回收量 `Q_rec` 不占用室外机容量，所以完全平衡时 `s = 1`，与室外机状态无关；完全不平衡时 `s` 就是「室外机容量 / 需要承担的量」。
5. **电费按负荷分别计**：室内机按需求热负荷 `|Q_i|` 计费（与 `s` 无关，开了就耗电）；室外机按实际承担比例 `min(1, Q_net/C)` 计费（关了就完全不耗电）。
6. **没有管网输送损失**：删除了管长、阀门、`η_pipe` 等参数，`Efficiency` 字段现在只表示系统满足率。
7. **无压缩机时强制关断并提示**：`C = 0` → 全部室内机 `ResourceOn = false`、`Efficiency = 0`、显示「缺少压缩机」。
8. **室外机是单值的**：每个账本周期只处于「冷凝器（排热）」「蒸发器（吸热）」「不换热」三种状态之一，不会同时吸热和放热。
9. **接管方式**：在 `Building_AcUnit.TickRare` 上挂 priority 高于 AO 的 Prefix 并返回 false，完全替代原方法与 AO 的 `Run`；账本放在 AO 模拟步钩子里，每 250 tick 一次。

---

## 第二章 源码事实：建模的硬约束

### 2.1 AO 侧

| 事实 | 位置 | 对模型的影响 |
|---|---|---|
| `RoomTempTracker.get_Temperature` 被 Prefix 替换为 `GetAverageAuthoritativeTemperature(room)`，返回 false | `Patches/4TemperaturePatches.cs:37` | 房间温度只是统计量，不能作为物理状态 |
| `RoomTempTracker.set_Temperature` Prefix 调 `OnSet`，`suppressDepth > 0` 时直接 return | `Patches/5TemperaturePatches.cs:16`、`Patches/RoomTemperatureWriteHook.cs:28` | AO 的 `TickRare` Prefix 用 `Enter()` 抑制原版房间写入 |
| `ApplyRoomTemperatureWrite(room, value, maxDelta)` 把 `value - 平均` 逐格加到 `room.Cells`，clamp ±200 | `RimWorld/AtmosphereMapComponent.cs:2901` | 房间级写入被翻译成「每格 +ΔK」，与原版 `ControlTemperatureTempChange` 的返回值语义一致，**不会放大能量** |
| `EnqueueTemperatureExternalDelta` 只累加到 `incomingTemperatureDeltas`，下个模拟步应用 | `RimWorld/AtmosphereMapComponent.cs:1839` | `AddTemperatureDelta` 是延迟生效的 |
| `FlushContinuousIntervalToQueues` 里 `Δ = currentTick - lastAppliedTick`，`TemperaturePerTick × Δ` 入队 | `RimWorld/AtmosphereMapComponent.cs:2622`、`:2631` | 持续源速率是 per-tick，按时间精确积分 |
| `DefaultSimulationIntervalTicks = 30`，`TimeStep = 1f` | `Simulation/LocalAtmosphereSettings.cs:12`、`:286` | 250 tick 的账本周期会被切成约 8 个模拟步 |
| `LocalAtmosphereEngine.AddTemperatureDelta(index, delta)` = `SetTemperature(T + delta)` | `Simulation/LocalAtmosphereEngine.cs:3401` | 无热容，K 即能量代理 |
| `QueueLocalizedTemperatureDelta(origin, room, delta)` 把 `delta` 均分到中心 + 四邻十字 5 格 | `RimWorld/AtmosphereMapComponent.cs:2843` | `PushHeat(cell, energy)` 的 `energy` 是**总量 K·格**，内部再除格数 |
| `PushHeat(IntVec3 cell, float energy)` 走 `QueueLocalizedTemperatureDelta` | `RimWorld/AtmosphereMapComponent.cs:2931` | 适合室外机排热/吸热 |
| `AtmosphereSourceRates` 有温度 / 地温 / 湿度 / 雾 / 风 / 压力 / 氧 8 组字段 | `RimWorld/AtmosphereSourceRates.cs:12` | AO 原生支持全部物理量，宿主桥只映射 5 个是宿主限制 |
| `AtmosphereThermodynamicKernel.Advance` 里蒸发吸热、凝结放热，受 `MaxLatentHeatTemperatureDeltaCelsius = 4` 限幅 | `Simulation/AtmosphereThermodynamicKernel.cs:19` | 除湿必然放热，潜热要并入冷凝负荷 |

### 2.2 VTE 侧

| 事实 | 位置 | 对模型的影响 |
|---|---|---|
| `Building_AcUnit.TickRare`：`if (!PowerOn \|\| !ResourceOn) return;` → 出风格 `Position + RotatedBy(outputOffsetNorth, Rotation)` → `num = energyPerSecond × 4.16666651f × sign(Target - T)` → `T > 120 → num = 0` → `ControlTemperatureTempChange` → `GetRoom(cell).Temperature += a` | `Buildings/Building_AcUnit.cs` | 双向热泵；每 250 tick 一次；`4.16666651 = 250/60` |
| `Building_AcUnit.TickRare` **不调 base** | 同上 | AO 注入的 `CompTempControlAtmosphere.CompTickRare` 在 ACUnit 上永不执行 |
| `VTE_ACUnitWall` 的 `thingClass` 也是 `Building_AcUnit` | `1.6/Defs/NetDefs/ACNet.xml:345` | 两者共用同一处 Harmony patch，只靠 Def 的 `offsetNorth` / `isAttachment` 区分 |
| `Building_AcControlUnit.TargetNetworkTemperature = 21f`（Scribe），gizmo 步进 -10/-1/重置/+1/+10，clamp -273.15..1000；改温时直接写 `comp.targetTemperature` 并广播给 receivers | `Buildings/Building_AcControlUnit.cs` | 控制器只提供目标值，不参与物理 |
| `CompAcTempControl.IndependentTemp`：为 true 时不接受网络目标 | `Comps/CompAcTempControl.cs` | 允许单机脱网 |
| `AcPipeNet.CalculateEfficiency`：`E = Production==0 ? 0 : (diff<0 ? max(0, 1-0.05|diff|) : min(1.5, 1+0.01|diff|))`，只在 `flag1`（dirty/开关）时重算 | `AcPipeNet.cs` | 原 `Efficiency` 不是连续量；本设计不再依赖它 |
| `AcPipeNet.PipeSystemTick` **不调 `base.PipeSystemTick()`** | 同上 | PipeSystem 的随机开关 / 存储 / 溢出逻辑被完全跳过 |
| `Count(ControllerList, ...) == 1` 才开门控 | 同上 | 改为 `>= 1`；多控制器不区分 |
| `Efficiency == 0` 或 controller 数 != 1 → 全部 `ResourceOn = false` | 同上 | 会和新账本抢 `ResourceOn`，必须明确分工 |
| `CompResourceTrader.Consumption`：`idle < 0 → BaseConsumption`；否则 `UsedLastTick ? Base : idle` | PipeSystem `CompResourceTrader` | 生产者的 `idle` 被忽略 |
| VTE 全程序集不调用 `Notify_UsedThisTick` | grep 确认 | `UsedLastTick` 恒 false；Def 里 `idle == consumption`，所以「维持值」在 VTE 中行为上是死的 |
| `CompResourceTrader_Compressor.CanBeOn() => base.CanBeOn() && !RoofUtility.IsAnyCellUnderRoof(parent)` | `CompResourceTrader_Compressor.cs` | 压缩机必须露天；被覆盖的不计入容量 |
| `CompResourceTrader_AC` / `CompResourceSingleton` / `CompResourceTrader_Compressor` 都只做 overlay/inspect，不 override `Consumption` | 同上 | 「负荷」就是开通台数 × 1，与温差无关；本设计不用它做热量 |

### 2.3 由此推出的设计约束

1. **账本周期 250 tick**（与 `TickRare` 对齐）。
2. **能量单位统一为 K·格 / 250 tick**。换算：`Q_full = |energyPerSecond| × 250/60`，与原版单机满额注入总量一致。
3. **落到 AO 时用持续源**（`RegisterContinuousSource`），让 AO 按 Δticks 积分，避免 250 tick 一次脉冲。
4. **房间是读写适配层，格是物理层**：所有设备级联最终都要落到具体格。
5. **潜热必须计入负荷**：除湿产生的 `num1 × latent` 是真实的热，不能忽略；本设计计入蒸发侧 `b`（见 3.3 与 5.4），AO 原实现是把它加到热端。
6. **`ResourceOn` 只作输入**（玩家开关 / 故障 / 屋顶覆盖），不作负荷分配输出。

---

## 第三章 三管式物理模型

### 3.1 VTE 原模型

```
每台室内机 i：
    T_c = 出风格温度
    T_t = 网络目标温度
    Q_i = |eps_i| × 250/60 × clamp01(|T_t - T_c| / 1K) × sign(T_t - T_c)
    room_i.Temperature += Q_i / cellCount(room_i)
```

- `Q_i > 0` 制热，`Q_i < 0` 制冷。
- 每台独立和房间换热，**彼此不交换能量**。
- 冷凝热（制冷时）和蒸发热（制热时）都直接从系统外凭空出现/消失，没有室外机。
- `Efficiency` 只乘电费。

### 3.2 三管式要补的结构

现实里的三管式 VRF 有三根冷媒管：

| 管 | 角色 | 对应本模型 |
|---|---|---|
| 高压气管 | 压缩机排气 → 室内机冷凝器 | 正在制热的室内机（`Q_i > 0`） |
| 低压气管 | 室内机蒸发器 → 压缩机回气 | 正在制冷的室内机（`Q_i < 0`） |
| 液管 | 室外机 ↔ 室内机 | 公共回路 |

VTE 只有一条 `EfficiencyNet`，没有相分离。我们在账本里**虚拟出两相**，物理上仍写回 AO 的格。

### 3.3 网络账本

对一个 `AcPipeNet`，每 250 tick 做一次：

```
对每台可运行的室内机 i（PowerOn && ResourceOn）：
    Tc_i = 出风格温度（AO TryGetTemperature）
    Tt   = 控制器 TargetNetworkTemperature
    drive_i = clamp01(|Tt - Tc_i| / 1K)
    Q_i = |eps_i| × (250/60) × heatFactor_i × drive_i × sign(Tt - Tc_i)   // 显热
    若 Tc_i > 120℃：Q_i = 0        // 保留 VTE 的高温停机
    若制冷（Q_i < 0）且可除湿：Q_latent_i = dehum_i × latent，否则 0

a = Σ max(0, +Q_i)                       // 冷凝侧总热负荷
b = Σ max(0, -Q_i) + Σ Q_latent_i        // 蒸发侧总冷负荷（含除湿潜热）
Q_rec = min(a, b)                        // 网内热回收量，不占用室外机容量

Q_signed = a - b                   // >0 净制热（需从室外吸热），<0 净制冷（需向室外排热）
Q_net    = |Q_signed|              // 必须由室外机承担的量
C = (Q_signed > 0) ? C_heat : C_cool
s = (Q_net < ε) ? 1 : min(1, C / Q_net)
```

**核心性质**：

| 场景 | `Q_signed` | `Q_net` | `s` | 含义 |
|---|---|---|---|---|
| 完全平衡 `a = b` | 0 | 0 | 1 | 全部靠热回收，室外机只做泵功 |
| 轻微不平衡 `a=100, b=90` | +10 | 10 | 1（若 `C≥10`） | 回收 90，室外机只补 10 |
| 完全不平衡 `a=100, b=0` | +100 | 100 | `C/100` | 全部由室外机承担，容量决定满足率 |
| 完全不平衡 `a=0, b=100` | -100 | 100 | `C/100` | 同上，方向相反 |
| 无压缩机 `C=0` | ≠0 | >0 | 0 | 强制关断 |

**自洽性**：若所有室内机按同一比例 `s` 削减，则 `a' = s·a`、`b' = s·b`、`Q_net' = s·Q_net`。要求 `Q_net' ≤ C`，即 `s ≤ C/Q_net`。所以 `s = min(1, C/Q_net)` 是精确解，不需要迭代。

### 3.4 同比例削减

每台室内机的实际输出：

```
Q_i_out = Q_i × s
```

没有优先级、没有单台豁免。这样整个网络只有一个 `Efficiency` 值，与 VTE 的 UI 自洽，玩家理解成本最低。

### 3.5 室外机的三种状态

`Q_signed` 是**标量**，只有一个符号，所以室外机在任一账本周期只可能是三种状态之一：

| 条件 | 室外机角色 | 热行为 |
|---|---|---|
| `Q_signed > 0`（制热多于制冷） | 蒸发器 | 从压缩机周围吸 `Q_signed × s` 的热 |
| `Q_signed < 0`（制冷多于制热） | 冷凝器 | 向压缩机周围排 `|Q_signed| × s` 的热 |
| `Q_signed = 0`（完全平衡） | 不换热 | 只搬运 `Q_rec`，不与环境交换 |

**不会同时吸热和放热。** 原因：`Q_rec` 已经在网内对冲掉了，`Q_signed` 只剩净差；`a > b`（制热多）时它是正数、室外机吸热，`a < b`（制冷多）时是负数、室外机排热，不存在「一部分正、一部分负」的分解。

多台压缩机并联时它们**同向工作**，容量相加，不会出现「一台排热、一台吸热」。若玩家把网络拆成两个独立子网（不同管连接），它们各自有独立的 `Q_net`，那是两个网络，不是同一台室外机同时做两件事。

### 3.6 容量

VTE 的压缩机容量是「可支持几台满额室内机」的台数单位（Def 里 `VTE_ACCompressor` 是 2、`VTE_LargeACCompressor` 是 6）。账本内部要统一成热容量，才能和 `Q_net`（K·格 / 250 tick）比较：

```
Q_ref = |eps_ACUnit| × (250/60) × heatFactor_ACUnit   // 一台满额 ACUnit 的热负荷，约 87.5 K·格 / 250 tick

C_cool = (Σ 可运行压缩机制冷台数) × Q_ref
C_heat = (Σ 可运行压缩机制热台数) × Q_ref
```

| 机组 | 台数容量 | 折合热容量（约） |
|---|---|---|
| `VTE_ACCompressor` | 2 | 175 K·格 / 250 tick |
| `VTE_LargeACCompressor` | 6 | 525 K·格 / 250 tick |

- 被屋顶覆盖的压缩机不计入容量（`CanBeOn() == false`）。
- 制热容量可乘以室外温度修正：`C_heat ×= clamp01((T_outdoor + 20) / 40)`（越冷越难从室外吸热）。第一版可先不做，留作参数。
- `Q_ref` 只用于量纲换算；`s` 本身是无量纲比例，不依赖 `Q_ref` 的取值。

---

## 第四章 账本算法

### 4.1 伪代码

```
每 250 tick，对每个 AcPipeNet net：

    // 1. 控制器
    if net.ControllerList 为空 或 无 CanBeOn 的控制器：
        全部室内机 slots.ZeroFrom(0)
        全部压缩机 slots.ZeroFrom(0)
        net.Efficiency = 0
        标记「缺少控制器」
        return

    // 2. 压缩机
    C_cool = Σ 可运行压缩机的制冷容量
    C_heat = Σ 可运行压缩机的制热容量
    if C_cool == 0 && C_heat == 0:
        强制关断：全部室内机 ResourceOn = false
        全部 slots.ZeroFrom(0)
        net.Efficiency = 0
        标记「缺少压缩机」
        return

    // 3. 收集需求
    Tt = 第一个可运行控制器的 TargetNetworkTemperature
    a = b = 0
    for each 室内机 i in net.receivers:
        if !PowerOn or !ResourceOn: continue
        Tc = AoBridge.TryGetTemperature(出风格) ?? skip
        drive = clamp01(|Tt - Tc| / 1K)
        Qi = |eps| × (250/60) × heatFactor × drive × sign(Tt - Tc)
        if Tc > 120: Qi = 0
        Qi_lat = 0
        if Qi < 0 且该格可除湿:
            Qi_lat = Q_latent(湿度, 目标湿度)   // 除湿潜热，计入蒸发侧 b，见 5.4
        if Qi > 0: a += Qi else b += -Qi
        b += Qi_lat
        账本[i] = Qi

    // 4. 网络
    Q_rec    = min(a, b)
    Q_signed = a - b                    // >0 净制热（室外吸热），<0 净制冷（室外排热）
    Q_net    = |Q_signed|
    C = (Q_signed > 0) ? C_heat : C_cool
    s = (Q_net < ε) ? 1 : min(1, C / Q_net)

    // 5. 执行
    for each 室内机 i:
        Q_out = 账本[i] × s
        写持续源：TemperaturePerTick = Q_out / 250 / 覆盖格数
        若制冷且除湿：MoisturePerTick = -dehum_i × s / 250
        风：MaintainTargetWind = true，TargetWind = 方向 × 风速

    // 6. 室外机（压缩功不计入排热，第一版简化）
    //    PushHeat 的 energy 为正 = 该处升温（排热），为负 = 降温（吸热）
    //    净制热 → 室外吸热 → 传负值；净制冷 → 室外排热 → 传正值
    if Q_net > 0:
        PushHeat(每台压缩机位置, (-Q_signed × s) / 压缩机数)
    // Q_signed == 0：不写热，只做泵功

    // 7. 电费
    W_indoor  = Σ (开启的室内机 i)   PowerConsumption_i × (|Qi| / Q_full_i)
    W_outdoor = Σ (可运行压缩机 j)   PowerConsumption_j × min(1, Q_net / max(ε, C))
    net.Efficiency = s
```

### 4.2 单位与量级

| 量 | 单位 | 来源 |
|---|---|---|
| `Q_i` | K·格 / 250 tick | `|eps| × 250/60 × heatFactor`，与 VTE 原版一致 |
| `Q_full_i` | K·格 / 250 tick | `|eps_i| × 250/60 × heatFactor_i`，满额需求 |
| `TemperaturePerTick` | K / tick | `Q_out / 250 / 格数` |
| `Q_net` | K·格 / 250 tick | `|a - b|`，室外机实际承担的量 |
| `PushHeat` 的 `energy` | K·格（总量） | `QueueLocalizedTemperatureDelta` 内部 `/5` |
| `W_indoor` / `W_outdoor` | W（游戏电力单位） | `PowerConsumption × 比例` |
| 潜热 | K·格 | `moisture × latent`，`latent` 取 AO 的 `FacilityMath.Latent` |

**量级自检**：一台 `energyPerSecond = -21` 的 ACUnit，满额 250 tick 注入 `21 × 4.1667 ≈ 87.5 K·格`。一个 25 格房间平均升温/降温 `3.5 K`，与 VTE 原版一致。

### 4.3 电费

**室内机**：按需求热负荷计费，与 `s` 无关。

```
W_indoor_i = PowerConsumption_i × (|Q_i| / Q_full_i)
           = PowerConsumption_i × drive_i × 除湿附加系数
```

- `Q_full_i = |eps_i| × (250/60) × heatFactor_i`，与 `Q_i` 的满额分母一致，所以 `|Q_i| / Q_full_i` 就是 `drive_i`。
- `|Q_i| = Q_full`（满额需求）→ 耗 `PowerConsumption`（100W），与 VTE 原版一致。
- `|Q_i| = 0.5 × Q_full` → 耗 50W。
- `s = 0`（容量不足）时 `|Q_i|` 仍是需求，所以**仍耗电**——室内机在空转等待冷媒。
- 室内机 `ResourceOn = false` 或被强制关断时不参与，不耗电。

**室外机**：按实际承担的净量计费。

```
W_outdoor_j = PowerConsumption_j × min(1, Q_net / max(ε, C))
```

- `Q_signed = 0`（完全平衡）→ 0，室外机不耗电。
- `Q_net ≥ C`（满载）→ 耗 `PowerConsumption`。
- 室外机 `ResourceOn = false` 或被屋顶覆盖 → 不计入，不耗电。
- 泵功可忽略；若要在「完全平衡」时保留一点电耗，可加 `W_pump = 0.05 × PowerConsumption`。

**总电费**：

```
W_total = W_indoor + W_outdoor
```

不再使用 VTE 的 `(2 - Efficiency)` 乘数。

### 4.4 无压缩机

```
若 C_cool == 0 && C_heat == 0：
    强制关断全部室内机（ResourceOn = false）
    slots.ZeroFrom(0)
    Efficiency = 0
    显示「缺少压缩机」（沿用 VTE.MissingCompressors）
```

关断后室内机不参与账本，所以也不耗电。玩家重新装压缩机后，下一个账本周期自动恢复。

---

## 第五章 写入 AO

### 5.1 室内机

持续源，每台室内机一个，写在出风格：

```
rates = new AtmosphereSourceRates {
    TemperaturePerTick = Q_out / 250 / n,
    MaintainTargetWind = active,
    TargetWind = 出风方向 × outletWindSpeed,
    TargetWindBlendPerTick = 0.1,
}
```

- `n`：能量分摊格数。默认 1（点源，靠 AO 扩散），可配置为「房间格数」以更均匀。
- 出风方向：`RotatedBy(outputOffsetNorth, Rotation)`；`AcUnitWall` 的 `offsetNorth = (0,0,0)`，即本体格。
- 除湿：`MoisturePerTick = -dehum × s / 250`（实际量按同比例削减）；对应的 `dehum × latent` 已在账本收集阶段计入 `b`（蒸发侧），执行阶段不再重复加。

### 5.2 室外机

```
AtmosphereInterface.PushHeat(compressor.Position, -Q_signed × s / 压缩机数)
```

- 正 `energy` = 该格升温（室外机排热），负 = 降温（室外机吸热）。所以净制冷（`Q_signed < 0`）传正值，净制热传负值，一行公式覆盖两种状态。
- 压缩功（`W_outdoor`）不计入排热，第一版简化；若要计入，改为 `-(Q_signed × s + W_outdoor × 热功当量)`。
- `PushHeat` 内部把总量均分到十字 5 格，只保留同房间的格。
- 若压缩机周围全是不可通行格，退化为 `AddTemperatureDelta` 到本体格。
- 被屋顶覆盖：不计入容量；若玩家强行运行，排热改走室内并在 UI 警告。

### 5.3 新风（第二版）

VTE 原模型没有新风。若要补，用：

```
AtmosphereInterface.AddVolumeSource(map, cell, cellVolumes, 21f, 送风温度)
```

- `cellVolumes` 单位是格体积（1 = 5 m³）。
- 新风量由「室内机数量 × 每台最小新风」或玩家设定决定。
- 第一版不做。

### 5.4 除湿与潜热

- 只在**制冷**且 `Tc < 露点` 时除湿。
- 除湿量：把出风格相对湿度降到 0.4（沿用 AO 的 `AcDryGoal`），限于 `max(0, vapor - 0.4 × VaporCapacity(Tc))`。
- 潜热：`Q_latent = dehum × latent`，**在收集需求阶段估算并加到 `b`（蒸发侧）**——因为 `Q_latent` 必须先进入 `Q_signed`，`s` 才算得对；执行阶段按 `s` 同比例削减实际除湿量。
- 顺序上：先按 `Tc` 与当前湿度算出 `dehum_est`，再算 `Q_latent`，最后才定 `Q_signed`/`s`，不能反过来。
- 产水：沿用 AO 的 `AO_Water` 逻辑，每 0.25 kg 一件。

---

## 第六章 与 VTE 三个字段的映射

| VTE 字段 | 原语义 | 新模型角色 | 实现 |
|---|---|---|---|
| `idleConsumptionPerTick`（维持值） | 死参数；VTE 从不触发 `UsedLastTick`，生产者分支还忽略它 | `MinLoad`：`a + b < MinLoad` 时停机或按最低频率运行 | 账本字段，不改 Def；`MinLoad = 0.15 × C` |
| `Consumption`（负荷） | 开通台数 × 1，与温差无关 | 流量请求，只做管网过载粗判和 UI | 继续由 `PipeNet` 维护，账本只读 |
| `Efficiency`（效率） | 供需比，只乘电费，非连续 | **系统满足率 `s`**，每 250 tick 主动重算 | 账本直接写 `net.Efficiency` |

**生产/消费账本保留**：`Production`/`Consumption` 仍按 VTE 的「控制器 -2、压缩机 -2/-6、室内机 +1」维护，UI 不变。`Efficiency` 现在是 `s`，范围 `[0,1]`，落在 VTE 的 `[0,1.5]` 显示范围内。

**不再使用的量**：`CalculateEfficiency` 的供需比公式、`(2 - E)` 电费乘数、管长/阀门/管网损失。

---

## 第七章 接管点

### 7.1 为什么不移除 AO 组件

- 移除后 `RunTempControl` 返回 true，原 `TickRare` 完整执行，`room.Temperature += a` 被 `ApplyRoomTemperatureWrite` 翻译成「每格 +a」。
- 这不会放大能量（`a` 已是每格量），但会**退化成房间均匀加热/制冷**：丢掉 AO 的格级梯度、风、湿度、除湿，且仍受 AO 的 `get_Temperature` 覆盖与 `maxDelta=200` 夹取。
- 结论：移除是降级，不是回到原版。正确做法是**接管**。

### 7.2 推荐接管点

**A. 室内机：`Building_AcUnit.TickRare` 的 Prefix，priority 高于 AO（如 `Priority.First`），返回 false。**

```
[HarmonyPatch(typeof(Building_AcUnit), nameof(Building_AcUnit.TickRare))]
[HarmonyPriority(Priority.First)]
static bool Prefix(Building_AcUnit __instance) {
    // 读账本、写 AO 源、写 PowerOutput、operatingAtHighPower
    return false;
}
```

- 返回 false 后，AO 的 `TempControlSubclassPatches.Prefix` 不执行，`Run` 不写源，原方法不写房间温度，**不需要 `RoomTemperatureWriteHook.Enter()`**。
- 必须自己处理：`PowerOn` 检查、`ResourceOn` 检查、`PowerOutput`、`operatingAtHighPower`。

**B. 账本：AO 的 `CompleteFinishedSimulation` Postfix（30 tick），每 250 tick 去重。**

```
[HarmonyPatch]
static class AtmosphereSimulationHook {
    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(AtmosphereMapComponent), "CompleteFinishedSimulation");
    static void Postfix(AtmosphereMapComponent __instance) {
        ACNetLedger.Tick(__instance);   // 内部 lastLedgerTick 去重
    }
}
```

- 该方法是 `private`（`RimWorld/AtmosphereMapComponent.cs:3071`），不能写 `nameof`。
- 放在模拟步里，保证账本先于所有建筑的 `TickRare` 更新完。
- 若不想依赖 AO 私有方法，可退回 `AcPipeNet.PipeSystemTick` 的 Postfix，但要接受时序不保证。

**C. `ResourceOn` 的边界。**

- 账本只在「无压缩机」时写 `ResourceOn = false`；其他情况只读。
- VTE 的 `Efficiency == 0 → 全部 ResourceOn = false` 仍可能触发；账本先写 `Efficiency`，让下一 tick 看到新值，或在接管后 patch 这段逻辑。

### 7.3 调度顺序

```
每 30 tick：AO 模拟步
    FlushContinuousIntervalToQueues → ApplyTemperatureDeltas
    → CompleteFinishedSimulation Postfix（我们：每 250 tick 算一次账本）

每 250 tick（账本周期）：
    1. 读控制器目标温度
    2. 读每台室内机出风格温度
    3. 算 a / b / Q_rec / Q_net / s
    4. 写 net.Efficiency = s
    5. 写每台设备的待写速率到账本缓存

每台室内机 TickRare（在账本之后）：
    读账本缓存 → 写 AO 持续源 → 写 PowerOutput
```

---

## 第八章 参数表

| 参数 | 默认 | 说明 |
|---|---|---|
| `LedgerIntervalTicks` | 250 | 账本周期，与 `TickRare` 对齐 |
| `CompressorCapacityCool` | 2 | 每台 `VTE_ACCompressor` 的台数容量，实际热容量 = 2 × `Q_ref` |
| `CompressorCapacityHeat` | 2 | 每台 `VTE_ACCompressor` 的台数容量，实际热容量 = 2 × `Q_ref` |
| `LargeCompressorCapacityCool` | 6 | 每台 `VTE_LargeACCompressor` 的台数容量，实际热容量 = 6 × `Q_ref` |
| `LargeCompressorCapacityHeat` | 6 | 每台 `VTE_LargeACCompressor` 的台数容量，实际热容量 = 6 × `Q_ref` |
| `ACUnitRefCapacity` | 87.5 | `Q_ref`（K·格 / 250 tick），由 `|eps_ACUnit| × 250/60 × heatFactor` 算出 |
| `HeatCapacityOutdoorTempFactor` | true | 制热容量是否按室外温度修正 |
| `MinNetThreshold` | 0.01 | `Q_net` 低于此值视为完全平衡 |
| `MinLoadFraction` | 0.15 | `MinLoad = 0.15 × C` |
| `OutletWindSpeed` | 15 | 沿用 AO 的 `outletWindSpeed` |
| `DehumGoalRh` | 0.4 | 除湿目标相对湿度 |
| `MaxOutletCelsius` | 120 | 沿用 VTE 的高温停机 |
| `IndoorPowerFull` | 100 | 室内机满额电耗（W），取自 Def |
| `CompressorPowerFull` | 50 / 100 | 压缩机满额电耗（W），取自 Def |
| `PumpWorkFraction` | 0 | 完全平衡时的泵功占额定功率比例，默认 0 |

所有参数进 `AOPatchSettings`，支持运行时调整与存档持久化。

---

## 第九章 边界情况

| 情况 | 行为 |
|---|---|
| 无控制器 | 账本跳过，所有设备 `ZeroFrom`，`Efficiency = 0`，UI 显示「缺少控制器」 |
| 多控制器 | 取第一个可运行控制器的 `TargetNetworkTemperature`，其余忽略；`CanBeOn` 判定改为 `>= 1` |
| 控制器关闭 / 断电 | 视为无控制器 |
| 无压缩机（`C = 0`） | 强制关断全部室内机，`Efficiency = 0`，UI 显示「缺少压缩机」 |
| 压缩机全部被屋顶覆盖 | 同「无压缩机」 |
| 压缩机部分被覆盖 | 被覆盖的容量不计入 `C` |
| 室内机 `ResourceOn = false` | 不参与账本，`ZeroFrom`，不耗电；不影响其他室内机 |
| 室内机 `PowerOn = false` | 同上 |
| 出风格越界 / 不可通行 | 该室内机 `Q_i = 0`，不参与账本 |
| 出风格在室外 | 允许运行（AO 的 `RunCooler` 也允许） |
| 室内温度 > 120℃ | 该机 `Q_i = 0`（保留 VTE 行为） |
| `Q_signed = 0`（完全平衡） | `s = 1`，室外机不换热、不耗电（泵功按参数） |
| `Q_net > C` | `s = C/Q_net < 1`，同比例削减，室外机满载耗电 |
| `a = b = 0`（全部待机） | `s = 1`，无输出、无室外机负荷；室内机按 `|Q_i| = 0` 不耗电 |
| 房间格数 > 3000 | 我们的方案用持续源，不受 AO 的 `ApplyRoomTemperatureWrite` 上限影响 |
| 读档 / 读档后 `lastLedgerTick` 失效 | 用 `TicksGame` 判断，不持久化账本中间量；只持久化 `Efficiency`（VTE 已有） |
| 拆除建筑 | `SourceSlots.RemoveAll` 由 AO 组件自己的 `PostDeSpawn` 处理；账本每 250 tick 重扫，自动剔除 |
| 多线程 | 账本只在主线程的 tick 里跑；AO 的 `incomingDeltasLock` 已处理并发写 |

### 9.1 错误图标的所有权

图标的实际状态存在 `PipeNetOverlayDrawer`（`CustomOverlayDrawer` 的脉动层 + `OverlayForThing` 字典）里，是**全局共享的可变状态**：谁都能写，写进去也不会自动回读。所以：

- **只有账本有权写中控器图标**：`AcNetOverlay.Refresh` 每账本步按 `s <= 0` 写一次，且只在值变化时写。
- VTE 自己还会在 `AcPipeNet.RegisterComp` / `UnregisterComp`（管网合并、拆分）里对 `CompResourceSingleton` 调 `UpdateOverlayHandle`，按**当时**的 `Efficiency` 直接写 drawer。合并那一刻被并入的网往往还是账本给「无控制器网」写下的 `0`：新空调先自成一网（`Efficiency = 0`），玩家再用管道把它接到主网，若新网恰好是「第一个邻接格」所在的网（邻接顺序北→东→南→西），它吞并主网，中控器就被注册进这个 `0` 网，图标当场被点亮。
- 因为 `_lastState` 只认「自己上次写过的值」，VTE 绕过缓存写入后，账本下一步算出的 `false` 会被 early-return 丢弃 → 图标永久脉动，而网络照常运行（`Efficiency`、`ResourceOn`、功率都是账本重算的）。

结论：**对这类共享可变状态做「缓存 + 只在变化时写」的节流，必须配套失效点。** `AcNetPatches` 因此给 `AcPipeNet.RegisterComp` / `UnregisterComp` 挂了两个 Postfix：`ControllerRegisteredPostfix`（作废缓存 + 同步温度）与 `ControllerUnregisteredPostfix`（只作废缓存），只要有 `CompResourceSingleton` 被搬动就 `AcNetOverlay.Forget(id)`，逼账本下一步无条件重写一次。中控器拆除时由 `CompResourceSingleton.PostDeSpawn` 的 Postfix（`ControllerDeSpawnPostfix`）清账，避免 `_lastState` 随拆建增长。

**为什么必须拆成两个**：`PipeNet.UnregisterComp` 不会清空 `comp.PipeNet`，而 `PipeNet.Destroy` 是边遍历 `connectors` 边调用 `UnregisterComp` 的半拆状态，此时读网络里的控制器/室内机全是残余数据。

### 9.2 目标温度的所有权

VTE 是**推送（push）模型**，不是轮询：

| 写入点 | 时机 | 覆盖面 |
|---|---|---|
| `CompAcTempControl.PostSpawnSetup:29` | 机器生成时 | 必然早退（见下），等于不生效 |
| `CompAcTempControl:50` | 玩家点「取消联动」 | 单台 |
| `Building_AcControlUnit:112` 广播 | 玩家改中控器温度 | 仅「那一刻已在 `net.receivers` 里」的机器 |

第一个写入点失效的原因是组件顺序：`ACNet.xml` 里 `CompAcTempControl`（324 行）排在 `CompResourceTrader_AC`（328 行）之前，同一次 `SpawnSetup` 中先跑的它读到的 `PipeNet` 还是 null（由后者的 `PostSpawnSetup` → `RegisterConnector` → `RegisterComp` 才赋值），于是 `CompAcTempControl.cs:27-28` 直接 return。

结果：新建、合并进网、拆管道重建后重新入网的机器，`targetTemperature` 永远停在 `CompProperties_TempControl.defaultTargetTemperature`（21℃），网络里出现两种出风温度。

**修复分两层，缺一不可：**

1. **物理层（账本）**：`AcNetLedger.ResolveTarget` —— 非独立机直接用中控器的 `TargetNetworkTemperature`，独立机用自己的字段。字段从此只是显示/存档用的副本，不是计算的唯一真源。
2. **显示层（同步）**：`ControllerRegisteredPostfix` 在每次 `RegisterComp` 后把网络温度补写进该机字段（中控器后注册时反向扫 `net.receivers`），保证 `CompTempControl.CompInspectStringExtra` 显示的值与实际出风一致。

同步的方向判定不需要「每台一个目标值」：`Q_i` 的符号由 `target` 与该机**所在房间**温度的差决定（`AcNetLedger.cs:214`），共用同一个 `target` 时，屋里热的机制热、屋里冷的机制冷，热回收照常成立。

**注意**：`CompAcTempControl` 自己没有 override `TargetTemperature`，写字段不会触发副作用；非独立机本来也没有温度按钮（`CompAcTempControl.cs:58` 只在 `IndependentTemp || pipeNet == null` 时给基类按钮），所以同步不会覆盖玩家手动值。唯一的副作用是新机的 `pipeNet` 字段可能仍为 null（同一个早退导致），会临时显示一个温度按钮 —— 点了也只是改字段，下一次注册同步会被覆盖回来。

---

## 第十章 验收标准

1. **双向**：目标温度高于室温时出热风，低于时出冷风，差值 < 1K 时停机。
2. **热回收**：同一网络上一半室内机制冷、一半制热时 `s = 1`，室外机不排热；`Efficiency` 显示 100%。
3. **不平衡**：全部室内机制冷、无制热时，`s = C / Σ|Q_i|`；增加压缩机后 `s` 上升。
4. **单值室外机**：任一账本周期，压缩机位置只有「升温」「降温」「不变」三种之一，不会同时出现。
5. **电费**：室外机不工作时，室内机仍按 `|Q_i|` 耗电；室外机按 `min(1, Q_net/C)` 比例耗电（满载 1.0，部分负载线性）；完全平衡时室外机不耗电。
6. **强制关断**：拆掉全部压缩机后，室内机在一个账本周期内关闭，UI 显示「缺少压缩机」，不再写 AO 源。
7. **同比例**：`a=100, b=50, C=75` 时 `s=1`；`C=30` 时 `s=0.6`，制热与制冷各按 0.6 削减。
8. **高温停机**：出风格 > 120℃ 时该机 `Q_i = 0`。
9. **性能**：100 台室内机 + 20 台压缩机的网络，账本单次 < 1 ms；无每 tick 分配。
10. **兼容**：与 AO 的 `CompTempControlAtmosphere`、`RoomTemperatureWriteHook` 无冲突；`CompTempOutputShape` 不被触发（我们的 Prefix 优先级更高）。
11. **存档**：读档后 `TargetNetworkTemperature`、`IndependentTemp`、`Efficiency` 保持；账本在一个周期内重建。
12. **温度继承**：新建、合并进网、拆管道重建后入网的联动机，在 `RegisterComp` 回调内（不等下一个模拟步）就取到中控器温度；面板显示与实际出风一致。独立机与「取消联动」的机器不受影响。
13. **独立机**：`IndependentTemp = true` 的机器改自己的温度，不触发全网同步，也不被同步覆盖。

---

## 附录 源码索引

**AO**
- `RimWorld/AtmosphereInterface.cs` —— 全部公开注入 API
- `RimWorld/AtmosphereMapComponent.cs:1839` —— `EnqueueTemperatureExternalDelta`
- `RimWorld/AtmosphereMapComponent.cs:2622` —— `FlushContinuousIntervalToQueues`
- `RimWorld/AtmosphereMapComponent.cs:2843` —— `QueueLocalizedTemperatureDelta`
- `RimWorld/AtmosphereMapComponent.cs:2901` —— `ApplyRoomTemperatureWrite`
- `RimWorld/AtmosphereMapComponent.cs:2931` —— `PushHeat`
- `RimWorld/AtmosphereMapComponent.cs:3071` —— `CompleteFinishedSimulation`（private）
- `RimWorld/AtmosphereSourceRates.cs:12` —— 8 组速率字段
- `Simulation/LocalAtmosphereEngine.cs:3401` —— `AddTemperatureDelta`
- `Simulation/LocalAtmosphereEngine.cs:3003` —— `ApplyTemperatureDeltas`
- `Simulation/LocalAtmosphereSettings.cs:12` —— `DefaultSimulationIntervalTicks = 30`
- `Simulation/AtmosphereThermodynamicKernel.cs:19` —— 潜热耦合
- `Buildings/4VanillaFacilities.cs` —— `CompTempControlAtmosphere.Run` / `RunCooler` / `RunHeater` / `Dehumidify`
- `Buildings/5VanillaFacilities.cs:14` —— `FacilityCompInjection`
- `Buildings/6VanillaFacilities.cs:50` —— `TempControlSubclassPatches.Prefix`
- `Buildings/13VanillaFacilities.cs:16` —— `RunTempControl`
- `Patches/RoomTemperatureWriteHook.cs:28` —— `OnSet`
- `Patches/4TemperaturePatches.cs:37` —— `get_Temperature` 替换
- `Patches/5TemperaturePatches.cs:16` —— `set_Temperature` 拦截

**VTE**
- `Buildings/Building_AcUnit.cs` —— `TickRare` 全文
- `Buildings/Building_AcControlUnit.cs` —— `InterfaceChangeTargetNetworkTemperature`
- `Comps/CompAcTempControl.cs` —— `IndependentTemp`
- `Comps/CompResourceTrader_AC.cs` / `CompResourceSingleton.cs` / `CompResourceTrader_Compressor.cs`
- `AcPipeNet.cs` —— `CalculateEfficiency` / `PipeSystemTick` / `WouldOverload`
- `1.6/Defs/NetDefs/ACNet.xml` —— 8 个机组与管路的 Def 数值

**PipeSystem 与原版**
- PipeSystem `CompResourceTrader.Consumption` / `PipeNet.RegisterComp` / `PipeNet.PipeSystemTick`
- `Source/Verse/GenTemperature.cs:315` —— `ControlTemperatureTempChange`
- `Source/RimWorld/Building_Cooler.cs:36` / `Building_Heater.cs:22` —— 原版温控的调用方式
