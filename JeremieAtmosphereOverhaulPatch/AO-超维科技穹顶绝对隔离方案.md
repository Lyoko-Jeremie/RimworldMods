# AO-超维科技穹顶绝对隔离方案

> 日期：2026-11-17
> 性质：设计方案（不含代码修改）。前置文档：`AO-超维科技穹顶与生物圈语义分析.md`、`AO-超维科技五机制适配可行性分析.md`。
> 核心变化：穹顶从“壳 + 软补偿”改为**无壳 + 六通道硬固定**（绝对隔离），并统一 AO 的 `RoofMask`、把地温纳入固定。

---

## 0. 语义定义

| 项 | 定义 |
|---|---|
| 外部 → 内部 | **完全不影响**：域内每格每步被写回设定值，外部平流/扩散/松弛/风带来的扰动在求解前被抹掉 |
| 内部 → 外部 | **允许影响**：域内与域外之间保留正常面开度，内部高温/高湿/高压/高氧会自然向外扩散、平流；这是“漏出”的唯一表达 |
| 固定通道 | 温度、湿度、氧气、气压、风速、**地温**（六通道） |
| 不做什么 | 不注入 `obstacleMask`（不建壳）、不写原版 `map.roofGrid`（不挡光）、不建连续源（每格一个源太贵） |

**为什么无壳**：`obstacleMask=0` 会连“内部→外部”的通量一起阻断（`MapObstacleMaskBuilder.cs:48,110-115`），穹顶就变成“对外也不漏”，与“允许影响外界”冲突。硬固定的不对称性来自“无限功率补偿内部”，不需要几何隔离。

---

## 1. 总体结构

```
每 AO 步（SimulationIntervalTicks，默认 30）：
  Task 内、engineLock 内（StartSimulationTask）：
    演化链：AdvanceProvisional → ProjectAndFinalize → 外来源 → ①PublishSnapshot
    ① LocalAtmosphereEngine.PublishSnapshot Prefix（步末、Read→Snapshot 复制前）
       ├─ 遍历域格 int[]（缓存）
       ├─ 温度  → Temperature.Read[i] = target
       ├─ 湿度  → Moisture.Read[i] = targetRH/100 × VaporCapacity(targetTemp, settings)
       ├─ 氧    → AddOxygenPercent(i, target − Oxygen.Read[i])
       ├─ 气压  → StoredPressurePa[i] = targetPa − AmbientSurfacePressurePa
       │          Pressure.Read[i] = (targetPa − 100000) / pascals
       ├─ 地温  → GroundTemperature[i] = target
       └─ 风    → VelocityX.Read[i] = VelocityZ.Read[i] = 0
  掩膜重建时（masksDirty）：
    ② MapObstacleMaskBuilder.Rebuild Postfix
       └─ 穹顶内 RoofMask[i] = 1（只改 AO，不改 map.roofGrid）
```

---

## 2. 六通道写入表

| 通道 | 写入位置 | 公式 | 关键注意 |
|---|---|---|---|
| 温度 | `engine.Temperature.Read[i]` | `target` | `Advance` 每步先把 Read 演化再写回（`LocalAtmosphereEngine.cs:3918-3929`），所以必须在演化后钉住 |
| 湿度 | `engine.Moisture.Read[i]` | `targetRH/100 × AtmosphereThermodynamicKernel.VaporCapacity(targetTemp, settings)` | `RelativeHumidity` 是派生场（`:3348-3349`、`AtmosphereMapComponent.cs:549`），写 RH 会被下一步重算覆盖，必须写 `Moisture` |
| 氧气 | `engine.AddOxygenPercent(i, delta)` | `target − Oxygen.Read[i]` | 量化 byte + residual（`:3665-3678`）；写数组会绕过 residual，造成漂移 |
| 气压 | `engine.StoredPressurePa[i]` 与 `engine.Pressure.Read[i]` | `Stored = targetPa − AmbientSurfacePressurePa`；`Read = (targetPa − 100000)/pascals` | 基线在 `ProjectAndFinalize` 内重算（`:2986`、`:4017-4048`）；`pascals = AtmosphereColorUtility.CurrentSolverPressureToPascals()`；两者都写才能让本步快照与下一步基线一致 |
| 地温 | `engine.GroundTemperature[i]` | `target` | `ApplyGroundEnergy` 每步在其上叠加太阳/屋顶导热（`:3019+`），步末覆盖 |
| 风速 | `engine.VelocityX.Read[i]` / `VelocityZ.Read[i]` | `0` | 在 `ProjectAndFinalize` 之后写，覆盖投影、AirMover、JetTransport、目标风源的全部结果（`AtmosphereMapComponent.cs:2989-2995`） |

**为什么不在 Prepare 阶段写 pending 差值（对原方案的修正）**：

