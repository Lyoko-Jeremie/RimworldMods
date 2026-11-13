# AO 模块功能与性能分析（RimWorld 1.7 运行时）

> 分析对象：Atmosphere Overhaul（workshop `3830470974`）在 **1.7** 分支的实际运行形态。
> 反编译源码根：`f:/RimworldCodeIL/AtmosphereOverhaul/`（主 dll，MVID `4E9F1EAE…`）。本文所有 `文件:行` 均以该根为基准，省略 `f:/RimworldCodeIL/AtmosphereOverhaul/` 前缀。
> 分析目的：为后续新功能开发（ProxyHeat 接管、CCC/VTE 兼容、主动气体转移等）建立"性能底账"，回答三个问题：**AO 哪些模块真在跑、每 tick 花在哪里、我的 patch 该往哪插**。
>
> **关于精度的声明**：本文**不承诺毫秒数**（未做采样 profiler）。全部成本以"可数的遍历次数"给出：`N = engine.Size = map.Size.x * map.Size.z`（250×250 图 N = 62 500），`A = 时间补丁域活跃格数`。这些次数是从源码逐行数出来的，可用于**相对比较**（A 模块比 B 模块贵几倍），不可直接换算为耗时。

---

## 0. 结论摘要

### 0.1 十二条核心结论

| # | 结论 | 证据 |
|---|---|---|
| 1 | **1.7 上 Burst 原生内核全部失效**。`BurstBackend.Initialize` 无条件 `return false`，`Status="RimWorld 1.7: managed solver"`；21 个 `TryBurst*` 分派点必然回退到 C# `Parallel.For`。`1.6/Native/win-x64/AtmosphereOverhaul.Burst.dll` 存在但永不加载。 | `Simulation/Burst/BurstBackend.cs:31-41`、`AtmosphereOverhaulMod.cs:32-35` |
| 2 | **1.7 上世界气候引擎走 WebGPU(V2)**，不是 Burst。`1.7/Assemblies/AtmosphereOverhaul.Perf81.dll`（MVID `5cc7327e…`，170 类型）是 `AtmosphereOverhaul.Simulation.Global.V2(.WebGpu)` 的原生加速实现；主 dll 的 `GpuAutodriveGlobalClimateEngine` 通过 `AutodriveGpuBackend.UseWebGpu` 加载 `1.6/Native/WebGpu/win-x64/wgpu_native.dll`。 | `Simulation/Global/GpuAutodriveGlobalClimateEngine.cs:60-72`、`AutodriveGpuBackend.UseWebGpu` |
| 3 | **世界引擎读的 native 目录写死 `1.6`**：`Path.Combine(modRoot, "1.6", "Native", "WebGpu")`。1.7 用户也走 1.6 的 dll。 | 同上 `:60-72` |
| 4 | **时间补丁域（TemporalPatch）在默认配置下是死代码**。`AtmosphereTuningManager.ScaleForInterval` 在 `CompressiblePressure > 0` 时**强制** `EnableTemporalPatches = 0`；可压是默认（XML `<CompressiblePressure>1</CompressiblePressure>`）。因此 `TickTemporalPatch` 每 tick 第一行就 return。 | `Simulation/AtmosphereTuningManager.cs:253-254`、`RimWorld/AtmosphereMapComponent.cs:2093`、`1.6/Defs/AtmosphereTuning.xml:249-254` |
| 5 | **每粗步真正的固定成本里有一笔"空扫"**：`DrainExternalIncomingDeltasUnsafe` 对 8 条外部增量缓冲各做一遍全 `size` 线性扫描，**没有任何脏标志/早退**；加上 `DrainIntoPending` 两遍与 `Array.Clear(residue)`，约 **11N 次遍历/粗步**，即使全图一个外部增量都没有。 | `RimWorld/AtmosphereMapComponent.cs:1945-2089`（无 dirty 门控）、`:2179-2193` |
| 6 | **投影（Project）是单步最大热点**，默认可压 packed SOR 路径 = `PressureIterations × N` + 约 `23N` 的其他子 pass。`PressureIterations` 不是常数 60，而是被 `AtmosphereTuningManager` 按 interval 重算（见 §4）。 | `Simulation/LocalAtmosphereEngine.cs:3678-3816`、`Simulation/AtmosphereTuningManager.cs:244-282` |
| 7 | **`simulationIntervalTicks` 的默认值 6 属于"最贵档位之一"**：interval=6 时每 tick 计算量约为 interval=30 的 **5.4 倍**（§4.2）。杠杆比任何代码级微优化都大。 | §4 |
| 8 | **风系统只有一个每 tick 全图扫描**：`WindLoadField.EnsureBuilt`（8×8 块聚合 + 3×3 膨胀，约 N 次乘加）。结构/家具/屋顶/搬运四套伤害系统全部是"成员列表 + 30 tick 节流"，不是每 tick 全图。 | `RimWorld/WindLoadField.cs:38-115`、四系统 Tick 头部门控 |
| 9 | **气体系统默认 4 种，全部 `VanillaGasGridBridge`**，数据本体在原版 `GasGrid` 的 packed uint 里，AO 只在批处理期间用临时缓冲。调度是"每 tick 最多派发 1 种气体 + 同一时刻仅 1 个后台 Task"，周期 = `max(interval, gasCount+1)`。 | `1.7/Defs/AtmosphereGasDefs.xml:4,35,62,92`、`Gases/AtmosphereGasManager.cs:574-594` |
| 10 | **渲染侧两个最大户**：地图风线最坏 **1200 次 `Graphics.DrawMesh`/帧**（`WindStreamlineRenderer`）；植物摆动每 60 tick 全图两遍 + `MapMeshDirty` 触发 section 重建。 | `RimWorld/WindStreamlineRenderer.cs:184`、`RimWorld/PlantWindSway.cs:261-286,601` |
| 11 | **本项目 patch 侧的最大单点成本**是 `InjectJets` 每端口"字符串 + `Activator.CreateInstance` + 12 次 `FieldInfo.SetValue` + `Invoke`"；其次是 `GetGizmos` Postfix 每渲染帧每选中物 6 个 Gizmo + 6 次 `Translate` + 4 个闭包。 | `Source/.../ProxyHeat/ProxyHeatRelayMapComponent.cs:1349-1378`、`ProxyHeatRelayPatches.cs:297-369` |
| 12 | **写 AO 的正确窗口只有一个**：`PrepareTemperatureSources` 的 Postfix（`pending*` 已 `Array.Clear`、已合并队列、已叠加连续源，但主任务尚未 `ApplyTemperatureDeltas`）。地温直写也在此窗口并**有效**——`ApplyGroundEnergy` 以 `groundT[i]` 作为积分初值。 | `RimWorld/AtmosphereMapComponent.cs:1788-1815`、`Simulation/LocalAtmosphereEngine.cs:3056-3057`（已逐行核实） |

### 0.2 每粗步性能预算（默认配置，可压 packed SOR）

设 `P = PressureIterations`（由 interval 决定：默认 interval=6 → **P=24**；interval=20/30 → **P=16**）。

| 阶段 | 遍历次数 | 说明 |
|---|---|---|
| `AdvanceProvisional` | ≈ 26N | 20+ 个全图 pass，含 4 次 `Mathf.Exp`/格的热力内核 |
| `ProjectAndFinalize` | ≈ P·N + 23N | P·N 是 SOR 扫描；23N 是清零/散度/梯度/暗热/边界等 |
| `ApplyGroundEnergy` | 1N | 每格 1 次地温积分 |
| `PublishSnapshot` | 9N | 8 个标量场 + 地温各一次 `Array.Copy` |
| `PrepareTemperatureSources` | ≈ 19N | 5×`Array.Clear`(5N) + 两遍全图(2N) + 入队排空(1N) + **外部增量空扫(11N)** |
| `Apply{Temperature,Moisture,Divergence}Deltas` | 3N | |
| **合计** | **≈ P·N + 81N** | interval=6 → **105N**；interval=30 → **97N** |

折算到"每 tick"（粗步每 `interval` tick 一次）：

| interval | P | 每粗步 | 每 tick | 相对 interval=6 |
|---|---|---|---|---|
| 3 | 24 | 105N | 35.0N | 2.0× |
| **6（默认）** | **24** | **105N** | **17.5N** | **1.00×** |
| 12 | 24 | 105N | 8.75N | 0.50× |
| 20 | 16 | 97N | 4.85N | 0.28× |
| 30 | 16 | 97N | 3.23N | **0.18×** |

> 250×250、interval=6 时每 tick 约 **1.1 M 次格访问**（其中约 1/3 落在 `Parallel.For` 多核并行段）。

### 0.3 性能开关杠杆（按收益排序）

