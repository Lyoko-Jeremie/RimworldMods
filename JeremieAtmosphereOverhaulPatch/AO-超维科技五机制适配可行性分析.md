# AO-超维科技五机制适配可行性分析

> 日期：2026-11-17
> 范围：超维科技（OuterrealmTech）五项需求在 Atmosphere Overhaul（AO）下的**可行性**与**实现骨架**。
> 性质：设计分析，不含代码修改。高层语义见姊妹文档 `AO-超维科技穹顶与生物圈语义分析.md`（2026-11-13）。
> 证据约定：`文件:行` 均指反编译源码。AO 源码在 `f:\RimworldCodeIL\AtmosphereOverhaul`，超维科技源码在 `F:\SteamLibrary\steamapps\common\RimWorld\Mods\FullyAutomaticOmniCrafter\Source\FullyAutomaticOmniCrafter`。

---

## 0. 结论摘要

| # | 需求 | 可行性 | 关键判断 |
|---|---|---|---|
| 1 | 所有超维科技建筑不受风影响 | **可行，纯 XML，零运行时开销** | 这些建筑全是 Partial/None 填充，AO 把它们归入"可穿透物"通道；用 `BuildingAtmosphereExtension` 把 `airFillPercent` 降到阈值以下即可整类退出 |
| 2 | 护盾保护范围内建筑/物品/屋顶 | **可行，需 4 个判定点补丁** | 护盾没有壳，内部风场与外部相同，不能靠"域内无风"一揽子免疫；必须分别从结构、屋顶、物品、pawn 四个通道跳过 |
| 3 | 穹顶＝稳定隔离环境（黑洞、可漏出、外界不影响） | **可行，但"单向阀"是近似** | 壳是"声明式边界"（每次掩膜重建后重新注入），渗透靠壳格 ε 面开度；五通道每步钉住。AO 没有单向扩散原语，瞬态上外界仍会微量渗入 |
| 4 | 生物圈＝异形穹顶 | **可行，比穹顶简单（无壳）** | Area 内每步钉住五通道 + 风目标源；代价是"魔法边界"观感与功率定价，性能随 Area 面积线性增长 |
| 5 | 随身指定 pawn 不受 AO 影响 | **部分可行** | 温度/气压/氧可以覆盖；风抛飞与 AO 体感温度需要额外补丁点。现有随身实现只覆盖了 `Thing.AmbientTemperature` 一条路 |

一句话：**五条需求在 AO 里都没有"原生开关"，但都有可落地的借用通道；真正的分水岭不是"能不能"，而是"每步要动多少格"以及"是否接受单向阀的近似"。**

---

## 1. 先明确现实模型（按 AGENTS.md 的要求）

| 超维机制 | 现实对应 | AO 里对应的物理量 |
|---|---|---|
| 建筑不受风 | 结构设计风压等级 | 风载荷（结构/可穿透物/屋顶） |
| 护盾 | 能量力场（挡弹片、耗散气流） | 动量场 + 风害判定 |
| 穹顶 | 封闭生命保障舱（Biosphere 2 / 空间站） | 边界（壳）+ 屋顶 + 内部源 |
| 生物圈 | 植物工厂 / 洁净室（强制通风、恒温恒湿） | 域内 Dirichlet 约束 + 风场 |
| 随身 | 单兵环境服（PLSS） | pawn 采样层 + 体感温度 |

这张表决定了一件事：**护盾不是边界，穹顶才是边界。** 护盾只耗散动量，不隔离介质；穹顶隔离介质，再用内部机组维持状态。把两者混为一谈会导致护盾意外变成"隔离罩"。

---

## 2. AO 风害的真实结构（决定补丁点）

风害不是一条通道，而是四条互相独立的判定，全部以 `VelocityX/Z Snapshot` 经 `WindLoadField`（8×8 块 + 3×3 膨胀）取载荷：