1. `AdvanceProvisional` 在 `ApplyTemperatureDeltas` 之后、`ProjectAndFinalize` 之前，会调用 `AtmosphereThermodynamicKernel.Advance` 再演化温度/湿度（`LocalAtmosphereEngine.cs:3918-3929`）；pending 只是“源”，写完后仍会被演化偏离，观感不是硬固定。
2. `temporalPatchManager.ExcludeOwnedSources` 会把 GC patch 拥有格（`Region==3`）的 pending 温度/湿度/速度清零（`TemporalPatchManager.cs:381-404`，调用点 `AtmosphereMapComponent.cs:2985`）→ 硬固定在这些格失效。
3. 步末写 Read 天然满足“外部影响被抹掉、内部影响留在外部”：内部整步演化照常、对外输运已发生在外部格上，步末只把内部读数写回目标。

**时序安全**：`PublishSnapshot` 的所有调用点都在 `engineLock` 内——Task body `AtmosphereMapComponent.cs:2973-3016`、`AddAtmosphereGas` `:1515-1533`、`FillMapAtmosphereGasPercentage` `:1535-1560`、`SetAmbientWind` `:3594-3605`、`CompleteFinishedSimulation` `:3077-3086`、`FinalizeInit/MapGenerated` `:1147-1170`；Prefix 无需再加锁，非步进的重复钉住无害。

**湿度需要 settings**：`AtmosphereMapComponent.settings` 是 private readonly 字段（`:58`），补丁用 `AccessTools.FieldRefAccess<AtmosphereMapComponent, LocalAtmosphereSettings>("settings")` 缓存读取。

---

## 3. RoofMask 统一

### 3.1 为什么必须统一

`RoofMask` 是 AO 的每格物理开关。玩家自建屋顶会让同一穹顶出现“部分有顶、部分无顶”的两套物理：

| 机制 | 无顶格 | 有顶格 | 代码 |
|---|---|---|---|
| 温度/湿度松弛 | 每步朝室外拉 | 不拉 | `AtmosphereMapComponent.cs:3381-3387` |
| 氧气松弛 | 每步朝环境氧拉 | 不拉 | `LocalAtmosphereEngine.cs:3558` |
| 环境风推动 | 速度被 nudged | 不 nudged | `:3493` |
| 湿度/热膨胀 | 不触发 | 触发 `NoteVaporVolume`/`NoteThermalExpansion` | `:3088-3092`、`:3012` |
| 相变 | 按露天（雨雪） | 按有顶（不降雨） | `:3925` |
| 地面太阳加热 | 直接吸太阳 | 只屋顶导热 | `ApplyGroundEnergy :3019+` |

混合状态下，硬固定虽然都能覆盖，但压力/湿度场会被两种处理分割，每步多一轮修正，且穹顶内会出现“一半淋雨、一半干燥”的观感。

### 3.2 做法

在 `MapObstacleMaskBuilder.Rebuild` 的 Postfix 里，按穹顶几何把 `engine.RoofMask[i]` 写成 **1**（已拍板）。**只改 AO 场，不改 `map.roofGrid`**，因此：

- 原版光照、太阳能、种植、房间判定不受影响；
- 玩家自建屋顶仍按原版挡光（这是原版行为，不是统一造成的）；
- AO 内部统一按“有顶”处理。

### 3.3 选“有顶”的代价

- 有顶格温度/湿度 delta 会转成压力膨胀/水汽体积，压力硬固定会覆盖，但瞬态多一轮；
- 无屋顶的格在 AO 里也不再有雨雪、不直接吸太阳（地温改走屋顶导热参数）；
- 观感差异：原版光照透进来，但 AO 里不淋雨——这是“有顶”语义的直接结果，需要接受。

### 3.4 植物生长不受影响

AO 没有植物生长补丁：只有 `PlantWindSway`（视觉，`:105,1192`）与 `RebuildPlantStatsUnsafe`（`:2797-2850`，只读 `def`/`Growth` 写蒸腾/遮荫源，不读 `RoofMask`）。原版植物生长读 `map.glowGrid`（原版屋顶决定）与 `GenTemperature`（硬固定后适宜），所以统一 `engine.RoofMask` 不影响生长。**前提是不要真写 `map.roofGrid`**——超维 `applyRoof` 默认 `false`，别在方案里打开。

---

## 4. 屋顶风载豁免

玩家自建屋顶会进 `WindRoofLoad`（成员判定 `roofMask>0.5 && IsConstructed(roofGrid.RoofAt)`，`WindRoofLoad.cs:145-150`）。风速钉零只免掉风项，穹顶内 101 kPa 对穹顶外 0 kPa 的 `Overpressure` 压力项仍在，屋顶会被压塌（`:378-379`）。

因此：**在 `WindRoofLoad.Tick` 的格循环里，若格属于穹顶域，直接跳过**（含风项与压力项）。这与“护盾内屋顶不损坏”是同一个豁免点，可以共用域索引。

---

## 5. “漏出”如何自然发生

- 内部固定 21℃/60%/21% O₂/101 kPa，外部 -20℃/0% O₂/0 kPa；
- 面开度保持原样（无壳），AO 的扩散与平流会从内部向外部输运热量、水汽、氧气、质量；
- 内部每步被写回设定值，表现为“持续向外泄漏、内部不下降”；
- 外部把冷空气推入内部时，在下一步求解前被覆盖，表现为“内部不受外部影响”。

