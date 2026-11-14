# AO 补丁侧性能审查（2026-11）

> 审查对象：`JeremieAtmosphereOverhaulPatch` 自身全部 32 个 `.cs`（`Source/**`，排除 `obj/`），以及它们调用的 AO / 原版 / 第三方入口。
> AO 底账见 `AO-模块功能与性能分析.md`；本文只补它没有串起来的一层：**补丁调用点 × 触发频率 × AO 内部放大因子**。
> 声明：未做 profiler。所有成本以“可数的遍历/反射/分配次数”给出，`N = map.Size.x * map.Size.z`（250×250 = 62 500）。反射单次耗时按经验值估算，仅用于排序，不承诺毫秒。

---

## 0. 审查范围与方法

- 源码：`Source/` 下 32 个文件，逐文件通读；重点文件为
  `ProxyHeatRelayMapComponent.cs`(1634 行)、`AirLoopMapComponent.cs`(1252 行)、
  `GcAirRelay.cs`(855 行)、`AtmosphereFieldBridge.cs`(675 行)、`AoBridge.cs`(612 行)、
  `AcNetLedger.cs`(554 行)。
- AO 反编译：`f:/RimworldCodeIL/AtmosphereOverhaul/`
  `RimWorld/AtmosphereMapComponent.cs`、`RimWorld/AtmosphereInterface.cs`、
  `Simulation/LocalAtmosphereEngine.cs`、`RimWorld/AtmosphereSourceRates.cs`、
  `Buildings/2VanillaFacilities.cs`。
- 原版反编译：`f:/RimworldCodeIL/Assembly-CSharp/`
  `Verse/ThingWithComps.cs`、`Verse/GenRadial.cs`、`Verse/GenTemperature.cs`。
- 第三方：`f:/RimworldCodeIL/VanillaTemperatureExpanded/Comps/CompCeilingFan.cs`。
- 搜索：`rg -n 'HarmonyPatch|\.Patch\(|PatchAll' Source` 确认补丁全集；
  `rg 'TryGetComp|GetComp<'` 核对原版容器实现；
  `rg 'AddVolumeSource|RegisterAirMover|AirMoverHasReturnPath'` 核对 AO 被调点。

---

## 1. 四层调用频率（先定分母）

| 层 | 触发 | 补丁侧主要入口 |
|---|---|---|
| 每 tick | `ThingWithComps.Tick` / `CompTick` | `CeilingFanPatch.Prefix`（`Compat.VanillaTemperatureExpanded/CeilingFanPatch.cs:23`） |
| 每模拟步 | `PrepareTemperatureSources` Postfix / `CompleteFinishedSimulation` Postfix（`AtmosphereMapComponent.cs:1203`、`:3071`） | `ProxyHeatRelayMapComponent.Inject`、`GcAirRelay.RelayTemperature`、`AcNetLedger.Run`、`AirLoopMapComponent.StepAll` |
| 每 60 tick | GC `GC_IE_ventIntensiveExchange` Prefix | `GcAirRelay.Scan`（`GcPatches.cs:85-93`） |
| 每 250 tick | `TickRare` / `RunTempControl` | `CompTempOutputShape.Apply` |
| 事件 | `RecalculateAffectedCells` / `SpawnSetup` / `GetGizmos` | `RefreshRing` / `RegisterSource` / gizmo 过滤 |

`PrepareTemperatureSources` 只在 `flag`（`IsHashIntervalTick(map, SimulationIntervalTicks)`）或 pending/爆炸请求时调用
（`AtmosphereMapComponent.cs:1182,1198-1207`），所以“每模拟步”实际间隔 = `SimulationIntervalTicks`。

---

## 2. P0：复杂度高于 O(n log n) 的逻辑

### 2.1 AirMover 每步全量重注册 × AO 线性查找 = O(A²)（最严重）