1. **结构载荷** `WindStructuralLoad`：成员＝"墙格（`obstacleMask<=0`）且 `useHitPoints` 的建筑"（`RimWorld/WindStructuralLoad.cs:397-403`）。载荷 `load = demand/(ratingQ×capacity) + pressureLoad`（`:481`），`windToleranceFactor` 只进 `ratingQ`（`:149-155`）。
2. **可穿透物/植物** `WindPassableLoad`：成员条件 `AirFill(def) >= max(0.001, WindDamageMinFillPercent)`（默认 `WindDamageMinFillPercent = 0.1`，`Simulation/LocalAtmosphereSettings.cs:74`），见 `RimWorld/WindPassableLoad.cs:101-150`；`windToleranceFactor` 乘进阈值（`:347`）。
3. **屋顶** `WindRoofLoad`：成员＝`RoofMask>0.5` 且"构造屋顶"（`pressureToleranceFactor < 1000`，`RimWorld/WindRoofLoad.cs:229-235`）；损坏落点 `RoofCollapserImmediate.DropRoofInCells`（`:378-379`）。屋顶同时有**风项与压力项**（`:247-330`）。
4. **抛飞** `WindTransport`：物品按格风载荷与 sigma 判定后 `TryLaunch`（`RimWorld/WindTransport.cs:82-200`）；pawn 按 stagger/knockdown/throw 阈值判定（`:260-360`）。

而 `WindLoadField` 是 8×8 块粒度 + 3×3 膨胀（`RimWorld/WindLoadField.cs:57-104`）。**这一点很重要**：即使某格风为 0，只要同块 8 格内有风，`BoundAt` 仍返回该块最大值。所以"域内清速"必须清到边界外至少 1 个块（8 格），或者干脆不用清速方案。

四条通道里，第 1、2 条按"建筑/物品自身"判定，第 3 条按"格"判定，第 4 条按"物品/pawn 位置"判定。**没有单一开关能让一个域同时免疫四条**——除非让域内速度场真的为 0（且覆盖膨胀半径），但那会把护盾变成隔离罩。这是需求 2 必须做 4 个补丁点的根本原因。

---

## 3. 需求 1：所有超维建筑不受风

### 3.1 现状

超维建筑全部是 `Standable` 或 `PassThroughOnly`，`fillPercent` 为 0.5（幻影墙为 1）。AO 的 `AirCanMoveThrough` 判定 `def.Fillage != FillCategory.Full` 即"空气可穿过"（`RimWorld/MapObstacleMaskBuilder.cs:191-204`），因此这些建筑：

- 不会进 `WindStructuralLoad`（要求 `obstacleMask<=0`）；
- 会进 `WindPassableLoad`（`fillPercent 0.5 >= 0.1`），可能被吹动/损坏。

### 3.2 方案

给全部超维建筑 Def 加：

```xml
<li Class="AtmosphereOverhaul.Simulation.BuildingAtmosphereExtension">
  <airPassable>true</airPassable>
  <airFillPercent>0.01</airFillPercent>
  <windToleranceFactor>1000000</windToleranceFactor>
  <pressureToleranceFactor>1000000</pressureToleranceFactor>
</li>
```

- `AirFill = 0.01 < 0.1` → 退出 `WindPassableLoad`（`Simulation/BuildingAtmosphereExtension.cs:32-36` + `RimWorld/WindPassableLoad.cs:101-150`）。
- 同时 `airPassable=true` 让 AO 的气体/阻尼把它们当"可穿过"，与它们 `PassThroughOnly` 的原版语义一致，不会误挡风。
- `windToleranceFactor`/`pressureToleranceFactor` 是双保险：若将来某建筑改成实心（Fillage Full），它进结构通道后 `ratingQ` 被放大到 `load` 只剩压力项；压力项再被 `pressureToleranceFactor` 压掉。

### 3.3 判断

