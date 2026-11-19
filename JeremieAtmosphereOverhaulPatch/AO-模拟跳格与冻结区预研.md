# AO 模拟跳格与冻结区预研

> 分析对象：Atmosphere Overhaul（workshop `3830470974`）1.7 分支。
> 反编译源码根：`f:/RimworldCodeIL/AtmosphereOverhaul/`（主 dll，MVID `4E9F1EAE…`）。文中 `文件:行` 均以此根为基准，省略前缀。
> 本文回答用户 2026-11-19 的预研问题：**能否让模拟跳过部分格、对指定格固定值，从而稳定特定区域环境状态并大幅降低计算量？**
> 口径与既有文档 `AO-模块功能与性能分析.md` 一致：**不承诺毫秒数**，成本以"可数遍历次数"给出，`N = engine.Size = map.Size.x × map.Size.z`（250×250 = 62 500）。

---

## 0. 结论摘要

**技术上可行，但不是"改一处就省"的改动；收益与改动量强相关，且存在三种语义不同的实现路径。**

| # | 结论 | 关键证据 |
|---|---|---|
| 1 | **现有实现（每步覆写）收益为 0**：它只是全量模拟后再写一遍。 | `Source/Compat.OmniCrafter/Patch_AbsoluteIsolation.cs`（PublishSnapshot Prefix 写六通道） |
| 2 | **默认参数下压力求解只占约 23%**，不是主要瓶颈：`simulationIntervalTicks=6` → `PressureIterations=24`，每粗步 ≈ `24N + 81N = 105N`。只跳过 SOR 的收益上限是 `0.23 × f`。 | `AtmosphereOverhaulSettings.cs:177`（默认 6）、`1.6/Defs/AtmosphereTuning.xml:5-7`（base=30、iterations=16）、`AtmosphereTuningManager.cs:251-257` |
| 3 | **AO 自带三套"跳过/降维"机制都不能直接用**：`PinSkyPressure` 只在 non-compressible 分支；`DeflateEnclosures` 仍逐格回灌；`TemporalPatchDomain` 被 `ScaleForInterval` 在 compressible 默认配置下**强制关闭**。 | `LocalAtmosphereEngine.cs:308-334`、`:5005-5153`、`AtmosphereTuningManager.cs:253-254` |
| 4 | **几何隔离（把冻结区 obstacleMask 置 0）是改动最少、收益最大的路径**：所有物理 pass 都有 `ObstacleMask <= 0` 短路，一处 Postfix 即可让全链路跳过冻结区。代价是语义变成"实心固体"（对外无质量/能量交换），需要额外读出覆盖层。 | `LocalAtmosphereEngine.cs:3762`、`:4476`、`:3818-3869` 等各 pass 的 mask 短路；`MapObstacleMaskBuilder.cs:82`（faceOpen = m[i]*m[i+1]） |
| 5 | **保留空气语义的全链路跳过（方案 B）需要改约 20 处**，且必须同时处理守恒缩放与压力回写，否则冻结值会被冲掉或数值发散。 | 见 §5、§6 |
| 6 | **推荐路线**：几何隔离为主 + `PackedSweep` 单点跳过 + 读出覆盖层；与现有"穹顶绝对隔离"语义完全一致，风险最低。 | 见 §4 |
| 7 | **降 interval 的杠杆仍大于跳格**：interval 6→30 约省 80% 每 tick 计算量；跳格在冻结面积 30% 时约省 30%。两者可叠加。 | 既有文档 `AO-模块功能与性能分析.md` §4 |

---

## 1. 每粗步到底在算什么

### 1.1 入口与顺序

`LocalAtmosphereEngine.cs:2932-2938`：

```
Tick() = AdvanceProvisional(...)      // 2932-2968
       + ProjectAndFinalize(...)      // 2970-2994
```

`AdvanceProvisional`（2940-2968）：

```
UpdatePressureMode(2944)
DiffuseVelocity(2945)
AdvectVelocity(2946)
ApplyCoriolis(2947)
爆炸余压扩散/衰减(2948-2952)
BeginScalarConservation(2953)
LimitScalarTraceToWalls(2954)
AdvectScalar ×3（温度/湿度/雾，2955-2957）
AdvectOxygen(2958)
RestoreWindAfterScalarTransport(2959)
EndScalarConservation(2960)
DiffuseScalar ×2（温度/湿度，2961-2962）
DiffuseOxygen(2963)
BeginPhaseChangeCapture(2964)
UpdateHumidityAndThermodynamics(2965)
ClampThermodynamicFields(2966)
EndPhaseChangeCapture(2967)
```

`ProjectAndFinalize`（2970-2994）：