| 杠杆 | 位置 | 收益 | 代价 |
|---|---|---|---|
| `simulationIntervalTicks` 6 → 20/30 | `AtmosphereOverhaulSettings.cs:16,24` | **省 72%~82%** 模拟计算量 | 大气演化变粗、瞬态响应变慢；≥20 时才会尝试激活补丁域（但默认被可压关闭） |
| 关 `WindTransportEnabled` / `WindCarriesItems` | `WindTransport.cs:90` | 省 30 tick 一次的 HaulableEver 全列表 + Pawn 全遍历 | 失去搬运风害玩法 |
| 关 `ShowStreamlineOverlay` | `AtmosphereMapComponent.cs:3116` | 省最坏 1200 draw call/帧 | 失去风线可视化 |
| `PlantSwayEnabled` 关 | `PlantWindSway.cs:114` | 省每 60 tick 两遍全图 + MeshDirty | 失去植物摆动 |
| `WindRouteEnabled`/`WindWalkEnabled` 关 | `WindPathCosts.cs:189-203` | 省 16 张 `NativeArray<ushort>` 刷新与寻路失效 | 风不再影响寻路 |
| `LocalAtmosphereEngine.UseEnclosureDeflation = false`（静态） | `LocalAtmosphereEngine.cs:122` | 省 ≈ 4N/步（Deflate）+ 拓扑重建 | 封闭房间压力收敛变慢 |
| `OpeningDischargeCoefficient = 0`（静态） | `:50` | 省 5N/步（PhaseChange 之外的 choke 复制）+ UpdateChoke | 失去开口流量系数物理 |
| `ScalarDiffusionIterations` 3 → 1 | XML `:32` | 省 4N/步 | 标量扩散变弱 |
| `GasDiffusionIterations` 1 → 0 | XML `:29` | 省 1N/步 + 首次 2×float[N] | 氧扩散消失 |

---

## 1. 架构总览

### 1.1 双引擎

```
                        ┌─────────────────────────────────────────┐
                        │ 世界层（行星 GCM）                        │
   weatherEngine==0 ───▶│ GpuAutodriveGlobalClimateEngine          │
   （默认）              │  ├─ V2 WebGPU (Perf81.dll + wgpu_native) │
                        │  └─ GlobalClimateTileCache / Snapshot    │
                        └───────────────┬─────────────────────────┘
                                        │ 每地图取样本（AmbientTemperature / GroundSkinExcess）
                        ┌───────────────▼─────────────────────────┐
                        │ 地图层（局部大气）                        │
                        │ AtmosphereMapComponent                   │
                        │  ├─ LocalAtmosphereEngine（~65 个 float[N] 场）│
                        │  ├─ 风系统（WindLoadField + 4 套伤害）    │
                        │  ├─ 气体系统（AtmosphereGasManager）      │
                        │  └─ 渲染（overlay / 风线 / 气体层）      │
                        └─────────────────────────────────────────┘
```

- 世界引擎选择：`Simulation/Global/WeatherEngineChoice.cs` → `settings.weatherEngine != 1 ? Simulator : VanillaDerivation`；默认 `0` → **Simulator**。`AtmosphereOverhaulMod.cs:30-31` 仅在 `UsesSimulator` 时 `GpuAutodriveGlobalClimateEngine.Initialize(content.RootDir)`。
- 静态持有：`Simulation/Global/3GlobalClimateEngine.cs:12`（`new GpuAutodriveGlobalClimateEngine()` 作为 `IGlobalClimateEngine`）。

### 1.2 1.7 上失效 / 空转的子系统

| 子系统 | 状态 | 证据 |
|---|---|---|
| Burst 原生内核（21 个 opcode） | **失效**：`Initialize` 恒 false，`TryBurst*` 恒 false | `Simulation/Burst/BurstBackend.cs:31-41` |
| 时间补丁域 TemporalPatch | **默认失效**：可压时被强制 `EnableTemporalPatches=0` | `Simulation/AtmosphereTuningManager.cs:253-254` |
| `ApplyOpenBoundarySponge` | **空函数**（方法体仅一对花括号） | `Simulation/LocalAtmosphereEngine.cs:4343-4345` |
| 全局子循环 `RunGlobalSubcyclingUnsafe` | 不可达（依赖 `UseGlobalSubcycling`，而它依赖补丁域 Active） | `RimWorld/AtmosphereMapComponent.cs:2977`、`TemporalPatchManager.cs:216` |
| `ManagedGasRenderer` | 空转（4 种气体都是 `VanillaGasLayer`，只画 `TintedQuads`） | `RimWorld/ManagedGasRenderer.cs:32-47` |
| `AtmosphereFieldBridge.MarkCachedMapDataDirty` | **死代码**（全项目零调用点） | `Source/JeremieAtmosphereOverhaulPatch/Bridge/AtmosphereFieldBridge.cs:439` |
| 常量 `CoarseSweeps=24`/`CoarseParallelCells=4096`/`PackStride=6` | 声明后**从未被引用**，实际用的是字面量 | `LocalAtmosphereEngine.cs:151,156,171` vs `:5056,5027,5372` |
| `BoundaryPressureRelaxation` / `BoundaryVelocityRelaxation` / `OpenBoundaryBandWidth` | 主引擎未使用（仅 `TemporalPatchDomain.cs:624` 用到后者之一） | — |

> **含义**：读 AO 源码时约有 1/4 的"高级子系统"在 1.7 默认配置下不会执行。做兼容 patch 时不必为这些路径设计，但要保证**不破坏它们的假设**（例如别把 `EnableTemporalPatches` 手动打开）。

### 1.3 线程模型与锁

| 锁 / 标志 | 保护对象 | 争用方 |
|---|---|---|
| `engineLock`（`AMC:59`，`readonly object`） | 整个引擎与主模拟任务 | 主线程 `RefreshRuntimeSettings`(1672)、`PrepareTemperatureSources`(1790)、`ApplyWaterMistPawnEffects`(3269)、`RebuildObstacleMask`(1261)、`TickTemporalPatch` 前置(2096)、`ExposeData`；后台主任务整段持锁(2973) |
| `incomingDeltasLock` | 8 条外部增量缓冲 + 爆炸残留 | 所有 `Enqueue*`(1846-1935)、两个 Drain、`TickAcousticLayer`、`MixReleasedGasCore` |
| `continuousSourcesLock` | `continuousSources` 字典 | Register/Update/Unregister/Apply |
| `jetDiagnosisLock` | 喷流诊断字典 | `PublishJetDiagnosis`(869) 与 UI 读取 |

- **主线程任何需要 `engineLock` 的调用都会阻塞到当前粗步结束**。这是本项目 Postfix 插入点必须谨慎的根本原因。
- 引擎内部另有 **27 处 `Parallel.For`**（行号见 §3.6），即"一条模拟线程"实际是"线程池按行并行"。锁只保护"任务 vs 主线程"，**不保护任务内部**。
- `UseLockFreePath => SimulationIntervalTicks <= 15`（`AMC:1033`）：默认 6 → 气体调度**不拿 `engineLock`**（`AMC:1212-1215`）；玩家若把 interval 调到 20/30，气体调度改为持锁路径（`:1216-1220`），两者行为不同，做 patch 时要注意。
- `ExposeData` 会 `Wait()` 两个任务（`AMC:1065-1093`）→ 存档点是一次同步屏障。

---

## 2. 时间线

### 2.1 每 tick（`MapComponentTick`，`AMC:1173-1221`）

| 顺序 | 调用 | 实际成本 |
|---|---|---|
| 1 | `EnsureEngine()` | O(1)（已建即返回） |
| 2 | `gasManager.CompleteScheduledBatchIfReady()` | O(1)，有完成批时才遍历转移列表 |
| 3 | `gasManager.TickWorkers(engine)` | O(气体数 × worker 数) 次取模轮询，不扫图 |
| 4 | `CompleteFinishedPatchSimulation()` | O(1) |
| 5 | `CompleteFinishedSimulation()` | O(1)；完成时含 `roomTempCache.Clear()`、overlay dirty |
| 6 | `flag = Gen.IsHashIntervalTick(map, interval)` | O(1) |
| 7 | `TickAcousticLayer(ticksGame)` | 未激活 → O(1) + `TrackBlastExposure`（空字典早退）；激活 → 每活跃 region 4~5 遍矩形 + 每 tick 全 pawn 查压力 |
| 8 | `TickTemporalPatch(ticksGame, flag)` | **默认 O(1)**（3~4 次比较后 return） |
| 9 | 风系四件套 Tick ×4 | 每 tick 进函数体，内部 30 tick 门控 → 常态 O(1) |
| 10 | `plantSway.Tick(...)` | O(1)（相位推进）+ 每帧 1 次小纹理上传 |
| 11 | `TickLocalForcings()` | 2500 tick 门控 |
| 12 | 主模拟启动判定 + `StartSimulationTask()` | 见 §2.2 |
| 13 | 气体调度 `TickGasScheduler`（`NextDispatchTick` 门控） | 每 tick 最多派发 1 种气体 |