**可行且零运行时成本**（纯 XML，`BuildingAtmosphereExtension.For(def)` 有字典缓存）。唯一注意：幻影墙这类 `fillPercent=1` 但 `passability=Standable` 的建筑，`Fillage` 仍是 Partial（非 Full），因此同样走"可穿透物"通道，被 `airFillPercent=0.01` 排除。若幻影墙被玩家切成"不可通行"（`Designator_PhantomWall2Passability`），需要确认切换后 `Fillage` 是否变成 Full——若变成 Full，它会进结构通道，此时靠 `windToleranceFactor=1e6` 仍然免疫。

---

## 4. 需求 2：护盾保护范围

### 4.1 现实模型

力场不隔离介质，只耗散弹片与气流。所以护盾内**不应该**变成无风/恒温区，只应该"东西不被吹坏"。这与穹顶的边界语义必须区分。

### 4.2 现有可用的 O(1) 查询

- 圆/方护盾：`CompOmniProjectileInterceptor.IsCellInside(cell)` 是 public virtual（`CompOmniProjectileInterceptor.cs:75-78`）；`OmniInterceptorTracker` 有每格 `cellCache` 和 public `IsCellWithinShield(IntVec3, out protector)`（`:386-400, 683-700`），且明确排除穹顶（`!(staticInter is CompOmniForceFieldDome)`）。
- 穹顶：`OmniForceFieldDomeNetworkManager.GetInstance(map).IsDomeCell(c)` / `TryGetPropsAt(c, out props)`（`OmniForceFieldDome.cs:786, 815`，public，O(1)）。
- 生物圈：`CompBiosphereManager.GetBiosphereAt(map, c)`（`Patch_CompBiosphere.cs:216`）。

也就是说，补丁侧已经有一层"格 → 保护者"的 O(1) 索引，不需要每步扫描建筑。

### 4.3 四个补丁点

| 通道 | 补丁点 | 做法 |
|---|---|---|
| 结构 | `WindStructuralLoad.Rebuild`/成员循环 | 收集成员时，若 `cell` 在任一护盾内 → `continue`（建筑不受伤） |
| 屋顶 | `WindRoofLoad.Tick` 的 cell 循环 | 若 `cell` 在护盾内 → 跳过（风项与压力项一起免掉，满足"房顶不损坏"） |
| 物品 | `WindTransport.Tick` 的 `tmpLaunch` 循环 | 若 `thing.Position` 在护盾内 → 不加入 `tmpLaunch`（或在 `TryLaunch` 前拦截） |
| pawn | `WindTransport.TickPawns` | 若 `pawn.Position` 在护盾内 → 跳过 stagger/throw |

四个点都只是"成员/候选循环里加一个 O(1) 判断"，每次检查是 O(成员数)，不是 O(格数)。护盾成员数量级是几十到几百，成本可忽略。

### 4.4 为什么不能"域内清速"

- 8×8 块 + 3×3 膨胀（`WindLoadField`）意味着清速要清到边界外 8 格，视觉上等于护盾外一圈突然无风；
- 清速会让 AO 的平流/扩散也停住，护盾意外获得"隔离"效果，与"只保护不隔离"的语义冲突；
- 所以护盾走"判定点跳过"，穹顶才走"域内清速"。

### 4.5 判断

**可行**。代价是 4 个 Harmony Postfix/Prefix（不涉及 transpiler），且全部集中在 AO 的风害类里，兼容面小。屋顶的"压力项"必须一起跳过，否则真空/低压地图上护盾内屋顶仍会被压塌——这正是"房顶不损坏"与"风不损坏"的差别。

---

## 5. 需求 3：穹顶＝稳定隔离环境

### 5.1 三件套：壳、顶、内部源

穹顶要成为"环境黑洞"，需要同时做三件事：