```
RefreshSealedRoomsIfNeeded(2975)
AddPressureOdditySources(2976)
AddThermalExpansionSources(2978)
AddWallLeakSources(2980)
Project(2981)
DampVelocity(2983)
ApplyOpenBoundarySponge(2984)   // 空函数
EnforceBoundaries(2985)
ApplyThermodynamicPressureBaseline(2986)
ApplyExplosionPressureResidue(2987)
EnforceBoundaries(2988)
ClampVelocityMagnitude(2989)
ClampVentVelocities(2990)
ImprintChokedJets(2992)
ApplyPressureOddityRealized(2993)
```

### 1.2 遍历形态

所有主物理 pass 都是**全院扫描**：

```csharp
Parallel.For(1, Height - 1, z => {
    for (int x = 1; x < Width - 1; x++) { ... }
});
```

且绝大多数先做 `if (ObstacleMask[i] <= 0) { 写 0 / continue; }`。
关键位置：`DiffuseVelocity:3818-3869`、`AdvectVelocity:3470-3504`、`AdvectScalar:3506-3532`、`AdvectOxygen:3534-3573`、`DiffuseScalar:3871-3905`、`DiffuseOxygen:3590-3633`、`UpdateHumidityAndThermodynamics:3907-3934`。

少数 pass 是 `Parallel.For(0, Size)` 或串行 `for (0..Size)`，也同样有 mask 判断或直接全量写：`ApplyCoriolis:3936-3958`、`ClampVelocityMagnitude:3986-4004`、`ClampThermodynamicFields:4006-4015`、`ApplyThermodynamicPressureBaseline:4017-4046`、`DampExplosionPressureResidue:4048-4073`、`LimitScalarTraceToWalls:5497-5529`、`BeginScalarConservation:5544-5582`、`EndScalarConservation:5584-5633`。

### 1.3 实际参数（不是代码默认值）

代码里的 `PressureIterations = 60` 是 `LocalAtmosphereSettings` 的**内置默认**（`LocalAtmosphereSettings.cs:286-298`），但运行时被 XML 覆盖并按 interval 缩放：

- `1.6/Defs/AtmosphereTuning.xml:5-7`：`baseIntervalTicks=30`、`TimeStep=1`、`PressureIterations=16`。
- `AtmosphereOverhaulSettings.cs:177`：`simulationIntervalTicks` 默认 **6**。
- `AtmosphereTuningManager.ScaleForInterval:251-257`：

```csharp
ratio = max(exactTail ? 1f/30 : 0.1, interval/30);
TimeStep = source.TimeStep * ratio;
if (CompressiblePressure > 0) EnableTemporalPatches = 0;      // :253-254
PressureIterations = max(4, round(16 * ratio));
if (CompressibleSolver >= 0.5 && ratio <= 0.45)
    PressureIterations = max(4, round(16 * 1.5f));            // = 24
```

interval=6 → ratio=0.2 → **P=24**；`TimeStep=0.2`。这与既有性能文档的 `P=24` 一致。

### 1.4 每粗步预算

| 阶段 | 遍历次数 | 能否跳过冻结格 |
|---|---|---|
| `AdvanceProvisional` | ≈ 26N（20+ 全图 pass，含 4 次 `Mathf.Exp`/格） | 大部分可以（skip + 写固定值） |
| `ProjectAndFinalize` | ≈ `P·N + 23N`（P=24） | SOR 可以；梯度→速度可以；边界/夹取可以 |
| `ApplyGroundEnergy` | 1N | 可以 |
| `PublishSnapshot` | 9N | 不可以（但可顺势写读出值） |
| `PrepareTemperatureSources` | ≈ 19N（含 11N 外部增量空扫） | 空扫不可跳，但可单独优化 |
| `Apply{Temperature,Moisture,Divergence}Deltas` | 3N | 可以 |
| **合计** | **≈ 105N** | — |

**关键推论**：压力 SOR 占 `24/105 ≈ 23%`。所以"只改 SOR"最多省 `0.23 × f`（f=冻结面积比），不足以支撑"大幅降低"。

---

## 2. AO 现成的三套机制，为什么都不能直接拿来用

### 2.1 `PinSkyPressure`：真正的 Dirichlet 钉死，但只用在一个分支

`LocalAtmosphereEngine.cs:308-334`：

```csharp
internal void RequestSkyPin(int[] cells) { ... }
private void AddSkyPinComponent(...) { ... }
private void PinSkyPressure() {
    for (int i = 0; i < skyPinCells.Count; i++)
        Pressure.Write[skyPinCells[i]] = 0f;     // 330-334
}
```

`RequestSkyPin` 在 `compressibleActive` 时**直接 return**（`:310`）。也就是说：默认可压缩模式下，AO 自己都不用这个机制。

**可复用点**：`PinSkyPressure` 证明了"在 Jacobi/SOR 每轮把某些格钉成常量"是安全的；但它只覆盖压力，且不覆盖速度/标量。

