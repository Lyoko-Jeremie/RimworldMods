# AO 源码全景阅读报告

> 证据来源：dotPeek 反编译源码 `f:\RimworldCodeIL\AtmosphereOverhaul\`（472 个 .cs，主 DLL，MVID `4E9F1EAE-1C1D-40E4-8CE7-79113DF84BA7`）与 `f:\RimworldCodeIL\AtmosphereOverhaul.Perf81\`（46 个 .cs，V2 全球引擎，MVID `A323D87E-C96A-41D1-9DF1-FD5AA9FFF240`）；XML 默认值 `1.7/Defs/AtmosphereTuning.xml`（332 行）。
> 本文只记录能在源码里直接指认的事实；推断会标注「推断」。

---

## 一、总览：两套引擎，两种物理，薄膜接口

| | 本地模式 Local | 世界模式 Global V2 |
|---|---|---|
| 类型 | `Simulation.LocalAtmosphereEngine` | `Simulation.Global.V2.GlobalAtmosphereV2DryCore` |
| 程序集 | AtmosphereOverhaul.dll | AtmosphereOverhaul.Perf81.dll |
| 维度 | 二维 (x,z)，单层空气包 | 三维球面，8 层 σ-p |
| 网格 | 规则笛卡尔，collocated | 二十面体 Voronoi，C-grid 交错 |
| 状态量 | T, P, u, w, 水汽, 雾, RH, O₂(byte) | p_s, θ(8 层), 边法向 u(8 层) |
| 数值核心 | 半拉格朗日 + 可压缩投影 (SOR) | 静力原始方程 + TRiSK + CLUST25 + 半隐式 |
| 时间步 | 每 6 tick（默认），TimeStep 缩放 | 内步 300 s（CFL>0.6 → 150 s） |
| 帧 | 无帧概念，异步 Task | 2500 tick = 1 小时/帧，24 帧前瞻 |
| 尺寸 | `map.Size.x × map.Size.z` | 65,612 列 × 8 层 = 524,896 单元 |
| 守恒审计 | 无 | 质量 1e-6、水 1e-8、逐小时能量 |

**两者只通过 3 个标量耦合**：tile 外部温度、近地气压、真空标志。风、湿度、降水、氧都不直接过桥。

---

## 二、本地模式

### 2.1 场与掩膜

SoA 双缓冲（`AtmosphereField`：Read/Write/Snapshot）：

- `Temperature`（℃）、`Pressure`（solver 单位）、`VelocityX/Z`（m/s）、`Moisture`（绝对水汽）、`Mist`（雾滴密度）、`RelativeHumidity`（诊断量）
- `Oxygen`：`ByteAtmosphereField`，0–100 整数百分比
- `GroundTemperature`、`LocalRainfall`

三张掩膜决定一切：

| 掩膜 | 含义 | 影响 |
|---|---|---|
| `ObstacleMask` | 1 = 空气格，0 = 实心 | 实心格所有场清零、速度置 0 |
| `FaceOpenX/Z` | 0..1 面开度 | 散度、压力梯度、Jacobi 分母全部按面加权 |
| `RoofMask` | 1 = 有顶 | `beta = sky` 只在露天格生效；决定太阳直射与外部松弛 |

### 2.2 主循环（`RunGlobalSubcyclingUnsafe`）

```csharp
int[] array = TemporalPatchSubstepSchedule.Build(elapsedTicks, patchSubstepTicks);
foreach (int num in array) {
    var s = (num == patchSubstepTicks)
        ? AtmosphereTuningManager.GetSettingsForInterval(num, tick)
        : AtmosphereTuningManager.GetSettingsForExactTail(num, tick);
    // 覆盖 CoriolisParameter / AmbientTemperature / GroundSkinExcess / AmbientRelativeHumidity / AmbientWind
    Array.Clear(groundTempDeltas); Array.Clear(groundMoistureDeltas);
    engine.ApplyGroundEnergy(groundTempDeltas, groundMoistureDeltas, s, sunPower);
    engine.ApplyTemperatureDeltas(groundTempDeltas);
    engine.ApplyMoistureDeltas(groundMoistureDeltas);
    float frac = (float)num / (float)elapsedTicks;
    engine.ApplyTemperatureDeltas(pendingTemperatureDeltas, frac);
    engine.ApplyMoistureDeltas(pendingMoistureDeltas, frac);
    engine.ApplyDivergenceDeltas(pendingDivergenceDeltas, frac);
    engine.AdvanceProvisional(s);
    ApplyAirMoversUnsafe(s, frac);
    ApplyJetTransportUnsafe(s, frac);
    engine.ProjectAndFinalize(s);
    engine.ApplyWallLeakage(num);
    CompleteJetTransportUnsafe(s);
}
```

注意：`ApplyDivergenceDeltas` 是**覆盖写**（先 `Array.Clear(externalDivergence)` 再赋值），不是累加。

### 2.3 每 tick 的调度（`MapComponentTick`）

```csharp
EnsureEngine();
gasManager.CompleteScheduledBatchIfReady(); gasManager.TickWorkers(engine);
CompleteFinishedPatchSimulation(); CompleteFinishedSimulation();   // 回写上一轮异步结果
bool flag = Gen.IsHashIntervalTick(map, AtmosphereOverhaulSettings.SimulationIntervalTicks);
TickAcousticLayer(num); TickTemporalPatch(num, flag);
if (Gen.IsHashIntervalTick(map, 250)) RefreshWallLeak();
if (Gen.IsHashIntervalTick(map, settings.MistPawnEffectIntervalTicks)) ApplyWaterMistPawnEffects();
windRoofs/plantSway/windLoad/windPassables/windTransport 各自 Tick;
TickLocalForcings();
if ((flag || coarseSimulationPending || explosionSimulationRequested) && !SimulationRunning) {
    RefreshRuntimeSettings();
    if (masksDirty) RebuildObstacleMask();
    PrepareTemperatureSources();
    StartSimulationTask();          // 后台线程
}
gasManager.TickGasScheduler(engine, settings, interval, num, flag);
```

模拟是**异步**的：`StartSimulationTask()` 起 Task，`CompleteFinishedSimulation()` 在主线程回写。

### 2.4 压力求解

`Project(iterations, acceleration, settings)`：

1. 清 `Pressure.Read/Write`、`divergence`，重置 `NozzlePump.Rate`
2. 散度（内部格，`Parallel.For(1, Height-1)`）：
   ```csharp
   divergence[i] = (-0.5*(vx[R]*openR - vx[L]*openL + vz[U]*openU - vz[D]*openD) + externalDivergence[i]) * ObstacleMask[i];
   ```
3. 分支：
   - **不可压**：`iterations` 次加权 Jacobi + `SolveNozzlePressureEdges()` + `PinSkyPressure()` + Swap
   - **可压缩**：`SolveCompressible(iterations, acceleration, settings)`
4. 压力梯度修正速度（面加权、再乘开度）
5. `CancelWallNormalOneSidedGradient`（可压缩）→ `StoreCompressiblePressure` → `EndChoke`

可压缩方程（`CompressibleSweepPass`/`CompressibleSorPass`）：

```
alpha*(p - pn) + beta*p - ∇·(open ∇p) = rhs
alpha = 1.2/(dt²·h·1e5)
beta  = (RoofMask<=0.5) ? CompressibleSkyCoupling : 0
rhs   = (externalDivergence*ObstacleMask - div(u)) / h
```

### 2.5 绝热关系（`StoreCompressiblePressure`）

```csharp
num2 = acceleration * 1.2f / dt                    // solver → Pa
stored[i] = Clamp(read[i] * num2, -Ambient, 19*max(Ambient,1e5))
num9  = (newStored - oldStored - 1e5*thermalStep - SourceRisePa) / (Ambient + oldStored)
num10 = Clamp(1 + num9, 0.5, 2)
ΔT    = T_K * (num10^(0.4*AdiabaticFactor*min(1, p_abs/1e5)) - 1)
```

即 `T ∝ p^((γ-1)/γ)` 的离散版，指数 `0.4 × CompressibleAdiabaticFactor`（默认 1）。

### 2.6 热压耦合的两条互斥路径

**路径 A（可压缩，默认）**：`ApplyTemperatureDeltas` 里：

```csharp
if (compressibleActive && RoofMask[i] > 0.5f)
    NoteThermalExpansion(i, delta * scale);
