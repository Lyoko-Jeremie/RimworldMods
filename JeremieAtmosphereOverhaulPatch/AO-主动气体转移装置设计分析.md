# AO 主动气体转移装置设计分析（v2）

> 目标（用户 m00414 原话）：不管两侧是室内还是室外都能开。将其设计为本质上是一个将一侧主动转移空气的装置，将一侧的气体温度湿度氧气强制转移到另一侧，并在两侧对应地产生流动。明确设定可以将室内温湿度气体气压排放到大气，也可以将大气气体气压温湿度压入室内，也可以跨一个墙面从一侧大气转移到墙另一侧的大气。接管期间是否通道。
>
> 三次收窄（用户后续原话，已定稿）：
> - m00486：「原版一把写掉整个 Room 在 AO 是不成立的。我们只需要想办法实现高速的 AirMover 即可。」→ 否掉房间级成对写。
> - m00500：「注意，模拟步数始终是按照 AO 的物理模拟系统进行，我们不能自己去控制间隔。」→ 否掉改 `SimulationIntervalTicks`、否掉依赖 subcycling。
> - m00506：「注意，我们要保持 GC 的通风口语义，通风口本身只影响两端的一个格子。再次阅读 @AGENTS.md」→ 否掉 K 条并行多端口/分布式端口。
>
> 分析基准：AO 反编译源码 `F:/RimworldCodeIL/AtmosphereOverhaul/`（MVID 4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7），GC 反编译源码 `F:/RimworldCodeIL/GoodClimate/`。本文只做设计分析，不含代码改动。
>
> 本文替代 v1：v1 把模拟间隔误当成 30 tick，且把「房间级成对写」列为可选路线；两处均已按 m00486/m00500 修正。

## 0. 结论摘要

1. **通道选 AO 的 `AtmosphereAirMover` 非喷流模式**（`ConditionExhaust = false`）：抽 inlet 体积 → 注 outlet → outlet 与 inlet 气样按 `1-exp(-V)` 混合 → 两侧各加一次速度脉冲。温度/湿度/雾/氧气随质量走，AO 内部守恒。这是唯一同时满足「气体真的被搬走」「两侧产生流动」「定向」的现成通道。
2. **定稿方案 = 单对端口 + 饱和参数 + GC 门控/抑制 patch**：
   - 端口就是 vent 的 `Position ± Rotation.FacingCell` 两格（符合 m00506「只影响两端各一格」）；
   - 参数取饱和值（`CubicMetresPerTick` 略高于 0.4167），使每步搬满 0.5 格体积上限；
   - patch 掉 GC 的 `GC_IE_ventIntensiveExchange`，保留 GC 的 IE 按钮、电费、VR 反转按钮。
3. **强度上限是物理的，不是参数没调好**：默认 6 tick/步、每步 0.5 格体积，64 格房间理想换气 τ ≈ 12.8 s；GC 原版约 0.6 s。差距约 20 倍，**无法在不改 AO 模拟步进的前提下消除**（m00500 已否决改步进）。
4. **逐建筑滑块可做**：唯一有效旋钮是 `CubicMetresPerTick`，有效范围 0 ~ 0.4167（气柱 5 m、6 tick），再大被 `LimitedVolumes` 卡死。滑块改设置不会丢周期（`UpdateAirMover` 先 `CaptureAirMover` 结算旧区间）。
5. **有 5 个必须在设计阶段解决的边界问题**：可压模式穿墙、VR 反转方向不跟、真空漏气、风速只有 0.5 m/s、不可压设置下「室内↔大气」静默失效。见 §4。

---

## 1. AirMover 机制（非喷流模式）

### 1.1 数据流

```
RegisterAirMover(owner, key, inlet, outlet, settings, activeTicks)   // AtmosphereMapComponent.cs:185-217
        ↓ 每模拟步
PrepareAirMovers(tick)                                              // :430-444
        ↓ CaptureAirMover → AirMoverTransport.ActiveTicks(...)      // :269-286
ApplyAirMoversUnsafe(settings, stepFraction)                        // :472-552
        ├─ IsAirMoverPortOpen(inlet) && IsAirMoverPortOpen(outlet) && AirMoverHasReturnPath(inlet, outlet)
        ├─ amount = min(1000, Flow(CubicMetresPerTick, ΔP*1000, MaxPressureRisePascals) * ElapsedTicks*stepFraction / AirColumnHeightMetres)
        ├─ exchangedCellVolumes = LimitedVolumes(amount, demand[inlet], demand[outlet])
        ├─ AddAirMoverVolumeRate(inlet, outlet, exchangedCellVolumes / TimeStep / PressureAccelerationScale)
        ├─ 两侧各一次 AirMoverPortImpulse → VelocityX/Z
        └─ outlet 混合：Temperature/Moisture/Mist/Oxygen 按 1-exp(-V) 向 inlet 样本靠拢
```

### 1.2 守恒性

- `AddAirMoverVolumeRate` 只做两件事：`externalDivergence[inlet] -= rate / ObstacleMask[inlet]`、`externalDivergence[outlet] += rate / ObstacleMask[outlet]`（`LocalAtmosphereEngine.cs:801-805`）。即**体积（质量）从一侧挪到另一侧**，总量不变。
- inlet 的**温度读数不变**（抽走的是「一份空气」，剩余空气状态不变），但**总能量随质量减少**。
- outlet 的混合是 `Mix(receiver, donor, 1-exp(-V))`，`receiver + (donor-receiver)*f`，是质量加权平均，不会过冲。
- 因此 **inlet 少掉的能量 = outlet 多出来的能量**，温度/湿度/氧气都守恒。比手工配平的「成对热交换」更可靠。
- `Flow` 有逆压衰减：`requested * max(0, 1 - max(0, ΔP) / maxRise)`（`AirMoverTransport.cs:19-22`）。outlet 压力高于 inlet 时流量线性下降，压差 ≥ `MaxPressureRisePascals` 时归零。**这就是风扇扬程**。
- `LimitedVolumes`：`requested * min(1, 0.5 / max(inletDemand, outletDemand))`（`AirMoverTransport.cs:34-38`）。同一格被多台 mover 争用时按需求比例分配，且单步总量 ≤ 0.5 格体积。**多台 mover 并联在同一格不会提速**，只会互相稀释（这也是 m00506 否掉 K 并行的物理原因）。
- 氧气/气体成分转移不是同步的：`ApplyAirMoversUnsafe` 记录到 `completedAirMoverTransfers`，模拟结束后 `CompleteAirMoverGasTransfers()`（`:896-901`）交给 `gasManager.QueueAirMoverTransfers(...)` 异步批量应用。温度/湿度/雾是同步的。

### 1.3 三种场景逐条

| 场景 | inlet / outlet | 连通性 | 实际效果 |
|---|---|---|---|
| 室内 → 大气 | 室内格 / 露天格 | vent 开 → 同一分量；可压模式下甚至不要求同分量 | 室内失质量；大气侧被户外松弛拉回 ambient，等于「排进大气」。 |
| 大气 → 室内 | 露天格 / 室内格 | 同上 | 室内得质量，T/湿/氧向**实时**大气值靠拢（读 `engine.Temperature.Read[inlet]`）。逆压时受 `Flow` 衰减，室内超压会吸不动。 |
| 大气 → 大气跨墙 | 两侧露天格 | vent 开 → 同一分量 | 能搬，但两侧每秒都被 `OutdoorTemperatureRelaxation = 0.15`（`LocalAtmosphereSettings.cs:35`）拉回 ambient，净效果 ≈ 吹风 + 轻微压力扰动。 |
| 室内 ↔ 室内（普通通风） | 两房间格 | vent 开 → 同一分量 | 定向搬气，两侧温湿氧趋于一致，但**只从端口开始**，房间整体靠 AO 扩散/对流。 |

> 「跨墙」成立的原因：`BuildVents` 在 `AxisX = true` 时只清 `faceOpenZ[index]` 与 `faceOpenZ[index - width]`（封上下），**保留 `faceOpenX`**；`AxisX = false` 时对称地保留 `faceOpenZ`。见 `MapObstacleMaskBuilder.cs:114-125`。所以 vent 格沿气流轴向与两侧空气格连通，flood fill 能穿过 vent 格。