**每 tick 唯一的无条件全图扫描在风系统**：`WindLoadField.EnsureBuilt`（首个到达的风系统触发，`builtTick` 去重）。

### 2.2 每 `interval` tick（默认 6）：主模拟粗步

主线程同步段（`AMC:2962-2970`，任务外）：

1. `sunPowerForCycle = pendingGroundSunPower`
2. `synchronizationTick = lastSimulationLaunchTick`
3. `elapsedTicksForCycle = max(1, sync - lastSynchronizedSimulationTick)`
4. `EnsureLatitudeCached()`、`settings.CoriolisParameter = CoriolisStrength * cachedLatitudeSin`
5. `PrepareLocalForcings(syncTick)`（遍历 `vortexSources`/`beamSources` 注册表，非全图）

启动前（`AMC:1198-1207`）：

6. `RefreshRuntimeSettings()` → **`AtmosphereTuningManager.ApplyScaledSettings(this.settings, interval, ticksGame)`**（每粗步一次 settings 重算，见 §4）
7. `masksDirty` → `RebuildObstacleMask()`
8. `PrepareTemperatureSources()`（见 §5.1）
9. `StartSimulationTask()`

任务体（`AMC:2983-3014`，全程 `lock(engineLock)`）：

```
ApplyGroundEnergy(pendingTemp, pendingMoist, settings, sunPower)      :2983
LocalForcing.BeforeStep                                              :2984
temporalPatchManager.ExcludeOwnedSources                             :2985
ApplyTemperatureDeltas(pendingTemperatureDeltas)                     :2986
ApplyMoistureDeltas(pendingMoistureDeltas)                           :2987
ApplyDivergenceDeltas(pendingDivergenceDeltas)                       :2988
[补丁域 Active 时] MergeBeforeGlobalProjection                       :2992
AdvanceProvisional(settings)                                         :3001（≈26N）
ApplyAirMoversUnsafe                                                 :2993
ApplyJetTransportUnsafe                                              :2994
ProjectAndFinalize(settings)                                         :3004（≈P·N+23N）
ApplyWallLeakage(elapsedTicksForCycle)                               :2996
CompleteJetTransportUnsafe                                           :2997
ApplyVelocityDeltasAfterSolver(pendingExplosionVelocityX/Z, MaxWind) :3009
ApplyTargetWindSourcesAfterSolverUnsafe                              :3010
LocalForcing.AfterSolve                                              :3011
RebaseAfterGlobalProjection(engine, syncTick)                        :3012
PublishSnapshot()                                                    :3013（9N）
AfterGlobalSnapshotPublished                                         :3014
```

任务完成后（`CompleteFinishedSimulation`，`AMC:3071-3096`）：异常仅记日志并继续；否则 `masksDirty` 则重建 + 再 `PublishSnapshot`；`gasManager.NotifySnapshotPublished()`；`CompleteAirMoverGasTransfers()`；`SetTemporalPatchSnapshot` + `AdvanceTemporalPatchGases`；`roomTempCache.Clear()`；`lastSynchronizedSimulationTick = lastSimulationLaunchTick`；`SetOverlayDrawersDirty()`。

### 2.3 低频节拍一览

| 周期 | 工作 | 成本 |
|---|---|---|
| 30 tick（`WindDamageCheckIntervalTicks`） | 结构/家具植物/屋顶/搬运四套伤害实算 | 各 O(成员数)，见 §6.3 |
| 60 tick（`MistPawnEffectIntervalTicks`） | `ApplyWaterMistPawnEffects` | O(AllPawnsSpawned) |
| 60 tick | `PlantWindSway.Recompute` + 纹理上传 | 2N 两遍 + 2 张纹理 `SetPixels32` |
| 60 tick | `AtmosphereTuningManager.LoadIfChanged`（在 `GetSettingsForInterval` 内） | **2 次磁盘 syscall**（`File.Exists` + `GetLastWriteTimeUtc`） |
| 250 tick | `RefreshWallLeak` → `RefreshMaxStoredPressure` + `WallLeakBuilder.Build` | 全图 `(h-2)*(w-2)` × 2 次 `Walk`(≤4 步) + 4 次 `ToArray` |
| 600 tick | `RebuildPlantStatsUnsafe` | `ThingRequestGroup` 全植物遍历 |
| 600 tick | `ProxyHeatRelayMapComponent.CleanupStates` | O(state 数) |
| 2500 tick | `TickLocalForcings` → `TornadoGenesis.HourlyCheck` | 未读透 |
| 2500 tick（风暴中） | `WindPassableLoad.RebuildPlants` | O(植物数) |

### 2.4 每帧（渲染）

| 入口 | 成本 |
|---|---|
| `MapComponentDraw` → `DrawActiveAtmosphereOverlays`（`AMC:3098`） | 各 overlay 层自己的 Draw |
| `WindStreamlineRenderer`（仅 `ShowStreamlineOverlay`） | 粒子数 `clamp(w*h/4, 300, 1200)`；**最坏 1200 次 `Graphics.DrawMesh`** |
| 植物摆动 `AdvancePhases` | 1 次小纹理（`blocksX×blocksZ`）`SetPixels32`，`frameCount` 门控一帧一次 |
| `ThingWithComps.GetGizmos` Postfix（本项目） | 每选中物每帧：1 iterator + 6 Gizmo + 6 `Translate` + 4 闭包 |
| `AcNetInspectPatches`（本项目，VTE） | 同 map 空调的 inspect 面板每帧新建 `StringBuilder` |

---

## 3. 本地数值核（`Simulation/LocalAtmosphereEngine.cs`，5 827 行）

### 3.1 `AdvanceProvisional`（`:2940-2968`）

| pass | 入口 | 复杂度 | 备注 |
|---|---|---|---|
| `UpdatePressureMode` | `:2944`/`2132` | O(1) | 仅模式切换时 O(N) 重建 |
| `DiffuseVelocity` | `:3818` | 1N | `diffusion` clamp ≤ 0.24；无迭代 |
| `AdvectVelocity` | `:3470` | 1N | `TraceBackwardUnbounded`（**不 clamp、不查障碍**）+ 采样时才 clamp |
| `ApplyCoriolis` | `:3936` | 1N | 三角函数在循环外 |
| 爆炸残留扩散+阻尼 | `:4075`/`:4048` | 2N | 仅 `explosionPressureResidueActive` |
| `BeginScalarConservation` | `:5544` | ≤1N | 只遍历"封闭且非天空"分量 |
| `LimitScalarTraceToWalls` | `:5497` | **5N** | 2 次 `Array.Copy` + 1 遍限幅 + 之后 `RestoreWind` 2 次 Copy |
| `AdvectScalar` ×3（温/湿/雾） | `:3506` | 3N | `TraceBackward`（clamp 到 `[0.5, W-1.5]`）+ `BilinearSampleOpenAir`（障碍掩码作权重） |
| `AdvectOxygen` | `:3534` | 1N | byte 场 + 量化 + 露天松弛 |
| `RestoreWindAfterScalarTransport` | `:5531` | 2N | 2 次 `Array.Copy` |
| `EndScalarConservation` | `:5584` | ≤2N | 按分量比例回写温度/湿度/雾 |
| `DiffuseScalar`（温、3 iter） | `:3871` | 3N | `write = read + Σ(nb-read)·mask·diffusion·0.25` |
| `DiffuseScalar`（湿、3 iter） | `:3871` | 3N | |
| `DiffuseOxygen`（1 iter） | `:3590` | 1N | 首次分配 2×`float[N]` 残差 |
| `BeginPhaseChangeCapture` | `:2629` | 2N | 仅可压；2 次 Copy |
| `UpdateHumidityAndThermodynamics` | `:3907` | 1N | **每格 4 次 `Mathf.Exp`**（见下） |
| `ClampThermodynamicFields` | `:4006` | 1N | **串行 for**，非 Parallel |
| `EndPhaseChangeCapture` | `:2642` | 1N | 仅可压 |
| **小计** | | **≈26N** | |

**热力内核每格成本**（`Simulation/AtmosphereThermodynamicKernel.cs:15-74`）：`VaporCapacity` 被调用 **4 次**（`:32/:38/:44/:67`），每次 1 次 `Mathf.Exp(MagnusTetensNumerator*T/(MagnusTetensDenominatorOffset+T))`；另有 `Mathf.Sqrt(vx²+vz²)`、`PressurePascalsFromSolver`、若干 `Clamp`。返回 `struct Result`（9 float），**无堆分配**。
→ 250×250 每步约 **25 万次托管 `Mathf.Exp`**。这是"无法用简单并行优化掉"的固有成本（除非改内核近似）。