AddTemperatureDelta(i, delta * scale);
```

`NoteThermalExpansion` 累 `ΔT/T_K × p/p0` → `AddThermalExpansionSources` → `externalDivergence`。**只有有顶格触发**。

**路径 B（不可压，`PreparePendingFieldDeltasUnsafe`）**：

```csharp
bool roofed = RoofMask[i] > 0.5f;
if (roofed && engine.CompressibleActive) continue;          // 可压缩模式下有顶格跳过
float excess = T - (roofed ? outdoorT : meanOutdoorT);
float delta  = excess * (roofed ? ConfinedThermalPressureGain : -UnroofedThermalPressureLoss);
delta = Clamp(delta, -160, 160);
pendingDivergenceDeltas[i] += delta;
// 无顶格的散度源最后做零均值化，保证不引入净质量
```

### 2.7 相变核（`AtmosphereThermodynamicKernel.Advance`）

六步：水面蒸发 → 雾滴蒸发 → 过饱和凝结 → 降水（雾超 `MaxMistDensity`）→ 露天雾消散 → 数值下限。

`VaporCapacity(T) = max(MinimumVaporCapacity, VaporCapacityBase * exp(17.6*T/(243+T)))`（Magnus-Tetens）。

潜热预算 `MaxLatentHeatTemperatureDeltaCelsius = 4` ℃/步。

### 2.8 参数默认值（XML `AtmosphereTuning.xml` + 代码常量）

| 参数 | 默认 |
|---|---|
| `baseIntervalTicks` | 30 |
| `simulationIntervalTicks` | 6 |
| `TimeStep` | 1（按 interval 缩放） |
| `PressureIterations` | 16（缩放后 ≥4） |
| `PressureAccelerationScale` | 2 |
| `CompressiblePressure` | 1（开） |
| `CompressibleSkyCoupling` | 0.2 |
| `CompressibleSolver` | 1（SOR） |
| `CompressibleMinSweeps/MaxSweeps` | 3 / 120 |
| `CompressibleSolveTolerance` | 0.1 |
| `CompressibleAdiabaticFactor` | 1 |
| `MaxWindSpeed` | 120 m/s |
| `CoriolisStrength` | 0.08 |
| `MinLatitudeFactor` | 0.2 |
| `OxygenPercent` | 21 |
| `OutdoorOxygenRelaxation` | **0**（户外氧不松弛） |
| `GroundSkyRadiativeTemperatureCelsius` | -25 |
| `GroundOutdoorCouplingRate` | 0.05 |
| `GroundAirConductionRate` | 0.15 |
| `MaxLatentHeatTemperatureDeltaCelsius` | 4 |
| `PatchSafeDivergence` | 20 |
| `PatchSafeTemperatureStep` | 80 |
| `PatchSafeVelocityImpulse` | 12 |
| `ConfinedThermalPressureGain` | 0.030 |
| `UnroofedThermalPressureLoss` | 0.10 |
| `ThermalPressureTemperatureThreshold` | 0.1 |
| `VacuumGasDissipationPerTickFactor` | 25 |
| `HeatEqualizationMultiplier` | 1.25 |

`ScaleForInterval` 关键行为：

```csharp
ratio = max(exactTail ? 1/sourceInterval : 0.1, interval/sourceInterval);
TimeStep = source.TimeStep * ratio;
if (CompressiblePressure > 0) EnableTemporalPatches = 0;      // 可压缩模式强制关 temporal patch
PressureIterations = max(4, round(source.PressureIterations * (SOR && ratio<=0.45 ? 1.5 : 1) ...));
// 各种 rate 用 ScaleProbability(1-(1-r)^ratio) / ScaleEulerRate(...) 重算
```

### 2.9 房间温度写钩子

```csharp
RoomTemperatureWriteHook.MaxRoomCells = 3000;
RoomTemperatureWriteHook.MaxDeltaKelvin = 200f;

OnSet(tracker, value):
  if (suppressDepth>0 || ProgramState!=2 || placingGravship) return;
  if (map==null || room.UsesOutdoorTemperature || room.CellCount > 3000) return;
  comp.ApplyRoomTemperatureWrite(room, value, 200f);