### 2.2 `DeflateEnclosures`：房间级降维，但仍逐格回灌

`EnsureEnclosures:4709-4818` 把"有顶空气 + 洞口"划成封闭房间；`BuildAggregates:4858-4963` 再在房间内按可通行面细分聚合块；`CoarseSumAggregate:4965-4984` 在块级求和；`DeflateEnclosures:5005-5153` 用块级 SOR（24 轮）求解后 `CoarseApplyAggregate:4986-5003` 逐格回灌：

```csharp
coarseP[cell] = coarseP[cell] + f + enclosureShift;   // 4986-5003
```

**可复用点**：这是"把一整个房间在压力上降维成少数未知量"的现成实现，思想与用户的需求一致。**不可用点**：回灌仍要遍历房间内每一格，省的是 SOR 轮数，不是遍历面积。而且它只处理压力。

### 2.3 `TemporalPatchDomain`：完整的"紧凑子域"基础设施，但默认被关死

`TemporalPatchTopology` 已经实现了用户想要的核心数据结构：`ActiveCells`（任意格集合）、`MapIndexToSlot`（全局索引 → 紧凑槽位）、`Core/Coupling/Halo` 分区、`ActiveFraction`（`ActiveCells.Length / Size`）。
`TemporalPatchDomain` 实现了"复制全局场到紧凑域 → 只在域内跑物理 → 合并回全局"的完整链路（`TemporalPatchDomain.cs:29-...`：`Allocate`、`CopyFromGlobal`、`Advance`、`MergeIntoGlobal`、`Publish`）。

**但它永远不会被启用**：

- `AtmosphereTuningManager.cs:253-254`：`if (CompressiblePressure > 0) EnableTemporalPatches = 0;`，而 `1.6/Defs/AtmosphereTuning.xml` 里 `<CompressiblePressure>1</CompressiblePressure>`。
- `TemporalPatchManager.BeginDetection` 在 `EnableTemporalPatches <= 0` 时直接 return（约 `:110-126`），`ConsiderCell` 同样（`:127-155`）。

**结论**：这套代码是"设计意图正确、运行时被关掉"的现成骨架，可以作为方案 B/D 的实现蓝本，但不能指望它默认生效。

---

## 3. 四种方案对比

| 维度 | A 现状：每步覆写 | B 全链路跳过 + Dirichlet | C 几何隔离 | D 改造 TemporalPatch |
|---|---|---|---|---|
| 做法 | 全量模拟后写六通道 | 每个 pass `if (fixed[i]) continue`，swap 后统一写回固定值 | 冻结格 `obstacleMask=0`（+`faceOpen=0`），全链路自动短路；读出层覆写 | 把 TemporalPatch 改成"全局冻结 + 用户区域精细" |
| 改动点 | 0（已实现） | ~20 处 pass + 1 个 `SwapReadWrite` Postfix + 守恒排除 + 源项排除 | ~3 处（Rebuild Postfix、PackedSweep、读出层） | 最大（拓扑构建 + 域物理 + 交换合并） |
| 计算量收益 | **0** | `≈ f × 可跳比例`（粗估 `0.7f ~ 0.9f`） | `≈ f × 100%`（除 PackedSweep 与几个全量夹取） | `≈ f × 100%`，但只在域内精细步，全局仍需跑 |
| 语义 | 显示固定，物理仍自由 | 冻结格是"恒值空气"，对外有交换 | 冻结格是"实心固体"，对外无交换 | 同 B，但只对局部精细 |
| 数值风险 | 低（但物理不自洽） | **高**：无限源/汇、守恒缩放冲突、压差驱动持续流动 | **低**：所有通量被面开度 0 掐断 | 中 |
| 与现有"穹顶绝对隔离"兼容 | 是 | 是（更正确） | **完全一致** | 是 |
| 推荐度 | 不推荐（无收益） | 谨慎 | **推荐** | 后续可选 |

---

## 4. 方案 C（几何隔离）详解

### 4.1 原理

`MapObstacleMaskBuilder.cs:25-90`：

```csharp
obstacleMask[i] = AirCanMoveThrough(edifice) ? 1 : 0;
...
faceOpenX[i] = (i < width-1) ? obstacleMask[i] * obstacleMask[i+1] : 0;   // ~:82
faceOpenZ[i] = ...;
```

只要把冻结区内部格 `obstacleMask` 置 0：

- 其四周 `faceOpenX/Z` 自动变 0 → 压力、速度、标量、平流全部无法穿过边界；
- 所有 pass 的 `if (ObstacleMask[i] <= 0)` 短路立即生效（`Project:3762`、`CompressibleSetupPass:4476`、各 `Diffuse*`/`Advect*` 的 mask 判断）；
- `LimitScalarTraceToWalls:5497-5529` 的 `RebuildFreeDistances:5454-5497` 会把"到墙的自由距离"算成 0，平流不会跨区。

