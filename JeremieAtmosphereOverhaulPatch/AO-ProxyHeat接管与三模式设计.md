# AO · ProxyHeat 接管与三模式热场设计

> 状态：设计稿 **v2**，已按真实源码逐条核对修订；仍未实现代码。
> 日期：2026-11-12（v1：2026-11-11）。
> 依据：AO 1.7 源码（`f:\RimworldCodeIL\AtmosphereOverhaul\`，MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）、ProxyHeat 源码（`f:\RimworldCodeIL\ProxyHeat\`，MVID `7358A296-A2E2-46F8-A5D3-4C48DE897B1C`）、Frostpunk Heat Generator Tower Def（`294100/3545379519`）、VTE-ProxyHeatPatch 源码（`f:\RimworldCodeIL\VTE-ProxyHeatPatch\`）。
> 配套文档：`AO源码全景阅读报告.md`、`AO-Frostpunk发电机分析.md`、`AO兼容层重构设计.md`、`AO-CCC与VTE重设计.md`。

---

## 修订记录（v1 → v2）

本节记录 v1 中与源码不符、已改正的地方。v1 的错误集中在「接管理由」与「注入通道」，不影响三模式叠加的整体架构。

1. **§1.1 接管前提整体改写**。v1 说「`enableProxyHeat` 关不掉读值，所以必须接管」，**结论错误**。ProxyHeat 全部 9 处读值调用点都先判 `enableProxyHeat`，且它**不替换原版建筑逻辑**（`TickRare` 是 Transpiler 插入一条记录调用 + Postfix 改功耗显示）。关掉开关后 ProxyHeat 完全无影响，不存在「救不回来」。接管的真正理由是：`tempOutcome` 是读数魔法，进不了 AO 的独立温度场；而 AO 已经按 `heatPerSecond` 自动生成真实点源，两者叠加会**双重计入**且目标温度冲突。
2. **§1.2 拦截目标方法名修正**。`ProxyHeatManager` 是 `MapComponent`，方法名是 `MapComponentTick`，不是 `Tick`。按 v1 写 `AccessTools.Method("ProxyHeat.ProxyHeatManager:Tick")` 会返回 null，拦截层静默失效。
3. **§4.3 空气源通道修正**。`CompHeatSourceAtmosphere` 走 `SourceSlots` → `RegisterContinuousSource` 注册**单格点源**，不是 `pendingTemperatureDeltas`。多格建筑的源落在 `parent.Position`（占用格第一格，即左下角）。
4. **§4.4 / §6 喷流温度语义修正**。`ExhaustTemperatureFloor` 是**温度下限**，不是「直接决定喷出温度」。实际喷出温度 = `max(环境温度, 入口邻格均温, floor)`。
5. **§5.2 补上漏掉的深屋顶分支**：`roofThermalRate <= 0 && airT > 15` 时 `num7 += (15 - airT) * min(0.5, 0.0015 * timeStep)`。
6. **§5.10 注入位置修正**。顺序结论没错，但公开 API 的写入点都在 `ApplyGroundEnergy` **之前**，会被 `GroundSkinAnchor` 覆盖。必须做成 `ApplyGroundEnergy` 的 Harmony Postfix，并处理全局子循环的重复调用。
7. **新增 §5.11 量级校准**。AO 自动点源 = `heatPerSecond × 3 / 60` ℃/tick，标准机 37.5、升级机 125，每 6 tick 实际 **+225 / +750 ℃ 到单格**，并把建筑格推向约 300 ℃（`num5` 钳制）。ProxyHeat 的 `maxTemperature = 30 / 50` 只是读数上限。两者叠加时桥接空气源会**反向制冷**，因此必须接管 AO 自动点源，不能叠加。
8. **§4.1 照射区语义修正**。ProxyHeat 原实现是 `FloodFill` + `GenSight.LineOfSight`（1.5 格内豁免）；v1 的纯直线射线扇会漏掉拐角与门洞后的格子，行为与原版不一致。射线扇降级为可选优化。
9. **§4.2 / §3.1 通道与字段修正**。地暖优先用 `AtmosphereSourceRates.GroundTemperaturePerTick`；`radiusOverride` 改为 `float`（ProxyHeat 的 `radius` 是 float）；`airTarget` 默认跟随 Def 的 `maxTemperature`。
10. **融雪暂缓**。按用户指示：先实测 AO 接管后雪的实际融化表现，再决定是否复刻 `smeltSnowRadius`。见 §4.5。

---

## 0. 结论摘要

1. **ProxyHeat 是「读值附加层」，不是「热源替换层」**。`ProxyHeatManager.GetTemperatureOutcomeFor`（ProxyHeatManager.cs:69-109）把 `tempOutcome` 加到读值上；所有调用它的人（`Thing.AmbientTemperature`、`GrowthSeasonNow`、`TryGetTemperatureForCell`、`TemperatureString`、`CanWanderToCell`、`GenNewRRJob`、`TryGiveJob`、`RegisterRoomTemperatureChange`、`RegisterHeatPush`）**都先检查 `enableProxyHeat`**。关掉开关，ProxyHeat 的读值加成、`TempTick`、融雪全部停止，原版建筑逻辑照旧运行——它是干净的「零影响」。
2. **`tempOutcome` 是读数魔法，不是能量**。60 格半径的室外暖区要 10⁹ W 量级，大型机只有 10⁵ W 量级。接管后不能「原样复现」，只能「用场模拟它的意图」。
3. **AO 已经给 Frostpunk 发电机生成了真实点源**。`HeatSourceInjection`（Buildings/2HeatSources.cs:16-45）会给所有带 `CompProperties_HeatPusher` 且不自己实现 `CompTick` 的建筑自动挂 `CompHeatSourceAtmosphere`。标准机 750 ℃/s → 37.5 ℃/tick，升级机 2500 → 125 ℃/tick，每 6 tick 实际往单格灌 **225 / 750 ℃**，并被 `num5` 钳制在 300 ℃ 附近。这把建筑格烧到远超 ProxyHeat 的 `maxTemperature = 30 / 50`。
4. **因此桥接层必须「接管 AO 点源」，不能叠加**。若保留 AO 点源，桥接空气源的目标 30/50 ℃ 低于点源稳态温度，闭环会算出负增量，变成「制冷」——与 ProxyHeat 想表达的「+80 ℃」完全相反。接管后由桥接层统一供能：地暖注地温、空气源注气温、喷流注气流。
5. **三模式是三种物理效应的叠加，不替换 AO 的其它设施**：地暖 = 辐射照地，空气源 = 辐射照空气（简化），喷流 = 强制对流。三者内部可以同时开启。
6. **地暖与空气源是串联，但反向拖拽不可忽略**。地温 → 0.03/步 → 空气；空气源直接注入。稳态 `T = (q_a + 0.03·Tg + 0.016·T_out)/0.046`。当空气比地温高很多时地暖项变负：`T = 300`、`Tg = 25` 时抽走 `8.25 K/步`。所以「地暖目标设得很低」确实会拖累空气源，面板要给提示。
7. **地暖的有效性取决于「漏多少」，不是「传多少」**。无顶地气耦合 0.03/步、户外松弛 0.016/步；有顶 0.00102/步、屋顶泄漏 0.0003/步。有顶升温慢 34 倍（76 秒 vs 2.2 秒）但终值更高：-200 ℃ 环境下 100 ℃ 地温可把有顶房间抬到 +32 ℃，无顶只能到 -4 ℃。真正无效的是真空地图、恒温/有界地形、`EnableGroundHeating = 0`。
8. **运行时无温度上限，但存档有**。`ClampTemperature` 只有下限 -273；`EncodeInt16` 的编码窗口是地温 `-100..150 ℃`、空气 `-100..120 ℃`，读档会把超界值夹回。桥接层要么读档后重新注入，要么在 UI 提示。
9. **户外地暖还会被 `GroundSkinAnchor` 压制**。`GroundSkinAnchor = 1` 时 `num4` 被整段替换成锚定项；但只在有 GPU 全球帧的地图上生效（`GroundSkinExcessValid`）。所以地暖注入必须落在 `ApplyGroundEnergy` 之后。

---

## 1. 为什么「接管」，以及接管什么

### 1.1 ProxyHeat 的真实结构

`ProxyHeatManager` 是 `MapComponent`，它覆盖的是 `MapComponentTick`：

```csharp
// ProxyHeat/ProxyHeatManager.cs:45-67
public override void MapComponentTick()
{
    if (this.dirty) this.Rebuild();
    if (!ProxyHeatMod.settings.enableProxyHeat)
        return;
    foreach (CompTemperatureSource temperatureSource in this.compTemperaturesToTick)
        temperatureSource.TempTick();
}
```

读值侧：

```csharp
// ProxyHeat/ProxyHeatManager.cs:69-109（节选）
public float GetTemperatureOutcomeFor(IntVec3 cell, float result)
{
    // 遍历 temperatureSources，跳过 !Affected
    if (!GridsUtility.UsesOutdoorTemperature(cell, this.map)
        && !ProxyHeatMod.settings.enableProxyHeatEffectIndoors)
        return result;                       // 只看 indoor 开关，不看 enableProxyHeat
    if (GridsUtility.GetEdifice(cell, this.map) != null)
        return result;
    // 收集各 comp.TemperatureOutcome，末尾 result += outcomes.Average()
}
```

**关键事实**：`GetTemperatureOutcomeFor` 本身确实不看 `enableProxyHeat`，但它的**所有调用点都在 ProxyHeat 自己的 patch 里，且每个都先判开关**：

| 调用点 | 位置 | 形态 | 开关检查 |
|---|---|---|---|
| `Thing.AmbientTemperature` | HarmonyPatches.cs:361-374 | Postfix | :366 |
| `PlantUtility.GrowthSeasonNow` | HarmonyPatches.cs:388-409 | Prefix | :400（且要求 `allowPlantGrowthInsideProxyHeatEffectRadius`） |
| `GenTemperature.TryGetTemperatureForCell` | HarmonyPatches.cs:411-421 | Postfix | :417（且要求 `checkForPlantGrowth` + 格上有植物） |
| `GlobalControls.TemperatureString` | HarmonyPatches.cs:329-359 | **Transpiler** | :355 |
| `RCellFinder.CanWanderToCell` | HarmonyPatches.cs:86-99 | Postfix | :89 |
| `Brrr.BrrrGlobals.GenNewRRJob` | HarmonyPatches.cs:255-295 | Prefix | :257 |
| `JobGiver_SeekSafeTemperature.TryGiveJob` | HarmonyPatches.cs:423-480 | Prefix | :428 |
| `RegisterRoomTemperatureChange` | HarmonyPatches.cs:161-178 | Transpiler 插入 | :167 |
| `RegisterHeatPush` | HarmonyPatches.cs:180-212 | 被上一行调用 | :186 |

**它也不替换原版建筑**。`TickRare_Transpiler`（HarmonyPatches.cs:116-139）只是在 `GenTemperature.ControlTemperatureTempChange` 调用后的 `Stloc` 处插入一条 `RegisterRoomTemperatureChange` 记录调用；`TickRare_Postfix`（:101-114）只更新 `PowerOutput` 与 `operatingAtHighPower`。原版 `Building_Heater/Cooler` 的产热、制冷、耗电逻辑原封不动。

**结论**：玩家在 VTE 设置里关掉 Proxy Heat，一切归零，没有任何副作用。v1 的「必须接管读值」论证不成立。

### 1.2 真正需要接管的原因

不是「关不掉」，而是**两套温度场无法共存**：

1. **AO 用自己的引擎**。`LocalAtmosphereEngine.Temperature` / `GroundTemperature` 是独立数组，`GenTemperature` 的读取在 AO 下会被温度场接管或改写。ProxyHeat 的读数加成要么被覆盖，要么只对少数读取路径生效——玩家看到的「+80 ℃」与 AO 实际温度不一致。
2. **AO 已经给了真实点源**。Frostpunk 发电机带 `CompProperties_HeatPusher`（`heatPerSecond = 750 / 2500`），`HeatSourceInjection` 静态构造会给它自动加 `CompHeatSourceAtmosphere`。这个点源每 6 tick 往单格灌 225 / 750 ℃（见 §5.11），把建筑格烧到 300 ℃ 附近。
3. **两者叠加是「双重计入」且互相打架**。ProxyHeat 的语义是「26 格内读数 +80 ℃、最高 30 ℃」；AO 点源的语义是「单格强热源」。保留点源时，桥接空气源的目标 30/50 ℃ 低于点源实际温度，闭环算出负增量 → **桥接层主动制冷**。这不是调参能解决的，必须二选一。
4. **接管的方向**：移除 AO 对该建筑的自动点源，由 `CompProxyHeatBridge` 用 Def 的 `radius / tempOutcome / maxTemperature / heatPerSecond` 统一供能。这样「一个建筑只有一个热源」，语义可控。

### 1.3 接管方案

| 层 | 目标 | 做法 |
|---|---|---|
| 读值 | `ProxyHeatManager.GetTemperatureOutcomeFor` | Prefix 返回 `false`，`__result` 保持传入的 `result`（剥掉读数加成） |
| Tick | `ProxyHeatManager.MapComponentTick` | Prefix 返回 `false`，桥接层接管 `TempTick` 的融雪部分（见 §4.5，暂缓） |
| AO 点源 | `CompProperties_HeatSourceAtmosphere` | 启动完成后从带 `CompProperties_TemperatureSource` 的 Def 上移除该 comp（见 §5.11.3） |

两层都用 `Prefix` 返回 `false`。`TargetMethod()` 用 `AccessTools.TypeByName("ProxyHeat.ProxyHeatManager")`，返回 null 时 Harmony 跳过打补丁，ProxyHeat 没装也不会炸。

**注意**：`GetTemperatureOutcomeFor` 的 Prefix 要写成「重置 `__result` 为入参 `result`」。因为它是 `float` 返回值，Prefix 里 `__result = result;`（`result` 是第二个参数）即可；若只写 `return false` 会让 `__result` 保留默认值 0，反而制造一个 -80 ℃ 的假低温。

---

## 2. 架构总览

```
┌─ 拦截层（Harmony）─────────────────────────────────┐
│ GetTemperatureOutcomeFor  → Prefix：__result = 入参  │
│ MapComponentTick          → Prefix：false，停掉 Tick │
└─────────────────────────────────────────────────────┘
                     ↓ 只保留配置数据
