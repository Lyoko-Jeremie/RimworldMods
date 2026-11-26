# AO 能量塔圆形风场方案（VolumeRing）

> 记录时间：2026-11-27。适用版本：RimWorld 1.7 + Atmosphere Overhaul（2026-11-23 DLL）。
> 相关代码：`Source/JeremieAtmosphereOverhaulPatch/ProxyHeat/ProxyHeatRelayMapComponent.cs`。
> 状态：已落地并在游戏内实测，效果从「只朝四角喷」改善为「四周基本均匀、四角略长」。

---

## 0. 结论摘要

能量塔的风场驱动最终采用 **VolumeRing**：在塔外 1–2 圈的环格上同时使用两条 AO 通道。

| 通道 | 阶段 | 作用 | 对应 API |
|---|---|---|---|
| 体积源 | solver 前 | 制造散度，经 `Project` 解出压力驱动流，负责**把风推远** | `AtmosphereInterface.AddVolumeSourceAtTemperature` |
| 矢量风 | solver 后 | 把环格与链格速度钉在径向上，负责**给风定方向** | `AtmosphereInterface...`（连续源 `MaintainTargetWind/TargetWind`） |

两条通道方向一致（径向向外），阶段不同（一个在 solver 前供能、一个在 solver 后定向），因此不互相对抗，而是协同。实测环格 22–25 m/s，环外 4 步 2–12 m/s，8/12 步回落到天然风量级附近；用户反馈「周围都比较均匀了，四个角还是比四边更长一点」。

---

## 1. 问题与排查结论

### 1.1 现象

- 迁移到 AO 公共接口后，能量塔喷流「只朝四个角、正东南西北几乎没有」；
- 迁移前快照仍能复现 → 排除我们代码引入；
- AO 三个 DLL 版本（old/mid/new）的喷流执行链逐字节一致 → 排除版本回归。

### 1.2 AO 侧关卡全部通过

实测诊断显示：

- `IsAirMoverPortOpen` / `TryGetNozzleReturnPath` / `HasOpenPath` 全部通过（`ao[seen=68 ok=68 closed=0 noRet=0 blocked=0]`）；
- 压力泵接受 100%（`req=122.0 acc=121.7 f=1.00`）；
- 端口方向分布均匀（`dirs[E=8 NE=6 N=8 NW=7 W=12 SW=9 S=10 SE=8]`）；
- 出口贴边 1 格（`dist[min=1.0 max=1.4 avg=1.2]`），符合「热源贴塔身」的物理模型。

### 1.3 四角强的真实机制

- `JetTransport.BuildPath` 对对角方向取 `num1=Sign(dx), num2=0`，只走横向 → 对角射流实际上沿横/纵格走，与相邻端口出口格接力；
- 轴向是单列孤立路径，被 4 邻域 `DiffuseVelocity` 稀释，衰减快；
- `AdvectVelocity` 半拉格朗日回溯会把贴边速度采样到塔身（`ObstacleMask=0`）→ 归零。

**结论：这是 AO 网格各向异性 + 喷流路径离散的固有结果，不是参数问题。**

---

## 2. 方案演进（被否决的路线）

| 方案 | 做法 | 结果 |
|---|---|---|
| ① 出口推远 | `TryFindOutlet` 取最远合法格（最多 30 格） | 用户否决：「出口必须紧靠建筑边缘」——热源必须从塔身开始，推远等于风从空地冒出 |
| ② `AddWindImpulse` 逐格径向 | solver 前写速度增量 | 被 `Project` 散度投影吃掉：径向环是纯散度场，投影后归零 |
| ③ ring `TargetWind` | 环格注册径向 `TargetWind` | 环格 21 m/s 很稳，但环外 4 步只剩 1–2：`TargetWind` 只在 solver 后钉环格，出环一步即被平流 + 投影 + 阻尼削掉 |
| ④ VolumeRing（当前） | 环格逐格体积源 + 链式体积 + 矢量风 | 已实测：周围均匀、射程明显改善 |

---

## 3. VolumeRing 实现

### 3.1 几何构建（`BuildWindRing`）

