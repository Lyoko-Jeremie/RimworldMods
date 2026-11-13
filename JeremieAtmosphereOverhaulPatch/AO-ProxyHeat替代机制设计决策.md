# AO · ProxyHeat 替代机制设计决策

> 状态：**定稿 v1**（2026-11-13），全部设计决策已拍板，待落码。
> 取代关系：本文取代 `AO-ProxyHeat接管与三模式设计.md` 的「结论」与「实现方案」部分。旧文档保留为调研记录（其 §5 物理推导、§6 温度语义仍有效），但其中 **§5.11.3「接管并移除 AO 点源」已被推翻**——最终方案是点源保留、范围效果作为额外层共存。
> 依据：AO 1.7 源码（`F:\RimworldCodeIL\AtmosphereOverhaul\`，MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、ProxyHeat 源码（`F:\RimworldCodeIL\ProxyHeat\`，MVID `7358A296-A2E2-46F8-A5D3-4C48DE897B1C`）、Frostpunk Heat Generator Tower Def（`294100/3545379519`）。

---

## 0. 一句话

**保留 AO 自己的热源系统，另外做一层「把 ProxyHeat 的读数魔法翻译成 AO 场里的真实热源」的转译器；频率、积分、扩散、耦合全部交给 AO，转译器只负责每隔一个模拟周期往 AO 的入队数组里加一笔增量。**

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
| `Source/JeremieAtmosphereOverhaulPatch/ProxyHeat/ProxyHeatBridge.cs` | 反射入口：`ProxyHeatMod.settings`（internal）、`ProxyHeatManager` 类型、`compTemperatures` / `compTemperaturesToTick`、`CompTemperatureSource` 的 `Props` / `AffectedCells` / `active` / `ShouldBeActive` / `SetActive` / `MarkDirty` / `CanWorkIn` |
| `ProxyHeat/ProxyHeatRelayMapComponent.cs` | `MapComponent`：持有全部源状态、每模拟周期注入、存档 |
| `ProxyHeat/ProxyHeatSourceState.cs` | 单源状态：`cells[]`、`cellIndices[]`、`outerCells[]`、`baseline`、`cachedTarget` |
| `ProxyHeat/ProxyHeatRelaySettings.cs` | Mod 设置：强度（℃/秒）、主动制冷开关、默认室内开关、最大制冷速率 |
| `ProxyHeat/VtePatchDetector.cs` | 检测 `ewolfy.vtePatch` 并出蓝信 |
| `Patches/ProxyHeatRelayPatch.cs` | 4 个 Harmony 补丁（见 §12） |
| `Bootstrap.cs` | 追加 `ProxyHeatRelayPatch.Install(harmony)`，失败降级只 `Log.Warning` |

不新增任何 Def、不新增 XML。全部运行时反射 + MapComponent。

### 2.2 数据流

```
ProxyHeat.CompTemperatureSource
  ├─ AffectedCells        （ProxyHeat 自己用 FloodFill + LineOfSight 维护）
  ├─ Props.tempOutcome    （静态源）或 CompTempControl.TargetTemperature（动态源）
  └─ active               （我们反射驱动）
        │
        ▼
ProxyHeatRelayMapComponent（每模拟周期）
  ├─ 算 baseline（环形采样）
  ├─ 算 target（sum + clamp）
  ├─ 算 qPerTick = Clamp01((target - T)/1) * rate
  └─ 注入：pendingTemperatureDeltas[i] += q*dt
           engine.GroundTemperature[i] += q*dt
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

v3 实现为**按极角自适应的 AO 原生 AirMover**。

为什么不跟建筑朝向：喷流是「热源向四周散开」，不是定向吹风。用户 m01313 明确「喷流的方向应该是径向始终指向远离建筑的方向」。

为什么不用固定八向（v2）：固定方向在半径大时会留下明显空档——半径 26 的塔，相邻两条气流之间隔了 22.5°，中间一大片照不到。用户 m01470 提出「直接通过各个格子与建筑中心的矢量角度」，v3 采纳。

构建方式（在 `RecalculateAffectedCells` 的 Postfix 里做，只在照射区重建时算一次）：

```
for cell in RingCells:                       // 已按 [0.75r, r] 筛过的外环格
    dx = cell.x - center.x; dz = cell.z - center.z
    angle = atan2(dz, dx)
    bucket = floor((angle + π) / 2π * buckets)
    每桶只保留 distSq 最大的那个格           // 取最外侧
之后对每桶选中的格：
    dir = normalize(dx, dz)                   // 真实方向，不是固定 45° 的倍数
    step = round(dir)                         // 取整，避免浮点抖动
    inlet  = cell - step                      // 贴在内侧一格
    outlet = cell + step * 2                  // 再往外两格
```

