# CCC 接入与「强制稳定」方案

> 面向 `JeremieAtmosphereOverhaulPatch` 的下一阶段实现。
> 本轮只做分析与方案，不改任何源码。
> 证据来自：AO 1.7 反编译（MVID `4e9f1eae1c1d40e48ce779113df84ba7`）、CCC 1.6 反编译（MVID `5eb3120a988a43778f49b68d29aeeedf`）、超维科技源码。

---

## 0. 结论速览

| # | 问题 | 现状 | 方案 | 优先级 |
|---|------|------|------|--------|
| 1 | CCC 在 1.7 到底加载不加载 | **加载**（`LoadFolders.xml` 无 `<v1.7>` 时回退到 v1.6） | 把 CCC 模块从「占位」改成真模块 | P0 |
| 2 | AO 对 CCC 温控的接管程度 | 温度**已经**进 AO 场（写钩子 + `PushHeat` 补丁）；真空判定脱节 | 只补「真空/气压一致性」与「风口局部化」 | P0/P1 |
| 3 | CCC 风口写 `Room.Temperature` | 被 AO 翻译成整房间逐格平移（≤3000 格、≤200 K） | 改成风口前几格 `AddDeltas`，带风、带湿度 | P1 |
| 4 | CCC 吸气扇 | 读 `VacuumUtility.GetVacuum`，不改变 AO 气压 | 补 `AddPressureDelta(-x)` | P2 |
| 5 | 超维生物圈 `controlTemperature` | 只改**读取值**（`GetTemperatureForCell` Postfix），物理场不变 | 区域内逐格 `AddDeltas` 强制 | P0 |
| 6 | 超维穹顶 `maintainTemperature`/`blockVacuum` | 同上，只改 `TryGetTemperatureForCell` 与 `VacuumUtility.GetVacuum` | 区域内逐格 `AddDeltas` 强制 | P0 |
| 7 | 强制稳定的正确节奏 | —— | 必须与 AO 求解器步长（默认 6 tick）对齐，否则过冲 | 关键 |

---

## 1. 事实更正：CCC 在 1.7 是能加载的

上一轮我判断「CCC 只声明到 1.6，所以 1.7 下不加载」是**错的**（用户 m00916 已纠正，这里给出代码依据）。

`Source/Verse/ModContentPack.cs:232` 的 `InitLoadFolders()`：

```csharp
List<string> list = LoadFoldersForVersion(VersionControl.CurrentVersionString);
if (list.NullOrEmpty())
{
    // 从 DefinedVersions() 里挑 <= 当前版本的最大版本
    // 过滤掉 "default"，按 VersionFromString 比较
    // 命中则 AddFolders(该版本对应的目录)
}
```

CCC 的 `LoadFolders.xml` 最高只有 `<v1.6>`，因此在 1.7 下：

1. `LoadFoldersForVersion("1.7")` 返回空；
2. 回退逻辑挑出 `v1.6`（`1.6 <= 1.7` 的最大值）；
3. `1.6/Assemblies/CentralizedClimateControl.dll`（41472 字节）与 `1.6/Defs/**` 全部加载。

**结论**：`ModLister.GetActiveModWithIdentifier("Mlie.CentralizedClimateControl", true)` 在 1.7 下返回非 null，`CompatModuleBase.IsTargetLoaded` 为 true，`TryApply` 会被调用。当前 CCC 模块返回 `Skipped` 的占位实现会让玩家以为"没生效"，实际 CCC 在跑、且与 AO 已经在交互。这一条必须优先修正（至少改成能显示真实状态）。

---

## 2. AO 已经接管了 CCC 的哪些行为（已逐条查证）

### 2.1 温度：已进 AO 场