```

`ApplyRoomTemperatureWrite`：`Δ = Clamp(value - 房间平均权威温度, -200, 200)`，**给房间每一格加同一个 Δ**，并 `RecordImpulse`。不是设为目标值。

`GenTemperature.EqualizeTemperaturesThroughBuilding` 在 AO 地图上被 Prefix 直接返回 false —— 原版通风被禁用。

---

## 三、世界模式（TRiSK 三维 GCM）

### 3.1 网格

`IcosahedralAtmosphereMeshV2.Build(frequency, 6371000f)`，`frequency` 范围 4–81，实际用 **81** → `10·81²+2 = 65,612` 列。

字段：`CellCenterX/Y/Z`、`CellAreaSquareMeters`（Voronoi）、`CellEdgeOffsets/Ids/Signs`、`EdgeCellA/B`、`EdgeNormalX/Y/Z`、`PrimalEdgeDistanceMeters`、`DualEdgeLengthMeters`。

`MeshDualV2` 提供对偶网格：`VertexCount`、`VertexX/Y/Z`、`VertexArea`、`VertexEdges`、`VertexEdgeSigns`、`VertexCells`、`KiteArea`。

### 3.2 垂直坐标

`HybridPressureCoordinateV2`：`p = A + B·p_s`。

`GlobalAtmosphereV2Constants`：

```csharp
LayerCount = 8;  InterfaceCount = 9;
PlanetRadiusMeters = 6371000;
ModelTopPressurePascals = 5000;      // 50 hPa
GasConstantDryAir = 287.05; SpecificHeatDryAir = 1004; SpecificHeatConstantVolumeDryAir = 716.95;
GravityMetersPerSecondSquared = 9.80665;
EarthRotationRadiansPerSecond = 7.292116E-05;
InitialInterfaceB = <9 floats>;
```

`CreateInitialFixedTop()`：`A[i] = 5000 * (1 - B[i])`。

### 3.3 状态量

`GlobalAtmosphereV2State` 仅 7 个成员：

```csharp
float[] SurfacePressurePascals;          // 逐列
float[] PotentialTemperatureKelvin;      // [layer*N + cell]
float[] EdgeNormalWindMetersPerSecond;   // [layer*E + edge]
double ElapsedSeconds; int FrameTick;
int LayerCellIndex(int columnIndex, int layerIndex);
```

温度、密度、垂直速度全部诊断。

### 3.4 方程（`EvaluateRightHandSide`）

```csharp
HydrostaticReconstructionV2.Compute(...);                 // 由 ps、θ 重建 Φ、T、p
pressureDepthPascals[i] = ps[i] - 5000;
CompatibleHorizontalOperatorsV2.ComputeEdgeGradient(Mesh, pressureDepth, pressureDepthGradient);
// 动量：气压梯度力
windTendency[e] += -∇Φ_e - 0.5*(α_A + α_B)*B_j*∇(p_s - p_top)_e;   // α = 287.05*T/p
// 质量通量散度
edgeScratch[e] = u[e] * ΔB_j * Δp_e;  ComputeDivergence(...) → HMD
// 连续方程
ps_tendency[i] = -Σ_j HMD[j][i];
// 热力学
thetaContentTendency = -∇·(u θ Δp) - ∂(ω θ)/∂η;   // MUSCL/minmod 边重建
```

`VerticalMassFlux[k+1] = Σ_{j≤k} [-ΔB_j*ps_t - HMD_j]`。

### 3.5 TRiSK + CLUST25（决定性证据）

`VectorInvariantOperatorsV2` 里直接写着：

```csharp
internal const float ClustUpwindFraction = 0.25f;   // CLUST25
this.VertexCoriolis[index] = -0.00014584232121706009 * this.Dual.VertexY[index];   // -2Ω·y
throw new InvalidOperationException("TRiSK: no vertex between consecutive edges of cell " + c);
throw new InvalidOperationException("CLUST: downwind vertex is not a neighbour of the upwind vertex, edge " + e);
```

算子：
- `GradDiv`：格心散度（边加权/面积）→ 边梯度（格心差/原边长度）
- `CurlCurl`：顶点涡度（边加权/顶点面积）→ 边旋度（顶点差/对偶边长）
- `Viscosity(efoldHours, gradDiv) = 1/(efoldHours*3600*λmax²)`，`λmax` 由幂迭代（100 次，种子 7）求
- `PackGpu` 把 14V+17E+4 words 打包进 `double[]` 给 GPU

位涡与 CLUST：

```csharp
viZeta[vertex] = (Σ sign*primalLen*u) / VertexArea;
viQ[vertex]    = (viZeta + VertexCoriolis) / max(1, Σ KiteFraction*ΔB*Δp);
// 中心权重
num6 = CentredRightWeight*q[right] + (1-CentredRightWeight)*q[left];
// CLUST：4 个顶点（upwind 侧 + 邻接）
num8 = Σ_{k=0..3} ClustWeights[8*edge + (u_along>=0?0:4) + k] * q[ClustVertices[...]];
// 混合系数
num9 = |u_along| / sqrt(u_norm² + u_along² + 1e-20);
viQHat[edge] = num9*num8 + (1-num9)*num6;
// 动量倾向
windTendency += PVFlux - ∇K + hyperviscosity
```

`ClustWeights` 构造：`num14 = 0.25 + 0.75*num12`（upwind 自身），邻居 `0.25*num18`，downwind 额外 `0.75*(1-num12)`。

**这是 Weller 2012 的 CLUST25 的直接实现**：混合系数随「流向与边的夹角」变化——流向平行于边（`u_along` 大）→ CLUST 迎风；垂直于边（`u_along≈0`）→ 中心线性。

### 3.6 时间积分

`StepImplicit`：

```csharp
if (UseArs232) { StepArs232(dt, ...); return; }
EvaluateRightHandSide(State, options, rhs1...);
// CFL 检查：水平 > 0.9 或垂直 > 0.9 → throw
waves.Freeze(); waves.FillRhs(dt, rhs1); waves.Solve(dt, uploadFrozen:true);
waves.Store(predictorState);
EvaluateRightHandSide(predictorState, options, rhs2...);
waves.CorrectorRhs(dt, rhs1, rhs2); waves.Solve(0.5*dt, uploadFrozen:false);
waves.Store(correctorState);
waves.PrepareMoisture(dt);
Copy(correctorState → State); State.ElapsedSeconds += dt; Moisture.Commit();
ReconstructHydrostaticState();
```

`StepArs232` 用 ARS(2,3,2) 系数：`0.2928932188134525`（1−√2/2）、`-0.9428090415820634`（−2√2/3）、`0.7071067811865475`（√2/2）。

`AutodriveSession` 默认：`allowedStep = 300`、`maximumInternalSeconds = 300`、`keHistory = new double[48]`、`AllowedStep ∈ {150,180,240,300}`。

### 3.7 湿物理（`MoistPhysics`）

方法：`Saturation`、`CloudCoverage`、`CloudResidenceSeconds`、`AutoconversionSeconds`、`CloudSedimentationFraction`、`PrepareStages`、`ApplySources`、`Convect`、`SurfaceWindEvaporationMultiplier`、`Adjust`、`Commit`、`AccumulateLayerFluxes`。

`PrepareStages(State, predictor, 0.7071, arsStage, 0.2929, corrector, dt, ...)` —— 湿过程与 ARS 三段同步。

### 3.8 其他物理

| 过程 | 方法 |
|---|---|
| 干对流调整 | `ApplyDryConvectiveAdjustment` |
| 表面通量 | `ApplySurfaceFlux`、`EvaporateFromSurface`、`SurfaceDragMultiplier` |
| 大气辐射 | `PrepareRadiation`、`ApplyAtmosphericRadiation`、`VaporAbsorptance` |
| 雪 | `WriteSnow/ReadSnow`、`InitializeSnowFromGround`、`UpdateSnow`、`SnowLand` |
| 海冰 | `MeltIce`、`RefreezeLeads`、`EvaluateIceTendency`、`ApplyIceTendency`、`ApplyBasalGrowth`、`RaiseUnderIceOceanToFreezing` |
| 海洋环流 | `BuildOceanCirculation`、`OceanCurrentAt`、`AdvectOceanHeat`、`TransportOceanHeat`、`SolveBiCgStab` |
| 次网格气旋 | `SubgridCycloneLayer`、`SubgridCyclone`、`FrontDetection` |
| 气体 | `GasTracers`、`PassiveTracerPhysics` |
| 自动驱动 | `ApplyAutodriveSources`、`AutodriveTargetPotentialTemperature` |
| 能量监督 | `ClimateSupervisor.ObserveHour` |
| 地形动力 | `ApplyDynamicalTerrain`、`RebaseToTerrain`、`ResetSurfaceToGeography` |

### 3.9 稳定性与守恒

`AdvanceHour`：

```csharp
double num3 = (Sum(Vapor)+Sum(Cloud)+Precipitated-Evaporated-InitialWater) / InitialWater;
if (!Finite(num3+meanT+ke+mass) || |num3| > 1E-08 || |mass/initialMass - 1| > 1E-06)
    throw new InvalidOperationException("Autodrive hourly inventory guard failed; no frame may be published.");
