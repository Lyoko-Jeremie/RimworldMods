# AO 可调风洞建筑 —— 高层设计与可行性分析

> 状态：分析稿（未实现，未改任何代码）
> 决策基线（2026-11-14 用户拍板）：
> 1. 定位 = **多功能可切换设备**（控温送风 / 风洞试验 / 气体吹换）；
> 2. 制冷 **允许不守恒**（凭空制冷，简单实现）；
> 3. 先出本分析文档，再决定是否落地。
>
> 本文所有结论均带 `文件:行` 证据，来源为 AO 反编译源码 `f:\RimworldCodeIL\AtmosphereOverhaul\`。

---

## 0. 结论摘要（TL;DR）

1. **可以，但风洞不能做成"大号风机"**。AO 的架构是「全局大气 + 局部强迫」两层，局部强迫目前只有涡旋 `VortexForcing` 与光束 `BeamForcing` 两种；风洞本质是**第三类局部强迫**：对一段管状空间同时"钉住"速度场与温度场。
2. **三根旋钮里，两根有现成机制，一根必须新增**：
   - 定向：**免费**。`FaceOpen = 两格遮挡乘积` + 半拉格朗日采样的 `ObstacleMask` 加权，管状几何天然一维。
   - 任意风速：**免费**。`MaintainTargetWind` 连续源是公开 API（`AtmosphereInterface.RegisterContinuousSource`），无需 Harmony。
   - 任意温度：**必须新增**。AO 的三条温度通道没有一条能"钉住"：射流温度是"地板"语义、连续源温度速率会被扩散与松弛抹平、`BeamForcing` 是私有且仅露天生效。需要一条 after-solver 的温度钉住通道（Harmony 插在 `ApplyTargetWindSourcesAfterSolverUnsafe` 的 Postfix）。
3. **最高层发现：有效风速与玩家的 simulation interval 强耦合**。半拉格朗日回溯距离 = `|v| × TimeStep`，而 `TimeStep` 随 interval 线性缩放（`AtmosphereTuningManager.ScaleForInterval`）。interval=6 时 120 速需 24 格长管；interval=30 时同样 120 速需 **120 格**——不可实现。因此建筑必须做 `v_max = L / TimeStep` 的运行时钳制，否则风洞在低性能设置下会静默失真。
4. **推荐方案 S（强迫式）**，方案 H（AirMover 真实闭环）留作"需要真实气体成分输运"的扩展。二者对照见 §5。

---

## 1. 需求拆解与现实模型

### 1.1 用户需求的三层拆解

| 需求原话 | 技术含义 | 判定 |
|---|---|---|
| "像风洞那样" | 一段截面均匀、沿程稳定、可调的定常流场 | 需要"速度场钉住"，不是"点源射流" |
| "任意温度" | 目标温度独立于环境温度，含低于环境 | AO 现有通道全部不支持"低于"，必须新增钉住 |
| "任意风速" | 0 ~ 上限连续可调，且截面均匀 | 钉住天然均匀；射流不均匀 |
| "定向气流" | 一维、只沿轴向 | 管状几何 + FaceOpen 天然成立 |
| "建筑" | 可建造、可供电、可切换 | 需要一个 Comp + 一套 Gizmo |

### 1.2 现实风洞模型与 AO 的对应

现实闭合回流风洞的四部件：

| 现实部件 | 现实作用 | AO 对应物 | 是否已存在 |
|---|---|---|---|
| 收缩段 contraction | 截面收缩 → 同流量加速、整流 | `faceOpen` 收窄 + `AddAirMoverVolumeRate` 的散度 | 半有（AirMover 路径） |
| 试验段 test section | 截面均匀、沿程稳定的可调流场 | **速度钉住矩形区** | 有范式（`CompSingleFieldFan`） |
| 换热器 heat exchanger | 把循环气调到设定温度（含冷却） | **温度钉住**（after-solver 直写） | **无**（仅 Beam 私有实现） |
| 回流段 + 风机 | 闭合回路、持续推动 | `AirMoverHasReturnPath`（空气连通分量判定） | 有（`AirMover` 路径） |

**关键认知**：现实靠"收缩段 + 长直段"把流场"整理"成均匀；游戏里可以直接"钉住"，跳过整理过程。**风洞的物理本质是"在一段指定空间里把速度场强制成均匀定值"，而不是"制造一股气流"**。

### 1.3 AO 的架构定位

AO 分两层（`AtmosphereMapComponent.StartSimulationTask`，`RimWorld/AtmosphereMapComponent.cs:2962`）：

```
全局大气（GpuAutodriveGlobalClimateEngine / AmbientWind）
   ↓ 提供背景温度、风、气压
局部大气（LocalAtmosphereEngine，逐格 2.5D 场）
   ↓ AdvanceProvisional（对流+扩散+相变） → ProjectAndFinalize（压力投影+边界）
局部强迫（LocalForcing.BeforeStep / AfterSolve）
   ↓ VortexForcing（涡旋） / BeamForcing（光束）