| CCC 行为 | 原版语义 | AO 下的实际结果 |
|---|---|---|
| `Building_AirVent.TickRare()` 末尾 `roomOrAdjacent.Temperature += num7` | 直接改房间温度 | `RoomTempTracker.set_Temperature` → `RoomTemperatureWriteHook.OnSet` → `ApplyRoomTemperatureWrite(room, value, 200f)`：**整房间逐格平移**，单次上限 200 K、房间上限 3000 格 |
| `Building_AirThermal.TickRare()` 制冷分支 `GenTemperature.PushHeat(val2, Map, num2 * 1.25f)` | 给南侧房间加热 | AO 的 `GenTemperaturePushHeatPatch`（Prefix，priority 0）整段替换为 `AtmosphereMapComponent.PushHeat(cell, energy)` → `QueueLocalizedTemperatureDelta`，**进 AO 温度场** |
| `Room.PushHeat` | 改房间温度 | AO 的 `Room_PushHeat_EnginePatch` 同样转成 `PushHeat` |

也就是说：**CCC 的温度链路不需要"抢救"，它已经在 AO 场里**。问题不在"有没有生效"，而在"生效的形状不对"。

### 2.2 真空：完全脱节

- `Building_AirVent.TickRare()` 读 `roomOrAdjacent.Vacuum`。原版 `Room.Vacuum` 的语义是：`!Map.Biome.inVacuum` 恒 0；`ExposedToSpace` 为 1；门洞取邻居平均；否则 `>0.01` 才返回值。
- `Building_IntakeFan.TickRare()` 读 `VacuumUtility.GetVacuum(item, map)`（内部同样落到 `Room.Vacuum`）。
- AO **不接管** `Room.Vacuum`，也不接管 `VacuumUtility.GetVacuum`（AO 的补丁列表里没有这两个）。AO 用的是自管气压场（`TryGetPressurePascals`）。
- 后果：AO 下被抽成低压的密封房间，`Room.Vacuum` 仍是 0 → CCC 的风口不会减速、不会按真空衰减 `num7`；反过来，原版判定的"太空真空"在 AO 已经加压的房间也会误判。

### 2.3 管网 / 气流：AO 完全不碰

`CompAirFlowTempControl`、`CompAirFlow`、`Building_AirPipe` 这一套是 CCC 自己的；AO 没有对应概念（AO 的等价物是 `AtmosphereSourceRates` 的 `TemperaturePerTick` + `MaintainTargetWind`）。所以管网效率、`ExhaustAirFlow`、`FlowEfficiency`、`ThermalEfficiency` 都还能用，只是它们最终落到 AO 场时只剩"温度"这一个自由度。

---

## 3. AO 的写入原语与语义（方案基石）

### 3.1 `AtmosphereInterface.AddDeltas`

```csharp
public static bool AddDeltas(Map map, IntVec3 cell, AtmosphereDeltas deltas);
```

`AtmosphereDeltas` 字段与单位：

| 字段 | 类型 | 单位 / 含义 |
|---|---|---|
| `Temperature` | float | 摄氏度增量（K 与 ℃ 同尺度） |
| `GroundTemperature` | float | 地面温度增量 |
| `Moisture` | float | 绝对水汽增量（hPa·cell 量级，与 `TryGetAbsoluteVapor` 同量纲） |
| `Mist` | float | 雾密度增量 |
| `Pressure` | float | **solver 单位**，见 3.2 |
| `OxygenPercent` | float | 氧浓度百分点增量 |
| `Wind` | Vector2 | 风速增量（m/s） |

**关键语义（必须记住）**：`AddDeltas` 不会立刻改场，而是把各字段**累加**进 `incoming*Deltas[index]` 缓冲：

```csharp
incomingTemperatureDeltas[index] += deltas.Temperature * scale;
incomingPressureDeltas[index]    += deltas.Pressure * scale;
// ... 其余字段同理
temporalPatchManager.RecordImpulse(...);
```

求解器下一次步进时统一应用。因此：

- 传 `target - current` 可以"一步补足"；
- **同一个求解器步内多次调用会叠加**，会过冲；
- 正确节奏是**每个求解器步调用一次**，即 `map.IsHashIntervalTick(AtmosphereOverhaulSettings.SimulationIntervalTicks)`（默认 6）。

### 3.2 压力换算（不需要闭环标定）