1. **壳**：AO 的 `obstacleMask` 决定平流/扩散/风能否穿过（`MapObstacleMaskBuilder.cs:48, 110-115`：`faceOpenX = obstacleMask[i] * obstacleMask[i+1]`）。壳格 `obstacleMask=0` 即"墙"，`faceOpen≈0` 即"面封闭"。
2. **顶**：AO 的 `roofMask` 来自 `map.roofGrid`（`MapObstacleMaskBuilder.cs:38-39`），有顶才无太阳直射、无外部温度松弛、相变按有顶、压力热项。超维现有 `applyRoof/roofDef` 会真写 `RoofGrid`（`OmniForceFieldDome.cs:417-497`），这条路对 AO 直接有效。
3. **内部源**：温度/湿度/氧/压力/风每步向设定值补偿。

### 5.2 壳必须是"声明式"的

AO 的掩膜不是持久数据：`AtmosphereMapComponent.RebuildObstacleMaskUnsafe` 会调 `maskBuilder.Rebuild`（`RimWorld/AtmosphereMapComponent.cs:3301-3308`），而 `MapObstacleMaskBuilder.Rebuild` 每次把 `obstacleMask` 从 `edificeGrid` 全量重算（`MapObstacleMaskBuilder.cs:26-88`）。因此：

- **不能**一次性写 `engine.ObstacleMask` 就当完成；
- 必须 hook `MapObstacleMaskBuilder.Rebuild` 的 Postfix，在每次重建后按穹顶几何重新注入壳格；
- 注入时机正好在 `ReconcileOpenedCells`、`windLoad.Rebuild`、`windPassables.RebuildBuildings`、`windRoofs.Rebuild` 之前（`AtmosphereMapComponent.cs:3301-3316`），所以后续派生数据会读到注入后的掩膜。

这与姊妹文档的"声明式边界，而不是一次性改写"结论一致（`AO-超维科技穹顶与生物圈语义分析.md:55-57`）。

### 5.3 "允许漏出"怎么表达

AO 没有单向阀原语。可用的近似有两个：

| 方案 | 做法 | 效果 | 风险 |
|---|---|---|---|
| 壳格 ε 面开度 | 壳格 `obstacleMask = 0.02`，相邻面开度 `0.0004` | 自然产生极小的平流/扩散/热交换，双向但极慢 | `obstacleMask>0` 的格会被当作"空气格"（有压力体积、进 `ApplyThermodynamicPressureBaseline`），壳自身也有压力 |
| 壳全封闭 + 壳侧独立源 | 壳 `obstacleMask=0`，在壳内侧/外侧各写补偿源 | 可控单向 | 需要自己搬运质量/能量，复杂且容易守恒出错 |

推荐 ε 方案：它让 AO 自己的求解器处理渗透，补丁只负责"内部钉住"，物理上最自洽。**但必须承认它仍是双向的**：外界冷空气会以极慢速度渗入，内部每步补偿把它顶回去，表现为"内部稳定、外界几乎不受影响"，而不是真正的"只出不进"。

### 5.4 五通道钉住的写入路径

| 通道 | AO 公共 API | 落点 | 备注 |
|---|---|---|---|
| 温度 | `AtmosphereInterface.AddTemperatureDelta`（`:241`） | `pendingTemperatureDeltas` → `ApplyTemperatureDeltas` | 每步写 `目标 - 当前` |
| 湿度 | `AddMoistureDelta`（`:263`） | `pendingMoistureDeltas` | 同上 |
| 氧气 | `AddOxygenPercentDelta`（`:431`） | `incomingOxygenDeltas` → `AddOxygenPercent` | 百分比，注意不要超过 100 |
| 气压 | `AddPressureDelta`（`:297`） | `incomingPressureDeltas`；compressible 模式下 `AddStoredPressurePa`（`AtmosphereMapComponent.cs:2049-2065`） | **依赖 compressible 模式**（默认 `CompressiblePressure=1`）。非 compressible 时写 `Pressure.Read`，会被 `ApplyThermodynamicPressureBaseline` 每步重写（`LocalAtmosphereEngine.cs:4017-4046`） |
| 风 | 连续源 `MaintainTargetWind` + `TargetWindBlendPerTick`（`AtmosphereSourceRates`） | `ProjectAndFinalize` 之后 `ApplyTargetWindSourcesAfterSolverUnsafe`（`AtmosphereMapComponent.cs:2730-2770`） | 这是 AO 官方支持的"钉住目标风"路径，比自己写速度数组安全 |

