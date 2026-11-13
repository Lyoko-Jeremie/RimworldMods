# AO · ProxyHeat 替代机制设计决策

> 状态：**已落码并实机确认**（2026-11-13，v1.9）。全部设计决策已拍板，`dotnet build -c Debug` 0 警告 0 错误，用户 m02400 确认效果良好。演进过程与验收记录见 §12.4 / §14。
> 取代关系：本文取代 `AO-ProxyHeat接管与三模式设计.md` 的「结论」与「实现方案」部分。旧文档保留为调研记录（其 §5 物理推导、§6 温度语义仍有效），但其中 **§5.11.3「接管并移除 AO 点源」已被推翻**——最终方案是点源保留、范围效果作为额外层共存。
> 依据：AO 1.7 源码（`F:\RimworldCodeIL\AtmosphereOverhaul\`，MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、ProxyHeat 源码（`F:\RimworldCodeIL\ProxyHeat\`，MVID `7358A296-A2E2-46F8-A5D3-4C48DE897B1C`）、Frostpunk Heat Generator Tower Def（`294100/3545379519`）。

---

## 0. 一句话

**保留 AO 自己的热源系统，另外做一层「把 ProxyHeat 的读数魔法翻译成 AO 场里的真实热源」的转译器；频率、积分、扩散、耦合全部交给 AO，转译器只负责每隔一个模拟周期往 AO 的入队数组里加一笔增量。喷流模式额外提供风，但同样只往 AO 的压力通道里塞参数，热量仍走面注入。**

---

## 1. 需求与边界

### 1.1 用户原话（m00160）

> 「我们要让我们的补丁存在时 禁用 ProxyHeat 。但我们不干预原始加热器在无ProxyHeat 状态下的行为。也就是说，我们要实现一套替代 ProxyHeat 的机制，使其既带有ProxyHeat 的范围生效特效，可以在室外使得加热器也可以影响附近一定距离范围内的温度（以AO的模式），又不影响加热器本身的功能，使其能够正常在屋顶下以AO模式工作，并提供开关让加热器可以选择是否在室内也影响附近一定距离范围内的温度。」

### 1.2 三条硬约束

1. **不干预原版行为**：不修改原版 `Building_TempControl.TickRare`、不移除 AO 的 `CompHeatSourceAtmosphere` 点源。范围效果是「多出来的一层」，不是替换。
2. **物理交给 AO**：不自己定 tick 频率、不自己实现积分/扩散。转译器只提供「每周期该加多少度」这个外部热源项，其余由 AO 的 `LocalAtmosphereEngine` 演化。
3. **玩家可控**：室内是否生效逐建筑开关；范围强度在 mod 设置里暴露；保留 ProxyHeat 原设置面板。

### 1.3 与旧方案（文档 v2）的差异

| 项 | v2 旧方案 | 最终方案 |
|---|---|---|
| AO 点源 | 移除（`RemoveAt` 掉 `CompProperties_HeatSourceAtmosphere`） | **保留**，与范围层共存 |
| ProxyHeat 禁用 | Prefix `GetTemperatureOutcomeFor` 返回入参 + 强制 `enableProxyHeat=true` | **强制 `enableProxyHeat=false`**（18 处 setting 判断全短路） |
| 注入通道 | 逐格 `AddTemperatureDelta` / `RegisterContinuousSource` | **Postfix `PrepareTemperatureSources` 直接写 `pending`/`GroundTemperature`** |
| 多源重叠 | 未定 | **sum**（不是 ProxyHeat 原版 Average） |
| 室内 | 全局开关 | **逐 Thing 开关** + 全局默认值 |
| 融雪 | 待实测 | **不管**，交原版雪机制 |

---

## 2. 总体架构

### 2.1 组件清单

| 文件 | 职责 |
|---|---|
| `Source/JeremieAtmosphereOverhaulPatch/Bridge/AtmosphereFieldBridge.cs` | 反射 AO：`AtmosphereMapComponent.engine` / `pendingTemperatureDeltas`、`LocalAtmosphereEngine.Size/Width/Temperature/GroundTemperature/ObstacleMask`、`AtmosphereField.Read`；以及 `AtmosphereAirMoverSettings`（12 字段）与 `AtmosphereInterface.RegisterAirMover` / `UnregisterAirMovers` |
| `ProxyHeat/ProxyHeatBridge.cs` | 反射 ProxyHeat：`ProxyHeatMod.settings`（internal）、`ProxyHeatManager.compTemperatures` / `compTemperaturesToTick`、`CompTemperatureSource` 的 `Props` / `AffectedCells` / `active` / `ShouldBeActive` / `SetActive` / `MarkDirty` / `CanWorkIn` |
| `ProxyHeat/ProxyHeatRelayMapComponent.cs` | `MapComponent`：持有全部源状态、每模拟周期注入、喷流注册，以及逐建筑的模式 / 室内 / 强度 / 风速存档 |
| `ProxyHeat/ProxyHeatSourceState.cs` | 单源状态：`Cells`、`RingCells`、`Baseline`、`Outcome`、`Cap`、`Floor`、`IsCooler`、`Jets`、`Layers`、`Radius`、`JetRegistered` |
| `ProxyHeat/ProxyHeatMode.cs` | `[Flags] ProxyHeatMode { None = 0, Air = 1, Soil = 2, Jet = 4, All }` |
| `ProxyHeat/ProxyHeatToggleCommand.cs` | `Command_Toggle` 子类，关掉 `GroupsWith` / `InheritInteractionsFrom`，防止四个模式按钮被 gizmo 分组合并 |
| `ProxyHeat/ProxyHeatRelayPatches.cs` | 5 个 Harmony 补丁（`PrepareTemperatureSources` / `RecalculateAffectedCells` / `PostSpawnSetup` / `ProxyHeatMod.WriteSettings`+`ApplySettings` / `ThingWithComps.GetGizmos`） |
| `ProxyHeat/VtePatchDetector.cs` | 检测 `VTE_ProxyHeatPatch` 并出蓝信 |
| `Bootstrap.cs` | `InstallTempOutputShape(harmony)` + `InstallProxyHeatRelay(harmony, settings)`，失败只 `Log.Warning` 降级 |

不新增任何 Def、不新增 XML。全部运行时反射 + MapComponent。

### 2.2 数据流

```
ProxyHeat.CompTemperatureSource
  ├─ AffectedCells        （ProxyHeat 自己用 FloodFill + LineOfSight 维护）
  ├─ Props.tempOutcome    （静态源）或 CompTempControl.TargetTemperature（动态源）
  └─ active               （我们反射驱动）
        │
        ▼
ProxyHeatRelayMapComponent（每模拟周期，Postfix PrepareTemperatureSources）
  ├─ 算 baseline（环形采样，<4 格回退 OutdoorTemp）
  ├─ 算 outcome（Props 或 Target - baseline）
  ├─ 算 target（sum / max，按 cap/floor 夹取）
  ├─ 空气通道（Air 或 Jet 任一开启）
  │    qPerTick = Clamp01((target - T)/1) * strength/60
  │    pendingTemperatureDeltas[i] += qPerTick * dt
  ├─ 地暖通道（Soil）
  │    engine.GroundTemperature[i]  += qPerTick * dt
  └─ 喷流通道（Jet，只出风）
       ExhaustTemperatureFloor = 出口格当前温度（量化 5℃）
       RegisterAirMover(...)  // AO 的压力通道，不搬热
        │
        ▼