```csharp
// AtmosphereOverhaul.RimWorld.AtmosphereColorUtility
public static float CurrentSolverPressureToPascals() => 1000f;

public static float PressurePascalsFromSolver(float solverPressure)
    => 100000f + solverPressure * CurrentSolverPressureToPascals();
```

因此：

```
pascals = 100000 + solver * 1000
solver  = (pascals - 100000) / 1000
```

- 常压 101325 Pa → solver ≈ **1.325**
- `AddPressureDelta(map, cell, deltaSolver)` 的 `deltaSolver` 就是这个单位（1 = 1000 Pa）。

### 3.3 其他原语

| 方法 | 语义 | 适用场景 |
|---|---|---|
| `AddVolumeSource(map, cell, cellVolumes, oxygenPercent, temperatureOffsetCelsius)` | 注入体积并混合气体（`MixReleasedGas`） | 充气、补氧；**会升压，不要用来"维持"** |
| `AddVolumeSourceAtTemperature(...)` | 同上，显式给气温 | 同左 |
| `AddOxygenPercentDelta` / `AddMoistureDelta` / `AddMistDelta` / `AddTemperatureDelta` / `AddPressureDelta` / `AddWindImpulse` | 单字段增量 | 只想改一项时 |
| `AddDeltas(map, CellRect, deltas)` | 一片区域同一组增量 | 区域初始化的"大补" |
| `TryGet*` 系列 | 读当前值，失败返回 false + 0 | 算 `target - current` |

### 3.4 不要用 `Room.Temperature` 写

- 上限 3000 格，超过静默忽略；
- 单次 `MaxDeltaKelvin = 200`；
- 语义是"整房间平移"，无法表达"从风口吹出"。

---

## 4. 超维生物圈：强制稳定

### 4.1 现状

`CompBiosphere.ApplyEffects()` 第 398-400 行只有：

```csharp
if (controlTemperature)
{
    GenTemperature.PushHeat(cell, map, 0.01f);
    // 象征性的推一点热量，为了让原版系统注意到 ...
    // 实际强制设置温度将通过 Patch 实现以保证稳定性
}
```

`Patch_CompBiosphere.cs:472` 的 `Patch_GenTemperature_GetTemperatureForCell` 只是在读取时返回目标值；`Patch_CompBiosphere.cs:685` 附近 patch `Room.OpenRoofCount` 让房间"自认为有屋顶"。

**结论**：UI 上看到 21 ℃，但 AO 的物理场（温度、气压、氧气、湿度）一点没动。风照样吹、气压照样漏、火照样烧氧。

### 4.2 目标

区域内每一格，每 N tick 被拉回：

- 温度 = `targetTemperature`
- 气压 = 常压（101325 Pa，solver 1.325）
- 氧气 = 21%
- 雾 = 0
- 风 = 0（区域内"强制无风"）
- 湿度 = 可选（50% RH），默认关闭

### 4.3 区域来源

`CompBiosphereManager.GetCompsForMap(Map)` 是 **public**，`CompBiosphere.SelectedArea`（`public Area`）与 `controlTemperature`/`targetTemperature`/`ensureNoVacuum` 都是 public，因此兼容 DLL 可以：

```csharp
var comps = CompBiosphereManager.GetCompsForMap(map);
foreach (var comp in comps)
{
    if (!comp.controlTemperature) continue;
    Area area = comp.SelectedArea;
    if (area == null) continue;
    foreach (IntVec3 c in area.ActiveCells) { ... }
}
```

不需要反射，也不需要改超维源码。

### 4.4 写入算法