气压这一点要特别说明：`AddPressureDelta` 在 compressible 模式下**就是**写 `StoredPressurePa`（`AtmosphereMapComponent.cs:2059-2062` → `LocalAtmosphereEngine.AddStoredPressurePa`，`LocalAtmosphereEngine.cs:2689-2696`），而 `StoredPressurePa` 正是 `ApplyThermodynamicPressureBaseline` 在 compressible 模式下的唯一偏置来源（`:4031`）。所以"气压钉在设定值"在这条路径上是成立的，前提是 compressible 模式开启。

### 5.5 判断

**可行**，但有三条必须写进设计说明的偏差：

1. "外界不影响"是"内部补偿足够快"的近似，不是硬隔离；
2. 气压钉住依赖 compressible 模式，需要检测/引导（若玩家关了可压缩压力，穹顶气压无法钉住，只能退回"读数覆盖"）；
3. 每步对域内每格写 5 个通道，成本 = O(域格数)；9×9 穹顶约 81 格，可忽略；若以后支持大穹顶，必须按面积定价或降频。

---

## 6. 需求 4：生物圈＝异形穹顶

### 6.1 与穹顶的差别

生物圈是 `Area` 指定的开放区域（`CompBiosphere.areaName`，`CompBiosphere.cs:63-140`），没有壳、没有屋顶。所以它是"植物工厂"模型：开放边界，靠强制通风维持状态。

### 6.2 方案

- 风：在 Area 内注册连续源 `MaintainTargetWind`（目标风速由玩家设定，默认 0）；
- 温度/湿度/氧/气压：同 5.4，但**只对 Area 内的格**写；
- 不做壳（否则生物圈会变成穹顶）；
- 保留现有的"有顶判定"等原版语义 patch 不动（它们不影响 AO 场）。

### 6.3 性能

Area 是玩家画的，可能几千到上万格。按项目红线（每粗步 ≤2N，N=62500，即 ≤12.5 万格操作），五通道 × 10000 格 = 5 万格操作/步，仍在红线内但已占 40%。建议：

- 设定"单次最大影响面积"（例如 5000 格），超出部分按距离衰减或拒绝；
- 只写"偏离超过阈值"的格（温差 < 0.1℃、湿度差 < 1%、气压差 < 100 Pa 时跳过），稳态下开销接近 0；
- 与 AO 的 `SimulationIntervalTicks`（默认 30）对齐，不自己另开 tick。

### 6.4 判断

**可行**，且比穹顶简单。核心风险不是技术，而是"魔法边界"的体验与功率定价：Area 内恒定 21℃、外面暴风雪，等于把 AO 的天气作用切掉了一块。必须用电力/科技/面积限制来定价。

---

## 7. 需求 5：随身指定 pawn

### 7.1 AO 对 pawn 的三条作用路径

1. **温度**：`GenTemperature.GetTemperatureForCell` 被 AO Postfix 覆盖（`Patches/1TemperaturePatches.cs:14-40`）；体感温度 `PawnEnvironmentCell.TryFeelsLike` 用该温度 + 风/湿（`Patches/3PawnEnvironmentPatches.cs:13-26`）。
2. **气压/氧**：`PawnAtmosphere.TrySample` 直接读 AO 场（`Pawns/PawnAtmosphere.cs:20-45`），`HediffGiver_AtmosphereTolerance.OnIntervalPassed` 据此加/减 severity（`Pawns/1AtmosphereHediffGivers.cs:16-100`）。
3. **风**：`WindTransport.TickPawns` 的 stagger/throw（`RimWorld/WindTransport.cs:260-360`）。

### 7.2 现有随身实现覆盖到哪