```

风洞应当是 `LocalForcing` 的第三个成员，而不是 `AirMover` 的一个变种。理由见 §2。

---

## 2. 为什么不是"大号风机"

AO 已有的"定向气流建筑"是风扇（`Defs/Buildings_Fans.xml` + `Buildings/CompSingleFieldFan.cs`）：

- `CompSingleFieldFan.TargetWindSpeed`（`Buildings/CompSingleFieldFan.cs:58`）= `SingleFieldFanMath.WindSpeed(输出功率, maxPower, ratedFlow, area)`，公式 `ratedFlow / area * pow(min(1, p/max), 1/3)`（`Buildings/SingleFieldFanMath.cs`）。
- `ReconcileWind()`（:213）对 `windFieldSize` 矩形**每格**注册一个连续源，rates = `{ MaintainTargetWind = true, TargetWind = rotation.AsVector2 * TargetWindSpeed, TargetWindBlendPerTick = props.windResponsePerTick }`。

即：**AO 自己的风扇就是"矩形区域速度钉住"**，不是射流、不是压力驱动。这是 AO 认可的合法范式，风洞沿用它不需要发明新物理。

而真正的"射流风机"是 `AirMover`（`CompPairedJet` / 涡轮喷气），它有两个对风洞致命的性质：

1. **沿程衰减**：`JetTransport.Advance` 每段用 `fraction2 = min(0.5, Speed*TimeStep/LastSubsteps)` 做指数混掺（`Simulation/JetTransport.cs`），速度沿 path 逐段被环境稀释。
2. **温度是"地板"语义，不能制冷**：`AtmosphereMapComponent.cs:651`

```csharp
float num9 = Mathf.Max(num3, settings.ExhaustTemperatureFloor);
```

`num3 = max(环境温度, 入口两侧均温)`。**射流永远不会低于入口温度**——所以"冷风洞"用 AirMover 在物理上做不到。

→ 结论：**风洞主线用"钉住"，AirMover 只在需要真实体积/气体成分输运时作为可选模块。**

---

## 3. 三根旋钮的可行性（逐条证据）

### 3.1 定向：管状几何天然一维（免费）

三条证据：

**(a) 面开度是乘积。** `RimWorld/MapObstacleMaskBuilder.cs` 第二遍：

```csharp
faceOpenX[i] = i.x < w-1 ? mask[i] * mask[i+1] : 0f;
faceOpenZ[i] = mask[i] * mask[i+w] : 0f;
```

中间隔一堵 Impassable 墙（`mask=0`）→ 该面开度 0。速度/标量的横向通道被物理切断。

**(b) 速度扩散只走开面。** `Simulation/LocalAtmosphereEngine.cs:3818` `DiffuseVelocity`：权重全部取自 `FaceOpenX[i-1]/FaceOpenX[i]/FaceOpenZ[i-w]/FaceOpenZ[i]`；`target = (source + (邻域加权均值 - source) * diffusion) * obstacleMask`。横向开度为 0 时，邻域均值只来自轴向 → 扩散只在轴向发生。

**(c) 标量对流采样按遮挡加权。** `Simulation/LocalAtmosphereEngine.cs:4198` `BilinearSampleOpenAir`：

```csharp
num5 = num2*num4*ObstacleMask[i1]; // 四角各乘 mask
...
return num9 <= 1e-4f ? fallback : (Σ source*weight) / num9;
```

回溯点若落在墙上，该角权重 0；四角全墙则回退为自身值。**温度不会穿墙输运**。

→ 只要"风洞"是「两侧 Impassable 墙 + 仅轴向开口」的管，一维性自动成立，**不需要任何代码**。

> 注意：`faceOpen = mask[i] * mask[i+1]` 意味着**相邻两格都必须开放**才通畅。所以管内不能有半格阻挡物（家具、门、植株）。设计上要求"管腔格保持空旷"。

### 3.2 任意风速：公开 API 即可（免费）

AO 的风扇之所以能"钉住"，靠的是 `AtmosphereMapComponent.ApplyTargetWindSourcesAfterSolverUnsafe`（`RimWorld/AtmosphereMapComponent.cs:2744`）：

```csharp
private void ApplyTargetWindSourcesAfterSolverUnsafe() {
  float[] read1 = engine.VelocityX.Read;  float[] read2 = engine.VelocityZ.Read;
  foreach (var s in continuousSources.Values) {
    if (s.RatesPerTick.MaintainTargetWind && s.RatesPerTick.TargetWindBlendPerTick > 0 && s.LastApplicationDeltaTicks > 0) {
      float num = 1f - Mathf.Pow(1f - s.RatesPerTick.TargetWindBlendPerTick, s.LastApplicationDeltaTicks);
      Vector2 v = Vector2.ClampMagnitude(s.RatesPerTick.TargetWind, settings.MaxWindSpeed);
      read1[cellIndex] += (v.x - read1[cellIndex]) * num;
      read2[cellIndex] += (v.y - read2[cellIndex]) * num;
      s.LastApplicationDeltaTicks = 0;
    }
  }
}
```

三个关键性质：

1. **在 `ProjectAndFinalize` 之后执行**（主循环 `:3010`，Project 在 `:3005`）→ 钉住的速度不会被压力投影改写。这是"速度钉住必须后置"的代码依据。
2. **`Vector2.ClampMagnitude(..., MaxWindSpeed)`** → 硬上限 `AtmosphereTuning.xml` 的 `MaxWindSpeed=120`。
3. **`TargetWindBlendPerTick = 1f` 时 `num = 1`** → 完全覆盖，即硬钉住。

而且注册通道是**公开 API**（`RimWorld/AtmosphereInterface.cs:300`）：

```csharp
public static AtmosphereSourceHandle RegisterContinuousSource(
    Map map, Thing owner, string sourceKey, IntVec3 cell, AtmosphereSourceRates ratesPerGameTick)