PhysicalDefectJ    = DiluteEnergy() - initialEnergy - LatentHeat*Evaporated - cumulative.Total;
HourDynamicsDefectJ = MeasureBudgetEnergy() - hourEnergy - hourWork.Total - (LatentDryWorkJ - hourLatent);
healthy = hourRetries==0 && maxCfl<0.6 && (ke < 1.3*keYesterdayMax) && |HourDynamicsDefectJ| < 0.05*max(ke,lastHourKe);
```

失败 → `rollback.Restore(Core)` → 降步长 150 重试最多 3 次 → 再失败抛异常（不发布帧）。

`ClimateSupervisor`：`FilteredTemperature/EnergyK/KeRatio`、`AnnualEnergyK`、`RequestedThermalKPerDay`、`RequestedDragMultiplier`、`WarmDays/ColdDays/LimitDays`。每小时 `ObserveHour(meanK, (energy-initialEnergy)/(initialMass*1004), ke/initialKe, healthy, seconds)`。

### 3.10 帧调度

```csharp
GlobalFrameIntervalTicks = 2500;   ForecastLeadFrames = 24;   TicksPerHour = 2500;
FrameStartTick(tick) = tick - tick % 2500;
```

`GlobalSimulationScheduler.Pump`：

```csharp
if (buildTask != null) return CompleteTaskIfReady(...);
if (Paused || WorkerFailure != null) return false;
int reserve = forecast.CountFutureFrames(frameTick, 23);
if (reserve >= 23) return false;
if (flag && reserve != 0) {
    double interval = 2500.0 / TargetSolverTicksPerSecond(reserve, actualTicksPerSecond);
    if (realTime - lastTaskStartRealTime < interval) return false;
}
StartFrame(...);
```

`FillFrame`：`new AutodriveSession(81, "1.6/Native/WebGpu", BoundedThermal, frameTick, checkpoint, nativeDynamics:true, latent, drag, hypervisc)`，逐小时 `session.AdvanceHour()`，然后写 7 个模式层 + 第 8 槽位 10 m 诊断。

---

## 四、两模式的接口

| 量 | 世界 → 本地 | 路径 |
|---|---|---|
| 温度 | ✅ | `TileTemperaturesComp.GetOutdoorTemp` Postfix → `mapTemperature.OutdoorTemp` → `GetOutdoorTemperatureSafe` → `settings.AmbientTemperatureCelsius` |
| 气压 | ✅ | `ComputeAmbientSurfacePressure()` → `AmbientSurfacePressurePa` → `ApplyMapAmbientOverrides` 缩放 `GroundOutdoorCouplingRate`/`GroundAirConductionRate` |
| 真空 | ✅ | `VacuumEnvironment.IsVacuumMap` → 氧 80%、天空辐射 -75℃、RH 0 |
| 风 | ⚠️ 间接 | `GlobalMapWeather.Choose(mm, celsius, rh)` → 原版 `WeatherDef` → `windManager.WindSpeed` → `VanillaWeatherDerivation.AmbientWind = speed*10` + 伪随机正弦朝向 |
| 湿度/氧/降水 | ❌ | 无通道 |

| 量 | 本地 → 世界 | 路径 |
|---|---|---|
| 热量 | ✅ | `SyncClimateDevices` → `SyncAtmosphericHeaters` → `ThermostatControl` / `LowestLayerHeatingWatts` |
| 气体 | ✅ | `ConfigureGasSources` |
| 海冰 | ✅（世界自身） | `SyncSeaIceBiomes` 替换 biome |

`AtmosphereInterface.SetAmbientWind` **没有任何内部调用者**，是纯外部 API。

---

## 五、源码级问题清单

### 5.1 `TryGetPressurePascals` 单位错误（严重）

读：`AtmosphereColorUtility.PressurePascalsFromSolver(solver) = 100000 + solver*1000`（`CurrentSolverPressureToPascals()` 恒返回 1000）。

而 `SampleSnapshot.Pressure = Pressure.Snapshot[i]`，可压缩模式下 `Pressure = StoredPressurePa / SolverPascalsPerUnit`，其中：

```csharp
SolverPascalsPerUnit = PressureAccelerationScale*1.2 / TimeStep;
```

默认 `interval=6` → `TimeStep = 1*0.2 = 0.2`、`accel=2` → `SolverPascalsPerUnit = 12`。

于是：`TryGetPressurePascals` 返回 `100000 + (stored/12)*1000 = 100000 + stored*83.3`。

**压力偏差被放大 83 倍。** 1 kPa 的偏差读出来是 83 kPa。

内部路径（`AtmosphereMapComponent.TryGetStaticPressurePascals`）是 `Ambient + StoredPressureAt(i)`，正确。`FireOxygen.Factor` 用的正是后者。

### 5.2 `AddPressureDelta` 与 `SolverPascalsPerUnit` 不一致

写：`EnqueuePressureExternalDelta(delta)` → `incomingPressureDeltas += delta` → drain 时 `AddStoredPressurePa(i, delta * 1000f)`。

**读用 12、写用 1000**，同一个参数两个换算系数。写 1 单位 = +1000 Pa，远大于读的 1 单位 = 12 Pa。

### 5.3 世界帧若干字段恒 0

`FillFrame` 里 `LowerMassAnomaly`、`LowerVorticity`、`UpperPressure`、`FrontStrength`、`SubgridEddyEnergy` 全部写 0。

### 5.4 降水不回灌土壤

`LocalRainfall` 无任何 `find_usages`；`SoilWaterMapComponent.AddWater` 只有 `CompIrrigator.ApplyDoses` 与 `JobDriver_WaterPlants` 两个调用者。

### 5.5 `ApplyOpenBoundarySponge` 空实现

### 5.6 暂停时全球模式不算

`Pump` 里 `if (Paused || WorkerFailure != null) return false;` —— 与 Steam 页"暂停时仍算"不符。

### 5.7 世界风不下传

见第四节。

---

## 六、对 patch 的直接影响

1. **压力读写不对称**：不要用 `TryGetPressurePascals`，改用 `AtmosphereMapComponent.TryGetStaticPressurePascals`（internal，反射）或 `TryGetSample` + 自己按 `SolverPascalsPerUnit` 换算。写压力用 `AddPressureDelta` 时按 `1 单位 = 1000 Pa` 估算，与读不等价。
2. **可压缩模式默认开**，`EnableTemporalPatches` 被强制置 0 → temporal patch 体系不可用，只能走 `AddDeltas`/`AddStoredPressurePa`。
3. **`ApplyDivergenceDeltas` 是覆盖写**，同一子步多次调用只保留最后一次。
4. **`ApplyRoomTemperatureWrite` 是整房间加 Δ**，不是设目标值；>3000 格静默忽略。
5. **`OutdoorOxygenRelaxation = 0`**：户外氧不自动回 21%，缺氧会真实发生。
6. **引擎尺寸 = 地图尺寸**：全图扫描 350×350 = 122,500 格，patch 每步刷全图必卡。
7. **世界模式只给本地 3 个量**：想做「按世界天气调本地风/湿度」需要自己新建通道。
8. **参数每步现读**：`TimeStep`、`PressureIterations`、各 rate 都随 `intervalTicks` 缩放，任何硬编码都会错。

---

## 七、仍未读透

- `GlobalAtmosphereV2DryCore` 的 `WaveSolve` 内部（半隐式重力波求解器的具体迭代格式）
- `ApplyAtmosphericRadiation` 的 `ArsGamma/ArsDelta` 系数含义
- `SubgridCycloneLayer` 的气旋生成/移动/眼墙逻辑
- `TemporalPatchManager` 的 patch 域管理与安全限幅
- `AtmosphereGasManager` 的气体调度与 worker
- 141 个 Harmony patch 的逐条清单与优先级

---

## 八、GPU 实现与反编译一致性核查（2026-11-11 补）

### 8.1 反编译一致性：dotPeek 与 MCP 结果相同，硬问题不是工具造成的

用同一组方法做三方对读（MCP = ILSpy 系 decompiler，dotPeek = 用户提供的 `f:\RimworldCodeIL\AtmosphereOverhaul\`）：

| 方法 | MCP 输出 | dotPeek 行号 | 结论 |
|---|---|---|---|
| `AtmosphereInterface.TryGetPressurePascals` | `pascals = PressurePascalsFromSolver(sample.Pressure)` | `RimWorld/AtmosphereInterface.cs:122` | 逐字一致 |
| `LocalAtmosphereEngine.SolverPascalsPerUnit` | `!compressibleActive ? 1000f : PressureAccelerationScale*1.2f/max(0.0001f, TimeStep)` | `Simulation/LocalAtmosphereEngine.cs:2118-2121` | 逐字一致 |
| `LocalAtmosphereEngine.ApplyOpenBoundarySponge` | 空方法体 | `Simulation/LocalAtmosphereEngine.cs:4343-4345` | 逐字一致 |

dotPeek 输出里的 `//IL_xxxx: Unknown result type` 只是泛型/嵌套类型的显示注释，不影响逻辑。**所以 5.1–5.6 的硬问题是代码本身的行为，不是反编译丢失。**