AO 自己：ApplyGroundEnergy → ApplyTemperatureDeltas → 扩散/耦合/投影
```

---

## 3. 与 ProxyHeat 的关系

### 3.1 强制 `enableProxyHeat = false`

**理由**：ProxyHeat 的 9 处读值调用点（`Thing.AmbientTemperature`、`GrowthSeasonNow`、`TryGetTemperatureForCell`、`GlobalControls.TemperatureString`、`RCellFinder.CanWanderToCell`、`Brrr` 系列、`RegisterRoomTemperatureChange`、`RegisterHeatPush`）全部先判 `enableProxyHeat`。此外 `TickRare_Postfix` 会改 `PowerOutput` / `operatingAtHighPower`，`TickRare_Transpiler` 会插 `RegisterRoomTemperatureChange`。**关掉它，18 处判断同时短路，是唯一干净的全关方式**。

实现：`ProxyHeatBridge.PatchSettings()` 在 `LongEventHandler.ExecuteWhenFinished`（不能用 `[StaticConstructorOnStartup]`，见 §11.5）里把 `ProxyHeatMod.settings.enableProxyHeat` 置 false，并 Postfix `ProxyHeatMod.WriteSettings` 防止玩家改回（改回时再置 false + 蓝信）。

**为什么不是路径 3（Prefix `GetTemperatureOutcomeFor` 返回入参）**：
- 必须强制 `enableProxyHeat=true` 才能让 `TempTick` 更新 `active`，否则断电建筑的 `active=false` 永久冻结，`RecalculateAffectedCells` 在 `:98-99` 直接 return，照射区永远不建。
- 强制 true 会让所有其它 patch 复活（改功耗显示、限寻路等），Prefix 只堵一个方法。
- 丢失 `GetTemperatureOutcomeFor` 室内分支（`:74-79`）的 `MarkDirty()` 清理副作用。
- `AmbientTemperature` 是热路径，Prefix 每次调用都有开销。

### 3.2 active 自维护（因为不调 `TempTick`）

`enableProxyHeat=false` 后 `MapComponentTick` 不再遍历 `compTemperaturesToTick` 调 `TempTick`，`active` 不会自己更新。我们自己做：

每 N tick（建议 30）遍历 `compTemperaturesToTick`：
```
should = (反射 ShouldBeActive) && (flickable == null || flickable.SwitchIsOn)
if (should != 反射 active)
{
    反射 SetActive(should);   // 内部 active = value; dirty = true;
    comp.MarkDirty();         // public，兜底
}
```

**关键点**：
- `ShouldBeActive`（`CompTemperatureSource.cs:174-180`）**不含 flickable**，所以必须自己叠加 `CompFlickable.SwitchIsOn`。
- `SetActive` 是 private，反射调用。
- `SetActive` 内部已置 `dirty = true`，`MarkDirty()`（`:82`）是 public，可再调一次保证。

### 3.3 `enableProxyHeatEffectIndoors = true`

`CompTemperatureSource.CanWorkIn(cell)`（`:88-91`）= `UsesOutdoorTemperature(cell) || enableProxyHeatEffectIndoors`。设 false 会让室内格被 `GetCells()` 过滤，`AffectedCells` 里根本没有室内格，「室内开关」就无从谈起。所以必须置 true，把「室内是否生效」的判断完全交给我们的逐 Thing 开关。

### 3.4 照射区来源：直接用 `AffectedCells`

不重写 FloodFill / LineOfSight。`ProxyHeatManager.MapComponentTick` 的 dirty 分支（`:45-67`）在 `if (!settings.enableProxyHeat) return;` **之前**，所以即使 `enableProxyHeat=false`，`RecalculateAffectedCells` 仍会跑，`AffectedCells` 仍是最新的。

我们只需 Postfix `Rebuild` / 或在注入时检查 `dirty`，把 `AffectedCells` 快照进 `ProxyHeatSourceState.cells`。

---

## 4. 照射区与基线

### 4.1 环形基线

目标温度需要「这个源在没有自己时，周围环境是多少」。

```
outerCells = { c ∈ AffectedCells | 0.75r ≤ Chebyshev(c, parent.Position) ≤ r } - OccupiedRect(parent)
```

- 用 Chebyshev 距离（与大正方形外圈一致），不按欧氏距离算，避免外圈四角漏掉。
- **排除 `OccupiedRect`**：建筑自身占的格温度被 AO 点源烧到 200~300℃，不能当基线。
- `outerCells.Count < 4`（小半径源、或照射区被墙切碎）→ `baseline = map.mapTemperature.OutdoorTemp`。
- 缓存：只有 `AffectedCells` 变化（Postfix `RecalculateAffectedCells` 或检测 `dirty`）时才重算 `outerCells`；`baseline` 每周期重采样（环境会因天气变化）。

### 4.2 本体格保留

`AffectedCells` 本来就把 `OccupiedRect` 加进去了（`CompTemperatureSource.cs:126` 的 `AddRange(...OccupiedRect(parent).Where(CanWorkIn))`）。**不要剔除**——床这类 pawn 可进入的建筑，要让 pawn 站上去也能读到效果。

只有「基线采样」排除 `OccupiedRect`，「注入」不排除。

### 4.3 室内格

`CanWorkIn` 已由 `enableProxyHeatEffectIndoors=true` 放行，所以室内格会在 `AffectedCells` 里。逐建筑开关决定「这个源的室内格是否注入」：

```
if (cell 在室内 && !thisSource.IncludesIndoors) skip;
```

室内判定用 `GridsUtility.UsesOutdoorTemperature(cell, map)`（与 ProxyHeat 同源）。

---

## 5. 目标温度

### 5.1 静态源（`Props.tempOutcome != null`）

```
target = baseline + Props.tempOutcome
```

注意 `tempOutcome` 是「温差」，不是绝对温度。

### 5.2 动态源（`Props.tempOutcome == null`，即 `Building_TempControl` 自动派生）

**不能读 `TemperatureOutcome`**：AO 把 `Building_TempControl.TickRare` 用 Prefix 短路了（`Buildings/12VanillaFacilities.cs:13-19` → `RunTempControl`），`RegisterRoomTemperatureChange` 永不调用，`lastRoomTemperatureChangeTicks` 恒 0，`TemperatureOutcome`（`CompTemperatureSource.cs:38-46`）直接返回 0。**AO 下这类源原本就是死的。**

改读：

```
CompTempControl control = parent.GetComp<CompTempControl>();
target = control.TargetTemperature;      // 绝对温度，CompTempControl.cs:22
bool isCooler = parent is Building_Cooler
             || (control.Props.energyPerSecond < 0 && !(parent is Building_Heater));  // Building/4VanillaFacilities.cs:39-44
```

`TargetTemperature` 为 `-99999` 时视为「无目标」，跳过该源。

### 5.3 多源重叠：sum

ProxyHeat 原版是 `Average()`（`ProxyHeatManager.cs:69-109`）。我们改成 **sum**，模拟「多个辐射源的热量叠加」。

```
target = baseline + Σ(outcome_i)      // 「允许温度叠加」开（默认）
target = baseline + max(outcome_i)    // 关
```

计算顺序：对每个 cell 收集命中源，累加，再 clamp。不要先 clamp 每个源再加（会低估）。

### 5.4 上限与下限

复用 ProxyHeat 的逐 def 字段：

```
cap = Props.maxTemperature;   // float?，null = 无上限
floor = Props.minTemperature; // float?，null = 无下限
target = Clamp(target, floor ?? -∞, cap ?? +∞);
```

多个源重叠时，取「所有命中源 cap 的**最小值**」作为该格的 cap（保守），floor 取最大值。

`maxTemperature` 的原始语义是「ProxyHeat 读值上限」，这里复用为「物理目标上限」，语义一致，玩家面板不用改。

### 5.5 只正不制

默认 `q >= 0`。当 `target < T`（AO 点源已经把格烧得比目标高）时 `q = 0`，**不制冷**。

这是「AO 点源保留 + 范围层共存」能成立的关键：点源把建筑格烧到 250℃，而 `target` 只有 30℃，如果允许制冷，范围层会去抵消点源——违背「不影响加热器本身的功能」。只正不制让范围层在点源覆盖区自动停手，只在点源覆盖不到的外圈补齐。

**例外**：主动制冷开启时（§8.4），制冷机方向允许负 q。

---

## 6. 注入通道

### 6.1 AO 官方入队时序（`RimWorld/AtmosphereMapComponent.cs:1788-1817`）

```
PrepareTemperatureSources()
  :1790 lock (engineLock)
  :1798 Array.Clear(pendingTemperatureDeltas)      ← 清空
  :1803 interval = SimulationIntervalTicks
  :1808 DrainIncomingTemperatureDeltasUnsafe()     ← 空气 incoming → pending
  :1809 DrainExternalIncomingDeltasUnsafe()        ← 地温 incomingGround → engine.GroundTemperature[i]
  :1813 ApplyContinuousSourcesUnsafe()             ← AO 自己的连续源
  :1816 }                                          ← 锁释放
  ← 【我们在这里 Postfix】
:2971 Task.Factory.StartNew(...)                   ← 方法返回后才启动
      ApplyGroundEnergy(pending, ...)              ← 读 GroundTemperature 作输入
      ApplyTemperatureDeltas(pending)              ← 应用