- 补丁侧：`ProxyHeatRelayMapComponent.InjectJets` 每源、每 JetPort、**每模拟步**都调
  `AirBridge.RegisterAirMover(...)`（`ProxyHeat/ProxyHeatRelayMapComponent.cs:1372-1375`）。
  `Jets` 上限 24（`ProxyHeatSourceState.cs:46` 注释与 `BuildJetPorts` 分桶上限）。
- AO 侧：`RegisterAirMover` 对 `airMovers` 做 **for 线性查 (Owner, Key)**
  （`RimWorld/AtmosphereMapComponent.cs:193-203`）；命中后可能再进 `UpdateAirMover`（`:219-239`，又一次线性）；
  未命中才 `Add`（`:215`）。
- 组合：设地图上 AirMover 总数 `A`。每模拟步注册动作数 ≈ `A`（每个端口一次），每次平均扫 `A/2`，
  合计 **O(A²/2)** 次比较/模拟步。`A ≈ 24 × Jet 源数 + GC 通风口数 + 其他`。
- 量级：20 个 ProxyHeat 源 → A≈480 → 约 11.5 万次比较/步；40 源 → A≈960 → 约 46 万次/步。
  （作为对照：本项目给每粗步定的 2N 红线 = 12.5 万“格操作”。）
- 注意：这与 `AO-模块功能与性能分析.md` §5.3 的 `O(mover 数)` 单次成本不矛盾；问题是**每步全量重注册**
  把它变成了平方。稳态完全没必要重新注册：`GetActive`/`GetModes` 不变时，端口集合也不变。

### 2.2 CCC `CountPipeCells`：O(nets × pipes)

- `AirLoopMapComponent.StepAll` 对每个 net **无条件**依次 `ComputeKey` → `RefreshLoopShape` → `StepLoop`
  （`Compat.CentralizedClimateControl/AirLoopMapComponent.cs:207-233`，`RefreshLoopShape` 在 `:231`）。
- `RefreshLoopShape` 每次调 `CountPipeCells(net, pipes)`，后者**全量扫 `List<CompAirFlow> pipes`** 并按 `GridID` 过滤
  （`:274-301`，调用点 `:421-471`）。
- 复杂度：`net` 数 M、管道数 P ⇒ **每模拟步 O(M×P)**；M 与 P 同阶时 O(n²)。
  即使拓扑完全没变，每步都重算。
- 补充：`ComputeKey` 也是 O(项目数)（`:303-338`），`net.Producers/Consumers/TempControls` 的遍历另算；
  但平方项来自 `CountPipeCells`。

### 2.3 ProxyHeat `_indoorOn.Contains`：O(S²)

- `Inject` 的源循环内，每个源都执行 `bool indoors = _indoorOn.Contains(parent)`
  （`ProxyHeat/ProxyHeatRelayMapComponent.cs:1023`）。`_indoorOn` 是 `List<Thing>`。
- `ApplyDefaultIndoors`（`:465-477`）在 `DefaultIndoorEffect=true` 时把**每个新源都加入** `_indoorOn`（`:473-475`）。
  默认设置若为开，则 `_indoorOn.Count ≈ 源数 S`。
- 复杂度：**O(S²)**/模拟步。S=20 时 400 次比较可忽略；S=200 时 4 万次，开始与源遍历本身同量级。
- 同时 `RegisterSource` 的 `_knownSources.Contains` 是 O(S)（`:752-769`），但只在 `PostSpawnSetup` 时触发，不是每步。

### 2.4 AO `UnregisterAirMovers(owner)`：O(A²)

- `RimWorld/AtmosphereMapComponent.cs:255-267`：外层逆序 `for`，内层调用 `UnregisterAirMover`（`:241-253`，再一次线性 + `RemoveAt` 位移）。
- 触发点：`ProxyHeat.StopJets`（`ProxyHeatRelayMapComponent.cs:1400-1414`）在喷流开关切换时调用。
  低频，但一次切换的代价是 O(A²)；A 大时会有一次可见卡顿。

---