```csharp
// 每个 AO 求解器步调用一次
static void StabilizeCell(Map map, IntVec3 cell, float targetCelsius, float targetOxygenPercent)
{
    if (!AtmosphereApi.TryGetTemperature(map, cell, out float t)) return;
    if (!AtmosphereApi.TryGetPressurePascals(map, cell, out float pa)) return;
    if (!AtmosphereApi.TryGetOxygenPercent(map, cell, out byte oxy)) return;

    var d = AtmosphereApi.NewDeltas();          // 反射 new AtmosphereDeltas()

    d.Temperature   = Mathf.Clamp(targetCelsius - t, -20f, 20f);   // 限幅，见 §9
    d.Pressure      = Mathf.Clamp((101325f - pa) / 1000f, -0.3f, 0.3f);
    d.OxygenPercent = Mathf.Clamp(21f - oxy, -5f, 5f);

    if (AtmosphereApi.TryGetMist(map, cell, out float mist) && mist > 0f)
        d.Mist = -mist;

    if (AtmosphereApi.TryGetWind(map, cell, out Vector2 w) && w.sqrMagnitude > 0.01f)
        d.Wind = -w;

    AtmosphereApi.AddDeltas(map, cell, d);
}
```

要点：

1. **用 `AddDeltas` 而不是 `AddVolumeSource`**：后者会往格子里塞体积，密封房间会持续升压。
2. **限幅**：一次只拉一部分（温度 ±20 K、气压 ±0.3 solver = 300 Pa），多步收敛。直接把 1000 Pa 的压差一步补上可能触发求解器不稳定。
3. **只读一次温度**：如果同时要改湿度，注意温度变化会改变饱和水汽，应先温度后湿度、或干脆不强制湿度。

### 4.5 频率、分片与预算

- `interval = AtmosphereOverhaulSettings.SimulationIntervalTicks`（默认 6）——必须与求解器同步，否则累加过冲。
- 区域可能很大（生物圈半径可达几十格）。建议：
  - `ForceFieldMaxCellsPerTick`（默认 2000）；
  - 用**游标轮转**而不是每次从头开始，保证每个格都被覆盖；
  - 每格 7 次反射调用（6 读 + 1 写），2000 格/步 ≈ 14000 次反射 ≈ 7-14 ms/步。6 tick 一步时这个开销偏高，建议默认降到 500 格/步，或把 interval 拉到 30 tick（成本降 5 倍，收敛仍可接受）。

### 4.6 与读值补丁的关系

超维自己的 `GetTemperatureForCell` Postfix 会覆盖 AO 的读数。两条路都保留没有坏处：

- AO 在场里已是 21 ℃ → Postfix 只是重复返回 21 ℃；
- 非 AO 地图 → Postfix 仍然生效，超维行为不变。

**不要**去 `Unpatch` 超维的补丁（跨 mod 卸载别人的补丁是高风险操作）。

---

## 5. 超维穹顶：强制稳定

### 5.1 现状

`CompOmniForceFieldDome.MaintainEnvironment()`（第 1107-1147 行）只做四件事：

```csharp
if (props.clearGas)       map.gasGrid.ClearCellUnsafe(c);
if (props.clearPollution) map.pollutionGrid.SetPolluted(c, false, true);
if (props.cleanFilth)     FilthMaker.RemoveAllFilth(c, map);
if (props.extinguishFire) ExtinguishFiresAt(c, map);
```

`props` 里有 `maintainTemperature`/`targetTemperature`/`blockVacuum`，但**没有任何物理写入**；温度与真空只靠两个 Postfix（`TryGetTemperatureForCell`、`VacuumUtility.GetVacuum`）改读取值。

### 5.2 区域来源

`OmniForceFieldDomeNetworkManager` 是 `MapComponent`，public API 可用：

```csharp
var mgr = OmniForceFieldDomeNetworkManager.GetInstance(map);
if (mgr != null && mgr.IsDomeCell(c) && mgr.TryGetPropsAt(c, out var props))
{
    if (props.maintainTemperature) ...
    if (props.blockVacuum) ...
}
```

`IsDomeCell` 是 O(1) 的 `networkCache` 查询，适合逐格判断。

穹顶是圆形，`CompOmniForceFieldDome.DomeRect` 只是外接矩形。两种遍历方式：

1. **矩形 + `IsDomeCell` 过滤**（简单，但矩形面积 = 4R²，圆 = πR²，浪费 27%）；
2. **从管理器拿 `networks`**：`networks` 是 private，但 `CompOmniForceFieldDome` 本身可以注册进我们自己的列表（patch `PostSpawnSetup`/`PostDeSpawn`），再用 `DomeRect` + `IsDomeCell`。