┌─ 桥接层（CompProxyHeatBridge，挂在建筑上）──────────┐
│ 读 CompTemperatureSource：radius / tempOutcome /    │
│   minTemperature / maxTemperature / smeltSnow*      │
│ 读 CompHeatPusher.heatPerSecond（用于量级）         │
│ 三个布尔开关 + 三组参数 + 预制默认值                │
│ 照射区（FloodFill + LineOfSight，缓存 + 失效重建）  │
└─────────────────────────────────────────────────────┘
                     ↓ 三种注入
┌─ 注入层（AO 场）────────────────────────────────────┐
│ 地暖      GroundTemperaturePerTick（Postfix 注入）  │
│ 空气源    TemperaturePerTick（连续源）              │
│ 喷流      RegisterAirMover(ConditionExhaust)        │
│ 前提：先移除 AO 对该建筑的 CompHeatSourceAtmosphere │
└─────────────────────────────────────────────────────┘
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
    public float groundMaxPerTick    = 2f;      // 每格功率上限（℃/tick）
    public float groundFalloffExp    = 2f;      // (1-d/r)^exp

    // 空气源
    // 负值表示「跟随 Def 的 maxTemperature」；Frostpunk 是 30 / 50
    public float airTargetCelsius    = -1f;
    public float airMaxPerTick       = 2f;
    public float airFalloffExp       = 2f;

    // 喷流
    // 负值表示「跟随 Def 的 tempOutcome」；Frostpunk 是 80 / 200
    public float jetTemperatureCelsius = -1f;
    public float jetCubicMetresPerTick = 100f;
    public float jetAirColumnHeight    = 5f;
    public float jetSpeed              = 60f;

    // 共用
    public float radiusOverride  = -1f;         // -1 = 用 ProxyHeat 的 radius（float）
    public int   cellsPerStep    = 500;         // 游标分片
    // 仅用于关闭「移除 AO 自动点源」这一步；实际移除在 Bootstrap 里全局做，
    // 不是逐建筑开关（AO 的 HeatSourceInjection 也是全局加）
    public bool  disableAoAutoHeatSource = true;
}
```

**为什么 `airTarget` 默认是「跟随 `maxTemperature`」**：Frostpunk 标准机 `maxTemperature = 30`、升级机 `50`，这是作者对「ProxyHeat 读数不超过多少」的设定。桥接层的闭环目标若写死 0 ℃，等于把作者的设计推翻；跟随 Def 才与「读数 +80 ℃、最高 30 ℃」的语义对齐。

**为什么 `radiusOverride` 是 `float`**：ProxyHeat 的 `CompProperties_TemperatureSource.radius` 是 `float`（默认 10），Frostpunk 配的是 26.0 / 60.0。用 int 会取整，且未来 Def 可能写小数。

### 3.2 运行时状态

- `bool groundOn, airOn, jetOn` —— 玩家开关，`ExposeData` 持久化
- `List<int> irradiatedCells` —— 照射区格索引，缓存
- `int cursor` —— 分片游标
- `bool dirty` —— 建筑/墙/半径变化时置位
- `int lastInjectTick` —— 地暖 Postfix 的去重标记（全局子循环会多次调用 `ApplyGroundEnergy`）

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
| 空气源 | 目标气温 | -273 ~ 2000 | Def 的 `maxTemperature`（30 / 50） |
| 空气源 | 功率上限 | 0 ~ 20 ℃/tick | 2 |
| 空气源 | 衰减指数 | 0.5 ~ 4 | 2 |
| 喷流 | 出口温度下限 | -273 ~ 2000 | Def 的 `tempOutcome`（80 / 200） |
| 喷流 | 流量 | 0 ~ 1000 m³/tick | 100 |
| 喷流 | 气柱高度 | 1 ~ 50 m | 5 |
| 喷流 | 出口速度 | 0 ~ 200 m/s | 60 |

每个面板底部有「重置为预制默认」按钮（读 `Props`，`airTarget` / `jetTemperature` 为负时显示 Def 实际值）。

---

## 4. 三模式规格

### 4.1 照射区计算（地暖与空气源共用）

**与原实现对齐：`FloodFill` + `LineOfSight`。**

ProxyHeat 的 `CompTemperatureSource.RecalculateAffectedCells`（CompTemperatureSource.cs:93-139）：

```
1. GenRadial.RadialCellsAround(parent.Position, Props.radius, true)
2. map.floodFiller.FloodFill(...) 扩散（能穿门/开口）
3. 对每个候选格：GenSight.LineOfSight(parent.Position, cell, map, false)
   或 cell == parent.Position，或 DistanceTo(parent.Position) <= 1.5（豁免）