唯一无法用托管反编译验证的是 native 与 GPU 部分（见 8.2–8.5）。

### 8.2 资产与程序集布局

- `1.7/AssetBundles/`：只有 `aoplants_win`（31,409 B）、`aoworld_mac`（17,905 B）、`aoworld_win`（19,937 B），文件头 `UnityFS ... 2022.3.35f1`。是 Unity AssetBundle，不是 shader，与 GPU 无关。
- 全 mod 目录没有任何 `.wgsl` / `.hlsl` / `.spv` / `.metal` / `.shader` 文件。
- `1.7/Assemblies/` 只有主 DLL、Perf81 DLL 及 pdb，**没有 `Native/` 目录**。
- 所有原生库在 `1.6/Native/`：`AtmosphereOverhaul.Burst.dll`（23,040 B）、`wgpu_native.dll`（win-x64，8,095,744 B）等六平台。
- `GpuAutodriveGlobalClimateEngine.FillFrame` 硬编码 `Path.Combine(root, "1.6", "Native", "WebGpu")` —— **1.7 的 GPU/Burst 运行时依赖磁盘上的 1.6/Native 目录**。
- `build-manifest.json`：Burst 1.8.18、`FloatMode: Strict`、`Fingerprint 635244030`。

### 8.3 三个嵌入 WGSL

`AtmosphereOverhaul.Perf81.dll` 的 `GetManifestResourceNames()` 恰好三项：

| 资源 | 大小 | 入口 | 对应 C# |
|---|---|---|---|
| `WebGpu.Waves.wgsl` | 8,309 B | `PressureColumn`、`PressureEdge`、`Transport`、`VectorOp`、`ReducePartial`、`ReduceFinal`、`FinishEarly` | `GlobalAtmosphereV2DryCore.WaveSolve`（:3691） |
| `WebGpu.DynamicsVI.wgsl` | 13,145 B | `CellVelocity`、`EdgeFlux`、`MassColumns`、`CellTendencies`、`WindFinish` | `TryVectorInvariantMomentum`（:060000D6 / `VectorInvariantV2`） |
| `WebGpu.Surface.wgsl` | 6,575 B | `SurfaceStep`（内含 `advanceGround`、`cloudOptics`） | `UpdateSlowSurface`（:2171）、`ApplySurfaceFlux` |