### 3.2 `ProjectAndFinalize`（`:2970-2994`）

| pass | 入口 | 复杂度 | 备注 |
|---|---|---|---|
| `RefreshSealedRoomsIfNeeded` | `:2975`/`:588` | O(1) 或全图 BFS | `airMoverConnectivityDirty` 门控 |
| `AddPressureOdditySources` | `:2976` | O(oddityCells) | 未激活时只清 5 个 List |
| `AddThermalExpansionSources` | `:2662` | 0 或 1N | `thermalExpansionAny` 早退 |
| `AddWallLeakSources` | `:5709` | 0 或 1N | `wallLeakAny` 早退 |
| **`Project`** | `:3678` | **P·N + ≈23N** | 见 §3.3 |
| `DampVelocity` | `:3960` | 1N | 直写 `Read`，`×(1-damping·DampingMask)` |
| `ApplyOpenBoundarySponge` | `:4343` | **0** | 空函数 |
| `EnforceBoundaries` ×2 | `:4220` | ≈0.22N | 2 圈边界 + 5 场 `CopyOpenBoundary` |
| `ApplyThermodynamicPressureBaseline` | `:4017` | 1N | **可压时用 `StoredPressurePa` 整体重写压力场** |
| `ApplyExplosionPressureResidue` | `:4114` | 0 或 1N | 直写 `Read` |
| `ClampVelocityMagnitude` | `:3986` | 1N | 超 `MaxWindSpeed` 才除 |
| `ClampVentVelocities` | `:5677` | O(vents) | 默认 `Vents==null` 立即返回 |
| `ImprintChokedJets` | `:1879` | 0 或 O(jet×81) | 无 jet 时 O(1) |
| `ApplyPressureOddityRealized` | `:336` | O(oddityCells) | 未激活时 O(1) |

### 3.3 `Project`（`:3678-3816`）三条求解路径

公共前置：清零 `Pressure.Read`/`Pressure.Write`/`divergence`（3N）→ 散度 pass（1N）→ 分支求解 → 压力梯度回写速度（1N，双 `Swap`）。

| 路径 | 触发条件 | 成本 |
|---|---|---|
| **不可压 Jacobi** | `compressibleActive == false` | 固定 60 次（`PressureIterations`）× N，**无收敛判据** |
| **可压 packed SOR**（默认） | `CompressibleSolver>=0.5 && UseEnclosureDeflation && PackedAvailable()` | `P·N` + 每 8 sweep 一次 `DeflateEnclosures`（≈2N + 24×(A+F)） |
| **可压红黑 SOR（非 packed）** | `PackedSorMode==0` 或存在 nozzle 泵 | `P·N`，两次半图红黑扫描/sweep |
| **可压 Jacobi** | `CompressibleSolver==0` | `P·N` |
| **自适应 SOR** | `CompressibleAdaptive>=0.5`（默认 0，不走） | sweeps ∈ [3,120]，有早停判据 |

其它固定子成本（packed 路径每步）：`CompressibleSetupPass` 1N、`PackedPack` 1N、`Array.Copy(compPn→Pressure.Read)` 1N、`StoreCompressiblePressure`（1 Copy + 1 Pow + `ConserveAdiabaticHeat` 2 遍）≈5N、`EndChoke` 2 Copy=2N、`BeginChoke`+`ApplySkyChoke`=3N、`CancelWallNormalOneSidedGradient` 1N。

**`DeflateEnclosures` 的关键性质**：粗网格未知量是 **aggregate（区域）而非格子**，24 次粗 Jacobi 成本 `24×(A+F)`，远小于 N；但每次前有两次 O(N) 的 `CoarseSumAggregate`/`CoarseApplyAggregate`。**只处理"封闭非天空"分量，开放/天空分量不会被它抹平。**

### 3.4 每步固定内存流量

| 来源 | 次数 |
|---|---|
| `LimitScalarTraceToWalls` + `RestoreWind` | 4×N Copy |
| `BeginPhaseChangeCapture` | 2×N Copy |
| `BeginChoke`+`ApplySkyChoke`+`EndChoke` | 5×N Copy |
| `StoreCompressiblePressure` | 1×N Copy |
| `PublishSnapshot` | 9×N Copy |
| **合计** | **≈21N 次纯 memcpy**（250×250 ≈ 1.3 M 次 float 搬运/步） |

### 3.5 五个值得注意的实现细节

1. **`Parallel.For` 的闭包分配**：27 处 `Parallel.For(..., (Action<int>)(z => …))`（`:2386,2563,3051,3214,3253,3482,3513,3543,3607,3692,3721,3752,3830,3881,3911,3949,3968,3993,4086,4471,4505,4539,4579,5030,5143,5379,5438`）。每处每次调用都新建一个 `Action<int>` 委托 + 捕获闭包（Roslyn 不缓存），一个粗步 **20~30 个 Gen0 小对象**。只有 `DeflateEnclosures` 显式缓存了委托（`:5022-5026`）。
2. **`ClampThermodynamicFields` 是串行 `for`**（`:4006-4015`），在 20+ 遍并行 pass 中间形成一个串行段。小图无感，大图会成为 Amdahl 瓶颈。
3. **两种场语义混用**：多数 pass 写 `Write` 再 `SwapReadWrite`，但 `DampVelocity`/`ClampVelocityMagnitude`/`ClampThermodynamicFields`/`ClampVentVelocities`/`ApplyExplosionPressureResidue` 是**直写 `Read`**。在"写 Write 后 Swap"的 pass 之间直写 Read，会被旧 Write 覆盖。
4. **`FaceOpenX/Z` 在 `Project` 内是被 choke 压过的值**：`BeginChoke` 把系数乘进 `FaceOpen`，`EndChoke` 才恢复。在 `Project` 内部读面开度读到的是压过的值。
5. **`AdvectVelocity` 用无界回溯，`AdvectScalar` 用带 clamp 的回溯**；`LimitScalarTraceToWalls` 只保护标量平流（临时改写速度再还原），**不保护速度平流**。`MaxWindSpeed=120` 时速度采样点可以飞出数格。

---

## 4. 时间步长缩放：`interval` 的真实代价（核心章节）

这一节是本文最重要的发现，因为它给出了一个**比任何代码优化都大 5 倍**的杠杆，而且解释了一个容易误解的现象：**AO 的默认配置恰好是计算量最大的档位之一。**

### 4.1 `ScaleForInterval` 的完整公式

每粗步前 `RefreshRuntimeSettings()` 调 `AtmosphereTuningManager.ApplyScaledSettings(this.settings, interval, tick)`（`AMC:1674`），后者走 `ScaleForInterval(baseSettings, baseIntervalTicks, interval)`（`Simulation/AtmosphereTuningManager.cs:244-282`）：

```
ratio = max(0.1, interval / baseIntervalTicks)          // baseIntervalTicks 来自 XML，=30
TimeStep                     = base.TimeStep * ratio
PressureIterations           = max(4, round(base.PressureIterations * ratio))
if (CompressibleSolver >= 0.5)                          // 默认 1 → 恒成立
    PressureIterations       = max(4, round(base.PressureIterations * (ratio <= 0.45 ? 1.5 : 1.0)))
if (CompressiblePressure > 0)                           // 默认 1 → 恒成立
    EnableTemporalPatches    = 0                        // ★ 补丁域被强制关闭
各种 diffusion / relaxation  = 1 - (1 - p)^ratio        // ScaleProbability
各种 Euler 速率              = ScaleProbability / ratio
```

**XML 实际基准值**（`1.6/Defs/AtmosphereTuning.xml`，注意 `AtmosphereTuningManager.cs:21` 把路径写死为 `1.6/Defs/AtmosphereTuning.xml`）：

```
<baseIntervalTicks>30</baseIntervalTicks>
<TimeStep>1</TimeStep>
<PressureIterations>16</PressureIterations>     ★ 不是代码默认的 60
<PatchSubstepTicks>6</PatchSubstepTicks>
<PatchActivationIntervalTicks>20</PatchActivationIntervalTicks>
<CompressiblePressure>1</CompressiblePressure>
<CompressibleSolver>1</CompressibleSolver>
<CompressibleAdaptive>0</CompressibleAdaptive>
```

代入后：

| interval | ratio | `ratio<=0.45` | **PressureIterations** | TimeStep |
|---|---|---|---|---|
| 3 | 0.10 | 是 | **24** | 0.10 |
| **6（默认）** | 0.20 | 是 | **24** | 0.20 |
| 12 | 0.40 | 是 | **24** | 0.40 |
| 20 | 0.667 | 否 | **16** | 0.667 |
| 30 | 1.00 | 否 | **16** | 1.00 |