---

## 2. 模拟间隔与强度定量（修正版）

### 2.1 实际步长是 6 tick，不是 30

| 事实 | 证据 |
|---|---|
| 存档默认间隔 6 | `AtmosphereOverhaulSettings.cs:24` `public int simulationIntervalTicks = 6;`，`:177` 存档默认 6 |
| 允许档位 | `:16-23` `AllowedSimulationIntervals = {3, 6, 12, 20, 30}`，`:165-172` getter、`:207-222` 吸附 |
| 何时跑模拟 | `AtmosphereMapComponent.MapComponentTick()`（`:1173`）每 tick 调用；`:1182` `Gen.IsHashIntervalTick(map, simulationIntervalTicks)`；`:1198-1207` 满足且 `!SimulationRunning` 时 `PrepareTemperatureSources()` + `StartSimulationTask()` |
| 实际 elapsed | `StartSimulationTask`（`:2962-2967`）：`elapsedTicksForCycle = lastSynchronized >= 0 ? max(1, synchronizationTick - lastSynchronized) : SimulationIntervalTicks`，上次未完成则跳过、elapsed 累积 |
| 注释陷阱 | `LocalAtmosphereSettings.cs:12` `DefaultSimulationIntervalTicks = 30` 是内部字段，不是实际步长 |

默认 6 tick / 步，每步 `ApplyAirMoversUnsafe` 一次（`:2993` 或 `:3002`，`stepFraction = 1f`；subcycling 在 `:3063`，默认 `PatchSubstepTicks = 6`、elapsed = 6 → 1 子步，等价每周期一次）。

### 2.2 时间常数

- 每步 `exchangedCellVolumes ≤ 0.5` 格体积（硬编码）。
- 64 格房间（8×8，气柱 5 m）单端口、完美混合：128 步 × 6 tick = **768 tick ≈ 12.8 s**。
- GC 原版：每 60 tick 把 80% 温差抹平，τ ≈ `60 / ln(5) ≈ 37 tick ≈ 0.6 s`，且是全屋瞬时。
- 差距约 **20 倍**，远小于 v1 误算的 50–100 倍（v1 用 30 tick）。
- 实际还会更慢，因为 outlet 只混一格，房间其余格靠 AO 扩散/对流。

### 2.3 强度上限的结论

在 m00500 的约束下（不自己控间隔、不依赖 subcycling），**0.5 格体积/步就是天花板**。能做的只有：

1. 接受它是「大功率换气扇」，数值上给足风量，靠长时间运行换气；
2. 用逐建筑滑块让玩家在 0 ~ 0.4167 之间调（见 §5），低于饱和才有可调空间。

v1 列的两条提速路径——改 `SimulationIntervalTicks`、房间级成对写——分别被 m00500、m00486 否决，**不再是候选**。

---

## 3. 单对端口 + 饱和参数（定稿）

每个 `GoodClimate_IE_Active == true` 的 `Building_Vent` 注册一台 AirMover：

| 参数 | 取值 | 依据 |
|---|---|---|
| `inlet` | `Position - Rotation.FacingCell` | GC 的后端（抽气侧） |
| `outlet` | `Position + Rotation.FacingCell` | GC 的前端（送气侧） |
| `InletDirection` / `OutletDirection` | `Rotation.FacingCell`（归一化 Vector2） | 两侧风都朝前端；inlet 侧空气被吸向 vent，outlet 侧被吹向房间 |
| `CubicMetresPerTick` | 0.42（饱和值，气柱 5 m、6 tick） | `0.5 * 5 / 6 = 0.4167`，略高即可拉满 |
| `AirColumnHeightMetres` | 5.0 | 与 ProxyHeat 的 `JetColumnHeight = 5f` 一致 |
| `MaxPressureRisePascals` | 1000 | 约 10 mbar，普通风扇扬程；给 1e6 会变成「无限扬程」，真空也能硬灌 |
| `ConditionExhaust` | `false` | 走混合通道，不是「合成射流」 |
| `activeTicks` | `-1` | `RegisterAirMover` 内部转 `double.MaxValue`（`:200`/`:213`），永不过期 |

**vent 通道保留默认 `SpeedLimit = 10`，不要压到 0.5、更不要设 0**：

- AirMover 的 `IsAirMoverPortOpen` 与（不可压模式的）`AirMoverHasReturnPath` 都依赖 vent 格的 `FaceOpen`；`SetSpeedLimit(0)` 会让 `CompAirVent.IsOpen == false`，`obstacleMask[vent] = 0`，端口断开，AirMover 静默跳过。
- v1 建议压到 0.5 是担心「AO 自然对流抢戏」；实际上 AO 的 vent 通道就是通风口的原生自然对流，与强制搬运叠加才符合物理，压小反而让 AO 的 vent speed 滑块失效、玩家困惑。
- 因此：**保留默认 10，AirMover 作为叠加的强制项**。

---

## 4. 五个边界问题（必须在设计阶段解决）

| # | 问题 | 源码证据 | 后果 | 对策 |
|---|---|---|---|---|
| 1 | **可压模式默认开启，且 `AirMoverHasReturnPath` 不要求连通** | `LocalAtmosphereSettings.cs:166` `DefaultCompressiblePressure = 1f`；1.7 `Defs/AtmosphereTuning.xml:249 <CompressiblePressure>1</CompressiblePressure>`；`LocalAtmosphereEngine.cs:2132-2135` 每步 `compressibleActive = settings.CompressiblePressure > 0`；`AirMoverHasReturnPath`（`:627-762`）可压分支只要求两格分量非 0 | 玩家把 AO 通风口速度拉到 0 或 flick 关，`obstacleMask[vent]=0`，AirMover 仍从 inlet 抽、往 outlet 注 → **气流穿实心墙** | 注册门控加 `CompAirVent.IsOpen`（`SpeedLimit > 0 && FlickUtility.WantsToBeOn(parent)`） |
| 2 | **VR 反转后 AirMover 方向不跟** | `GC_ThingComp.CompGetGizmosExtra` 的 VR 按钮执行 `parent.Rotation = parent.Rotation.Opposite`；`RegisterAirMover` 存 `InletOffset = inlet - owner.Position`（`AtmosphereMapComponent.cs:209-210`），只有 `UpdateAirMover`（`:232-233`）才改 offset | 反转通风口后 GC 的物理朝向变了，AirMover 仍按旧 offset 吹，方向与建筑相反，直到重新注册 | 在 `CompTick`/Postfix 里记录 `parent.Rotation`，变化时 `UpdateAirMover`（新 inlet/outlet）或 `UnregisterAirMovers` + 重新注册。`UpdateAirMover` 首句 `CaptureAirMover`（`:231`）会先结算旧区间，不丢周期 |
| 3 | **抑制 GC 交换后，GC 的真空自动关断也丢了** | `GC_MapComponent.GC_IE_ventIntensiveExchange`（`:34-82`）里 `Vacuum > 0 → comp2.DoFlick() + 发信`；若 Prefix 整体 `return false` 就一起跳过 | 真空/太空里激活 IE，GC 不再自动关断并写信；可压分支下真空格 `ObstacleMask` 仍 >0，AirMover 继续搬 → **真空漏气** | Prefix 先判两侧 `Room.Vacuum > 0`：是 → `return true`（让 GC 原逻辑关断并写信）；否 → `return false`（抑制交换，由 AirMover 接管）。注册门控也加 `Vacuum <= 0` 双保险 |
| 4 | **「两侧产生流动」只有 0.5 m/s，且无法再提高** | `ApplyAirMoversUnsafe:496-510`：`num = exchangedCellVolumes / TimeStep`，`TimeStep = 1f`（`LocalAtmosphereSettings.cs:286`）→ `num ≤ 0.5`；`speed = Mathf.Min(MaxWindSpeed, num)`，`MaxWindSpeed = 120f`（`:314`） | 饱和后风速恒 0.5 m/s，视觉上几乎无感；调大 `CubicMetresPerTick` 不再增加风速 | 接受（0.5 格体积/步 ≈ 0.5 m/s 量级）。若一定要视觉风，只能另加纯视觉粒子，但那是第二条通道，会破坏「只影响端口格」，不建议 |
| 5 | **不可压设置下「大气↔室内」静默失效** | `AirMoverHasReturnPath` 不可压分支：两格不同分量时要求 `airMoverOpenComponents[c1] && airMoverOpenComponents[c2]`（两分量都触边）；封闭室内分量不触边 | 默认 `CompressiblePressure = 1` 不受影响；玩家把它改 0 后，「往大气排/从大气抽」直接 false，无任何提示 | 文档标注「依赖可压模式（默认）」；或在不可压时降级为「只允许同类空间」并给玩家提示 |