## 3. P1：每模拟步内的 O(n) 以上逻辑 / 超预算

### 3.1 ProxyHeat `Inject` 每格 8 次累加（可能破 2N 红线）

- 第一遍逐源遍历 `state.Cells`（ProxyHeat 的 `AffectedCells`），每格调 `Accumulate`
  （`ProxyHeatRelayMapComponent.cs:969-1054`，`Accumulate:1103-1155`）。
- `Accumulate` 每格写 **8 个数组**（`_sumAir/_maxAir/_baselineAir/_capAir/_floorAir/_coolAir/_strengthAir/_touchedAir`）
  + `_touchedAir.Add`，soil 一套同构（`:1103-1155`）。
- 量级：半径 8 约 201 格/源；20 源 ≈ 4 020 格 × 8 ≈ **3.2 万写/步**（占 2N 的 26%）。
  半径 20 约 1 256 格/源 × 20 源 ≈ 2.5 万格 × 8 ≈ **20 万写/步**，**超 2N 红线 1.6 倍**。
- 这是“合法但可能超预算”的实现；优化方向是合并数组（如把 max/baseline 只对 touched 记录）或降低每格写次数，而不是改算法。

### 3.2 CCC `ApplyWind`：每出风口 BFS + 每格 `SetSlot`

- `ApplyWind`（`AirLoopMapComponent.cs:923-1063`）：
  - `CollectSpreadCells` 做 BFS，上限 `MaxSpreadCells=40`（`:1108-1190`，常量 `:60`）；
  - 对 `spreadCells` 每格：`cells.IndexOf(cell)`（`:1035`，线性）× 构造 `SlotRates` × `AoBridge.SetSlot`（`:1057`）；
  - `SetSlot` 每次 = `Activator.CreateInstance` + 10 次 `FieldInfo.SetValue`（含 2 个 `Vector2` 装箱）+ `Invoke`
    （`Bridge/AoBridge.cs:509-517`、`NewRates:580-596`）+ AO `UpdateContinuousSource` 的 8 次 `Enqueue*`
    （`AtmosphereMapComponent.cs:2622-2649`）。
- 量级：出风口 V=10 时 400 格/步 × 约 4 μs ≈ **1.6 ms/步** 的反射+入队；`IndexOf` 额外 O(40×格数)。
- 稳态 `SourceSlots.Set` 有句柄缓存（`Buildings/2VanillaFacilities.cs:21-32`），AO 侧是 O(1) 字典，**不是**每格重注册；
  成本主要来自反射与 `Enqueue*`。

### 3.3 AcNet：每室内机每步 `GetSlots` 线性扫 `AllComps`

- `AcNetLedger.Run` 第 5 步每台室内机调 `AoBridge.GetSlots(entry.Unit)`（`Compat.VanillaTemperatureExpanded/AcNet/AcNetLedger.cs:299`），
  失败/零输出走 `ZeroFrom`（`:308`），正常走 `SetSlot`（`:319`）。
- `AoBridge.GetSlots` 是 **for 扫 `parent.AllComps` 找 `CompTempControlAtmosphere`**
  （`Bridge/AoBridge.cs:477-506`），没有字典/缓存。
- 复杂度：**O(units × comps)**/模拟步。units=20、comps=30 时 600 次；units=100 时 3 000 次。
  与 3.4 的反射相比，这个线性扫不是最大头，但可以按 parent 缓存 `slots` 引用。

### 3.4 GC `RelayTemperature`：每通风口每步 4 次反射

- `GcAirRelay.RelayTemperature`（`Compat.GoodClimate/GcAirRelay.cs:698-745`）每模拟步遍历 `Registered`，
  每台 2 次 `AoBridge.TryGetTemperature` + 2 次 `AddTemperatureDelta`（均反射 + `object[]`）。
- `TryGetTemperature` 内部还要 `FindAtmosphereComponent` **线性扫 `map.components`**
  （`Bridge/AoBridge.cs:290-312,440-459`）。
