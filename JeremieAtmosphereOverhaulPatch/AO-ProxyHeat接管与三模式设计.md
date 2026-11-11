# AO · ProxyHeat 接管与三模式热场设计

> 状态：设计稿，未实现代码。
> 日期：2026-11-11。
> 依据：AO 1.7 源码（`f:\RimworldCodeIL\AtmosphereOverhaul\`，MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、ProxyHeat 源码（`f:\RimworldCodeIL\ProxyHeat\`，MVID `7358A296-A2E2-46F8-A5D3-4C48DE897B1C`）、Frostpunk Heat Generator Tower Def（`294100/3545379519`）。
> 配套文档：`AO源码全景阅读报告.md`、`AO-Frostpunk发电机分析.md`、`AO兼容层重构设计.md`、`AO-CCC与VTE重设计.md`。

---

## 0. 结论摘要

1. **ProxyHeat 的全局开关 `enableProxyHeat` 关不掉读值**。`GetTemperatureOutcomeFor` 不检查它，所以"提示玩家去关"是无效操作。必须由本 mod 用 Harmony Prefix 拦截。
2. **ProxyHeat 的 `tempOutcome` 是读数魔法，不是能量**。在 AO 的物理场里，60 格半径的室外暖区需要 10⁹ W 量级，而大型机只有 10⁵ W 量级。所以接管之后不能"原样复现"，只能"用场模拟它的意图"。
3. **三模式是叠加层**，不替换 AO 的 `CompHeatSourceAtmosphere`。它们模拟三种不同的物理效应：辐射照地、辐射照空气、强制对流。
4. **地暖与空气源是串联，但反向拖拽不可忽略**。地温 → 0.03/步 → 空气；空气源直接注入。稳态 `T = (q_a + 0.03·Tg + 0.016·T_out)/0.046`。当空气比地温高很多时地暖项变负：`T = 300`、`Tg = 25` 时抽走 `8.25 K/步`，可以是空气源的好几倍——所以"地暖目标设得很低"确实会拖累空气源，不是可以忽略的小量。
5. **地暖的有效性取决于"漏多少"，不是"传多少"**。无顶地气耦合 0.03/步、户外松弛 0.016/步；有顶 0.00102/步、屋顶泄漏 0.0003/步。有顶升温慢 34 倍（76 秒 vs 2.2 秒）但终值更高：-200 ℃ 环境下 100 ℃ 地温可把有顶房间抬到 +32 ℃，无顶只能到 -4 ℃。真正无效的是真空地图（耦合被乘零）、恒温/有界地形、`EnableGroundHeating = 0`。
6. **运行时无温度上限，但存档有**。`ClampTemperature` 只有下限 -273；然而 `EncodeInt16` 的编码窗口是地温 `-100..150 ℃`、空气 `-100..120 ℃`，读档会把超界值夹回。桥接层要么读档后重新注入，要么在 UI 提示。
7. **户外地暖还会被 `GroundSkinAnchor` 压制**。`GroundSkinAnchor = 1` 时 `num4` 被整段替换成锚定项，玩家注入每步被覆盖；但只在有 GPU 全球帧的地图上生效（`GroundSkinExcessValid`）。

---

## 1. 为什么必须"接管"而不是"提示"

### 1.1 `enableProxyHeat` 的实际作用域

```csharp
// ProxyHeat/ProxyHeatManager.cs:65  —— Tick 里
if (!ProxyHeatMod.settings.enableProxyHeat)
  return;
foreach (CompTemperatureSource temperatureSource in this.compTemperaturesToTick)
  temperatureSource.TempTick();