```

**Postfix 直接写两个数组**：
- 空气：`pendingTemperatureDeltas[index] += q * dt`
- 地温：`engine.GroundTemperature[index] += q * dt`

这就是 AO 自己的连续源（`:2534 ApplyContinuousSourcesUnsafe` → `:2634 EnqueueGroundTemperatureExternalDelta`）在做的同一件事，只是我们把「rate × delta」自己算好写进去。

### 6.2 为什么不用 `SourceSlots`

`SourceSlots.Set(owner, slot, cell, rates)`（`Buildings/2VanillaFacilities.cs:14-49`）是**一个 slot 只能挂一个 cell**。`CompTempOutputShape` 只写 1~4 格，所以完全够用；ProxyHeat 是面源：

| 源 | 半径 | 格数 |
|---|---|---|
| 原版 Campfire | 2.5 | ≈ 20 |
| Frostpunk 标准机 | 26 | ≈ 2124 |
| Frostpunk 升级机 | 60 | ≈ 11310 |

每格一个 slot = 每台机器注册上万条 `AtmosphereSourceHandle`，而 `ApplyContinuousSourcesUnsafe` 每个周期都要遍历全部 handle。量级不可接受。

**继承它的精神**（设速率、让 AO 乘 delta 积分），只把「每格一个 slot」换成「每周期写一次数组」。

### 6.3 没有 `pending` 访问权限时的退路

`pendingTemperatureDeltas` 是 private，`engine.GroundTemperature` 是 public 属性（`LocalAtmosphereEngine.cs:2879`）。

- `engine` 通过 `AtmosphereMapComponent` 的 field 反射拿（`AoBridge` 已有 `FindAtmosphereComponent`）。
- `pendingTemperatureDeltas` 反射一次缓存 `float[]` 引用即可。
- 备选：用 `AtmosphereInterface.AddTemperatureDelta` / `AddGroundTemperatureDelta`（public static），但会进 `incoming*` 队列，**延迟一个周期**才 drain，效果一样只是慢一拍。作为降级路径保留。

### 6.4 子步缩放自动正确

`RunGlobalSubcyclingUnsafe`（`:3034-3068`）每个子步执行 `ApplyTemperatureDeltas(pendingTemperatureDeltas, num2)`，`num2 = num1 / elapsedTicks`，且 **`Σnum2 = 1`**。我们注入的是「整周期量」，被分摊到各子步后总量不变，**不需要自己处理 `num2`**。

地温更干净：`ApplyGroundEnergy` 每个子步 `:3027 groundT[i] = result.GroundTemperature`，是「读入→算→写回」，我们的增量是输入，第一个子步就被吸收。

### 6.5 线程安全

Postfix 执行时 `engineLock` 已释放（`:1816`），但 `Task.Factory.StartNew`（`:2971`）在方法返回**之后**才启动，所以写 `pending` 没有竞态。

保险起见可以反射拿 `engineLock` 再加一层 `lock`，代价是一次 Monitor 进入/退出，可忽略。

---

## 7. 速率与频率

### 7.1 qPerTick 公式

```
T      = 该格当前 AO 温度（Temperature.Read[index]，数组直读）
target = 见 §5
dead   = 1.0℃ 死区
rate   = 该格上「最强命中源」的逐建筑强度（℃/秒）/ 60
q      = Clamp01((target - T) / dead) * rate
```

`Clamp01((target - T)/1)` 让温度接近目标时自动减速，避免像 AO 点源那样冲过头（AO 点源的 `num5` 要等到 >200℃ 才开始衰减，我们不需要那么迟）。

### 7.2 默认强度

默认 `8℃/秒`（`8/60 ℃/tick`）。设置页可调 `0.5 ~ 30 ℃/秒`，并且**每栋建筑可以在自己的 gizmo 里单独覆盖**（见 §9.1）。

关键：同一格可能被多个源覆盖，取**命中它的最强源的强度**，而不是全局平均——弱源不该拖慢强源。实现上 `Accumulate` 把 `strength` 取 max 存进 `_strengthAir/_strengthSoil`，`WriteChannel` 逐格读它算 `q`。

旧版 1℃/秒 的 key 是 `rangeStrengthCelsiusPerSecond`，改成 `rangeStrength` 是为了让老存档也能拿到新的默认值（Scribe 缺 key 时用默认参数）。

### 7.3 dt 积分

```
dt = Find.TickManager.TicksGame - state.lastInjectTick
if (dt <= 0) return;          // 同一周期重复调用时去重
```

与 AO 自己的连续源（`:2571 ticksGame - continuousSource.LastAppliedTick`）完全同构。

### 7.4 计算频率与注入频率分离

- **注入**：每模拟周期一次（`PrepareTemperatureSources` Postfix），不可调。
- **重算 `q`**：同一周期内一次即可。若性能吃紧，可以每 K 个周期重算一次 `q`，中间周期用缓存的 `q * dt`——积分仍由 AO 做，不违背「物理交给 AO」。

---

## 8. 三模式

三个模式是同一个源的三种输出通道，可同时开启（用户 m00375 定案：三个模式开关已给玩家控制权）。

### 8.1 空气模式（默认开）

`pendingTemperatureDeltas[index] += q * dt`

### 8.2 地暖模式（默认关）

`engine.GroundTemperature[index] += q * dt`

注意：
- 必须是「地温」而不是「气温」，`ApplyGroundEnergy` 下一周期会用地温算地气交换。
- 有顶房间地气耦合只有 `0.00102/步`，地暖升温慢但终值高（旧文档 §5.5：-200℃ 环境下 100℃ 地温可把有顶房间抬到 +32℃）。
- 真空地图（`AmbientSurfacePressurePa = 0`）耦合与松弛同乘 0，地暖无效——检测到就跳过并提示。
- `GroundSkinAnchor = 1.0` 且 `GroundSkinExcessValid > 0.5`（有 GPU 全球帧）时，`ApplyGroundEnergy` 会用锚定项覆盖。我们是 Postfix 写入，下一周期 `ApplyGroundEnergy` 会重算，所以**注入不会被覆盖，但会被锚定项稀释**。地暖在 GPU 地图上效果打折，属预期。

### 8.3 喷流模式（默认关）

v6 实现为**从建筑外围向外、可多层接力的 AO 原生 AirMover，且只负责风**（热量见 §8.3.1）。

为什么不跟建筑朝向：喷流是「热源向四周散开」，不是定向吹风。用户 m01313 明确「喷流的方向应该是径向始终指向远离建筑的方向」。

**端口来源必须是 ProxyHeat 的照射区**（用户 m02017）。`AffectedCells` 已经过 `floodFiller.FloodFill` 的连通性检查和 `GenSight.LineOfSight` 的可见性检查，被墙、山体挡住的格根本不在集合里。所以「这个格到底存不存在」由 ProxyHeat 决定，我们只在存在的格里挑端口：

```
候选 = 建筑外扩 (k+1) 圈的几何周长 ∩ AffectedCells
```

这条约束同时解决了两件事：
- 单层时不会在墙里生成喷口（v1.7 之前是纯几何周长，被挡的格照样注册，然后被 AO 静默清零）；
- **多层变得安全**——每一层都从同一份合法集合里取，不存在「外圈一半落在墙里」。

层数由设置给（1~3，默认 1）。第 k 层用 `OccupiedRect.ExpandedBy(k+1)` 的周长，因此**第 2 层紧贴第 1 层**（间距 1 格），不是跨到 0.5r：

| 层 | 候选环 | 出口 |
|---|---|---|
| 第 1 层 | 建筑外扩 1 圈 | 对准第 2 圈（或最后一层时放到 `maxDist`） |
| 第 2 层 | 建筑外扩 2 圈 | 对准第 3 圈（或 `maxDist`） |
| 第 3 层 | 建筑外扩 3 圈 | `maxDist` |

除最后一层外，出口对准下一圈的同一方向，形成接力：第 1 层把热推到第 2 层，第 2 层再往外推。`maxDist = clamp(radius * 0.5, 3, 40)`。

构建方式（在 `RecalculateAffectedCells` 的 Postfix 里做，只在照射区重建时算一次）：

```
for layer in 0..layers-1:
    rect = OccupiedRect.ExpandedBy(layer + 1)
    stride = 周长 > 32 ? 2 : 1
    dist = (不是最后一层) ? layer + 2 : maxDist
    for cell in EnumeratePerimeter(rect):        // 顺序固定，抽稀时疏密均匀
        跳过 occupied / 越界
        跳过 !AffectedCells.Contains(cell)        // ← 核心：格必须真实存在
        outlet = 从中心沿「中心→cell」射线逐格外推，取第一个
                 InBounds && !occupied && ObstacleMask > 0 的格
        找不到 -> 这条不注册
        dir = normalize(outlet - cell)