public static bool UpdateContinuousSource(...)   // :317 / :329
public static bool UnregisterContinuousSource(...) // :343
public static bool TryGetContinuousSourceHandle(...) // :360
```

`AtmosphereSourceRates`（`RimWorld/AtmosphereSourceRates.cs:12-24`）字段：

```csharp
public float TemperaturePerTick;  public float GroundTemperaturePerTick;
public float MoisturePerTick;     public float MistPerTick;
public Vector2 WindPerTick;       public float PressurePerTick;
public float OxygenPercentPerTick;
public bool MaintainTargetWind;   public Vector2 TargetWind;
public float TargetWindBlendPerTick;
```

→ **速度钉住零 Harmony、零反射、零私有字段**。我们只需要像 `CompSingleFieldFan.ReconcileWind()` 那样，对风洞矩形每格注册/更新一个源。

**成本**：`ApplyTargetWindSourcesAfterSolverUnsafe` 是 `O(源数)`；一个 3×24 的风洞 = 72 个源，每粗步 72 次 `+=`。相比 `JetTransport.Advance` 的 `O(512 × 源数 × 128)`，**差 3~4 个数量级**。

### 3.3 任意温度：这是唯一的真难点（必须新增）

AO 现有三条温度通道，逐条说明为什么都不行：

**(a) 射流出口温度 = 地板语义，只能加热**

`RimWorld/AtmosphereMapComponent.cs:651`：`num9 = Mathf.Max(num3, settings.ExhaustTemperatureFloor)`。`num3` 是 `max(环境温度, 入口两侧均温)`。所以 `ExhaustTemperatureFloor` 只能把温度**往上顶**，写 -100 也无效。

**(b) 连续源温度速率会被 solver 抹平**

`AtmosphereSourceRates.TemperaturePerTick` 走 `EnqueueTemperatureExternalDelta`（`RimWorld/AtmosphereMapComponent.cs:2631`），在主循环第 4 步 `engine.ApplyTemperatureDeltas` 消费——**在 `AdvanceProvisional` 之前**。而 `AdvanceProvisional`（`Simulation/LocalAtmosphereEngine.cs:2943`）内部：

```
UpdatePressureMode
DiffuseVelocity → AdvectVelocity → ApplyCoriolis
BeginScalarConservation → LimitScalarTraceToWalls
AdvectScalar(Temperature) → AdvectScalar(Moisture) → AdvectScalar(Mist) → AdvectOxygen
RestoreWindAfterScalarTransport → EndScalarConservation
DiffuseScalar(Temperature, TemperatureDiffusion=0.24, iterations=3)
DiffuseScalar(Moisture, 0.12, 3) → DiffuseOxygen
BeginPhaseChangeCapture → UpdateHumidityAndThermodynamics → ClampThermodynamicFields
```

温度 delta 注入后，先被抛射到下游（`AdvectScalar`），再被扩散（0.24 × 3 次）抹平，还要叠加室外松弛。**速率给得再大，稳态也到不了"任意值"**——它是一阶系统，只能偏离环境一个有限量。

**(c) 唯一"钉住"的先例是 Beam，但私有且仅露天**

`Simulation/LocalForcing.cs` 的 `AfterSolve(...)` 在 `CoreRadius` 内把 `Temperature.Read` 钉到 `CoreCelsius`、地温钉到 `GroundCelsius`、氧清零。但：

- 它是 AO 内部 `static` 方法，不可从外部扩展；
- 只对 `roofMask <= 0.5`（露天）生效，屋顶下无效。

→ **要"任意温度"，必须新增一条 after-solver 的温度钉住通道。** 这是本设计唯一必须写 Harmony 的地方。

#### 插入点选择

主循环尾部（`RimWorld/AtmosphereMapComponent.cs:3009-3014`）：

```csharp
this.engine.ApplyVelocityDeltasAfterSolver(...);              // :3009
this.ApplyTargetWindSourcesAfterSolverUnsafe();               // :3010  ← 速度钉住
LocalForcing.AfterSolve(...);                                 // :3011  ← 涡旋/Beam 覆盖
this.temporalPatchManager.RebaseAfterGlobalProjection(...);   // :3012
this.engine.PublishSnapshot();                                // :3013  ← Snapshot 定版
this.temporalPatchManager.AfterGlobalSnapshotPublished();     // :3014
```

三个候选：

| 候选位置 | 优点 | 缺点 |
|---|---|---|
| `ApplyTargetWindSourcesAfterSolverUnsafe` 的 **Postfix** | 上下文齐全（`this` = AMC，可拿 `engine`/`settings`/`continuousSources`）；与速度钉住同址；省掉 engine→map 反查 | 之后 `LocalForcing.AfterSolve`（:3011）可能覆盖（仅当风洞走廊内有涡旋/Beam） |
| `PublishSnapshot` 的 **Prefix** | 保证是最终值 | 需要从 `LocalAtmosphereEngine` 反查所属 Map（引擎实例无 Map 字段），需维护静态表 |
| `LocalForcing.AfterSolve` 的 **Postfix** | 在 Beam 之后，不会被覆盖 | `AfterSolve` 是 static，只有 engine 参数，同样要 engine→map 反查 |

**推荐：`ApplyTargetWindSourcesAfterSolverUnsafe` 的 Postfix**。理由：它是唯一"天然携带 AMC 上下文 + 就在速度钉住处 + 遍历的正是连续源表"的位置；涡旋/Beam 与风洞同处一格的冲突本身应当在建筑层禁止（见 §6.4）。

钉住公式（与速度同构）：

```csharp
// 伪代码：Postfix(AtmosphereMapComponent __instance)
foreach (var s in __instance.continuousSources) {
  if (!s.RatesPerTick.MaintainTargetTemperature) continue;
  int i = s.CellIndex;
  if (__instance.engine.ObstacleMask[i] <= 0f) continue;
  float t = __instance.engine.Temperature.Read[i];
  float k = 1f - Mathf.Pow(1f - s.RatesPerTick.TargetTemperatureBlendPerTick, s.LastApplicationDeltaTicks);
  __instance.engine.Temperature.Read[i] = t + (s.RatesPerTick.TargetTemperatureCelsius - t) * k;
}
```

注意：

- `AtmosphereSourceRates` **没有** `TargetTemperatureCelsius` 字段，需要我们自己维护一张"风洞源表"（Comp 内的 `List<(IntVec3 cell, float target)>`），而不是往 AO 的 struct 里加字段（加字段会改 AO 的序列化格式，patch mod 不该碰）。
- 写 `Temperature.Read` 必须在 `engineLock` 内——Postfix 天然满足（调用点 `:3010` 在 `lock (this.engineLock)` 内）。
- `PublishSnapshot`（`Simulation/LocalAtmosphereEngine.cs:3194`）会 `Array.Copy(Read → Snapshot)`，所以 Postfix 写 Read 会进 Snapshot，下一帧 `SampleSnapshot` 读到的是钉住值，与连续源一致。

#### 温度钉住会不会"被下一步对流带走"

会，而且这正是我们要的：钉住发生在 solver 之后，下一帧 `AdvanceProvisional` 开头先 `AdvectScalar`，把钉住的温度沿速度场搬到下游。于是：

- **上游格**每帧被重置为 target；
- **下游格**通过半拉格朗日从上游采样，逐步趋向 target。

这就是"气流携带温度"的机制——**风洞的"冷风"不是出口一个点冷，而是"上游被钉冷 + 对流把它送下去"**。

推论（重要）：**温度钉住必须覆盖进气段 + 整个试验段，只钉出口是无效的**。若只钉出口格，回溯采样会从出口上游（未钉住的格）取温度，等于没钉。

### 3.4 几何约束：半拉格朗日回溯距离 → 建筑长度下限

`Simulation/LocalAtmosphereEngine.cs:4145` `TraceBackward`：

```csharp
pastX = (float) x - dt * velocityX;
pastZ = (float) z - dt * velocityZ;
// 只 clamp 到地图边界，不检查墙
```

`AdvectScalar` 用 `TraceBackward`（bounded 到地图），`AdvectVelocity` 用 `TraceBackwardUnbounded`。回溯是**直线**，每步跨 `|v| × dt` 格。

`dt = settings.TimeStep`，而 `AtmosphereTuningManager.ScaleForInterval`（`Simulation/AtmosphereTuningManager.cs:244-271`）：

```csharp
float ratio = Mathf.Max(0.1f, intervalTicks / baseIntervalTicks);   // base = 30
settings.TimeStep = source.TimeStep * ratio;                        // source.TimeStep = 1
```

即 `TimeStep = interval / 30`（默认 interval=6 → 0.2）。

**回溯格数 = |v| × TimeStep**：

| Simulation interval | TimeStep | v=20 | v=60 | v=120 |
|---|---|---|---|---|
| 3 | 0.1 | 2 | 6 | 12 |
| 6（默认） | 0.2 | 4 | 12 | 24 |
| 12 | 0.4 | 8 | 24 | 48 |
| 20 | 0.67 | 13 | 40 | 80 |
| 30 | 1.0 | 20 | 60 | 120 |

含义：

1. **试验段长度 L 必须 ≥ 回溯格数 + 2**，否则回溯点越过管口，从管外取温度 → 冷气被环境稀释。
2. **有效风速受管长钳制**：`v_effective ≤ (L - 2) / TimeStep`。interval=30 时 30 格管最多 28 速。
3. **风洞必须做运行时钳制**：`EffectiveMax = min(MaxWindSpeed, (L-2) / TimeStep)`，否则玩家把 interval 从 6 调到 30，风洞会静默失效（UI 仍显示 120，实际输出被稀释）。

> 这是本设计最反直觉、也最容易被忽略的一条。它把"建筑尺寸"与"玩家的性能设置"耦合起来。

---

## 4. 方案 S：强迫式多功能风洞（推荐）

### 4.1 三模式矩阵

| 模式 | 速度钉住 | 温度钉住 | 体积/气体源 | 对应现实 | 主要用途 |
|---|---|---|---|---|---|
| **A 控温送风** | 有（可低可高） | 有 | 无 | 风洞 + 换热器 | 大号空调 / 热风炮 / 冷风幕 |
| **B 风洞试验** | 有（高速） | 可选 | 无 | 纯风洞 | 吹飞 pawn/物品、测结构抗风 |
| **C 气体吹换** | 有（中速） | 可选 | 有 | 风洞 + 排气 | 吹毒气、抽真空、控氧 |
| **关闭** | 无 | 无 | 无 | — | 待机 |

切换逻辑：

- 模式切换时**只改变注册的源集合**（速度源恒在，温度源按模式增删，体积源按模式增删），不重建建筑。
- **禁止每 tick 重注册**：`RegisterContinuousSource`（`RimWorld/AtmosphereMapComponent.cs:2409`）是 `foreach` 全字典线性查 `(Owner, SourceKey)`，`O(源数)`。只在模式/档位/朝向变化时 `UpdateContinuousSource`（`O(1)`，`TryGetValue`）。

### 4.2 建筑几何

推荐 **"主机 + 玩家自建管腔"** 的变体（与 AO 涡轮喷气一致）：

```
    墙  墙  墙  墙  墙  墙
    墙 [进气][试验段 × L][出风] 墙
    墙  墙  墙  墙  墙  墙
         ^
       主机（3×3，Impassable，供电）