- 取 `occupied.ExpandedBy(layer+1)` 的矩形边界（layer = 0..layers-1，layers = 设置里的 `JetLayers`，限制 1–2）；
- 过滤条件：
  1. `cell.InBounds(map)` 且不在建筑占地内；
  2. `ObstacleMask[index] > 0`；
  3. **必须落在 `state.Cells` 内**——`state.Cells` 是 ProxyHeat 的 `AffectedCells`（`HashSet<IntVec3>`），已做过 FloodFill 连通性与遮挡筛选。这是用户要求「用 HeatProxy 算出的地面有效格判断遮挡」的落点，避免自己推射线。

### 3.2 体积注入（`InjectWindVolumeRing`）

每步对每个环格：

```csharp
AoBridge.AddVolumeSourceAtTemperature(map, cell, volumes, 21f, celsius);
```

- `volumes` = 设置值 × 该塔速度比例（见 3.4）；
- 温度传该格当前温度（净热中性，不额外加热）；
- 体积源一次性提交（`pendingPointVolumes` 每步清空），不需要注册/注销 key。

**链式注入**：环格之外，沿径向往外再推 2 格（`VolumeChainDepth = 2`），按 `0.6^step` 衰减继续灌体积：

```csharp
for (int step = 1; step <= VolumeChainDepth; step++)
{
    IntVec3 chainCell = round(ringCell + dir * step);
    if (!state.Cells.Contains(chainCell)) break;   // 被挡即停
    AddVolumeSourceAtTemperature(map, chainCell, volumes * pow(0.6, step), 21f, celsius);
}
```

链式注入模拟 AO Jet 的「沿路径逐格分配体积」——单靠环格时，压力波一过就散，环外 8/12 步只剩天然风。

### 3.3 矢量风（`RegisterWindRing`）

- 环格注册 `TargetWind`（径向 × 速度，`TargetWindBlendPerTick = 0.1`）；
- **链上 2 格也注册**（速度按 `0.6^step` 衰减，key 为 `prefix + i + "c" + step`），给轴向提供连续 3 格的定向速度，抵消 4 邻域扩散对轴向的抹平；
- 只在「未注册」或「速度变化」时整体重写（先按前缀 `RemoveContinuousSourcesWithPrefix` 再逐格 `SetContinuousSource`），其余 tick 跳过——避免每步刷新触发 `FlushContinuousIntervalToQueues` 把 `TargetWind` 差值转成 solver 前增量（那样会退化成方案③）。

### 3.4 体积随喷流速度缩放

```csharp
float referenceSpeed = AutoJetSpeed(state.Radius);
float tunedSpeed = GetJetSpeed(parent, state.Radius);
float volumes = baseVolumes * Clamp(tunedSpeed / referenceSpeed, 0.25f, 3f);
```

原因：之前「喷流速度」只改环格 `TargetWind`，环外由体积源驱动、完全不受影响，用户调它看不到变化。缩放后一个参数同时控制两条通道。

### 3.5 设置项

| 项 | 位置 | 说明 |
|---|---|---|
| `WindDriveMode` | 0=环形 TargetWind、1=VolumeRing（默认）、2=喷流 | 三档驱动方式 |
| `WindVolumePerCell` | 默认 1.5，滑块 0.1–5，key `windVolumePerCellV2` | 每格每步体积 |
| `JetLayers` | 1–2 | 环层数 |

### 3.6 诊断日志

```
volring kyon_HeatGeneratorTowerLarge cells=64 vol=1.500 inj=64/0 chain=118 vec=1 pos=(48, 0, 70)
ringdir E(23.6/5.2/2.9/2.2) NE(23.1/6.5/2.1/1.5) ...
```

- `cells`：环格数；`inj`：体积注入成功/失败；`chain`：链上成功注入格数；`vec`：矢量风是否已注册；
- `ringdir`：8 方向 ×（环格 / 环外 4 / 8 / 12 步）实测风速。

---

## 4. 为什么两条通道缺一不可