> 注意第二行赋值的语义：它用 `base.PressureIterations * 1.5` **覆盖**了第一行的 `*ratio` 结果。即"小步长时反而把压力迭代抬到 1.5 倍基准"，理由是 `TimeStep` 变小后每次 SOR 的推进量也小，需要更多扫掠才能收敛到同样的容差。**这是有意为之的物理一致性设计，不是 bug**；但它带来的性能后果是"小 interval 既更频繁又单步不太便宜"。

### 4.2 五档 interval 的每 tick 成本

用 §0.2 的 `P·N + 81N` 模型：

| interval | P | 每粗步 | 粗步频率（每 tick） | **每 tick 成本** | 相对 |
|---|---|---|---|---|---|
| 3 | 24 | 105N | 1/3 | 35.0N | 2.00× |
| **6** | 24 | 105N | 1/6 | **17.5N** | **1.00×** |
| 12 | 24 | 105N | 1/12 | 8.75N | 0.50× |
| 20 | 16 | 97N | 1/20 | 4.85N | 0.28× |
| 30 | 16 | 97N | 1/30 | 3.23N | 0.18× |

**分解**（interval=6 时每 tick 的 17.5N 从哪来）：

- 投影 SOR：24N/6 = 4.0N（23%）
- 其它 Project 子 pass：23N/6 = 3.8N（22%）
- AdvanceProvisional：26N/6 = 4.3N（25%）
- `PrepareTemperatureSources`（含 11N 空扫）：19N/6 = 3.2N（18%）
- PublishSnapshot + GroundEnergy + 三个 ApplyDeltas：13N/6 = 2.2N（12%）

→ **没有任何单项超过 1/4**。这意味着 AO 的性能特征是"长尾"而非"单点热点"：优化任何一个 pass 最多省 5%，要拿到 2× 只能动 interval 或批量合并 pass。

### 4.3 三个反直觉结论

1. **interval 越大越省，且不是线性**：6→30 省 82%，但精度代价是"大气响应延迟最多 30 tick（0.5 秒）"。对玩家而言 0.5 秒的天气延迟通常不可感知，**这是一个被默认值浪费掉的大杠杆**。
2. **interval ≥ 20 时才会尝试激活时间补丁域，但补丁域又被可压强制关闭** → interval=20/30 是"纯低频粗模拟"，没有细步补偿。所以"把 interval 调大"确实会损失局部瞬态精度（爆炸、喷流、开关门的响应），这与 AO 文档中"补丁域用于局部细化"的设计意图正好相反地被默认配置抵消了。
3. **`ConsiderCell` 在 interval<20 时仍然被全图每格调用一次**（`PreparePendingFieldDeltasUnsafe` 第二遍，`AMC:3401-3435` → `TemporalPatchManager.cs:138-139` 立即 return）。默认配置下这是 **62 500 次必然返回的方法调用/粗步**，纯浪费。这一遍理论上可以在 `interval < PatchActivationIntervalTicks` 时整段跳过。

---

## 5. 地图组件管线（`AtmosphereMapComponent.cs`，4 440 行）

### 5.1 `PrepareTemperatureSources`（`:1788-1817`）—— 唯一的写入窗口

顺序（全程在 `lock(engineLock)` 内）：

```
EnsureEngine / InitializeAmbientFieldsIfNeeded / 4×EnsureBuffer        :1792-1797
Array.Clear ×5: pendingTemperature/Moisture/Divergence/ExplosionVX/VZ :1798-1802   ★ 清空
BeginDetection(settings, interval)                                     :1804
PreparePendingFieldDeltasUnsafe()                                      :1805   2 遍全图
RebuildPlantStatsUnsafe()                                              :1806   600 tick 门控
pendingGroundSunPower = ComputeCurrentSunPower()                       :1807
DrainIncomingTemperatureDeltasUnsafe()                                 :1808   1 遍全图
DrainExternalIncomingDeltasUnsafe()                                    :1809   ★ 11 遍全图（无脏标志）
ConsiderContinuousSourcesForPatchUnsafe()                              :1811
CommitDetection()                                                      :1812
ApplyContinuousSourcesUnsafe(ticksGame)                                :1813   O(源数)
PrepareAirMovers(ticksGame)                                            :1815
```

**为什么这是唯一正确的写入窗口**（三重约束）：

- 在 `:1798` 之前写 `pending*` → 被 `Array.Clear` 抹掉；
- 在 `:3001`（主任务 `ApplyTemperatureDeltas`）之后写 → 跳过"障碍格过滤 + 屋顶格 `NoteThermalExpansion` 记账"，且下一粗步又被清空；
- Postfix 的执行点落在 `:1815` 之后、任务体之前，恰好是"已清空、已汇聚、尚未 apply"。

**地温通道已验证有效**：`ApplyGroundEnergy` 以 `groundT[i]` 作为 `AtmosphereGroundEnergyKernel.Advance` 的初值，再写回 `groundT[i] = result.GroundTemperature`（`LocalAtmosphereEngine.cs:3056-3057`，已逐行核实）。所以外部 `+= engine.GroundTemperature[i]` 是"改写下一步的积分初值"，**不是被覆盖**。这与空气通道 `pendingTemperatureDeltas[i] +=` 的语义不同（后者是本步追加增量），但两者都有效。

### 5.2 源注册系统（`continuousSources`）

| 方法 | 查找 | 复杂度 |
|---|---|---|
| `RegisterContinuousSource`（`:2409`） | **foreach 全字典线性查 (Owner, SourceKey)** | O(源数)，命中则就地更新（先 `Flush`） |
| `UpdateContinuousSource`（`:2453`） | `TryGetValue(id)` | O(1) |
| `UnregisterContinuousSource`（`:2484`） | `TryGetValue(id)` | O(1) |
| `UnregisterContinuousSources(owner)`（`:2497`） | 全字典收集到 `new List<int>()` 再批量删 | O(源数)×2 |
| `ApplyContinuousSourcesUnsafe`（`:2534`） | foreach 全部源 | O(源数)，**每源只写 1 格** |
| `FlushContinuousIntervalToQueues`（`:2622`） | 按 dt 把 8 个速率入队 | 8 次 `Enqueue*`（各持锁）+ 1 次 `RecordImpulse` |

**关键性质：连续源没有"半径²"展开**。每源每粗步只按 `CellIndex` 写 1 格（风分量另乘 `obstacleMask` 系数）。半径效果完全由调用方（ProxyHeat/CCC/VTE 的 relay）自己展开成多源。

**成本含义**：源数 S 时，每粗步 `O(S)`；每细步（若补丁域激活）再 `O(S)`。上千源时这笔开销会与"11N 空扫"同量级（S=1000 时 1000 vs 11×62500=687500，量级仍相差两个数量级，**不必为源数过度焦虑**，但要避免在 `RegisterContinuousSource` 里做 O(S²) 的批量注册）。

### 5.3 AirMover / Jet（喷流）

| 方法 | 结构 | 每步成本 | 分配 |
|---|---|---|---|
| `RegisterAirMover`（`:185`） | **for 线性查 (Owner, Key)** | O(mover 数) | 0 |
| `UnregisterAirMovers(owner)`（`:255`） | 逆序 for + 内层再线性查 | **O(n²)** + `RemoveAt` 位移 | 0 |
| `PrepareAirMovers`（`:430`） | 逆序清理 + 逐个 Capture | O(mover 数) | `pendingAirMovers.ToArray()` 1 个数组/粗步 |
| `ApplyAirMoversUnsafe`（`:472`） | 两遍 prepared | O(mover) | `new Dictionary<int,float>` ×2 + `new float[len]` + 每 mover 的 `OutletMixture`/`AirMoverGasTransfer` |
| `ApplyJetTransportUnsafe`（`:597`） | foreach prepared，按 (inlet,outlet,dir) 去重 | O(mover) | `new Dictionary<(int,int,float,float),Source>` + `new Dictionary<int,float> demand` |
| `CompleteJetTransportUnsafe`（`:748`） | `JetTransport.Advance` | **最重分支**，见下 | |
| `UpdateSealedRooms`（`:839`） | 按源列表 | O(源 × 组件格数) | 0 |
| `PinPressure`（`:831`） | `SealedComponentMean` | O(组件格数)，非全图 | 0 |
| `PublishJetDiagnosis`（`:869`） | 复制 int 键字典 | 每步无条件，不生成字符串 | 0 |

**`JetTransport.Advance`（`Simulation/JetTransport.cs:23-106`）是整条管线最重的分支**：