**不对称性的来源是“无限功率补偿”，不是几何。** 因此内部向外的影响是真实的（外部会缓慢被加热/加湿/加压），内部向内的被补掉。

---

## 6. 性能预算

### 6.1 每步成本

域格 D × 6 次写入（温度/湿度/氧/压力两处/地温/风速两处）≈ 8D 次浮点写 + D 次氧量化调用。`VaporCapacity` 只依赖目标温度与 settings，**每步只算一次**并缓存（所有格温度相同）。

| D | 每步操作 | 占红线（12.5 万/步） |
|---|---|---|
| 121（11×11 穹顶） | ~1k | 0.8% |
| 1,000 | ~8k | 6.4% |
| 5,000 | ~40k | 32% |
| 10,000 | ~80k | 64% |

### 6.2 优化清单

1. **域格 `int[]` 缓存**：穹顶尺寸/开关变化时重建，每步只遍历该数组；
2. **一次遍历六通道**：同一循环内写，缓存行友好；
3. **直接写 Read 数组**：不走 `AddPressureDelta`（每格锁 + `RecordImpulse`）、不用 `RegisterContinuousSource`（源字典遍历 + Owner 校验）、也不走 `AddTemperatureDelta` 的 `ObstacleMask` 分支；
4. **湿度常量缓存**：`VaporCapacity` 每步一次，循环内只做乘法；
5. **只在 AO 步进时写**：跟随 `SimulationIntervalTicks`（`LocalAtmosphereSettings.cs:12`，默认 30），不额外挂 tick；
6. **跳过墙格**：`ObstacleMask<=0` 的格不写（墙格不是空气格）；
7. **面积监控（暂不降级）**：先做域格缓存，实机测量 D=1000/5000 的实际耗时后再决定是否需要“边界 2 层硬固定 + 内部按误差阈值写”或 2× 步长写入。

### 6.3 一次性成本

- `RoofMask` 注入：每次掩膜重建 O(D)，与地图结构变化同频，不随步进增长；
- 域索引查询：O(1)（`OmniForceFieldDomeNetworkManager.IsDomeCell` / `TryGetPropsAt`）。

---

## 7. 风险清单

1. **不守恒**：内部是无限源/汇，质量与能量凭空补足；这是“绝对隔离”的固有代价，必须在设计说明里写明。
2. **热膨胀/水汽体积耦合（已消除）**：步末写 Read 不经过 `ApplyTemperatureDeltas`/`ApplyMoistureDeltas`，不触发 `NoteThermalExpansion`/`NoteVaporVolume`（`LocalAtmosphereEngine.cs:3012,3090-3092`），没有膨胀/水汽压力噪声。
3. **氧量化抖动（已接受）**：`AddOxygenPercent` 量化到 byte + residual（`:3665-3677`），设定 21% 时实际在 20.5~21.5% 间抖动；不影响玩法，不额外处理。
4. **观感**：原版光照透进来但 AO 里不淋雨、不刮风、植物不摆；玩家会看到“透明罩里恒温”的效果。
5. **内部设备被抹平（已接受）**：域内任何发热/产湿/耗氧设备的效果每步被覆盖，设备等于失效；这是“每格固定”的直接后果，需要在 UI/说明里告知玩家。
6. **外部仍会缓慢受影响**：内部持续向外泄漏，外部温度/湿度/压力会缓慢漂移；若有多个穹顶，叠加效应需要观察。
7. **写入时序**：六通道全部在 `PublishSnapshot` Prefix 内写（Task 内、`engineLock` 内、`ProjectAndFinalize` 之后）；写 Read 而非 pending，不受 `AdvanceProvisional` 后续演化与 `ExcludeOwnedSources` 清零影响（`AtmosphereMapComponent.cs:2973-3016`、`TemporalPatchManager.cs:381-404`）。
8. **GC 重叠与 `ReconcileOpenedCells`**：不注入 `obstacleMask`，不涉及 `ReconcileOpenedCells`；`RoofMask` 注入放 `MapObstacleMaskBuilder.Rebuild` Postfix，确保 `windRoofs.Rebuild` 读到统一值。若穹顶与 Good Climate 的 temporal patch 域重叠，GC 在 `RebaseAfterGlobalProjection` 写入的 Read 会被我们的 Prefix 覆盖 → 绝对隔离优先，GC 在穹顶内失效（需在说明里写明）。

---

## 8. 实现顺序

1. **阶段 A（核心）**：域索引（穹顶格 `int[]`，随 SetRadius/开关重建）+ 六通道硬固定（`PublishSnapshot` Prefix 步末钉住，无 RoofMask 统一、无壳）。先验证“外部不影响内部、内部影响外部”。
2. **阶段 B（一致性）**：`RoofMask` 统一为 1 + `WindRoofLoad` 豁免穹顶内屋顶。
3. **阶段 C（扩展）**：护盾四判定点豁免；生物圈 Area 复用同一六通道写入（无 RoofMask 统一、无屋顶豁免）；随身补丁点（气压/氧/风）。
4. **阶段 D（性能）**：稀疏化、实机性能测量（D=1000/5000）、UI 设定值（温/湿/氧/压/风/地温）。面积降级策略待实测后再定。