### 4.2 三处必做改动

**C1. 在 mask 重建后强制置 0（主线程）**

挂 `MapObstacleMaskBuilder.Rebuild` 的 Harmony Postfix，把冻结格 `obstacleMask=0`、对应 `faceOpenX/Z=0`。必须在 `Rebuild` 之后、`ReconcileOpenedCells` 之前或之后都行，但**要在 `previousObstacleMask` 记录之前**，否则下一 tick `ReconcileOpenedCells:4590-4645` 会把它当"新开放格"用四邻平均插值填充温度/湿度/雾/氧。

`AtmosphereMapComponent.cs:3301-3309`：

```csharp
Array.Copy(engine.ObstacleMask, previousObstacleMask, engine.Size);
maskBuilder.Rebuild(map, engine);                 // ← Postfix 在这里改 0
engine.ReconcileOpenedCells(previousObstacleMask);
engine.InvalidateAirMoverConnectivity();
```

注意 `previousObstacleMask` 是 Copy **Rebuild 之前**的值。若上一 tick 我们已把它改成 0，则 `previous=0`、`new`（Rebuild 后、我们 Postfix 前）可能是 1 → `ReconcileOpenedCells` 仍会插值。**最稳的做法是把 Postfix 挂在 `Rebuild` 内部逻辑之后、`Array.Copy` 之前**——即挂 `RebuildObstacleMaskUnsafe` 的 Prefix/Prefix 阶段做不了，需要挂 `MapObstacleMaskBuilder.Rebuild` 的 Postfix，并接受一次无意义的插值（`ReconcileOpenedCells` 只在 `previous<=0 && new>0` 时触发，若我们在 `Rebuild` 返回前把 new 改回 0，则 `ReconcileOpenedCells` 看不到 new>0 —— 所以 **Rebuild Postfix 是正确的挂点**）。

**C2. `PackedSweep` 单点跳过**

`PackedSweep:5420-5452` 是唯一**不检查 `ObstacleMask`** 的 pass（`fixed` 指针 + 预乘记录），墙格会算 `p = 0*p + 0 = 0`。几何隔离后冻结格是"墙"，SOR 仍会算它们（60/24 轮 × fN 次）。加一行 `if (mask[i] <= 0) continue;` 即可，或利用 `packRecords` 里 `alpha=beta=0` 的特征跳过。

**C3. 读出覆盖层**

`SetBlockedCellsToAmbient:3319`、`ReconcileOpenedCells` 会把 `obstacleMask<=0` 的格写成环境值。冻结区变成"实心"后，`PublishSnapshot:3194-3206` 读到的也是环境值。因此必须保留一个**读出层**：在 `PublishSnapshot` Prefix 里对冻结格写目标值（压力/温度/湿度/气体/速度 0），语义与现有 `Patch_AbsoluteIsolation` 完全一致。

### 4.3 收益

- 物理 pass 全部按 `f` 比例省（因为循环里短路）；
- `PackedSweep` 加一行后也按 `f` 省；
- 剩余不可跳的：`PublishSnapshot` 9N（仍要 Copy）、`PrepareTemperatureSources` 的 11N 空扫、若干全量串行夹取（`ClampThermodynamicFields`/`ApplyThermodynamicPressureBaseline`/`ClampVelocityMagnitude`，都是 O(1)/格）。
- 粗估：`105N → 9N + 19N + (105N-28N)(1-f) + 少量夹取` ≈ `105N - 77N·f`。
  - f=10% → 省 ~7%
  - f=30% → 省 ~22%
  - f=50% → 省 ~37%

### 4.4 代价与限制

1. **冻结区内部不再有任何物理**：温度/湿度/压力不会自演化，也不会与外部交换。这正好等于"绝对隔离穹顶"的语义。
2. **冻结区不能有活物/机器依赖内部气流**：它们读的是读出层的固定值，而不是真实场。
3. **`EnforceBoundaries` 可能把边界速度清零**，与"固定 0 速度"一致，无冲突。
4. **`CancelWallNormalOneSidedGradient:3810`** 会处理墙面单侧梯度，需要确认它不会在冻结区边界产生异常（它只读 `FaceOpen`，应自动为 0）。

---

## 5. 方案 B（保留空气语义的全链路跳过）详解

若用户要的是"有交换的恒温气库"而不是"实心固体"，则不能用 C，需要：

### 5.1 逐 pass 改动清单