```
每源 BuildPath(...) → new List<int> + ToArray            :135-157
LastSubsteps = clamp(ceil(max(demand*2, speed*TimeStep*2)), 1, 512)    :48
for substep in 0..LastSubsteps:
    foreach source:
        LimitedVolumes / outlet 混 4 场 / 可选混动量
        沿 Path 逆序逐段：5 个场 Exchange + 氧 + GasExchanges 累加   :74-93
```

→ 成本量级 **O(LastSubsteps(≤512) × 源数 × pathLen(≤128))**。当存在大量高流量喷流时，这一项可以单独吃掉一个粗步的大部分时间。**这是本项目"主动气体转移"设计必须重点评估的性能边界。**

### 5.4 掩膜重建（`masksDirty`）

触发链：`MarkCachedMapDataDirty()`（`:1277-1281`，`Volatile.Write` + `gasManager.NotifyMapDataDirty()`）；Harmony 调用点覆盖 `Thing.DeSpawn`、`TerrainGrid.SetTerrain`、`RoofGrid.SetRoof`、`Building_Door.DoorOpen/DoorTryClose`、`Building.SpawnSetup`、`CompAirVent`、`Building_VacBarrier.ReceiveCompSignal`。**只置 dirty，不立刻重建。**

消费点三处：启动模拟前（`:1201`）、粗模拟完成后（`:3077`）、细步前（`:2100`）。`SimulationRunning` 时 `RebuildObstacleMask` 只置 dirty（`:1265-1267`）。

`RefreshCachedMapDataUnsafe()`（`:3311-3337`）的真实成本：

```
RebuildObstacleMaskUnsafe       :3313   2 遍全图(MapObstacleMaskBuilder) + 若干 N 级数组分配
windLoad.Rebuild                :3314   含 for size 段
windPassables.RebuildBuildings  :3315   遍历全部人工建筑
windRoofs.Rebuild               :3316   2 遍全图 + chamfer 距离变换
engine.SetBlockedCellsToAmbient :3319   全 size 扫描
```

→ **一次围栏/门/屋顶变动就触发"4 套系统 + 引擎"的全图重建**。这是 AO 在"建造/拆除密集"场景下的主要卡顿来源。本项目如果需要频繁改变拓扑（例如 ProxyHeat 建/拆 jet），必须批量化 `MarkCachedMapDataDirty`。

### 5.5 时间补丁域（默认死路径）

代码结构完备（`TemporalPatchManager` 634 行 + `TemporalPatchDomain` 971 行 + `TemporalPatchTopology` + `TemporalPatchMaskBuilder` + `PatchSourceJournal`），但默认配置下：

- `ScaleForInterval` 强制 `EnableTemporalPatches = 0`（`AtmosphereTuningManager.cs:253-254`）；
- `BeginDetection` 见 `EnableTemporalPatches <= 0` 立即 return（`TemporalPatchManager.cs:123`）；
- `ConsiderCell` 见 `coarseInterval < PatchActivationIntervalTicks(20)` 立即 return（`:138-139`）；
- `TickTemporalPatch` 见 `EnableTemporalPatches <= 0` 立即 return（`AMC:2093`）；
- → `domain` 永远 `null`，`PublishedSnapshot` 永远 `null`，`UseGlobalSubcycling` 永远 false，`RunGlobalSubcyclingUnsafe` 不可达。

**但有两笔成本在死路径下仍然发生**：

1. `PreparePendingFieldDeltasUnsafe` 第二遍里对全图每格调用 `ConsiderCell`（62 500 次必然返回）；
2. `RebaseAfterGlobalProjection` **先**执行 `sourceJournal.DiscardThrough(syncTick)`，**再**判断 `domain == null` 返回（`TemporalPatchManager.cs:360-364`）——即每个粗步都做一次 journal 截断（空表时 O(1)，有 impulse 时 `new List<long>()` + 全表遍历 + `Remove`）。

---

## 6. 风系统

### 6.1 频率表

| 类.方法 | 调用频率 | 扫描范围 | 量级 |
|---|---|---|---|
| `WindLoadField.EnsureBuilt` | **每 tick 一次**（`builtTick` 去重） | **全图 N** + 全块 3×3 膨胀 | ≈N 次乘加 |
| `WindStructuralLoad.Tick` | 每 tick 进函数，**30 tick 实算** | 结构成员 `count` + 每成员 4 方向×≤3 格 | 每 30 tick O(count) |
| `WindPassableLoad.Tick` | 每 tick 进函数，**30 tick 实算** | `buildings`/`plants`（风暴时 +`wild`） | 每 30 tick O(成员数) |
| `WindRoofLoad.Tick` | 每 tick 进函数，**30 tick 实算** | 屋顶成员 `count` + ≤4 edgeSample | 每 30 tick O(屋顶格数) |
| `WindRoofLoad.TickEffects` | **每 tick** | `effects[0..effectCount]`，上限 96 | ≤96 次 EffectTick |
| `WindTransport.Tick` | 每 tick 进函数，**30 tick 实算** | `HaulableEver` 全列表 + `AllPawnsSpawned` | 每 30 tick O(可搬运物 + pawn) |
| `PlantWindSway.Tick` | 每 tick；**60 tick Recompute** | Recompute 全图两遍 | 每 60 tick 2N |
| `WindPathCosts.MapComponentTick` | 每 tick | 刷 `ceil(8*height/max(8,refreshTicks))` 行 | O(行数×width)，仅 windy 时 |
| `WindLostThings.WorldComponentTick` | 世界 tick | 丢失物列表，250 tick 才查返还 | 极小 |
| `WindTransport.WalkFactor` | 每次寻路代价查询 | 采样 `to` 一格 | O(1) |

### 6.2 唯一的每 tick 全图扫描：`WindLoadField`

- 8×8 格一 block（`BlockShift=3`），`raw[]`/`dilated[]` 长度 `blocksX*blocksZ`（`RimWorld/WindLoadField.cs:28-31`）。
- 每 tick：`raw[block] = max(vx²+vz²)`（全图 N 次读，`:59-71`）→ 叠加补丁域活跃格速度（`:72-88`）→ 3×3 膨胀得 `dilated`（块数×9）→ 缓存 `MapMaxSq`。
- **不做跨 tick 累加**，每 tick 整体重算；`builtTick` 保证同一 tick 内多系统只算一次。
- `BoundAt(block)` 越界返回 `float.MaxValue`（视作无限风压），`RimWorld/WindLoadField.cs:33-36`。

→ 这是"每 tick 固定的 N 量级成本"，与 interval 无关，**不能通过调 interval 省掉**。

### 6.3 四套伤害系统

| 系统 | 成员来源 | 重建时机 | 每 30 tick 成本 |
|---|---|---|---|
| 结构 `WindStructuralLoad` | `edificeGrid` 过滤（非蓝图/挡光/有 HP/非自然岩） | 仅 `masksDirty` | O(count×4×3 采样) + 风暴时 `Sweep` ×12 |
| 家具植物 `WindPassableLoad` | `ThingRequestGroup.BuildingArtificial` / `.Plant` | `masksDirty`；植物另按 2500 tick | O(成员数)，每成员 ≤16 格采样 |
| 屋顶 `WindRoofLoad` | 全图屋顶格 | 仅 `masksDirty` | O(屋顶格数) |
| 搬运 `WindTransport` | **无注册表**，每 30 tick 直接遍历 `HaulableEver` | 每 30 tick | O(可搬运物) + O(pawn) |

共同设计：**事件上限用轮转游标实现**（非随机抽样），保证不遗漏也不突发。

| 系统 | 上限 | 实现 |
|---|---|---|
| 结构 | `WindDamageMaxEventsPerCheck=64` | `(applyCursor+i)%count` 连续取 64 |
| 家具植物 | 64 | `(set.Cursor+i)%count` |
| 野生植物 | 256 | 同上，仅风暴态 |
| 屋顶 | 掀翻 64 / 特效 24 | `Hash01` 随机起点轮转 |
| 搬运 | 起飞 16 / 在飞 64 / 投掷 4 | 逐候选计数 |

### 6.4 寻路成本场 `WindPathCosts`

- 8 个八分区 × 2 双缓冲 = **16 张整图 `NativeArray<ushort>`**（Persistent）+ 每个已记录目的地一张整图（0~64 个）。
- `windy = MapMaxSq >= WindWalkCalmSpeed²` 才刷新；每 tick 刷若干行、一轮结束 `Notify_CellDelta(IntVec3.Zero)` 让原版寻路路径失效重算（`RimWorld/WindPathCosts.cs:189-223`）。
- **含义**：这是"用寻路缓存换 CPU"的设计——风改变时全图寻路代价场失效，会引发一次原版 `PathGrid` 重算风暴。若本项目要频繁改风场，需注意这条隐藏成本。

### 6.5 风渲染