推荐 1 的变体：维护一个 `List<CompOmniForceFieldDome>` 缓存（每 250 tick 用 `map.listerBuildings.allBuildingsColonist` 扫一遍，建筑数量通常几百，成本可忽略），然后只遍历 `DomeRect` 内且 `IsDomeCell` 为真的格。

### 5.3 与生物圈的差异

| 项 | 生物圈 | 穹顶 |
|---|---|---|
| 区域 | `Area.ActiveCells` | `DomeRect` ∩ `IsDomeCell` |
| 温度目标 | `comp.targetTemperature` | `props.targetTemperature` |
| 气压目标 | 是否 `ensureNoVacuum` 决定 | 是否 `blockVacuum` 决定 |
| 典型大小 | 小（半径十几格） | 可能很大（半径几十格） |
| 额外 | 无 | `clearGas` 已由超维清原版气体，但我们仍需清 AO 气体（`AddGas` 负值或 `AddDeltas` 不支持气体名——见 §5.4） |

### 5.4 气体清除

`AtmosphereDeltas` 没有「按气体名清零」的字段。要清 AO 的自定义气体，需要 `ForEachGasCell` + `AddGas` 负值，或者 `AddGas(def, cell, -density)`。方案里先不做（超维的 `clearGas` 清的是原版 `GasGrid`，AO 的 `AO_BlindSmoke` 等是另一套）；后续可作为 P2。

---

## 6. CCC：三档方案

### 6.1 P0 —— 真空判定改 AO 气压

**目标**：让 CCC 的风口在 AO 的低压/真空房间里正确减速、正确衰减。

做法：Prefix 接管 `Building_AirVent.TickRare`，复刻原逻辑，只把

```csharp
float vacuum = roomOrAdjacent.Vacuum;
```

换成

```csharp
float vacuum = AtmosphereApi.TryGetPressurePascals(map, val, out float pa)
    ? Mathf.Clamp01(1f - pa / 101325f)   // 101325 Pa → 0，0 Pa → 1
    : roomOrAdjacent.Vacuum;             // 非 AO 地图回退原版
```

其余（`num6`、`num7`、`ControlTemperatureTempChange`、`roomOrAdjacent.Temperature += num7`）保持原样——因为写钩子会把温度翻译进 AO 场。

风险：

- 复刻 60 行逻辑，CCC 更新后可能漂移；
- `AccessTools.Method(typeof(Building_AirVent), "TickRare")` 找不到就降级（不打补丁），状态显示 `Skipped`。

### 6.2 P1 —— 风口局部化

**目标**：把「整房间平移 `num7`」改成「风口前 N 格注入 `num7` 的一部分 + 风」。

做法：在接管后的 `TickRare` 里，不再写 `roomOrAdjacent.Temperature`，而是：

```csharp
IntVec3 front = val;                       // 风口正对格
var cells = GenRadial.RadialCellsAround(front, 2.9f, true);
float share = num7 / cells.Count();
foreach (IntVec3 c in cells)
{
    var d = AtmosphereApi.NewDeltas();
    d.Temperature = share;
    d.Moisture    = share * 0.02f;         // 冷风带一点冷凝
    d.Wind        = new Vector2(rot.FacingCell.x, rot.FacingCell.z) * 2f;
    AtmosphereApi.AddDeltas(map, c, d);
}
```

收益：风口有方向性、有风、能影响局部湿度；不再受 3000 格限制。

代价：房间整体温度不再被直接拉平，玩家可能觉得"效果变弱"；可以用 `share = num7 / cells.Count() * 1.5f` 补偿。

### 6.3 P2 —— 吸气扇抽气

`Building_IntakeFan.TickRare()` 目前只算 `CurrentAirFlow`，不改变房间气压。AO 下应该：