4. CanWorkIn(cell) = UsesOutdoorTemperature(cell, map) || enableProxyHeatEffectIndoors
```

**v1 的「纯直线射线扇」不是等价物**：射线只走直线，墙后的格子即使通过门洞能绕过去也照不到；而原版 `FloodFill` 会穿过门。若直接改成射线，行为会明显改变（门口/拐角外升温消失）。所以：

- **默认实现**：复刻 `FloodFill + LineOfSight`，缓存结果。
- **可选优化**：射线扇作为「大半径低精度」模式，仅在 `cellsPerStep` 与半径都很大、实测卡顿时启用；开启时在设置面板明确标注「行为与原版不同」。

**缓存失效**：建筑生成/拆除、半径变化、`RebuildObstacleMask` 之后置 `dirty`。用 `MapComponentTick` 每 250 tick 检查一次 `masksDirty`。

### 4.2 地暖

对 `irradiatedCells` 中的每格：

```
falloff = pow(1 - d/R, groundFalloffExp)
q = clamp((groundTarget - GroundTemperature[i]) * 0.5,
          -groundMaxPerTick, +groundMaxPerTick) * falloff
GroundTemperature[i] += q * dt
```

- 每步处理 `cellsPerStep` 格，游标轮转
- `dt = ticksSinceLastApply`（通常 6）
- 不乘 `ObstacleMask`（AO 的 `GroundTemperaturePerTick` 通道本来也不乘，见 `ApplyContinuousSourcesUnsafe` 2592-2593）

**注入通道**：优先走 `AtmosphereSourceRates.GroundTemperaturePerTick`；但该字段在 `ApplyContinuousSourcesUnsafe`（tick 准备阶段，早于 `ApplyGroundEnergy`）写入，会被 `GroundSkinAnchor` 覆盖。因此地暖实际落地的写法是：

```
Harmony Postfix 打在 LocalAtmosphereEngine.ApplyGroundEnergy 上
Postfix 内：
  if (engine == null || engine.GroundTemperature == null) return; // 入口提前 return 时 Postfix 仍会跑
  if (ThisTickAlreadyInjected) return;   // 全局子循环会多次调用
  foreach 该地图所有 CompProxyHeatBridge：
      foreach 照射区格（游标分片）：
          engine.GroundTemperature[i] += q * dt;
  ThisTickAlreadyInjected = true;