```

它只让 comp **停止更新** `lastRoomTemperatureChange`。而读值函数：

```csharp
// ProxyHeat/ProxyHeatManager.cs:69
public float GetTemperatureOutcomeFor(IntVec3 cell, float result)
{
  List<CompTemperatureSource> temperatureSourceList;
  if (this.temperatureSources.TryGetValue(cell, out temperatureSourceList))
  {
    if (!GridsUtility.UsesOutdoorTemperature(cell, this.map) && !ProxyHeatMod.settings.enableProxyHeatEffectIndoors)
    { ... return result; }                       // 只看 indoor 开关
    if (GridsUtility.GetEdifice(cell, this.map) != null)
      return result;
    ...
    num += temperatureOutcome;                   // 完全不看 enableProxyHeat
```

**结论**：玩家在 VTE 设置里关掉 Proxy Heat，`Thing.AmbientTemperature` 照样被加 `tempOutcome`。提示无效。

### 1.2 必须拦两层

| 层 | 目标方法 | 作用 |
|---|---|---|
| 读值 | `ProxyHeatManager.GetTemperatureOutcomeFor` | 让四个 Postfix（`Thing.AmbientTemperature`、`PlantUtility.GrowthSeasonNow`、`GenTemperature.TryGetTemperatureForCell`、`GlobalControls.TemperatureString`）全部拿到原值 |
| Tick | `ProxyHeatManager.Tick` | 停止 `TempTick` 的 `FloodFill + LineOfSight` 重算，省 CPU（作者自述的卡顿来源） |

两层都用 `Prefix` 返回 `false`。`TargetMethod()` 用 `AccessTools.TypeByName("ProxyHeat.ProxyHeatManager")`，返回 null 时 Harmony 自动跳过，ProxyHeat 没装也不会炸。

拦截之后，ProxyHeat 退化为一个"只提供 `CompTemperatureSource` 配置数据"的静态库——我们读它的 `radius`、`tempOutcome`、`minTemperature`、`maxTemperature`，但它的逻辑一行都不跑。

---

## 2. 架构总览

```
┌─ 拦截层（Harmony）─────────────────────────────┐
│ GetTemperatureOutcomeFor  → Prefix false      │
│ ProxyHeatManager.Tick     → Prefix false      │
└───────────────────────────────────────────────┘
                     ↓ 只保留配置数据
┌─ 桥接层（CompProxyHeatBridge，挂在建筑上）─────┐
│ 读 CompTemperatureSource 的 radius/tempOutcome │
│ 三个布尔开关 + 三个参数组 + 预制默认值          │
│ 射线扇算照射区（缓存 + 失效重建）               │
└───────────────────────────────────────────────┘
                     ↓ 三种注入
┌─ 注入层（AO 场）───────────────────────────────┐
│ 地暖      GroundTemperaturePerTick            │
│ 空气源    TemperaturePerTick                  │
│ 喷流      RegisterAirMover(ConditionExhaust)  │
│ （叠加在 AO 的 CompHeatSourceAtmosphere 之上） │
└───────────────────────────────────────────────┘
```

---

## 3. 桥接 comp

### 3.1 字段

```csharp
public class CompProperties_ProxyHeatBridge : CompProperties
{
    // 三模式默认开关（XML 可设）
    public bool groundOnByDefault  = false;
    public bool airOnByDefault     = false;
    public bool jetOnByDefault     = false;

    // 地暖
    public float groundTargetCelsius = 25f;     // 目标地温
    public float groundMaxPerTick    = 2f;      // 功率上限（℃/tick）
    public float groundFalloffExp    = 2f;      // (1-d/r)^exp

    // 空气源
    public float airTargetCelsius    = 0f;      // 目标气温
    public float airMaxPerTick       = 2f;
    public float airFalloffExp       = 2f;

    // 喷流
    public float jetTemperatureCelsius = 200f;
    public float jetCubicMetresPerTick = 100f;
    public float jetAirColumnHeight    = 5f;
    public float jetSpeed              = 60f;

    // 共用
    public int   radiusOverride = -1;           // -1 = 用 ProxyHeat 的 radius
    public int   cellsPerStep   = 500;          // 游标分片
}
```

### 3.2 运行时状态

- `bool groundOn, airOn, jetOn` —— 玩家开关，`ExposeData` 持久化
- `List<int> irradiatedCells` —— 射线扇算出的格索引，缓存
- `int cursor` —— 分片游标
- `bool dirty` —— 建筑/墙/半径变化时置位

### 3.3 Gizmo

三个 `Command_Action`：

| 左键 | 右键 |
|---|---|
| 切换该模式开关 | 打开该模式的设置面板 |

```csharp
public override void ProcessInput(Event ev)
{
    if (ev.type == EventType.MouseDown && ev.button == 1)
    { OpenSettingsWindow(); ev.Use(); return; }
    base.ProcessInput(ev);
}
```

### 3.4 设置面板

`Window` 子类，用 `Widgets.HorizontalSlider` + `Widgets.TextFieldNumeric`。每个模式一个面板：

| 面板 | 控件 | 范围 | 默认 |
|---|---|---|---|
| 地暖 | 目标地温 | -273 ~ 2000 | 25 |
| 地暖 | 功率上限 | 0 ~ 20 ℃/tick | 2 |
| 地暖 | 衰减指数 | 0.5 ~ 4 | 2 |
| 空气源 | 目标气温 | -273 ~ 2000 | 0 |
| 空气源 | 功率上限 | 0 ~ 20 ℃/tick | 2 |
| 空气源 | 衰减指数 | 0.5 ~ 4 | 2 |
| 喷流 | 出口温度 | -273 ~ 2000 | 200 |
| 喷流 | 流量 | 0 ~ 1000 m³/tick | 100 |
| 喷流 | 气柱高度 | 1 ~ 50 m | 5 |
| 喷流 | 出口速度 | 0 ~ 200 m/s | 60 |

每个面板底部有"重置为预制默认"按钮（读 `Props`）。

---

## 4. 三模式规格

### 4.1 照射区计算（地暖与空气源共用）

**射线扇**，不用 `FloodFill`，不用逐格 `LineOfSight`：

```
源点 = 建筑中心格（footprint 的中心，向下取整）
半径 R = radiusOverride > 0 ? radiusOverride : Props.radius
射线数 = max(64, ceil(2πR))          // 保证间距 ≤ 1 格
每条射线沿方向走 R 步，逐格：
    若该格 ObstacleMask <= 0  → 记入 irradiatedCells，停止（被墙挡）
    否则                       → 记入 irradiatedCells，继续
```

成本：`2πR × R`。R=60 时约 2.26 万次，比逐格视线（约 68 万次）快 30 倍。

**缓存**：只在建筑生成、拆除、半径变化、`RebuildObstacleMask` 之后重算。用 `MapComponentTick` 每 250 tick 检查一次 `masksDirty` 标志。

**为什么地暖也要射线**：地暖模拟的是"热辐射加热地面"。辐射走直线，墙挡光。墙后的地面确实照不到。这一点与 ProxyHeat 的 `LineOfSight` 一致，但 ProxyHeat 还叠了 `FloodFill`（模拟热空气连通），而辐射不需要连通性。

**为什么空气源也用射线**：简化。物理上空气对长波辐射近乎透明，真实机制是"地面吸热 → 二次加热空气"。这里把两者合并为"照射区的空气直接被加热"，作为游戏模型接受。收益是范围完全一致，UI 只需画一个光斑。

### 4.2 地暖

对 `irradiatedCells` 中的每格：

```
falloff = pow(1 - d/R, groundFalloffExp)
q = clamp((groundTarget - GroundTemperature[i]) * 0.5, -groundMaxPerTick, +groundMaxPerTick) * falloff
GroundTemperature[i] += q * dt
```

- 每步处理 `cellsPerStep` 格，游标轮转
- `dt = ticksSinceLastApply`（通常 6）
- 不乘 `ObstacleMask`（地温与空气格无关，但射线已经保证是空气格）

### 4.3 空气源

```
falloff = pow(1 - d/R, airFalloffExp)
q = clamp((airTarget - Temperature[i]) * 0.5, -airMaxPerTick, +airMaxPerTick) * falloff
Temperature[i] += q * dt
```

同样游标分片。走 `AddTemperatureDelta` 或直接写 `pendingTemperatureDeltas`（与 `CompHeatSourceAtmosphere` 同一条通道）。

### 4.4 喷流

对建筑 footprint 的外圈可通行格，每格注册一个 `AirMover`：

```
inlet  = 外圈格
outlet = inlet + 外法向一格
settings = new AtmosphereAirMoverSettings {
    CubicMetresPerTick      = jetCubicMetresPerTick / 外圈格数,
    AirColumnHeightMetres   = jetAirColumnHeight,
    InletDirection          = -外法向,
    OutletDirection         = 外法向,
    MaxPressureRisePascals  = 5000,
    ConditionExhaust        = true,
    ExhaustSpeed            = jetSpeed,
    ExhaustTemperatureFloor = jetTemperatureCelsius,
    ExhaustPressureRatio    = 1.1f,
    InletPressureRatio      = 0.9f,
    ExhaustOxygenPercent    = 21f,
    WaterKgPerTick          = 0f,
}
RegisterAirMover(map, comp, "jet" + i, inlet, outlet, settings, activeTicks: 0)
```

`ConditionExhaust = true` 走 `ApplyJetTransportUnsafe`：构造 `JetTransport.Source`，`AddNozzlePump` + `AddAirMoverMomentum`，**不看 inlet 温度**，注入设定状态的气体。

注意事项：

1. `IsAirMoverPortOpen(cell)` 要求 `ObstacleMask > 0` 且四面 `FaceOpen` 和 > 0。Frostpunk 建筑 `passability=PassThroughOnly`（`fillage != Full`），外圈格可通行（`AirCanMoveThrough` 返回 true）。
2. `AirColumnHeightMetres > 0`，否则 `Valid = false`，源被静默移除。
3. `AirMoverHasReturnPath(inlet, outlet)` 是前置检查，防"只吹不回"。喷流本身不需要回风，实现时需确认 `ConditionExhaust=true` 是否绕过此检查。
4. 5×5 建筑外圈 20 格，每格一条射流，角度间隔 18°。半径 60 格外相邻射流间隔约 19 格——稀疏。可加密或接受"射线状热柱"。

---

## 5. 地温场与地暖的真实行为（AO 源码验证）

这一节在实现前必须读通。初版文档里有一组错误的系数（把 `GroundAirConductionRate` 当成被 `ScaleForInterval` 缩放过），已于 2026-11-11 修正，见 5.3。

### 5.1 地温是一个独立标量场

`LocalAtmosphereEngine.GroundTemperature` 是 `float[]`，每格一个值，与空气温度 `Temperature` **地位平等、互不派生**。它只有一条与空气的强耦合（`GroundAirConductionRate` 或 `RoofedFloorExchangePerTick`），其余都是"地温自己的演化"。

初始化走 `SeedGroundTemperatureUniform`（`Simulation/LocalAtmosphereEngine.cs:3060` 附近），用地图外部温度填满，再按每格地形的 `MinTemp/MaxTemp` 夹一次。

**地暖的本质是把热量注进一个和空气弱耦合的储库**，这解释了两件事：它升温慢，但它不会被空气一夜吹冷。

### 5.2 一步的完整算法

`AtmosphereGroundEnergyKernel.Advance`（`Simulation/AtmosphereGroundEnergyKernel.cs:33-100`）：

```csharp
float num2 = roofed ? 0f : sunPower * (1f - canopyShade);          // 只有无顶才有直射
bool flag = groundMinimum == groundMaximum;                        // 恒温地形
if (flag) num3 = groundMinimum;                                    // ① 地热/深渊：钉死
else if (!roofed) {
    float num4 = (num2*(1-groundAlbedo)                            // 短波吸收
                - settings.GroundRadiativeRate*(Tg - Tsky)         // 长波向天
                - settings.GroundOutdoorCouplingRate*(Tg - Tamb))  // 与外部气温耦合
               * groundThermalRate * timeStep;
    float num6 = (Tamb + skinExcess*num5 - Tg) * min(1, GroundSkinAnchorRate*thermalRate*dt);
    num4 += (num6 - num4) * min(1, GroundSkinAnchor);              // ← 见 5.8
    num3 = clamp(num3 + num4, groundMinimum, groundMaximum);       // ② 户外：自由演化
}
if (roofed && !flag) {                                             // ③ 有顶：地温被空气带走 q/30
    num9 = (num3 - airTemperature) * min(0.5, RoofedFloorExchangePerTick*30*dt);
    num3 = clamp(num3 - num9/FloorHeatCapacityRatio, ...);
} else
    num7 = (num3 - airTemperature) * GroundAirConductionRate * timeStep;  // ④ 地→空气
if (roofed) num7 += (roofSun - RoofAirCouplingRate*(T - Tamb)) * roofThermalRate * dt;  // ⑤ 屋顶项
num7 -= transpiration * sunFrac * dt * LatentHeatPerUnit;          // ⑥ 蒸腾吸热
```

### 5.3 地气耦合的实际系数（修正）

**关键事实**：`AtmosphereTuningManager.ScaleForInterval` 只缩放它显式列出的字段。地温相关的九个系数**一个都不在列表里**：

```
GroundAirConductionRate / RoofedFloorExchangePerTick / FloorHeatCapacityRatio
RoofAirHeatingRate / RoofAirCouplingRate / GroundRadiativeRate / GroundOutdoorCouplingRate
GroundSkinAnchor / GroundSkinAnchorRate
```

它们保持 Def 默认值，只有 `TimeStep` 被缩放到 **0.2** 后直接乘。所以：

| | 无顶（户外/院子） | 有顶（室内） |
|---|---|---|
| 空气增量系数 | `GroundAirConductionRate 0.15 × 0.2` = **0.03/步** | `RoofedFloorExchangePerTick 0.00017 × 30 × 0.2` = **0.00102/步** |
| 地温是否被空气冷却 | 否 | 是，但除以 `FloorHeatCapacityRatio = 30` |
| 屋顶泄漏项 | 无 | `RoofAirCouplingRate 0.1 × roofThermalRate 0.015 × dt 0.2` = **0.0003/步** |

初版文档写的 0.0064 / 0.0002 是把 `GroundAirConductionRate` 误当成被 `ScaleProbability(0.15, 0.2) = 0.032` 缩放过。实际没有，差 4.7 倍和 5 倍。

### 5.4 为什么有顶房间反而更暖

这不是反直觉，是两个松弛项的数量级差异：

- **无顶空气**同时被两个松弛拖：地气耦合 0.03 + 户外松弛 0.016（`OutdoorTemperatureRelaxation 0.15 → ScaleProbability 0.032 → EnableGroundHeating>0 时 ×0.5`）。后者把空气往 -200 ℃ 拉。
- **有顶空气**只有地气耦合 0.00102 + 屋顶泄漏 0.0003。泄漏比户外松弛小 53 倍。

结果：有顶的地气耦合虽然弱 30 倍，但因为"漏"少得多，**终值反而更高**。代价是速度：

| | 无顶 | 有顶 |
|---|---|---|
| 时间常数 | `1/(0.03+0.016)` = 22 步 ≈ **2.2 s** | `1/(0.00102+0.0003)` = 758 步 ≈ **76 s** |
| 达到 63% 所需 | 2 秒 | 76 秒 |

所以有顶房间的地暖是"慢但稳、终值高"，无顶户外是"快但被环境拖着走"。

### 5.5 稳态温度

只开地暖（空气无其他热源）时，令输入=泄漏：

**无顶**

```
0.03·(Tg − T) = 0.016·(T − T_out)
T = (0.03·Tg + 0.016·T_out) / 0.046 = 0.652·Tg + 0.348·T_out
```

**有顶**

```
0.00102·(Tg − T) = 0.0003·(T − T_out)
T = (0.00102·Tg + 0.0003·T_out) / 0.00132 = 0.773·Tg + 0.227·T_out
```

代入 -200 ℃ 环境：

| 地温 Tg | 无顶稳态 | 有顶稳态 |
|---|---|---|
| 25 ℃ | **-53.3 ℃** | **-26.2 ℃** |
| 50 ℃ | -36.5 ℃ | -7.3 ℃ |
| 100 ℃ | -4.4 ℃ | **+31.9 ℃** |
| 150 ℃（存档上限） | +28.2 ℃ | +70.6 ℃ |
| 500 ℃ | +256 ℃ | +331 ℃ |

**结论修正**：在 -200 ℃ 场景里，有顶房间只要 100 ℃ 地温就能到 +32 ℃。无顶户外则需要约 110 ℃ 才能到 0 ℃。这与初版文档"有顶几乎无效、无顶也要 500 ℃"的结论相反。

### 5.6 地暖与空气源的关系（串联，但有反向拖拽）

空气源用恒定功率 `q_a`（℃/步）直接注入空气时：

```
q_a + 0.03·(Tg − T) = 0.016·(T − T_out)
T = (q_a + 0.03·Tg + 0.016·T_out) / 0.046
```

以 -200 ℃、`Tg = 25`、`q_a = 3.2` 为例：

```
T = (3.2 + 0.75 − 3.2) / 0.046 = 16.3 ℃
```

对比：只开空气源是 0 ℃（`3.2/0.016 − 200`），只开地暖是 -53.3 ℃。组合起来 16.3 ℃，地暖净贡献 16.3 ℃。

**但反向项不小。** 当 `T > Tg` 时地暖项为负：

| 空气 T | 地温 Tg | 地暖项 `0.03·(Tg−T)` |
|---|---|---|
| 30 ℃ | 25 ℃ | −0.15 K/步 |
| 100 ℃ | 25 ℃ | **−2.25 K/步** |
| 300 ℃ | 25 ℃ | **−8.25 K/步** |

对比空气源常见的 `q_a = 3.2`：空气到 100 ℃ 时地暖已经抽走 70%，到 300 ℃ 时抽走 2.6 倍。所以：

- **地暖与空气源不是"互不干扰"**，是"串联 + 反向耦合"
- 玩家把地暖目标设得比空气目标低很多，会显著拖累空气源
- 反过来，地暖目标高、空气源目标低时，地暖会帮空气源"托底"

实现上不需要仲裁逻辑——物理上就是叠加。但设置面板要提示玩家："地暖目标低于空气目标时，会从空气里抽热"。

### 5.7 地暖适用场景

| 场景 | 有效性 | 原因 |
|---|---|---|
| **有顶房间/封闭屋子** | ✅ **最适用** | 终值最高（0.773·Tg+0.227·Tout），76 秒到 63%。适合长时间保温 |
| **无顶院子/户外平地** | ✅ 适用，但被环境拖累 | 快（2.2 秒），但 34.8% 的权重被环境温度占据 |
| **沙地** | ✅ 最好 | `thermalRate 0.95`，响应最快 |
| **水地形** | ⚠️ 慢，非无效 | `thermalRate 0.03` 是沙地的 1/32 |
| **真空地图** | ❌ 完全无效 | `GroundAirConductionRate *= 气压比 = 0`，详见 5.8 |
| **恒温地形**（`Min==Max`） | ❌ 无效 | `num3 = groundMinimum` 钉死，注入被丢弃 |
| **有界地形**（`Capped`/`Fixed`） | ❌ 受限 | `Clamp` 上限吃掉注入 |
| **`EnableGroundHeating = 0`** | ❌ 无效 | `ApplyGroundEnergy` 入口（`LocalAtmosphereEngine.cs:3025`）直接 return |
| **实心格** | ❌ 无效 | `if (obstacle[i] <= 0f) return;` |

### 5.8 三个额外压制条件

**① 真空地图：耦合被乘零。**

```csharp
// RimWorld/AtmosphereMapComponent.cs:3983-3985
float num = Mathf.Clamp(engine != null ? engine.AmbientSurfacePressurePa / 100000f : AmbientPressureRatio(map), 0f, 2f);
settings.GroundOutdoorCouplingRate *= num;
settings.GroundAirConductionRate *= num;
```

真空地图 `AmbientSurfacePressurePa = 0` → `num = 0` → 耦合与外部松弛同时归零。地温既不传给空气，也不向环境散热，会一直累积。**真空环境必须用空气源或喷流。**

**② 恒温与有界地形。**

`GroundMaterialTable.ResolveGround`（`Simulation/GroundMaterialTable.cs:40-86`）读 `AtmosphereTerrainExtension`：

- `Fixed` → `MinTemp = MaxTemp = fixedTemperature`，地温完全钉死
- `Capped` → `MinTemp/MaxTemp = min/max`，注入被 `Clamp` 吃掉
- 默认地形 `±1e9`，不限制

**③ `GroundSkinAnchor` 会让户外地暖被"半覆盖"。**

```csharp
// Simulation/AtmosphereGroundEnergyKernel.cs:50-60
if (GroundSkinAnchor > 0 && settings.GroundSkinExcessValid > 0.5) {
    float num6 = (Tamb + skinExcess*num5 - num3) * min(1, GroundSkinAnchorRate*thermalRate*dt);
    num4 += (num6 - num4) * min(1, GroundSkinAnchor);   // GroundSkinAnchor = 1 → num4 被替换成 num6
}
```

`GroundSkinAnchor` 默认 **1.0**，这一行等价于 `num4 = num6`——**玩家这一步注入的地温增量被整段覆盖**，地温被拉向"环境 + 太阳皮肤超温（clamp 在 -30..50 K）"。

但 `GroundSkinExcessValid` 只在**有 GPU 全球帧**时才是 1（`RefreshGroundSkinExcess`：`!IsVacuumMap && TryGetCachedGlobalSample && sample.IsGpuAutodrive`）。所以：

- 没跑世界模式的地图 → `Valid = 0` → 锚定不生效 → 地暖注入自由
- 跑了世界模式的地图 → 锚定生效 → 地温被拉向 `Tamb + skinExcess`，玩家注入每步被压制（沙地 1.14%/步）

**同一个地暖在世界模式启用前后表现不同**，世界模式下需要更大的注入功率。这条要写进设置面板提示。

### 5.9 存档编码窗口（硬约束）

AO 存地温时走 `AtmosphereScribeUtility.EncodeInt16`（`RimWorld/AtmosphereScribeUtility.cs:15-35`），不是直接存 float，而是**先归一化到 `[minVal, maxVal]`，再线性映射到 `ushort` 全部 65536 个刻度**：

```csharp
float num1 = 1f / (maxVal - minVal);
float num2 = (source[index1] - minVal) * num1;   // 归一化到 0..1
if (num2 < 0f) num2 = 0f;
else if (num2 > 1f) num2 = 1f;                    // ← 数据在这里丢，不是位宽不够
int num3 = (int)(num2 * ushort.MaxValue) - 32768;
short num4 = clamp(num3, short.MinValue, short.MaxValue);
```

精度很高（250 K ÷ 65535 ≈ **0.0038 K**），但窗口只有 250 K 宽。`AtmosphereMapComponent.cs:1135-1142` 的六组窗口：

| 字段 | 窗口 | 超界后果 |
|---|---|---|
| `engineTemperature`（空气） | **-100 .. 120 ℃** | 300 ℃ 读档变 120 ℃ |
| `engineGroundTemp`（地温） | **-100 .. 150 ℃** | 500 ℃ 读档变 150 ℃ |
| `engineMoisture` | 0 .. 20 | 超界归一 |
| `engineMist` | 0 .. 100 | 超界归一 |
| `engineSealedRoomPressure` | -101325 .. 303975 Pa | 超界归一 |
| `enginePressureOddity` / `engineCompressiblePressure` | -303975 .. 303975 Pa | 超界归一 |

所以"不限制温度上限"只能在**运行时**成立；**读档会把地温夹回 150 ℃**，把空气夹回 120 ℃。桥接层要么在读档后重新注入，要么在 UI 上明确提示"读档后需要重新升温"。

### 5.10 注入顺序

`ApplyGroundEnergy` 每步会重算并 `Clamp` 地温。桥接层写在它**之前**会被这一步的 `Clamp` 处理（默认无界，但 `Capped` 地形会夹掉）。建议：

```
每步的顺序：
1. engine.ApplyGroundEnergy(...)          // AO 自己的太阳/辐射/耦合（含 GroundSkinAnchor）
2. 桥接层注入地温（GroundTemperature[i] += q*dt）
3. engine.ApplyTemperatureDeltas(pendingTemperatureDeltas)   // 含空气源的注入
```

这样地暖注入落在 AO 重算**之后**，不会被 `GroundSkinAnchor` 覆盖；空气源天然进入 `pendingTemperatureDeltas` 通道，与 `CompHeatSourceAtmosphere` 的点源同一条路。
---

## 6. 温度无上限的后果

`ClampTemperature`（`Simulation/LocalAtmosphereEngine.cs:4415`）只有下限：

```csharp
float num = settings != null ? settings.MinimumTemperatureCelsius : Default.MinimumTemperatureCelsius;
return value < num ? num : value;
```

`GroundMaterialTable.ResolveGround` 的默认 `MinTemp = -1e9f`、`MaxTemp = 1e9f`，只有地形带 `AtmosphereTerrainExtension`（`mode = Fixed/Capped`）时才有界。

所以三个后果：

**① 地温可以无限升。** 玩家把地暖目标设 2000 ℃，地温就真的到 2000 ℃。植物根区瞬间烤死、`Item` 可能自燃、pawn 走上去立刻中暑。

**② 空气源的闭环没有天花板。** 但闭环本身是稳定的：`q = (target − T) × 0.5`，`T` 会收敛到 `target`，不会发散。风险在玩家把 `target` 设得过高。

**③ 喷流是唯一绕过闭环的通道。** `ExhaustTemperatureFloor` 直接决定喷出气体的温度，没有"目标 − 当前"的反馈。设 2000 ℃ 就喷 2000 ℃。

**④ 绝热正反馈。** AO 的 `StoreCompressiblePressure` 用泊松关系：

```
ΔT = T_K × ((p/p₀)^(0.4·factor·min(1,p/1e5)) − 1)
```

温度升 → 热膨胀 → `externalDivergence` → 压力升 → 绝热压缩 → 温度再升。这条链在 `stored` 被 clamp 到 19 atm 时封顶，理论放大倍率约 `19^0.4 ≈ 3.3`，不是无限，但足以让"设 500 ℃"变成"实际 1600 ℃"。

**处理**：不拦，但在面板上加一行提示。保留功率上限（`groundMaxPerTick` / `airMaxPerTick` / `jetCubicMetresPerTick`）作为唯一的安全阀——用户说的是"不限制温度上限"，不是"不限制功率"。

---

## 7. 蓝信（Letter）

### 7.1 触发条件

在 `LongEventHandler.ExecuteWhenFinished` 或 `GameComponent.FinalizeInit` 里检查：

```csharp
bool proxyHeatLoaded = ModLister.GetActiveModWithIdentifier("...") != null;
bool vtePatchLoaded  = ModLister.GetActiveModWithIdentifier("ewolfy.vtePatch") != null;
```

### 7.2 内容

- **正常情况**（只装了 ProxyHeat）：告知玩家 ProxyHeat 的热场已被本 mod 接管，其读数加成已关闭；不需要在 VTE 设置里操作。
- **警告情况**（同时装了 VTE-ProxyHeatPatch）：额外提示该 patch 的 IL 栈不平衡，与拦截层叠加时行为不可预测，建议移除。

### 7.3 类型

`LetterDefOf.NeutralEvent`（蓝色）。

---

## 8. 实现顺序

| 阶段 | 内容 | 依赖 |
|---|---|---|
| P0 | 拦截层（两个 Prefix + 条件 patch） | 无 |
| P0 | `CompProxyHeatBridge` 骨架 + XML 注入 | 无 |
| P1 | 射线扇 + 缓存 + 失效重建 | P0 |
| P1 | 地暖模式（游标分片 + 闭环） | P1 射线 |
| P1 | 空气源模式 | P1 射线 |
| P2 | 喷流模式（`RegisterAirMover`） | P0 |
| P2 | Gizmo + 三个设置面板 | 三模式 |
| P3 | 蓝信 | P0 |
| P3 | 文档 + 不兼容声明 | 全部 |

---

## 9. 未决项

1. **`AirMoverHasReturnPath` 是否对 `ConditionExhaust=true` 生效**——如果生效，喷流会被前置检查拒掉。
2. **喷流的 inlet 用外圈格还是建筑中心**——取决于 `IsAirMoverPortOpen` 的实际判定。
3. **射线扇的半格偏移**——源点在格心还是格角，会影响射线的对称性。
4. **三模式同时开启时的 UI 表达**——一个光斑还是三个光斑。
5. **地温的横向传导**——AO 没有，辐射阴影边界是锐利的。是否在桥接层补一层边界平滑。
6. **`tempOutcome` 动态值的处理**——小型机走 `lastRoomTemperatureChange`，AO 接管 `TickRare` 后归零。是否只用 Def 固定值。

---

## 附录 A：AO 参数速查（缩放后）

`SimulationIntervalTicks = 6`，`ratio = 6/30 = 0.2`，`timeStep = 0.2`。

| 参数 | 源值 | 有效值/步 | 是否被 `ScaleForInterval` 缩放 |
|---|---|---|---|
| `OutdoorTemperatureRelaxation` | 0.15 | 0.032 → ×0.5 = **0.016** | 是（`ScaleProbability`），只作用于无顶空气格 |
| `GroundAirConductionRate` | 0.15 | ×timeStep = **0.03** | **否**（不在缩放列表） |
| `RoofedFloorExchangePerTick` | 0.00017 | ×30×timeStep = **0.00102** | **否** |
| `RoofAirCouplingRate` | 0.1 | ×roofThermalRate 0.015 ×timeStep = **0.0003** | **否** |
| `GroundRadiativeRate` | 0.005 | 0.005（再 ×thermalRate×dt） | **否** |
| `GroundOutdoorCouplingRate` | 0.05 | 0.05（再 ×thermalRate×dt） | **否** |
| `GroundSkinAnchor` | 1.0 | 1.0 | **否** |
| `FloorHeatCapacityRatio` | 30 | 30 | **否**（除数） |
| `TemperatureDiffusion` | 0.24 | `ScaleProbability(0.24, 0.2) = 0.053` | 是 |

**注意**：地温相关的九个系数全部不在 `ScaleForInterval` 的缩放列表里（`Simulation/AtmosphereTuningManager.cs:244-290` 只列了扩散/松弛/蒸发/边界那一批），所以它们保持 Def 默认值，只被 `timeStep = 0.2` 直接乘。

## 附录 B：源码索引

| 文件 | 行 | 内容 |
|---|---|---|
| `ProxyHeat/ProxyHeatManager.cs` | 65 | `enableProxyHeat` 只影响 Tick |
| `ProxyHeat/ProxyHeatManager.cs` | 69-109 | `GetTemperatureOutcomeFor` 完整逻辑 |
| `Simulation/AtmosphereGroundEnergyKernel.cs` | 33-100 | `Advance` 地气耦合 |
| `Simulation/LocalAtmosphereEngine.cs` | 3045 | `ApplyGroundEnergy` |
| `Simulation/LocalAtmosphereEngine.cs` | 4415 | `ClampTemperature`（只有下限） |
| `Simulation/LocalAtmosphereSettings.cs` | 258-268 | 地温相关默认值 |
| `Simulation/GroundMaterialTable.cs` | 68-72 | 默认 `MinTemp/MaxTemp = ±1e9` |
| `RimWorld/AtmosphereMapComponent.cs` | 609-694 | `ApplyJetTransportUnsafe` |
| `RimWorld/AtmosphereMapComponent.cs` | 2534-2620 | `ApplyContinuousSourcesUnsafe` |
| `Simulation/AtmosphereTuningManager.cs` | 244 | `ScaleForInterval` / `ScaleProbability` |