另有一个「非错误但要注意」的点：

- **`AtmosphereAirMoverSettings` 是 struct 且没有重写 `Equals`**（`AtmosphereAirMover.cs:13-52`）→ `RegisterAirMover:198` 的 `!airMover.Settings.Equals(config)` 走 `ValueType.Equals` 反射逐字段比较（含装箱）。**只在状态变化时注册**（IE 激活/关闭、Rotation 变化、滑块变化），不要每周期刷，否则每步反射比较 12 字段，违反 AGENTS.md 的性能要求。

---

## 5. 可压 / 不可压（默认可压）

### 5.1 判定链

```
1.7/Defs/AtmosphereTuning.xml:249   <CompressiblePressure>1</CompressiblePressure>
        ↓ AtmosphereTuningManager（常量 TuningRelativePath = "1.6/Defs/AtmosphereTuning.xml"，每 60 tick 热加载）
LocalAtmosphereSettings.CompressiblePressure = 1
        ↓ 每模拟步
LocalAtmosphereEngine.UpdatePressureMode()  :2132-2135
   compressibleActive = settings.CompressiblePressure > 0
```

- 判定是**全局单一系数**，不按房间、屋顶、真空、地图类型分支；默认全图可压。
- 唯一例外是读档：`AtmosphereMapComponent.cs:1659-1663` 解码 `savedCompressibleBytes` 后调 `NotifyCompressiblePressureRestored`（`:2171-2183`），`StoredPressurePa` 非零就强制 `compressibleActive = true`；但下一步 `UpdatePressureMode` 会按设置切回。
- 可压模式内部的 `CompressibleSolver=1` / `CompressibleSkyCoupling=0.2` / `CompressibleAdaptive=0` / `MinSweeps=3` / `MaxSweeps=120` / `Tolerance=0.1` 是**求解器参数**，不是模式切换：`CompressibleSolver >= 0.5` 走 SOR + enclosure deflation（`LocalAtmosphereEngine.cs:2226-2236`），否则纯 Jacobi。

### 5.2 两种模式的行为分叉

| 位置 | 可压 | 不可压 |
|---|---|---|
| 压力解 | `SolveCompressible`（`:2185`），`StoredPressurePa` 是真实 Pa | Jacobi 投影（`:3716-3744`），`Pressure` 是势，密度固定 1.2，`SolverPascalsPerUnit = CurrentSolverPressureToPascals()`（`:2120`） |
| 外部写压力 | `AddStoredPressurePa`（`AtmosphereMapComponent.cs:2059-2060`） | 另一分支（`:2061`） |
| 射流体积 | `Volumes *= max(0, (surface + StoredPressureAt(inlet))/100000)`（`:670-671`）；`SweptPerPa = Requested / max(1, surface + StoredPressureAt(inlet))`（`:725-726`） | 不乘 |
| 屋顶风载 | `(StoredPressureAt(i) - ambientDyn) * scale`（`WindRoofLoad.cs:411-412`） | `coarse1*1000 - 热膨胀基线`（`:407-408`） |
| 时间补丁 | `AtmosphereTuningManager.cs:253-254`：`CompressiblePressure > 0 → EnableTemporalPatches = 0`，**默认可压时时间补丁被关** | 按 `EnableTemporalPatches = 1` 生效 |
| AirMover 连通性 | 只要求两格分量非 0，**不要求连通** | 要求同分量，或两分量都触边 |

### 5.3 对本方案的含义

1. 默认可压 → 问题 1（穿墙）是**默认就会发生**的，`CompAirVent.IsOpen` 门控是必须项。
2. 可压时 `Volumes` 要乘 `(surface + StoredPressureAt(inlet))/100000`：室内常压 ≈1 无感；真空/低压会缩小搬运量，但这**不能替代**显式的 `Vacuum` 门控（低压 ≠ `Room.Vacuum > 0`）。
3. 可压时 `EnableTemporalPatches = 0` → 时间补丁平滑不生效，写场走 `pending*` 直通路径；**不要依赖 `RecordImpulse` 的时间平滑**。

---

## 6. 逐建筑调速滑块

### 6.1 可调什么

唯一有效旋钮是 `AtmosphereAirMoverSettings.CubicMetresPerTick`：

```
amount = CubicMetresPerTick * ElapsedTicks / AirColumnHeightMetres
exchangedCellVolumes = amount * min(1, 0.5 / max(inletDemand, outletDemand))
```

- 线性区上限：`0.5 * AirColumnHeightMetres / 6`（名义 6 tick）。气柱 5 m 时 **0.4167 m³/tick**；超过它 `LimitedVolumes` 卡在 0.5，滑块无效。
- 0–100% 映射 `Cubic = 0.4167 * ratio`，对应每步混合 `1 - exp(-0.5 * ratio)`：ratio=1 → 39%/步，ratio=0.5 → 22%/步，ratio=0 → 停。
- 模拟滞后（`ElapsedTicks` 累积）时高档会饱和，低档仍线性；这是短暂现象，可接受。

### 6.2 实现要点

- 改设置不会丢周期：`RegisterAirMover` 发现 `Settings` 变了会走 `UpdateAirMover`，其首句 `CaptureAirMover(airMover, CurrentGameTick)`（`AtmosphereMapComponent.cs:231`）先结算旧区间，再换 settings。
- UI 用原版 `Dialog_Slider`（只有 int 版），内部 ×100 存 `exchangePercent`，`textGetter` 里除回去显示。
- 挂在 `GC_ThingComp.CompGetGizmosExtra` 的 Postfix，追加一个 `Command_Action`；不覆盖 GC 原有的 IE / VR 按钮。
- **不要复用 AO 的 `GasSliderGizmo`**：它调的是 `CompAirVent.SetSpeedLimit`（`engine.Vents.Limit`，自然对流口径），与本滑块的 `CubicMetresPerTick` 不是一回事。Tooltip 要写清「强制换气强度，不是通风口开度」。
- 只在状态变化时注册（见 §4 末尾的 struct `Equals` 问题）。

---

## 7. 注册门控清单（必须同时满足）

1. `GoodClimate_IE_Active == true`；
2. `CompPowerTrader` 存在且 `PowerOn`（保留 GC 电费语义）；
3. `CompFlickable` 存在且 `SwitchIsOn`；
4. **`CompAirVent.IsOpen`**（`SpeedLimit > 0 && FlickUtility.WantsToBeOn(parent)`）——防问题 1 的穿墙；
5. **两侧 `Room.Vacuum <= 0`**——防问题 3 的真空漏气；
6. 两侧端口格 `IsAirMoverPortOpen`（AO 内部还会再查，提前查可省注册）；
7. `inlet != outlet`（`CaptureAirMover` 与存档清理都会过滤，但提前排除更干净）。

方向：`InletDirection = OutletDirection = parent.Rotation.FacingCell`。

---

## 8. patch 清单

> **注**：本节是「带滑块 + 挂 `CompGcAirRelay`」的草案。不含滑块的最终实现见 §11，其中 patch 2 的抑制方式、真空处理位置、组件挂载方式均以 §11 为准；§6 滑块降级为可选增强。