- 量级：vents=20 → 80 次反射 + 80 次 components 扫描/步。可接受，但 `FindAtmosphereComponent` 可缓存。

### 3.5 CompCeilingFan：每 tick 3 次 `GetComp` + 每 15 tick 百格 `SetSlot`

- `CompCeilingFan.CompTick` 原本**每 tick** 被调（`VanillaTemperatureExpanded/Comps/CompCeilingFan.cs:21`，原版只在
  `IsHashIntervalTick(parent,60)` 时才 `PushHeat`，`:26-27`）。补丁 `CeilingFanPatch.Prefix` 每 tick 先执行
  （`Compat.VanillaTemperatureExpanded/CeilingFanPatch.cs:23-43`）：
  - `parent.GetComp<CompCeilingFanAtmosphere>()`（`:30`）
  - `comp.Tick` 内 `parent.GetComp<CompPowerTrader>()`（`CompCeilingFanAtmosphere.cs:95`）
  - `Spin` 内再 `fan.parent.GetComp<CompPowerTrader>()`（`CeilingFanPatch.cs:48`）
  这 3 次是**原版 `GetComp<T>`**，有 `compsByType` 字典（`Assembly-CSharp/Verse/ThingWithComps.cs:105-130`），**O(1)**，不是线性扫。
- 真正的热点是 `Apply`：每 `applyIntervalTicks=15` 一次（`CompCeilingFanAtmosphere.cs:122-126`），
  对 `_cells`（半径 6 约 113 格，`List` 容量 128，`:51`）**每格一次 `SetSlot`**（`:143-154`），
  再 `ZeroFrom`（`:156`）。即每 15 tick 约 113 次“Activator + 10 SetValue + Invoke”。
  5 个吊扇 = 565 次/15 tick ≈ **37 次反射/tick**，突发时（同 tick 对齐）可达 565 次连续反射。
- `RebuildCells` 每 60 tick：`GenRadial.RadialCellsAround` 是 **yield iterator**（`Assembly-CSharp/Verse/GenRadial.cs:186-194`），
  每次分配迭代器状态机；每格 `Impassable` + `GetRoom`（`CompCeilingFanAtmosphere.cs:172-192`）。

### 3.6 AO `MixReleasedGasCore` 的 BFS 扩散

- CCC 出风口注入走 `AddVolumeSourceAtTemperature`（`AirLoopMapComponent.cs:618+`），
  AO 侧 `MixReleasedGasCore` 从注入格开始沿 `FaceOpen` **BFS 扩散**，逐格消耗 `min(1, remaining)` 体积
  （`RimWorld/AtmosphereMapComponent.cs:307-376`）。
- 复杂度 O(扩散格数)，最坏 O(N)；体积越大扩散越远。每格还 `lock (incomingDeltasLock)`（`:355-364`）。
- CCC 每 vent 格每步注入一次，所以成本 ≈ `vent 格数 × BFS 覆盖格数`。冷风体积大时需关注。

---

## 4. P1：频繁分配（每模拟步 / 每 tick）

| 位置 | 分配物 | 频率 |
|---|---|---|
| `AtmosphereMapComponent.PrepareAirMovers` `:441` | `pendingAirMovers.ToArray()` 新数组，长度 O(A) | 每模拟步 |
| `ApplyAirMoversUnsafe` `:476,494,495` + `:477` | `new Dictionary<int,float>`、`new Dictionary<int,OutletMixture>`、`new Dictionary<int,Vector2>`、`new float[len]` | 每模拟步 |
| `ProxyHeat.InjectJets` `:1372-1375` | `"aopjet:" + id + ":" + d` 字符串，每端口每步 1 个 | 每模拟步 |
| `AirLoopMapComponent.TrySampleIntake` `:792-830` | `GenAdj.CellsAdjacent8Way`（IEnumerable）+ 每格反射 | 每模拟步 × 每 producer |
| `CompCeilingFanAtmosphere.RebuildCells` `:172` | `GenRadial.RadialCellsAround` yield 迭代器 | 每 60 tick |
| `ProxyHeat.EnumeratePerimeter` `:720-742` | yield 迭代器 | 每次 `RefreshRing`（拓扑变化） |
| `AirLoopMapComponent.StepAll` `:241-248` | `Log.Message(string.Concat(...))` 长字符串 | 每 60 次 StepAll |
| `AcNetLedger.Run` `:416-428` | `string.Format` + 诊断字符串 | 每 2000 tick |