| 只有体积源 | 只有 TargetWind | 两者叠加 |
|---|---|---|
| 环格是压力驻点（∇p 在源格中心对称抵消），塔边「看起来没风」；远处有流但方向各向同性 | 环格 21 m/s 很稳，但出环即被平流/投影/阻尼削掉，环外 1–2 | 环格有明确速度，环外有连续压力驱动，射程与均匀性同时改善 |

关键源码依据：

- `AddPointVolume`（`LocalAtmosphereEngine.cs:1350-1386`）：在 `externalDivergence` 加项；要求 `ObstacleMask>0` 且 `airMoverComponents[cell]>0`（后者对所有开放空气格恒成立，`EnsureAirMoverConnectivity` 会对全部 `ObstacleMask>0` 格 flood fill）；
- `ApplyTargetWindSourcesAfterSolverUnsafe`（`AtmosphereMapComponent.cs:2771-2797`）：solver 后直接 lerp `VelocityX.Read`，不经 `Project`；
- 每步顺序（`:3019-3048`）：`AdvanceProvisional`（Diffuse → Advect → Coriolis）→ AirMover/Jet → `ProjectAndFinalize` → `ApplyVelocityDeltasAfterSolver` → `ApplyTargetWindSourcesAfterSolver` → `PublishSnapshot`。

---

## 5. 与 InjectWindRing + InjectJets 的等价性

不完全等价，差异四项：

| 机制 | Jet | VolumeRing |
|---|---|---|
| 成对压力泵 | `AddNozzlePump(inlet, outlet, …)`（`AtmosphereMapComponent.cs:1220`） | 无（单点散度） |
| 动量源 | `jetTransport.Advance` 的 speed | 无（靠 `TargetWind` 替代） |
| 温度下限 | `ExhaustTemperatureFloor` | 无（传当前格温度） |
| 路径约束 | `BuildPath` 拒绝被挡射流 | 无（四面散开，靠 `state.Cells` 限制注入点） |

若后续需要「窄束射流」，可给环格注册 `ConditionExhaust` AirMover 走完整 Jet 通道，代价是每格一个 mover 的端口检查与回流 BFS。

---

## 6. 实测记录

| 时间 | 配置 | 结果 |
|---|---|---|
| 22:09 | VolumeRing，vol=0.08 | 环格 0.2–6.4、环外 3.4–8.3，与天然风同量级 → 无感（差 Jets 约 20 倍） |
| 23:33 | VolumeRing，vol=1.5，无链式 | 环格 22–25，环外 4 步 2–12、8 步 1–4、12 步 1–2；仍四角强 |
| 23:51 | + 链式 2 格 + 环格 TargetWind | 用户反馈：周围均匀明显改善，四角略长 |
| 24:00 | + 链格 TargetWind + 体积随喷速缩放 | 待再测 |

---

## 7. 剩余问题与后续可选项

1. **四角略长于四边**：已通过「TargetWind 扩到链格」缓解；若仍明显，可选对角方向体积 ×0.8（人工各向异性，不优先）。
2. **射程**：若环外 8/12 步仍不足，可加深链（`VolumeChainDepth` 3）或改用 Jet 通道补动量。
3. **VTE 空调风变短**：独立回归，源于迁移后独立 key 空间与 AO 原生源并存、`TargetWind` 叠加抵消，尚未修复。

---

## 8. 源码索引

| 内容 | 位置 |
|---|---|
| 环格构建 | `ProxyHeat/ProxyHeatRelayMapComponent.cs` `BuildWindRing` |
| 体积环 + 链式 | 同上 `InjectWindVolumeRing` |
| 矢量风注册 | 同上 `RegisterWindRing` / `RegisterWindCell` |
| 环状态字段 | `ProxyHeat/ProxyHeatSourceState.cs`（`WindRingCells/RingLayers/RingOrigin/RingRot/RingRegistered/RingSpeed/Cells`） |
| 设置项 | `AOPatchSettings.cs` `WindDriveMode`/`WindVolumePerCell` |
| UI | `AOPatchMod.cs`（驱动方式循环按钮 + 体积滑块） |
| 诊断 | `DiagRing` / `DiagRingDirs` / `Diag` |