1. **`GC_ThingComp.GC_IE_ventAllowIntensiveExchange`（Prefix，替换返回值）**：保留 `parent is Building`、`PowerOn`、`SwitchIsOn`、两 Room 非 null、`Vacuum <= 0`；**只放宽 `PsychologicallyOutdoors`**（一侧室外也返回 true）。不要简单 `return true`，否则断电/真空也能开。
2. **`GC_MapComponent.GC_IE_ventIntensiveExchange`（Prefix）**：真空 → `return true`（让 GC 关断并写信）；否则 `return false`（抑制 GC 的 `room2.Temperature += ...`，由 AirMover 接管）。
3. **`CompGcAirRelay`（挂 `Building_Vent`）**：IE 激活时注册 AirMover；关闭时 `UnregisterAirMovers`；检测 `Rotation` 变化重建；滑块值变化走 `UpdateAirMover`。
4. **`GC_ThingComp.CompGetGizmosExtra` Postfix**：追加调速滑块。
5. **门控**：见 §7。

---

## 9. 已否决的路线（留档，避免回潮）

| 路线 | 否决原因 | 出处 |
|---|---|---|
| 房间级成对写（`CompProperties_AoRoomExchange` / `CompRoomFieldExchange`） | AO 没有逐格质量、写整个 Room 不成立；且大房间超 500 格/步预算 | m00486 |
| 改 `SimulationIntervalTicks` / 依赖 subcycling 提速 | 模拟步进完全交给 AO，mod 不能自己控间隔 | m00500 |
| K 条并行端口 / 分布式端口 | 违反 GC 通风口的「只影响两端各一格」语义 | m00506 |
| 把 `CompAirVent.speedLimit` 设为 0（旧真空联动） | 端口 `FaceOpen` 归零，AirMover 静默失效 | 本文 §3 |

---

## 10. 待定

1. 滑块的默认档位（建议 100% 满档，还是 70% 留一点余量）。
2. 功耗如何映射：GC 原 100 W 不变，还是按 `CubicMetresPerTick` 线性缩放（更真实，但要改 `GC_IE_ventUpdatePowerOutput`）。
3. 真空信件是否保留 GC 原文（`GoodClimate_IE_textLetter` 说「必须手动重新开启」）；我们的 Prefix 让真空走原逻辑，所以应自动保留。
4. 端口贴地图边缘的 vent：`IsAirMoverPortOpen` 要求非边缘，降级为「内移一格」还是「禁用并提示」。
5. 是否需要在 gizmo 里显示「AO 接管中」的状态行，避免玩家误以为 GC 的 80%/60 tick 还在生效。

---

## 11. 不加滑块的底层实现（定稿方案）

前提：不做逐建筑调速滑块，参数固定为「刚好打满 0.5 格/步」的饱和值；不新增 `MapComponent`，不新增建筑 `ThingComp`；只用一个反射缓存类 + 一个同时挂 `Prefix`/`Postfix` 的 patch 类。

### 11.1 结构

| 文件 | 职责 |
|---|---|
| `Source/Compat.GoodClimate/GcAirRelay.cs` | 反射缓存、注册/注销、端点门控、真空关断与信件 |
| `Source/Compat.GoodClimate/GcPatches.cs` | `GcVentAllowPatch`、`GcVentExchangePatch` |
| `Source/Compat.GoodClimate/GoodClimateCompat.cs` | `Apply` 内 `PatchAll`，GC/AO 类型缺失时整体跳过 |

为什么不用 `CompGcAirRelay`：注册状态不需要存档，建筑被拆时 AO 自己会在 `PrepareAirMovers` 里剔除失效 owner；组件方案还要处理 `PostSpawnSetup`/`PostDeSpawn`/读档重入，收益为零。

为什么不用新 `MapComponent`：`GC_MapComponent.MapComponentTick` 已经每 60 tick 调一次 `GC_IE_ventIntensiveExchange()`，在这个方法上挂 `Postfix` 就得到 60 tick 的免费扫描节拍，且顺序天然是「先抑制交换、再接管扫描」。Harmony 2 中 `Prefix` 返回 `false` **不会**阻止 `Postfix` 执行，这是本方案成立的前提。

### 11.2 反射缓存

```csharp
internal static class GcAirRelay
{
    internal static readonly Type GcMapComponentType = AccessTools.TypeByName("GOS.GoodClimate.GC_MapComponent");
    internal static readonly Type GcThingCompType   = AccessTools.TypeByName("GOS.GoodClimate.GC_ThingComp");
    internal static readonly Type GcPropsType       = AccessTools.TypeByName("GOS.GoodClimate.GC_CompProperties");

    internal static readonly FieldInfo IeActiveField  = AccessTools.Field(GcThingCompType, "GoodClimate_IE_Active");
    internal static readonly FieldInfo GcVentsField   = AccessTools.Field(GcMapComponentType, "vents");
    internal static readonly FieldInfo LabelLetterField = AccessTools.Field(GcPropsType, "GC_IE_labelLetter");
    internal static readonly FieldInfo TextLetterField  = AccessTools.Field(GcPropsType, "GC_IE_textLetter");

    internal static readonly Type AirVentType = AccessTools.TypeByName("AtmosphereOverhaul.Buildings.CompAirVent");
    internal static readonly PropertyInfo AirVentIsOpen = AccessTools.Property(AirVentType, "IsOpen");

    internal static readonly Type AirMoverSettingsType =
        AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereAirMoverSettings");
    internal static readonly MethodInfo RegisterAirMover =
        AccessTools.Method("AtmosphereOverhaul.RimWorld.AtmosphereInterface:RegisterAirMover");
    internal static readonly MethodInfo UnregisterAirMovers =
        AccessTools.Method("AtmosphereOverhaul.RimWorld.AtmosphereInterface:UnregisterAirMovers");
}
```

- `GoodClimate_IE_Active` 是 **public bool 字段**（`GC_ThingComp.cs:19`），仍用缓存 `FieldInfo` 读，避免反射类型转换开销。
- `GC_CompProperties.GC_IE_labelLetter` / `GC_IE_textLetter` 是 public string（`GC_CompProperties.cs:23-24`）。
- `AtmosphereAirMoverSettings` 是 struct，反射 `Invoke` 的参数必须是**装箱实例**：缓存一个 `boxedSettings`（`Activator.CreateInstance` 一次），之后每次 `FieldInfo.SetValue` 改字段再 `Invoke`；`Invoke` 会 unbox 复制，单线程内安全。
- 复用 `private static readonly object[] RegisterArgs = new object[7]`，避免每 60 tick 分配。

### 11.3 Patch 1：放宽 `GC_IE_ventAllowIntensiveExchange`

只影响「IE 按钮是否可点」，不影响实际交换（交换已被 Patch 2 接管）。

```csharp
[HarmonyPatch]
internal static class GcVentAllowPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(GcAirRelay.GcThingCompType, "GC_IE_ventAllowIntensiveExchange");

    private static bool Prefix(ThingComp __instance, ref bool __result)
    {
        __result = false;
        if (!(__instance is Building b) || b.Map == null) return false;

        CompPowerTrader power = b.GetComp<CompPowerTrader>();
        if (power != null && !power.PowerOn) return false;
        CompFlickable flick = b.GetComp<CompFlickable>();
        if (flick != null && !flick.SwitchIsOn) return false;

        Room roomA = GridsUtility.GetRoom(b.Position + b.Rotation.FacingCell, b.Map);
        Room roomB = GridsUtility.GetRoom(b.Position - b.Rotation.FacingCell, b.Map);
        if (roomA == null || roomB == null) return false;

        // 与原版唯一差异：删掉 !PsychologicallyOutdoors 判断
        __result = roomA.Vacuum <= 0f && roomB.Vacuum <= 0f;
        return false;
    }
}
```

`__instance` 用基类 `ThingComp` 接收，本工程不静态引用 GC 程序集；GC 未装时 `TypeByName` 为 null，`Apply` 里直接跳过。

### 11.4 Patch 2：抑制交换 + Postfix 扫描