| 渲染器 | 频率 | 规模 | 说明 |
|---|---|---|---|
| `WindStreamlineRenderer`（地图风线） | 每帧，仅 `ShowStreamlineOverlay` | 粒子 `clamp(w*h/4,300,1200)`，固定 `new WindParticle[1200]` | 逐粒子 **`Graphics.DrawMesh` 一次** → 最坏 1200 draw call/帧；`animationAccumulator` 不足 1/30s 不推进 |
| `WorldWindStreamlineRenderer`（世界风线） | 每帧，缓存失效才重算 | `cachedSegments[1500]`、`cachedPathPoints[73500]`、`instanceMatrices[1023]` | **单次** `DrawMeshInstanced`；刷新预算 2700 次 raycast |
| `PlantWindSway` | 每帧相位上传；60 tick 重算 | fine 网格 + block 网格两张纹理（每 8 格 1 像素） | `MapMeshDirty` 触发 section 重建是 CPU 侧最大项 |

---

## 7. 气体系统

### 7.1 4 种气体与存储模式

`1.7/Defs/AtmosphereGasDefs.xml`：`AO_BlindSmoke` / `AO_ToxGas` / `AO_RotStink` / `AO_DeadlifeDust`，**全部**：

```
storageMode  = VanillaGasGridBridge
renderMode   = VanillaGasLayer
maxDensity   = 255
activeThreshold = 1
scheduled    = true
```

- **Bridge 模式**：密度数据实际在原版 `GasGrid` 的 packed uint 里（每格 4 字节装 4 种原版气体 × 8 bit）。AO 只在批处理期间把它解包到临时缓冲，**每格额外常驻 = 0**。
- **Managed 模式**：`state.Density` 每格 1 个 float，但需要自带 `TintedQuads` 渲染器才有画面（`ManagedGasRenderer` 只认这条路，且当前 4 种气体都不走）。
- 两种模式都会分配工作缓冲（`Gases/AtmosphereGasState.cs:32-46`）：每气体 3×`float[N]` + 1×`uint[N]` + 3×`ulong[ceil(N/64)]` ≈ **1.0 MB/气体**（250×250 推算）。Bridge 模式下 `Density` 也会被分配但基本不用 → 白占 N float。
- 注册是**全量 `DefDatabase` 扫描**（`AtmosphereGasManager.cs:358-388`），第三方新增 `AtmosphereGasDef` 会自动加入。

### 7.2 调度

```
每 tick 门控：if (flag || ticksGame < NextDispatchTick) return;      AMC:1209
interval = max(SimulationIntervalTicks, gasCount + 1)                AtmosphereGasManager.cs:574-579
每 tick 最多派发 1 种气体；同一时刻只有 1 个后台 Task
HasRunningTask || isSolverLaunchTick → next = current + 1
cursor >= count → next = gasCycleStartTick + interval
```

默认 4 种气体 + interval=6 → 周期 = `max(6,5) = 6` tick，4 次派发分摊在 4 个非模拟 tick 上 → **每种气体约每 6 tick 更新一次**。
**含义**：模拟求解 tick 上完全不跑气体，把 CPU 让给主 solver（这是有意设计）。

### 7.3 每批成本

`ComputeGasBatch`（后台 Task，`AtmosphereGasManager.cs:616-639`）：

| 阶段 | 成本 |
|---|---|
| `CaptureSingleGasBatch` | 按 stale 标记 `Array.Copy` 克隆 `ObstacleMask`/`VelocityXSnapshot`/`VelocityZSnapshot` |
| `CaptureSingleGasContext` | Bridge 逐格 `gasGrid.GetDirect` 解包；**任一格 ≥ activeThreshold 才继续**，否则整批跳过 |
| `AdvectGas` | 半拉格朗日，`TraceBackward` + `BilinearSampleOpenAir` |
| `DiffuseGas` | 4 邻域加权，`iterations = diffusionIterations`（默认 1） |
| `EnforceNonIncreasingGasMass` | **两遍全 N** 求和后整场缩放 |
| `BuildGasResult` | O(N) |
| `RebuildActiveAndWorkBuckets` | **O(N) 全数组**，每批跑 4~6 次 |
| `RoundBridgeDensity` | O(N) 纯计算 |

→ 每批约 **6N~10N 次浮点/比较**，**与活跃格多少无关**（bucket 只优化算子内部，不优化 bucket 重建本身）。4 种气体 × 每 6 tick → 每 tick 平均 1N 量级。

**分配**：每批固定 3 个上下文对象 + 3 个捕获 lambda 闭包 + 1 个 `Task` → 默认约 2 对象 + 2 闭包/tick，可控但可消除。

### 7.4 渲染

`Patches/GasRenderPatches.cs` 整体替换 `SectionLayer_Gas.Regenerate`：

- 逐格 `AnyGasAt` → 取 4 种原版气体最大密度百分比 → `BaseQuadCountForDensity` 映射 1..6 个 quad；
- `GasRenderBudgetManager` 汇总 section 直方图 → 全局 `RenderLevel(0..6)` → 每格实际 quad = `base - renderLevel`；
- **全图总 quad ≤ 100 000**（`QuadBudget`），`MaxBaseQuadCount=6`；
- 每次 `Regenerate` 分配 `new int[7]` + `new List<GasCellRenderData>()` → **section 重建时成批小分配**（建造/缩放）。

---

## 8. 世界引擎与渲染

（待补：`GlobalSimulationScheduler` / `WeatherClockGate` / `GpuAutodriveGlobalClimateEngine` 推进频率、`GlobalAtmosphereFrameSnapshot` 与 `GlobalClimateTileCache` 的更新成本、overlay 世界层渲染。）

---

## 9. 本项目 patch 侧成本

### 9.1 反射桥：结构良好，热点在 `Invoke`/装箱

三座桥（`AoBridge` / `AtmosphereFieldBridge` / `ProxyHeatBridge`）全部是**类型与成员懒解析一次存 static 字段**，无 `GetMethod` 热调用，无 `Delegate.CreateDelegate`，无表达式树。仍走 `Invoke`/`GetValue`/`SetValue` 的点：

| 桥方法 | 调用点 | 频率 | 每次分配 |
|---|---|---|---|
| `AoBridge.TryGetTemperature` | `CompTempOutputShape.cs:174`、CCC `AirLoopMapComponent.cs:808/825`、GC `GcAirRelay.cs:719-720` | 每 TickRare × 每输出格 | `new object[]{map,cell,0f}` |
| `AoBridge.SetSlot` | `CompTempOutputShape.cs:191/286/293`、CCC `:1057`、VTE `AcNetLedger.cs:319` | 每 TickRare × 每格 | **`Activator.CreateInstance` + 10 次 `FieldInfo.SetValue` + `object[5]`** |
| `AtmosphereFieldBridge.RegisterAirMover` | `ProxyHeatRelayMapComponent.cs:1372`、`GcAirRelay.cs:382/401` | 每粗步 × 每 jet port | **`Activator` + 12 次 `SetValue` + `Invoke` + `object[]`** |
| `RegisterContinuousSource`/`UpdateContinuousSource` | `GcAirRelay.cs:673/678/683/688` | GC 每 60 tick | `Activator` + 10 次 `SetValue` + `Invoke` |
| `ProxyHeatBridge.GetMaxTemperature` | `ProxyHeatRelayMapComponent.cs:1461` | 每粗步 × 每源 | `float?` **装箱** |

**结论**：反射调用本身不是问题（`MethodInfo.Invoke` 约 100ns 级），**装箱与 `object[]` 分配**才是问题。真正值得改的只有三处：`RegisterAirMover`（每端口 14 次反射）、`SetSlot`（每格 11 次反射）、`GetGizmos`（见 9.3）。

### 9.2 Harmony patch 频率风险

| 目标 | 类型 | 频率 | 风险 |
|---|---|---|---|
| `AtmosphereMapComponent.PrepareTemperatureSources` | Postfix | 每粗步 | **中高**（本项目 ProxyHeat 主注入点） |
| `ThingWithComps.GetGizmos` | Postfix | **每渲染帧 × 每选中 ThingWithComps** | **中**（挂在所有 ThingWithComps 上，未命中也要付一次 `AllComps` 线性扫描） |
| `CompCeilingFan.CompTick`（VTE） | Prefix | **每 tick × 每吊扇** | 中（短路原方法 + `Spin`） |
| `AcUnit.TickRare`（VTE） | Prefix `Priority.High=600` | 每 TickRare | 中（抢在 AO 前） |
| `AcPipeNet.PipeSystemTick`（VTE） | Prefix | 每 tick（管网） | 中 |
| `CompleteFinishedSimulation`（CCC/VTE） | Prefix+Postfix | 每粗步 | 中（dt 记账） |
| `CompInspectStringExtra` ×3（VTE） | Prefix | **每帧**（inspect 面板） | 中（`new StringBuilder` + 多次 `Translate`） |
| `FacilityPatchUtility.RunTempControl` | Prefix | 每建筑每 TickRare | 中（每格 `TryGetTemperature`+`SetSlot`） |