| # | 位置 | 改动 |
|---|---|---|
| 1 | `DiffuseVelocity:3818-3869` | 循环内 `if (IsFixed[i]) continue;` |
| 2 | `AdvectVelocity:3470-3504` | 同上（target 写固定速度，或依赖 swap Postfix） |
| 3 | `ApplyCoriolis:3936-3958` | 同上 |
| 4 | `LimitScalarTraceToWalls:5497-5529` | 同上（保持速度 0） |
| 5 | `AdvectScalar:3506-3532`（×3） | 同上 |
| 6 | `AdvectOxygen:3534-3573` | 同上 |
| 7 | `DiffuseScalar:3871-3905`（×2） | 同上 |
| 8 | `DiffuseOxygen:3590-3633` | 同上 |
| 9 | `UpdateHumidityAndThermodynamics:3907-3934` | 同上 |
| 10 | `ClampThermodynamicFields:4006-4015` | 同上 |
| 11 | `Project` 的 divergence pass `3691-3711` | 跳过（冻结格 divergence=0） |
| 12 | `CompressibleSetupPass:4476` | 条件里加 `IsFixed[i]` → alpha=beta=rhs=0、`pn=stored*toSolver` |
| 13 | `PackedSweep:5420-5452` / `CompressibleSorPass:4527-4573` | 跳过 |
| 14 | `Project` 的梯度→速度 `3752-3806` | 跳过 |
| 15 | `DampVelocity:3960-3984` / `ClampVelocityMagnitude:3986-4004` | 跳过 |
| 16 | `ApplyThermodynamicPressureBaseline:4017-4046` | 跳过（否则 `StoredPressurePa` 重写 `Pressure.Read`） |
| 17 | `StoreCompressiblePressure:2380-2381` | 跳过（或依赖 read 未变） |
| 18 | `BeginScalarConservation:5544-5582` / `EndScalarConservation:5584-5633` | **统计与缩放都要排除冻结格** |
| 19 | `ApplyTemperatureDeltas` / `ApplyMoistureDeltas` / `ApplyDivergenceDeltas` | 跳过 |
| 20 | `AddWallLeakSources:2980` / `AddThermalExpansionSources:2978` / `AddPressureOdditySources:2976` | 跳过冻结区相关面/格 |
| 21 | `ImprintChokedJets:2992` / `ApplyPressureOddityRealized:2993` / `EndChoke` | 跳过 |

### 5.2 统一写回点：`AtmosphereField.SwapReadWrite`

`Simulation/AtmosphereField.cs:30-35` 是**唯一**的 swap 实现：

```csharp
public void SwapReadWrite() { float[] read = Read; Read = Write; Write = read; }
```

可以挂一个 Harmony Postfix，在 swap 后把固定格的值写回 `Read`。这样：

- 每个 pass 里只需要 `continue`，不需要在每个分支里记得写 target；
- 覆盖 `VelocityX/Z`、`Pressure`、`Temperature`、`Moisture`、`Mist`、`Oxygen`、`ExplosionPressureResidue` 全部 field；
- `ByteAtmosphereField.cs:31` 有独立实现，需要单独处理。

**注意**：Postfix 每 field 每次 swap 遍历固定格数组。假设冻结 1000 格、每步 ~15 次 swap，约 1.5 万次写，可接受。

### 5.3 最大风险：守恒缩放

`EndScalarConservation:5622-5627` 对每个 `airMoverComponent` 统一缩放：

```csharp
read1[cell] = ClampTemperature((read1[cell] + 273.15) * num7 - 273.15, settings);
read2[cell] *= num8;
read3[cell] *= num9;
```

若冻结格与外部同属一个 component，这一步会**直接覆盖固定值**。必须：

- 在 `BeginScalarConservation` 统计时排除冻结格；
- 在 `EndScalarConservation` 缩放时跳过冻结格；
- 或者让冻结区在 `EnsureAirMoverConnectivity` 里单独成 component（更干净，但改动更大）。

---

## 6. 正确性陷阱清单（两条路线都必查）