```csharp
[HarmonyPatch]
internal static class GcVentExchangePatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(GcAirRelay.GcMapComponentType, "GC_IE_ventIntensiveExchange");

    private static bool Prefix() => false;

    private static void Postfix(MapComponent __instance)
    {
        if (__instance?.map != null) GcAirRelay.Scan(__instance.map);
    }
}
```

真空语义必须搬进 `Scan`：GC 原方法在真空时会 `DoFlick()` 并写 `NegativeEvent` 信，`Prefix` 一抑制就丢。去重靠「`DoFlick` 先把 `SwitchIsOn` 关掉 → 下一轮电源/开关检查直接返回」，玩家手动重开后才可能再次触发。

### 11.5 扫描与生命周期

```csharp
private static readonly Dictionary<int, Building> registered = new Dictionary<int, Building>();
private static readonly Dictionary<int, Rot4> directions = new Dictionary<int, Rot4>();
private static readonly HashSet<int> seen = new HashSet<int>();
private static readonly List<int> stale = new List<int>();

internal static void Scan(Map map)
{
    // 直接读 GC 自己的 vents 注册表（List<GC_ThingComp>，private），
    // 省掉 listerBuildings 两次全量遍历与 HasComp 过滤。
    object gcMapComp = map.GetComponent(GcMapComponentType);
    IList vents = gcMapComp == null ? null : GcVentsField.GetValue(gcMapComp) as IList;
    if (vents == null) return;

    seen.Clear();
    for (int i = 0; i < vents.Count; i++)
    {
        ThingComp vent = vents[i] as ThingComp;
        Building b = vent == null ? null : vent.parent as Building;
        if (b == null || b.Destroyed || !b.Spawned || b.Map != map) continue;
        seen.Add(b.thingIDNumber);
        Consider(map, vent, b, mask);
    }

    // 收集后统一注销，避免遍历中改字典
    stale.Clear();
    foreach (var pair in registered)
        if (!seen.Contains(pair.Key)) stale.Add(pair.Key);
    for (int i = 0; i < stale.Count; i++)
    {
        if (registered.TryGetValue(stale[i], out Building b)) Unregister(b);
        registered.Remove(stale[i]);
        directions.Remove(stale[i]);
    }
}

private static void Consider(Building b)
{
    int id = b.thingIDNumber;
    object gcComp = FindGcComp(b);
    if (gcComp == null || !(bool)IeActiveField.GetValue(gcComp)) { Unregister(b); return; }

    CompPowerTrader power = b.GetComp<CompPowerTrader>();
    CompFlickable flick = b.GetComp<CompFlickable>();
    if ((power != null && !power.PowerOn) || (flick != null && !flick.SwitchIsOn)) { Unregister(b); return; }

    // 关键门控：vent 关着时禁止穿墙（可压模式下 AirMoverHasReturnPath 不要求连通）
    if (!(bool)AirVentIsOpen.GetValue(b.GetComp(AirVentType))) { Unregister(b); return; }

    IntVec3 inlet  = b.Position + b.Rotation.FacingCell;
    IntVec3 outlet = b.Position - b.Rotation.FacingCell;
    Room roomA = GridsUtility.GetRoom(inlet,  b.Map);
    Room roomB = GridsUtility.GetRoom(outlet, b.Map);

    if ((roomA != null && roomA.Vacuum > 0f) || (roomB != null && roomB.Vacuum > 0f))
    {
        flick?.DoFlick();
        SendVacuumLetter(b, gcComp);
        Unregister(b);
        return;
    }

    if (!PortLooksOpen(b.Map, inlet) || !PortLooksOpen(b.Map, outlet)) { Unregister(b); return; }

    if (registered.ContainsKey(id))
    {
        if (directions[id] == b.Rotation) return;   // 无变化
        Unregister(b);                              // 方向变了：先结算旧区间
    }
    Register(b, inlet, outlet);
    registered.Add(id, b);
    directions[id] = b.Rotation;
}
```

注册调用：

```csharp
RegisterArgs[0] = b.Map;
RegisterArgs[1] = b;
RegisterArgs[2] = "GC_IE_" + b.thingIDNumber;
RegisterArgs[3] = inlet;
RegisterArgs[4] = outlet;
RegisterArgs[5] = boxedSettings;   // 已 SetValue 的装箱 struct
RegisterArgs[6] = -1f;             // 永不过期 → ValidUntilTick = double.MaxValue
RegisterAirMover.Invoke(null, RegisterArgs);
```

`RegisterAirMover` 自己会校验 `owner.Spawned && !Destroyed && owner.Map == map`、`settings.Valid`、`inlet != outlet`、`GenGrid.InBounds`，所以这些检查可以省略；但 `CompAirVent.IsOpen` 不能省。

### 11.6 参数（固定饱和值）

```csharp
// 按 AO 允许的最小档位 3 tick 取值；6/12/20/30 tick 时由 LimitedVolumes 截断到 0.5 格/步。
private const float AirColumnHeight = 5f;
private const float CubicMetresPerTick = 0.5f * AirColumnHeight / 3f;   // ≈ 0.8333
```

- `CubicMetresPerTick = CubicMetresPerTick`、`AirColumnHeightMetres = 5f`、`MaxPressureRisePascals = 1000f`、`ConditionExhaust = false`，其余字段 0。
- `InletDirection = new Vector2(facing.x, facing.z)`、`OutletDirection = -InletDirection`（取反）。
- 为什么不按当前档位动态算：低档位（3 tick）需要 0.8333，高档位多出来的部分被 `LimitedVolumes` 截断，效果仍是每步 0.5 格；固定成 0.8333 可以避免玩家改 AO 档位时重新注册。

### 11.7 边界

| 边界 | 处理 |
|---|---|
| VR 反转 | `directions[id] != b.Rotation` → 先 `Unregister` 再 `Register`；AO 的 `UpdateAirMover` 不会自己改 offset |
| 拆除 | `ScanList` 只收 `Spawned && !Destroyed`；残留 id 走 `Unregister(map, b)`，对失效 owner 返回 0 |
| 读档 | 不存档任何状态；60 tick 内按 `GoodClimate_IE_Active` 重建，注册幂等 |
| 多线程 | `Postfix`/`Register` 全在主线程；`PrepareAirMovers` 也在主线程，后台 Task 只读 `preparedAirMovers` |
| 性能 | 反射静态缓存；`RegisterArgs`/`boxedSettings`/`scratch`/两个 `HashSet` 复用；每 60 tick 一次，几百建筑可忽略 |
| `PortLooksOpen` | 用 `AtmosphereFieldBridge` 的 `ObstacleMask` 自查（`> 1/1000`、非地图边缘、四邻 `FaceOpen > 0`），不反射 `internal` 的 `IsAirMoverPortOpen` |
| 真空信 | `LabelLetterField`/`TextLetterField` 读 GC `Props`，`LetterMaker.MakeLetter(label, text, LetterDefOf.NegativeEvent, new LookTargets(b))` + `Find.LetterStack.ReceiveLetter` |

### 11.8 固有限制（本方案不解决）

1. **风速上限 0.5 m/s**：`num = exchangedCellVolumes / TimeStep`，`TimeStep = 1f`，`exchangedCellVolumes ≤ 0.5`，所以 `speed = min(120, 0.5) = 0.5`。加大 `CubicMetresPerTick` 只会更快饱和，不会更大。
2. **不可压设置下室内↔大气失效**：`AirMoverHasReturnPath` 非可压分支要求两格同分量或都触边；默认 `CompressiblePressure = 1` 所以正常玩不受影响，玩家关掉可压时装置自动失效。
3. **电费与搬运量脱钩**：GC 的 `PowerOutput = -100W` 保留，搬多少都是 100 W。
4. **气体成分/氧气异步**：`CompleteAirMoverGasTransfers` 交 `gasManager.QueueAirMoverTransfers`，可能晚一个周期；温度/湿度/雾同周期。
5. **不再重复注册**：只在状态变化时调 `RegisterAirMover`，避免 `ValueType.Equals` 每周期反射比较 12 个字段。