```

`origin` 用 `OccupiedRect.CenterCell` 而不是 `Position`——多格建筑的 `Position` 是左下角，用它算极角会整体偏一个建筑尺寸。

出口必须沿射线逐格找，而不是随便 `round(center + dir*dist)`：终点若落在墙里，AO 的 `JetTransport.HasOpenPath` 会把这条喷流静默清零。现在改成「先找到第一个开放格再注册」，找不到就干脆不注册，省掉那部分白遍历。

#### 8.3.1 喷流只出风，热量走空气通道（v1.9）

v1.9 起，喷流模式的**热量不再由喷流自己传**，而是和空气模式共用同一条逐格面注入通道：

- `useHeat = (modes & Air) != 0 || (modes & Jet) != 0`，一个源只累加一次，热量均匀铺满整个 `AffectedCells`；
- `ExhaustTemperatureFloor` 改成**出口格当前温度**（量化 5℃ 台阶），于是 `JetTransport` 里 outlet 的 `Mix` 和沿 path 的 `Exchange` 都没有温差可传；
- 风照旧走 `ConditionExhaust = true` 的 `AirMover`（压力通道 `AddNozzlePump`），不受 `Project` 投影影响。

原因：AO 的 `JetTransport.BuildPath` 只用 `Math.Sign` 取 ±x/±z（`Simulation/JetTransport.cs:133-135`），所以热量只会沿四条平行直线交换——这正是用户 m02127 看到的十字。**动量方向本身是自由的浮点 `Vector2`**，不受这条限制，所以「风保留、热改道」是唯一同时满足两者的做法。

量化 5℃ 仍然是必须的：`RegisterAirMover` 只要发现 `Settings` 有任何字段变化就会 `UpdateAirMover`，而它会把 `LastTick` 重置到当前 tick，使本周期 `ActiveTicks = 0`、整条喷流没有流量。

参数（`AtmosphereAirMoverSettings`，反射构造）：

| 字段 | 值 | 说明 |
|---|---|---|
| `CubicMetresPerTick` | `speed / 60` | `speed` 是 m/s，除以 60 换成每 tick 体积 |
| `AirColumnHeightMetres` | `5` | 与 AO `CompPairedJet.columnHeight` 默认值一致 |
| `InletDirection` | `-dir` | 风从内侧来 |
| `OutletDirection` | `dir` | `Vector2`，可任意角，所以方向不会被折叠成 8 向 |
| `MaxPressureRisePascals` | `1000000` | |
| `ConditionExhaust` | `true` | 走 `ApplyJetTransportUnsafe` 的喷流通道 |
| `ExhaustSpeed` | `speed` | |
| `ExhaustTemperatureFloor` | `round((baseline + outcome) / 5) * 5` | 量化到 5℃ 台阶，见下 |
| `ExhaustPressureRatio` | `1.5` | |
| `InletPressureRatio` | `0.5` | |
| `ExhaustOxygenPercent` | `21` | |
| `WaterKgPerTick` | `0` | 不产水 |

风速：**仍由玩家控制，但初值按建筑半径推算**（用户 m01828）。`AutoJetSpeed(radius) = clamp(radius * 1.5, 20, 150)`——半径 3 得 20、半径 26 得 39、半径 60 得 90。玩家在逐建筑「风速」弹窗里改过之后，以玩家值为准；全局设置页不再提供风速滑块。

生命周期：`activeTicks = -1`（永不过期），AO 的 `RegisterAirMover` 按 `owner + key` 就地更新；因为不会自动过期，关喷流、断电、模块关闭时必须显式 `UnregisterAirMovers`。建筑拆除不用管，`PrepareAirMovers` 会按 `Owner.Destroyed` 移除。

`ExhaustTemperatureFloor` 必须量化：`RegisterAirMover` 发现 `Settings` 任何字段变化就会 `UpdateAirMover`，而 `UpdateAirMover` 里的 `CaptureAirMover` 会把 `LastTick` 重置到当前 tick，使同周期 `ActiveTicks = max(0, min(tick, validUntil) - lastTick) = 0`，本周期零流量。

`AtmosphereFieldBridge.ResolveAirMover()` 是**独立能力**：解析失败只让喷流降级，不影响空气/地暖。字段依次缓存在 `_airMoverFields[12]`，避免每次按名字查找。

### 8.4 主动制冷（默认关）

**定义**（用户 m00304 澄清）：不是「加热器反向抽热」，而是**让制冷机也能利用这套机制**——`isCooler` 的源目标低于环境时允许负 q。

```
if (isCooler && settings.AllowActiveCooling)
{
    q = Clamp((target - T) / dead, -maxCoolPerTick, +rate);
    排除 OccupiedRect（不给自己降温）
}
```

保护：
- 默认关闭。
- `maxCoolPerTick` 默认 `0.3 ℃/tick`，显著小于加热速率。
- 排除 `OccupiedRect`，避免建筑自身结冰。
- 开启时在设置面板给警告：制冷机会抵消 AO 点源，可能让加热设备「越开越冷」。

---

## 9. UI

### 9.1 逐建筑开关与参数（6 个）

Postfix `ThingWithComps.GetGizmos`，只要 `thing.TryGetComp<CompTemperatureSource>() != null`（反射类型）就追加 6 个 gizmo：

| gizmo | 类型 | 作用 | 存储 |
|---|---|---|---|
| 空气模式 | `ProxyHeatToggleCommand` | 把该源的温差注入 AO 气温场 | `_modeMasks` bit0 |
| 地暖模式 | `ProxyHeatToggleCommand` | 注入 AO 地温场 | `_modeMasks` bit1 |
| 喷流模式 | `ProxyHeatToggleCommand` | 八径向注册 AO 原生 AirMover | `_modeMasks` bit2 |
| 室内生效 | `ProxyHeatToggleCommand` | 该源的照射区是否覆盖室内格 | `_indoorOn` |
| 强度：x ℃/s | `Command_Action` | 弹出 `Dialog_Slider`，0.5~30 | `_tunedStrength` |
| 风速：x m/s | `Command_Action` | 弹出 `Dialog_Slider`，10~200 | `_tunedJetSpeed` |

四个 toggle 统一用 `ProxyHeatToggleCommand`（`GroupsWith` / `InheritInteractionsFrom` 均返回 false），避免同组点击转发。
六个图标全部是 `BaseContent.WhiteTex` 白色占位，等美术资源替换。

**逐建筑参数**（2026-11-13 用户要求滑块也逐建筑）：
- `_tunedThings` / `_tunedStrength` / `_tunedJetSpeed` 三条并行 List + `_tuningCache`（Thing → 下标）；
- `GetStrength` / `GetJetSpeed` 未命中回落设置页默认值；`SetStrength` / `SetJetSpeed` 首次写入时用默认值初始化其余列；
- `Dialog_Slider` 只吃整数：强度内部按 `0.1℃/秒` 一格换算，显示时除回去；
- 注入时逐格取「命中该格的最强源的强度」（`_strengthAir/_strengthSoil`），不是全局平均；
- 每周期 `TrimDeadTuningEntries()` 从尾部清理已拆除建筑。

**三模式是逐建筑的**（2026-11-13 用户要求改）。mod 设置页里的三个同名 checkbox 降级为「新放置建筑的默认值」，**只对还没有显式记录的建筑生效**；玩家一旦在某台机器上点过开关，就写入显式记录，之后不再受默认值影响。

状态存 `ProxyHeatRelayMapComponent`：
- 运行时 `Dictionary<Thing, int>` 做 O(1) 查询（`_modeCache` / `_tuningCache`）；
- 存档用并行 `List`，`Scribe_Collections.Look(..., LookMode.Reference)` / `(..., LookMode.Value)`；读档后按较短的对齐重建索引，防老存档错位；
- 每周期 `TrimDeadModeEntries()` / `TrimDeadTuningEntries()` 从尾部删已拆除建筑，删完重建索引。

**索引语义**：`_modeCache` / `_tuningCache` 存的必须是**下标**，不是值。v1.2 曾在这里把掩码当索引，导致 `List` 越界被 Unity GUI 静默吞掉，表现是「按钮点了没反应」。

### 9.2 保留 ProxyHeat 设置面板

`ProxyHeatMod.ApplySettings()` 只把 `compTempData` 回写到 def 的 comp props，**不重建 `AffectedCells`**。`MarkDirty` 只有三个触发点：`PostSpawnSetup`（`:70`）、`SetActive`（`:171`）、`GetTemperatureOutcomeFor` 室内分支（`:77`）。改 radius / tempOutcome / maxTemperature 都不会触发重建。

所以需要 Postfix `ProxyHeatMod.WriteSettings`（或 `ApplySettings`）：
```
foreach (comp in manager.compTemperatures) comp.MarkDirty();
```

下次 `MapComponentTick` 的 dirty 分支会重建照射区。

### 9.3 强度暴露

`AOPatchSettings`：
- `RangeStrengthCelsiusPerSecond`（默认 **8.0**，设置页范围 0.5~30；key 用 `rangeStrength`）
- `JetLayers`（默认 **1**，设置页 1~3；key `jetLayers`）。第 k 层用 `OccupiedRect.ExpandedBy(k+1)` 的周长，所以第 2 层**紧贴**第 1 层（间距 1 格），不是跨到 0.5r
- 风速**不再有全局设置项**：初值由 `AutoJetSpeed(radius) = clamp(radius * 1.5, 20, 150)` 按建筑半径给出，玩家在逐建筑「风速」弹窗里覆盖
- `AllowTemperatureStacking`（默认 true）
- `AllowActiveCooling`（默认 false）
- `MaxCoolPerTick`（默认 0.3）
- `DefaultIndoorEffect`（默认 false）
- `AirModeEnabled` / `GroundModeEnabled` / `JetModeEnabled`（**均为 false**，只是「新建筑的默认值」，不是全局开关；逐建筑必须以 gizmo 显式开启）
- `ProxyHeatDebugLog`（默认 true）：前几次注入和每次 `PrepareTemperatureSources` 命中会往 `Player.log` 打 `[AOPatch/ProxyHeat]` 行

**喷流模式的语义**：只出风。它的热量不走喷流自己，而是并入空气通道的面注入（`useHeat = Air || Jet`）。所以「开喷流 = 该建筑获得与空气模式相同的均匀加热 + 一圈向外的风」。空气与喷流同开时热量只算一次，不会翻倍。

设置页的值只对**没有逐建筑覆盖**的建筑生效；每栋建筑都能用自己的 gizmo 调强度与风速。设置面板用 `Listing_Standard`，与 `AOPatchMod` 现有设置面板同风格。

---

## 10. 性能预算

| 项 | 量级 | 频率 |
|---|---|---|
| 重算 `outerCells` | O(πr²) | 仅 `dirty` 时 |
| 重算 `baseline` | O(outerCells) | 每模拟周期 |
| 注入 | O(注入格数) | 每模拟周期 |
| 喷流注册 | O(端口数) = O(周长 × 层数)，6×6 单层 28 条 | 每模拟周期 |

- Frostpunk 升级机 radius 60 ≈ 11310 格；两台 + 原版若干源，总量约 2~3 万格。
- 每模拟周期（默认 6 tick）一次，纯数组累加 + 一次乘法，约 **0.3~0.8 ms**。
- **必须**用 `Temperature.Read` 数组直读，不要 `cell.GetTemperature(map)`（每次走 Harmony patch 链）。
- `cell → index` 用 `cell.z * engine.Width + cell.x` 直接算，不走 `AtmosphereInterface.ResolveIndex`（避免每格一次 `Component(map)` 查找）。
- **温度面注入必须直写 `pendingTemperatureDeltas`**，不能改用 `AddTemperatureDelta`：后者每次都要过 `PatchSourceJournal.Record`（加锁 + 字典），200 源 × 1.1 万格会成为几十毫秒级开销。
- 喷流量级小（周长 28 格/源），走 `AddWindImpulse` 一类的逐格回调是可接受的，但喷流实际走的是 `RegisterAirMover`（按 owner+key 幂等更新），成本更低。
- `temperatureSources[cell]` 的 List 可能残留已失活源，必须用自维护 `activeSet` 过滤，不能假设都活跃。

---

## 11. 兼容与风险

### 11.1 VTE-ProxyHeatPatch

`ewolfy.vtePatch`（`F:\RimworldCodeIL\VTE-ProxyHeatPatch\`）用 Transpiler 把 `GetTemperatureOutcomeFor` 的 `brfalse_S` 改 `Nop`、删 `Ldarg_2; Ret`，**IL 栈不平衡**，可能让方法 JIT 失败。Harmony Prefix 救不了 JIT 失败（JIT 先于 Prefix 生效）。

处理：启动时检测该 Harmony id 存在 → `LetterDefOf.NeutralEvent` 蓝信提示玩家移除。**不尝试兼容**（用户 m00160 明确「不考虑 VTE-ProxyHeatPatch 存在，检测到提示玩家即可」）。

### 11.2 融雪

**不管**（用户 m00239 定案：「融雪依赖AO本身的特性，我们不需要自行处理」）。

ProxyHeat 的融雪在 `CompTemperatureSource.TempTick`（`:205-212`），我们不调 `TempTick`，所以它不会跑。原版雪机制自己会化。AO 源码里没有融雪逻辑，只有 `MouseoverReadoutPatches.cs:82` 读雪深用于显示。

### 11.3 AO 点源共存

点源保留 → 双重计入会不会过热？

- 建筑格：点源烧到 200~300℃，`target` 只有 30~80，`q = 0`（只正不制），范围层不动作。✓
- 外圈：点源覆盖不到（`CompHeatSourceAtmosphere` 只写 `parent.Position` 单格），范围层从这里开始加热，温度由 target 决定，不会超过 target。✓
- 中间地带：点源通过 AO 的扩散已经把温度垫高，范围层的 `(target - T)` 变小，自动少出力。✓

所以「点源 + 只正不制」不会过热，只是外圈升温比纯范围层更快（因为起点更高）。这是预期行为。

### 11.4 存档窗口

AO 的 `EncodeInt16` 把地温映射到 `-100..150℃`、空气 `-100..120℃`。目标超过窗口时读档会被夹回。范围层的 target 一般在 -50~100℃ 内，不会越界；如果玩家把 `tempOutcome` 调到 500，会出现「读档后效果变弱」，属已知限制，UI 提示即可。

### 11.5 初始化时机

本 mod / AO / ProxyHeat 都是 `[StaticConstructorOnStartup]`，执行顺序由 mod 加载顺序决定，不能假设 AO 或 ProxyHeat 先跑。

- `AoBridge.Resolve()` 已用 `Bootstrap` + 失败降级。
- `ProxyHeatBridge.Resolve()` 同样只能在 `LongEventHandler.ExecuteWhenFinished` 里做，**不要**在静态构造里访问 `ProxyHeatMod.settings`（可能还没初始化）。
- MapComponent 的 `FinalizeInit` 里再扫一次 `compTemperatures`（此时所有 Thing 已 spawn）。

---

## 12. 实现清单

### 12.1 Harmony 补丁（4 个）

| 目标 | 类型 | 作用 |
|---|---|---|
| `AtmosphereOverhaul.RimWorld.AtmosphereMapComponent:PrepareTemperatureSources` | Postfix | 注入 `pending` / `GroundTemperature`（核心） |
| `ProxyHeat.CompTemperatureSource:RecalculateAffectedCells` | Postfix | 更新 `ProxyHeatSourceState.cells` / `outerCells` |
| `ProxyHeat.ProxyHeatMod:WriteSettings` | Postfix | 遍历 `compTemperatures` 调 `MarkDirty()` + 强制 `enableProxyHeat=false` |
| `Verse.ThingWithComps:GetGizmos` | Postfix | 逐建筑室内开关 |

### 12.2 落码顺序

1. `ProxyHeatBridge`（纯反射，无副作用）→ 单测：能在游戏里读到 `compTemperatures` 数量。
2. `ProxyHeatRelayMapComponent` + `ProxyHeatSourceState`（只做空气模式，注入到 `pending`）。
3. 逐建筑开关 + 设置面板。
4. 地暖模式。
5. 喷流模式。
6. 主动制冷。
7. 蓝信 / VTE 检测 / 性能压测（Frostpunk 两台 + 原版火堆若干）。

### 12.3 验收标准

- 火堆（radius 2.5）：外圈 3 格内能读到明显升温；AO 点源覆盖的本体格不被削弱。
- Frostpunk 标准机（radius 26）：室外 26 格内形成平滑温度场，无 200℃ 热点（点源仍然只在建筑格）。
- 断电 / 断燃料 / 关开关：`AffectedCells` 清空，温度回落。
- 室内开关：关闭后室内格不再注入，室外仍生效。
- 存档读档：开关状态、源状态恢复；温度不跳变。
- 性能：200 源场景下模拟周期耗时增加 < 1ms。

### 12.4 实测记录

| 日期 | 版本 | 现象 | 结论 |
|---|---|---|---|
| 2026-11-13 | v1.0 | 建筑只出现一个「室内生效」开关，塔周围无升温，篝火仅下风轻微升温 | 三个 bug（只遍历 `compTemperatures` / active 死锁 / `GetActive` 早于 `RefreshActive`）→ v1.1 |
| 2026-11-13 | v1.1 | 三个模式按钮点击无反应 | `_modeCache` 存的是掩码却当索引用，`_modeMasks[1]` 越界被 Unity GUI 静默吞掉 → v1.2 |
| 2026-11-13 | v1.2 | 生效，但喷流风速太小、户外加热太慢 | 喷流 v1 只是沿朝向注热、没有风 → v1.3 改 AO 原生 `AirMover`，默认强度 8℃/秒，滑块逐建筑 |
| 2026-11-13 | v1.3 | 喷流出现十字，四角无热 | 出口格用 `RoundToInt(dir)*2` 折叠成 8 向 → v1.7 每格一条 + 出口沿射线取整 |
| 2026-11-13 | v1.7 | 3 层仍十字 | 真正根因：`JetTransport.BuildPath` 用 `Math.Sign` 把热交换路径锁成 ±x/±z；动量方向其实是自由的 → v1.9 喷流只出风 |
| 2026-11-13 | v1.9 | 「现在效果很好」 | 收束：范围为面注入，喷流只负责风 |

---

## 13. 决策台账

| # | 决策 | 来源 |
|---|---|---|
| 1 | AO 点源保留，范围层共存，不移除 Def comp | 用户 m00160 |
| 2 | 强制 `enableProxyHeat=false` + 蓝信 | 用户 m00268 |
| 3 | 路径 3（Prefix `GetTemperatureOutcomeFor`）否决 | 源码核查 |
| 4 | 不调 `TempTick`，自维护 active | 源码核查 |
| 5 | `enableProxyHeatEffectIndoors=true` 保证照射区完整 | 源码核查 |
| 6 | 直接复用 `AffectedCells`，不重写照射区 | 源码核查 |
| 7 | 环形基线 `[0.75r, r]` 减 `OccupiedRect`，<4 格回退 `OutdoorTemp` | 用户 m00375 |
| 8 | 本体格保留在照射区 | 用户 m00375 |
| 9 | 多源 sum，「允许温度叠加」默认开 | 用户 m00389 |
| 10 | 上限逐 def 复用 `maxTemperature`，`minTemperature` 作下限 | 用户 m00394 |
| 11 | 室内开关逐 Thing，状态存 MapComponent | 用户 m00394 |
| 12 | 只正不制，默认不允许负 q | 设计推导 |
| 13 | 主动制冷 = 制冷机走同机制，默认关，`maxCoolPerTick=0.3` | 用户 m00304 |
| 14 | 注入点 = Postfix `PrepareTemperatureSources`，写 `pending`/`GroundTemperature` | 源码核查（m00436） |
| 15 | 不用 `SourceSlots`（一格一 handle，面源不可行） | 源码核查 |
| 16 | 频率 = AO 模拟周期，积分 = AO，`dt = TicksGame - lastInjectTick` | 用户 m00394 |
| 17 | `ratePerTick` 默认 1℃/秒（v1.3 改为 **8℃/秒**，key 改名 `rangeStrength`） | 用户 m00474，m01313 调整 |
| 18 | 三种模式一次做全 | 用户 m00474 |
| 19 | 融雪交原版，不自实现 | 用户 m00239 |
| 20 | 保留 ProxyHeat 设置面板 + `WriteSettings` 后 `MarkDirty` | 用户 m00239 |
| 21 | 三模式改为**逐建筑** gizmo；mod 设置页三项降级为「新建筑默认值」，显式记录后不再受默认影响 | 用户 m00938/m00970 |
| 22 | 喷流改为**径向 AO 原生 AirMover**，方向不跟建筑朝向 | 用户 m01313 |
| 23 | 强度/风速滑块**逐建筑**，点击弹 `Dialog_Slider` | 用户 m01313/m01320 |
| 24 | 喷流出口按**格与中心的真实极角**分桶（每桶取最外格），不用固定八向 | 用户 m01470 |
| 25 | 三个模式默认**全关**（新建筑必须逐建筑手动开启）；Scribe key 改为 `airModeDefault`/`groundModeDefault`/`jetModeDefault` | 用户 m01542 |
| 26 | 喷流用 `activeTicks = -1` + 出风温度量化 5℃ 台阶（规避 AO `UpdateAirMover` 重置 `LastTick` 导致 `ActiveTicks == 0`）；关喷流/关模块时必须显式 `UnregisterAirMovers` | 实测 |
| 27 | 喷流端口从半径外环改到**建筑外围一圈**，风速初值按半径 `clamp(r*1.5, 20, 150)`，玩家仍可逐建筑调 | 用户 m01809/m01828 |
| 28 | 喷流改为**每个周长格一条 + 出口沿射线取整格放到 0.5r**（不用两圈接力），消除十字 | 用户 m01955/m01960 |
| 29 | 喷流端口候选 = 几何周长 ∩ AffectedCells（被遮挡的格不生成喷口）；层数 1~3 可调、层间距 1 格；出口沿射线找第一个开放格 | 用户 m02017/m02021 |
| 30 | 喷流模式**只出风**：热量并入空气通道的逐格面注入（`useHeat = Air \|\| Jet`）；`ExhaustTemperatureFloor` 改为出口格当前温度（量化 5℃），消除 `JetTransport` 四向 path 画的十字 | 用户 m02313（m02400 实测确认） |

---

## 14. 后续

> 状态：**已落码并实机确认（2026-11-13，v1.9）**。`dotnet build -c Debug` 0 警告 0 错误。
>
> 最终形态：范围效果由 Postfix `PrepareTemperatureSources` 直写 `pendingTemperatureDeltas` / `GroundTemperature`，物理全部交给 AO；三模式逐建筑开关；喷流走 AO 原生 `AirMover`（压力通道），但**只负责风**，热量由空气通道的逐格面注入提供。用户 m02400：「现在效果很好」。
>
> 演进简表：v1.1 修「完全没生效」→ v1.2 修 gizmo 无响应 → v1.3 喷流改 AirMover + 默认 8℃/s + 逐建筑滑块 → v1.4 极角分桶 → v1.5 默认全关 + 修 `LastTick` 重置 → v1.6 端口搬到建筑外围 + 风速按半径 → v1.7 每格一条 + 出口 0.5r → v1.8 端口候选 = 照射区 ∩ 周长 + 分层 → v1.9 喷流只出风。

### 14.1 v1.1 修复记录（2026-11-13，实测「完全没生效」后）

首轮实机测试现象：建筑只出现「范围效果及于室内」gizmo，塔周围温度无变化，篝火仅下风方向轻微升温。

定位到的三个 bug：

1. **只遍历 `compTemperatures`**。`Inject` 开头 `if (comps.Count == 0) return;`，而该列表只收 active 的源 → 空列表直接短路。
2. **active 死锁**。`active` 只在 `PostSpawnSetup :60` 初始化、由 `TempTick :196-202` 维护；我们设 `enableProxyHeat=false` 后 `TempTick` 不跑，断电状态下生成的建筑永久 `active=false`，`RecalculateAffectedCells :98-99` 直接 return → 永不进 `compTemperatures` → 也没机会被 `RefreshActive` 修回来。
3. **`GetActive` 检查在 `RefreshActive` 之前**。即使源在某些列表里，第 288 行的 `!GetActive(comp)` 会把它 continue 掉，`RefreshActive` 永远不执行。

修复：

- 新增 `_knownSources` 全量名单，Postfix `CompTemperatureSource.PostSpawnSetup(bool)` 登记；兜底从 `compTemperatures ∪ compTemperaturesToTick` 捞（纯静态源只能靠 PostSpawnSetup）。
- `Inject` 改为遍历 `_knownSources`，`RefreshActive` 提到 `GetActive` 判断之前；`active` 变为 true 时 `SetActive` + `MarkDirty`，下一个 `MapComponentTick` 的 dirty 分支重建照射区。
- 新增 `ProxyHeatDebugLog` 设置（默认开）：前 5 次注入 + 每 600 tick 打一条 `sources/active/touched/airWrite/first=[defName act out cells]` 摘要；`PrepareTemperatureSources_Postfix` 前 3 次打「命中 + relay 是否存在」。

### 14.2 v1.2 修复记录（2026-11-13，实测「三个模式按钮点击无反应」后）

现象：三个模式 gizmo 能看到，点下去毫无反应，按钮状态也不变。

根因不是 UI 点击链路，而是 `_modeCache` 的**下标/掩码类型混用**：

```csharp
// RebuildModeCache：注释说存「下标」，实际存了掩码
_modeCache[thing] = _modeMasks[i];