无分配/已复用（已核对，不必改）：`AcNetLedger._entries/_compressors` 是 `List` + struct（`:51-52`）；
CCC 的 `windSlots/liveWindOwners/spreadCells` 等 scratch 复用（`:104-123`）；
`SourceRates`/`SlotRates`/`JetPort` 均为 struct。

---

## 5. P2：反射热点（常数大，非超线性）

已缓存的：`ProxyHeatBridge.Resolve`（一次性 `:79-140`）、`AoBridge.Resolve`（`:143-257`）、
`GcAirRelay` 静态构造（`:87-128`）、`AtmosphereFieldBridge` 的 `_registerSource` 等一次性解析。
**未缓存、每次调用都走反射/装箱**的主要有：

| 接口 | 每次成本 | 主要调用点 |
|---|---|---|
| `AtmosphereFieldBridge.RegisterAirMover` | `Activator` + 12×`SetValue` + `object[]` + `Invoke` + `IsValid.GetValue`（`:607-648`） | `ProxyHeat.InjectJets:1372`、`GcAirRelay:383/402` |
| `AtmosphereFieldBridge.AddTemperatureDelta` | `Invoke` + `object[]`（`:544-563`） | `GcAirRelay.RelayTemperature:698-745` |
| `AoBridge.TryGetTemperature` | `GetValue` + 封装（`:267-283`）+ `FindAtmosphereComponent` 线性（`:440-459`） | `AcNetLedger:177,341`、`GcAirRelay:581-600`、`CompTempOutputShape:174,231`、`AirLoopMapComponent.TrySampleIntake` |
| `AoBridge.SetSlot` | `NewRates`（Activator+10 SetValue）+ `Invoke`（`:509-517,580-596`） | `AcNetLedger:319`、`AirLoopMapComponent:1057`、`CompCeilingFanAtmosphere:153`、`CompTempOutputShape:191/286/293` |
| `AoBridge.GetSlots` | for 扫 `AllComps`（`:477-506`） | `AcNetLedger:299`、`CompTempOutputShape:100` |
| `AoBridge.AddVolumeSource(AtTemperature)` | `Invoke` + `object[]`（`:344-379`） | `AirLoopMapComponent:609`、`:618+` |
| `ProxyHeatBridge.GetActive/GetProps/GetMax...` | `GetValue`（`ProxyHeatBridge.cs:231-283`） | `ProxyHeat.RefreshActive/RefreshOutcome`（每源每步约 6~8 次） |

---

## 6. 已核实“不是问题”的点（避免误报）

1. **`ThingWithComps.GetComp<T>` 不是线性扫**：`count<3` 直接比较；否则走 `compsByType` 字典
   `TryGetValue(typeof(T))`，仅未命中且非 sealed 才回退线性（`Assembly-CSharp/Verse/ThingWithComps.cs:105-130`）。
   `compsByType` 在 `InitializeComps` 时用 `GroupBy` 建一次（`:157-178`）。因此 `CeilingFanPatch` 的 3 次 `GetComp` 是 O(1)。
2. **`SourceSlots.Set` 稳态不是 O(源数)**：它缓存 `AtmosphereSourceHandle`，命中后走 `UpdateContinuousSource`
   （`AtmosphereOverhaul/Buildings/2VanillaFacilities.cs:21-32`），AO 侧 `TryGetValue(sourceId)` 是 O(1)
   （`AtmosphereMapComponent.cs:2453-2482`）。只有“首次注册”才落到 `RegisterContinuousSource` 的
   线性 `foreach (continuousSources.Values)`（`:2409-2451`，`:2422`）。