### 11.9 与 §6/§8 的关系

- §6 滑块是可选增强：若要加，只需在 `Consider` 里把 `CubicMetresPerTick` 乘 `ratio`，并在比值变化时 `Unregister`+`Register`（或走 `UpdateAirMover`）。
- §8 第 2 条「真空 → `return true`」不再采用：改为整体 `return false`、真空在 `Scan` 里处理，避免「部分放行」造成的逐 vent 语义分裂。
- §8 第 3 条「挂 `CompGcAirRelay`」不再采用：改由 `Postfix` 扫描，免存档、免生命周期。

---

## 12. 定向喷流实现（v3 定稿，取代 §11 的 AirMover 普通模式）

### 12.1 目标语义

强力通风口 = 一台「风机 + 喷嘴」：开启后从入口格抽气、从出口格定向喷出，体积守恒地搬运入口空气的温度（带下限）、湿度、氧气，并在两侧产生定向风；同时关闭该格的自然通风通道，避免 AO 的 Vent 自然流反向短路。

选择喷流而不是普通 AirMover 的原因：

- 普通 AirMover 的 `LimitedVolumes` 硬顶 0.5 格体积/步，风速封顶 0.5 m/s，调 `CubicMetresPerTick` 无效；
- 喷流走 `ApplyJetTransportUnsafe`，用 `LastSubsteps` 拆分，总流量上限 256 格/步；
- 喷流方向由 `OutletDirection` 固定，沿 `Direction` 建 path 并做动量，天然定向；
- 喷流的 `AddAirMoverVolumeRate(inlet, outlet, ...)` 成对写入，体积守恒。

### 12.2 自然通风开关

- 每个 Vent 的「允许自然通风」状态编码在 `CompAirVent.speedLimit`：`0` = 关闭，`>0` = 允许（值即自然通风速度上限）。
- **默认关闭**：新放置或旧档未初始化（`speedLimit < 0`）的 Vent，由 `Building.SpawnSetup` Postfix 立即置 0，不等 60 tick 扫描。
- **按钮**：`Command_Toggle`，追加在 `GC_ThingComp.CompGetGizmosExtra` 之后，标签「自然通风」。
- **联动**：GC 强力模式 active 时按钮禁用（`disabledReason`），并强制 `speedLimit = 0`；关闭强力模式后仍保持 0，玩家需手动点按钮才允许自然通风。
- **恢复上次值**：`EnsureNaturalVentOff` 关闭前把当前 `SpeedLimit` 存入 `SavedSpeed`，按钮打开时恢复该值（从未设过则用 10）。
- 注意：`IsIntensiveActive` 必须传**父建筑**（`GC_ThingComp` 在建筑上），不能传 `CompAirVent`——否则 `FieldInfo.GetValue` 抛 `Object does not match target type`，按钮永远不会禁用，点击后被扫描覆盖回 0，表现为「按钮无效」。
- 修改 `speedLimit` 后必须调 `AtmosphereMapComponent.MarkCachedMapDataDirty()`：`IsOpen` 决定 `BuildVents` 是否把该格写入 `engine.Vents`，也决定 `AirCanMoveThrough` 返回 1 还是 0（vent 格实心/可通行）。

### 12.3 喷流参数

**限流（关键）**：`CubicMetresPerTick` 按两侧最小室内房间格数动态限制为
`0.25 * cells * AirColumnHeight / 6`，即每模拟步交换体积不超过房间体积的 25%。
否则 `1-exp(-V)` 在 V=12 格时已接近 100%，两个反向喷流会每步把对方出口格完全替换，
形成 2 周期交替振荡。两侧都是室外（大气）时不限流。

| 字段 | 值 | 说明 |
|---|---|---|
| `ConditionExhaust` | `true` | 走 `ApplyJetTransportUnsafe`，与普通 AirMover 互斥 |
| `CubicMetresPerTick` | `10f` | `Volumes = min(1000, C * elapsed / 5)`；默认 6 tick → 12 格体积/步 |
| `AirColumnHeightMetres` | `5f` | 与 AO 气柱高一致 |
| `InletDirection` | `facing` | 入口在 `Position + facing` |
| `OutletDirection` | `-facing` | 出口在 `Position - facing`，方向从入口指向出口 |
| `ExhaustSpeed` | `10f` | 射流风速，同时决定 substeps（`max(speed*2, demand*2)`） |
| `InletPressureRatio` | `0f` | 压差头 = `surfacePressurePa * (Exhaust - Inlet)` |
| `ExhaustPressureRatio` | `0.02f` | 2000 Pa 压差头，驱动 `AddNozzlePump` |
| `ExhaustTemperatureFloor` | 入口温度（量化 1℃） | 喷出温度下限；AO 会再 `max(ambient, 入口周边均值)` |
| `ExhaustOxygenPercent` | 入口氧（量化 1%） | 喷出氧浓度 |
| `WaterKgPerTick` | `0f` | 不额外加水；湿靠 `Moisture.Read[inlet]` 实测混合 |
| `MaxPressureRisePascals` | `1000f` | 非喷流字段，占位 |

### 12.4 回填与时机

- 每 60 tick（GC 的 `GC_IE_ventIntensiveExchange` 节拍）读入口格温度与氧：温度用 `AtmosphereFieldBridge.GetTemperatureRead(engine)[index]`，氧用 `AoBridge.TryGetOxygenPercent(map, inlet, out float)`。
- 量化到 1℃ / 1% 后与已注册参数比较，只有跨档才 `RegisterAirMover`（AO 内部走 `UpdateAirMover`）。
- 量化是为了避免 `Settings` 每周期变化：`UpdateAirMover` 会先 `CaptureAirMover` 把 `mover.LastTick = CurrentGameTick`，同周期再 Capture 时 `ActiveTicks = 0`，那一周期零流量。
- `activeTicks = -1f` → `ValidUntilTick = double.MaxValue`，不自动过期；拆除时由 AO 按 `Owner.Spawned/Destroyed` 剔除，我们另在 `Stale` 分支 `UnregisterAirMovers`。

### 12.5 门控顺序

1. `GoodClimate_IE_Active == true`；
2. `CompPowerTrader.PowerOn`（存在时）；
3. `CompFlickable.SwitchIsOn`（存在时）；
4. 两侧 `Room.Vacuum <= 0`（任一真空 → `DoFlick()` + 复刻 GC 负事件信 + 注销）；
5. `EnsureNaturalVentOff(building)`：`SpeedLimit > 0` 时反射调 `SetSpeedLimit(0)`（AO 内部已 `MarkCachedMapDataDirty()`，无需重复通知）；
6. `PortLooksOpen(inlet)` 与 `PortLooksOpen(outlet)`：`ObstacleMask > 0.001`、非地图边缘；
7. 注册/刷新喷流；方向变化时先注销旧区间再注册。

**不再要求 `CompAirVent.IsOpen`**：自然通风关闭（`speedLimit = 0`）正是本方案的目标状态，若仍要求 `IsOpen` 会永久拒绝注册。

### 12.6 已知限制

- **温度下限**：`ExhaustTemperatureFloor` 被 AO 抬到 `max(ambient, 入口周边均值)`，冷气搬不过去；这是喷流的物理假设，不是 bug。要精确搬运低温只能改用 AirMover 普通模式或自研体积源。
- **反向双喷会振荡**：两台喷流对吹时，若每步交换量超过房间体积的 50%，闭环特征值变负，温度会在两房间之间交替翻转；已被 12.3 的 25% 限流压住。
- 喷流沿 path 做 `Exchange`，射流会与路径环境混合，方向性随距离衰减。
- 出口沿 `facing` 必须有可通行的下一格（`JetTransport.HasOpenPath`），贴墙/贴山体时静默无流量。
- 60 tick 回填一次，温度快速变化时喷出温度会滞后一个节拍。
- 喷流用 `ExhaustOxygenPercent` 参数而非入口实测，需回填才接近「搬运」。

### 12.7 实现清单