| # | 陷阱 | 证据 |
|---|---|---|
| 1 | `Project` 开头 `Array.Clear(Pressure.Read/Write/divergence)`，**直接写 `Pressure.Read` 固定压力无效**，必须走 `StoredPressurePa`。 | `LocalAtmosphereEngine.cs:3685-3687` |
| 2 | 每个 field 都是 Read/Write 双缓冲 + `SwapReadWrite`，固定值必须写在 target 或 swap 后写回。 | `AtmosphereField.cs:30-35` |
| 3 | `StoreCompressiblePressure` 用 `stored = read / toSolver`（`num2 = accel*1.2/dt`）回写；若 SOR 跳过且 read 未变，闭环成立。 | `:2364-2381`、`:2192` |
| 4 | `ApplyThermodynamicPressureBaseline` 每步用 `StoredPressurePa` 重写 `Pressure.Read`。 | `:4017-4046`（`:4031`） |
| 5 | `EndScalarConservation` 的区域缩放会破坏固定值。 | `:5622-5627` |
| 6 | `ReconcileOpenedCells` 对 `previous<=0 && new>0` 的格用四邻平均插值填温度/湿度/雾/氧。 | `:4590-4645`、`AtmosphereMapComponent.cs:3307` |
| 7 | `SetBlockedCellsToAmbient` 把 `obstacleMask<=0` 的格写成环境温度。 | `AtmosphereMapComponent.cs:3319` |
| 8 | `PackedSweep` 不检查 `ObstacleMask`，几何隔离下仍会算墙格。 | `:5420-5452` |
| 9 | 平流是 semi-Lagrangian，回溯可能跨格；`LimitScalarTraceToWalls` 把速度限制到"到墙自由距离"后才安全。 | `:4145-4170`、`:5497-5529` |
| 10 | `Parallel.For(1, Height-1)` 按行分区，矩形冻结区会造成线程负载不均。 | 各 pass 的 `Parallel.For` |
| 11 | 模拟在独立 `Task` 内（`AtmosphereMapComponent.cs:2962-3023`），只允许读主线程预构建的格数组；枚举建筑必须在主线程。 | `Source/Compat.OmniCrafter/OmniDomeZoneCache.cs` 注释 |
| 12 | 固定格与邻居大温差会形成无限热源/汇；固定压差会持续驱动流动。B 路线必须限幅/隔离。 | `ClampTemperature`/`ClampThermodynamicFields` |

---

## 7. 性能预期（可数遍历口径）

设 `f = 冻结面积 / N`。

| 方案 | 每粗步遍历 | f=10% | f=30% | f=50% |
|---|---|---|---|---|
| A 现状 | 105N | 105N | 105N | 105N |
| C 几何隔离 | ≈ 105N − 77N·f | ~97N（−7%） | ~82N（−22%） | ~66N（−37%） |
| B 全链路（估） | ≈ 105N − 70N·f | ~98N（−7%） | ~84N（−20%） | ~70N（−33%） |
| 只跳 SOR（B 子集） | 105N − 24N·f | ~103N（−2%） | ~98N（−7%） | ~93N（−11%） |

**对比**：`simulationIntervalTicks` 6→30，每 tick 计算量约降 80%。所以：

> **跳格是"锦上添花"，降 interval 才是"大头"。** 但跳格可以与降 interval 叠加，且跳格不改变冻结区的可见状态。

---

## 8. 建议的实施顺序

1. **先做 C1+C3 的最小闭环**：Rebuild Postfix 把冻结格 mask=0 + 读出覆盖层，验证游戏内冻结区状态稳定、外部平流不进、无明显数值异常。此时收益≈几何短路带来的自然收益（不含 PackedSweep）。
2. **加 C2（PackedSweep 跳过）**，测 SOR 段耗时变化。
3. **需要"有交换"语义时**，再按 §5 逐项做 B；每加一项用"冻结区内部温度是否漂移""外部是否出现异常风"做回归。
4. **验证方法**：不依赖 profiler，先在 `Tick()` 前后加 Stopwatch 统计（仅调试构建），比较 f=0 / f=30% 两种配置的单步耗时；同时用 `PublishSnapshot` 后的 Snapshot 检查冻结区读值是否恒定。

---

## 9. 待用户决策的语义问题

1. **冻结区对外是否允许交换？**
   - 不允许（实心固体）→ 方案 C，改动最小、最稳。
   - 允许（恒温气库）→ 方案 B，需处理守恒缩放与压差驱动。
2. **冻结区是否需要参与"封闭房间"压力降维？** 若冻结区本身是一个封闭房间，C 方案会让它变成"墙内墙"，`EnsureEnclosures` 可能把它当 hole/边界，需要确认 `IsRoofedAir` 与 `enclosureHole` 的判定（`:4668-4707`）。
3. **固定值是否要跟随地图大气？** 现有 `ResolveTargetPressurePa(ambientPa)` 已支持；C 方案的读出层要沿用同一策略，否则会出现"冻结区压力与外部大气长期不一致"。
4. **是否接受"冻结区内部无风"？** C 方案天然速度 0；B 方案需要显式固定速度 0，否则压差会驱动内部流动。

---

## 10. 参考