`WebGpuBackend.Shader(name)` 用 `Assembly.GetExecutingAssembly().GetManifestResourceStream("WebGpu." + name)` 读取，缺失即抛 `FileNotFoundException("Embedded shader missing: ...")`。

### 8.4 启用链路

```
GpuAutodriveGlobalClimateEngine.FillFrame
  → new AutodriveSession(81, "<mod>/1.6/Native/WebGpu", BoundedThermal, tick, checkpoint, nativeDynamics: true, ...)
     → Core.EnableGpuWaves(gpuDirectory)        // GpuWaveDevice ctor → ManagedBackendFactory(cellCount, edgeCount, mi, geo, 5000.0, 287.05, 287.05/1004)
     → Core.EnableGpuDynamics(gpuDirectory)     // VectorInvariantMomentum 时走 VectorInvariant.PackGpu(...) + "DynamicsVI.hlsl" + "GPU-vi-trisk-clust25-v1"
        → WebGpuBackend.InitializeDynamics(...)  // 非 DynamicsVI.hlsl 直接 NotSupportedException
  → wgpu_native: DeviceCreateCommandEncoder / BeginComputePass / SetPipeline / DispatchWorkgroups
```

结论：**世界模式默认全程 GPU**（`nativeDynamics: true` 且 `gpuDirectory` 非 null），不是可选装饰。GPU 覆盖动力学、波解、地表；湿物理、辐射、对流、海冰、气旋仍在 C#。

### 8.5 只在 GPU 上、C# 里看不到的实现

1. **`Waves.wgsl` 把时间步写死为 300 s**
   - `PressureColumn`：`let td = F(1u,i) * (300.0 * Q(P.source, P.n+i) - Db(l)*F(0u,i)*dp) / (Db(l)*depth) + ...`
   - `Transport`：`... / 300.0`
   - `P.a`（真正的 dt）只用于 `VectorOp` 的 mode 0/5/7 系数。
   - 而 `AutodriveSession.AdvanceHour` 的 `dt = min(allowedStep, ...)`，`allowedStep` 默认 300，CFL `> 0.6` 或 `InvalidOperationException` 时降到 **150**；`WaveSolve.Solve` 在 `gpuWaveDevice != null` 时仍走 GPU。
   - 即 **dt=150 时压力/位温增量仍按 300 s 计算，约 2 倍偏差**，CPU 路径用参数 `dt` 正确。这是「shader 里有、C# 里没有」的实证。

2. **`DynamicsVI.wgsl` 的 `CellTendencies` 内联了超粘性耗散加热**
   ```wgsl
   power += -Len(e) * Dist(e) * scratch[FluxIndex(0u,l,e)] * Hyper(l,e);
   outputData[P.n + i] = -Divergence(1u,l,c) - (VTheta(l+1,c) - VTheta(l,c))
       + (0.5 * power / Area(c)) * InverseCp() / exner;
   ```
   C# 侧对应 `AddVectorInvariantHeating`，但 GPU 把它直接内联进 θ 倾向，且用 `exner = inputData[...] / Theta(l,c)` 归一。

3. **`Surface.wgsl` 的 `advanceGround` 用稳定/不稳定分层交换系数**
   ```wgsl
   let exchange = clamp(select(stableK, unstableK, difference > 0.0) * difference, -100.0, 400.0);
   let outgoing = emissivity * SIGMA * pow(max(180.0, skin), 4.0);
   ...
   nextSkin = clamp(nextSkin, 180.0, 340.0)
   ```
   skin/slab 双层热容 + 传导 split 与 C# `UpdateSlowSurface` 是两份独立实现，GPU 版把温度限幅 `180–340 K` 写死在 shader。

### 8.6 对 patch 的额外含义

- 1.7 的 GPU 依赖 `1.6/Native/`，若用户只保留 1.7 目录会直接抛 `"The WebGPU backend is not loaded."`。
- 任何「世界模式温度/风有偏差」的怀疑，需要区分 CPU 路径（正确）与 GPU 路径（dt<300 时偏差）。
- 不要试图通过反射替换 `Waves.wgsl`，它是嵌入资源且管线在 `WebGpuBackend` 构造时编译。

### 8.7 修正「仍未读透」

- `WaveSolve` 已读：`Solve` 在 GPU 时走 `gpuWaveDevice.Solve(...)`，CPU 时走 BiCGStab 迭代；`ArsStageThreeRhs`/`ArsFinalIncrement` 是 ARS(2,3,2) 的 stage-3 与最终增量。
- 新增未读：`SubgridCycloneLayer` 的 `AdvanceHour` 内部、`GasTracers` 的 GPU 路径（目前未见 WGSL，应为 CPU/worker）。

---

## 九、AtmosphereOverhaul.Burst.dll 全面勘查（2026-11-11 补，IDA Pro MCP）

### 9.1 身份

| 项 | 值 |
|---|---|
| 路径 | `1.6/Native/win-x64/AtmosphereOverhaul.Burst.dll` |
| 大小 / 基址 | 23,040 B / `0x180000000`，映像 0x8000 |
| MD5 / SHA256 | `163f9367a35a4152b1c12d39bad6ec61` / `cf5dd114accb334adb50e0f710393bb2b6792b231a036ad1eae10155c7083952` |
| 段 | `.text` 0x5000（rx）、`.rdata` 0x1000（r）、`.pdata` 0x1000（r） |
| 函数 | **22 个**：2 个命名 + 20 个 `sub_*` |
| 导出 | 仅 2 个：`AtmosphereExecuteV1`(0x180001000, 5,976 B)、`burst.initialize`(0x180005B80) |
| 导入 | **零**（纯计算库，不依赖 CRT/系统 DLL） |

`burst.initialize` 只调用两个空桩（`sub_180002770`/`sub_180002780`，均为 `push rbp; mov rbp,rsp; pop rbp; retn`），即 Burst 的 CPU 特性初始化在此平台无事可做。

### 9.2 ABI

```c
void AtmosphereExecuteV1(int opcode, uint* context, int start, int end);
```

- `opcode`：-1 或 0..20，见 9.3。
- `context`：`BurstContext` 的指针，里面是一组 SoA 指针 + 宽度/高度 + **内联的 1184 字节 `BurstSettings`**（`sub_180005B10` 是 memcpy，case 0/12 每次把 settings 拷到栈上 1184 B 局部副本）。
- `start`/`end`：半开行区间 `[start, end)`，只处理内部格（`1..W-2`、`1..H-2`），边界由 C# 的 `EnforceVelocityBoundary`/`ZeroPressureBoundary` 负责。
- 返回：`void`（`case -1` 例外，写 16 字节常量到 `*context`）。