---

## 9. 已拍板决策（2026-11-18）

1. **面积降级：暂不实现**。先做域格缓存 + 稀疏化，实机测量 D=1000/5000 的耗时后再决定是否降级。
2. **氧量化抖动：接受**。直接使用 `AddOxygenPercent(i, delta)`，不自行维护 residual。
3. **域内设备被抹平：接受**。不设设备白名单，域内发热/产湿/耗氧一律被每步覆盖。
4. **`RoofMask` 统一为 1**。只改 `engine.RoofMask`，不写 `map.roofGrid`。
5. **不实现“局部风/局部温度”例外**。穹顶内一律绝对隔离开关。

### 仍然开放（实现细节，非方向性）

- 域索引缓存的失效时机（`SetRadius`/`SetSize`/开关/CellRect 变化）。
- `VaporCapacity` 每步一次的计算结果放在哪里复用（Prefix 内局部变量即可，不需要 MapComponent 字段）。
- 实施顺序：阶段 A 先做穹顶，验证通过后再扩到生物圈/护盾/随身。

---

## 10. 目标值：地图级共享 + 建筑 Gizmo（2026-11-18 拍板）

### 10.1 数据模型

新增 `MapComponent_OmniAtmosphereTargets`（补丁项目内，每地图一份）：

| 字段 | 默认值 | 范围 |
|---|---|---|
| `targetTemperature` | 21 ℃ | -100 ~ 120 |
| `targetHumidity` | 60 % | 0 ~ 100 |
| `targetOxygenPercent` | 21 % | 0 ~ 100 |
| `targetPressureKpa` | 101.325 kPa | 0 ~ 150 |
| `targetGroundTemperature` | 跟随 `targetTemperature` | -100 ~ 120 |
| `groundFollowsAir` | true（**AO 模式下恒为 true，不提供单独设定**） | — |

- 同一地图上所有穹顶与生物圈共用这一份目标值，改一处全局生效。
- `ExposeData` 用**新 key**（老存档缺 key 时自然取默认，不需要迁移；参见“改默认值对老存档无效”的教训）。
- 写入公式：气压通道用 `targetPressureKpa × 1000 − AmbientSurfacePressurePa`；湿度写 `Moisture = targetHumidity/100 × VaporCapacity(targetTemperature, settings)`；地温通道每步写 `targetTemperature`（跟随气温，玩家不单独设值）。

### 10.2 UI

- Postfix `CompOmniForceFieldDome.CompGetGizmosExtra`（`OmniForceFieldDome.cs:232`）与 `CompBiosphere.CompGetGizmosExtra`（`CompBiosphere.cs:244`），各追加一个 `Command_Action`「大气目标」，icon 复用温度类图标。
- 点击打开 `Dialog_OmniAtmosphereTargets`：4 行滑条（温度/湿度/氧气/气压）+ 地温行只读显示“跟随气温 = X ℃” + 确定/取消；滑条实时写 MapComponent，取消时恢复。
- 所有显示字符串进 `Languages`（i18n），不在代码里硬编码。

### 10.3 与超维现有温度 UI 的冲突（必须处理）

超维现在的温度实现有两套：

1. **生物圈**：实例字段 `controlTemperature` + `targetTemperature`（`CompBiosphere.cs`），Gizmo 开关 + `Dialog_CompBiosphere_Temperature`，通过 `Patch_GenTemperature_GetTemperatureForCell`（`Patch_CompBiosphere.cs:472-486`）覆盖原版单格温度。
2. **穹顶**：`props.targetTemperature`（**Def 级共享**，改它会影响所有地图的穹顶），通过 `Patch_OmniForceFieldDome_Temperature`（`OmniForceFieldDome.cs:1349-1369`）覆盖原版单格温度。

地图级目标一旦生效，AO 场与这两套原版读数会分裂（AO 读地图级、原版读实例/props）。**已拍板处理方案**：

- **地图级面板是 AO 场的唯一真值**；
- **AO 模式下穹顶不再读 `props.targetTemperature`**：`Patch_OmniForceFieldDome_Temperature` 的目标值改为读 `MapComponent_OmniAtmosphereTargets.targetTemperature`；
- **生物圈原有温度 Dialog 变灰并给鼠标提示**：`CompBiosphere.CompGetGizmosExtra` 返回的温度 `Command_Action` 替换为禁用版（`Disable(reason)`），提示「由『大气目标』面板统一设置」；生物圈 `targetTemperature` 字段仅保留存档兼容，不参与 AO 场；
- 生物圈「控制温度」开关（`Command_Toggle`）同样在 AO 模式下禁用，避免出现两套开关。

### 10.4 新增 Gizmo 的入口判断

