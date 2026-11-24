# AO 公共接口迁移分析

> 生成时间：2026-11-24
> 依据版本：`F:/SteamLibrary/steamapps/workshop/content/294100/3830470974/1.7/Assemblies/AtmosphereOverhaul.dll`（2026-11-23 14:17）
> 反编译源码：`f:/RimworldCodeIL/AtmosphereOverhaul/`（2026-11-24 11:19 反编译，与上述 DLL 同版本）
> 状态：**分析完成，未修改任何代码**

---

## 0. 结论摘要

1. AO 作者在 [Steam 讨论页](https://steamcommunity.com/workshop/filedetails/discussion/3838400596/567051865431393584/) 公布了约 60 个公共接口，覆盖读场、写增量、连续源、区域、温控 hook、护盾、pawn 气氧、通风口、事件。
2. 作者承诺：迁移完成前，「旧写法 → 公共替代」表左列的内部成员**名字、签名、含义都不变**。因此可以渐进迁移，不必一次性重写。
3. 约 20 项现有内部依赖可迁移到公共接口；**5 类功能无法靠这些接口等价实现**（见 §5），必须保留内部 patch。
4. 迁移中有 4 处必须注意的语义陷阱（见 §3.3）：`StepPreparing` 时序、`HoldCell` 与 `mask=0` 互斥、`LocalAtmosphereSample` 单位、`SetContinuousSource` 移动语义。
5. 本轮复核另外发现现状实现有 3 处缺陷（见 §7），与迁移独立，但迁移时可顺带消除。

---

## 1. 本项目当前对 AO 的依赖形态

- **编译期不引用 AO 程序集**。`Source/JeremieAtmosphereOverhaulPatch/JeremieAtmosphereOverhaulPatch.csproj:49-67` 只引用 0Harmony、Assembly-CSharp、UnityStubs、Waylith、Waylith.UnityCompat。
- 全部 AO 访问是运行时按名反射，缺成员时各桥把失败记入 `_failure` 后返回 false，**静默降级**。
- 全工程对 AO 的 Harmony 补丁只有 2 个：
  - `Source/JeremieAtmosphereOverhaulPatch/ProxyHeat/ProxyHeatRelayPatches.cs:51`：`PrepareTemperatureSources` Postfix。
  - `Source/JeremieAtmosphereOverhaulPatch/Patches/TempOutputShapePatch.cs:48`：`FacilityPatchUtility.RunTempControl` Prefix。
- 反射清单集中在 `Bridge/AtmosphereFieldBridge.cs`（1273 行）：engine 数组、`pendingTemperatureDeltas`、`settings`、`CalculateVaporCapacity`、`CurrentSolverPressureToPascals`、`oxygenResidual` 等。

---

## 2. AO 新公共接口清单

### 2.1 逐格读

`AtmosphereInterface.cs:399-512`

`TryGetTemperature` / `TryGetGroundTemperature` / `TryGetRelativeHumidity` / `TryGetAbsoluteVapor` / `TryGetMist` / `TryGetOxygenPercent` / `TryGetWind` / `TryGetPressurePascals` / `TryGetAmbientPressurePascals` / `HasAtmosphere` / `SimulationIntervalTicks` / `VaporCapacity(Map, float)` / `VaporUnitsPerKilogram(float)` / `LatentHeatKelvinPerVaporUnit(Map)`。

关键修正：`TryGetPressurePascals`（:443）现在转发到 `TryGetStaticPressurePascals`，返回 **绝对压力 Pa** = `Mathf.Max(0, AmbientSurfacePressurePa + StoredPressureAt(i))`（`AtmosphereMapComponent.cs:1039`）。过去「101325 + 投影压力、不区分真空/海拔/密封房间」的问题已消除。

### 2.2 全图读

`TryGetFieldView(Map, out AtmosphereFieldView)`（`AtmosphereInterface.cs:80`）。

`AtmosphereFieldView`（`RimWorld/1AtmosphereInterface.Compat.cs:14-75`）**直接持有 engine 的 Snapshot 数组引用，不是拷贝**：

- `Temperature / GroundTemperature / RelativeHumidity / Vapor / Mist / OxygenPercent / Wind / PressurePascals / IsAir / IsRoofed`
- `IndexOf(cell) => cell.z * Width + cell.x`
- `PressurePascals(i) => Mathf.Max(0, engine.AmbientSurfacePressurePa + engine.StoredPressureAt(i))`
- `IsAir(i) => obstacle[i] > 0`，`IsRoofed(i) => roof[i] > 0.5`

只在主线程调用；下一步发布后旧视图数据被覆盖。

### 2.3 一次性增量

`AtmosphereInterface.cs:551-611`、`:733-758`

- 单格：`AddTemperatureDelta` / `AddGroundTemperatureDelta` / `AddMoistureDelta` / `AddMistDelta` / `AddOxygenPercentDelta` / `AddWindImpulse` / `AddPressureDelta`。
- 结构体：`AddDeltas(Map, IntVec3, AtmosphereDeltas)`、`AddDeltas(Map, CellRect, ...)`、`AddDeltasInRadius(...)`。
- 批量：`AddTemperatureDeltas(Map, int[] cellIndices, float[] deltas, int count)`、`AddMoistureDeltas(...)`。

语义（`AtmosphereMapComponent.cs:3674-3725`）：`EnqueueExternalDeltas` 在一次 `lock(incomingDeltasLock)` 内写 8 个通道，出锁后一次 `temporalPatchManager.RecordImpulse`。

### 2.4 热量与体积源

- `PushHeat(Map, IntVec3, float energy)`（:182）。
- `AddVolumeSource(Map, cell, float cellVolumes, float oxygenPercent, float temperatureOffsetCelsius)`（:513）。
- `AddVolumeSourceAtTemperature(Map, cell, float cellVolumes, float oxygenPercent, float gasCelsius)`（:532）。

### 2.5 凝水

`EmitCondensate(Thing building, float poolKg)`（:202）→ `WaterMath.EmitWater(building, ref poolKg)`，与 AO 除湿机同路径（含 Dubs 兼容）。返回剩余 kg。

### 2.6 连续源

- `SetContinuousSource(Map, Thing owner, string key, IntVec3 cell, AtmosphereSourceRates rates)`（:130）
- `ZeroContinuousSourcesWithPrefix(Map, Thing, string)`（:153）
- `RemoveContinuousSourcesWithPrefix(Map, Thing, string)`（:159）
- `RegisterContinuousSource` / `UpdateContinuousSource`（两个重载，:640 / :652）/ `UnregisterContinuousSource` / `UnregisterContinuousSources` / `TryGetContinuousSourceHandle`

`AtmosphereSourceRates`：`TemperaturePerTick / GroundTemperaturePerTick / MoisturePerTick / MistPerTick / WindPerTick(Vector2) / PressurePerTick / OxygenPercentPerTick / MaintainTargetWind / TargetWind / TargetWindBlendPerTick`。

### 2.7 每步事件

- `event AtmosphereStepHandler StepPreparing`（:92）：主线程、线程锁之前、`PrepareTemperatureSources` 内部触发。
- `event AtmosphereStepHandler StepApplied`（:94）：主线程、一步完成并发布快照后触发；存档期间不触发。

委托签名：`delegate void AtmosphereStepHandler(Map map, int gameTick, int elapsedTicks)`。

### 2.8 温控

- XML：`AO_OutletLayout`（DefModExtension，`Buildings/10VanillaFacilities.cs`）。
- 调参：`AO_FacilityExtension`（public，字段全 public，`Buildings/7VanillaFacilities.cs`）。
- C#：`IAtmosphereTempControlHook { void BeforeRun(Building_TempControl, ref AtmosphereTempControlRequest); void AfterRun(Building_TempControl, ref AtmosphereTempControlResult); }`
- 注册：`RegisterTempControlHook(ThingDef, hook)` / `RegisterTempControlHook(Type, hook)` / `UnregisterTempControlHook`。

AO 已通过 `FacilityCompInjection`（`Buildings/13VanillaFacilities.cs`）把 `CompTempControlAtmosphere` 自动挂到每个带 `CompProperties_TempControl` 的 `Building_TempControl`，并通过 `Buildings/VanillaFacilities.cs:13`、`Buildings/1VanillaFacilities.cs:13`、`Buildings/14VanillaFacilities.cs` patch 掉原版 `TickRare`。即 **AO 已完全接管所有温控建筑的 rare tick**。

### 2.9 通风口

`CompAirVent`（`Buildings/1AirVent.cs`）：`SpeedLimit` / `MaxSpeedLimit` / `ApplySpeedLimit(float)` / `IsOpen`；`AtmosphereInterface.InvalidateMapMasks(Map)`（:210）。

### 2.10 区域

`RegisterRegion(Map, Thing owner, IEnumerable<IntVec3> cells, AtmosphereRegionSpec)`（:331）/ `UpdateRegion`（:344）/ `UnregisterRegion`（:354）/ `UnregisterRegions(Map, owner)`（:360）。

`AtmosphereRegionSpec`：`Isolated / SealSky / TemperatureCelsius / RelativeHumidity / OxygenPercent / PressurePascals / HoldStill`。

### 2.11 风与 pawn

- `IAtmosphereShield { bool Covers(Map, IntVec3); }` + `RegisterShield` / `UnregisterShield` / `IsShielded`。
- `IAtmospherePawnAirProvider { bool TryGetAir(Pawn, out float pressureHpa, out float oxygenHpa); }` + `RegisterPawnAirProvider` / `UnregisterPawnAirProvider`。

### 2.12 喷流

`RegisterAirMover` / `UpdateAirMover` / `UnregisterAirMover(s)` / `SubmitAirMoverInterval`。

---

## 3. 接口真实实现核验

本节是本次分析的核心：逐条确认「宣称的语义」是否等于「真实实现」。

### 3.1 与宣称一致的部分

| 接口 | 核验结果 |
|---|---|
| `TryGetPressurePascals` | 确实返回绝对 Pa（`TryGetStaticPressurePascals`，`AtmosphereMapComponent.cs:1039`） |
| `EmitCondensate` | 确实走 `WaterMath.EmitWater`，未 Spawn 或 `poolKg<=0` 时原样返回 |
| `SetAmbientWind` | `ClampMagnitude` 后写 `ambientWind` 与 `settings.AmbientWindSpeed/NorthSpeed` |
| `PushHeat` | 真实现见 §3.2，语义比宣称窄 |
| 区域保持值 | 在 `CompleteFinishedSimulation` 内、快照发布前写入（`AtmosphereMapComponent.cs:3109-3139`） |
| `IAtmosphereShield` | 覆盖 8 个判定点，见 §3.4 |
| `IAtmospherePawnAirProvider` | `PawnAtmosphere.TrySample` 第一分支即调用（`Pawns/PawnAtmosphere.cs:24-32`） |

### 3.2 与宣称有偏差 / 需要前提条件

**① `PushHeat` 只影响最多 5 格，不是整个房间**

`AtmosphereMapComponent.cs:2958` → `QueueLocalizedTemperatureDelta`（:2870）：只遍历 origin 及上下左右共 5 格，要求 `obstacleMask>0` 且同 Room，`delta/count` 均分写入 `incomingTemperatureDeltas`。墙体格才按 8 邻域房间分摊。

`energy` 的单位是「K·格总量」，不是焦耳。

**② `AddVolumeSource` 是「沿 FaceOpen 的 BFS 扩散」，不是只影响该格**

`MixReleasedGasCore`（`AtmosphereMapComponent.cs:332-401`）：密封组件先按 `vol*n/(n+vol)` 折减；随后 BFS，每格每次最多吃 1 单位 `b`，四邻要求 `face>0 && ObstacleMask[j]>0`。1 格气柱 ≈ 5 m³。

**③ `RegisterRegion(Isolated)` 只改 FaceOpenX/Z，不改 ObstacleMask**

`ApplyApiRegionMasksUnsafe`（`AtmosphereMapComponent.cs:4730-4770`）：`Isolated` 只把区域边界的 `faceOpenX/Z` 置 0；`SealSky` 只写 `roofMask=1`。**完全不碰 `obstacleMask`**。

而引擎把 `ObstacleMask[i] <= 0` 当整格短路（`LocalAtmosphereEngine` 中遍布 `if ((double)this.ObstacleMask[i] <= 0.0) continue/return`），`FaceOpen=0` 只是让通量系数为 0、循环照跑。→ **`RegisterRegion(Isolated)` 不省算力**，只有把 mask 置 0 才省。

**④ `HoldCell` 对 `ObstacleMask<=0` 的格直接 return**

`LocalAtmosphereEngine.cs:6187-6225`：首行 `if (ObstacleMask[i] <= 0) return;`。写入通道只有：`Temperature.Read`、`Moisture.Read`+`RelativeHumidity.Read`+`Mist=0`、`Oxygen.Read`+`residual=0`、`StoredPressurePa`（仅 `compressibleActive`）、`VelocityX/Z.Read`（`HoldStill`）。

**不写**：`GroundTemperature`、`Pressure.Read`、`oxygenResidual` 之外的东西、`RoofMask`。

→ 用我们的几何隔离（整片 mask=0）时，`RegisterRegion` 的保持值对该片格**全部无效**。两者互斥。

**⑤ `TryGetFieldView` 与 `TryGet*` 不是同一份数据**

- `TryGetSample` 路径（`AtmosphereMapComponent.cs:1322/1337`）= `engine.SampleSnapshot(i)` **再叠加** `temporalPatchManager.TryBlendSample` 与 `acousticLayer.AddToSample`。
- `TryGetFieldView` 直接给 Snapshot 数组，**不含补间、不含声学层**。
- `TryGetGroundTemperature`（:1351）会再经 `temporalPatchManager.TryGetGroundTemperature` 修正。
- `TryGetMist`（:3654）读 `engine.MistSnapshot` 再 `TryGetMist` 修正；`TryGetAbsoluteVapor`（:2804）读 `AbsoluteVaporSnapshot`。

### 3.3 四个必须注意的语义陷阱

**陷阱 1：`StepPreparing` 在 `Array.Clear(pending*)` 之前触发**

`PrepareTemperatureSources()`（`AtmosphereMapComponent.cs:1814`）顺序：

```
:1816  RaiseStepPreparing(Find.TickManager.TicksGame)   ← 事件在这里
:1825  Array.Clear(pendingTemperatureDeltas / pendingMoistureDeltas / ...)
:1835  DrainIncomingTemperatureDeltasUnsafe()
:1836  DrainExternalIncomingDeltasUnsafe()
:1840  ApplyContinuousSourcesUnsafe()
```

所以在 `StepPreparing` 里**直接写 `pendingTemperatureDeltas` 会被下一行 Clear 抹掉**。必须用 `Add*Delta` 写 `incoming*`（Drain 在 Clear 之后，本步同步生效）。

同理，旧的「`PrepareTemperatureSources` Postfix 里直写 pending」之所以有效，是因为 Postfix 在 Clear 之后。

**陷阱 2：`HoldCell` 与 `mask=0` 互斥**（见 §3.2④）

**陷阱 3：`LocalAtmosphereSample` 的单位**

`LocalAtmosphereSample.cs`（19 行）字段 `Pressure / Moisture`。`LocalAtmosphereEngine.cs:3483-3490`：

- `Pressure = Pressure.Snapshot[i]` → **solver 单位，不是 Pa**。
- `Moisture = RelativeHumidity.Snapshot[i]` → **0-1 相对湿度**。

要 Pa 用 `TryGetPressurePascals`；要绝对水汽用 `TryGetAbsoluteVapor`。

**陷阱 4：`SetContinuousSource` 的移动语义**

`AtmosphereInterface.cs:141` 确实调用带 cell 的 `UpdateContinuousSource(map, owner, handle, cell, rates)` 重载，所以已存在 key 时 cell 生效、源会移动。

但源的实际位置是 **owner 相对偏移**：`ApplyContinuousSourcesUnsafe` 每步用 `owner.Position + OwnerOffsetX/Z` 重算（`AtmosphereMapComponent.cs:2561`）。注册的绝对格只是初始偏移，owner 移动时源跟着移动。

### 3.4 `IAtmosphereShield` 的 8 个实际调用点

| 位置 | 作用 |
|---|---|
| `WindStructuralLoad.cs:452` | 结构载荷 demand/pressureLoad/pendingDamage 清零；`:464` 的 load 同时含压差项 → 风+压差都豁免 |
| `WindPassableLoad.cs:328` | 可穿透物 `PendingAction` 清除 |
| `WindRoofLoad.cs:345` | 屋顶移除 pending 剔除 |
| `WindTransport.cs:303` | pawn 不被抛飞 |
| `WindTransport.cs:328` | **`StaggerFor` 在此 if 分支内，踉跄也被豁免** |
| `WindTransport.cs:580` | 物品不被吹走 |
| `WindBorneThing.cs:235/247/334/348` | 飞行物撞击伤害（我们当前未 patch，是净收益） |

对比：本项目现有 6 个风害 patch 被完整覆盖，且多覆盖飞行物。

### 3.5 `BuildingAtmosphereExtension.AirFill` 的坑

`Simulation/BuildingAtmosphereExtension.cs:31-35`：

```csharp
public static float AirFill(ThingDef def)
    => !airPassable || airFillPercent < 0 ? def.fillPercent : airFillPercent;
```

→ `airPassable=false` 时 `airFillPercent` **被忽略**。要设自定义填充率必须同时 `airPassable=true`。

而 `airPassable=true` 会让 `MapObstacleMaskBuilder.AirCanMoveThrough` 返回 true（`MapObstacleMaskBuilder.cs:204-216`），从而把 `fillPercent=1` 的建筑从 `obstacleMask` 抹掉。

### 3.6 其它实现细节

- `Vents.Limit` 是风速上限 m/s（`LocalAtmosphereEngine.cs:6485-6536`）：`Conductance = |num3|<=Limit ? 1 : max(1e-4, Limit/num3)`，并把两侧速度 clamp 到 `±Limit`。
- `MapObstacleMaskBuilder.Rebuild`（`MapObstacleMaskBuilder.cs:19-84`）：`obstacleMask` 只有 0/1 二值；`faceOpenX[i] = mask[i]*mask[i+1]`；最后 `BuildVents` 覆写通风口掩膜与面开度。
- `TemporalPatchManager.RecordImpulse`（`Simulation/Patches/TemporalPatchManager.cs:285-298`）写 `sourceJournal.Record(tick+1, ...)`，不受 `BeginDetection` 清空影响。
- `AtmosphereMapComponent.TryGetStaticPressurePascals` 与 `AtmosphereFieldView.PressurePascals` 公式完全一致。
- `RebuildObstacleMaskUnsafe`（:3345-3355）顺序：`Array.Copy` → `maskBuilder.Rebuild` → `ApplyApiRegionMasksUnsafe` → `ReconcileOpenedCells` → `InvalidateAirMoverConnectivity`。

---

## 4. 迁移映射表

| 现状实现（文件） | 依赖的 AO 内部面 | 可迁到 |
|---|---|---|
| `ProxyHeatRelayPatches.cs:51` `PrepareTemperatureSources` Postfix | `pendingTemperatureDeltas` 直写 | `StepPreparing` + `AddTemperatureDeltas`（批量）/ `AddGroundTemperatureDelta`（地温逐格） |
| `ProxyHeatRelayMapComponent.cs:1428` `RegisterAirMover` 反射 | `RegisterAirMover` 私有绑定 | 公共 `AtmosphereInterface.RegisterAirMover` |
| `CompTempOutputShape.cs` `GetSlots/SetSlot/ZeroFrom` | `CompTempControlAtmosphere.slots`（SourceSlots） | `SetContinuousSource` + 自建 key 前缀（如 `Jeremie.OutletShape.*`）+ `Zero/RemoveContinuousSourcesWithPrefix` |
| `PushHeat` 反射调用 | `AtmosphereMapComponent.PushHeat` | 公共 `AtmosphereInterface.PushHeat`（注意只影响 5 格） |
| `AtmosphereFieldBridge.GetStaticPressurePascals` | `TryGetStaticPressurePascals` | `TryGetPressurePascals` |
| 氧/水汽/湿度读 | `Oxygen.Read` / `AbsoluteVaporSnapshot` / `Moisture.Read` | `TryGetOxygenPercent` / `TryGetAbsoluteVapor` / `TryGetRelativeHumidity` |
| `WaterMath.EmitWater` 反射 | `WaterMath` | `EmitCondensate` |
| `CalculateVaporCapacity` + `settings` 反射 | `AtmosphereThermodynamicKernel` | `VaporCapacity(Map, float)`；`LatentHeatKelvinPerVaporUnit(Map)`（部分替代 settings） |
| `MarkCachedMapDataDirty` 反射 | `AtmosphereMapComponent.MarkCachedMapDataDirty` | `InvalidateMapMasks(Map)` |
| `CompAirVent.speedLimit` 直写 / `SetSpeedLimit` patch | public 字段 | `ApplySpeedLimit(float)` |
| `AtmosphereFieldBridge` 引擎数组 | `Temperature/Moisture/... .Read` | 主线程读场用 `TryGetFieldView`；跨线程用 `TryGet*` |
| `ProxyHeatRelayMapComponent` 用 `lastSynchronizedSimulationTick` + `CompleteFinishedSimulation` | 内部 tick 差 | `StepApplied` 的 `elapsedTicks` |
| `TempOutputShapePatch.cs:48` Prefix `RunTempControl` | `FacilityPatchUtility` | `RegisterTempControlHook` + `Request.SkipOutput` + `ManagePower=false` |
| VTE 吊扇 `SetSlot` | `SourceSlots` | `SetContinuousSource` |
| CCC `AirLoopMapComponent` 体积/压力/温度注入 | 反射 + `PushHeat`/`AddMoistureDelta` | `AddVolumeSource` / `AddTemperatureDeltas` / `AddMoistureDeltas` |
| GC `PrepareTemperatureSources` patch | `pending*` | `StepPreparing` |
| 超维 6 个风害 patch | `WindStructuralLoad` 等内部字段 | `IAtmosphereShield`（覆盖 6 点 + 飞行物） |
| `Patch_OmniPawnProtection` `PawnAtmosphere.TrySample` Prefix | 内部采样方法 | `IAtmospherePawnAirProvider` |
| `Patch_OmniBuildingAtmosphere` `AirFill` Postfix | `BuildingAtmosphereExtension.AirFill` | XML `BuildingAtmosphereExtension`（**受 §3.5 坑限制，实际不可等价**） |
| `Patch_OmniGeometricIsolation` | `MapObstacleMaskBuilder.Rebuild` | **无对应接口** |
| `Patch_AbsoluteIsolation` | `PublishSnapshot` Prefix | **无对应接口** |
| `GcPatches.SetSpeedLimit` patch | `CompAirVent.SetSpeedLimit` | 可被 `ApplySpeedLimit` 替代 |
| `EdificeGridRestorePatch` | 原版 `Building.DeSpawn` | 保留（与 AO 无关） |
| `Patch_OmniAtmosphereTemperature` | 原版 `GenTemperature` + AO 优先级 800 | 保留（原版侧） |

---

## 5. 无法靠接口等价实现的五类功能

### ① 穹顶几何隔离的「省算力」

`RegisterRegion(Isolated)` 只改 FaceOpen，不改 `ObstacleMask`；引擎按 `ObstacleMask<=0` 整格短路。要用接口拿到「整片跳过计算」的收益，必须让 mask=0，而接口没有写 mask 的能力。

现状收益公式：≈ `105N − 77N·f`（f = 冻结面积比，30% → 省 22%，50% → 省 37%）。

### ② 穹顶六通道硬固定

`HoldCell` 只写温度/湿度/RH/雾/氧/StoredPressurePa/风，**缺 `GroundTemperature`、`Pressure.Read`、`oxygenResidual`、`RoofMask`**，且对 `ObstacleMask<=0` 直接 return。

- 缺地温会破坏「地温也固定」的语义；
- 缺 `SealedPressurePa`/`PressureOddityPa` 的当前实现也会在非可压缩模式失效（见 §7.2）；
- 与几何隔离互斥。

### ③ 超维建筑风免疫的「不破坏阻挡几何」

要求的语义是：不进入可穿透物风载荷名单 + 不被风阻尼推动 + 保持 `obstacleMask` 阻挡。

- `AirFill` 只有 `airPassable=true` 时才读 `airFillPercent`；
- `airPassable=true` 会让该建筑从 `obstacleMask` 抹掉。

XML 路径走不通，只能保留 `AirFill` Postfix。

### ④ 随身温控 pawn 的风豁免

`IAtmosphereShield.Covers(Map, IntVec3)` **只有格维度，没有 pawn 维度**。随身温控是「某个 pawn 单独免疫」，同格其它 pawn 不应免疫；接口无法表达，会误保护同格所有单位。

### ⑤ 「AO 锁内直写 pending」的零开销

公共增量一律走 `lock(incomingDeltasLock)` + 每格 `RecordImpulse`。只有温度/湿度有批量入口，且批量入口省锁不省 journal。

现状 `ProxyHeat` 在 `PrepareTemperatureSources` Postfix 内直写 `pendingTemperatureDeltas`，无锁无 journal；迁移后至少多一次锁与 N 次 journal 记录。是否可接受需按每步格数评估（性能红线：每粗步 ≤2N）。

---

## 6. 建议迁移顺序

1. **`StepApplied`** —— 替换 `lastSynchronizedSimulationTick` + `CompleteFinishedSimulation` 前后缀，风险最低、收益明确（拿到准确 `elapsedTicks`）。
2. **`IAtmosphereShield` + `IAtmospherePawnAirProvider`** —— 一次替换超维 6 个风 patch + 1 个 pawn 采样 patch，并额外获得飞行物豁免。
3. **读接口** —— `TryGetFieldView`（主线程）+ `TryGet*`（跨线程），先替换只读路径，验证数值与插值差异。
4. **写接口** —— 先温度/湿度批量（`AddTemperatureDeltas`/`AddMoistureDeltas`），地温逐格；压力暂保留直写，观察 journal 开销。
5. **温控 hook** —— 先用 XML `AO_OutletLayout` 表达几何，再用 `IAtmosphereTempControlHook` 接管模式与功率；最后删 `TempOutputShapePatch`。
6. **区域接口** —— 只用于新功能（如护盾内的隔离区），不用于穹顶。
7. **清理** —— `SourceSlots`、`FacilityPatchUtility`、`MarkCachedMapDataDirty`、`speedLimit` 反射全部删除。

---

## 7. 现状实现缺陷（本轮复核补充，与迁移独立）

### 7.1 护盾掩膜刷新周期恒为 30 tick

`Source/Compat.OmniCrafter/OmniProtection.cs:56-58`：

```csharp
windCheckIntervalField = AccessTools.Field(
    AccessTools.TypeByName("AtmosphereOverhaul.Simulation.LocalAtmosphereSettings"),
    "WindDamageCheckIntervalTicks");
```

但 `LocalAtmosphereSettings.cs:700` 是属性：

```csharp
public int WindDamageCheckIntervalTicks { get; set; }
```

`AccessTools.Field` 只找字段 → 恒 null → 回退 30（`OmniProtection.cs:278-281`）。

**修复**：改用 `AccessTools.Property(...).GetValue(settings)`，并缓存 getter。

### 7.2 穹顶气压目标只在可压缩模式下生效

`Patch_AbsoluteIsolation.cs:171` 只写 `storedPressure[index] = storedPressureTarget`。

但 `LocalAtmosphereEngine.StoredPressureAt`（`:2156-2163`）：

```csharp
if (this.compressibleActive && this.StoredPressurePa != null)
    return this.StoredPressurePa[i];
return this.SealedPressurePa == null ? 0.0f : this.SealedPressurePa[i] + this.PressureOddityPa[i];
```

关闭 `CompressiblePressure` 后，绝对压力走 `SealedPressurePa + PressureOddityPa`，补丁写的 `StoredPressurePa` 完全不参与。

同时 `:160`：

```csharp
float pascals = AtmosphereFieldBridge.GetSolverPascalsPerUnit();
if (pascals <= 0f) pascals = 1000f;
```

而正确系数是 `engine.SolverPascalsPerUnit(settings)`：

```csharp
// LocalAtmosphereEngine.cs:2151-2154
return !compressibleActive
    ? AtmosphereColorUtility.CurrentSolverPressureToPascals()   // 1000
    : settings.PressureAccelerationScale * 1.2f / Mathf.Max(0.0001f, settings.TimeStep);
```

`Patch_AbsoluteIsolation.cs:170` 的 `pressureReadTarget = (targetPa - 100000f) / pascals` 在可压缩模式下会偏。

**修复**：按 `engine.CompressibleActive` 分支——可压缩写 `StoredPressurePa`，否则写 `SealedPressurePa + PressureOddityPa`；系数改用 `SolverPascalsPerUnit(settings)`。

### 7.3 几何隔离会清掉穹顶内通风口的面开度

`Patch_OmniGeometricIsolation` 是 `MapObstacleMaskBuilder.Rebuild` 的 Postfix；`BuildVents` 在 `Rebuild` 内部先执行（`MapObstacleMaskBuilder.cs:86-180`）。Postfix 随后清四邻 `FaceOpen`，会把通风口刚写入的面开度覆盖为 0。

若语义是「穹顶内完全隔离」，这是预期行为；若希望穹顶内仍可用通风口，需要在 Postfix 里跳过被 `BuildVents` 标记过的格。

### 7.4 两条低风险事实（子代理核对，未逐行复核）

- `Patch_AbsoluteIsolation` 的 `PublishSnapshot` Prefix 会在 9 个调用点执行（`AtmosphereMapComponent.cs:1181, 1194, 1317, 1557, 1583, 3048, 3121, 3124, 3649`），含 `FinalizeInit` / `MapGenerated` / `ClearFlow` / `AddAtmosphereGas` / `FillGas` / `CompleteFinishedSimulation` / `SetAmbientWind`，不只是模拟步末。
- `Patch_OmniAtmosphereTemperature.cs` 注释写「超维 Priority.Last(100)」，但 `OmniForceFieldDome.cs:1367`、`Patch_CompBiosphere.cs:610` 的温度补丁实际没有 `HarmonyPriority`（默认 400）；AO 是 800（`Patches/1TemperaturePatches.cs:17`），我们是 90。postfix 相对执行顺序未逐行核验。

---

## 8. 需向作者确认的问题

1. `AtmosphereFieldView` 为什么不暴露 `FaceOpenX/Z`？做通道连通性判断时它很关键。
2. 区域保持值能否覆盖 `GroundTemperature` / `Pressure.Read` / `RoofMask` / `oxygenResidual`？
3. `HoldCell` 对 `ObstacleMask<=0` 直接 return 是设计意图吗？几何隔离与通道固定互斥是否有意为之？
4. `Isolated` 是否影响压力求解的「密封组件」划分？还是只关面通量？
5. 有没有「一次加锁写多通道」的批量增量入口？现在只有温度/湿度有 `Add*Deltas`。
6. `AirFill` 在 `airPassable=false` 时忽略 `airFillPercent` 是 bug 还是有意？
7. `IAtmosphereShield` 能否加一个 pawn 维度（或允许 `Covers(Pawn)`）？
8. `AirMoverHasReturnPath` / `IsAirMoverPortOpen` 能否公开？（GC 需要）

---

## 9. 关键源码位置索引

| 主题 | 位置 |
|---|---|
| 公共接口总入口 | `f:/RimworldCodeIL/AtmosphereOverhaul/RimWorld/AtmosphereInterface.cs`（795 行） |
| 读场/写通道实现 | `RimWorld/AtmosphereMapComponent.cs`（5499 行） |
| 引擎与 `HoldCell` | `Simulation/LocalAtmosphereEngine.cs`（6656 行，`HoldCell` :6187） |
| 掩膜构建 | `Simulation/MapObstacleMaskBuilder.cs`（237 行） |
| 建筑大气扩展 | `Simulation/BuildingAtmosphereExtension.cs`（49 行） |
| 温控接管 | `Buildings/12VanillaFacilities.cs`（342 行） |
| 出风口布局 | `Buildings/10VanillaFacilities.cs` |
| 设施参数 | `Buildings/7VanillaFacilities.cs` |
| 通风口组件 | `Buildings/1AirVent.cs` |
| 时间补丁 | `Simulation/Patches/TemporalPatchManager.cs`（634 行） |
| 风害 | `Simulation/WindStructuralLoad.cs` / `WindPassableLoad.cs` / `WindRoofLoad.cs` / `WindTransport.cs` |
| pawn 采样 | `Pawns/PawnAtmosphere.cs` |
| 字段视图 | `RimWorld/1AtmosphereInterface.Compat.cs` |
| 区域规格 | `RimWorld/8AtmosphereInterface.Compat.cs` |

---

## 10. 附：本次分析的方法说明

- 所有 AO 行号取自 `f:/RimworldCodeIL/AtmosphereOverhaul/`（2026-11-24 反编译，与 2026-11-23 的 DLL 同版本）。
- 本项目行号取自 `Source/` 下源码。
- 结论均以「源码事实」为准，不采信文档、注释或子代理结论；两处子代理结论在本轮被推翻（`SetContinuousSource` 曾误判为忽略 cell、`TryGetPressurePascals` 曾误判为返回 solver 单位）。
- 未修改任何代码。

---

## 附录 A：四 Mod 现有换算公式（迁移时须保持不变）

### A.1 VTE `AcNetLedger`

```
qFull       = |energyPerSecond| * (dtTicks/60) * heatFactor
drive       = Clamp01(|target - feedback| / 1)
qi          = energyPerSecond * (dtTicks/60) * sign(target-feedback) * drive * heatFactor
refCapacity = qRef > 0 ? qRef : 87.5 * (dtTicks/250)
s           = min(1, capacity / qNet)，qNet < 0.01 归零
室外机       maxDrop = 8 * (dt/250) ℃
            floor   = max(-30, baseline - maxDrop)
            allowed = 5 * (floor - cellTemp)   // ×5 格
```

依赖：`CompTempControlAtmosphere.slots`（私有）、`SourceSlots.Set/ZeroFrom`、`PushHeat`、`AO_FacilityExtension.heatFactor/outletWindSpeed`、`lastSynchronizedSimulationTick` + `CompleteFinishedSimulation`。

### A.2 VTE 吊扇

```
perTick    = heatPerSecond * coolingFactor / 60 / count
TargetWind = Tangent * windSpeed        // Tangent = (dz, -dx)/|r|
```

每 60 tick RebuildCells，每 30 tick Apply。

### A.3 CCC `AirLoop`

```
PressurePa     = MassKg * 287 * T_K / max(0.1, V_m3)
VaporCapacity  = max(0.001, 6.11 * exp(17.6*t / max(1, 243+t)))
VaporPerKg     = 461.5 * max(1, c+273.15) / 500
intakeRate     = capacity * Clamp01(1 + (roomP - P) / max(1, fanMaxRise))
drive          = Clamp(1 + (P - roomP) / max(1, EffP), 0.5, 1.5)
```

`AirLoopMapComponent`：

```
Volume            = max(3, pipeCells*0.6 + (producers+consumers+tempControls)*1.5)
RatedIntakeKgPerSec = airFlow * mult * 0.000472 * 1.2
friction          = min(6, 1 + pipeCells*0.02)
EffPressurePa     = 300 * friction
FanMaxRisePa      = 2000 / friction
cellVolumes       = m³ / 5                // AO 1 格 = 5 m³
vaporDelta        = deliveredM3 * (loop.Vapor - roomVapor) / (5 + deliveredM3)
CondensateKg     += excess * cellFraction / VaporPerKg
speed             = min(120, airFlow*0.1)
perCellSpeed      = speed / sqrt(N)
热 energy         = min(|ΔT|, 10) * 0.25 * (airFlow/100) * flowEff² * thermalOk * 4.1666665 * 12
                  → ControlTemperatureTempChange
totalHeatPerTick  = roomDelta * cellCount / 250 * AoHeatScale
```

### A.4 GC `GcAirRelay`

```
alphaMax = min(0.6, 2 / max(1, |ΔT|))
volumes  = -ln(1 - alphaMax)
           Clamp(volumes * 5/6, 0.5, 10)
温度     target  = 0.15 * (inletT - outletT)
         applied = outletT - last
         delta   = Clamp(target - applied, ±2)
氧       oxygenPerTick = 0.1 * (inletO2 - outletO2) / 6
air mover 常量：AirColumnHeightMetres=5、MaxPressureRisePascals=1000、
              ExhaustSpeed=30、ExhaustPressureRatio=0.02、
              InletPressureRatio=0、WaterKgPerTick=0
```

风险：`RegisterAirMover` 的 settings 逐字段比较，任何变化都会触发 `UpdateAirMover` 把 `LastTick` 重置为当前 tick → 本周期 `ActiveTicks==0` 不产流。出风温度须量化到 5℃ 台阶。

---

## 附录 B：超维兼容 10 个文件的 AO 依赖点

| 文件 | AO 依赖 | 迁移候选 |
|---|---|---|
| `Patch_AbsoluteIsolation.cs` | `LocalAtmosphereEngine.PublishSnapshot` Prefix；反射 10 个通道数组 + `settings` + `CalculateVaporCapacity` + `CurrentSolverPressureToPascals` + `AmbientSurfacePressurePa` | 无（§5②） |
| `Patch_OmniGeometricIsolation.cs` | `MapObstacleMaskBuilder.Rebuild(Map, engine)` Postfix；反射 `ObstacleMask`/`FaceOpenX`/`FaceOpenZ`/`Width` | 无（§5①） |
| `Patch_OmniAtmosphereTemperature.cs` | 无 AO 成员；依赖 AO 在 `GenTemperature` 的 800 优先级 Postfix | 保留 |
| `Patch_OmniBuildingAtmosphere.cs` | `BuildingAtmosphereExtension.AirFill(ThingDef)` Postfix | 无（§5③） |
| `Patch_OmniWindExemption.cs` | 反射 `WindStructuralLoad`(count/demand/pressureLoad/pendingDamage/building)、`WindPassableLoad+MemberSet`(Count/Things/PendingAction/LastLoad/LastRiskPerHour)、`WindRoofLoad`(pendingCount/pending/cellIndex)、`WindTransport.TmpThrow`；6 个 Harmony patch | `IAtmosphereShield` |
| `Patch_OmniPawnProtection.cs` | `PawnAtmosphere.TrySample` Prefix | `IAtmospherePawnAirProvider` |
| `OmniProtection.cs` | `AccessTools.Field(LocalAtmosphereSettings, "WindDamageCheckIntervalTicks")` | 改 `AccessTools.Property` 或直接读 `settings` |
| `OmniDomeZoneCache.cs` | 只沿用 AO 索引约定 `x + z*Width` | 无（索引约定不变） |
| `MapComponent_OmniAtmosphereTargets.cs` | 反射 `AtmosphereMapComponent.engine`、`LocalAtmosphereEngine.AmbientSurfacePressurePa` | 可用公共 `TryGetPressurePascals`/`TryGetAmbientPressurePascals` |
| `OmniCompatBootstrap.cs` | 仅 `AtmosphereFieldBridge.SixChannelAvailable` 能力门 | 可用公共 `HasAtmosphere` 替代门禁 |

---

## 附录 C：本轮记忆同步

已完成以下记忆写入（项目 `JeremieAtmosphereOverhaulPatch`）：

- 技术决策：AO 公共接口迁移总览（可迁/不可迁/顺序）。
- 事实：`RegisterRegion` 掩膜语义与 `HoldCell` 互斥、`StepPreparing` 时序、`LocalAtmosphereSample` 单位、`SetContinuousSource` 移动语义、`IAtmosphereShield` 8 覆盖点、`AirFill` 坑、作者兼容承诺、温控接管竞争、`AddVolumeSource` 5 m³/BFS、`OmniProtection` 属性反射 bug、`Patch_AbsoluteIsolation` 气压模式缺陷、几何隔离覆盖通风口面开度。
- 结构：编译期不引用 AO、107 处反射、仅 2 处 Harmony 补丁。
- 教训：接口语义必须以 `f:/RimworldCodeIL` 最新反编译为准。
- 话题：本次迁移任务的起因与状态（待拍板）。

---

## 附录 D：需要 AO 新增的 API（迁移缺口）

### D.0 判断口径

只列「现有公共接口无法表达、且本项目迁移必须用」的能力。每条都给出：缺口证据、推荐签名、AO 内可落点。

### D.1 P0：不做就无法迁移

#### ① 区域的「不参与模拟」

**缺口**：`RegisterRegion(Isolated)` 只写 `FaceOpenX/Z=0`（`AtmosphereMapComponent.cs:4754-4765`），不改 `ObstacleMask`。引擎按 `ObstacleMask<=0` 整格短路，`FaceOpen=0` 只是让通量系数为 0、循环照跑。→ 拿不到省算力收益。

**推荐形式**（扩展现有 struct，不新增方法）：

```csharp
public struct AtmosphereRegionSpec
{
    // ... 现有字段
    public bool Static;   // 新增：整片不参与模拟
}
```

语义：AO 在 `ApplyApiRegionMasksUnsafe` 内对 `Static` 区域：

1. `engine.ObstacleMask[i] = 0f`；
2. 重算四邻 `FaceOpenX/Z`（因为 Rebuild 已按旧 mask 算过）；
3. `HoldCell` 对这些格走 `force` 分支（见下），仍写保持值。

`HoldCell` 配套修改（`LocalAtmosphereEngine.cs:6187`）：

```csharp
internal void HoldCell(..., LocalAtmosphereSettings settings, bool force = false)
{
    if ((uint)i >= (uint)Size || (!force && ObstacleMask[i] <= 0f))
        return;
    // ...
}
```

这样 `Static` + `force` 同时实现「不计算」与「快照仍显示设定值」——因为 mask=0 后引擎不会再改该格 `Read`，`PublishSnapshot` 会把 `Read` 原样复制进 `Snapshot`。

**为什么不能直接暴露写 mask 的方法**：写 mask 会与 `BuildVents`、`ReconcileOpenedCells`、`SetBlockedCellsToAmbient` 冲突，必须由 AO 在 `RebuildObstacleMaskUnsafe` 的固定顺序内处理。

#### ② 区域保持值的通道扩展

**缺口**：`AtmosphereRegionSpec`（`8AtmosphereInterface.Compat.cs:10-33`）只有温度/RH/氧/压力/风；`HoldCell`（`LocalAtmosphereEngine.cs:6196-6219`）不写 `GroundTemperature`、`RoofMask`、`Mist`，且 `PressurePascals` 只在 `compressibleActive` 时生效。

**推荐形式**：

```csharp
public struct AtmosphereRegionSpec
{
    // ... 现有字段
    public float GroundTemperatureCelsius;  // NaN = 不保持
    public float Mist;                      // NaN = 不保持
    public bool? Roofed;                    // null = 不改
}
```

`HoldCell` 内补：

```csharp
if (!float.IsNaN(groundCelsius))
    GroundTemperature[i] = groundCelsius;

if (!float.IsNaN(mist))
    Mist.Read[i] = Mathf.Max(0f, mist);

if (roofed.HasValue)
    RoofMask[i] = roofed.Value ? 1f : 0f;

if (!float.IsNaN(pascals))
{
    if (compressibleActive && StoredPressurePa != null)
        StoredPressurePa[i] = pascals - AmbientSurfacePressurePa;
    else if (SealedPressurePa != null && PressureOddityPa != null)
    {
        SealedPressurePa[i] = pascals - AmbientSurfacePressurePa;
        PressureOddityPa[i] = 0f;
    }
}
```

#### ③ 建筑级风免疫（不破坏阻挡几何）

**缺口**：`BuildingAtmosphereExtension.AirFill` 在 `airPassable=false` 时直接返回 `def.fillPercent`（`BuildingAtmosphereExtension.cs:31-35`），`airFillPercent` 被忽略；而 `airPassable=true` 会让 `AirCanMoveThrough` 返回 true，抹掉 `obstacleMask`。

**推荐形式**（XML 字段，最符合 RimWorld 习惯）：

```xml
<li Class="AtmosphereOverhaul.Simulation.BuildingAtmosphereExtension">
  <windImmune>true</windImmune>
</li>
```

```csharp
public class BuildingAtmosphereExtension : DefModExtension
{
    // ... 现有字段
    public bool windImmune;
}
```

消费点：

- `RimWorld/WindPassableLoad.cs:105`：`if (BuildingAtmosphereExtension.For(def).windImmune) continue;`
- `Simulation/MapObstacleMaskBuilder.cs:218` 的 `ComputeDampingScale`：`windImmune` 时风阻尼项按 0。

备选 C#：`RegisterWindImmuneDef(ThingDef)` / `UnregisterWindImmuneDef(ThingDef)`。

#### ④ pawn 维度的风豁免

**缺口**：`IAtmosphereShield` 只有 `Covers(Map, IntVec3)`（`6AtmosphereInterface.Compat.cs:12-15`），无 pawn 维度。随身温控是「单个 pawn 免疫」，格级接口会误保护同格其它 pawn。

**推荐形式**（新增接口，不改现有接口以保持二进制兼容）：

```csharp
public interface IAtmospherePawnShield
{
    bool Covers(Pawn pawn, Map map, IntVec3 cell);
}

void RegisterPawnShield(IAtmospherePawnShield shield);
void UnregisterPawnShield(IAtmospherePawnShield shield);
bool IsPawnShielded(Pawn pawn, Map map, IntVec3 cell);
```

消费点：`WindTransport.cs:303`（抛飞）与 `:328`（`StaggerFor`）改为先查 `IsPawnShielded`，未命中再回落 `IsShielded`；建筑类风害仍用格级 `IsShielded`。

#### ⑤ 批量多通道增量

**缺口**：只有温度/湿度有 `AddTemperatureDeltas` / `AddMoistureDeltas`；其它 6 个通道只能逐格 `Add*Delta`，每格一次锁 + 一次 journal。

**推荐形式**（与现有批量签名对齐）：

```csharp
void AddDeltas(Map map, int[] cellIndices, AtmosphereDeltas[] deltas, int count); // 每格一份
void AddDeltas(Map map, IntVec3[] cells, AtmosphereDeltas deltas, int count);     // 同一份到多格
```

实现：一次 `lock(incomingDeltasLock)` 内循环写 8 个通道，出锁后逐格 `RecordImpulse`（journal 仍逐格，但锁从 N 次降到 1 次）。

### D.2 P1：强烈建议

#### ⑥ `FaceOpenX/Z` 读

**缺口**：`AtmosphereFieldView`（`1AtmosphereInterface.Compat.cs:14-75`）不含 `FaceOpenX/Z`，跨线程做连通性/隔离判断时拿不到。

**推荐形式**：

```csharp
public float FaceOpenX(int index) => this.engine.FaceOpenX[index];
public float FaceOpenZ(int index) => this.engine.FaceOpenZ[index];
```

文档需注明：这是被 choke 压过的值，只用于连通性判断，不要当物理量。

#### ⑦ AirMover 可通行查询公开

**推荐形式**：

```csharp
bool AirMoverHasReturnPath(Map map, IntVec3 inlet, IntVec3 outlet);
bool IsAirMoverPortOpen(Map map, IntVec3 cell);
```

（内部已有 `internal bool AirMoverHasReturnPath(int, int)`（`LocalAtmosphereEngine.cs:659`）与 `internal bool IsAirMoverPortOpen(int)`（:824），只需加 Map 重载。）

#### ⑧ `SolverPascalsPerUnit` 公开

**缺口**：外部只能拿到 `CurrentSolverPressureToPascals()`（恒 1000），可压缩模式的正确系数是 `settings.PressureAccelerationScale * 1.2 / TimeStep`（`LocalAtmosphereEngine.cs:2151-2154`），导致 `Patch_AbsoluteIsolation` 换算偏差。

**推荐形式**：

```csharp
float SolverPascalsPerUnit(Map map);
```

### D.3 P2：能力增强

#### ⑨ 温控 hook 的完全接管

现状 `IAtmosphereTempControlHook` 只能改 Request / 读 Result，不能替换 `RunHeater`/`RunCooler` 算法。

```csharp
public interface IAtmosphereTempControlHook
{
    void BeforeRun(Building_TempControl building, ref AtmosphereTempControlRequest request);
    void AfterRun(Building_TempControl building, ref AtmosphereTempControlResult result);

    // 新增：返回 true = 已接管，AO 不再执行默认算法
    bool Run(Building_TempControl building,
             ref AtmosphereTempControlRequest request,
             ref AtmosphereTempControlResult result);
}
```

#### ⑩ 设施参数的实例级覆盖

`AO_FacilityExtension.For(ThingDef)` 是 def 级，同一 def 的不同建筑无法用不同功率/出风口参数。

```csharp
public interface IAtmosphereFacilityParams
{
    bool TryGetFacilityParams(out AO_FacilityExtension ext);
}
```

`AO_FacilityExtension.For(Thing thing)` 先查 comp 上的接口，再回落 def。

#### ⑪ 每步「发布前」事件

`StepPreparing` 在模拟前（写了会被覆盖），`StepApplied` 在 Publish 之后（来不及写可见值）。若 D.1① 的 `Static + force` 已实现，此项可不做；否则需要：

```csharp
event AtmosphereStepHandler StepPublishing;  // engineLock 内、PublishSnapshot 之前
```

需明确线程语义（若在模拟 Task 内触发，订阅方必须无 Unity 主线程调用）。

### D.4 不需要新增、只需文档化

- `Isolated` 会切分压力/air mover 组件：组件 BFS 用 `FaceOpenX/Z`（`LocalAtmosphereEngine.cs:724-731`），邻居要求 `face > 0`（:818）。→ `Isolated` 等价于「独立的密封压力组件」。
- `HoldCell` 跳过 `ObstacleMask<=0` 是设计意图还是遗漏。
- 区域设定不存档，需在 `SpawnSetup` 注册。
- `AirFill` 在 `airPassable=false` 时忽略 `airFillPercent`。
- `SetContinuousSource` 的源位置是 owner 相对偏移。
- `AtmosphereFieldView` 跨线程只读的安全边界（两次 Publish 之间）。

---

## 附录 E：迁移缺口速查（给 AO 作者）

> 本节是附录 D 的精简速查版，便于按优先级逐项实现；详细论证与源码位置见附录 D。

再核对后，迁移缺口共 **8 个必须/强烈建议 + 3 个能力增强**。以下按优先级列出，并给出推荐形式。

### P0：不补就无法迁移

#### ① 区域的「不参与模拟」

缺口：`RegisterRegion(Isolated)` 只写 `FaceOpenX/Z=0`（`AtmosphereMapComponent.cs:4754-4765`），不碰 `ObstacleMask`；引擎按 `ObstacleMask<=0` 整格短路，所以拿不到省算力收益。

推荐：扩展 `AtmosphereRegionSpec` 加一个 `public bool Static;`，由 AO 在 `ApplyApiRegionMasksUnsafe` 内写 `ObstacleMask=0` 并重算四邻 `FaceOpen`；同时给 `HoldCell` 加 `bool force = false`，`Static` 区域走 force 分支。

为什么这样拆：`mask=0` 后 `HoldCell` 会直接 return，必须靠 force 才能让快照仍显示设定值；而写 mask 又不能直接暴露给外部（会与 `BuildVents`、`ReconcileOpenedCells`、`SetBlockedCellsToAmbient` 冲突），只能由 AO 在固定顺序内处理。

#### ② 区域保持值的通道扩展

缺口：`AtmosphereRegionSpec` 只有温度/RH/氧/压力/风；`HoldCell`（`LocalAtmosphereEngine.cs:6196-6219`）不写地温/屋顶/雾，且 `PressurePascals` 只在 `compressibleActive` 时生效。

推荐：规格加 `float GroundTemperatureCelsius`、`float Mist`（NaN=不保持）、`bool? Roofed`；`HoldCell` 补地温/雾/屋顶分支，并把压力分支补成「可压缩写 `StoredPressurePa`，否则写 `SealedPressurePa`+`PressureOddityPa`」。

#### ③ 建筑级风免疫

缺口：`AirFill` 在 `airPassable=false` 时忽略 `airFillPercent`（`BuildingAtmosphereExtension.cs:31-35`），而 `airPassable=true` 会抹掉 `obstacleMask`。

推荐：XML 字段 `windImmune="true"`（`BuildingAtmosphereExtension` 新增 `public bool windImmune`），在 `WindPassableLoad.cs:105` 与 `ComputeDampingScale` 两处消费。比 C# 注册接口更符合 RimWorld 习惯。

#### ④ pawn 维度的风豁免

缺口：`IAtmosphereShield.Covers(Map, IntVec3)` 无 pawn 维度，格级接口会误保护同格其它 pawn。

推荐：新增接口（不改已发布的 `IAtmosphereShield`，保持二进制兼容）：

```csharp
public interface IAtmospherePawnShield { bool Covers(Pawn pawn, Map map, IntVec3 cell); }
void RegisterPawnShield(IAtmospherePawnShield shield);
void UnregisterPawnShield(IAtmospherePawnShield shield);
bool IsPawnShielded(Pawn pawn, Map map, IntVec3 cell);
```

消费点：`WindTransport.cs:303`（抛飞）与 `:328`（`StaggerFor`）先查 pawn shield，未命中再回落格级 shield。

#### ⑤ 批量多通道增量

缺口：只有温度/湿度有批量入口，其余 6 通道逐格加锁 + 逐格 journal。

推荐：沿用现有签名风格补两个重载：

```csharp
void AddDeltas(Map map, int[] cellIndices, AtmosphereDeltas[] deltas, int count);
void AddDeltas(Map map, IntVec3[] cells, AtmosphereDeltas deltas, int count);
```

实现上只需在 `EnqueueExternalDeltas` 外层做一次 `lock`，journal 仍逐格（锁从 N 次降到 1 次）。

### P1：强烈建议

#### ⑥ `FaceOpenX/Z` 读

`AtmosphereFieldView` 不含 faceOpen，跨线程做连通性判断时缺数据。推荐加 `float FaceOpenX(int)` / `float FaceOpenZ(int)`，文档注明「被 choke 压过的值，只用于连通性判断」。

#### ⑦ AirMover 可通行查询公开

内部已有 `AirMoverHasReturnPath(int,int)`、`IsAirMoverPortOpen(int)`（`LocalAtmosphereEngine.cs:659/824`），只需加 Map 重载：`bool AirMoverHasReturnPath(Map, IntVec3, IntVec3)`、`bool IsAirMoverPortOpen(Map, IntVec3)`。

#### ⑧ `SolverPascalsPerUnit` 公开

外部只能拿到恒为 1000 的 `CurrentSolverPressureToPascals()`，可压缩模式正确系数是 `PressureAccelerationScale*1.2/TimeStep`（`:2151-2154`）。推荐 `float SolverPascalsPerUnit(Map map)`。

### P2：能力增强

#### ⑨ 温控 hook 完全接管

现有 hook 只能改 Request/读 Result。推荐给 `IAtmosphereTempControlHook` 加 `bool Run(building, ref request, ref result)`，返回 true 表示跳过 AO 默认算法。

#### ⑩ 设施参数实例级覆盖

`AO_FacilityExtension.For(ThingDef)` 是 def 级。推荐加 `IAtmosphereFacilityParams`，`For(Thing)` 先查 comp。

#### ⑪ 每步「发布前」事件

`StepPreparing` 写了会被覆盖、`StepApplied` 在 Publish 之后。若 ① 的 `Static + force` 已实现可不做；否则需要 `StepPublishing`（engineLock 内、PublishSnapshot 之前），并明确线程语义。

### 推荐形式的共性原则

1. 优先扩展现有 struct/XML 字段，而不是新增散落的方法；
2. 新能力用新接口，不改已发布的接口（保住二进制兼容）；
3. 批量 API 沿用 `indices + deltas + count` 模式；
4. 语义命名要区分「隔离」与「不模拟」——`Isolated` 是关面，`Static` 是退场；
5. XML 能表达的不要给 C#。

### 一条新核对结论

`Isolated` **会切分压力/air mover 组件**：组件 BFS 用 `FaceOpenX/Z` 扩展（`LocalAtmosphereEngine.cs:724-731`），且 `VisitAirMoverNeighbor` 要求 `face > 0`（`:818`）。所以 `Isolated` 等价于「独立密封压力组件」，而不是单纯关通量——这一点建议写进 AO 文档。

---

## 附录 F 迁移实施记录（2026-11-24）

按 m00442 的 7 步顺序落地，五工程 `dotnet build -c Debug` 均 0 警告 0 错误。**游戏内逐建筑验证尚未进行**。

| 步 | 内容 | 落点 |
|---|---|---|
| 1 | `StepApplied` 事件替换 `CompleteFinishedSimulation` + `lastSynchronizedSimulationTick` 差分 | `Bridge/AoBridge.cs` 新增订阅与 Emit 动态代理；`Compat.CentralizedClimateControl/CccPatches.cs`、`Compat.VanillaTemperatureExpanded/AcNet/AcNetPatches.cs` 优先订阅，失败回退旧 patch |
| 2 | `IAtmosphereShield` + `IAtmospherePawnAirProvider` | 新建 `Compat.OmniCrafter/OmniAtmosphereApi.cs`；`Patch_OmniWindExemption` 成功时跳过 4 个旧 patch，保留 `ThrowCollectedPawns`/`StaggerFor`/`TrackerDirty`；`Patch_OmniPawnProtection` 优先 provider；修复 `OmniProtection` 把属性当字段导致恒 30 tick |
| 3 | 读接口 | 压力改 `TryGetPressurePascals(Map,…)`；`Bridge/AtmosphereFieldBridge.cs` 加 `TryGetFieldView` 与 `FieldView`；`Compat.GoodClimate/GcAirRelay.cs` 主线程读改 FieldView；FaceOpen 反射保留 |
| 4 | 写接口 | `AoBridge` 加 `StepPreparing`、`AddTemperatureDeltas`、`SetContinuousSource/ZeroContinuousSourcesWithPrefix/RemoveContinuousSourcesWithPrefix/UnregisterContinuousSources`；`PushHeat`/`EmitCondensate`/`InvalidateMapMasks`/`ApplySpeedLimit` 走公共入口；ProxyHeat 空气通道批量、地温仍直写；`CompTempOutputShape` 改连续源 |
| 5 | 温控出风格 | 新建 `Patches/OutletLayoutBootstrap.cs`：把 `CompProperties_TempOutputShape.shape` 翻译成 `AO_OutletLayout` 挂 `def.modExtensions`；`Bootstrap`/`TempOutputShapePatch` 优先使用，成功即不再 patch `RunTempControl`（AO 原生已含多格出风、效率、热侧倍率与除湿） |
| 6 | 区域接口 | **暂缓**：当前没有需要 `RegisterRegion(Isolated)` 的新功能；穹顶几何隔离继续用 `Rebuild` Postfix（见 §5） |
| 7 | 清理 `SourceSlots` | 吊扇、VTE `AcNetLedger`、CCC `AirLoopMapComponent` 全部改 `SetContinuousSource`；`AoBridge.Resolve()` 不再把 `SourceSlots`/`CompTempControlAtmosphere.slots` 作为可用性门禁 |

### 残留（有意保留）

- `Compat.VanillaTemperatureExpanded/AcNet/AcNetPatches.cs` 的 `AoRunPrefix` 仍读 `CompTempControlAtmosphere.slots` 并 `ZeroFrom(b, 1)`：这是清 AO **自己**写进 slot1..3 的热侧残留源，不属于我们的 key 空间；`_aoSlotsField` 为 null 时整段跳过，AO 重构后自动失效。
- `ProxyHeatRelayMapComponent.Inject` 仍读 `engine.Temperature.Read` / `GroundTemperature` / `ObstacleMask` 三个内部数组：地温与障碍暂无公共批量入口；空气通道已改为 `AddTemperatureDeltas`，`pendingTemperatureDeltas` 不再作为硬要求。

### 需要游戏内验证的项

温控出风口（Single/TwoCellHeater/TwoCellCooler/WallOffset）、空调除湿产水、吊扇、VTE 空调管网、CCC 出风方向与断电清零、ProxyHeat 温度注入、超维护盾与随身温控。