3. **`AirMoverHasReturnPath` 不是每 mover O(N)**：只有 `airMoverConnectivityDirty` 时做一次全图 flood fill
   + 两次全图扫描，之后 O(1) 查表（`Simulation/LocalAtmosphereEngine.cs:627-762`）。dirty 由 mask 变化置位。
4. **`GenTemperature.ControlTemperatureTempChange` 是 O(1)**：只取 `room`、`room.CellCount`、`room.Temperature`
   （`Assembly-CSharp/Verse/GenTemperature.cs:315-327`），CCC `ApplyWind:950` 调用不构成扫描。
5. **`EnumeratePerimeter`/`TryFindOutlet`/`RefreshRing` 只在拓扑变化时重建**（`ProxyHeatRelayPatches.cs:107-124` 的
   `RecalculateAffectedCells` Postfix），不是每步。
6. **`GcVentSpawnPatch` 已按 def 预过滤**（`GcPatches.cs:154-166` 的 `def.HasComp(AirVentType)`），不会对每块墙做 AllComps。

---

## 7. 按严重度排序的修复方向（本次不改码）

1. **ProxyHeat 喷流改为“状态不变不重注册”**：把 `Jets` 的注册句柄缓存住（`JetPort → handle`），
   仅在端口集合/方向/`Settings` 变化时 `RegisterAirMover`，其余步只更新参数或干脆不调。
   这可把 2.1 的 O(A²) 降到 O(A)（甚至 O(变化数)）。同时消掉每步的 `"aopjet:"+...` 字符串。
2. **CCC `CountPipeCells` 增量化**：`RefreshLoopShape` 只在 `AirFlowNet` 的 `GridID`/成员变化时重算；
   或按 net 缓存 `pipeCount`，用管道增删事件（`PipeSystem` 的 dirty 标志）失效。
   可把 2.2 的 O(M×P) 降到 O(变化数)。
3. **ProxyHeat `_indoorOn` 改用 `HashSet<Thing>`**：把 2.3 的 O(S²) 降到 O(S)。
4. **反射批量接口替换**：为 `RegisterAirMover`/`SetSlot`/`AddTemperatureDelta`/`TryGetTemperature`
   缓存 `Delegate`（`CreateDelegate` 绑定到具体字段 Setter/方法）或改用 `AtmosphereInterface` 的公开方法；
   避免 `Activator.CreateInstance` 与 `object[]`。
5. **`GetSlots` 按 parent 缓存 slots 引用**（`Dictionary<Thing, object>`，`PostDeSpawn` 清理），消掉 3.3 的线性扫。
6. **吊扇 `Apply` 降低反射密度**：`_cells` 与 `SlotRates` 可预生成（每格一个），只在温度变化时改 `TemperaturePerTick`；
   或把 `applyIntervalTicks` 从 15 提到 30（收益线性）。
7. **ProxyHeat `Accumulate` 合并数组**（3.1）：把 `_baselineAir/_capAir/_floorAir` 改为“只在 touched 上算”，
   或把 8 个数组压成 3 个结构体数组，控制在大半径源下的 2N 预算内。
8. **AO 每步 4 个集合**（4 节）属 AO 自身；补丁侧能做的是**减少注册的 mover/源数量**（与 1、2 联动）。

---

## 8. 量化对照（250×250，N=62 500，2N=125 000）