- 只有建筑所属地图存在 `AtmosphereMapComponent`（AO 已加载并初始化）时才显示「大气目标」Gizmo；没有 AO 时隐藏，避免无意义的 UI。
- 生物圈的 Area 可能覆盖多张地图之外的格（Area 是单地图的），MapComponent 天然按地图隔离，无需额外处理。

---

## 11. 实现现状（阶段 A，2026-11-18）

### 11.1 已实现

宿主 `Bridge/AtmosphereFieldBridge.cs`：

- 新增六通道反射成员与公开访问器：`GetMoistureRead`、`GetRelativeHumidityRead`、`GetOxygenRead`、`GetOxygenResidual`、`GetPressureRead`、`GetStoredPressurePa`、`GetVelocityXRead`、`GetVelocityZRead`、`GetRoofMask`、`GetAmbientSurfacePressurePa`、`GetSettings`、`CalculateVaporCapacity`、`GetSolverPascalsPerUnit`、`SixChannelAvailable/SixChannelFailure`。
- 六通道能力独立于既有 `Available` 判定，缺失只降级隔离模块。

兼容项目 `Source/Compat.OmniCrafter`：

| 文件 | 职责 |
| --- | --- |
| `MapComponent_OmniAtmosphereTargets.cs` | 地图级目标值（4 字段 + Scribe 新 key）；`MapComponentTick` 每 30 tick 在主线程预热引擎上下文与格缓存 |
| `OmniDomeZoneCache.cs` | 穹顶矩形 + 生物圈 Area 的扁平格索引缓存；主线程构建、模拟线程只读 |
| `Patch_AbsoluteIsolation.cs` | `LocalAtmosphereEngine.PublishSnapshot` Prefix：写温度/湿度/相对湿度/气压/偏置气压/地温/风/氧/氧余数/`RoofMask=1`；跳过 `ObstacleMask<=0` |
| `Patch_OmniAtmosphereTemperature.cs` | `GenTemperature.GetTemperatureForCell` / `TryGetTemperatureForCell` Postfix（Priority 90），按穹顶/生物圈返回地图目标温度 |
| `Patch_OmniAtmosphereGizmos.cs` | 穹顶/生物圈追加「大气目标」Gizmo；生物圈温度与控温开关 `Disable(reason)`；`DirtyCache` 触发缓存失效 |
| `Dialog_OmniAtmosphereTargets.cs` | 880×430 面板：温度/湿度/氧气/气压各一行「滑条 + 数字输入框 + 单位」，气压行带「跟随地图大气」勾选与当前环境值，地温只读行 |
| `OmniCompatBootstrap.cs` | `[StaticConstructorOnStartup]` 安装；`Map.Dispose` 清理静态缓存 |

### 11.2 线程模型（关键）

AO 的模拟在 `Task.Factory.StartNew` 里跑，主线程不等待。因此：

- **禁止在 `PublishSnapshot` Prefix 里枚举建筑、Area 或 `Find.Maps`**；
- 引擎上下文与格数组由 `MapComponentTick` 在主线程构建，模拟线程只做字典/数组读取；
- 目标值字段是 4 字节 float 单写单读，无撕裂风险。

### 11.3 未实现（后续阶段）

- 条目 1「超维建筑不受风影响」：未加 `BuildingAtmosphereExtension` 的 `airFillPercent=0.01`。
- 条目 2「护盾保护范围」：未在 `WindStructuralLoad` / `WindPassableLoad` / `WindRoofLoad` / `WindTransport` 四个判定点做 O(1) 豁免。
- 条目 5「随身」：未实现气压/氧/风通道覆盖。
- 屋顶风载豁免：默认目标气压 101.325 kPa 与环境一致，压力项为零，不会压塌；玩家把目标气压调到显著偏离环境时，玩家自建屋顶仍可能受压。
- 性能实测与面积降级阈值。

> 2026-11-18 第三轮：条目 1、2、5 已实现（见 §11.5）；屋顶风载豁免随条目 2 的护盾豁免一并覆盖护盾内屋顶，穹顶内屋顶仍按「目标气压与环境一致」处理。

### 11.4 界面改造（2026-11-18 第二轮）

用户要求：气压单位 kPa → AO 对外的 hPa；提供「自动跟随地图大气」；每个变量在滑条之外再给数字输入框；面板宽度两倍。

落地：