- `LocalAtmosphereEngine.cs`：Tick `:2932-2938`、AdvanceProvisional `:2940-2968`、ProjectAndFinalize `:2970-2994`、Project `:3678-3816`、SolveCompressible `:2185-2295`、StoreCompressiblePressure `:2364-2381`、CompressibleSetupPass `:4456-4493`、CompressibleSorPass `:4527-4573`、PackedPack `:5376-5418`、PackedSweep `:5420-5452`、BeginScalarConservation `:5544-5582`、EndScalarConservation `:5584-5633`、EnsureEnclosures `:4709-4818`、BuildAggregates `:4858-4963`、DeflateEnclosures `:5005-5153`、PinSkyPressure `:330-334`。
- `AtmosphereMapComponent.cs`：MapComponentTick `:1173-1221`、StartSimulationTask `:2962-3023`、PublishSnapshot `:3194-3206`、RebuildObstacleMaskUnsafe `:3301-3309`、RefreshCachedMapDataUnsafe `:3311-3337`。
- `AtmosphereTuningManager.cs:244-282`（ScaleForInterval）。
- `AtmosphereOverhaulSettings.cs:165-172,177`（默认 interval=6）。
- `1.6/Defs/AtmosphereTuning.xml:5-29`（base=30、iterations=16、CompressiblePressure=1）。
- `MapObstacleMaskBuilder.cs:25-90`（mask 与 faceOpen 定义）。
- `AtmosphereField.cs:30-35`、`ByteAtmosphereField.cs:31`（swap 点）。
- 既有文档：`AO-模块功能与性能分析.md`（性能底账）、`AO-补丁侧性能审查.md`（补丁侧成本）。

---

## 11. 可切换隔离模式（地图级开关）的可行性

用户要求：把方案 C 做成可切换，**按地图**设置是否启用隔离模式，默认隔离，开关放在穹顶与生物圈的建筑 Gizmo 中。

**结论：可行，且是现有架构的自然扩展。** 现有 `MapComponent_OmniAtmosphereTargets` 已经是"每地图一份 + Scribe 存档 + 主线程预热"，`Patch_OmniAtmosphereGizmos` 已经同时挂在 `CompOmniForceFieldDome` 与 `CompBiosphere` 的 `CompGetGizmosExtra` 上，`MapObstacleMaskBuilder.Rebuild` 是 public 方法（`MapObstacleMaskBuilder.cs:19-22`），`AtmosphereMapComponent.MarkCachedMapDataDirty()` 是 public（`:1277-1281`），`engine.ObstacleMask / FaceOpenX / FaceOpenZ` 的数组元素可直接写（`LocalAtmosphereEngine.cs:2867,2875,2877` 是 `public float[] { get; private set; }`）。三处现成接口刚好够用。

### 11.1 数据模型

在 `MapComponent_OmniAtmosphereTargets` 增加：

```csharp
public bool UseGeometricIsolation = true;   // 默认隔离
// ExposeData:
Scribe_Values.Look(ref UseGeometricIsolation, "aoOmniGeometricIsolation", true);
```

旧存档没有该 key 时读默认 `true`，满足"默认隔离"。与现有目标值共享同一地图组件，理由是同一地图所有穹顶/生物圈连成一片时 AO 场只有一个连通域（与目标值共享的设计理由一致）。

### 11.2 UI

在 `Patch_OmniAtmosphereGizmos.AppendDome` / `AppendBiosphere` 里各追加一个 `Command_Toggle`：

```csharp
new Command_Toggle {
    defaultLabel = "AOPatch_Omni_Isolation".Translate(),
    defaultDesc  = "AOPatch_Omni_IsolationDesc".Translate(),
    isActive     = () => targets.UseGeometricIsolation,
    toggleAction = () => {
        targets.UseGeometricIsolation = !targets.UseGeometricIsolation;
        Patch_OmniGeometricIsolation.RequestRebuild(map);
    }
}
```

两个建筑切换的是同一地图级开关（不是每建筑一份），否则同图多个穹顶的语义会分裂。

### 11.3 mask 写入时机（核心）

挂 `MapObstacleMaskBuilder.Rebuild` 的 **Postfix**（`public void Rebuild(Map map, LocalAtmosphereEngine engine)`，`__0`=map、`__1`=engine）：

1. `targets = MapComponent_OmniAtmosphereTargets.Get(map)`；若 `null` 或 `!UseGeometricIsolation` → 直接返回（关闭时不改，保持 AO 原生值）。
2. `cells = OmniDomeZoneCache.GetCells(map)`（主线程可读缓存；为空则 `EnsureBuilt`）。
3. 对每个 `i`：`engine.ObstacleMask[i] = 0`，并把受影响的四个面置 0：
   - `FaceOpenX[i]`、`i % width > 0 ? FaceOpenX[i-1]`
   - `FaceOpenZ[i]`、`i >= width ? FaceOpenZ[i-width]`
4. **不要全量重算 `FaceOpenX/Z`**：`Rebuild` 末尾 `BuildVents:83` 会为通风口写特殊面开度（`MapObstacleMaskBuilder.cs:112-123`），全量重算会把它冲掉。