`Patch_PersonalTemperature.cs` 用 `Thing.AmbientTemperature` Postfix（`Priority.Last`）覆盖 pawn 的环境温度，并给 `SafeTemperatureAtCell`/`ComfortableTemperatureAtCell` 加 Postfix（`:7-124`）。

Harmony 的优先级是"数字越大越先执行"（`First=800 ... Last=100`），所以：

- 超维的 `Priority.Last` 在 AO 的 `800` **之后**执行，能覆盖 `Thing.AmbientTemperature` 与 `GenTemperature.*` 的读数；
- 但 AO 的体感温度 `PawnEnvironmentCell.TryFeelsLike` 直接调 `GenTemperature.GetTemperatureForCell`，不经过 `Thing.AmbientTemperature`，**随身覆盖不到**；
- 气压/氧 hediff 完全不经过温度查询，**随身覆盖不到**；
- 风抛飞也**覆盖不到**。

### 7.3 要"完全不受伤"需要补三个点

| 路径 | 补丁点 | 做法 |
|---|---|---|
| 气压/氧 hediff | `HediffGiver_AtmosphereTolerance.OnIntervalPassed` | 若 pawn 在随身名单 → 直接 `return`（或 patch `PawnAtmosphere.TrySample`，对该 pawn 返回 101.3kPa / 21% O₂） |
| 风 | `WindTransport.TickPawns` | 若 pawn 在随身名单 → 跳过 |
| 体感温度 | `PawnEnvironmentCell.TryFeelsLike`（或 `FeelsLikeTemperature.TryGetFromAir`） | 该函数不带 pawn 参数，只能在 `SafeTemperatureAtCell`/`ComfortableTemperatureAtCell` 的 Postfix 层用 pawn 上下文覆盖（超维已有），或给 AO 的 patch 加 pawn 判断 |

### 7.4 判断

**部分可行**：温度"安全判定"已覆盖；气压/氧/风需要新增补丁点；"体感温度显示"需要接受一个近似——AO 的体感是格级的，不带 pawn，随身只能覆盖"判定"，覆盖不了"显示值"。若要显示也正确，需要在 `FeelsLikeTemperature.TryGetFromAir` 的调用侧注入 pawn 上下文（类似超维现有的 `PersonalTemperatureReadContext`），改动面更大。

---

## 8. 统一架构建议

把五个需求收敛成一个"域（Domain）"抽象，补丁只认域，不认具体建筑：

```
IDomainProvider（每格 O(1) → Domain 或 null）
  ├─ ShieldDomain   ：OmniInterceptorTracker.IsCellWithinShield
  ├─ DomeDomain     ：OmniForceFieldDomeNetworkManager.IsDomeCell / TryGetPropsAt
  └─ BiosphereDomain：CompBiosphereManager.GetBiosphereAt

Domain 的属性：
  - Kind（Shield / Dome / Biosphere）
  - Cells（缓存，dirty 时重建）
  - 设定值（温度/湿度/氧/气压/风速）
  - 行为：是否挡风、是否挡介质、是否钉住
```

- 护盾：`挡风=false, 挡介质=false, 钉住=不钉`，只在风害四通道里"跳过"。
- 穹顶：`挡风=true, 挡介质=true, 钉住=五通道`，壳由 `MapObstacleMaskBuilder.Rebuild` Postfix 注入。
- 生物圈：`挡风=false, 挡介质=false, 钉住=五通道`，无壳。

这样补丁点在风害类里只问"这个格是否属于某个 Domain"，在写入路径里只问"这个 Domain 的设定值"，不会退化成五套独立逻辑。

---

## 9. 性能预算

| 补丁点 | 频率 | 每次成本 | 备注 |
|---|---|---|---|
| 建筑 def 级风免疫 | 加载时一次 | 0 | XML |
| 护盾风害跳过 | 每风害检查（默认 30 tick） | O(成员数) | 几十到几百 |
| 穹顶壳注入 | 每次掩膜重建（地图结构变化） | O(壳格数) | 周长量级 |
| 穹顶/生物圈五通道钉住 | 每 AO 模拟步（默认 30 tick） | O(域格数 × 5) | 关键预算项 |
| 随身判定 | 每风害检查 / hediff interval | O(pawn 数) | 可忽略 |