```

`ApplyGroundEnergy` 在 `StartSimulationTask` 的 `lock(engineLock)` 内、`Parallel.For` 结束后返回，Postfix 在该点执行是线程安全的；不需要额外加锁。但必须处理 `TryBurstGroundEnergy` 提前返回的分支——Postfix 仍会执行，这正好是我们需要的。

### 4.3 空气源

```
falloff = pow(1 - d/R, airFalloffExp)
q = clamp((airTarget - Temperature[i]) * 0.5,
          -airMaxPerTick, +airMaxPerTick) * falloff
Temperature[i] += q * dt
```

通道：`AtmosphereSourceRates.TemperaturePerTick` + `RegisterContinuousSource`（`SourceSlots` 模式）。这条通道最终写 `pendingTemperatureDeltas`，与 `CompHeatSourceAtmosphere` 相同，但**不是**直接写 `pendingTemperatureDeltas`。

注意：

1. **多格建筑不要只注册一个格**。`CompHeatSourceAtmosphere` 只用 `parent.Position`，多格建筑会偏到左下角。桥接层应对照射区按扇区聚合（例如按 4×4 分块，每块一个源），或对每格注册（用 `SourceSlots` 批量管理句柄）。
2. **源必须随建筑销毁注销**：`PostDeSpawn` 里 `UnregisterContinuousSources(map, parent)`。
3. **`RatesPerTick` 必须是有限值**，否则 `ApplyContinuousSourcesUnsafe` 会把它当作无效源移除（:2553）。

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
    ExhaustTemperatureFloor = jetTemperatureCelsius,   // 是「下限」，不是设定值
    ExhaustPressureRatio    = 1.1f,
    InletPressureRatio      = 0.9f,
    ExhaustOxygenPercent    = 21f,
    WaterKgPerTick          = 0f,
}
RegisterAirMover(map, comp, "jet" + i, inlet, outlet, settings, activeTicks: 0)
```

`ConditionExhaust = true` 走 `ApplyJetTransportUnsafe`（AtmosphereMapComponent.cs:597-694）。**实际喷出温度**是：

```csharp
// AtmosphereMapComponent.cs:637-651
num3 = stepSettings.AmbientTemperatureCelsius;        // 环境
// 再对 inlet 相邻（横向或纵向）开放格的平均温度取 max
num9 = Mathf.Max(num3, settings.ExhaustTemperatureFloor);
```

所以 `ExhaustTemperatureFloor` 只是**下限**：环境更热时喷出环境温度，只有环境更冷时才抬到 floor。

注意事项（已按源码核对）：

1. `IsAirMoverPortOpen(cell)`（LocalAtmosphereEngine.cs:792-799）要求 `ObstacleMask > 0.001`、**非地图边界格**、四面 `FaceOpen` 和 > 0。Frostpunk 建筑 `passability = PassThroughOnly`，外圈格可通行。
2. `AirColumnHeightMetres > 0`，否则 `Valid = false`，源被静默移除。
3. `AirMoverHasReturnPath` **不是** ConditionExhaust 的门禁。`ApplyAirMoversUnsafe`（:472-493）只在 `!ConditionExhaust` 时检查它；ConditionExhaust 走 `ApplyJetTransportUnsafe` 的 `TryGetNozzleReturnPath`（:624），后者在 `AirMoverHasReturnPath` 失败时仍可能返回 true（Sky/Sealed 端）。
4. 但 `ApplyJetTransportUnsafe` **仍要求** `IsAirMoverPortOpen(inlet) && IsAirMoverPortOpen(outlet)`（:616），且 `JetTransport.HasOpenPath(engine, outlet, direction)` 为 false 时 `Volumes / Water` 归零（:712-716）。外圈格必须真的能通到开放空间。
5. 外圈格数：标准机 5×5 → 20 格；升级机 6×6 → **24 格**（v1 只按 20 格举例）。每格一条射流，角度间隔 18° / 15°。半径 60 格外相邻射流间隔约 19 格，稀疏——可加密或接受「射线状热柱」。
6. `CubicMetresPerTick` 是**总流量**，要除以实际注册的外圈格数，别写死 20。

### 4.5 融雪（暂缓，先看 AO 的效果）

ProxyHeat 的 `CompTemperatureSource.TempTick`（:182-214）每 60 tick 跑一次融雪：

```csharp
if (Gen.IsHashIntervalTick(parent, 60) && Props.smeltSnowRadius > 0f)
    foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, smeltSnowRadius, true))
        if (c.GetSnowDepth(map) > 0
            && GetTemperatureOutcomeFor(c, ...) > smeltSnowAtTemperature)
            SnowUtility.AddSnowRadial(c, map, ...);
```

Frostpunk 配的是 `smeltSnowRadius = 26 / 60`、`smeltSnowAtTemperature = 0`、`smeltSnowPower = 0.001`。