### 9.3 opcode ↔ C# ↔ native 全表

| opcode | C# 方法（`Simulation/LocalAtmosphereEngine.cs`） | native | 作用 |
|---|---|---|---|
| -1 | `BurstBackend.Available` 探测 | 内联 | 写 `{1,0,0,0}` 指纹 |
| 0 | `TryBurstGroundEnergy`(899) | 内联 + `sub_180002790` | 逐格地面能量 |
| 1 | `TryBurstPassiveAdvection`(965) | `sub_180004760` | 被动标量半拉格朗日；实心格**清零** |
| 2 | `TryBurstPassiveDiffusion`(995) | 内联 | 4 邻加权平滑（独立于 Jacobi） |
| 3 | `TryBurstVelocityAdvection`(1019) | `sub_180005500` | 速度半拉格朗日 + 露天 ambient nudge |
| 4 | `TryBurstScalarAdvection`(1057) | `sub_180004F40` | 温度/水汽/雾半拉格朗日；实心格**保留原值** |
| 5 | `TryBurstOxygenAdvection`(1087) | `sub_180004120` | byte 氧半拉格朗日 + 户外 ±1 滞回 |
| 6 | `TryBurstOxygenDiffusion`(1123) | `sub_1800044C0` | byte 氧 + 浮点残差扩散 |
| 7 | `TryBurstDivergence`(1157) | 内联 | 面开度加权散度（SIMD） |
| 8 | `TryBurstJacobi`(1183) | 内联 | 不可压加权 Jacobi |
| 9 | `TryBurstPressureGradient`(1207) | `sub_180004A20` | 压力梯度修正速度 |
| 10 | `TryBurstVelocityDiffusion`(1236) | `sub_1800058B0` | 速度 4 邻扩散 |
| 11 | `TryBurstScalarDiffusion`(1270) | 内联 | 标量扩散 |
| 12 | `TryBurstThermodynamics`(1293) | `sub_180005200` → `sub_180002B20` | 相变核 |
| 13 | `TryBurstCoriolis`(1328) | 内联 | 旋转矩阵 |
| 14 | `TryBurstDamping`(1357) | 内联 | SIMD 衰减 |
| 15 | `TryBurstWindClamp`(1378) | 内联 | 限速 |
| 16 | `TryBurstResidueDiffusion`(1402) | `sub_180004C80` | 爆炸压力残差扩散 |
| 17 | `TryBurstCompressibleSetup`(1430) | `sub_180003900` | 可压缩 alpha/beta/pn/rhs |
| 18 | `TryBurstCompressibleSweep`(1483) | `sub_180003F50` | 可压缩 Jacobi sweep |
| 19 | `TryBurstCompressibleSor`(1521) | `sub_180003D30` | 红黑 SOR |
| 20 | `TryBurstCompressiblePackedSor`(1564) | `sub_180003790` | 预打包 6 float/格的 SOR |

### 9.4 各核算法要点（从反编译读出，与 C# 逐条对应）

- **op1/op4 半拉格朗日**：`x' = x - vx*k`、`z' = z - vz*k`，clamp 到 `[0.5, W-1.5]`（`0x3F000000` 常量），双线性 4 点权重（`xmmword_180006170 = (1,1,·,·)` 用于 `1-frac`）；权重和 `<= 1e-4` 时回退。两者唯一差别是实心格：op1 写 0、op4 复制源值。
- **op3 速度平流**：在 op1 基础上加 `if (ambient != 0 && RoofMask <= 0.5) v += nudge*(ambient - v)`；最后 `* obstacleMask`。
- **op5 氧平流**：实心格写 `ambientOxygen`；露天且 `relaxation > 0` 时，若采样值等于原值则朝 ambient **±1 步**（`v34 = new < ambient ? 1 : -1`），`round_ties_even` 后饱和到 `[0,100]`。
- **op6 氧扩散**：`v = byte + residual`（残差保留小数），4 邻均匀扩散 `v += Σ(nb - v) * diffusion`，clamp 到 `[0,100]` 写 byte、`residual = v - byte`。**这是 byte 场精度补偿，C# 侧对应 `oxygenResidual`**。
- **op9 压力梯度**：`grad = Σ (p_nb - p) * FaceOpen`，`sumOpen <= 0` 时分母取 1，`v = (v - grad/sumOpen * acceleration) * mask`。
- **op13 科里奥利**：`x' = x*cos - y*sin; y' = y*cos + x*sin`，`cos/sin` 来自 `context[418]/[419]`，只对 `mask > 0`。
- **op14 阻尼**：`factor = max(0, 1 - max(0, mask)*k) * src`，SSE 4 路 + 标量尾部两条路径。
- **op15 限速**：`if (maxSq < vx²+vz²) { s = maxSpeed/sqrt; vx *= s; vz *= s; }`。
- **op17 可压缩 setup**：`alpha = ctx+1760` 常量；`beta = (RoofMask <= 0.5) ? ctx+1764 : 0`；`pn = stored * ctx+1768`；`rhs = (externalDiv*mask - div(u)) / ctx+1772`，其中 `div(u) = (0.5*(vx[i]+vx[i+1])*open_R - 0.5*(vx[i-1]+vx[i])*open_L + 同 Z 方向)`。
- **op18 可压缩 sweep**：`denom = open_L + open_C + open_D + open_U + alpha + beta`；`p = (rhs + Σ open*p_nb + alpha*pn) / denom`；`denom <= 0` 写 0。
- **op19 红黑 SOR**：起始列 `2 - ((row & 1) == parity)`，步长 2；`p += (p_new - p) * omega`；每行把 `max|Δp|` 写入 `rowDelta`。
- **op20 打包 SOR**：每格 6 个 float（`w_left, w_down, w_right, w_up, w_self, rhs`），`p = Σ w*p + rhs`，系数已预乘，无除法。
- **op12 热力学**：外层 `sub_180005200` 遍历格，`mask <= 0` 时清 4 场；否则调 `sub_180002B20(T, V, M, vx, vz, P, waterMask, roofed, settings)`，写回 5 个 float（T、V、M、RH、rain）。
- **op0 地面能量**：外层在分派器内联，逐格调 `sub_180002790(groundT, airT, roofed, albedo, thermalRate, minT, maxT, roofAlbedo, roofRate, canopyShade, transpiration, settings, sunPower)`。

### 9.5 `.rdata` 常量表（0x180006000 起）