```csharp
if (CompAirProducer.IsActive())
{
    float m3 = CompAirProducer.CurrentAirFlow * 0.01f;   // 经验系数
    float dPa = -m3 * 1000f / 250f;                      // 每 250 tick 抽走的 Pa
    AtmosphereApi.AddPressureDelta(map, Position, dPa / 1000f);
}
```

风险：AO 的求解器会把压差扩散回房间，实际抽气效率远低于预期；需要实测调系数。建议放在最后。

### 6.4 制冷排热：不需要处理

`Building_AirThermal` 的 `GenTemperature.PushHeat(val2, Map, num2 * 1.25f)` 已被 AO 的 `GenTemperaturePushHeatPatch` 翻译成 `AtmosphereMapComponent.PushHeat`。**保持原样即可**，只需在验收清单里确认"制冷时南侧格温度上升"。

---

## 7. 共通底座改造

### 7.1 `AtmosphereApi` 扩展

```csharp
// 已有：TryGetTemperature/TryGetPressurePascals/TryGetOxygenPercent/
//       TryGetRelativeHumidity/TryGetWind/AddTemperatureDelta/AddMoistureDelta/
//       AddMistDelta/AddOxygenPercentDelta/AddPressureDelta/AddWindImpulse/SetAmbientWind
// 新增：
bool TryGetAbsoluteVapor(Map map, IntVec3 cell, out float vapor);
bool TryGetMist(Map map, IntVec3 cell, out float mist);
bool TryGetGroundTemperature(Map map, IntVec3 cell, out float celsius);
bool AddDeltas(Map map, IntVec3 cell, AtmosphereDeltasProxy deltas);
bool AddVolumeAtTemperature(Map map, IntVec3 cell, float volumes, float oxygenPercent, float celsius);
bool AddGas(Map map, AtmosphereGasDefProxy def, IntVec3 cell, float amount);
bool TryGetGas(Map map, AtmosphereGasDefProxy def, IntVec3 cell, out float density);
```

### 7.2 `AtmosphereDeltas` 反射封装

`AtmosphereDeltas` 是 struct，字段全是 public。封装成：

```csharp
public sealed class AtmosphereDeltasProxy
{
    private static readonly Type Type = ReflectionCache.Type("AtmosphereOverhaul.RimWorld.AtmosphereDeltas");
    private static readonly FieldInfo FTemperature = ...;
    // ...
    public object Boxed { get; }   // 反射调用时传入
    public float Temperature { set => FTemperature.SetValue(Boxed, value); }
    // ...
}
```

字段 `FieldInfo` 在静态构造里缓存一次；每次 `StabilizeCell` 只做 `SetValue`，比 `GetField` 便宜一个数量级。

### 7.3 `ForceFieldStabilizer`

一个 `MapComponent`，职责：

- 维护区域列表（生物圈 + 穹顶）；
- 游标轮转；
- 预算控制；
- 统计（本 tick 处理格数、耗时、跳过原因）；
- 总开关（`AOPatchSettings.ForceFieldEnabled`）。

### 7.4 设置项

| 键 | 默认 | 说明 |
|---|---|---|
| `EnableForceField` | true | 总开关 |
| `ForceFieldInterval` | 0 = 跟随 AO 求解器 | 0 时读 `SimulationIntervalTicks` |
| `ForceFieldMaxCellsPerTick` | 500 | 每步预算 |
| `ForceFieldHumidity` | false | 是否强制 50% RH |
| `ForceFieldKillWind` | true | 区域内风归零 |
| `EnableCccVacuumBridge` | true | CCC 真空判定改 AO 气压 |
| `EnableCccLocalizedVent` | false | 风口局部化（P1，默认关） |

---

## 8. 性能预算

单格 7 次反射（6 读 + 1 写），按每次 2 μs 估算：

| 每步格数 | 反射次数 | 估算耗时 |
|---|---|---|
| 200 | 1400 | ~3 ms |
| 500 | 3500 | ~7 ms |
| 2000 | 14000 | ~28 ms |

结论：

- 默认 500 格/步，且只在 AO 地图上跑；
- 若区域 > 2000 格，自动把有效频率降到每 5 步一次（等价 30 tick 一轮）；
- 提供 `VerboseLogging` 输出「上一轮耗时 / 处理格数」，便于玩家反馈。