**AO 没有专门的融雪逻辑**（`f:\RimworldCodeIL\AtmosphereOverhaul\` 里只有 `snowGrid` 读取与天气派生）。原版的融雪分两路：

- **室内**：`SteadyEnvironmentEffects` 只在 `!roomUsesOutdoorTemperature` 时，用 `room.Temperature > 0` 融雪（`MeltAmountAt`）。
- **室外**：用 `map.mapTemperature.OutdoorTemp`（`outdoorMeltAmount`），**不看 AO 的温度场**。

所以接管后可能出现：室内雪会随 AO 房间温度自然融化；室外雪在 AO 加热区里不化，只有 ProxyHeat 的 `smeltSnowRadius` 能让它化。

**处理**：按用户指示，本轮不实现融雪。先实测 AO 接管后两种场景的实际表现，再决定是否在桥接层复刻 `smeltSnowRadius`（或改成「照射区内的雪按 AO 温度自然融化」）。在此之前，拦截 `MapComponentTick` 会让室外融雪功能暂时丢失，需在更新日志中注明。

---

## 5. 地温场与地暖的真实行为（AO 源码验证）

这一节是实现前必须读通的物理约束。

### 5.1 地温是一个独立标量场

`LocalAtmosphereEngine.GroundTemperature` 是 `float[]`，每格一个值，与空气温度 `Temperature` **地位平等、互不派生**。它只有一条与空气的强耦合（`GroundAirConductionRate` 或 `RoofedFloorExchangePerTick`），其余都是「地温自己的演化」。

初始化走 `SeedGroundTemperatureUniform`（`LocalAtmosphereEngine.cs:3060` 附近），用地图外部温度填满，再按每格地形的 `MinTemp/MaxTemp` 夹一次。

**地暖的本质是把热量注进一个和空气弱耦合的储库**，这解释了两件事：它升温慢，但它不会被空气一夜吹冷。

### 5.2 一步的完整算法

`AtmosphereGroundEnergyKernel.Advance`（`Simulation/AtmosphereGroundEnergyKernel.cs:18-82`）：

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
if (roofed) {
    if (roofThermalRate > 0)
        num7 += (roofSun - RoofAirCouplingRate*(T - Tamb)) * roofThermalRate * dt;  // ⑤ 屋顶项
    else if (airT > 15)
        num7 += (15 - airT) * min(0.5, 0.0015 * timeStep);         // ⑥ 深屋顶：封顶 15 ℃（v1 漏）
}
num7 -= transpiration * sunFrac * dt * LatentHeatPerUnit;          // ⑦ 蒸腾吸热
```

### 5.3 地气耦合的实际系数

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

- **无顶空气**同时被两个松弛拖：地气耦合 0.03 + 户外松弛 0.016（`OutdoorTemperatureRelaxation 0.15 → ScaleProbability 0.032 → EnableGroundHeating>0 时 ×0.5`）。
- **有顶空气**只有地气耦合 0.00102 + 屋顶泄漏 0.0003。泄漏比户外松弛小 53 倍。

结果：有顶的地气耦合虽然弱 30 倍，但因为「漏」少得多，**终值反而更高**。代价是速度：

| | 无顶 | 有顶 |
|---|---|---|
| 时间常数 | `1/(0.03+0.016)` = 22 步 ≈ **2.2 s** | `1/(0.00102+0.0003)` = 758 步 ≈ **76 s** |
| 达到 63% 所需 | 2 秒 | 76 秒 |

所以有顶房间的地暖是「慢但稳、终值高」，无顶户外是「快但被环境拖着走」。

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

**结论**：在 -200 ℃ 场景里，有顶房间只要 100 ℃ 地温就能到 +32 ℃。无顶户外则需要约 110 ℃ 才能到 0 ℃。

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

- **地暖与空气源不是「互不干扰」**，是「串联 + 反向耦合」
- 玩家把地暖目标设得比空气目标低很多，会显著拖累空气源
- 反过来，地暖目标高、空气源目标低时，地暖会帮空气源「托底」

实现上不需要仲裁逻辑——物理上就是叠加。但设置面板要提示玩家：「地暖目标低于空气目标时，会从空气里抽热」。

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
| **`EnableGroundHeating = 0`** | ❌ 无效 | `ApplyGroundEnergy` 入口（`LocalAtmosphereEngine.cs:3019`）直接 return |
| **实心格** | ❌ 无效 | `if (obstacle[i] <= 0f) return;` |

### 5.8 三个额外压制条件

**① 真空地图：耦合被乘零。**

```csharp
// RimWorld/AtmosphereMapComponent.cs:3981-3991
float num = Mathf.Clamp(engine != null ? engine.AmbientSurfacePressurePa / 100000f : AmbientPressureRatio(map), 0f, 2f);
settings.GroundOutdoorCouplingRate *= num;
settings.GroundAirConductionRate *= num;
```

真空地图 `AmbientSurfacePressurePa = 0` → `num = 0` → 耦合与外部松弛同时归零。地温既不传给空气，也不向环境散热，会一直累积。**真空环境必须用空气源或喷流。**

**② 恒温与有界地形。**

`GroundMaterialTable.ResolveGround`（`Simulation/GroundMaterialTable.cs:44-86`）读 `AtmosphereTerrainExtension`：

- `Fixed` → `MinTemp = MaxTemp = fixedTemperature`，地温完全钉死
- `Capped` → `MinTemp/MaxTemp = min/max`，注入被 `Clamp` 吃掉
- 默认地形 `±1e9`，不限制

**③ `GroundSkinAnchor` 会让户外地暖被「半覆盖」。**

```csharp
// Simulation/AtmosphereGroundEnergyKernel.cs（Advance 内）
if (GroundSkinAnchor > 0 && settings.GroundSkinExcessValid > 0.5) {
    float num6 = (Tamb + skinExcess*num5 - num3) * min(1, GroundSkinAnchorRate*thermalRate*dt);
    num4 += (num6 - num4) * min(1, GroundSkinAnchor);   // GroundSkinAnchor = 1 → num4 被替换成 num6
}
```

`GroundSkinAnchor` 默认 **1.0**，这一行等价于 `num4 = num6`——**玩家这一步注入的地温增量被整段覆盖**，地温被拉向「环境 + 太阳皮肤超温（clamp 在 -30..50 K）」。

但 `GroundSkinExcessValid` 只在**有 GPU 全球帧**时才是 1（`RefreshGroundSkinExcess`，AtmosphereMapComponent.cs:1684-1693：`!IsVacuumMap && TryGetCachedGlobalSample && sample.IsGpuAutodrive`）。所以：

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

所以「不限制温度上限」只能在**运行时**成立；**读档会把地温夹回 150 ℃**，把空气夹回 120 ℃。桥接层要么在读档后重新注入，要么在 UI 上明确提示「读档后需要重新升温」。