**时机优势**：`AtmosphereMapComponent.cs:3301-3309` 的顺序是 `Copy previous → Rebuild → ReconcileOpenedCells`。Rebuild Postfix 位于 `ReconcileOpenedCells` 之前，所以冻结格 `new mask = 0`，`ReconcileOpenedCells` 不会把它当"新开放格"插值填充。关闭隔离时会自然触发一次 `previous=0 → new=1` 的插值，用四邻平均给冻结区一个合理初值，这正是想要的。

### 11.4 切换即时生效

`MarkCachedMapDataDirty()` 是 public（`:1277`），反射或直接调用均可。切换后下一 tick `RefreshCachedMapDataUnsafe` 重跑 `Rebuild`：

- 开启：Rebuild 算出正常 mask → 我们的 Postfix 置 0；
- 关闭：Rebuild 全量重算 mask/faceOpen/vent → 不再置 0，自然恢复。

### 11.5 固定与隔离是两个独立维度（2026-11-20 用户澄清）

用户原话：**「穹顶内任何时候都要固定。此开关只控制是否隔离。」**

| 开关 | 六通道固定（写 Read） | 几何隔离（mask=0） | 对外交换 |
|---|---|---|---|
| 开（默认） | 是 | 是 | 无 |
| 关 | 是 | 否 | 有（内部每步被覆写回目标值） |

推论（与初版分析相反，已按用户澄清修正）：

1. **`Patch_AbsoluteIsolation` 的 Prefix 不加开关判断**，永远写六通道。
2. **`Patch_OmniAtmosphereTemperature` 的读数覆盖不改**，永远与目标值一致——穹顶内任何时候都固定。
3. **唯一必须同步改的**是 `Patch_AbsoluteIsolation.cs:200-205` 的 `obstacleMask <= 0` 跳过：隔离开启时冻结格 mask=0，该跳过会让读出层对整片穹顶失效。改为无条件写冻结集合内的格即可。真实墙格写 Read 无害：其相邻 `faceOpen=0`，`BilinearSampleOpenAir:4198-4218` 按 mask 加权采样不会取到墙格，`BeginScalarConservation:5544-5582` 只统计 mask>0 的 airMoverComponent。

也就是说：**开关关闭 = 方案 A（现状，覆写但不隔离）；开关开启 = 方案 C（覆写 + 几何隔离）**。两者共用同一套固定逻辑，只有 mask 写入受开关控制。

### 11.6 风险与坑

| # | 风险 | 说明 |
|---|---|---|
| 1 | 线程 | mask 只在主线程（Rebuild Postfix）写，模拟线程只读，安全；开关 bool 主线程写、模拟线程读，32 位原子，可接受（可加 `volatile`）。 |
| 2 | `PackedSweep` 仍会算墙格 | 见 §4.2 C2；不修则 SOR 段的隔离收益打折。 |
| 3 | vent 与隔离冲突 | 跨冻结区边界的通风口会被 `FaceOpen=0` 掐断，符合"绝对隔离"语义，但需在 UI/文档说明。 |
| 4 | `SetBlockedCellsToAmbient` | `RefreshCachedMapDataUnsafe:3319` 会把 mask<=0 的格写成环境温度；读出层每步覆写保证显示正确。 |
| 5 | 关闭隔离的物理表现 | 穹顶内仍被每步覆写成目标值，但外部平流/扩散会持续与穹顶边缘交换；读数是目标值，边缘存在通量。这是"固定但不隔离"的预期语义。 |
| 6 | 默认值变化 | 现有玩家一直是"覆写但不隔离"；升级后默认开启几何隔离，穹顶对外不再交换。需在更新日志/开关描述里明确。 |
| 7 | 目标值面板 | 目标值任何时候都生效，面板不需要灰显；但开关描述要说明"关闭后穹顶会与外部交换，仅内部读数保持目标值"。 |

### 11.7 改动清单

| 文件 | 改动 |
|---|---|
| `MapComponent_OmniAtmosphereTargets.cs` | + `UseGeometricIsolation = true` 字段与 Scribe |
| `Patch_OmniGeometricIsolation.cs`（新增） | `MapObstacleMaskBuilder.Rebuild` Postfix（仅开关开启时置 0）；`RequestRebuild(map)` |
| `Patch_OmniAtmosphereGizmos.cs` | 两个 Append 里追加 `Command_Toggle` |
| `Patch_AbsoluteIsolation.cs` | **只**删除 `mask<=0` 跳过；Prefix 不加开关判断 |
| `AtmosphereFieldBridge.cs` | + `GetFaceOpenX/GetFaceOpenZ` |
| `OmniCompatBootstrap.cs` | 安装新补丁（放在六通道判断之后） |

**不需要改**：`Patch_OmniAtmosphereTemperature`（读数覆盖始终跟随目标值）。