- 目标值字段 `TargetPressureKpa` 改为 `TargetPressureHpa`，默认 1013.25；Scribe 新 key `aoOmniTargetPressureHpa`，读档时兼容旧 key `aoOmniTargetPressureKpa`（×10 迁移）。
- 新增 `PressureFollowsAmbient`（默认 true）：为 true 时每步用 `Component.ComputeAmbientSurfacePressure(map)` / 100 作为目标气压，滑条与输入框灰显但显示当前环境值，避免手填值与环境压差过大而在穹顶边缘产生巨大气流。
- 新增 `ResolveTargetPressurePa(ambientPa)`、`AmbientPressureHpa`（`cachedEngine → Bridge.GetAmbientSurfacePressurePa/100`）。
- `Patch_AbsoluteIsolation` 改用 `ResolveTargetPressurePa`；`Patch_OmniAtmosphereTemperature` 不需要气压。
- 面板：`InitialSize` 880×430（原 440×430）；`DrawRow` 统一「标签 + 滑条 + 输入框 + 单位」，输入框接受 `InvariantCulture` 数字，越界 `Mathf.Clamp`；禁用行不接收输入，只显示文本。
- i18n 新增 `AOPatch_Omni_PressureAuto`、`AOPatch_Omni_PressureAmbient`（中英）。
- 编译：宿主无改动；兼容项目 `dotnet build -c Debug` 0 警告 0 错误，dll 部署到 `1.7/Compat/OmniCrafter/Assemblies/`。

## 12. 条目 1 / 2 / 5 实现（2026-11-18 第三轮）

### 12.1 条目 1：超维建筑不受风

- 不是 XML 注入 `airPassable`，而是 patch `BuildingAtmosphereExtension.AirFill(ThingDef)` 的 Postfix：凡 `def.building != null` 且 `modContentPack.PackageId == Jeremie.Fully.Automatic.OmniCrafter` 的 def，AirFill 返回 `0.01f`。
- 效果：`WindPassableLoad.RebuildBuildings` 的筛选条件 `AirFill >= max(0.001, WindDamageMinFillPercent)` 不成立，超维建筑整体退出「可穿透物/家具」风载荷名单（不被打伤、不被吹动、不被吹走）。
- **为什么不注入 `airPassable=true`**：`MapObstacleMaskBuilder.AirCanMoveThrough` 在 `Fillage == Full` 时会因 `extension.airPassable` 直接返回 true，把幻影墙（fillPercent=1）从 obstacleMask 里抹掉，破坏它原本的挡风/挡气能力。`AirFill` 出口只影响风载荷名单与 `ComputeDampingScale`，不动阻挡几何。
- 副作用：超维建筑的 `DampingMask` 系数从 `fillPercent*1.6` 降到 `0.01*1.6`，风穿过建筑格时衰减更小；这是「该建筑不参与风阻」的必然结果。

### 12.2 条目 2：护盾覆盖范围风豁免

四个判定点全部在 `AtmosphereMapComponent.MapComponentTick`（主线程），可以安全访问 `OmniInterceptorTracker`：

| 判定点 | patch 位置 | 做法 |
| --- | --- | --- |
| 建筑结构 | `WindStructuralLoad.ComputeChainLoads` Postfix | 护盾内成员的 `demand`/`pressureLoad`/`pendingDamage` 清零 |
| 可穿透物/植物 | `WindPassableLoad.Evaluate` Postfix | 护盾内成员的 `PendingAction`/`LastLoad`/`LastRiskPerHour` 清零，Apply 不再执行 |
| 屋顶 | `WindRoofLoad.ApplyRemovals` Prefix | 从 `pending` 列表剔除护盾内格；全剔除时跳过原方法（避免 `pendingCount % 0`） |
| 物品/pawn 抛飞 | `WindTransport.TryLaunch` Prefix / `ThrowCollectedPawns` Prefix | 护盾内物品直接 `__result=false`；待抛列表移除护盾内 pawn |
| pawn 踉跄 | `Pawn_StanceTracker.StaggerFor` Prefix | 护盾内或随身 pawn 直接跳过 |

护盾覆盖判定用 `OmniInterceptorTracker.IsCellWithinShield(cell, out _)`（每格 O(1) 数组缓存，排除 hostility 检查），外加 `ConditionalWeakTable<Map, TrackerHolder>` 缓存 MapComponent 查找。

### 12.3 条目 5：随身 pawn

- 气压/氧：`PawnAtmosphere.TrySample` Prefix，随身启用的 pawn 直接返回地图目标值（`ResolveTargetPressurePa` / `TargetOxygenPercent`），令 `TryGetBodyAir`、套装 UI、呼吸供给整条链路一致，返回 false 阻止再读 AO 场。
- 风：踉跄与抛飞复用 `IsPawnWindProtected`（随身启用 或 护盾内）的豁免。
- 温度：超维自身的 `Thing.AmbientTemperature`（Priority.Last）与 `SafeTemperatureAtCell` Postfix 已覆盖，无需重复。
- 「随身启用」判定用 `PersonalTemperatureUtility.TryGetOverride(pawn, cell, map, out _)`，即超维的原生设置，不另存状态。

### 12.4 新增文件

| 文件 | 职责 |
| --- | --- |
| `OmniProtection.cs` | 护盾格判定、随身判定、Map→Tracker 缓存 |
| `Patch_OmniBuildingAtmosphere.cs` | 条目 1，`AirFill` Postfix |
| `Patch_OmniWindExemption.cs` | 条目 2，四通道 + 踉跄 |
| `Patch_OmniPawnProtection.cs` | 条目 5，`PawnAtmosphere.TrySample` Prefix |

`OmniCompatBootstrap` 调整顺序：条目 1/2/5 只依赖 AO 在场，先安装；六通道能力缺失时只停用绝对隔离与目标值面板。