| 文件 | 改动 |
|---|---|
| `Source/JeremieAtmosphereOverhaulPatch/Bridge/AtmosphereFieldBridge.cs` | 新增 `MarkCachedMapDataDirty(Map)` 反射封装 |
| `Source/Compat.GoodClimate/GcAirRelay.cs` | 反射 `speedLimit` 字段 / `SpeedLimit` 属性 / `SetSpeedLimit`；`EnsureNaturalVentOff`；喷流参数与温/氧回填；门控去掉 `IsOpen` |
| `Source/Compat.GoodClimate/GcPatches.cs` | 新增 `CompGetGizmosExtra` Postfix 追加自然通风按钮；`Building.SpawnSetup` Postfix 让新通风口立即置 0 |
| `Languages/ChineseSimplified (简体中文)/Keyed/JeremieAOPatch.xml` | 按钮标签 / 描述 / 禁用原因 |
| `Languages/English/Keyed/JeremieAOPatch.xml` | 同上英文 |

### 12.8 与 §11 的差异

§11 的 AirMover 普通模式（`ConditionExhaust=false`）被本方案取代：0.5 格/步限流、方向由压差决定、且只要 vent 保持 `IsOpen` 就会与自然通道互相短路。§11 中「SetSpeedLimit(0) + 自研体积源」的讨论保留为备选路线，仅当喷流的「喷出温度下限」无法接受时才考虑。

---

## 13. 喷流 + 端口源叠加（v5 方案）与性能核算

> 本节是 m01586 提出的叠加方案：在通风口**输出端口格**同时挂一个连续源和一台喷流，用源补温度/氧，用喷流搬体积/湿度/风。§12 的纯喷流方案仍有效，本节是它的增强版。

### 13.1 为什么叠加

§12 的纯喷流有两个固有缺陷：

1. 喷出温度受 `num9 = max(室外温度, 入口邻格均温, ExhaustTemperatureFloor)` 限制（`AtmosphereMapComponent.cs:633-651`），低温端搬不过去；
2. 温度混合与体积绑定（`fraction = 1-exp(-V)`，`JetTransport.cs:57-63`），大风量必然带来大温度阶跃。

源通道（`SourceSlots` → `ApplyContinuousSourcesUnsafe`）恰好能补这两点：`TemperaturePerTick` 直接写该格，无室外下限、无整格混合。两者叠加后：

- 喷流负责「搬空气」：体积、湿度、风、压差；
- 源负责「搬能量」：温度、氧。

### 13.2 分工（必须严格，否则三处打架）

| 物理量 | 负责方 | 依据 |
|---|---|---|
| 体积/质量 | 喷流 | `AddAirMoverVolumeRate`（`LocalAtmosphereEngine.cs:801-805`）是唯一守恒体积通道 |
| 湿度/雾 | 喷流 | `JetTransport.cs:61-62` 已按 `Moisture.Read[inlet]` 实测搬运 |
| 氧 | 源 `OxygenPercentPerTick` | 喷流的 `ExhaustOxygenPercent` 是设置值（`:63`），源可精确修正 |
| 温度 | 源 `TemperaturePerTick` | 喷流有室外下限且整格混合 |
| 风 | 喷流 | `AddAirMoverMomentum` 是真正的射流；源不要再开 `MaintainTargetWind` |
| 压力 | 喷流 | `AddNozzlePump` 已建立压差；源不要重复写 `PressurePerTick` |

**一个槽即可同时承载温度与风**：`AtmosphereSourceRates` 同时含 `TemperaturePerTick`、`WindPerTick`、`MaintainTargetWind`、`TargetWind`、`TargetWindBlendPerTick`、`PressurePerTick`、`OxygenPercentPerTick`（`RimWorld/AtmosphereSourceRates.cs:12-24`），不需要两个源。

### 13.3 执行顺序（决定谁覆盖谁）

`AtmosphereMapComponent.cs:2991-3012`：

```
AdvanceProvisional
ApplyAirMoversUnsafe              // 非喷流
ApplyJetTransportUnsafe           // 喷流：体积/湿/氧/动量（2994/3003）
ProjectAndFinalize                // 压力投影（2995/3004）
ApplyWallLeakage
CompleteJetTransportUnsafe
ApplyVelocityDeltasAfterSolver
ApplyTargetWindSourcesAfterSolverUnsafe   // 源的目标风（3010）
LocalForcing.AfterSolve
```

结论：

- 喷流动量写在 `ProjectAndFinalize` **之前**，会被投影削弱一部分；
- 源的目标风写在投影**之后**，保留更直接，但**会覆盖喷流刚写的速度**（按 `1-(1-blend)^ticks` 向 `TargetWind` 混合）；
- 所以风只能选一方。选喷流，源不设 `MaintainTargetWind` 与 `WindPerTick`。

### 13.4 温度闭环

喷流温度混合：`T_out' = T_out + (1-exp(-V)) * (num9 - T_out)`。把 `ExhaustTemperatureFloor` 设成出口当前温度并量化后：

- `T_out ≥ 室外` 且 `T_out ≥ 入口邻格均温` 时，`num9 = T_out`，喷流「等温搬体积」，温度净变化为 0；
- 入口更热时 `num9 = 入口邻格均温`，喷流仍会额外加热出口，这一步无法预知。

因此温度**不要开环对账**，用闭环（每模拟周期末尾读一次实际值）：

```csharp
// 每模拟周期（6 tick）执行一次
float actual = ReadTemperature(map, outlet);          // AtmosphereInterface.TryGetTemperature
float delta  = targetDelta - (actual - lastOutlet);   // targetDelta = 本步希望出口变化的量
AddTemperatureDelta(map, outlet,  delta);
AddTemperatureDelta(map, inlet,  -delta);              // 成对，守恒
lastOutlet = actual;
```

喷流多混了多少，下一周期反向补多少，不依赖 `num9` 的具体数值。

### 13.5 参数

| 参数 | 值 | 说明 |
|---|---|---|
| `CubicMetresPerTick` | 按 ΔT 上限动态算 | `α_max = min(0.5, 2/|T_in-T_out|)`，`V = -ln(1-α_max)`，`C = V*AirColumnHeight/6`，clamp `[0.5,10]` |
| `AirColumnHeightMetres` | 5 | 与 §12 一致 |
| `ExhaustSpeed` | 10 | 喷流风 |
| `ExhaustPressureRatio` / `InletPressureRatio` | 0.02 / 0 | 与 §12 一致 |
| `ExhaustTemperatureFloor` | 出口当前温度（量化 1℃） | 让喷流尽量等温 |
| `ExhaustOxygenPercent` | 入口氧（量化 1%） | 与源二选一，建议交给源 |
| `targetDelta`（源温度率） | `k * (T_in - T_out)`，`k≈0.15` | 每周期按温差写 |
| `OxygenPercentPerTick` | `k_o * (O2_in - O2_out)` | 同上 |

### 13.6 性能核算

| 项 | 每模拟周期成本 | 说明 |
|---|---|---|
| `ApplyJetTransportUnsafe` | `O(源数)` 次 `new Dictionary` + 每源 `new Source` | `:605` 每步新建字典；2 台 ≈ 2 个小对象/步 |
| `JetTransport.Advance` | `LastSubsteps × 每源` | `LastSubsteps = min(512, max(1, ceil(max(speed*TimeStep*2, demandMax*2))))`；`ExhaustSpeed=10`、`Volumes≈12` → 约 24 子步；每子步 4 次 `Mix` + path（约 12 格）× 3 通道 `Exchange` → 约 960 次数组操作/源/步 |
| `ApplyContinuousSourcesUnsafe` | `O(槽数)` | 每槽约 10 次数组写；无分配（坏源时才 `new List`） |
| `ApplyTargetWindSourcesAfterSolverUnsafe` | `O(槽数)` | 未开 `MaintainTargetWind` 则整段跳过 |
| `ConsiderContinuousSourcesForPatchUnsafe` | `O(槽数)` | `EnableTemporalPatches<=0` 时 `ConsiderCell` 立即返回（`:138`） |
| `AddTemperatureDelta` ×2/源 | 加锁 + `PatchSourceJournal.Record` 字典查找 | `EnableTemporalPatches<=0` 时 `domain=null`（`:172`），但 `RebaseAfterGlobalProjection` **无条件** `DiscardThrough(synchronizationTick)`（`:362`），journal 不会累积 |
| 掩膜重建 | 仅 `masksDirty` 时全图 O(N) | 与源/喷流注册次数相关，注册只在状态变化时发生 |