按红线 12.5 万格操作/步：

- 穹顶 9×9 ≈ 81 格 × 5 = 405，安全；
- 生物圈 5000 格 × 5 = 2.5 万，安全但已占 20%；
- 生物圈 20000 格 × 5 = 10 万，接近红线，必须做"偏离阈值跳过"或面积上限。

---

## 10. 风险清单

1. **掩膜重建时序**：壳注入必须在 `MapObstacleMaskBuilder.Rebuild` 之后、`windLoad/windRoofs.Rebuild` 之前；若用 `RebuildObstacleMask()`（public）手动触发，注意它会走 `RefreshCachedMapDataUnsafe` 全流程，成本 O(N)。
2. **`ReconcileOpenedCells`**：它比较前后掩膜处理"打开的格"；我们注入的是"从 1 改 0"（空气→墙），需要确认它不会把壳格当"可通行"处理。
3. **`obstacleMask = 0.02` 的语义**：AO 多处用 `>0` 判断"有空气"，壳格会参与压力基线、体积源、空气混合；需要逐处确认不会产生异常压力/气体。
4. **气压钉住依赖 compressible 模式**：若玩家关闭可压缩压力，`AddPressureDelta` 落 `Pressure.Read` 且被每步基线重写，穹顶气压无法稳定。
5. **Harmony 优先级**：随身 patch 用 `Priority.Last`；新增的气压/氧/风补丁必须确认与 AO 的 `800`/`0` 顺序，避免互相覆盖。
6. **跨线程**：AO 的模拟在 Task 里跑；补丁只能在主线程写 `incoming*`/`pending*`（AO 的 API 已加锁），不要直接摸 `engine` 数组。
7. **屋顶压力项**：护盾/穹顶内屋顶若只免疫风项，真空地图上仍会被压力项压塌；必须一起跳过或给屋顶 `pressureToleranceFactor`。
8. **原版语义撕裂**：穹顶写 `RoofGrid` 会让原版也认为"有顶"（光照/种植/太阳能），这是现有实现的行为；若要"只对 AO 生效"，需要改为 AO 侧 `roofMask` 注入，而不是写 `RoofGrid`——两者不能同时成立。
9. **性能**：生物圈 Area 面积不可控；需要在 UI 层给"面积/功耗"上限，否则玩家画满全图会直接击穿红线。
10. **AO 版本漂移**：以上行号基于当前反编译版本（AO 1.7，Assembly MVID `4E9F1EAE-...`）；AO 更新后需重新核对 `AtmosphereInterface` 与掩膜构建。

---

## 11. 待拍板

1. 护盾内是否允许"完全无风"？（推荐否：只做风害判定跳过，保留风场与平流）
2. 穹顶"允许漏出"用 ε 面开度（双向、极慢）还是自建单向源？（推荐 ε）
3. 穹顶气压设定范围：只允许"地表压 ~ 低压"（受 `AddStoredPressurePa` clamp），还是也允许高压？
4. 生物圈面积上限与功率定价：按面积线性，还是按"每格维持功耗"？
5. 随身名单来源：沿用个人温控终端的设置列表，还是新建独立的"随身保护"名单？
6. 穹顶/生物圈的"设定值"是否允许玩家在 UI 里改 5 个通道，还是先只开放温度/气压？

---

## 12. 一句话总结

按此设计**可以落地**，但要接受三个事实：

- 护盾是"判定豁免"，不是"隔离罩"；
- 穹顶是"壳 + 顶 + 每步补偿"，"外界不影响"是补偿够快的近似，不是硬边界；
- 生物圈是"开放边界的强制通风"，性能与体验都由面积和功率决定。

三者共用一层"域索引"与一套"五通道写入"，不共用同一套行为开关——这是把五个需求做成一个体系、而不是五个特例的关键。