// ToggleMode：把它当「下标」写
_modeMasks[index] = (int)next;   // index 其实是掩码值，如 Air=1
```

于是点击时 `_modeMasks[1] = next` 在只有一个记录项时直接越界抛 `ArgumentOutOfRangeException`，被 Unity GUI 静默吞掉；而 `_modeCache` 本身从不刷新，`GetModes` 永远返回旧掩码，`isActive()` 因此固定在初始值。

修复：

- `RebuildModeCache` 存下标：`_modeCache[thing] = i;`
- `GetModes` 按下标读且在界内才用：`index >= 0 && index < _modeMasks.Count` 时返回 `(ProxyHeatMode)_modeMasks[index]`，否则回落 `DefaultModes()`。
- 顺带把四个 toggle 换成 `ProxyHeatToggleCommand`（`GroupsWith` / `InheritInteractionsFrom` 均返回 false），排除同组点击转发这一层干扰。
- 新增诊断：`ToggleMode` 前 20 次打 `toggle <defName> <mode> -> <新掩码>`；`GetGizmos_Postfix` 前 3 次打命中记录。

**教训**：缓存「实体→下标」只存下标；「点击无反应」先怀疑方法抛异常被 GUI 吞掉，再怀疑缓存自相矛盾，最后才是 UI 分组。

### 14.3 v1.3 调整（2026-11-13，实机确认生效后）

实测确认范围效果已生效，用户提出三个调整：

1. **喷流风速太小** → v1 的喷流只是「沿朝向前 3 格温度注入」，压根没有风。改为八径向 `RegisterAirMover`，默认 60 m/s（AO 自己的喷流 half=30 / max=90）。
2. **空气模式户外加热太慢** → 默认强度 1℃/秒 → 8℃/秒，设置页范围放宽到 0.5~30；强度 key 改名 `rangeStrength` 以便老存档拿到新默认值。
3. **四个图标改白色占位** → 全部换成 `BaseContent.WhiteTex`，等美术资源。

追加要求（同轮）：

4. **喷流方向必须径向**（m01313）→ 不跟 `Rotation`，固定 8 向，从建筑中心向外。
5. **滑块也逐建筑**（m01313）→ 强度与风速各加一个 gizmo，点击弹 `Dialog_Slider`；存储用 `_tunedThings/_tunedStrength/_tunedJetSpeed` 三条并行 List；注入时逐格取最强源的强度。
6. **滑块用弹出对话框**（m01320）→ `Dialog_Slider` 只吃整数，强度按 0.1℃/秒 一格换算。

随之调整：`Accumulate`/`WriteChannel` 多带一条 `strength` 通道；`_strengthAir/_strengthSoil` 两个新数组；`InjectJets` 不再手工写 `pending`，改为注册 AirMover，物理全交 AO。

`AtmosphereFieldBridge` 新增独立的 `ResolveAirMover()` 与 `RegisterAirMover(...)`，12 个字段按索引缓存，解析失败只让喷流降级。

### 14.4 v1.4 调整（2026-11-13，喷流方向改真实极角）

用户 m01470：「方向径向，是否可以直接通过各个格子与建筑中心的矢量角度，而不是固定方向？」

结论：可以，而且比固定八向更正确。改为：

- `RefreshRing` 里顺带 `BuildJetPorts`：遍历外环格，按 `atan2(dz,dx)` 分桶，每桶取距中心最远的格，方向用该格的真实单位向量（不是 45° 的倍数）。
- 桶数 `clamp(ceil(2πr/4), 8, 24)`：篝火 8 条、半径 26/60 都 24 条。
- `JetPort`（`Inlet`/`Outlet`/`DirX`/`DirY`）缓存在 `ProxyHeatSourceState.Jets`，`InjectJets` 只负责注册，不再算几何。
- 删除 `JetDirections`/`JetOffsets` 两个固定方向常量。

这样每 15° 一条气流且每条都精确指向它对应的那片区域，避免了固定八向在大半径下的空档。

### 14.5 v1.5 修复（2026-11-13，喷流无风 + 默认全关）

用户 m01542：「做修改，默认三个模式都不启用。当前存在问题，喷流模式似乎没有产生风。」

喷流无风是**两个独立缺陷叠在一起**，任何一个都足以让喷流完全不工作：

**缺陷 A：全局开关把逐建筑开关架空了。**
`Inject` 里原本写 `bool jet = settings.JetModeEnabled && AtmosphereFieldBridge.AirMoverAvailable;`。`JetModeEnabled` 的语义在 v1.3 已经降级成「新建筑的默认值」，但这里仍拿它当门禁——只要玩家没在设置页开「默认启用喷流」，逐建筑按钮点开了也进不到 `InjectJets`。
修复：门禁只保留 `AirMoverAvailable`，是否真的喷流交给 `InjectJets` 内部的 `GetModes(parent) & ProxyHeatMode.Jet` 逐源判断。

**缺陷 B：`RegisterAirMover` 的 `LastTick` 重置陷阱。**
AO 的 `RegisterAirMover` 按 owner+key 找到已有记录后，只要 `Settings` 有任何字段不等就调 `UpdateAirMover`，而 `UpdateAirMover` 内部会 `CaptureAirMover(airMover, CurrentGameTick)`，把 `LastTick` 重置到当前 tick。随后本周期 `PrepareAirMovers` 再调 `CaptureAirMover` 时，`AirMoverTransport.ActiveTicks(lastTick = tick, tick, validUntil) == 0`，直接 return，**这一周期不产生任何流量**。
我们原来每周期都把 `ExhaustTemperatureFloor` 设成 `baseline + outcome`，它随温度场每周期漂移 → `Settings.Equals` 恒不成立 → 每周期都 `UpdateAirMover` → `LastTick` 每周期被重置 → 永远没有流量。
修复两条：
- `JetActiveTicks` 从 `60f` 改为 `-1f`（永不过期，建筑拆除时由 `PrepareAirMovers` 移除），不再依赖周期性续期。
- 出风温度量化到 5℃ 台阶：`jetFloor = round(target / 5f) * 5f`。温度缓慢漂移时几十个周期才跨一次台阶，绝大多数周期 `Settings` 不变，`LastTick` 得以保留、流量正常累积。

**默认全关：**
`AirModeEnabled` 默认从 `true` 改为 `false`，三个模式的 Scribe key 一并改为 `airModeDefault` / `groundModeDefault` / `jetModeDefault`——按 v1.3 的教训（改默认值对老存档无效），换 key 才能让老存档也落到「全关」。

**随之而来的注销义务：**
`activeTicks = -1` 意味着 AO 不会让喷流自己过期，所以以下情况必须显式注销，否则会残留一条永远在吹的热气流：
- 逐建筑关掉喷流 / 建筑断电 / 目标温度跌破环境温度 / `state.Jets` 为空 → `StopJets(state)`（只在 `state.JetRegistered` 为 true 时真去注销，避免每周期扫 AO 的 airMovers 列表）；
- 模块总开关或 `ProxyHeatRelayEnabled` 关闭 → `StopAllJets()`；
- 建筑被拆除 → 不用管，AO 的 `PrepareAirMovers` 会按 `Owner.Destroyed` 移除。

副作用说明：老存档里玩家此前通过 gizmo 逐建筑开过的模式仍保留（那是 `_modeThings/_modeMasks`，与全局默认值无关），只是新放置的建筑默认三个模式都不开。

### 14.6 v1.6：喷流端口改到建筑外围

**触发**：用户 m01761 实测（半径 60 的升级塔）「只在区域外边缘的边角出现气流，区域内完全没有气流」，m01809 补充「测试用的是大加热塔，半径 60，故不存在半径过小问题」。

**诊断**（三层叠加）：
- 端口取在 `chebyshev ∈ [0.75r, r]` 的外环，`outlet = cell + 2*step`，半径 60 时气流起点距塔 60 格——区域内部必然没有风，看到的就是「最外缘」。
- `RingCells` 用 Chebyshev 筛，而 `AffectedCells` 是欧氏圆，45° 方向 `0.707r < 0.75r` 被排除，24 个角度桶里四个对角桶恒为空。
- 外环出口落点随机，贴基地外墙或山体时被 `IsAirMoverPortOpen`（四面 `FaceOpen` 之和为 0）/ `TryGetNozzleReturnPath` / `JetTransport.HasOpenPath` 静默清零，只剩零星几条。

**用户否决的方案 A**（对有效区域每格施加带温度的风场）：`LocalAtmosphereEngine.VelocityX/VelocityZ` 虽是 public `AtmosphereField`，但 `ProjectAndFinalize` 里的 `Project(...)` 是压力投影，职责就是消掉速度场的散度分量，而径向向外的风是纯散度场，写进去会被削掉；绕过去只能写 private 的 `externalDivergence`，上万格同时当源会让固定迭代次数的压力求解不收敛。

**v1.6 改动**：
- `BuildJetPorts` 改为从 `OccupiedRect.ExpandedBy(1)` 的周长环取格；`inlet = 周长格`，`outlet = cell + step*2`（v1.6）→ v1.7 再改为「沿射线取整格、放到 0.5r」，见 §14.7。
- 桶数从「按半径」改为「按周长」：`clamp(ceil(周长 / 2), 8, 24)`。
- 每桶选「角度最接近桶中心」的格，而不是「距中心最远」，让方向均匀。
- `origin` 从 `Position` 改为 `OccupiedRect.CenterCell`（多格建筑的 `Position` 是左下角）。
- 风速仍由玩家控制，但初值改为 `AutoJetSpeed(radius) = clamp(radius * 1.5, 20, 150)`；删除全局风速设置项与 `JetSpeedMetresPerSecond` 字段，逐建筑弹窗保留。
- `BuildJetPorts` 增加 `Map` 参数用于 `InBounds` 检查。

**编译**：0 警告 0 错误。

### 14.7 v1.7：喷流方向不再折叠成十字

**触发**：用户 m01955 实测（半径 60 大塔）「效果还可以，但会出现一个十字」，并要求「周围一圈包括角落都产生喷流、方向按建筑正中心的矢量方向」以及「再外增加一圈」。

**十字的根因**：v1.6 用 `step = round(dir)` 当步长、`outlet = cell + step*2`。`round(dir)` 只有 8 种取值，6×6 塔 `ExpandedBy(1)` 周长 28 格、14 个角度桶（每 25.7°）折叠后变成「轴向各 3 条、斜向各 1 条」，出口又只差 2 格，视觉上就是四臂粗、斜角细的十字。

**关键事实**：AO 的 `OutletDirection` 是 `Vector2`，可以取任意浮点角，不受 8 邻域限制；受限的只有 `inlet`/`outlet` 必须是整数格。所以只要让出口格沿射线连续取整，方向多样性立刻恢复。

**方案取舍**：分析过「两圈接力」（内圈贴建筑、外圈在 0.45r 再推一次），代价是 mover 翻倍、外圈 inlet 频繁落在墙里被 `IsAirMoverPortOpen` 静默丢弃、以及额外抽走环境空气造成局部低压。用户 m01960 选方案 A：**单圈 + 出口直接放大到 0.5r**，靠 `JetTransport` 一次把热推到中段，中段到边缘交给平流。

**v1.7 改动**（集中在 `BuildJetPorts` 与新增的 `EnumeratePerimeter`）：
- 不再按角度分桶；直接遍历周长格，每个格一条（四角天然包含），角度间隔从 25.7° 降到 12.9°。
- `dist = Mathf.Clamp(radius * 0.5f, 3f, 40f)`；`outlet = round(center + dir * dist)`。
- 方向改用 `normalize(outlet - cell)`，避免「从 A 排出却朝 B 吹」。
- 周长 > 32 时按序每 2 格抽一条（`EnumeratePerimeter` 顺序固定，取模保证疏密均匀而不是随机丢格）。
- 跳过 `outlet` 越界、落在 `OccupiedRect` 内、或与 `cell` 重合的情形。

**条数**：3×3 → 8、5×5 → 16、6×6 → 28；单圈即可覆盖整圈方向。

### 14.8 v1.8：端口来源改用 ProxyHeat 照射区 + 分层

**触发**：用户 m02017 指出「射流起点都应该在 heatProxy 区域已经计算出来的格子上，如果被遮挡，则不会计算出这个格子，格子不存在，也就不应该为这个格子创建喷射点」，并追问是否可以支持多层。

**结论**：这是把「端口合法性」的判定权交还给 ProxyHeat。`BuildJetPorts` 的候选从「几何周长」改为「几何周长 ∩ `state.Cells`」：

- `AffectedCells` 已过 `FloodFill`（连通）与 `GenSight.LineOfSight`（可见），被墙挡的格不存在；
- `RecalculateAffectedCells` 的 predicate 排除实心 edifice，占用格也不存在；
- 我们再排除 `OccupiedRect`（自身格，ProxyHeat 会主动加进去）。

**多层因此变得安全**：v1.6 否决「两圈」的理由是「外圈 inlet 容易落墙」，那其实是「端口来源不是照射区」的症状；改成照射区白名单后，落墙的格本来就不在候选里。

**层间距按用户要求改为 1 格**：第 k 层用 `OccupiedRect.ExpandedBy(k+1)` 的周长，第 2 层紧贴第 1 层，而不是跨到 0.5r。除最后一层外，出口对准下一圈的同一方向，形成接力。

**出口也校验**：`TryFindOutlet` 沿「中心→inlet」射线逐格外推，取第一个 `InBounds && !occupied && ObstacleMask > 0` 的格；找不到这条就不注册。`InjectJets` 里再用 `obstacle` 二次确认，避免 AO 的 `HasOpenPath` 静默清零。

**层数暴露给玩家**：`AOPatchSettings.JetLayers`（1~3，默认 1，Scribe key `jetLayers`），设置页「喷流模式」下方一个 1~3 滑条，不进 `JetModeEnabled` 的条件分支——因为喷流是逐建筑开关的，层数必须随时可调。`Inject` 里检测 `state.Layers != layers` 时重建端口。

**编译**：0 警告 0 错误。

### 14.9 v1.9：喷流只出风，热量改走空气通道的面注入

**触发**：用户 m02313「尝试实现「温度面注入 + 喷流只出风」」。

**根因回顾**：v1.8 之后 3 层仍是十字。复核 AO 源码后确认，`JetTransport.BuildPath`（`Simulation/JetTransport.cs:133-135`）用
`num1 = |dx| >= |dy| ? Math.Sign(dx) : 0; num2 = num1 == 0 ? Math.Sign(dy) : 0;`
把路径限制成 ±x/±z 四条直线；而 `Exchange`（`:73-81`）沿这条 path 逐格交换温度。于是所有同轴向 path 平行重叠，热被画成十字，四角没有 path 经过。

**关键区分**：动量（风）走的是另一条通道——`AddAirMoverMomentum`（`AtmosphereMapComponent.cs:734`）→ `AddAirMoverPortMomentum`（`LocalAtmosphereEngine.cs:881-895`）→ `VelocityX/Z[outlet] += direction.normalized * ((speed - dot) * blend)`，`Direction` 是浮点 `Vector2`，**任意角都不折叠**。所以「方向少」不是喷流的能力上限，只是「热」的通道上限。

**实现**：
1. `Inject` 中把逐建筑通道判定改成 `useHeat = Air || Jet`，`useSoil` 不变；同一源只 `Accumulate` 一次 → 喷流开启时热量也由空气通道的逐格面注入完成，均匀覆盖 `AffectedCells`。
2. `InjectJets` 增加 `float[] temperature` 参数；`ExhaustTemperatureFloor` 从「`baseline + outcome`（目标温度）」改为「**出口格当前温度** 量化 5℃ 台阶」：
   ```csharp
   float outletTemp = temperature[outletIndex];
   float jetFloor = Mathf.Round(outletTemp / 5f) * 5f;
   ```
   这样 `JetTransport` 的 `Mix(T[outlet], source.Temperature, fraction1)` 与沿 path 的 `Exchange` 都没有温差，path 不再画十字；风仍由 `AddNozzlePump` 从 outlet 推出。
3. 量化 5℃ 保留：不量化则 `Settings` 每周期都变 → `UpdateAirMover` 重置 `LastTick` → `ActiveTicks = 0` → 永远无风。

**为什么不用 `AddWindImpulse` 造风**（方案 B 的风部分）：`AddWindImpulse` 写的是速度通道，会被 `Project` 的散度投影削掉（径向向外是纯散度场），再被 `DampVelocity(0.5)` 每步减半；AO 自己的定向输送走 `externalDivergence` / `AddNozzlePump` 压力通道。所以风必须留给 `AirMover`。

**性能**：`useHeat` 只是合并了通道，面注入的成本与空气模式相同（r=60 约 1.1 万格/源，直写 `pendingTemperatureDeltas`，不走 `AddTemperatureDelta` 的 journal）。

**编译**：0 警告 0 错误，DLL 已写入 `Assemblies/`。

**待实测**：十字是否消失、四角是否升温、风是否仍在、`Logs/Player.log` 里 `AOPatch/ProxyHeat` 的 `jet=` 状态。

**实测结果（2026-11-13，用户 m02400）**：「现在效果很好」。十字消失、四角升温、风保留，v1.9 定为当前形态，不再继续调参数。相关验收记录见 §12.4。

两件后续事项：

- 若实测发现「点源 + 范围层」在 Frostpunk 上仍有视觉热点，再评估是否引入「点源附近半径内降权」。
- 旧文档 `AO-ProxyHeat接管与三模式设计.md` 保留，但读者应先读本文。