编译：`dotnet build Source/Compat.OmniCrafter -c Debug` 0 警告 0 错误，dll 部署到 `1.7/Compat/OmniCrafter/Assemblies/`（03:21）。

### 12.5 护盾掩膜缓存（2026-11-18 第四轮）

按 AO 的检查间隔把护盾覆盖缓存成 `bool[]`，避免每个风害成员都做一次 tracker 查询：

- `OmniProtection` 维护 `Dictionary<Map, ShieldMask>`；`ShieldMask` 含 `bool[] Cells`（长度 = `NumGridCells`）、`BuiltTick`、`Interval`。
- 刷新条件：`GenTicks.TicksGame - BuiltTick >= Interval`，`Interval` 取自 AO `LocalAtmosphereSettings.WindDamageCheckIntervalTicks`（通过宿主桥 `AtmosphereFieldBridge.GetSettings` 反射读取，取不到时用 30）。
- 重建只遍历护盾包围盒（矩形用 `OccupiedRect`，圆形用 `Radius` 包围盒 + `IsCellInside` 逐格），**不扫全图**；`Array.Clear` 复用旧数组。
- 护盾开关/尺寸变化走 `OmniInterceptorTracker.DirtyCache`，其 Postfix 调 `OmniProtection.MarkAllDirty()` 立即置脏，下一次判定就重建，不等满一个间隔。
- 批量判定（结构成员、可穿透物、屋顶、物品发射）读掩膜 O(1)；pawn 类低频单点判定仍走 `IsShieldProtectedLive` 实时查超维 `cellCache`，避免掩膜间隔导致踉跄误判。
- `Map.Dispose` 时 `OmniProtection.Forget` 清理掩膜与 tracker 缓存。

## 13. 多线程审查（2026-11-18 第五轮）

审查范围：`Source/Compat.OmniCrafter/` 全部 9 个 .cs 文件 + 宿主 `Bridge/AtmosphereFieldBridge.cs`。
方法：对每个 Harmony 补丁目标正向查 AO 与原版的调用点，确认其真实执行线程；逐条核对所有静态缓存（`ConditionalWeakTable`、`Dictionary`、`bool[]` 掩膜、静态字段）的读写线程；单独检查唯一在 AO 后台 Task 内执行的代码路径。

### 13.1 结论

只有**一个**补丁真正在 AO 的模拟 Task 里执行，即绝对隔离 Prefix；它读取的对象全部是主线程预先构建或线程安全的。其余补丁点全部在主线程被调用，不存在数据竞争。

### 13.2 唯一后台路径：绝对隔离 Prefix

`Patch_AbsoluteIsolation.cs:120-228` 挂在 `LocalAtmosphereEngine.PublishSnapshot` 上，运行在 `AtmosphereMapComponent.cs:2966` 的 `Task.Factory.StartNew` 内。它只访问：

| 访问对象 | 线程安全性 |
| --- | --- |
| `Contexts` 字典 | 全程 `lock (Sync)`（`:128-131`），主线程 `Register/Forget` 同一把锁 |
| `OmniDomeZoneCache.GetCells` | 锁内只读查表，返回已构建完成的不可变 `int[]`；`Build` 在锁外、替换在锁内（`OmniDomeZoneCache.cs:56-100`） |
| `targets.Target*` 字段 | 32 位原子读；最坏情况延迟一步生效，无崩溃 |
| `AtmosphereFieldBridge.GetSettings` | 读 AMC 的 `private readonly settings` 字段，构造后不变 |
| `CalculateVaporCapacity` | 纯函数，转发 `AtmosphereThermodynamicKernel.VaporCapacity`（`LocalAtmosphereEngine.cs:3465-3468`） |
| 引擎 `Read` 数组写入 | 在 `PublishSnapshot` 内、`engineLock` 内，此时引擎其余 `Parallel.For` 未启动 |

### 13.3 已修竞态

1. **`Patch_OmniAtmosphereTemperature.GetRefs`**（原 `:89` 的 `Refs.Add(map, refs)`）
   net48 的 `ConditionalWeakTable.Add` 在键已存在时抛 `ArgumentException`。Harmony 补丁链覆盖全游戏，若第三方 mod 或原版从后台线程调用 `GenTemperature.GetTemperatureForCell`，同时主线程首次查询同一张地图，就会直接抛异常。
   已改为 try/catch 兜底（`Patch_OmniAtmosphereTemperature.cs:89-99`），并新增 `Warm(Map)`（`:108`），在 `MapComponentTick` 中提前做组件查找（`MapComponent_OmniAtmosphereTargets.cs:136`）。
2. **`OmniProtection.GetTracker`**（原 `:72` 的 `Trackers.Add`）
   同类问题；虽只在主线程调用，仍做同样兜底（`OmniProtection.cs:71-84`）。

### 13.4 未修风险（当前不触发）