**实测方法**：同一存档，关闭/开启 `EnableForceField`，各跑 10 秒，看 Dubs Performance Analyzer 或 Unity Profiler 里 `ForceFieldStabilizer.MapComponentTick` 的占比。

---

## 9. 风险与规避

| 风险 | 说明 | 规避 |
|---|---|---|
| 求解器过冲 | `AddDeltas` 在一步内累加；每 tick 调用会叠 6 次 | 只按 `SimulationIntervalTicks` 节奏调用 |
| 大 delta 不稳定 | 一步补 1000 Pa 可能触发 AO 的 `PatchSafeDivergence` | 限幅：温度 ±20 K、气压 ±0.3 solver、氧 ±5 点 |
| 密封房间升压 | `AddVolumeSource` 持续注入 | 用 `AddDeltas` + `AddPressureDelta`，不用体积源 |
| 与 AO `temporalPatchManager` 交互 | 大量 impulse 会被记录为 patch，可能增加子步成本 | 控制频率与格数；观察日志 |
| 与超维读值补丁叠加 | 读值被改两次 | 无害；不要 Unpatch 别人 |
| CCC 更新漂移 | 方法签名变化导致反射失败 | 找不到就 `Skipped` + 日志，不崩溃 |
| 3000 格限制 | `Room.Temperature` 路径静默失效 | 走 `AddDeltas` 就绕开了 |
| 非 AO 地图 | 所有 `Add*` 返回 false | 全部 `if (!Add...)` 保护；非 AO 时 `ForceFieldStabilizer` 直接不注册 |

---

## 10. 实施顺序

1. **P0-a**：修 CCC 模块的状态显示（`NotInstalled` → `Applied`/`Skipped` 的真实原因），把「占位」改成「已检测、待接管」。
2. **P0-b**：`AtmosphereApi` 补 `TryGetAbsoluteVapor`/`TryGetMist`/`AddDeltas`/`AtmosphereDeltasProxy`。
3. **P0-c**：`ForceFieldStabilizer` + 生物圈区域（先只做温度/气压/氧）。
4. **P0-d**：穹顶区域（`DomeRect` + `IsDomeCell`）。
5. **P1-a**：CCC 真空判定改 AO 气压（Prefix 接管 `Building_AirVent.TickRare`）。
6. **P1-b**：CCC 风口局部化（默认关闭，作为可选项）。
7. **P2**：CCC 吸气扇抽气、AO 自定义气体清除、湿度强制。

每步验收：`dotnet build` 0 警告；进游戏用开发者模式开 AO 覆盖层，肉眼确认「区域内温度/气压/氧气被拉回目标」；日志里 `[AO Patch]` 无异常。

---

## 附录：证据位置

- `Source/Verse/ModContentPack.cs:232` `InitLoadFolders()` 的版本回退。
- AO `AtmosphereOverhaul.RimWorld.AtmosphereInterface`：`AddDeltas`/`AddPressureDelta`/`AddVolumeSource`。
- AO `AtmosphereOverhaul.RimWorld.AtmosphereColorUtility.PressurePascalsFromSolver` / `CurrentSolverPressureToPascals`。
- AO `AtmosphereOverhaul.RimWorld.AtmosphereMapComponent.EnqueueExternalDeltas` / `PushHeat`。
- AO `AtmosphereOverhaul.Patches.GenTemperaturePushHeatPatch`、`Room_PushHeat_EnginePatch`、`RoomTemperatureWriteHook`。
- CCC `CentralizedClimateControl.Building_AirVent.TickRare()`、`Building_AirThermal.TickRare()`、`Building_IntakeFan.TickRare()`、`CompAirFlowTempControl`。
- 超维 `Source/FullyAutomaticOmniCrafter/CompBiosphere.cs:398-400`、`Patch_CompBiosphere.cs:472`、`OmniForceFieldDome.cs:1107-1147/1348-1387/691-820`。