```

- 主机：3×3，`Fillage=2`（Impassable，默认挡空气），`blockWind=true`，`holdsRoof=false`（不挡雨，让管内"半露天"，避免厚岩顶 15℃ 对抗）。
- 管腔：Terrain（玩家自建墙围出），保持空旷（不放家具/门/植株，否则 `faceOpen` 被打断）。
- 风洞壁 = 普通墙即可（`Fillage=2` → `AirCanMoveThrough=false` → `obstacleMask=0`），**不需要专用建筑**。
- 若要做"预制整机"（一个 3×N 建筑占满管腔），则管内不能放实验对象，失去"风洞试验"的意义——**不推荐**。

备选：写一个 `AO_WindTunnelWall` Def（`Fillage=2` + `BuildingAtmosphereExtension{ airPassable=false, windToleranceFactor=... }`），给玩家一块"专用风洞壁"，好处是能在 UI 上标注"这是风洞的一部分"。非必需。

### 4.3 速度钉住（零 Harmony）

Comp 草案（不实现，只给接口）：

```csharp
public class CompWindTunnel : ThingComp {
    // 运行时状态（需 ExposeData 序列化）
    private float targetSpeed;          // 0 ~ EffectiveMax
    private float targetTemperature;    // 温度钉住目标（模式 A/B 可选）
    private WindTunnelMode mode;        // A/B/C/Off
    private readonly List<IntVec3> tunnelCells = new();   // 管腔格缓存
    private readonly List<AtmosphereSourceHandle> handles = new();
}
```

注册（仿 `CompSingleFieldFan.ReconcileWind`，`Buildings/CompSingleFieldFan.cs:213`）：

```csharp
var dir = parent.Rotation.AsVector2;
var rates = new AtmosphereSourceRates {
    MaintainTargetWind = true,
    TargetWind = dir * EffectiveSpeed,
    TargetWindBlendPerTick = 1f,     // 硬钉住
};
foreach (var cell in tunnelCells) {
    if (!AtmosphereInterface.UpdateContinuousSource(map, parent, handle, cell, rates))
        handle = AtmosphereInterface.RegisterContinuousSource(map, parent, SourceKey(i), cell, rates);
}
```

要点：

- `TargetWindBlendPerTick = 1f` → `num = 1 - pow(0, dt) = 1` → 完全覆盖，硬钉住。
- `EffectiveSpeed = Mathf.Min(targetSpeed, (L-2) / settings.TimeStep, settings.MaxWindSpeed)`。
- 风速改变时只 `UpdateContinuousSource`（`O(1)` 每次），共 N 次。
- 30 tick 校验一次 handle 有效性（仿 `WindRegistrationIsCurrent`，:185），防止 AO 重建/注销后悬挂。

### 4.4 温度钉住（唯一的 Harmony）

- 目标格：**进气段 + 试验段全部格**（不是只出口，见 §3.3 推论）。
- 插入点：`ApplyTargetWindSourcesAfterSolverUnsafe` 的 Postfix（见 §3.3 插入点分析）。
- 数据：Comp 自身维护 `List<(int cellIndex, float targetCelsius, float blend)>`，按 `Map` + `Comp` 分组缓存；Postfix 里遍历这张表，不遍历 AO 的连续源字典（避免污染 AO 数据、避免反序列化兼容问题）。
- 每粗步成本：`O(风洞格数)`，与速度钉住同量级。

**温度"任意"的上下限**（`Defs/AtmosphereTuning.xml`）：

- 下限：`MinimumTemperatureCelsius = -273`（模型硬边界）。
- 上限：`MaxHumidityModelTemperatureCelsius = 200`（超过后湿度模型不再准确）。
- 建议 UI 范围：**-100 ~ +200 ℃**；超出仍在物理上"可写"，但语义不保证。

### 4.5 模式 C：气体吹换

两条实现，按需求选：

| 需求 | API | 代价 |
|---|---|---|
| 单格喷气（吹毒/补氧） | `AtmosphereInterface.AddVolumeSourceAtTemperature(map, cell, cellVolumes, oxygenPercent, gasCelsius)`（`RimWorld/AtmosphereInterface.cs:209`） | 走 `AddPointVolume` → 看分量：开放分量转散度，封闭分量转静压记账（`LocalAtmosphereEngine.AddPointVolume`，`Simulation/LocalAtmosphereEngine.cs:835`） |
| 真实体积输运（吹换整管） | `RegisterAirMover(...)` + `AtmosphereAirMoverSettings` | 走 `JetTransport.Advance`，`O(512×源×128)`，最贵 |

建议：**模式 C 默认用 `AddVolumeSourceAtTemperature` 单格注入**，只有当玩家明确要"把整管气体推出去"时才启用 AirMover。理由：`AddPointVolume` 对"开放分量"会转成散度 → 压力 → 速度，与我们的速度钉住**可能互相打架**（散度产生的速度会被钉住覆盖）。若二者共存，需要把体积源放在钉住段的**下游外**，让它只产生"排气"效果。

> 待验证项：速度钉住 + 开放分量体积源同时存在时，`ProjectAndFinalize` 的投影结果是否稳定（可能出现"气压被钉住的速度场持续抽走"导致压力发散）。需要实测。

### 4.6 不守恒制冷：记账方式与可选排热

用户已拍板"可以不守恒"。但文档必须写清代价，以便将来回退：

**主方案（不守恒）**：

- 温度钉住直接写 `Temperature.Read[cell] = target`（每步覆盖），**不做能量记账**。
- 耗电模型（建议）：`W = baseW + k * |target - ambient| * cellCount`，即"越偏离环境越耗电"，让玩家有成本感，但没有任何物理汇。
- UI 明示："本设备为 AO 未建模的能量源，不参与全局热量守恒"。

**可选开关（守恒排热，默认关）**：

- 每步把 `(target - T_before) * cellCount * airHeatCapacity` 的等量正/负热注入到"出口外 1 格 + 大气"：
  - 制冷时 → 向出口外注入正热（冷凝器）；
  - 加热时 → 从出口外抽取热量（热泵）。
- 注入点用 `AtmosphereInterface.AddTemperatureDelta(map, cell, delta)`（`:228`，走 solver 前队列）。
- 代价：需要选"排热汇"（室外大气 / 地热 / 专用排热管），实现复杂度显著上升。

→ **建议先做不守恒版跑通，排热作为后续可选 Patch**。这符合用户"先简单实现"的偏好。

---

## 5. 方案 H：物理闭环风洞（对照，不推荐作为主线）

### 5.1 技术上可行

- `AirMover` 有回路判定：`AirMoverHasReturnPath`（`Simulation/LocalAtmosphereEngine.cs:627-762`）用 `FaceOpen` 做 flood fill 分空气连通分量；两端同分量或两者都 open → 有回路。
- 开式风洞（两端都通大气）→ 天然有回路 → `TryGetNozzleReturnPath`（:764）走 Solve 分支，正常产生射流。
- 真实闭环（U 形管两端接回同一空腔）→ 两个分量 open → 有回路。

### 5.2 为什么不做主线

| 维度 | 问题 |
|---|---|
| 温度 | **不能低于入口温度**（`AtmosphereMapComponent.cs:651` 地板语义）。冷风洞直接不可行。 |
| 沿程均匀性 | `JetTransport` 逐段指数混掺，速度沿管衰减；截面不均匀（点/小面出口）。 |
| 性能 | `O(LastSubsteps ≤ 512 × 源数 × pathLen ≤ 128)`，是 AO 全程最重的分支（见 `AO-模块功能与性能分析.md` §5.3）。 |
| 参数耦合 | 流量 `CubicMetresPerTick`、`AirColumnHeightMetres`、`ExhaustSpeed`、压比、demand 归一（`256/max(demand)`）互相牵制，玩家很难通过一个 UI 旋钮得到"我要 80 速"。 |
| 密封记账 | 若管腔被判定为封闭分量，体积源会转成 `AddSealedComponentPressure`，风洞变成"加压罐"而不是"风洞"。 |

→ **方案 H 的正确名字是"闭环通风管道"**，适合"整管换气/控氧"，不适合"风洞试验"。

---

## 6. 硬约束与红线

### 6.1 管长与 interval 耦合（必须钳制）

见 §3.4。`EffectiveMax = min(MaxWindSpeed, (L-2)/TimeStep)`。

**红线**：不允许在 UI 上显示超过 `EffectiveMax` 的数值，否则玩家会认为"风洞坏了"。

### 6.2 风害反噬

`WindStructuralLoad` / `WindPassableLoad` / `WindTransport` 每 30 tick 把风压作用到：

- 结构（`edificeGrid` 过滤，非蓝图/挡光/有 HP/非自然岩）；
- 家具与植物（`ThingRequestGroup.BuildingArtificial` / `.Plant`）；
- 可搬运物 + pawn（`HaulableEver` / `AllPawnsSpawned`）。

后果：

- 风洞内的高风速会**吹飞试验对象**（这是 B 模式的核心玩法，保留）；
- 也会**吹坏风洞自身的墙/门/主机**（`blockWind` 或 `windToleranceFactor` 保护）；
- 还会**吹动风洞外的物品**（若管口朝外，射流会溢出到室外）。

**红线**：主机与专用风洞壁必须通过 `BuildingAtmosphereExtension.windToleranceFactor`（`Simulation/BuildingAtmosphereExtension.cs`）提高抗风系数；管腔内的"试验对象"不保护。

### 6.3 屋顶/地温对抗

- 非屋顶格：`OutdoorTemperatureRelaxation=0.15/步` 拉向环境温度（`AtmosphereTuning.xml`），温度钉住必须在其后（Postfix 已满足）。
- 屋顶格：`AtmosphereGroundEnergyKernel.Advance` 用地温换热；`roofThermalRate<=0`（厚岩顶）且 `Tair>15` 时额外向 15℃ 拉（`Mathf.Min(0.5f, 0.0015f*timeStep)`）。
- 结论：**风洞不要建在厚岩顶下**（会与 15℃ 恒温对抗）；也不要全露天（环境松弛强）。建议"普通建造屋顶"或"无屋顶但避开岩顶"。

### 6.4 与涡旋/Beam 冲突

`LocalForcing.AfterSolve`（:3011）在速度钉住（:3010）之后执行，会覆盖同格的涡旋/Beam 结果。若风洞格与涡旋/Beam 重叠：

- 速度：被涡旋覆盖，风洞失效；
- 温度：我们的 Postfix 在 :3010，会被 :3011 的 Beam 覆盖（若露天）。

**红线**：在 Comp 的 `CompTick` 里检测"风洞管腔格是否落在任一 VortexForcing/BeamForcing 影响范围内"，若是则 UI 报警并暂停该格钉住（或直接禁止建造）。

### 6.5 管腔必须空旷

`faceOpen = mask[i]*mask[i+1]` 要求相邻两格都开放。管腔里放：

- 门（关闭时 `AirCanMoveThrough` 走 `Building_Door` 特判，关闭门 airPassable=false → 挡）；
- 家具（`Fillage != 2` 时 airPassable=true，但 `AirFill` 会进 `dampingMask`，增加阻尼）；
- 植株（`dampingMask += growth*fillPercent*0.8`）。

→ 管腔允许"能通过的物品"，但**不要放建筑/家具/植株**，否则阻尼与面开度都会被破坏。

---

## 7. 性能预算

设风洞 `3 × L` 格，`L = 24`（默认 interval=6 下支持 120 速），共 72 格：

| 项 | 频率 | 成本 |
|---|---|---|
| 速度钉住（`ApplyTargetWindSourcesAfterSolverUnsafe`） | 每粗步（6 tick） | 72 次 `+=` |
| 温度钉住（我们的 Postfix） | 每粗步 | 72 次读+写 |
| `UpdateContinuousSource` | 仅档位/朝向变化 | 72 × `O(1)` |
| `RegisterContinuousSource` | 仅建造/模式切换 | 72 × `O(源数)`（一次性） |
| `WindLoadField.EnsureBuilt` | **每 tick** | 全图 N 次扫描（与风洞无关，但风洞提高了局部风速平方，使"风害系统"更活跃） |

对比 `JetTransport.Advance`（方案 H）：`O(512 × 源数 × 128)`。**方案 S 便宜 3~4 个数量级**。

隐藏成本：

- `WindPathCosts`（`RimWorld/WindPathCosts.cs`）：风场变化会让原版 `PathGrid` 全图重算。风洞持续高风速 → 每 tick 都在 windy 状态 → 寻路代价场持续刷新。**这是方案 S 唯一的显著隐藏成本**，需要在实测中确认。
- `WindStructuralLoad` 等 30 tick 实算：风洞内高风速会让 `count` 成员的风压普遍升高，但成员数不变，成本不变。

---

## 8. 与 AO 其他系统的交互矩阵

| 系统 | 交互 | 风险 | 处置 |
|---|---|---|---|
| `WindStructuralLoad` | 风洞内结构受风压 | 吹坏主机/墙 | `windToleranceFactor` 保护 |
| `WindPassableLoad` | 家具/植物受风压 | 吹飞管腔内容物 | 保留（玩法） |
| `WindTransport` | 可搬运物被吹走 | 物品飞出管口 | 保留（玩法） |
| `WindPathCosts` | 寻路代价场失效 | 性能 | 实测 |
| `WindLoadField` | 每 tick 全图扫 | 固定成本 | 不可优化（AO 自身） |
| `BeamForcing` | 温度覆盖 | 冲突 | §6.4 检测 |
| `VortexForcing` | 速度覆盖 | 冲突 | §6.4 检测 |
| `CompAirVent` | 管腔若有通风口，`BuildVents` 会改 `faceOpen` | 打断一维 | 禁止管腔装通风口 |
| `ApplyWallLeakage` | 管壁泄漏 | 温度/压力泄漏 | 无需处理（墙默认不漏） |
| 全局大气 | `AmbientWindNudgeFraction=0.05` 在露天把速度拉向环境风 | 风洞出口外的射流被环境风偏转 | 保留（自然） |
| `ProxyHeat`（本项目） | 若风洞制冷不守恒，与 ProxyHeat 的守恒记账冲突 | 记账不平 | 文档标注"风洞为独立能量源" |

---

## 9. 待决策项

1. **建筑形态**：主机 + 玩家自建管腔（推荐），还是预制整机（3×N 占满管腔，不能放实验对象）？
2. **温度钉住范围**：进气段+试验段全钉（推荐），还是"进气段钉 + 试验段放任"（更省格、但温度沿程衰减）？
3. **排热开关**：是否要做"守恒排热"的可选开关（默认关）？
4. **模式 C 的输运方式**：`AddVolumeSourceAtTemperature` 单格注入（推荐先做），还是 `AirMover` 真体积输运？
5. **风速钳制策略**：`EffectiveMax = min(MaxWindSpeed, (L-2)/TimeStep)` 是否在 UI 上直接显示钳制后的值？
6. **是否需要"风洞壁"专用 Def**（带 `windToleranceFactor` 与 UI 标注），还是用普通墙？
7. **是否需要"试验对象固定装置"**（防止 B 模式下物品被吹出管口丢失）？

---

## 附录 A：证据索引

| 结论 | 文件:行 |
|---|---|
| 主循环顺序（速度钉住在 Project 之后、AfterSolve/Snapshot 之前） | `RimWorld/AtmosphereMapComponent.cs:3009-3014` |
| 速度钉住实现 + `ClampMagnitude(MaxWindSpeed)` | `RimWorld/AtmosphereMapComponent.cs:2744-2769` |
| 射流温度地板语义（不能制冷） | `RimWorld/AtmosphereMapComponent.cs:651` |
| 连续源温度速率入口 | `RimWorld/AtmosphereMapComponent.cs:2631` |
| `ApplyContinuousSourcesUnsafe`（每源 1 格） | `RimWorld/AtmosphereMapComponent.cs:2534` |
| `RegisterContinuousSource`（O(源数) 线性查） | `RimWorld/AtmosphereMapComponent.cs:2409` |
| `AdvanceProvisional` 全序列（AdvectScalar → DiffuseScalar） | `Simulation/LocalAtmosphereEngine.cs:2943-2970` |
| `ProjectAndFinalize` 全序列（含 `ClampVelocityMagnitude`） | `Simulation/LocalAtmosphereEngine.cs:2972-2995` |
| `DiffuseVelocity`（FaceOpen 权重） | `Simulation/LocalAtmosphereEngine.cs:3818` |
| `TraceBackward`（直线回溯，不查墙） | `Simulation/LocalAtmosphereEngine.cs:4145` |
| `BilinearSampleOpenAir`（ObstacleMask 加权，全墙回退） | `Simulation/LocalAtmosphereEngine.cs:4198` |
| `AdvectScalar`（半拉格朗日，dt=TimeStep） | `Simulation/LocalAtmosphereEngine.cs:3506` |
| `faceOpenX/Z = mask[i]*mask[i+1]` | `RimWorld/MapObstacleMaskBuilder.cs`（Rebuild 第二遍） |
| `ComputeDampingScale`（0.001 + 1.6·fill + 0.8·plant，clamp 1.9） | `RimWorld/MapObstacleMaskBuilder.cs` |
| 空气连通分量 / 回路判定 | `Simulation/LocalAtmosphereEngine.cs:627-762` |
| `TryGetNozzleReturnPath` / `IsAirMoverPortOpen` | `Simulation/LocalAtmosphereEngine.cs:764-792` |
| `JetTransport.Advance`（substeps ≤512, path ≤128） | `Simulation/JetTransport.cs:23-106` |
| `AtmosphereInterface` 公开 API 全表 | `RimWorld/AtmosphereInterface.cs:16-460` |
| `AtmosphereSourceRates` 字段 | `RimWorld/AtmosphereSourceRates.cs:12-24` |
| `AtmosphereDeltas` 字段 | `RimWorld/AtmosphereDeltas.cs:12-21` |
| `CompSingleFieldFan.ReconcileWind` | `Buildings/CompSingleFieldFan.cs:213-245` |
| `SingleFieldFanMath.WindSpeed` | `Buildings/SingleFieldFanMath.cs` |
| `ScaleForInterval`（TimeStep = interval/30） | `Simulation/AtmosphereTuningManager.cs:244-271` |
| 地面能量核（屋顶换热 / 厚岩顶 15℃） | `Simulation/AtmosphereGroundEnergyKernel.cs` |
| `BuildingAtmosphereExtension`（airPassable / windToleranceFactor） | `Simulation/BuildingAtmosphereExtension.cs` |
| `LocalForcing.AfterSolve`（Beam 仅露天） | `Simulation/LocalForcing.cs` |
| `MaxWindSpeed=120` / `AmbientWindNudgeFraction=0.05` / `OutdoorTemperatureRelaxation=0.15` / `TemperatureDiffusion=0.24` | `Defs/AtmosphereTuning.xml` |
| JetTransport 性能定性（最重分支） | `AO-模块功能与性能分析.md` §5.3 |
| WindPathCosts 隐藏成本 | `AO-模块功能与性能分析.md` §6.4 |

---

## 附录 B：设计参数表（草案）

| 参数 | 建议值 | 依据 |
|---|---|---|
| 风洞截面 | 3 格宽（主机 3×3） | 与 AO 涡轮喷气一致 |
| 试验段长度 L | 24 格（默认 interval=6 支持 120 速） | §3.4 回溯表 |
| 风速范围 | 0 ~ `EffectiveMax` | §3.4 钳制 |
| 温度范围 | -100 ~ +200 ℃ | `MinimumTemperatureCelsius=-273` / `MaxHumidityModelTemperatureCelsius=200` |
| `TargetWindBlendPerTick` | 1.0（硬钉住） | `AMC:2764` 的 `num` 公式 |
| 温度钉住 blend | 1.0（硬钉住） | 同构 |
| 供电 | 待定（建议按 `|ΔT|×格数` 线性） | §4.6 |
| 模式切换开销 | 仅 `UpdateContinuousSource` | §4.1 |
| handle 校验周期 | 30 tick | 仿 `WindRegistrationIsCurrent` |