**全项目无 Transpiler**（符合 AGENTS.md 要求）。**没有任何 patch 挂在"每格循环内层"的原版函数上** —— 最密的只是每 tick 每建筑。

### 9.3 ProxyHeat 组件

- **该组件没有 `Tick`/`MapComponentTick` 覆写**，只有 `ExposeData`。全部成本由 `PrepareTemperatureSources` 的 Postfix 驱动 → **每粗步一次**，不是每 tick。
- `Inject()`（`ProxyHeatRelayMapComponent.cs:836-1100`）分段：
  - 固定反射开销约 9 次 `PropertyInfo.GetValue` + `FindManager`（遍历 `map.components`）+ `SeedKnownSources`；
  - 每源：`RefreshActive`（2 反射 + `TryGetComp`）、`RefreshOutcome`（3 反射 + 2 `TryGetComp` + 遍历整圈 RingCells）；
  - **内层格循环 **默认被整段跳过**（三模式默认全 false，`:1018-1021` 的 `continue`）；
  - `WriteChannel` 只遍历被命中的格（`_touchedAir/_touchedSoil`）；
  - `InjectJets` 每源每端口一次 `RegisterAirMover` + 1 个字符串 `"aopjet:"+id+":"+d`（`:1374`）。
- **没有全图遍历**；`EnsureBuffers`（16 个 `size` 长度数组）只在地图尺寸变化时分配；`stamp` 机制替代每周期 `Array.Clear`。
- `dt = now - _lastInjectTick` 且**上限 600 tick**（`:942-945`）→ 暂停/读档后不会补算整段时间。频率无关化正确。

### 9.4 本项目侧最值得优化的三点

1. **`InjectJets` 每端口**：字符串拼接 + `Activator.CreateInstance` + 12 次 `SetValue` + `Invoke`。可缓存 key、复用实例、改用 setter 委托。
2. **`GetGizmos` Postfix**：每帧每选中物 1 iterator + 6 Gizmo + 6 `Translate` + 4 闭包 + `AllComps` 扫描。可缓存 Gizmo 实例与 `Translate` 结果。
3. **`RefreshOutcome` 每源遍历 RingCells + 3 次反射（含 `float?` 装箱）**：可把 cap/floor 缓存在 `state` 里，getter 改委托。

---

## 10. 热点排序

按"每 tick 折算格访问次数"排序（interval=6，250×250）：

| 排名 | 热点 | 每 tick 量级 | 占比 | 可优化性 |
|---|---|---|---|---|
| 1 | `Project` SOR 扫描（24N/6） | 4.0N | 23% | 低（物理必需）；可试 `CompressibleAdaptive=1` 早停 |
| 2 | `AdvanceProvisional`（26N/6） | 4.3N | 25% | 中：合并 pass、消除 Copy、热力内核近似 |
| 3 | Project 其它子 pass（23N/6） | 3.8N | 22% | 中：`UseEnclosureDeflation=false` 省 4N |
| 4 | `PrepareTemperatureSources`（19N/6） | 3.2N | 18% | **高**：11N 空扫可加脏标志；`ConsiderCell` 可整段跳过 |
| 5 | `PublishSnapshot` 等（13N/6） | 2.2N | 12% | 中：Snapshot 可改增量/双缓冲指针交换 |
| 6 | `WindLoadField.EnsureBuilt` | 1N/tick | **与 interval 无关** | 中：可降频到每 2~3 tick（视觉几乎无差） |
| 7 | 气体批处理 | ~1N/tick | — | 中：bucket 重建可加脏标志 |
| 8 | 风系四件套（30 tick） | O(成员数)/30 | 场景相关 | 低（已是节流设计） |
| 9 | `WallLeakBuilder`（250 tick） | O(N)/250 | 低 | 低 |
| 10 | 掩膜重建（拓扑变动） | 4 套系统全图 | **突发** | **高**：批量化 dirty |

**结论**：稳态下 AO 的成本是**长尾分布**（无单项 >25%），所以"改一个循环"最多省 5%。真正有效的三个方向是：

1. **调 interval**（省 72~82%，零代码）；
2. **消除 3 笔结构性浪费**（11N 空扫、`ConsiderCell` 空调用、`Parallel.For` 闭包），合计约 20%；
3. **批量化掩膜重建**（消除建造/拆除场景的突发卡顿）。

---

## 11. 对后续开发的直接含义

### 11.1 性能预算红线

本项目新增任何"每粗步"工作，应控制在 **≤2N**（即 ≤ 全步的 2%）以内。换算成绝对量：250×250 图每粗步 ≤ 12.5 万格操作。超出这个量级的机制必须：
- 走"源注册"（每源 1 格，O(源数)），而不是自己扫图；
- 或按 tick 门控（≥30 tick 一次）；
- 或复用 AO 已有的场（`engine.ObstacleMask`/`RoofMask`/`VelocityXSnapshot`），不要自己再建一份。

### 11.2 插入点选择

| 需求 | 正确插入点 | 禁忌 |
|---|---|---|
| 加/减温度、湿度、雾、地温 | `PrepareTemperatureSources` Postfix 写 `pending*` / `engine.GroundTemperature` | 写 `Pressure.Read`、在任务体内写、在 `Array.Clear` 前写 |
| 加/减动量 | `RegisterContinuousSource` 的 `VelocityXPerTick` 字段，或 `ApplyVelocityDeltasAfterSolver` 之后 | 在 `AdvanceProvisional` 里注速度（会被投影抹掉） |
| 改建筑/地形/屋顶 | 调 `MarkCachedMapDataDirty()`（public） | 直接调 `RebuildObstacleMask`（抢锁且在任务中只置 dirty） |
| 加气体源 | `engine.AddGas`/气体批处理 API | 直接写 `state.Density`（会被下一批覆盖） |
| 读大气状态 | `engine.XxxSnapshot`（跨线程安全） | 读 `engine.Xxx.Read`（与 `Parallel.For` 竞争） |

### 11.3 必须尊重的三个"陷阱"

1. **`FaceOpenX/Z` 在 `Project` 内是被 choke 压过的值**——不要在 `Project` 期间的面开度上做物理判断。
2. **`ApplyThermodynamicPressureBaseline` 每步用 `StoredPressurePa` 整体重写 `Pressure.Read`**——直写 `Pressure.Read` 的 patch 会在下一步被抹掉；要改压力必须写 `StoredPressurePa`（`AddStoredPressurePa` 已封装 clamp）或 `externalDivergence`。
3. **`EndScalarConservation` 会按分量比例缩放温度/湿度/雾**——直写这三场不会被抹掉，但会被乘上一个全局比例，效果不可精确控制。要精确控温用 `pendingTemperatureDeltas`。

### 11.4 对"主动气体转移装置"设计的约束（承接既有文档）

`JetTransport.Advance` 的 `O(LastSubsteps(≤512) × 源数 × pathLen(≤128))` 是硬边界。设计时：

- 源数应 ≤ 数个（每个建筑一个源，不要每格一个源）；
- `Speed*TimeStep` 决定 `LastSubsteps`，高风速会线性放大成本；
- `pathLen = min(128, max(2, ceil(Speed*TimeStep)+2))`，长距离输送会放大路径长度；
- 若需要"大量低速输送"，**用连续源（每源 1 格）比用 AirMover 便宜 2~3 个数量级**。

---

## 12. 未读透 / 未确认

| 项 | 状态 |
|---|---|
| 世界引擎（V2 WebGPU）的推进频率、GPU→CPU readback 频率与阻塞点 | **待补（§8）** |
| `TornadoGenesis.HourlyCheck` 内部成本 | 未读 |
| `AtmosphereGasWorker.TickRareEffects` 各 Worker 实现 | 未读 |
| `ApplyGasBatchResult`/`UpdateDensity` 的写入范围 | 未读 |
| `WindStructuralLoad.ComputeDemand`/`Sweep` 的精确复杂度 | 只确认有 `for size`/`for count` 段 |
| `CompTemperatureSource.RecalculateAffectedCells` 的实际触发频率 | 未确认 |
| `ProxyHeatRelayMapComponent.cs:1027` 的 `UsesOutdoorTemperature` 门控语义是否与"室内范围效果"一致 | **存疑**，需与 ProxyHeat 的 `AffectedCells` 语义核对 |
| 各路径真实毫秒开销 | **未做 profile**（本文刻意不给毫秒数） |
| Burst 原生库是否 SIMD | 无法从托管侧判断（`Initialize` 恒 false） |
| `ManagedGasRenderer` 是否有别的预算补偿路径 | 未找到 |