- **超维生物圈静态缓存无锁**：`CompBiosphereManager.GetBiosphereAt` 使用静态 `lastMap/lastGrid`（`Patch_CompBiosphere.cs:230-246`），底层 `biosphereGrid[map] = grid`（`:138`）由主线程写。只有温度 Postfix 在后台线程被调用时才会读到它。AO 自身唯一的 `GenTemperature.GetTemperatureForCell` 调用点是 `Patches/3PawnEnvironmentPatches.cs:23`，由主线程 `SafeTemperatureAtCell` 触发，故实际不触发。若日后确认有后台调用者，可加 `UnityData.IsInMainThread` 守卫（`Verse/UnityData.cs:29-32`），或自建生物圈格缓存以绕开超维字典。
- **`ctx.Map.Disposed` 与写数组之间的窗口**（`Patch_AbsoluteIsolation.cs:133`）：最坏情况是把数据写进一个已废弃引擎的数组，无副作用。
- **`targets` 字段无 volatile**：仅「改动延迟一步生效」的可见性问题，非崩溃。
- **`AtmosphereFieldBridge.Resolve()` 的 `_resolved` 无锁**：Bootstrap 已在主线程预先解析（Install 时读 `SixChannelAvailable`），Task 内只读，无实际风险。

### 13.5 其余补丁点的主线程证据

| 补丁目标 | 调用点 | 线程 |
| --- | --- | --- |
| `BuildingAtmosphereExtension.AirFill` | `MapObstacleMaskBuilder.cs:201`、`WindPassableLoad.cs:104`，仅由 `AtmosphereMapComponent.cs:3302-3316` 的 `RebuildObstacleMask` 触发 | 主线程（`MapComponentTick`） |
| `WindStructuralLoad.ComputeChainLoads` | `WindStructuralLoad.cs:451`（Tick 内） | 主线程 |
| `WindPassableLoad.Evaluate` | `WindPassableLoad.cs:235-239`（Tick 内） | 主线程 |
| `WindRoofLoad.ApplyRemovals` | `WindRoofLoad.cs:323`（Tick 内） | 主线程 |
| `WindTransport.TryLaunch / ThrowCollectedPawns / StaggerFor` | `WindTransport.cs:186 / 198 / 330` | 主线程 |
| `PawnAtmosphere.TrySample` | `1SuitStatusGizmo.cs:46`、`BreathingSupplyHediff.cs:35`、`PawnAtmosphere.cs:51` | 主线程（UI / hediff / 套装） |

`OmniProtection` 的 `Masks`/`Trackers` 只在风害 tick 与 `StaggerFor` 中访问，均为单线程；宿主桥的反射参数数组用 `[ThreadStatic]` 池化，无共享缓冲。

### 13.6 编译

`dotnet build Source/Compat.OmniCrafter/Compat.OmniCrafter.csproj -c Debug` 0 警告 0 错误；dll 部署到 `1.7/Compat/OmniCrafter/Assemblies/`（03:38:42）。

## 14. 光照层 alpha 修复（2026-11-18 第六轮）

超维科技两处 `VisualGlowAt` 补丁都把 alpha 当作亮度写入，导致 `SectionLayer_LightingOverlay` 把该格渲染成完全不透明色块（生物圈：灰块；穹顶：过曝白块）。已统一按「只抬 RGB、不动 alpha」修复：

| 位置 | 修复前 | 修复后 |
| --- | --- | --- |
| 生物圈 `Patch_CompBiosphere.cs` `VisualGlowAtCell/AtIndex` | `GetBiosphereAt != null` 即覆盖（含 None 模式），`__result.a = 255` | 先判 `lightingMode == None` 直接返回；只抬 `r/g/b` |
| 穹顶 `OmniForceFieldDome.cs` `WithDomeGlow` | `a = max(a, light)`；SunLamp 的 `glow=1f` 使 light 恒 255 | 只抬 `r/g/b` |
| 穹顶 `OmniForceFieldDome.cs` `WithSkyGlow` | `a = max(a, sky)`；白天 `sky≈1` → alpha 255 | 只抬 `r/g/b` |

- 生物圈 dll：2026-11-18 11:19；穹顶 dll：2026-11-18 14:28（同一次 1.7 构建，962560 字节）。
- 未动：`Patch_OmniForceFieldDome_GroundGlowAt`（只改 float 返回值，语义正确）、`Patch_OmniForceFieldDome_IndoorMask`（透光屋顶取消室内阴影，语义正确）、`OmniForceFieldDomeGlobalGroundGlowNotPatched`（死代码，不参与 patch）。
- 待验证：穹顶 SunLamp 白天 / Lamp 黄昏 / 默认透光 + 非自然屋顶三种组合下不再出现不透明色块。
- 1.6 产物：`1.6/Assemblies/FullyAutomaticOmniCrafter.dll`（2026-11-18 14:46:24，963072 字节）。默认路径即可编译（`dotnet build -c Debug -p:RimWorldVersion=1.6`），`..\..\..\..\..\RimWorld-16\` 从项目目录解析到 `steamapps\common\RimWorld-16`，无需显式传 `RimWorldDirV16`。