### 5.10 注入顺序

`ApplyGroundEnergy` 每步会重算并 `Clamp` 地温，`GroundSkinAnchor` 会覆盖无顶注入。正确的落地方式：

```
每 6 tick 的准备阶段（AtmosphereMapComponent.cs:1790-1816）：
  1. ApplyContinuousSourcesUnsafe()        // 连续源写入 pending* 与 groundTemperature（地温会被覆盖）
  2. StartSimulationTask() → lock(engineLock)
       ApplyGroundEnergy(...)              // AO 自己的太阳/辐射/耦合（含 GroundSkinAnchor）
       ← 桥接 Postfix 在这里注入地温（不被覆盖）
       ApplyTemperatureDeltas(...)         // 空气源（连续源）在这里落地
       ...
```

要点：

1. 地暖 Postfix **必须**打在 `ApplyGroundEnergy` 上，且用 `lastInjectTick` 去重——全局子循环（`AtmosphereMapComponent.cs:3043-3068`）每子步都会再调一次 `ApplyGroundEnergy`。
2. 空气源走 `TemperaturePerTick` 连续源即可，它天然在 `ApplyTemperatureDeltas` 里应用，不会被 `GroundSkinAnchor` 抹掉。
3. 不要用 `AtmosphereInterface.AddGroundTemperatureDelta`：它只是把增量排队到下一 tick 的 `pendingTemperatureDeltas`，最终仍在 `ApplyGroundEnergy` 之前，照样被覆盖。

### 5.11 量级校准（v2 新增）

这是 v1 最严重的参数设计问题。

#### 5.11.1 AO 自动点源的真实强度

`CompHeatSourceAtmosphere.CompTick`（Buildings/1HeatSources.cs:51-80）：

```csharp
float num3 = heatPerSecond * (pusherHeatFactor > 0 ? pusherHeatFactor : 3f) / 60f;
float num5 = heatPerSecond < 0 ? clamp01((T+273.15)/150) : (T <= 200 ? 1f : clamp01((300-T)/100));
rates.TemperaturePerTick = num3 * num1 * num5;   // num1 = 燃烧氧气系数 1..3
```

`ApplyContinuousSourcesUnsafe`（AtmosphereMapComponent.cs:2588-2589）：

```csharp
int num4 = ticksGame - continuousSource.LastAppliedTick;   // 通常 6
pendingTemperatureDeltas[index] += rates.TemperaturePerTick * num4;
```

代入 Frostpunk：

| 机型 | `heatPerSecond` | `TemperaturePerTick` | 每 6 tick 实际 | 钳制 |
|---|---|---|---|---|
| 标准机 | 750 | **37.5 ℃/tick** | **+225 ℃** | 单格 >200 ℃ 后 `num5` 线性衰减，~300 ℃ 停 |
| 升级机 | 2500 | **125 ℃/tick** | **+750 ℃** | 同上 |

**这比 ProxyHeat 的 `maxTemperature = 30 / 50` 高一个数量级。** AO 点源会主动把建筑格烧到 200~300 ℃，而 ProxyHeat 的设计语义是「26/60 格内读数 +80/+200，最高 30/50」。

#### 5.11.2 叠加会反向制冷

桥接空气源是闭环：`q = clamp((airTarget - T) * 0.5, ±airMaxPerTick)`。

- 若保留 AO 点源，`T` 很快超过 30/50 ℃ → `q` 变负 → 桥接层主动从 AO 场里抽热。
- 结果：玩家打开「空气源」反而让建筑周围变冷；关掉才回到 AO 点源的 200~300 ℃。
- 这与「用 ProxyHeat 的意图加热」完全相反。

**所以不能叠加，只能接管。**

#### 5.11.3 接管方式

在 AO 的 `HeatSourceInjection` 静态构造执行完之后，把带有 ProxyHeat `CompProperties_TemperatureSource` 的 Def 上的 `CompProperties_HeatSourceAtmosphere` 移除：

```csharp
// 在 Bootstrap 的 LongEventHandler.ExecuteWhenFinished 阶段（保证所有
// [StaticConstructorOnStartup] 已跑完）
Type aoCompProps = AccessTools.TypeByName("AtmosphereOverhaul.Buildings.CompProperties_HeatSourceAtmosphere");
Type proxyProps  = AccessTools.TypeByName("ProxyHeat.CompProperties_TemperatureSource");
if (aoCompProps != null && proxyProps != null)
{
    List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
    for (int d = 0; d < allDefs.Count; d++)
    {
        ThingDef def = allDefs[d];
        if (def.comps == null || def.comps.Count == 0) continue;

        bool hasProxy = false;
        for (int c = 0; c < def.comps.Count; c++)
        {
            if (proxyProps.IsInstanceOfType(def.comps[c])) { hasProxy = true; break; }
        }
        if (!hasProxy) continue;

        // 倒序删除，避免索引失效；不用 Linq 的 Any/RemoveAll 以减少分配
        for (int c = def.comps.Count - 1; c >= 0; c--)
        {
            if (aoCompProps.IsInstanceOfType(def.comps[c])) def.comps.RemoveAt(c);
        }
    }
}
```

移除后 `CompHeatSourceAtmosphere` 不再 tick，AO 点源消失；由桥接层统一供能。

> 注意：`def.comps` 是 `List<CompProperties>`，用两层 `for` 比对（不用 Linq 的 `Any`/`Where`），删除用倒序 `RemoveAt`。AGENTS.md 要求减少 Linq 分配。

#### 5.11.4 接管后的功率分配

接管后，建筑唯一的能量入口是桥接层。按 Def 语义分配：

| 模式 | 通道 | 默认目标 | 默认功率上限 | 量级依据 |
|---|---|---|---|---|
| 空气源 | `TemperaturePerTick` 连续源 | `maxTemperature`（30 / 50） | 2 ℃/tick/格 | 26 格照射区约 2100 格，游标 500 格 → 4.2 tick 扫一遍，等效区域升温 ≈ 0.48 ℃/tick |
| 地暖 | `GroundTemperaturePerTick` / Postfix | 25 ℃ | 2 ℃/tick/格 | 地温与空气弱耦合，靠长时间积累；上限只是防瞬移 |
| 喷流 | `RegisterAirMover` | `tempOutcome`（80 / 200）作下限 | 100 m³/tick / 格 | `ExhaustTemperatureFloor` 是下限，实际喷出 `max(环境, floor)` |