按 2–3 台通风口、默认 6 tick/步估算：

- 喷流是唯一大头，约 24 子步 × 2 源 ≈ 5 千次数组操作/步；
- 源部分约 40–60 次数组写/步；
- `AddTemperatureDelta` 每步 4 次加锁，量级可忽略；
- 整体 < 0.1 ms/步（估算），远低于 AO 主求解的 Poisson/压力迭代。

结论：**性能不是瓶颈**。真正需要控制的是注册频率——`AtmosphereAirMoverSettings` 是 12 字段 struct 且未重写 `Equals`，`RegisterAirMover` 内部 `!Settings.Equals` 会走 `ValueType.Equals` 反射逐字段比较（含装箱），所以只在开关/旋转/参数变化时注册，不要每周期重注册。

### 13.7 已知问题

1. **「等温搬体积」不是严格守恒**：AO 的温度场只存温度不存焓，喷流在 `num9=T_out` 时搬了体积/质量但出口温度不变，等于没有随质量搬运焓。AO 自己的 AirMover 也是这个近似。
2. **入口更热时喷流仍加热出口**：只能靠 13.4 的闭环补偿，不能消除。
3. **两套句柄**：`RegisterAirMover`（owner+key）与 `RegisterContinuousSource`（返回 `AtmosphereSourceHandle`）必须分别维护；`AtmosphereSourceHandle` 是 public struct，`RegisterContinuousSource`/`UpdateContinuousSource`/`UnregisterContinuousSource` 都是 public static（`AtmosphereInterface.cs:300-357`），但本工程不静态引用 AO，需反射构造 struct 并装箱传参。
4. **双反向振荡残余耦合**：源闭环温度稳定，但喷流体积搬运仍会让两侧质量/压力交替变化；`num9` 跟随入口邻格时两个反向喷流仍有弱耦合。限流（13.5 的 ΔT 上限）仍要做。
5. **源写温度不会扩散到整房间**：只写端口格，靠 AO 扩散摊开；大房间整体变化慢，但不会过冲。

### 13.8 实现清单（2026-11-14 已落码）

| 文件 | 改动 | 状态 |
|---|---|---|
| `Source/JeremieAtmosphereOverhaulPatch/Bridge/AtmosphereFieldBridge.cs` | 新增 `SourceRates` struct、`RegisterContinuousSource`/`UpdateContinuousSource`/`UnregisterContinuousSources`/`AddTemperatureDelta` 反射封装，以及 `SourceAvailable`/`SourceFailure` | 已完成 |
| `Source/Compat.GoodClimate/GcAirRelay.cs` | 每 vent 两槽连续源（`SIN_`/`SOUT_`，只写氧）；`RelayTemperature` 每模拟周期成对注入温度（`TemperatureRelayRate=0.15`、单步上限 2℃）；`ExhaustTemperatureFloor`/`ExhaustOxygenPercent` 改用出口值；限流改 `α_max=min(0.5,2/ΔT)`、不再看房间格数；`Unregister` 同步注销源与句柄 | 已完成 |
| `Source/Compat.GoodClimate/GcPatches.cs` | 新增 `GcTemperatureRelayPatch`（Postfix `AtmosphereMapComponent.PrepareTemperatureSources` → `GcAirRelay.RelayTemperature`）；其余 patch 不变 | 已完成 |
| `Languages/*/Keyed/JeremieAOPatch.xml` | 不需要新键（自然通风按钮沿用 §12） | 不变 |

编译：主工程 `Assemblies/JeremieAtmosphereOverhaulPatch.dll` 72192 字节、GC 兼容 `1.7/Compat/GoodClimate/Assemblies/Jeremie.AtmosphereOverhaulPatch.GoodClimate.dll` 23040 字节，均 0 警告 0 错误。

注意：`Compat.GoodClimate.csproj` 引用的是**已编译的**主工程 dll，改 `AtmosphereFieldBridge` 后必须先编主工程、再编 GC 兼容，否则新成员不可见。

---

## 附：关键代码位置索引

| 内容 | 位置 |
|---|---|
| `simulationIntervalTicks` 默认 6 / 档位 | `AtmosphereOverhaulSettings.cs:16-24`、`:165-177`、`:207-222` |
| `MapComponentTick` 的模拟调度 | `RimWorld/AtmosphereMapComponent.cs:1173-1207` |
| `StartSimulationTask` 的 elapsed 累积 | 同上 `:2962-2967` |
| 非 subcycling 分支（`ApplyAirMoversUnsafe` 调用点） | 同上 `:2985-3006` |
| subcycling 分支 | 同上 `:3059-3063` |
| `RegisterAirMover` / `activeTicks<0 → MaxValue` / offset 存储 | 同上 `:185-217` |
| `UpdateAirMover`（先 `CaptureAirMover`） | 同上 `:219-239` |
| `UnregisterAirMover(s)` | 同上 `:241-267` |
| `CaptureAirMover` | 同上 `:269-286` |
| `PrepareAirMovers` | 同上 `:430-444` |
| `ApplyAirMoversUnsafe`（非喷流） | 同上 `:472-552` |
| `ApplyJetTransportUnsafe`（喷流） | 同上 `:597-` |
| `CompleteAirMoverGasTransfers`（氧气异步） | 同上 `:896-901` |
| 可压/不可压判定 `UpdatePressureMode` | `Simulation/LocalAtmosphereEngine.cs:2132-2162` |
| 读档恢复 `NotifyCompressiblePressureRestored` | 同上 `:2171-2183` |
| `SolveCompressible` | 同上 `:2185-` |
| 不可压 Jacobi 投影 | 同上 `:3716-3744` |
| `AirMoverHasReturnPath` | 同上 `:627-762` |
| `IsAirMoverPortOpen` | 同上 `:792-799` |
| `AddAirMoverVolumeRate` | 同上 `:801-805` |
| `AirMoverPortImpulse` | 同上 `:888-895` |
| `AirMoverTransport`（Flow / LimitedVolumes / Mix / ActiveTicks） | `Simulation/AirMoverTransport.cs:12-47` |
| `AtmosphereAirMoverSettings`（struct，无 `Equals` 重写） | `RimWorld/AtmosphereAirMover.cs:13-52` |
| `TimeStep = 1f` / `PressureAccelerationScale = 2f` / `MaxWindSpeed = 120f` | `Simulation/LocalAtmosphereSettings.cs:286`、`:288`、`:314` |
| `DefaultCompressiblePressure = 1f` | 同上 `:166`、`:457` |
| 可压时 `EnableTemporalPatches = 0` | `Simulation/AtmosphereTuningManager.cs:253-254` |
| `BuildVents`（`CompAirVent.IsOpen` 与 `faceOpen` 清零规则） | `RimWorld/MapObstacleMaskBuilder.cs:86-161` |
| 户外松弛 `OutdoorTemperatureRelaxation = 0.15` | `Simulation/LocalAtmosphereSettings.cs:35` |
| `PreparePendingFieldDeltasUnsafe`（户外松弛） | `RimWorld/AtmosphereMapComponent.cs:3360-3438` |
| GC 按钮门控 | `GC_ThingComp.cs:51-122` |
| GC 交换逻辑 | `GC_MapComponent.cs:18-82` |
| 本项目喷流参数参考 | `Source/JeremieAtmosphereOverhaulPatch/ProxyHeat/ProxyHeatRelayMapComponent.cs:1356-1362`（`CubicMetresPerTick = speed/60`、`AirColumnHeightMetres = JetColumnHeight = 5f`、`MaxPressureRisePascals = 1e6`） |

---