| 地址 | 值 | 用途 |
|---|---|---|
| `0x180006000` | `(1,1,1,1)` | 向量化填充 |
| `0x180006010` | `1.0f` | 单位 |
| `0x180006014` | `0.25f` | 4 邻平均 |
| `0x180006020` | `(-0.5,-0.5,-0.5,-0.5)` | 中心差分 |
| `0x180006030` | `(-0.5, 0.5, 0, 0)` | 面通量符号 |
| `0x180006040` | `int 1` | 指纹/版本 |
| `0x180006048` | `0x718` = 1816 | `BurstContext` 大小 |
| `0x18000604C` | `0x4A0` = 1184 | `BurstSettings` 大小 |
| `0x180006054` | `0.02f` | 下限 |
| `0x180006058` | `1.5f` | 风速比 |
| `0x18000605C` | `30.0f` | 间隔 |
| `0x180006060` | `15.0f` | 阈值 |
| `0x180006064` | `0.0015f` | 相变下限 |
| `0x180006068` | `double 1.4426950408889634` = log2(e) | `expf` 实现 |
| `0x180006150` | `(0.5, 0.5, 0, 0)` | 面通量 0.5 权重 |
| `0x180006170` | `(1, 1, 0, 0)` | `1 - frac` |

### 9.6 结论

1. **这是 C# 的 Burst 编译产物，不是隐藏实现**：21 个 opcode 与 21 个 `TryBurst*` 严格一一对应，`BurstBackend.Execute(N,...)` 的 N 即 switch 的 opcode。
2. **它是"核"而不是"循环"**：只处理 `[start, end)` 行区间；行调度、掩膜重建、字段 Swap 仍在 C#。Burst 不可用时 C# 有完整回退实现，所以缺失只影响性能，不影响正确性。
3. **与 GPU 路径性质不同**：GPU 的 `Waves.wgsl` 是独立重写并硬编码 300 s 步长；Burst 是 C# 源码直接编译，逻辑与源码严格一致，没有"C# 里看不到的行为"。
4. **只覆盖本地模式**：世界模式（`GlobalAtmosphereV2DryCore`、TRiSK、ARS232、WebGPU）不在这个 DLL 里，那些在 `AtmosphereOverhaul.Perf81.dll` + 三个 WGSL 里。
5. **1.7 同样依赖它**：`1.7/Assemblies/` 没有原生库，`BurstBackend` 按平台解析 `1.6/Native/<rid>/AtmosphereOverhaul.Burst.dll`；`build-manifest.json` 记录 Burst 1.8.18、`FloatMode: Strict`、`Fingerprint 635244030`。

### 9.7 对 patch 的意义

- `usePrecompiledBurst = true`（默认）意味着**热力学、压力 Jacobi、平流、扩散、科里奥利、限速、可压缩求解都走这份二进制**。若 patch 想修改这些行为，改 C# 对应方法在默认配置下不会生效；要么在设置里关掉 Burst，要么改 native（不现实）。
- 但 patch 现在做的事（`AddDeltas`、`AddPressureDelta`、`ApplyRoomTemperatureWrite`）都发生在 **drain / 主线程装配阶段**，不经过 Burst 核，因此不受影响。
- 若怀疑"本地物理结果与 C# 源码不符"，优先查 Burst 路径；若怀疑"世界模式结果异常"，查 WGSL 的 300 s 硬编码。

### 9.8 wgpu_native.dll 勘察（同批）

| 项 | 值 |
|---|---|
| 路径 | `1.6/Native/WebGpu/win-x64/wgpu_native.dll` |
| 大小 / 基址 | 8,095,744 B / `0x180000000` |
| MD5 | `b1334a1ff018f69fcb9437d699f0c35c` |
| 函数 / 导出 | 21,207 / 250 个 `wgpu*` C API |
| 来源 | `C:\Users\runneradmin\.cargo\git\checkouts\wgpu-53e70f8674b08dd4\87576b7\...`（wgpu git commit `87576b7`，CI 构建） |
| 后端 | `d3dcompiler_47.D3DCompile`（DX12）、`OPENGL32.wgl*`、`user32/gdi32`（surface）；`vulkan-1.dll` 动态加载 |
| AO 定制 | **无**：搜 `atmosphere|trisk|clust|autodrive|overhaul` 只命中 Vulkan 扩展名 |

结论：标准 wgpu-native 运行时，负责编译 WGSL、分配 buffer、dispatch compute pass、回读结果。AO 的物理在三个嵌入 WGSL 里，不在这个 DLL 中；上一轮发现的 `300.0` 硬编码发生在 WGSL 源码层，与此运行时无关。

---

## 十、GPU 计算层（WGSL）专章

WGSL 的逐行分析见独立文档 [AO-GPU-WGSL分析.md](AO-GPU-WGSL分析.md)（399 行），内容包括：

- `Waves.wgsl`：19 个 work 切片、7 个 entry、`scalar[0]` 状态机（0/1/2/-1/-2/-3）、BiCGStab 的 α/β/ρ/ω 来源，以及第 42/84 行的 **300 秒硬编码**。
- `DynamicsVI.wgsl`：`meshData`/`geo`/`dg`/`inputData`/`scratch` 的完整偏移表、5 个 entry（`CellVelocity`/`EdgeFlux`/`MassColumns`/`CellTendencies`/`WindFinish`）、CLUST25 的 `beta = |ut|/sqrt(u²+ut²)`、超粘性耗散转热项。
- `Surface.wgsl`：`advanceGround` 的 skin/slab 双层解、`cloudOptics` 的 8 层云光学、`SurfaceStep` 的太阳高度角与海冰-lead 混合、三套写死的 `unstableK/stableK`。
- 三层实现对照表（Burst / GPU / 托管）与四条发现的问题。

---

## 十一、自带兼容补丁（1.7/Compat）

DBH / DCH / VGE 三个兼容包的完整分析见独立文档 [AO-Compat设计分析.md](AO-Compat设计分析.md)（501 行），要点：

- DCH 不改建筑，用 `Prefix + Transpiler + Finalizer` 劫持 `GenTemperature.ControlTemperatureTempChange`、`Room.Temperature` setter、`GenTemperature.PushHeat` 三个原版入口。
- 加热：单格 `TemperaturePerTick`，2 K 死区 + 1 K 带宽，**无风**。
- 制冷：两格平分、**15 m/s 出风**（速度钳制式趋近，非冲量）、抽湿到 40% RH 并产冷凝水、室外机 1.25 倍排热。
- 吊扇：圆盘内切向 + 30% 径向的螺旋风，`blend = 0.05`。
- `ToleranceK`/`BandK`/`OutletWind`/`HotSideFactor` 是文档性常量，代码里全用字面量。