**为什么不再按 `heatPerSecond` 分配**：`heatPerSecond` 的单位是「原版 HeatPusher 的功率」，AO 把它换算成「单格温度增量」。ProxyHeat 的语义是「区域读数」，不是「单格功率」。接管后若仍按 750/2500 分配，等于把 AO 的 225/750 ℃/6tick 单格强度搬到 2100 格上，总量会爆掉。正确做法是**用闭环目标表达意图，用功率上限控制速度**——这也正是 §0②「用场模拟意图」的落地方式。

**如果要让「Def 声称的 +80 ℃ / 26 格」在物理上接近**：需要 10⁹ W 量级，AO 的场承受不起，也不该承受。桥接层的目标不是复现读数，而是「让玩家感觉到这里有一个强热源，且范围与 ProxyHeat 一致」。

---

## 6. 温度无上限的后果

`ClampTemperature`（`Simulation/LocalAtmosphereEngine.cs:4415`）只有下限：

```csharp
float num = settings != null ? settings.MinimumTemperatureCelsius : Default.MinimumTemperatureCelsius;
return value < num ? num : value;
```

`GroundMaterialTable.ResolveGround` 的默认 `MinTemp = -1e9f`、`MaxTemp = 1e9f`，只有地形带 `AtmosphereTerrainExtension`（`mode = Fixed/Capped`）时才有界。

所以四个后果：

**① 地温可以无限升。** 玩家把地暖目标设 2000 ℃，地温就真的到 2000 ℃。植物根区瞬间烤死、`Item` 可能自燃、pawn 走上去立刻中暑。

**② 空气源的闭环没有天花板。** 但闭环本身是稳定的：`q = (target − T) × 0.5`，`T` 会收敛到 `target`，不会发散。风险在玩家把 `target` 设得过高。

**③ 喷流是唯一绕过闭环的通道。** `ExhaustTemperatureFloor` 是**下限**，实际喷出温度 `max(环境, 入口邻格均温, floor)`。它没有「目标 − 当前」的反馈，设 2000 ℃ 就至少喷 2000 ℃（环境更热时更高）。这是四条里最危险的。

**④ 绝热正反馈。** AO 的 `StoreCompressiblePressure` 用泊松关系：

```
ΔT = T_K × ((p/p₀)^(0.4·factor·min(1,p/1e5)) − 1)
```

温度升 → 热膨胀 → `externalDivergence` → 压力升 → 绝热压缩 → 温度再升。这条链在 `stored` 被 clamp 到 19 atm 时封顶，理论放大倍率约 `19^0.4 ≈ 3.3`，不是无限，但足以让「设 500 ℃」变成「实际 1600 ℃」。

**处理**：不拦，但在面板上加一行提示。保留功率上限（`groundMaxPerTick` / `airMaxPerTick` / `jetCubicMetresPerTick`）与喷流温度下限作为唯一的安全阀——用户说的是「不限制温度上限」，不是「不限制功率」。

---

## 7. 蓝信（Letter）

### 7.1 触发条件

在 `LongEventHandler.ExecuteWhenFinished` 或 `GameComponent.FinalizeInit` 里检查：

```csharp
bool proxyHeatLoaded = ModLister.GetActiveModWithIdentifier("...") != null;
bool vtePatchLoaded  = ModLister.GetActiveModWithIdentifier("ewolfy.vtePatch") != null;
```

`ewolfy.vtePatch` 是 VTE-ProxyHeatPatch 的 Harmony id（VTE_ProxyHeatPatch.cs:16）。

### 7.2 内容

- **正常情况**（只装了 ProxyHeat）：告知玩家 ProxyHeat 的热场已被本 mod 接管，其读数加成已关闭；不需要在 VTE 设置里操作。
- **警告情况**（同时装了 VTE-ProxyHeatPatch）：额外提示该 patch 用 Transpiler 把 `brfalse_S` 改成 `Nop` 并删除 `Ldarg_2; Ret`（ProxyHeatManager_GetTemperatureOutcomeFor_Patch.cs:21-57），移掉「室内提前 return」。**它与我们的 Prefix 可以共存**（Transpiler 是编译期改写，Prefix 是运行时短路），但 `Nop` 后栈上残留 bool，属于 IL 栈不平衡，行为不可预测，建议移除。

### 7.3 类型

`LetterDefOf.NeutralEvent`（蓝色）。

---

## 8. 实现顺序

| 阶段 | 内容 | 依赖 |
|---|---|---|
| P0 | 拦截层：`GetTemperatureOutcomeFor` Prefix（`__result = 入参`）+ `MapComponentTick` Prefix | 无 |
| P0 | `CompProxyHeatBridge` 骨架 + XML 注入 | 无 |
| P0 | 移除 AO 自动点源（`CompProperties_HeatSourceAtmosphere`） | 需先确认 AO 静态构造已执行 |
| P1 | 照射区：`FloodFill + LineOfSight` + 缓存 + 失效重建 | P0 |
| P1 | 地暖（`ApplyGroundEnergy` Postfix + 游标分片 + 去重） | P1 照射区 |
| P1 | 空气源（连续源 + 分块聚合） | P1 照射区 |
| P2 | 喷流（`RegisterAirMover`，外圈 `footprint` 格数动态） | P0 |
| P2 | Gizmo + 三个设置面板（默认值跟随 Def） | 三模式 |
| P3 | 蓝信 | P0 |
| P3 | 融雪（先实测 AO，见 §4.5） | 待定 |
| P3 | 文档 + 不兼容声明 | 全部 |

---

## 9. 未决项

1. ~~`AirMoverHasReturnPath` 是否对 `ConditionExhaust=true` 生效~~ —— **已答**：不生效，`ApplyAirMoversUnsafe` 只在 `!ConditionExhaust` 时检查它；`ApplyJetTransportUnsafe` 走 `TryGetNozzleReturnPath`，Sky/Sealed 端可返回 true。但 `IsAirMoverPortOpen` 与 `HasOpenPath` 仍是硬门禁。
2. ~~喷流的 inlet 用外圈格还是建筑中心~~ —— **已答**：用 footprint 外圈格；`IsAirMoverPortOpen` 要求非边界、`ObstacleMask > 0.001`、`FaceOpen` 和 > 0。标准机 20 格、升级机 24 格。
3. **射线扇的半格偏移**——若启用射线扇优化，源点在格心还是格角会影响对称性；默认实现不用射线扇，此问题降级为可选优化项。
4. **三模式同时开启时的 UI 表达**——一个光斑还是三个光斑。
5. **地温的横向传导**——AO 没有，辐射阴影边界是锐利的。是否在桥接层补一层边界平滑。
6. ~~`tempOutcome` 动态值的处理~~ —— **已答**：`TemperatureOutcome` 在 `Props.tempOutcome.HasValue` 时用固定值；否则最近 250 tick 内用 `lastRoomTemperatureChange`，否则 0。接管 `MapComponentTick` 后 `lastRoomTemperatureChange` 不再更新，动态值会退化为 0。**桥接层只读 Def 固定值**；若某 Def 没配 `tempOutcome`，桥接层按 `heatPerSecond` 推导或直接视为「无 ProxyHeat 意图」。
7. **融雪**：先实测 AO 接管后室内/室外雪的表现，再决定是否复刻 `smeltSnowRadius`（§4.5）。
8. **AO 自动点源接管的时机**：`HeatSourceInjection` 是 `[StaticConstructorOnStartup]`，本 mod 的移除逻辑必须在它之后执行；用 `LongEventHandler.ExecuteWhenFinished` 还是 `FinalizeInit` 需要实测确定。

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