桶数随半径自适应：`buckets = clamp(ceil(2πr / 4), 8, 24)`。

| 半径 | 桶数 | 说明 |
|---|---|---|
| 3（篝火） | 8 | 小半径也能裹住一圈 |
| 26（标准塔） | 24 | 每 15° 一条 |
| 60（升级塔） | 24 | 上限，避免注册上千条 AirMover |

代价与收益：每个源最多 24 个 handle，200 个开喷流的源约 4800 条；相比逐格注册（半径 60 约 1.1 万格/源）低两个数量级，而方向精度已经从「固定 8 向」提升到「每 15° 一条且指向真实格」。

参数（`AtmosphereAirMoverSettings`，反射构造）：

| 字段 | 值 | 说明 |
|---|---|---|
| `CubicMetresPerTick` | `speed / 60` | `speed` 是 m/s，除以 60 换成每 tick 体积 |
| `AirColumnHeightMetres` | `5` | 与 AO `CompPairedJet.columnHeight` 默认值一致 |
| `InletDirection` | `-dir` | 风从内侧来 |
| `OutletDirection` | `dir` | 朝外 |
| `MaxPressureRisePascals` | `1e6` | 近似不限压，AO 自己也这么给 |
| `ConditionExhaust` | `true` | 启用出口温度 |
| `ExhaustSpeed` | `speed` | 默认 60 m/s（AO 喷流 half=30 / max=90） |
| `ExhaustTemperatureFloor` | `baseline + outcome` | **关键**：出口温度下限 = 目标温度，所以喷出的气是热的 |
| `ExhaustPressureRatio` | `1.5` | 轻微增压 |
| `InletPressureRatio` | `0.5` | 与 AO 默认一致 |
| `ExhaustOxygenPercent` | `21` | 正常空气 |
| `WaterKgPerTick` | `0` | 不产水 |

生命周期：AO 的 `RegisterAirMover` 按 `owner + key` 就地更新，所以每模拟周期重复调用是安全的；`activeTicks = 60` 远大于模拟周期，源关闭喷流后旧记录会自动过期，无需显式注销。

`AtmosphereFieldBridge.ResolveAirMover()` 是**独立能力**：解析失败只让喷流降级，不影响空气/地暖。字段依次缓存在 `_airMoverFields[12]`，避免每次按名字查找。

`inlet`/`outlet` 是建筑外侧格，不经过建筑实体格，绕过 `ObstacleMask = 0` 的问题。

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
- `JetSpeedMetresPerSecond`（默认 **60**，设置页范围 10~200）
- `AllowTemperatureStacking`（默认 true）
- `AllowActiveCooling`（默认 false）
- `MaxCoolPerTick`（默认 0.3）
- `DefaultIndoorEffect`（默认 false）
- `AirModeEnabled` / `GroundModeEnabled` / `JetModeEnabled`（默认值，不是全局开关）

设置页的值只对**没有逐建筑覆盖**的建筑生效；每栋建筑都能用自己的 gizmo 调强度与风速。设置面板用 `Listing_Standard`，与 `AOPatchMod` 现有设置面板同风格。

---

## 10. 性能预算

| 项 | 量级 | 频率 |
|---|---|---|
| 重算 `outerCells` | O(πr²) | 仅 `dirty` 时 |
| 重算 `baseline` | O(outerCells) | 每模拟周期 |
| 注入 | O(注入格数) | 每模拟周期 |

- Frostpunk 升级机 radius 60 ≈ 11310 格；两台 + 原版若干源，总量约 2~3 万格。
- 每模拟周期（默认 6 tick）一次，纯数组累加 + 一次乘法，约 **0.3~0.8 ms**。
- **必须**用 `Temperature.Read` 数组直读，不要 `cell.GetTemperature(map)`（每次走 Harmony patch 链）。
- `cell → index` 用 `cell.z * engine.Size + cell.x` 直接算，不走 `AtmosphereInterface.ResolveIndex`（避免每格一次 `Component(map)` 查找）。
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

---

## 14. 后续

> 状态：**已落码，`dotnet build -c Debug` 通过（2026-11-13）**。实机已验证范围效果生效；v1.3 按实测观感调整了喷流（改 AO 原生 AirMover）、默认强度（8℃/秒）与逐建筑滑块。

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

- 落码完成后，把本文状态改为「已实现」，并在 §12.3 补实测数据。
- 若实测发现「点源 + 范围层」在 Frostpunk 上仍有视觉热点，再评估是否引入「点源附近半径内降权」。
- 旧文档 `AO-ProxyHeat接管与三模式设计.md` 保留，但读者应先读本文。