| 场景 | 每模拟步“等效遍历/反射” | 说明 |
|---|---|---|
| ProxyHeat 20 源、半径 8、默认 indoors | 3.2 万写 + 约 400 次 Contains + 约 160 次反射 | 写占 2N 的 26% |
| 同上 + 喷流 480 个 AirMover | + 约 11.5 万次 `RegisterAirMover` 比较 | **接近/超过 2N** |
| ProxyHeat 40 源、半径 20 | 20 万写 + 960 mover 的 46 万次比较 | **显著超 2N** |
| CCC M=10、P=300、V=10 | 3 000 次 `CountPipeCells` + 400 次 `SetSlot` 反射 + BFS | `SetSlot` 反射是主常数 |
| AcNet units=20 | 约 100 次反射 + 600 次 `GetSlots` 扫 comps | |
| GC vents=20 | 80 次反射 + 80 次 components 扫 + 每 60 tick Scan | |
| 吊扇 5 台 | 每 15 tick 565 次 `SetSlot` 反射 ≈ 37 次/tick | 突发 565 次 |

> 结论：**真正构成“复杂度 > O(n log n)”的是 2.1（AirMover O(A²)）、2.2（CCC O(M×P)）、2.3（O(S²)）、2.4（O(A²) 低频）**。
> 其余是“热路径上的常数过大/分配过多”，量级上更值得优先处理的是 2.1、3.5、3.2 的反射与每步全量重注册。

## 9. 修复落地记录（2026-11-14）

用户确认后按 §7 落地，`dotnet build -c Debug` 通过（0 警告 / 0 错误）。

| 项 | 文件 | 改法 | 行为保持 |
|---|---|---|---|
| 2.1 喷流重注册 | `ProxyHeat/ProxyHeatSourceState.cs`、`ProxyHeat/ProxyHeatRelayMapComponent.cs` | 新增 `JetSignature`（每端口 4 个 float：speed、jetFloor、inletCell、outletCell）；签名相同且 `JetRegistered` 时跳过 `RegisterAirMover`；`RefreshRing` 与 `StopJets` 置 null | AO `PrepareAirMovers:430-444` 只在 owner 失效时移除 mover，缓存句柄安全；端口/速度/出气温度量化台阶变化都会重注册 |
| 2.2 CCC 管格数 | `Compat.CentralizedClimateControl/AirLoopMapComponent.cs` | `pipeCellCache` 按 `GridID` 缓存；`ReadNets` 在 `IsDirty` 时清，`ReadPipes` 在列表引用/数量变化时清 | `RefreshLoopShape` 每步仍重算 volume/friction，只把 `CountPipeCells` 降为 O(1) |
| 2.3 室内开关 | `ProxyHeat/ProxyHeatRelayMapComponent.cs` | 新增 `_indoorOnSet`，List（Scribe）与 Set 双写；读档 `RebuildIndoorCache` | 存档格式不变 |
| 2.4 / 3.5 反射 | `Bridge/AoBridge.cs`、`Bridge/AtmosphereFieldBridge.cs` | `CreateDelegate` 缓存 `TryGetTemperature/Oxygen/Vapor/AddVolume/AddMoisture/AddTemperatureDelta/Unregister*`；表达式树写 `AtmosphereSourceRates` 10 字段（替代 10 次 `SetValue`）；`Expression.New` 生成装箱工厂；参数数组 ThreadStatic 复用 | 失败全部回退原 `Invoke`/`SetValue`；`SetSlot` 因 `SourceSlots` 是 AO 内部类型，跨程序集表达式树不可见，仍走 `Invoke`（参数数组已复用） |
| 3.4 GetSlots | `Bridge/AoBridge.cs` | `ConditionalWeakTable<Thing,object>` 缓存非 null slots | `slots` 是 `CompTempControlAtmosphere` 的 `readonly` 字段（`Buildings/4VanillaFacilities.cs:20`），不会替换 |
| 3.6 吊扇 | `Compat.VanillaTemperatureExpanded/CompCeilingFanAtmosphere.cs` | `applyIntervalTicks` 15→30；`_rateCache` 按 `count`+`perTick` 缓存，`RebuildCells` 置 null | AO `UpdateContinuousSource` 先 `FlushContinuousIntervalToQueues` 再重置 `LastAppliedTick`，拉长间隔不丢积分 |