**注意**：地温相关的九个系数全部不在 `ScaleForInterval` 的缩放列表里（`Simulation/AtmosphereTuningManager.cs:244-282` 只列了扩散/松弛/蒸发/边界那一批），所以它们保持 Def 默认值，只被 `timeStep = 0.2` 直接乘。

### 附录 A.2：点源量级速查

| 项 | 标准机 | 升级机 |
|---|---|---|
| `heatPerSecond` | 750 | 2500 |
| `pusherHeatFactor` 默认 | 3 | 3 |
| `TemperaturePerTick` | 37.5 | 125 |
| 连续源每 6 tick 实际增量 | **+225 ℃** | **+750 ℃** |
| `num5` 钳制 | >200 ℃ 后衰减，~300 ℃ 停 | 同上 |
| ProxyHeat `radius` | 26 | 60 |
| ProxyHeat `tempOutcome` | 80 | 200 |
| ProxyHeat `maxTemperature` | 30 | 50 |
| `smeltSnowRadius` | 26 | 60 |

---

## 附录 B：源码索引

| 文件 | 行 | 内容 |
|---|---|---|
| `ProxyHeat/ProxyHeatManager.cs` | 45-67 | `MapComponentTick`（不是 `Tick`） |
| `ProxyHeat/ProxyHeatManager.cs` | 69-109 | `GetTemperatureOutcomeFor` 完整逻辑 |
| `ProxyHeat/HarmonyPatches.cs` | 25-41 | 硬编码 patch 目标列表 |
| `ProxyHeat/HarmonyPatches.cs` | 116-139 | `TickRare_Transpiler`（只插入记录，不替换原版） |
| `ProxyHeat/HarmonyPatches.cs` | 161-178 / 180-212 | `RegisterRoomTemperatureChange` / `RegisterHeatPush` |
| `ProxyHeat/HarmonyPatches.cs` | 361-374 | `Thing.AmbientTemperature` Postfix（开关检查 :366） |
| `ProxyHeat/CompTemperatureSource.cs` | 38-46 | `TemperatureOutcome` 动态值 |
| `ProxyHeat/CompTemperatureSource.cs` | 93-139 | `RecalculateAffectedCells`（FloodFill + LineOfSight） |
| `ProxyHeat/CompTemperatureSource.cs` | 146-160 | `PostDrawExtraSelectionOverlays` |
| `ProxyHeat/CompTemperatureSource.cs` | 182-214 | `TempTick`（含每 60 tick 融雪） |
| `AO/Buildings/1HeatSources.cs` | 19-93 | `CompHeatSourceAtmosphere`（点源） |
| `AO/Buildings/2HeatSources.cs` | 16-45 | `HeatSourceInjection`（自动加点源） |
| `AO/Buildings/1HeatedFloorMat.cs` | 23-74 | `CompHeatedFloorMat`（AO 自带地暖垫） |
| `Simulation/AtmosphereGroundEnergyKernel.cs` | 18-82 | `Advance` 地气耦合（含深屋顶分支） |
| `Simulation/LocalAtmosphereEngine.cs` | 3003-3017 | `ApplyTemperatureDeltas` |
| `Simulation/LocalAtmosphereEngine.cs` | 3019-3062 | `ApplyGroundEnergy` |
| `Simulation/LocalAtmosphereEngine.cs` | 4415-4419 | `ClampTemperature`（只有下限） |
| `Simulation/LocalAtmosphereEngine.cs` | 627-762 / 764-782 / 792-799 | `AirMoverHasReturnPath` / `TryGetNozzleReturnPath` / `IsAirMoverPortOpen` |
| `Simulation/AtmosphereTuningManager.cs` | 244-282 / 291-296 | `ScaleForInterval` / `ScaleProbability` |
| `Simulation/LocalAtmosphereSettings.cs` | 258-268 | 地温相关默认值 |
| `Simulation/GroundMaterialTable.cs` | 44-86 | `ResolveGround`（Fixed/Capped） |
| `RimWorld/AtmosphereMapComponent.cs` | 1790-1816 | tick 准备阶段（`ApplyContinuousSourcesUnsafe` 在 1813） |
| `RimWorld/AtmosphereMapComponent.cs` | 2534-2620 | `ApplyContinuousSourcesUnsafe` |
| `RimWorld/AtmosphereMapComponent.cs` | 2962-3068 | `StartSimulationTask` 与全局子循环 |
| `RimWorld/AtmosphereMapComponent.cs` | 472-493 / 597-694 / 712-716 | `ApplyAirMoversUnsafe` / `ApplyJetTransportUnsafe` / `HasOpenPath` |
| `RimWorld/AtmosphereMapComponent.cs` | 1684-1693 | `RefreshGroundSkinExcess` |
| `RimWorld/AtmosphereMapComponent.cs` | 1135-1142 / 1619-1661 | 存档编码/解码窗口 |
| `RimWorld/AtmosphereScribeUtility.cs` | 15-35 | `EncodeInt16` / `DecodeInt16` |
| `RimWorld/AtmosphereSourceRates.cs` | 12-24 | 连续源字段（含 `GroundTemperaturePerTick`） |
| `RimWorld/AtmosphereInterface.cs` | 18-31 / 228-250 / 300-358 | `RegisterAirMover` / `Add*Delta` / `RegisterContinuousSource` |
| `RimWorld/AtmosphereAirMover.cs` | 13-49 | `AtmosphereAirMoverSettings` |
| `VTE-ProxyHeatPatch/VTE_ProxyHeatPatch.cs` | 16 | `new Harmony("ewolfy.vtePatch")` |
| `VTE-ProxyHeatPatch/ProxyHeatManager_GetTemperatureOutcomeFor_Patch.cs` | 21-57 | Transpiler（`Nop` + 删 `Ldarg_2; Ret`） |
