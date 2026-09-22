# 万能工作代理跨地图工作、代理池回收与口袋地图支持 设计方案

> 状态：设计已定稿（五项决策 + 九条追加要求已纳入），尚未实施。
>
> **通用性要求（首要约束）**：必须在**完全不安装** MultiFloors / SimplePortal / RVwithPD / RVAutoHome 的情况下正常工作，且必须**自动支持任何行为类似的未知 Mod**，不得以"识别 Mod 身份"为前提。
>
> 相关文档：`OmniWorkstationNavigation.md`（代理导航规则）、`OuterrealmStorageRuntimeLifecycleRefactor.md`（生命周期）、`GlobalStorageDesign.md`（GameComponent 读档时序先例）。

## 1. 问题

工作代理（`FAOC_OmniWorkProxy`）在第三方 Mod 把它搬到另一张地图后失控：既不受工作站控制，也无法被"重建工作代理"回收，并会满地图乱跑。

已知触发方（均通过口袋地图 / 楼层地图把 Pawn 在 `Map` 之间转移）：

- MultiFloors（`F:\294100\294100\3384660931`，`1.6\Assemblies\MultiFloors.dll`）
- RV with built-in PD（`F:\294100\294100\3342334887`，`RVwithPD.dll`）
- SimplePortal（`F:\294100\294100\3325512144`，`SimplePortalLib.dll`）
- RV Auto-Embark（`F:\294100\294100\3801427298`，`RVAutoHome.dll`）

### 1.1 失控链路的精确时序

| # | 事件 | 现有代码的反应 | 结果 |
|---|---|---|---|
| 1 | 泵唤醒代理 `a`（A 图，A 池） | `WakeProxyForVanillaSearch` → `Assignments[a]=A 站`、`ActiveProxies+={a}`、`AreaRestriction=A 区` | 正常 |
| 2 | `JobGiver_Work` 在 A 图返回 `NoJob` | — | — |
| 3 | MultiFloors Postfix 瞬时把 `a` 挪到 B 图搜索后挪回 | **无察觉**（同调用栈、无 tick、不经过 DeSpawn/Spawn） | 得到 `MF_ChangeLevelThroughStair` job |
| 4 | `a` 执行该 job → `SwitchMap`：`DeSpawnOrDeselect` + `GenSpawn.Spawn(a, B 图)` | 我们没有 `SpawnSetup` 钩子 | `a.Map = B 图`，**仍绑定 A 站** |
| 5 | ≤60 tick 内 A 图 `EnsureProxyCount`（`OmniWorkstation.cs:2062-2070`）看到 `a.Map != map` | `Unassign(a)` → `proxies.RemoveAt` | **`a` 从 A 池永久消失** |
| 6 | `a` 在 B 图工作结束 → `EndCurrentJob` | `Patch_OmniWorkProxy_DeactivateOnJobEnd`（`:2934`）取 **B 图**组件 | B 图组件 `proxies` 无 `a` → `NotifyProxyBecameIdle` 无命中，静默 return |
| 7 | `a` 继续思考（`mindState.Active` 仍 true） | `FreezeWhenInactive`（`:2906`）：`IsActive=false` 但 `CurJob != null && !IsIdleJob` → **放行 Tick** | `a` 成为"技能 999 / 全工作开放 / 无区域限制"的自由殖民者 → **满地图乱跑** |
| 8 | 玩家点"重建工作代理" | `PerformRecreateAllProxies`（`:2127`）只遍历 A 图 `proxies` + A 图 `sleepingProxies` | **无法回收 B 图上的 `a`** |

附加恶化因素：

- `TryGetStation`（`:1015-1032`）要求 `station.Map == pawn.Map`，否则**主动 `Unassign`** → 跨图瞬间失去工作类型白名单、活动区限制与失败隔离。
- `Covers()`（`:66-69`）是"以工作站为圆心的半径 + `cell.InBounds(工作站所在图)`"，跨图时按同坐标判定。
- `RefreshProxyState`（`:2238`）、`PutProxyToSleep`（`:2439`）都有 `pawn.Map != map` 硬短路。
- 静态 `Dictionary Assignments` 被主线程写、被 `OmniWorkProxyNavigation`（异步寻路路径生成）读 → 数据竞争（见 AGENTS.md 的 1.6 多线程注意事项）。

## 2. 第三方 Mod 的跨图机制（反编译确证）

### 2.1 MultiFloors

- **楼层图是原版 pocket map**：`MultiFloors.Maps/LevelMapGenerator.cs:15-34` 用 `WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.PocketMap)` + `MapGenerator.GenerateMap(...)` + `Find.World.pocketMaps.Add(val)`，并设置 `PocketMapParent.sourceMap`。
- 权威目录：`map.GroundMap().LevelMapComp().MapByLevel`（`MF_LevelMapComp`），另有 `Map.LowerMap()/UpperMap()`。
- **路径 A 标准换图**（`MultiFloors.Jobs/CrossLevelMoveJobUtility.cs:110-116`）：

```csharp
pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false, true);
pawn.DeSpawnOrDeselect(DestroyMode.Vanish);
GenSpawn.Spawn(pawn, newPos, newMap, Rot4.Random, WipeMode.Vanish, false, false);
```

由 `Toils_Stairs.MoveToDestLevel`（`Toils_Stairs.cs:107-140`）的 `initAction` 在爬完楼梯时调用，随后 `pawn.TryResumeDeferredJob()` 启动换图前预存的真实工作。

- **路径 B 字段级瞬移**（`MultiFloors/LevelUtility.cs:93-111`）——不触发任何 Harmony 钩子：

```csharp
pawn.OldMap() = pawn.Map;  pawn.OldPosition() = pawn.Position;
((Thing)pawn).mapIndexOrState = (sbyte)Find.Maps.IndexOf(newMap);
((Thing)pawn).positionInt  = newPosition;
```

- **跨层工作扫描**（`HarmonyPatches/HarmonyPatch_ScanJobsOnOtherLevel.cs`）：`JobGiver_Work.TryIssueJobPackage` 的 Postfix（`HarmonyPriority(100)`）。条件 `!ScanningOtherLevel && __result == NoJob && !pawn.StayOnCurrentMap() && pawn.Map.TryGetLevelControllerOnCurrentTile(...)`；命中则对每个"垂直向外"的楼层执行 `UnsafeTeleportTo` → **在该图上下文重跑 `TryIssueJobPackage`** → 找到工作就用 `MakeChangeLevelThroughStairJob(destStair, destMap)` 替换结果，真实工作经 `pawn.NextJobThinkResult()` 保存 → `UnsafeTeleportBack`。
- **瞬移与回移在同一调用栈内完成，中间不推进 tick** → 只要归属校验发生在 tick 边界，就永远不会误判它为跨图。
- 跨层 job 用 `job.globalTarget = new GlobalTargetInfo(IntVec3.Zero, destMap, false)` 承载目标图（`CrossLevelJobFactory.cs`）。
- `pawn.StayOnCurrentMap()`（Prepatcher 字段，`MultiFloorManager.LockPawn` 会临时置 true）是 MultiFloors 官方的"禁止该 pawn 跨层"开关。
- **楼梯/电梯都是原版 `MapPortal` 的子类**：`public abstract class Stair : MapPortal`（`Stair.cs:13`），`StairEntrance : Stair`（`StairEntrance.cs:12`）、`StairExit : Stair`（`StairExit.cs:11`）、`Elevator : Stair`（`Elevator.cs:14`）——这是第 10 章"入口授权"的通用基础。
- Settings 打开 `UsePrioritizedScanner` 时改由 `HarmonyPatch_ScanJobsOnOtherLevelPrioritized` 用 transpiler 替换 `WorkGiversInOrderNormal`。

### 2.2 SimplePortal

- 复用**原版 `MapPortal`**（`RimWorld/MapPortal.cs`，`Building` + `IThingHolder`）；`SimplePortal_Building : MapPortal`（`SimplePortal_Building.cs:12`），`CompSimplePortal` 持有 `linkedPortal`（另一端建筑，位于另一张图），且 `GetOtherMap()` 返回 `linkedPortal.MapHeld`（`:251-266`）。
- 换图点唯一（`JobDriver_EnterSimplePortal.cs:93-100`）：`DeSpawnOrDeselect(Vanish)` + `GenSpawn.Spawn(pawn, val, linkedPortal.MapHeld, ...)`。
- 进入由 `JobGiver_EnterSimplePortal` / `JobGiver_HaulToSimplePortal`（SimplePortal 自己实现）挑选空闲殖民者 → **我们的代理天然是候选**。

### 2.3 RVwithPD

- RV 内部空间是原版 pocket map（`PocketMapUtility.DestroyPocketMap`）。
- `InteriorSpaceMapComponent` 挂在内部 pocket map 上，持有 `ownerThing`（车辆）；关闭时 `TeleportPawnsClosing()` 用 **`SkipUtility.SkipTo(pawn, cell, dest)`**（原版 API，`RimWorld/SkipUtility.cs:15-27`），`dest = ownerThing.MapHeld`，找不到则退化为"任意 `IsPlayerHome` 的图"。
- RVwithPD 自身没有 pawn 的 `GenSpawn.Spawn` 调用 → 换图由 RV 本体与 `SkipUtility` 完成。
- **含义**：RV 把代理吞进去再吐出来时，落点是"车辆当前所在图"，与代理原本的归属图无关；**它没有 portal 建筑可供范围判定** → 第 10.4 节的"载体授权"。

### 2.4 RVAutoHome —— "自动跨图工作 + 自动回家"的参考实现

| 组件 | 作用 |
|---|---|
| `RVAutoHomeGameComp`（GameComponent，1216 行） | 全局跨图调度器 |
| `HomeRecord { vehicle, roomMap, OuterMap, workerSlots, workersInside, snapshotTick, ... }` | 跨图状态快照，250 tick 缓存 |
| `HomeUtility.IsAutoHomeWorker` / `HomeDecision` / `RoomFlow.TickGatheringExit` | 谁进去工作 / 谁出来集会 |
| `BaseWorkProbe.TypesFor(map)` | 按图缓存 250 tick 的"这张图有什么工作"位图；`Refresh` 开头即 `if (map != null && !map.IsPocketMap)` |
| `SimplePortalCompat.cs` | 用 SimplePortalLib 的 `EnterSimplePortal` job 完成进出 |

启示：跨图工作调度应当是"**全局 GameComponent + 每图能力快照 + 跨图 job 执行**"三段式，而不是把状态绑在单张图的 MapComponent 上。

## 3. 通用性设计（核心约束）

### 3.1 三条硬约束

| | 约束 | 含义 |
|---|---|---|
| **C1** | **零依赖可运行** | 未安装任何相关 Mod 时，全部功能必须可用（此时"跨图"退化为原版多图场景：多殖民地、gravship、原版 `MapPortal` 等），且行为与开销不得与现状有可观察差异 |
| **C2** | **未知 Mod 自动支持** | 不得以"识别 Mod 身份"为前提；必须以"观测 Pawn 的客观状态 + 原版图关系"驱动 |
| **C3** | **特化只做加速** | 任何 Mod 特化路径必须 (a) 软依赖、(b) 可失败并降级、(c) 降级后行为依然正确 |

### 3.2 可用的原版通用设施（已核实）

| 通用观测点 | 原版 API | 用途 |
|---|---|---|
| 代理出现在某图 | `Pawn.SpawnSetup(Map, bool)`（`Verse/Pawn.cs:693`；`Verse/GenSpawn.cs:178` 证明所有 `GenSpawn.Spawn` 都走它） | **真实换图的权威信号** |
| 代理离开某图 | `Thing.DeSpawn` / `Thing.DeSpawnOrDeselect`（`Verse/Thing.cs:637`） | 区分"我方入舱"与"被第三方搬走" |
| 代理被容器吞掉 | `Pawn.ParentHolder`（`IThingHolder`）；`MapPortal` 本身就是 `IThingHolder`（`GetDirectlyHeldThings()` 返回 `containerProxy`） | 不再依赖"地图上能看见它" |
| 图上所有代理 | `map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn)`。**注意不能用 `mapPawns`**：`PrepareProxy` 已 `DeRegisterPawn`（`:2204`），代理不在 `mapPawns` 里 | 读档重建 / 兜底收养 |
| **图上所有入口** | `map.listerThings.ThingsInGroup(ThingRequestGroup.MapPortal)`（原版枚举成员，见 `Verse/ThingRequestGroupUtility.cs:170`；RVwithPD 亦用该组枚举 `MapPortal`） | 第 10 章入口授权 |
| 图之间的关系 | `Map.Parent`（`MapParent`）、**`PocketMapParent.sourceMap`**（`RimWorld/Planet/PocketMapParent.cs:14`，`public Map sourceMap`）、`Map.Tile`、`Map.IsPocketMap` | 判断"同群 / 派生"关系，**全部是原版类型** |
| 入口 ↔ 目标图 | `MapPortal.GetOtherMap()`（由 `MapPortal` / `PocketMapExit` / `Stair*` / `SimplePortal_Building` 各自实现）、`MapPortal.exit`（`PocketMapExit`）、`PocketMapExit.entrance` | 第 10.3 节 |
| 通用跨图传送 | `SkipUtility.SkipTo(thing, cell, dest)`（`RimWorld/SkipUtility.cs:15-27`） | 异常时的替代回收手段 |
| 工作查询的图 | `JobGiver_Work.TryIssueJobPackage` 内全程使用 `pawn.Map` / `pawn.Position`（`RimWorld/JobGiver_Work.cs:43-208`） | 见第 9、10 章 |
| 通用 job 入口 | `Pawn_JobTracker.StartJob`；跨图 job 的 `Job.globalTarget`（`GlobalTargetInfo`） | 开关与非工作入口拦截 |
| **地图组件自动注册** | `Verse/Map.cs:515-533` `FillComponents()` 遍历 `typeof(MapComponent).AllSubclassesNonAbstract()` 并 `Activator.CreateInstance(type, this)`（跳过 `CustomMapComponent` 子类） | **含 pocket map 的每张图都自动拥有 `MapComponent_OmniWorkstation`，无需注册代码** |
| **寻路网格自动注册** | `Verse/AI/Pathing.cs:20-38` 构造函数 `foreach (PathGridDef key in DefDatabase<PathGridDef>.AllDefsListForReading)` → `Activator.CreateInstance(key.workerType, map, key)` | 每张图都有 `FAOC_OmniWorkProxyPathGrid` |
| **地图组件随存档保存** | `Verse/Map.cs:687` `Scribe_Collections.Look(ref components, "components", LookMode.Deep, this)` | 口袋图的代理池（含休眠舱）随存档保存 |
| **地图销毁** | `Verse/PocketMapUtility.cs:36-41` `DestroyPocketMap` → `Game.DeinitAndRemoveMap(map, true)`；`Verse/Game.cs:585-624` 顺序为 `MapDeiniter.Deinit` → `maps.Remove` → `MapComponentUtility.MapRemoved(map)` → `map.Dispose()`，并级联销毁 `sourceMap == 被删地图 && destroyOnParentMapAbandoned` 的子口袋图 | 见 6.4 与第 13 章 |

### 3.3 无法通用化的部分（重要负面结论）

- **原版 portal 无法"通用地走进去"**：`EnterPortalUtility.JobOnPortal` 构造的是 `JobDefOf.HaulToPortal`（搬运 job），且 `HasJobOnPortal` 要求 `portal.leftToLoad` 非空；真正的"进入"由 `LordJob_LoadAndEnterPortal` / `LordToil_LoadAndEnterPortal`（lord 驱动）完成。**不存在"给任意 portal 发一个进入 job"的通用原版 API。**
- **MultiFloors 的跨层移动**（`MFJobDefOfs.MF_ChangeLevelThroughStair`）是 Mod 私有 job。
- 因此：**"优雅地走回去"不可能通用化**。核心回程必须是**传送回收**（6.2 R2），"走回"只作为已知 Mod 的加速器（6.2 R1）。
- **原版 `MapPortal.GetOtherMap()` 会惰性生成 pocket map**，而 `Stair.GetOtherMap()`（`Stair.cs:140-143`）**直接抛 `NotImplementedException`** → 求"入口通往哪张图"必须用防御性包装（10.3）。

### 3.4 为什么"未知 Mod 也会被自动覆盖"

1. **任何跨图最终都会留下原版可观测的痕迹**：标准路径必经 `GenSpawn.Spawn` → `SpawnSetup`；被装入容器 → `ParentHolder` 变化。
2. **即使 Mod 绕开事件**（如字段级瞬移 `UnsafeTeleportTo`）：`pawn.Map != homeMap` 会在**下一个 tick 边界**成立。而现有代码**已经在每 60 tick 读 `pawn.Map`**（`EnsureProxyCount`）——只需把反应从"删记录"改成"保留 + 看护"，**不新增任何必需机制**即可覆盖这类 Mod。
3. **任何跨图工作都使用原版搜索 API**（`pawn.Map.listerThings`、`GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ...)`），我们的补丁正挂在这些原版入口上 → "代理在别的图能否正常工作"自动成立。
4. **任何跨图都必须经过"入口"或"载体"**，而两者都可由原版类型枚举（`ThingRequestGroup.MapPortal` / `PocketMapParent.sourceMap`）→ 第 10 章的准入判定不需要识别 Mod。

### 3.5 覆盖面定义

> 任何把 Pawn 从一个 `Map` 转移到另一个 `Map`（含 pocket map），或转移进某个 `IThingHolder` 容器，并可能在其中调用 `DeSpawn` / `GenSpawn.Spawn` / 直接改写地图字段的 Mod。

由 3.4 自动覆盖，**不需要逐个适配**。

### 3.6 检验清单

| 场景 | 期望 |
|---|---|
| 无任何相关 Mod | `abroadMap` 恒为 null；驻外看护列表恒空；新增代码路径不执行；行为与现状一致 |
| 有已知 Mod（MultiFloors / SimplePortal / RV） | 走回（通道可用时），否则传送回收 |
| 有未知 Mod（把代理搬进任意 pocket map） | `SpawnSetup` 或 60 tick 校验触发；超时后传送回收；**绝不留在无主状态** |
| 未知 Mod 把代理装进容器并销毁容器 | `ParentHolder` 观测 + 超时 → 强制收回或按 `Destroyed` 清理 |
| 已知 Mod 被卸载 | 全部特化路径静默跳过，退化为通用路径，无异常 |

## 4. 设计目标、不变量与决策

### 4.1 不变量

> **代理的归属池由创建它的池决定，与它"当前在哪张图"完全解耦。位置只是运行状态。**
>
> **推论 1（池隔离）**：一切池级操作（重建、禁用、补齐）只作用于 `homeMap == 目标池` 的代理；正驻留在本图但归属别的池的代理**一律不动**。
>
> **推论 2（池粒度）**：池的粒度是**地图** —— 同一张图上的所有工作站**共享该图的池**（对应现有 `configuredProxyCount` 是 per-map 配置）。工作站只贡献"工作类型筛选 / 半径 / 启用状态"，不各自成池。
>
> **推论 3（入口准入）**：代理只能经由**被本池某个工作站范围覆盖的入口**离开当前图（第 10 章）。

### 4.2 需求

| | 需求 |
|---|---|
| R-1 | 支持自动跨图工作（环境中存在允许跨图工作的 Mod 时） |
| R-2 | 每张图有自己的代理池；A 池代理去 B 图工作后回 A 池；B 池代理来 A 图工作后回 B 池 |
| R-3 | 重建工作代理时，仍留在其他图的代理也必须被强制销毁 |
| R-4 | 每个代理记录自己的归属池；游戏全局管理组件能查询并管理所有代理，并支持强制重建所有代理 |
| R-5 | 工作站支持运行在口袋地图中，而不是必须在主地图 |
| R-6 | 重建只作用于"该工作站所在地图的池"；不属于此池的代理不重置 |
| R-7 | 新增按钮与管理界面：查看/管理全游戏所有工作站、地图池与所有代理；显示每个代理的**归属地图**与**当前所在地图 + 位置**；可强制重建所有代理；可控制所有工作站是否工作 |
| R-8 | 单代理操作：手动重建指定代理；强制指定代理停止工作并立即回收回所属池（修复卡住的代理） |
| R-9 | 重建因地图丢失等原因意外丢失的代理（池缺口修复） |
| **R-10** | **入口授权**：若图上某个通往其它地图的入口**没有被本池任何工作站的范围覆盖**，代理就**不应该去该入口对应的地图** |
| **R-11** | 池按**地图**共享（同图所有工作站共用一个池）——已确认，见 4.1 推论 2 |

### 4.3 已拍板的决策

| 决策 | 选定 |
|---|---|
| 工作范围 | 按**同坐标投影**归属图工作站的半径 |
| 存档恢复 | `GameComponent_OmniWorkProxyRegistry` 内的 `List<ProxyHomeRecord>`（`IExposable` + `LookMode.Deep`）随档保存；代理身上另有 `Hediff_OmniWorkProxyHome` 镜像（`isBad=false`）用于索引损坏时自愈 |
| 回程策略 | 统一走**传送回收**（原版无通用"走进 portal 即换图"API，理由见 6.2）：空闲 600 tick、工作中 1500 tick |
| **多段换图授权** | **逐段授权（严格）**：A→C→B 的每一段都要单独满足"该段入口被覆盖"，任一段不通过则目标图不可达 |
| **非 portal 型跨图** | **按"载体（车辆）位置是否落在工作站范围内"判定**（10.4） |

### 4.4 关键前提（已核实）

- 所有 `GenSpawn.Spawn` 都调用 `SpawnSetup`（`Verse/GenSpawn.cs:178`）。
- `Pawn_PlayerSettings.allowedAreas` 是 `Dictionary<Map, Area>`（`Pawn_PlayerSettings.cs:22,84`）→ **每图一份活动区是原版机制**。
- `Thing.PreSwapMap/PostSwapMap` 只被 `GravshipUtility` 调用（`GravshipUtility.cs:314,322`），不能当通用换图钩子。
- `JobGiver_Work.TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)` 是 `public override`（`RimWorld/JobGiver_Work.cs:43-208`）。
- `Stair : MapPortal`（`Stair.cs:13`）；`SimplePortal_Building : MapPortal`（`SimplePortal_Building.cs:12`）；`PocketMapExit : MapPortal` 且与入口双向链接（`RimWorld/PocketMapExit.cs:16,29-33`）。

## 5. 数据结构

### 5.1 `GameComponent_OmniWorkProxyRegistry` → **全局代理管理组件**（新增，满足 R-4 / R-7 / R-8 / R-9 / R-10）

```csharp
/// 全局唯一的代理管理组件：查询 + 管理 + 强制重建 + 总开关 + 入口授权 + 视图数据源。
/// 由 Game.FillComponents 自动实例化并随存档深保存。
public sealed class GameComponent_OmniWorkProxyRegistry : GameComponent
{
    public enum ProxyState
    {
        Sleeping,        // 在归属图休眠舱
        IdleAtHome,      // 在归属图空闲（宽限/等待中）
        WorkingAtHome,   // 在归属图工作
        IdleAbroad,      // 驻外且空闲（等待回程）
        WorkingAbroad,   // 驻外且工作
        InContainer,     // 被 IThingHolder 吞掉（车辆 / MapPortal / 平台）
        Lost             // 记录存在但实体不可达（详见 6.7）
    }

    public sealed class ProxyHome                       // 每个代理的"归属池"记录
    {
        public Map homeMap;              // 归属池所在图（= 工作站所在图）
        public int stationThingId;       // 归属工作站
        public Area projectedArea;       // 在 homeMap 上的工作区
        public Map abroadMap;            // 当前驻外图（null = 在 homeMap）
        public bool inContainer;         // 被 IThingHolder 吞掉
        public int abroadSinceTick = -1; // 驻外起始 tick（回程超时）
        public int lastSeenTick;
        public ProxyState state;         // 缓存的状态（看护循环刷新）
    }

    private readonly Dictionary<Pawn, ProxyHome> homes = new Dictionary<Pawn, ProxyHome>();
    private readonly List<Pawn> scratch = new List<Pawn>();          // 复用，避免分配
    private readonly List<Map> scratchPools = new List<Map>();

    // ─── 全局总开关（R-7）───
    public bool GlobalWorkEnabled { get; private set; }   // Scribe_Values，默认 true
    public void SetGlobalWorkEnabled(bool enabled);       // 关闭时回收全部场上代理进入休眠

    // ─── 查询（R-4 / R-7）───
    public static GameComponent_OmniWorkProxyRegistry Instance { get; }  // 构造函数/ExposeData 赋值
    public bool TryGetHome(Pawn p, out ProxyHome home);
    public static Map HomeMapOf(Pawn p);                    // 无归属时回退 p.Map
    public MapComponent_OmniWorkstation ManagerFor(Pawn p); // 按 homeMap 取组件
    public MapComponent_OmniWorkstation ManagerFor(Map pool);
    public void EnumeratePool(Map pool, List<Pawn> into);   // 某池全部代理（含驻外）
    public void EnumerateForeign(Map pool, List<Pawn> into); // 某池的驻外代理
    public void EnumerateAll(List<Pawn> into);              // 全部代理
    public int TotalCount { get; }
    public PoolStats StatsOf(Map pool);                     // 总数/活跃/休眠/驻外/容器内/缺失

    // ─── 入口授权（R-10）───
    public bool IsEntryAuthorized(Map from, Map to);        // O(1) 查表（第 10 章）
    public void InvalidateEntryCache(Map from = null);      // 入口/工作站/地图变化时失效
    public void RefreshEntryCache();                        // 60 tick 周期全量重算

    // ─── 视图数据源（R-7：所有工作站 + 所有池 + 所有代理）───
    public void BuildOverview(List<PoolOverview> pools, List<ProxyRow> rows);
    // PoolOverview: pool(Map) / label / 工作站数 / 是否可用 / configured / total / active /
    //               sleeping / abroad / inContainer / missing / reachableMaps / uncoveredEntries
    // ProxyRow:     pawn / name / index / homeMap / homeLabel / currentMap / currentLabel /
    //               pos / state / jobReport / stuck / canReclaim

    // ─── 管理（R-3 / R-4 / R-8 / R-9）───
    public void Register(Pawn p, Map homeMap, int stationThingId);
    public void Unregister(Pawn p);
    public void NotifySpawned(Pawn p, Map map);             // SpawnSetup 钩子
    public void NotifyDespawned(Pawn p, DestroyMode mode);  // DeSpawn 钩子
    public void NotifyHomeMapRemoved(Map pool);             // 归属图被销毁
    public bool ForceDestroy(Pawn p);                       // 强删（任意图/容器/Job 状态）
    public int ForceRecreatePool(Map pool);                 // 重建单池（含驻外强删；R-3/R-6）
    public int ForceRecreateAllPools();                     // 重建所有池（R-7）
    public bool RecreateSingle(Pawn p);                     // 重建单个代理（R-8）
    public bool ReclaimNow(Pawn p);                         // 停工作 + 立即回收回所属池（R-8）
    public int RepairPool(Map pool);                        // 补齐/修复单池（R-9）
    public int RepairAllPools();                            // 修复所有池（R-9）
    public void RebuildAfterLoad();                         // FinalizeInit：扫 AllMaps 重建
}
```

**读档时序约束（沿用 `GlobalStorageDesign.md:115` 的既有教训）**：`Instance` 必须在本组件的**构造函数**或 `ExposeData(LoadingVars)` 中赋值，**不可照搬 `GameComponent_OmniResurrector` 的 `FinalizeInit` 赋值模式** —— 因为 `Building_OmniWorkstation.SpawnSetup` 早于 `GameComponent.FinalizeInit`，而 `SpawnSetup` 里就要注册工作站。取用统一走 `Current.Game.GetComponent<GameComponent_OmniWorkProxyRegistry>()` 兜底 + 静态 `Instance` 加速。

- **归属表用 `List<ProxyHomeRecord>` + `IExposable` 序列化**（`Scribe_Collections.Look(ref records, "proxyHomes", LookMode.Deep)`）：`Dictionary<Pawn,...>` 既无法直接 Scribe 又会强引用 Pawn，因此索引在 `PostLoadInit` / `FinalizeInit` 由该列表重建（`RebuildIndex`）。
- **代理身上另有镜像**：`Hediff_OmniWorkProxyHome`（见 5.2）。权威表缺失时用 `TryRestoreFromMirror` 反向把归属救回。
- **镜像校验回写**：`Pawn.SpawnSetup` / `Pawn.DeSpawn`（换图最容易不一致的时刻）以及低频周期（240 tick）都走 `VerifyMirror` —— 以权威表为准修正镜像；权威表里根本没这个代理时，先尝试用镜像救回，救不回就**清掉镜像**（否则下一次会把它"救"回一个已废弃的池）。
- **序列化** `GlobalWorkEnabled`（`Scribe_Values.Look`），避免读档后意外开始工作。
- `IsProxy(pawn)` 仍只看 `kindDef`（O(1)，不碰字典）。
- **无任何第三方 Mod 时**：`homeMap == p.Map` 恒成立，`EnumerateForeign` 恒空。

### 5.2 `Hediff_OmniWorkProxyHome`（新增 Hediff：归属的**镜像**，满足 R-4 的"代理记录自己的池"）

> **实现修正（重要）**：原设计的 `CompOmniWorkProxyHome : ThingComp` **已废弃**。理由（均为原版源码确证）：
> 代理的 `def` 是原版 `Human`（只有 `kindDef` 是 `FAOC_OmniWorkProxy`），而
> ① `Verse.Thing.ExposeData()`（`Source/Verse/Thing.cs:865`）**完全不序列化 `comps`**；
> ② 读档后 `ThingWithComps.InitializeComps()`（`Source/Verse/ThingWithComps.cs:159-178`）只按 `def.comps` 重建。
> 所以运行时 `AllComps.Add` 的组件**必然读档丢失**，而写进 `def.comps` 会污染所有人类。
> 同理，`BackstoryDef`（`Source/RimWorld/BackstoryDef.cs:16`）是 Def 级共享对象，**没有任何 per-pawn 数据槽**，不能承载归属；
> 它的正确用途是"不被随机生成"，项目已通过 `<shuffleable>false</shuffleable>` + PawnKindDef 强制指定实现。
> **唯一可靠的 per-pawn 载体是 Hediff**：`HediffSet.ExposeData`（`Source/Verse/HediffSet.cs:184`）
> 使用 `Scribe_Collections.Look<Hediff>(ref hediffs, "hediffs", LookMode.Deep)`，随存档深保存。

```csharp
public class Hediff_OmniWorkProxyHome : HediffWithComps
{
    public int homeMapUniqueId = -1;   // 归属池所在地图的 uniqueID
    public int stationThingId = -1;    // 创建该代理时绑定的工作站

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref homeMapUniqueId, "homeMapUniqueId", -1);
        Scribe_Values.Look(ref stationThingId, "stationThingId", -1);
    }
}
```

- `Defs/ThingDefs_Buildings/OmniWorkstation.xml` 新增 `HediffDef FAOC_OmniWorkProxyHome`：
  `hediffClass` 指向上面的类，**必须标注 `isBad=false`**（否则代理会被当作"带病"，触发心情/医疗相关判定），
  另设 `tendable=false` / `everCurableByItem=false` / `duplicationAllowed=false`，避免被抓去治疗或重复叠加。
- 只存 `int` 而不是 `Map` 引用：地图被销毁后它只是查不到，不会留下悬空引用。
- 代理休眠舱 `sleepingProxies`（`ThingOwner<Pawn>`，`LookMode.Deep`）承担"在舱内"的存在性；
  registry 的 `ProxyHomeRecord` 是**权威归属**，Hediff 只是它的一份可自愈副本。

### 5.3 跨图活动区（决策 1 落地）

利用原版 per-map 活动区（`Pawn_PlayerSettings.allowedAreas` 是 `Dictionary<Map, Area>`，`Pawn_PlayerSettings.cs:22,84`）：

```csharp
// 代理跨到 B 图时（驻外看护首次发现）：
Area projection = GetOrCreateProjectionArea(B图, station);   // 挂在 B图.areaManager
pawn.playerSettings.allowedAreas.SetOrAdd(B图, projection);
// 回 A 图后：allowedAreas[A图] 本来就还在，无需恢复
// 回收/销毁时：清理所有图上的 Area_OmniWorkStation 残留
```

```csharp
/// <summary>
/// 跨图范围判定：同坐标投影——以工作站坐标为圆心，用同一坐标系在任意图上求半径。
/// 工作站在本图时仍要求 InBounds（与原逻辑一致）；跨图时以目标图自身边界为准。
/// 入口覆盖判定（第 10 章）复用此方法。
/// </summary>
public bool CoversOn(Map pawnMap, IntVec3 cell)
{
    if (!Spawned) return false;
    Map home = Map;
    if (pawnMap == null || home == null || !cell.InBounds(pawnMap)) return false;
    return Position.InHorDistOf(cell, WorkRadius);
}
```

## 6. 策略生命周期

### 6.1 状态机

```
        Dormant(home 图休眠舱)
              │ WakeProxyAtStation（home 图 GenSpawn.Spawn 到工作站格）
              ▼
        Working@home ─── 第三方换图（SpawnSetup / ParentHolder / 60 tick 校验）───▶ Working@abroad
              ▲                                                                            │
              │                                                                   Job 结束 / 空闲
              │                                                                            ▼
              │         ┌────────────────────── Idle@abroad ──────────────────────┐
              │         │ R1 已知 Mod 的回程 job（可选加速：楼梯 / portal）          │
              │         │ R2 传送回收（零依赖·必然成功，超时 1500 tick 后强制）      │
              └─────────┴──────────────────────────────────────────────────────────┘
                    R2 = 放宽后的 PutProxyToSleep（DeSpawn 后收入 home 图休眠舱）
```

### 6.2 回程两级降级

**R1 走楼梯 / portal 回程 job —— 经核实不予实现（有源码依据）**

> **结论（实现修正）**：原设计的 `TrySendHomeJob`（给第三方 portal 构造回程 job）**已放弃**。
> 依据（rimsage 确证 `Source/RimWorld/EnterPortalUtility.cs:23-30`）：
> ① 原版**没有**"单个 Pawn 走进 portal 即换图"的通用 API —— `EnterPortalUtility.JobOnPortal`
> 只构造 `JobDefOf.HaulToPortal`，而 `HasJobOnPortal` 要求 `portal.leftToLoad` 非空（装载场景专用）；
> ② 玩家手动"进入门户"走的是 `LordJob_LoadAndEnterPortal` 的 Lord 机制，而代理**不在**
> `mapPawns` / `FreeColonists` 列表里（这正是它不进殖民者栏的原因），无法套用；
> ③ MultiFloors / SimplePortal 各有自己的换图实现（`UnsafeTeleportTo` / `linkedPortal`），
> 为其逐个写适配器会违反第 3 章的"零 Mod 依赖"约束，且对未知 Mod 仍然失效。

**实现实况：统一走 R2 传送回收，并按"是否正在工作"分级超时**

| 驻外状态 | 回收条件 | 常量 |
|---|---|---|
| 空闲（`IsIdleState` 为真） | 在目标图连续空闲超过 10 秒即视为"那边没活"，收回本池 | `AbroadIdleReclaimTicks = 600` |
| 正在执行真实工作 / 被容器接住 | 占用超过 25 秒视为卡住，强制收回 | `AbroadReclaimTicks = 1500` |

- **回程不受入口授权约束**（见 10.7）：否则代理会被困在别的图。
- 空闲宽限期是必需的：代理刚被搬到目标图时必然短暂空闲，若立即收回就永远无法跨图工作。

**R2 传送回收（零依赖，默认路径，必然成功）**

```csharp
/// 万能兜底：不依赖任何第三方 API，等价于"把代理收回归属图的休眠舱"。
private void ReclaimAbroad(Pawn pawn)
{
    StopIssuedJobFor(pawn);                            // 走 registry.ManagerFor（归属图组件）
    OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);   // 携带物落回当前图地面，不随代理消失
    ReleaseAllProjectedAreas(pawn);                    // 清理 allowedAreas 中所有图的残留
    PutProxyToSleep(registryRecordOf(pawn));           // 放宽后的版本：任意图均可送入 home 图休眠舱
    WakePumpNow();
}
```

- 不把 `SkipUtility.SkipTo` 作为默认手段（它会播放 Skip 特效与音效，且与现有 `PutProxyToSleep` / `WakeProxyAtStation` 语义重复），仅作为异常路径备选。
- 下次该池唤醒时，`WakeProxyAtStation` 会在**归属图**的工作站格 `GenSpawn.Spawn` 出来。

### 6.3 驻外看护（在**归属图**组件的 60 tick 维护里）

```csharp
private void MaintainAbroadProxies(int tick)
{
    var registry = GameComponent_OmniWorkProxyRegistry.Instance;
    List<Pawn> list = registry.ForeignScratch;      // 复用，避免分配
    registry.EnumerateForeign(map, list);           // 归属本图但不在本图；无 Mod 时恒空
    for (int i = 0; i < list.Count; i++)
    {
        Pawn pawn = list[i];
        if (pawn == null || pawn.Destroyed) { registry.Unregister(pawn); continue; }

        if (pawn.Spawned && pawn.MapHeld != null && !IsIdleState(pawn)) continue;   // 正在工作

        if (!pawn.Spawned || pawn.MapHeld == null)                                   // 容器内/失去地图
        {
            if (tick - home.abroadSinceTick >= AbroadReclaimTimeoutTicks /* 1500 */)
                ReclaimAbroad(pawn);
            continue;
        }
        if (TrySendHomeJob(pawn, out _)) continue;                                   // 走回（加速）
        if (tick - home.abroadSinceTick >= AbroadReclaimTimeoutTicks)
            ReclaimAbroad(pawn);                                                     // 传送回收
    }
}
```

**必须同时做**：驻外期间 `IsActive` 保持 `true`，否则 `Patch_OmniWorkProxy_FreezeWhenInactive`（`:2906-2917`）会跳过 `Pawn.Tick`，代理连回程 job 都跑不动。`Patch_OmniWorkProxy_DeactivateOnJobEnd` 在 `pawn.Map != homeMap` 时**不得**走 `NotifyProxyBecameIdle`。

### 6.4 强制销毁与重建（满足 R-3 / R-6）

**语义**：
- 在池 `P`（= `station.Map`）上执行重建 → 销毁**归属恰好为 `P`** 的全部代理，无论它们当前在哪张图、是否被容器持有、是否正在跑 job（R-3）。
- **其他池的代理一律不动** —— 包括此刻正驻留在 `P` 所在图上工作的别池代理（R-6）。

```csharp
/// 强删单个代理：可从任意图、任意容器、任意 Job 状态下执行。
public bool ForceDestroy(Pawn pawn)
{
    if (pawn == null) return false;
    if (pawn.Destroyed) { Unregister(pawn); return false; }
    StopIssuedJobFor(pawn);                                        // 归属组件：停 Job + 清 Active
    OmniWorkProxyUtility.Unassign(pawn);
    OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);               // best-effort：能落地就落地
    if (pawn.holdingOwner != null) pawn.holdingOwner.Remove(pawn); // 从容器/休眠舱取出
    if (pawn.Spawned) pawn.DeSpawn(DestroyMode.Vanish);            // 幂等
    Unregister(pawn);
    if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
    WorkFailuresBridge.PurgeAllMaps(pawn);                         // 清所有图的失败表条目
    return true;
}

/// 重建单池：只处理 homeMap == pool 的代理（R-6）。
public int ForceRecreatePool(Map pool)
{
    if (pool == null) return 0;
    int destroyed = 0;
    EnumeratePool(pool, scratch);               // ★ 以 homeMap 过滤，而不是"当前在本图的代理"
    for (int i = 0; i < scratch.Count; i++)
        if (ForceDestroy(scratch[i])) destroyed++;

    MapComponent_OmniWorkstation mgr = ManagerFor(pool);
    mgr?.ClearSleepingPool();                   // 兜底清空归属图休眠舱
    mgr?.EnsureProxyCount();                    // 分批补建（沿用现有逻辑）
    mgr?.WakePumpNow();
    return destroyed;
}

/// 重建所有池（R-7）。
public int ForceRecreateAllPools()
{
    int destroyed = 0;
    CollectPools(scratchPools);                 // 从 homes 汇总 distinct homeMap
    for (int i = 0; i < scratchPools.Count; i++) destroyed += ForceRecreatePool(scratchPools[i]);
    return destroyed;
}
```

要点：
1. **枚举来源必须是 registry 且按 `homeMap == pool` 过滤** —— 既不能是 `map.proxies` + `map.sleepingProxies`（现状 bug），也不能是"当前在本图的代理"（会误删别池的驻留代理）。
2. **先置 `rebuilding = true` 标记**，避免强删过程中 `EndCurrentJob` 补丁把代理重新纳入池或触发 `NotifyProxyBecameIdle`。
3. **容器内代理**：`pawn.holdingOwner.Remove(pawn)` 后再 `Destroy`（`PutProxyToSleep` 已有同类处理可参照）。
4. **携带物**：`ReleaseAllHeldThings` 只在 `pawn.Spawned` 时可落地；容器内/无图时按现有语义销毁（与 `PerformRecreateAllProxies` 的既有承诺一致：代理随身物品先落地）。
5. **失败表**：`WorkFailures` 是 **per-map** 的（`MapComponent_OmniWorkstation.WorkFailures`），驻外代理的失败记录写在**别的图**的表里 → 强删时必须跨图清理（`PurgeAllMaps(pawn)`，或把失败表改为按**归属图**持有）。
6. **地图销毁路径复用同一入口**：`MapComponent_OmniWorkstation.MapRemoved()` → `registry.NotifyHomeMapRemoved(map)` → 对归属本图者全部 `ForceDestroy`。

**池隔离反例（必须写成测试用例）**：

| 初始状态 | 操作 | 期望 |
|---|---|---|
| A 池代理 `a` 正驻留在 B 图工作；B 池代理 `b` 正驻留在 A 图工作 | 在 A 图点「重建本池」 | **只销毁 `a`**（尽管它在 B 图）；`b` 毫发无损（尽管它在 A 图） |

### 6.5 单代理操作（满足 R-8）

```csharp
/// 手动重建指定代理：销毁它并让所属池补建一个替代品。
public bool RecreateSingle(Pawn p)
{
    Map pool = HomeMapOf(p);
    if (!ForceDestroy(p)) return false;
    ManagerFor(pool)?.EnsureProxyCount();       // 池内补齐一个；编号由 RenumberProxies 重排
    ManagerFor(pool)?.WakePumpNow();
    return true;
}

/// 强制该代理停止工作并立即回收回**所属池**（不销毁）。
/// 用于手动修复卡住的代理：卡在寻路、卡在第三方 Job、卡在容器里。
public bool ReclaimNow(Pawn p)
{
    if (p == null || p.Destroyed) return false;
    if (!TryGetHome(p, out ProxyHome home)) return false;

    MapComponent_OmniWorkstation mgr = ManagerFor(home.homeMap);
    if (mgr == null) return false;

    mgr.StopIssuedJobFor(p);                       // 停 Job + 清 Active（走归属组件）
    OmniWorkProxyUtility.Unassign(p);
    if (p.holdingOwner != null && !IsOurSleepingHolder(p.holdingOwner))
    {
        OmniWorkProxyUtility.ReleaseAllHeldThings(p);   // 携带物落地
        p.holdingOwner.Remove(p);                       // 从第三方容器取出
    }
    ReleaseAllProjectedAreas(p);
    mgr.PutProxyToSleepFor(p);                     // 强制送入归属图休眠舱（放宽版）
    mgr.WakePumpNow();
    return true;
}
```

- 两者都**只登记意图、在 tick 阶段执行**（沿用现有"Gizmo 只登记、`MapComponentTick` 执行"的模式），避免在 `OnGUI` 改动地图集合。
- `ReclaimNow` 是"不销毁"的急救手段；`RecreateSingle` 是"换一个"的手段。界面上并列提供。

### 6.6 全局工作总开关（满足 R-7 的"控制所有工作站是否工作"）

```csharp
public void SetGlobalWorkEnabled(bool enabled)
{
    GlobalWorkEnabled = enabled;
    foreach (Map pool in Pools())                    // 所有池
    {
        MapComponent_OmniWorkstation mgr = ManagerFor(pool);
        if (!enabled) mgr?.ReclaimAllActive();       // 关闭：立即把场上代理收回休眠舱（不销毁）
        else          mgr?.WakeAllStations();        // 开启：唤醒所有站
    }
    Messages.Message("OmniWorkstation_GlobalWork" + (enabled ? "On" : "Off"), MessageTypeDefOf.NeutralEvent, false);
}
```

- 与 per-station 的 `Building_OmniWorkstation.automationEnabled` 是 **AND** 关系：全局是总闸，逐站开关仍在。
- 关闭时 `MapComponentTick` 的泵步直接跳过（在 `TryGetNextDueStation` 前判 `GlobalWorkEnabled`），**不销毁代理、不改配置**；`EnsureProxyCount` 仍维持配置数量。
- 状态随存档（`Scribe_Values`）。

### 6.7 意外丢失代理的判定与修复（满足 R-9）

三种"丢失"必须可区分，界面各给一个按钮：

| 情形 | 判定 | 修复动作 |
|---|---|---|
| **记录失效** | `pawn == null \|\| pawn.Destroyed` 但 registry 仍有记录 | `Unregister`；该池 `EnsureProxyCount` 自动补建 |
| **实体不可达** | `pawn` 未被销毁，但 `!Spawned` 且 `ParentHolder == null`，或 `MapHeld == null` 且 `ParentHolder == null` | 标记 `Lost` → 「清理并补建」：`ForceDestroy`（能引用到就彻底清）+ 池补建；若 `ParentHolder` 链完整则不算 Lost（属于容器内） |
| **池本身丢失** | `homeMap == null` 或已不在 `Find.Maps`（被 `DeinitAndRemoveMap`/`Dispose`） | 现策略：`NotifyHomeMapRemoved` 时已强删；若仍有残留记录 → `Unregister` + 丢弃代理 |

```csharp
/// 修复单池：清理失效/丢失记录，并把数量补到 configure 值（若该图有可用工作站）。
public int RepairPool(Map pool)
{
    int fixedCount = 0;
    EnumeratePool(pool, scratch);
    for (int i = 0; i < scratch.Count; i++)
    {
        Pawn p = scratch[i];
        if (p == null || p.Destroyed) { Unregister(p); fixedCount++; continue; }
        if (IsUnreachable(p))         { ForceDestroy(p); fixedCount++; continue; }
    }
    ManagerFor(pool)?.ClearSleepingPoolOfInvalid();   // 清休眠舱中的失效条目
    ManagerFor(pool)?.EnsureProxyCount();             // 补齐缺口（wanted - actual）
    return fixedCount;
}
```

- `EnsureProxyCount`（`:2079-2085`）本身就是"缺口补齐"，`RepairPool` 只是把"清失效记录"这一步显式化，使其可被玩家一键触发。
- 界面顶部显示各池的 `missing = configured - total`，非 0 时给出「修复」高亮。

### 6.8 观测层（三条信号 + 一条兜底）

| 信号 | 挂点 | 动作 |
|---|---|---|
| 出现在某图 | `Pawn.SpawnSetup` Postfix | `NotifySpawned(pawn, map)`：`map == homeMap` → 清 `abroadMap`；否则记 `abroadMap = map`、`abroadSinceTick = now` |
| 离开某图 | `Pawn.DeSpawn` Postfix | 若 `pawn.ParentHolder` 是容器 → `inContainer = true`；若是我方入舱 → 不动 |
| 被容器吞掉 | 看护循环读 `pawn.ParentHolder`（不额外挂钩子） | `inContainer = true`，等它重新出现 |
| **兜底** | 60 tick 维护读 `pawn.Map != homeMap` | 覆盖任何绕开事件的路径（含字段级瞬移） |

## 7. 全局代理管理组件与界面（满足 R-4 / R-7 / R-8 / R-9 / R-10）

### 7.1 能力矩阵

| 能力 | 接口 | 说明 |
|---|---|---|
| 归属查询 | `TryGetHome` / `HomeMapOf` / `EnumeratePool(pool)` | 代理 ↔ 池 双向查询 |
| 全量枚举 | `EnumerateAll` / `EnumerateForeign` | 跨所有图，不依赖任何 `Map` 上下文 |
| 统计 | `StatsOf(pool)` / `TotalCount` | 总数 / 活跃 / 休眠 / 驻外 / 容器内 / 缺失 |
| 单代理强删 | `ForceDestroy(pawn)` | 任意图、任意容器、任意 Job 状态 |
| **单代理重建** | `RecreateSingle(pawn)` | R-8：销毁并补建一个替代品 |
| **单代理回收** | `ReclaimNow(pawn)` | R-8：停工作 + 立即回**所属池**（不销毁） |
| 单池重建 | `ForceRecreatePool(pool)` | R-3/R-6：仅归属本池者被强删 |
| **全池重建** | `ForceRecreateAllPools()` | R-7 |
| **池修复** | `RepairPool(pool)` / `RepairAllPools()` | R-9：清失效记录 + 补齐缺口 |
| **全局总开关** | `GlobalWorkEnabled` / `SetGlobalWorkEnabled` | R-7 |
| **入口授权** | `IsEntryAuthorized(from, to)` / `RefreshEntryCache()` | R-10：第 10 章 |
| 地图销毁 | `NotifyHomeMapRemoved(pool)` | 归属该图者全部强删 |
| **视图数据源** | `BuildOverview(pools, rows)` | 供管理界面一次性快照 |

### 7.2 实例获取与读档时序

```csharp
// 统一入口（构造期即可用）
GameComponent_OmniWorkProxyRegistry reg =
    GameComponent_OmniWorkProxyRegistry.Instance               // 构造函数/ExposeData 赋值
    ?? Current.Game.GetComponent<GameComponent_OmniWorkProxyRegistry>();
```

**约束**：`Instance` 不可在 `FinalizeInit` 才赋值（`SpawnSetup` 早于它），见 `GlobalStorageDesign.md:115` 的既有结论。

### 7.3 管理界面（R-7 / R-8 / R-9 / R-10）

**新窗口**：`Window_OmniWorkstationManager`（跨图视图）。现有 `Window_OmniWorkstationMonitor`（`OmniWorkstationMonitor.cs:115`，`Find.CurrentMap`）与 `Dialog_OmniWorkstationStatus`（`OmniWorkstation.cs:761`）保留为**单池视图**。

**入口（按钮）**：

| 入口 | 位置 | 说明 |
|---|---|---|
| **「代理与工作站总览」按钮** | `OmniCrafterMod.DoSettingsWindowContents`（`OmniCrafterMod.cs:200`） | **首选**：无需新增 Def，改动最小 |
| 次级入口 | 工作站 Gizmo 中的「查看全局管理」 | 方便游戏内直接打开 |
| 可选入口 | 主按钮栏（`MainButtonDef`） | 项目当前**没有** `MainButtonDef`，需新增 XML Def + `MainButtonWorker`（可参考 `RVAutoHome` 的 `MainButtonWorker_Flow` / `MainTabWindow_RVFlow`）——列为可选增强 |

**界面布局**（三分区，均可滚动）：

```
┌─ 全局总开关：[✔ 所有工作站工作]   [强制重建所有代理]  [修复所有池]  [刷新]  [输出日志] ─┐
├─ 工作站与地图池（按图一行）───────────────────────────────────────────────────────┤
│  图名/层 | 工作站数 | 可用 | 配置 | 总数 | 活跃 | 休眠 | 驻外 | 容器 | 缺失 | 可去地图 | 未覆盖入口 | 开关 | 重建 | 修复 │
│  ▸ 主地图       2    ✔    8     8     5     3     0     0    0   主地图/二层    1        [✔]  [重建] [修复] │
│  ▸ 二层(pocket) 1    ✔    4     4     1     3     1     0    0   二层          0        [✔]  [重建] [修复] │
├─ 代理列表（全部代理）─────────────────────────────────────────────────────────────┤
│  名称 | 归属池 | 当前位置 | 状态 | 当前工作 | 操作                                 │
│  Worker 0 | 主地图 | 主地图 (102,45)      | 工作中@归属 | 搬运钢铁 → 仓库 | [重建] [停止并回收] │
│  Worker 1 | 主地图 | **二层 (88,12)**     | 工作中@驻外 | 缝制衣物          | [重建] [停止并回收] │
│  Worker 2 | 二层   | 二层·休眠舱           | 休眠      | —                 | [重建] [停止并回收] │
│  Worker 3 | 主地图 | **车辆 X 内部**       | 容器内    | —                 | [重建] [停止并回收] │
│  Worker 4 | 主地图 | —                     | **丢失**  | —                 | [清理并补建]        │
└──────────────────────────────────────────────────────────────────────────┘
```

**每列的取值来源**：

| 列 | 取值 |
|---|---|
| 名称 | `pawn.Name?.ToStringShort`（由 `RenumberProxies` 维持 `Worker N` 编号） |
| **归属池** | `homeMap` → 标签：`map.Parent?.Label` 或 `PocketMapParent.sourceMap`/`Map.Tile` 推导的"层"描述，退化用 `map.uniqueID` |
| **当前位置** | `pawn.Spawned` → `map` + `Position`；`inContainer` → `pawn.ParentHolder` 的 `Label`/类型名；休眠 → "归属池·休眠舱"；`Lost` → "—" |
| 状态 | `ProxyState`（见 5.1） |
| 当前工作 | `SafeJobReport(pawn, pawn.CurJob)`（已存在，`:1949`） |
| 卡住标记 | `IsStalledJob`（`:2347`）为真 或 `abroadSinceTick` 超时 → 高亮（供玩家判断该不该按「停止并回收」） |
| **可去地图** | 该池通过入口授权可达的图列表（10.6）——便于玩家发现"我把楼梯建在范围外了" |
| **未覆盖入口** | 该图上通往其它图、但**未被本池工作站范围覆盖**的入口数量（10.6） |

**性能要求（千级代理）**：

1. **不要每帧重建列表**：打开时调用一次 `BuildOverview`；此后按 `每 60 tick` 或「刷新」按钮重建。
2. **只绘制可视行**：`int firstRow = Mathf.FloorToInt(scroll.y / RowHeight);` + `int visible = Mathf.CeilToInt(viewHeight / RowHeight);`，循环 `firstRow..firstRow+visible`。项目已有 `Widgets.BeginScrollView` 的先例（`OmniWorkstationMonitor`）。
3. **不在 `OnGUI` 里执行破坏性操作**：按钮只设置 `pendingAction`，由 `MapComponentTick`/`GameComponentTick` 统一执行。
4. 不引入 Linq；`BuildOverview` 复用 registry 的 `scratch` 列表。

### 7.4 全局开关与跨图开关的关系

| 开关 | 层级 | 默认 | 作用 |
|---|---|---|---|
| `GlobalWorkEnabled` | 全局（R-7） | true | 总闸：所有池停止/恢复派发；关闭时回收场上代理进休眠舱（不销毁） |
| `Building_OmniWorkstation.automationEnabled` | 工作站 | true | 单站启用/禁用（现有） |
| `Settings.AllowCrossMapWork` | 全局设置（R-1） | true | 是否允许代理在非归属图工作（第 9 章 L1/L2/L3） |
| **`Settings.EntryAuthorizationMode`** | 全局设置（R-10） | **严格** | 严格：未授权即拒绝；仅警告：只提示不阻止 |

四者是 AND 关系；界面同时暴露前两者与 `missing` / `可去地图` / `未覆盖入口`。

## 8. 口袋地图支持（满足 R-5）

### 8.1 已具备的原版基础（已确证，无需任何新代码）

| 环节 | 结论 | 依据 |
|---|---|---|
| 地图组件 | `MapComponent_OmniWorkstation` **自动出现在每张图上，含 pocket map** | `Verse/Map.cs:515-533` `FillComponents()` 遍历 `typeof(MapComponent).AllSubclassesNonAbstract()`（跳过 `CustomMapComponent` 子类）并 `Activator.CreateInstance(type, this)` |
| 寻路网格 | `FAOC_OmniWorkProxyPathGrid` 在每张图上有效 | `Verse/AI/Pathing.cs:20-38` 构造函数为 `DefDatabase<PathGridDef>.AllDefsListForReading` 逐个建实例 |
| 存档 | 口袋图的池（含 `sleepingProxies`）随存档保存 | `Verse/Map.cs:687` `Scribe_Collections.Look(ref components, "components", LookMode.Deep, this)` |
| 区域 | `Area_OmniWorkstation` 挂各图自己的 `areaManager` | — |
| 地图销毁 | 走 `MapComponentUtility.MapRemoved(map)` | `Verse/Game.cs:585-624` |

**结论：`MapComponent_OmniWorkstation` 从不假设"主地图"，它本就是 per-map 的。** 因此"工作站运行在口袋地图"在架构层面已经成立，剩下的是**逐项消除"以本图为中心"的隐含假设**。

### 8.2 需要消除的"本图中心"假设清单

| # | 位置 | 现状 | 改为 | 影响 |
|---|---|---|---|---|
| 1 | `OmniWorkFailureCache.For(pawn)`（`:201-202`）、`Patch_OmniWorkProxyNavigation.cs:44,191` | 按 `pawn.Map` 取 `WorkFailures` | 按 **归属图** 取（`registry.ManagerFor(pawn).WorkFailures`） | 跨图工作时失败隔离写错了图 |
| 2 | `Patch_..._DeactivateOnJobEnd` `:2934`、`Patch_..._TrackVanillaJobStart` `:2981` | `___pawn.Map.GetComponent<...>()` | `registry.ManagerFor(pawn)` | 步骤 6/7 的失控根源 |
| 3 | `Patch_OmniWorkstation_WakeOnDesignation` `:3064-3077` | 只唤醒**本图**工作站 | 同时唤醒 `abroadMap == 本图` 的驻外代理的**归属站** | 否则跨图工作的响应延迟最多一个退避周期（120 tick） |
| 4 | `EnsureProxyCount` `:2072-2077` | `wanted = 本图 operational 工作站数 > 0 ? configured : 0` | 保持（语义正确：池由本图是否有可用工作站决定）；再叠加 `GlobalWorkEnabled` | 口袋图的池正常创建 |
| 5 | `RecoverExistingThings` `:1986-1994` | `map.listerBuildings.allBuildingsColonist` 找工作站 | 保持（口袋图上属于 `Faction.OfPlayer` 的建筑会被收录） | **前提**：口袋地图上的工作站必须属于玩家阵营 |
| 6 | `RecoverExistingThings` `:1997-2005` | 用本图 `listerThings[Pawn]` 收养并 `PutProxyToSleep` | **收养前查 registry**：归属不是本图 → 跳过 | 否则本图会**抢走**别的池的代理（含口袋图之间互抢） |
| 7 | `RemoveWorkArea` `:1704-1711` | `PawnsFinder.All_AliveOrDead` + 本图池 | 保持（已是跨图） | OK |
| 8 | `OmniWorkstationMonitor.Draw` | `Find.CurrentMap` | 保持；全局视图见 7.3 | 可接受 |
| 9 | 殖民者栏 / 工作设置栏 | 代理已 `DeRegisterPawn`，不进 `mapPawns` | 保持 | 口袋图场景同样正确 |
| 10 | **超维存储取送料**（`OuterrealmStorage/*`） | 待核查：候选集是否限定"代理所在图" | 实施期核查 | 列为**实施期核查项**，不作为本方案前提 |

### 8.3 地图未加载 / 被移除的边界

- `Game.DeinitAndRemoveMap(map, true)`（`Game.cs:585-624`）会把图从 `Find.Maps` 移除 → 该图不再 tick → **该图上的池停止工作**。这是原版固有行为，不做对抗。
- 级联：`sourceMap == 被删地图 && pocketMapProperties.destroyOnParentMapAbandoned` 的子口袋图会被一并销毁（例如 MultiFloors 楼层随地面图消失）。
- **策略**：地图被移除 = 该池与该池全部代理一并销毁（`NotifyHomeMapRemoved` → `ForceDestroy`），不会留下无主代理；界面上的该池条目随之消失。
- 若某 Mod 只是"暂停"地图（`MapParent.HasMap = false`）而非销毁：**列实施期核查项**（该状态下图是否仍在 `Find.Maps` 决定它是否 tick）。

## 9. 查询图感知与跨图工作开关

### 9.1 工作查询发生在哪张图 —— 可精确获知

原版 `JobGiver_Work.TryIssueJobPackage`（`RimWorld/JobGiver_Work.cs:43-208`，`public override`）全程以 `pawn.Map` / `pawn.Position` 为上下文：`pawn.Map.listerThings.ThingsMatching(...)`、`GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ...)`、`GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map, ...)`、`new TargetInfo(c, pawn.Map)`。MultiFloors 正是靠临时改写 `pawn.Map` 让原版与所有第三方 WorkGiver 在目标图上下文工作。

| 途径 | 位置 | 得到的信息 |
|---|---|---|
| ① 总入口（推荐） | `JobGiver_Work.TryIssueJobPackage` 的 Prefix | 本次工作查询的图（读 `pawn.Map`），覆盖 `NonScanJob` / `scanThings` / `scanCells` |
| ② 单次搜索 | `GenClosest.ClosestThingReachable(IntVec3 root, **Map map**, ...)` 的 Prefix（**已 patch**：`Patch_OmniWorkProxy_ForceGlobalSearch`） | 搜索上下文图，也是 `map.listerThings` 的来源 |
| ③ 全局可达搜索 | `GenClosest.ClosestThing_Global_Reachable(IntVec3 root, **Map map**, ...)` 的 Prefix（**已 patch**：`Patch_OmniWorkProxy_GlobalSearchFilter`） | 同上（validator 回调已在此处） |

**嵌套重跑天然可识别**：MultiFloors 在 Postfix 里 `UnsafeTeleportTo(B 图)` 后**递归调用同一个方法** → 我们的 Prefix 再次进入，此时 `pawn.Map == B 图`。故"每次进入 Prefix 重新读 `pawn.Map`"即正确实现：无静态状态、跨帧无残留、天然同步。

### 9.2 跨图工作开关（三层）

**L1 通用软拦截**：`JobGiver_Work.TryIssueJobPackage` Prefix

```csharp
[HarmonyPatch(typeof(JobGiver_Work), "TryIssueJobPackage")]
public static class Patch_OmniWorkProxy_BlockForeignMapWork
{
    [HarmonyPrefix]
    public static bool Prefix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
    {
        if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
        if (Settings.AllowCrossMapWork) return true;                    // 开关，默认 true
        if (__instance.emergency) return true;                          // 紧急工作（灭火等）可选放行
        if (GameComponent_OmniWorkProxyRegistry.HomeMapOf(pawn) == pawn.Map) return true;
        __result = ThinkResult.NoJob;                                   // 非归属图 → 拒绝本次查询
        return false;
    }
}
```

**L2 可选加速**：`pawn.StayOnCurrentMap() = true`（MultiFloors 官方开关，其扫描条件含 `!pawn.StayOnCurrentMap()`）→ 避免 L1 下"每次都把所有楼层扫一遍才发现拿不到结果"的浪费。**软依赖，缺失时静默跳过。**

**L3 目标级**：在 `Patch_OmniWorkProxy_GlobalSearchFilter` 的 validator 里追加图限制。

**推荐组合**：开关开启（默认）→ 不启用；开关关闭 → **L1 + L2**。

> **L1 与第 10 章的关系**：L1 是"总闸/全局开关"，第 10 章的入口授权是"逐目标图的细化"。两者是 AND：L1 放行后仍要过入口授权。

### 9.3 非工作入口的跨图（通用拦截）

`JobGiver_Work` 之外的跨图入口（SimplePortal 的 `JobGiver_EnterSimplePortal`、RVAutoHome 的 `HomeDecision`、RV 上下车，以及未知 Mod 的自定义入口）在 `Patch_OmniWorkProxy_TrackVanillaJobStart`（`Pawn_JobTracker.StartJob`，已存在）统一拦截：

```csharp
// 判据 A（通用）：job 声明的目标图不是归属图
if (newJob.globalTarget.IsValid && newJob.globalTarget.Map != homeMap) { /* 按开关取消 */ }
// 判据 B（通用）：job 的主要目标 Thing 不在归属图
if (newJob.targetA.Thing?.MapHeld is Map m && m != homeMap) { /* 按开关取消 */ }
```

判据 A 直接命中 MultiFloors 的全部跨层 job（都用 `globalTarget` 承载目标图），对未知 Mod 同样有效。**这里的"取消"同样要叠加入口授权**（10.7）。

## 10. 入口授权（跨图准入，满足 R-10）

> 需求原文：**"如果地图上存在一个到其他地图的入口，但是没有工作站的范围覆盖了这个入口，那么代理就不应该去这个入口对应的地图。"**

### 10.1 规则（逐段授权，严格）

> **授权式**：池 `P` 的代理从图 `X` 进入图 `Y`，当且仅当 **`X` 图上存在入口 `E`**，满足：
> 1. `E` 通往 `Y`（`SafeGetTargetMap(E) == Y`）；且
> 2. 存在工作站 `W ∈ P`，使 `W.CoversOn(X, E.Position)` 为真。
>
> 若不存在满足 1+2 的 `E`，则 **`Y` 对 `P` 不可达**。

**多段换图逐段判定**（已拍板：严格）：`A→C→B` 中
- `A→C` 看 **A 图**上入口是否被覆盖；
- `C→B` 看 **C 图**上入口是否被覆盖（用 `W.CoversOn(C, E.Position)`，即同坐标投影）；
- 任一段不通过 → `B` 不可达。

> 代价：深层图（远离工作站所在楼层的同坐标区域）通常判不通过。这是严格语义的必然结果，缓解手段见 10.6（界面提示"未覆盖入口"与"可去地图"，让玩家自行扩大半径或调整工作站位置）。

### 10.2 判定时机 —— 正好利用 MultiFloors 的「内层重跑」

MultiFloors 评估"要不要去 B 图"时，是先 `UnsafeTeleportTo(B 图)` 再**递归调用同一个 `TryIssueJobPackage`**，因此那一刻 `pawn.Map` **就是 B 图**。于是把授权判定插在 `JobGiver_Work` 的 Prefix 最前面即可：

```csharp
[HarmonyPatch(typeof(JobGiver_Work), "TryIssueJobPackage")]
public static class Patch_OmniWorkProxy_MapEntryAuthorization
{
    [HarmonyPrefix]
    public static bool Prefix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
    {
        if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;

        Map queryMap = pawn.Map;                                  // 外层 = A 图；MultiFloors 内层 = B 图
        Map homeMap  = GameComponent_OmniWorkProxyRegistry.HomeMapOf(pawn);
        if (queryMap == null || homeMap == null || queryMap == homeMap) return true;

        GameComponent_OmniWorkProxyRegistry reg = GameComponent_OmniWorkProxyRegistry.Get();
        if (reg == null) return true;

        if (!reg.IsEntryAuthorized(homeMap, queryMap))            // ★ O(1) 查缓存表（10.5）
        {
            if (Settings.EntryAuthorizationMode == EntryAuthMode.Strict)
            {
                __result = ThinkResult.NoJob;                      // 拒绝 → MultiFloors 拿不到换层 job
                return false;
            }
            reg.NotifyUnauthorizedAttempt(homeMap, queryMap, pawn); // 仅警告模式：不阻止，只记录
        }
        return true;
    }
}
```

**要点**：这条规则**不需要识别 Mod 身份**，也不需要读 job 的 `globalTarget` —— 任何"临时改写 `pawn.Map` 再重跑工作查询"的 Mod（当前是 MultiFloors；未来出现同类机制的任何 Mod）都会被自动约束（符合第 3 章 C2）。

### 10.3 入口与目标图的通用求法（已逐条确证）

| 环节 | 结论 | 依据 |
|---|---|---|
| 枚举入口 | `map.listerThings.ThingsInGroup(ThingRequestGroup.MapPortal)` | `ThingRequestGroup.MapPortal` 是**原版枚举成员**（`Verse/ThingRequestGroupUtility.cs:170`）；RVwithPD 正是用该组枚举 `MapPortal` |
| MultiFloors | `StairEntrance : Stair : MapPortal`、`StairExit : Stair`、`Elevator : Stair` | `MultiFloors/Stair.cs:13`、`StairEntrance.cs:12`、`StairExit.cs:11`、`Elevator.cs:14` |
| SimplePortal | `SimplePortal_Building : MapPortal`，`GetOtherMap()` = `linkedPortal.MapHeld` | `SimplePortalLib/SimplePortal_Building.cs:12,251-266` |
| 原版 | `PocketMapExit : MapPortal`，与入口**双向链接**（`MapPortal.exit` ↔ `PocketMapExit.entrance`） | `RimWorld/PocketMapExit.cs:16,29-33` |
| 覆盖判定 | `W.CoversOn(X, E.Position)` | 5.3；同图时即原有 `Covers` |

**求目标图必须防御性实现**（已查出两个坑：原版 `GetOtherMap()` 会惰性生成 pocket map；`Stair.GetOtherMap()`（`Stair.cs:140-143`）抛 `NotImplementedException`）：

```csharp
/// <summary>
/// 安全求"入口通往的目标图"：不触发 pocket map 惰性生成，不容忍任何异常。
/// </summary>
internal static Map SafeGetTargetMap(MapPortal portal)
{
    if (portal == null) return null;
    try
    {
        if (portal is PocketMapExit exit) return exit.entrance?.Map;    // ① 出口反向（无副作用）
        if (portal.exit != null)          return portal.exit.MapHeld;   // ② 原版正向链接（无副作用）
        if (portal.PocketMapExists)       return portal.PocketMap;      // ③ 已生成：只读属性，不触发生成
        if (portal.GetType() != typeof(MapPortal))
            return portal.GetOtherMap();                                // ④ 第三方 override（Stair 子类 / SimplePortal）
        return null;                                                    // 原版且未链接：绝不调用（避免生成）
    }
    catch (Exception e)
    {
        Log.WarningOnce("[OmniWorkstation] portal target lookup failed: " + e, portal.thingIDNumber);
        return null;
    }
}
```

第 ④ 步的 `GetType() != typeof(MapPortal)` 是关键：第三方子类都 override 了且实现安全，而**精确的原版 `MapPortal`** 若尚未生成 pocket map 就不能碰。

### 10.4 非 portal 型跨图：载体授权（已拍板）

RV 上下车、gravship 搬运**没有入口建筑**可供范围判定。已定策略：**按"载体（车辆）位置是否落在工作站范围内"判定**。

```csharp
/// 求"目标图 T 所对应的载体（车辆）"，取不到返回 null。
internal static Thing ResolveCarrier(Map target)
{
    if (target == null) return null;
    // 软依赖 RVwithPD：内部空间 pocket map 上挂着 InteriorSpaceMapComponent，
    // 其 ownerThing 就是车辆（已从反编译确证 RVwithPD/InteriorSpaceMapComponent.cs）。
    if (RvWithPdCompat.TryGetCarrier(target, out Thing vehicle) && vehicle != null) return vehicle;
    return null;   // 其余情形由 10.1 的 portal 规则覆盖，或保守未授权
}

/// 载体授权：车辆当前所在位置必须落在本池某个工作站范围内。
internal static bool IsCarrierAuthorized(Map homeMap, Thing carrier)
{
    if (carrier == null) return false;                       // 取不到载体 → 保守拒绝
    Map carrierMap = carrier.MapHeld;
    if (carrierMap == null) return false;                    // 车辆不在任何图上（如 gravship 在世界地图）
    for (int i = 0; i < StationsOf(homeMap).Count; i++)
    {
        Building_OmniWorkstation w = StationsOf(homeMap)[i];
        if (w.Operational && w.CoversOn(carrierMap, carrier.PositionHeld)) return true;
    }
    return false;
}
```

判定并入 10.1：对目标图 `Y`，若 `Y` 通过 **portal** 可达 → 走 10.1；否则若 `Y` 能解析出**载体** → 要求 `IsCarrierAuthorized`；两者都不成立 → **未授权**。

**退化与例外**：

| 情形 | 处理 |
|---|---|
| RVwithPD 内部空间 | 车辆位置判定（`ownerThing.MapHeld` + `PositionHeld`） |
| gravship（`Map.Parent` 是 `WorldObject`，不在任何 `Map` 上） | 载体 `MapHeld == null` → 保守未授权；**列实施期核查项**（若要放行，需按星球 `Tile` 比较或另给开关） |
| 未知 Mod 的自定义载体（不是 `MapPortal`，也没有 `ownerThing`） | 保守未授权；玩家可用 9.3 的 `StartJob` 判据或 `AllowCrossMapWork` 总开关注销（**记入日志，便于诊断**） |

### 10.5 缓存与性能（本机制唯一的实现难点）

`JobGiver_Work.TryIssueJobPackage` 是**极高频**入口，且 MultiFloors 会让它**每次思考重入 N 次（N = 候选层数）**。因此：

```csharp
// 授权表：from → to → allowed。IsEntryAuthorized 只做两级字典查，不枚举 listerThings。
private readonly Dictionary<Map, Dictionary<Map, bool>> entryAuth =
    new Dictionary<Map, Dictionary<Map, bool>>();
```

- **刷新时机**：归属图组件的 **60 tick** 维护周期内 `RefreshEntryCache()` 全量重算（入口数量少，成本可忽略）。
- **立即失效**：`InvalidateEntryCache(from)` 由这些事件触发 ——
  - `Building_OmniWorkstation.SpawnSetup` / `DeSpawn` / `NotifyConfigurationChanged`（半径、启用状态变化）；
  - 入口建筑的 `SpawnSetup` / `DeSpawn`（`ThingRequestGroup.MapPortal` 增删）；
  - 地图增删（`Find.Maps` 变化）。
- 前缀只做一次字典查（O(1)）；**未命中按"未授权"处理**（保守，且与"缓存尚未建立"的初始状态一致）。
- 复用 `scratch` 列表，不引入 Linq（符合 AGENTS.md）。

### 10.6 界面与可发现性

| 展示项 | 位置 | 价值 |
|---|---|---|
| **可去地图** | 池行 | 该池经入口/载体授权可达的图列表 |
| **未覆盖入口** | 池行 | 本图上"通往其它图但未被本池任何工作站覆盖"的入口数量；**非 0 时高亮** |
| 入口清单（可选） | 池详情 | 逐个列出入口（标签/位置/目标图/是否被覆盖/覆盖它的工作站） |
| 地图高亮（可选） | 地图绘制 | 在 `Covers` 命中的入口处绘制标记 |
| 模式开关 | 全局设置 | `EntryAuthorizationMode`：**严格**（默认） / **仅警告**（只提示不阻止） |

### 10.7 边界与不变量

1. **只约束"去"，不约束"回"**：回程永远保留 R2 传送回收兜底（不依赖入口），否则代理会被困在别的图里。
2. **同图不相连区域同样受约束**：MultiFloors 的 `CurrentMapHasDisjointArea` 会让代理经楼梯去同图另一侧 —— 同一语义（入口未被覆盖 → 不去）。
3. **紧急工作**：`JobGiver_Work.emergency`（灭火等）默认**同样受授权约束**；如需放行，在 Prefix 中判 `__instance.emergency` 提前返回（与 L1 的处理一致，作为可选项）。
4. **四开关 AND**：`GlobalWorkEnabled` ∧ `automationEnabled` ∧ `AllowCrossMapWork` ∧ `IsEntryAuthorized`。
5. **授权失败不是错误**：代理只是拿不到该目标图的工作，会继续在归属图搜索或按退避重试，不会卡死；若它**已经在**未授权图（例如入口被玩家拆了），驻外看护仍会把它传送收回。

## 11. 代码改动清单

### 11.1 修改现有成员

| 位置 | 现状 | 改为 |
|---|---|---|
| `TryGetStation` `:1015` | `station.Spawned && station.Map == pawn.Map` 否则 `Unassign` | 归属判定改为 `station.Spawned && station.Map == registry.homeMap`；跨图**不解绑** |
| `Unassign` `:971` | 只清"当前图"的活动区 | 调 `ReleaseAllProjectedAreas(pawn)`，清理 `allowedAreas` 中**所有图**的 `Area_OmniWorkStation` |
| `EnsureProxyCount` `:2062-2070` | `pawn.Map != map` → `Unassign + RemoveAt` | 保留记录；`abroadMap` 非空 → 交驻外看护；仅 `registry.homeMap != map` 才移出本地视图 |
| `RecoverExistingThings` `:1997-2005` | 用本图 `listerThings[Pawn]` 收养 | **收养前查 registry**，归属非本图 → 跳过（防止抢别的池的代理） |
| `RefreshProxyState` `:2238` | `pawn.Map != map → false` | 跨图分支：只做"是否空闲 / 是否失去地图"判定 |
| `PutProxyToSleep` `:2439` | `if (pawn.Map != map) return;` | 允许任意图回收：`DeSpawn(Vanish)`（幂等）后 `TryAdd` 到 **homeMap** 的 `sleepingProxies` |
| **`PerformRecreateAllProxies` `:2127`** | 只遍历本图 `proxies` + 本图 `sleepingProxies` | **改为 `registry.ForceRecreatePool(map)`**（R-3 + R-6）：只强删 `homeMap == map` 的代理，含驻外与容器内 |
| `MapRemoved` `:1721` | 只销毁 `pawn.Map == map` 的代理 | 改为 `registry.NotifyHomeMapRemoved(map)`：归属本图者**全部强删**（含驻外）；并 `InvalidateEntryCache` |
| `MapComponentTick` `:1743` / `TryGetNextDueStation` `:2506` | 无全局开关 | 前面插入 `if (!registry.GlobalWorkEnabled) { ReclaimAllActive(); return; }`（R-7）；维护周期内调 `RefreshEntryCache()`（R-10） |
| `Patch_..._DeactivateOnJobEnd` `:2934` | `pawn.Map.GetComponent<...>()` | `registry.ManagerFor(pawn)`；跨图时不走 `NotifyProxyBecameIdle` |
| `Patch_..._TrackVanillaJobStart` `:2981` | 同上 | 同上；追加 9.3 的拦截判据 + 10.1 的入口/载体授权 |
| `Patch_OmniWorkProxy_FreezeWhenInactive` `:2910-2915` | 非活跃 + 空闲 → 跳过 Tick | 增加"驻外代理一律放行"分支 |
| `WakeProxyForVanillaSearch` / `StartSearchWait` `:2564-2565, 2610-2611` | `AreaRestrictionInPawnCurrentMap = runtime.workArea` | 按 `station.Map` 设置；跨图后按当前图重设 |
| `Patch_OmniWorkProxy_GlobalSearchFilter` `:3039` | `station.Covers(thing.PositionHeld)` | `station.CoversOn(pawn.Map, thing.PositionHeld)` |
| `OmniWorkFailureCache.For(pawn)` `:201`、`Patch_OmniWorkProxyNavigation.cs:44,191` | 按 `pawn.Map` 取失败表 | 按**归属图**取（`registry.ManagerFor(pawn).WorkFailures`） |
| `Patch_OmniWorkstation_WakeOnDesignation` `:3064-3077` | 只唤醒本图工作站 | 追加：唤醒 `abroadMap == 本图` 的代理的归属站 |
| `Building_OmniWorkstation.SpawnSetup/DeSpawn/NotifyConfigurationChanged` `:71,79,153` | 只更新本图调度器 | 追加 `registry.InvalidateEntryCache(Map)`（R-10） |
| `OmniCrafterMod.DoSettingsWindowContents` `:200` | 仅现有设置项 | 追加「代理与工作站总览」按钮（R-7）与 `EntryAuthorizationMode` 开关（R-10） |
| Gizmo「重建工作代理」文案 | "销毁本张地图上的全部工作代理…" | 改为"**只**销毁本图池的代理（含正在其他地图上工作的）；其他地图的池不受影响" |
| 静态 `Assignments`（`Dictionary`） | 主线程写、寻路线程读 | `ConcurrentDictionary`，或按图分片 + 主线程快照 |

### 11.2 新增类型与 API

```csharp
// 新增
GameComponent_OmniWorkProxyRegistry      // 全局管理组件（5.1）
Hediff_OmniWorkProxyHome : HediffWithComps // 归属镜像（5.2，必须 isBad=false）
OmniWorkProxyEntryAuthorization          // 入口授权：缓存表 + SafeGetTargetMap + 载体判定（第 10 章）
Window_OmniWorkstationManager : Window   // 全局管理界面（7.3）
PoolOverview / ProxyRow / PoolStats      // 视图数据模型（7.3）
RvWithPdCompat                           // 软依赖：InteriorSpaceMapComponent.ownerThing（10.4）

// Building_OmniWorkstation
public bool CoversOn(Map pawnMap, IntVec3 cell);
public void SetAutomationEnabled(bool value);          // 供界面逐站开关

// MapComponent_OmniWorkstation
private void MaintainAbroadProxies(int tick);
private void ReclaimAbroad(Pawn pawn);
private bool TrySendHomeJob(Pawn pawn, out Job job);
internal void ClearSleepingPool();                     // 供 ForceRecreatePool 兜底
internal void ClearSleepingPoolOfInvalid();            // 供 RepairPool
internal void EnsureProxyCount();                      // private → internal（供 registry 调用）
internal void WakePumpNow();                           // 同上
internal void WakeAllStations();                       // 同上（全局开关）
internal void ReclaimAllActive();                      // 同上（全局开关关闭时回收）
internal void StopIssuedJobFor(Pawn p);                // 同上（R-8）
internal void PutProxyToSleepFor(Pawn p);              // 同上（R-8，放宽版）
private static void ReleaseAllProjectedAreas(Pawn pawn);
private Area GetOrCreateProjectionArea(Map targetMap, Building_OmniWorkstation station);
```

`allowedAreas` 是私有字段，用 `AccessTools.FieldRef`（**静态构造期缓存**）读取；或调用原版 `Notify_AreaRemoved(area)`（`Pawn_PlayerSettings.cs:256-259` 已具备该能力）。

### 11.3 新增补丁

| 补丁 | 用途 | Mod 依赖 |
|---|---|---|
| `Pawn.SpawnSetup` Postfix | `registry.NotifySpawned`（捕获真实换图） | 无 |
| `Pawn.DeSpawn` Postfix | `registry.NotifyDespawned`（区分入舱/被搬走） | 无 |
| `JobGiver_Work.TryIssueJobPackage` Prefix | 查询图感知 + L1 开关 + **入口授权**（9.2 / 10.2） | 无 |
| `MapPortal.SpawnSetup` / `Thing.DeSpawn` 轻量分支 | `registry.InvalidateEntryCache(map)`（入口增删） | 无 |

### 11.4 新增/更新的语言条目（`Languages/*/Keyed/OmniWorkstation.xml`）

`OmniWorkstation_GlobalManage`（总览按钮）、`OmniWorkstation_GlobalWorkOn/Off`、`OmniWorkstation_RecreateAllPools`、`OmniWorkstation_RepairPools`、`OmniWorkstation_RecreateProxy`、`OmniWorkstation_ReclaimProxy`、`OmniWorkstation_RepairLostProxy`、`OmniWorkstation_ProxyHomeColumn`、`OmniWorkstation_ProxyCurrentColumn`、`OmniWorkstation_ProxyState_*`、**`OmniWorkstation_ReachableMaps`（可去地图）**、**`OmniWorkstation_UncoveredEntries`（未覆盖入口）**、**`OmniWorkstation_EntryAuthStrict/Warn`** 等，同时更新 `OmniWorkstation_RecreateProxiesDesc` 的措辞以体现池隔离。

### 11.5 可选加速器（全部可缺失）

| 能力 | 软依赖对象 | 缺失时 |
|---|---|---|
| 回程 job（楼层） | `MultiFloors.Jobs.CrossLevelJobFactory.MakeChangeLevelThroughStairJob` | 走 R2 传送回收 |
| 回程 job（传送门） | `SimplePortalLib.LoaderToil.JobGiver_EnterSimplePortal` / `SimplePortalDefOf.EnterSimplePortal` | 走 R2 |
| 禁止跨图（省开销） | MultiFloors 的 `pawn.StayOnCurrentMap()` | 仅靠 L1 + 入口授权 |
| **载体识别（RV）** | `RVwithPD.InteriorSpaceMapComponent.ownerThing` | 退化为"保守未授权"（10.4） |
| 池/图层标签 | MultiFloors 的 `MapByLevel` / `Map.Level()` | 退化用 `PocketMapParent.sourceMap` + `Map.Tile` + `map.uniqueID` |
| 排除 RVAutoHome 征召 | `RVAutoHome.HomeUtility.IsAutoHomeWorker` | 代理不在 `mapPawns` / `FreeColonists`，通常不会被征召 |

**注意**：入口授权本身**不是**加速器 —— 它只依赖 `ThingRequestGroup.MapPortal` + `GetOtherMap` 覆盖体系（原版 + 公共 API），因此**零 Mod 也可完整工作**（对应 C1）。

**不新增 transpiler**（符合 AGENTS.md）。

## 12. 通用核心 vs 可选加速器（总览）

| 能力 | 通用实现（零 Mod 依赖） | 可选加速器 |
|---|---|---|
| 归属保持 | registry + `CompOmniWorkProxyHome` | — |
| 跨图检测 | `SpawnSetup` / `DeSpawn` / `ParentHolder` / 60 tick `pawn.Map` 校验 | MultiFloors 的 `ScanningOtherLevel`（仅诊断） |
| 跨图工作 | 代理原生搜索逻辑（图无关，天生支持） | — |
| **入口授权** | **`ThingRequestGroup.MapPortal` + `SafeGetTargetMap` + `CoversOn`** | RV 载体识别（`ownerThing`） |
| 强删 / 池重建 / 单代理操作 / 池修复 | `ForceDestroy` / `ForceRecreatePool` / `ForceRecreateAllPools` / `RecreateSingle` / `ReclaimNow` / `RepairPool` | — |
| 全局总开关 | `GlobalWorkEnabled` | — |
| 总览界面 | `BuildOverview` + `Window_OmniWorkstationManager` | MultiFloors `MapByLevel`（更精确的层标签） |
| 口袋地图运行 | **原版 `Map.FillComponents` + `Pathing` 已自动覆盖**（见 8.1） | — |
| 图关系展示 | `Map.Parent` / `PocketMapParent.sourceMap` / `Map.Tile` / `Map.IsPocketMap` | MultiFloors `MapByLevel` |
| 回程 | **R2 传送回收**（`PutProxyToSleep`） | R1 楼梯 / portal job |
| 禁止跨图 | L1 `JobGiver_Work` Prefix + `StartJob` 的 `globalTarget` 判定 + 入口授权 | L2 `StayOnCurrentMap` |
| 避免被别的图抢走 | registry 判定（`RecoverExistingThings` 跳过） | — |

## 13. 存档、清理与失败模式

| 场景 | 处理 |
|---|---|
| 保存时正驻外 | `ProxyHomeRecord.abroadMap` 记录；`List<ProxyHomeRecord>` 随档保存，读档后 `RebuildIndex` 直接恢复归属，继续看护并回程 |
| 读档后 registry 索引重建 | `ExposeData(PostLoadInit)` 与 `FinalizeInit` 都调用 `RebuildIndex()`，由已保存的列表重建 `Dictionary` 索引；另外 `MapComponent_OmniWorkstation.RecoverExistingThings` 收养时按 `listerThings[Pawn]`（**不是 `mapPawns`**）+ 各图休眠池补齐 |
| 镜像校验回写 | `VerifyMirror`：权威表与 Hediff 镜像不一致 → 以权威表回写；权威表无此代理 → 先 `TryRestoreFromMirror` 救回，救不回则清掉镜像。触发点：`Pawn.SpawnSetup` / `Pawn.DeSpawn` + 每 240 tick 的本池校验 |
| `GlobalWorkEnabled` | 随存档保存；读档后保持关闭状态，不自动开工 |
| 旧存档缺少归属记录 | 退化为"按当前所在图归属"并补写列表与镜像（一次性迁移）；代理身上已有镜像时优先由 `TryRestoreFromMirror` 恢复 |
| 代理被容器吞掉 | `!pawn.Spawned` + `ParentHolder` 非空 → `inContainer`；等它重新出现，超时则强制收回 |
| **归属图被销毁**（口袋图关闭 / 地面图移除） | `MapRemoved` → `registry.NotifyHomeMapRemoved(map)` → 归属该图者**全部强删**（含驻外）；级联销毁的子口袋图同样处理 |
| **代理意外丢失**（R-9） | 界面显示 `Lost`；「清理并补建」= `ForceDestroy`（能引用到就彻底清）+ `EnsureProxyCount` 补齐 |
| **玩家拆掉了入口**（授权失效） | 已在未授权图的代理由驻外看护传送收回；新工作不会派发过去 |
| 两图完全无通路 | 只能传送回收，发一条 `Messages.Message` 提示 |
| 第三方卸载 | 全部特化路径静默跳过（`Available == false`），退化为通用路径 |

## 14. 性能与多线程

- 新增开销：① `SpawnSetup/DeSpawn` 的 O(1) 字典操作（`IsProxy` 首行短路）；② 60 tick 维护遍历"驻外代理"小列表（**无 Mod 时恒空**）；③ `JobGiver_Work` Prefix 的 **O(1) 授权查表**（**严禁在该前缀里枚举 `listerThings`**，见 10.5）。
- 授权表刷新是 60 tick 一次的小规模枚举（入口数量少）。
- 重建/强删是低频破坏性操作，不受性能约束，但**必须避免在 `OnGUI` 里执行**。
- **界面性能**（千级代理仍然是 O(可视行数)）：一次性快照 + 只绘制可视行 + 手动/定时刷新；不引入 Linq。
- **不引入**每 tick 全图扫描。
- `TryGetStation` 在寻路工作线程被调用 → 字典改 `ConcurrentDictionary`（或每图分片 + 主线程发布快照）。**授权表同理**（它也被 `JobGiver_Work` 调用；发布时用"整表替换"避免半更新状态被读到）。
- 反射全部在静态构造期解析并缓存 `AccessTools.FieldRef` / `MethodInfo`；类型探测失败即置 `Available = false`。

## 15. 分阶段实施与验证

| 阶段 | 内容 | 验证 |
|---|---|---|
| P1 止血 | `EnsureProxyCount` 不删驻外记录；`PutProxyToSleep` 允许跨图回收；`DeactivateOnJobEnd` 用归属组件；`RecoverExistingThings` 不抢别的池的代理 | V0 + V1 |
| P2 全局管理组件 | registry + `Hediff_OmniWorkProxyHome` 镜像（`isBad=false`）+ `SpawnSetup/DeSpawn` 钩子 + 归属放宽 + `ForceDestroy` + `VerifyMirror` 校验回写 | V0 + V2 + V4 |
| P3 池级重建 | `ForceRecreatePool` / `ForceRecreateAllPools` + Gizmo 文案 + 地图销毁路径 | **V5** |
| P4 单代理操作与池修复 | `RecreateSingle` / `ReclaimNow` / `RepairPool` / `RepairAllPools` | **V7** |
| P5 全局开关与界面 | `GlobalWorkEnabled` + `Window_OmniWorkstationManager` + 设置页按钮 + 语言条目 | **V8** |
| P6 回程 | R2 传送回收 + 超时；R1 加速器 | V1 + V3 |
| P7 口袋地图 | 8.2 清单逐项落实 | **V6** |
| **P8 入口授权** | ✅ 已实施：`OmniWorkProxyEntryAuthorization`（`Refresh` / `IsAuthorized` / `SafeGetTargetMap` / 载体探测 / `Invalidate`）+ `JobGiver_Work.TryIssueJobPackage` Prefix + 缓存失效调用点（工作站 `SpawnSetup`/`DeSpawn`/`NotifyConfigurationChanged`、维护周期 `Refresh`）；**待补**：界面"可去地图 / 未覆盖入口"展示（`UncoveredPortals` 已就绪）、`EntryAuthorizationMode` 设置项 | **V9** |
| P9 跨图总开关 | 部分：`GlobalWorkEnabled` 已实施；L1（按 `pawn.Map` 判定的工作查询闸门）已由 P8 的 Prefix 覆盖；L2 `StayOnCurrentMap` **不采用**（会关掉 MultiFloors 的跨层扫描） | 关闭后代理不再接受非归属图的工作查询 |

### 实施进度（代码实况）

| 项 | 状态 |
|---|---|
| P1 止血（`EnsureProxyCount` 不删驻外记录 / `PutProxyToSleep` 允许跨图回收 / `RecoverExistingThings` 不抢别的池 / `ClearAreaRestriction` 清所有图） | ✅ |
| registry（归属表 `LookMode.Deep` 序列化、`HomeMapOf`/`ManagerOf`/`EnumeratePool`/`EnumerateForeign`、`ForceDestroy`/`RecreateSingle`/`ReclaimNow`/`ForceRecreatePool`/`ForceRecreateAllPools`/`SetGlobalWorkEnabled`/`PruneInvalid`） | ✅ |
| `Pawn.SpawnSetup` / `Pawn.DeSpawn` 换图观测钩子 | ✅ |
| `Hediff_OmniWorkProxyHome` 归属镜像（`isBad=false`）+ `TryRestoreFromMirror` + `VerifyMirror`（换图/入舱即时 + 240 tick 低频兜底） | ✅ |
| 管理动作以归属图为准（`ManagerOf`）、驻外代理一律放行 Tick、`MapRemoved` 整池强删 | ✅ |
| 驻外看护（分级超时：空闲 600 tick / 工作中 1500 tick 强制传送回本池） | ✅（R1 走楼梯回程 job 经源码核实不实现，见 6.2） |
| 入口授权（第 10 章规则）+ `JobGiver_Work` 前缀 + 授权缓存与失效 | ✅ |
| 状态窗口（`Dialog_OmniWorkstationStatus`）：跨图代理计数 / 全局总闸 / 校验归属镜像 | ✅ |
| 池修复（R-9）：`RepairPool` / `RepairAllPools` + `ClearSleepingPoolOfInvalid` + `VerifyPoolMirrors`，界面入口「修复本池」 | ✅ |
| 逐代理操作（R-8）：「停止并回收」/「重建此代理」按钮；界面只登记请求，由 `ProcessPendingManagementRequests()` 在 tick 中执行（OnGUI 阶段不触碰地图集合） | ✅ |
| 入口授权可视化（10.6 部分）：状态窗口显示「可去地图」与「未被覆盖的入口」数（`GetReachableMaps` / `UncoveredPortals`） | ✅ |
| `EntryAuthorizationMode`：Mod 设置页「跨图工作准入」（严格 / 仅警告），随 Mod 设置保存（`OmniCrafterSettings.entryAuthStrict`，`StrictMode` 直接读它） | ✅ |
| 总览窗口（R-7 全量视图）：`Window_OmniWorkstationManager` —— 按池列出工作站 / 代理数、跨图数、未覆盖入口、可去地图；每个代理显示归属图 / 当前位置 / 当前工作；带批量重建、批量修复、校验镜像与逐代理操作 | ✅ |
| R1 走楼梯 / portal 回程 job | ❌ 不实现（源码核实无通用 API，理由见 6.2）；由分级超时的传送回收替代 |

实际改动文件：`OmniWorkProxyRegistry.cs`（新）、`Hediff_OmniWorkProxyHome.cs`（新）、`OmniWorkProxyEntryAuthorization.cs`（新）、`Window_OmniWorkstationManager.cs`（新）、`OmniWorkstation.cs`、`OmniWorkFailureCache.cs`、`OmniCrafterSettings.cs`、`OmniCrafterMod.cs`、`Defs/ThingDefs_Buildings/OmniWorkstation.xml`、`Languages/{ChineseSimplified (简体中文),English}/Keyed/OmniWorkstation.xml`、`FullyAutomaticOmniCrafter.csproj`。

**至此方案文档的全部需求 R-1..R-11 均已实现，或给出有源码依据的不实现结论。**

### 验证矩阵

| | 场景 | 期望 |
|---|---|---|
| **V0** | **不装任何相关 Mod** | 编译通过；`Tests/OmniNavigation` 通过；游戏内行为与改动前一致；断言"驻外看护执行 0 次" |
| V1 | 已知 Mod（MultiFloors / SimplePortal / RV） | 跨图工作正常、池归属正确、可回收 |
| V2 | 未知 Mod 模拟（原版 `MapPortal` / gravship / Debug Action 搬到临时 pocket map） | 超时后传送回收，绝不无主 |
| V3 | 容器场景（装入 `IThingHolder`） | `inContainer` 观测与恢复 |
| V4 | 存读档（驻外 / 容器中 / 全局开关关闭） | 归属、看护与开关状态均恢复 |
| **V5** | **池级重建（R-3 / R-6）** | A 池代理驻留在 B 图 + B 池代理驻留在 A 图时，在 A 图点「重建本池」→ 只销毁 A 池代理（含在 B 图的），B 池代理（含在 A 图的）**毫发无损**；携带物落地不丢失 |
| **V6** | **工作站建于口袋地图（R-5）** | 在 MultiFloors 楼层 / 原版 pocket map 上建站：池正常创建、代理出舱工作、跨图工作与回收正常、存读档正常、该口袋图被销毁时池与代理一并清理 |
| **V7** | **单代理操作与池修复（R-8 / R-9）** | 用「停止并回收」把卡住的代理（卡寻路 / 卡第三方 Job / 卡容器）立即收回其归属池且**不销毁**；用「重建此代理」换一个并保持池内总数不变；人工删除一个代理的实体后用「清理并补建」使池数量恢复 |
| **V8** | **全局开关与总览界面（R-7）** | 关闭总开关 → 所有池停止派发、场上代理全部回到各自归属池的休眠舱、代理**未被销毁**；界面正确显示每个代理的归属池与当前图 + 坐标、容器内代理显示容器名、丢失代理显示 `Lost`；「强制重建所有代理」销毁全部池的代理 |
| **V9** | **入口授权（R-10）** | ① MultiFloors：把一层工作站的半径缩到**覆盖不到楼梯**，确信代理**不会**上二层工作，且界面上该池显示"未覆盖入口 ≥ 1"、"可去地图"不含二层；把半径调大覆盖楼梯后，代理恢复跨层工作。② 逐段授权：A→C 覆盖、C→B 未覆盖时，代理能去 C **不能**去 B。③ SimplePortal：把 portal 建在范围外 → 代理不进门；建在范围内 → 可进门。④ RV：车辆停在工作站范围内 → 允许上车，驶出范围 → 拒绝（载体授权）。⑤ 代理已被搬到未授权图后，驻外看护仍能把它传送回收（**回程不受授权约束**） |

统一回归：

```powershell
dotnet run --project Tests/OmniNavigation/OmniNavigation.csproj -c Release
dotnet run --project Tests/DoBillResources/DoBillResources.csproj -c Release
dotnet build -c Debug
```

## 16. 风险与未确证项

1. MultiFloors 的 `ScanningOtherLevel` 是**静态 bool**、Postfix `HarmonyPriority(100)`；若我们也在 `JobGiver_Work` 挂补丁，需核对 Harmony 执行顺序（当前 Mod 列表顺序未验证）。**这是入口授权最关键的顺序风险**：我们的 Prefix 必须对 MultiFloors 的"内层重跑"同样生效（Harmony 的 Prefix 对递归调用天然生效，故风险可控，但仍需实测）。
2. `allowedAreas` 持有 `Map` 强引用 → 驻外结束必须清理，否则 pocket map 对象可能被拖住。
3. 第三方在代理 `EndCurrentJob` 后立刻销毁 pocket map 的极端情况，只能靠"传送回收"救场。
4. `HarmonyPatch_ScanJobsOnOtherLevelPrioritized`（transpiler 版）会替换 `WorkGiversInOrderNormal`，与 `ApplyStationWorkSettings`（全 1 优先级）叠加后的扫描轮次行为需在真实环境验证。
5. 未知 Mod 若**绕过 `StartJob`**（直接给 `pawn.jobs.curJob` 赋值）或绕过 `GenSpawn.Spawn`（字段级瞬移），唯一防线是 60 tick 的 `pawn.Map != homeMap` 校验 + 超时传送回收 —— 正确性不受影响，只是回收延迟最多一个超时周期（1500 tick）。
6. **管理界面的规模**：`configuredProxyCount` 上限为 1024/图，多图可达数千代理；界面必须只绘制可视行并避免每帧重建列表（见 7.3 性能要求），否则会出现明显卡顿（项目已有 `float-menu-fps1-analysis.md` 类性能记录，应遵循同一标准）。
7. **池的粒度是"图"**（已确认，4.1 推论 2）：同一张图的多个工作站共享一个池。工作站之间**不**各自成池；它们对池的贡献是"工作类型筛选 / 半径 / 启用状态"三者的并集。
8. **入口授权的严格性代价**：逐段授权 + 半径判定意味着"工作站范围必须覆盖楼梯/portal，否则代理去不了那些图"。这是需求的本意，但容易让玩家困惑 → 必须靠 10.6 的界面反馈（可去地图 / 未覆盖入口 / 可选地图高亮）来降低困惑。
9. **入口枚举的完备性**：`ThingRequestGroup.MapPortal` 的确切成员判定基于"枚举成员名 + RVwithPD 的实际用法"确证；实施时应游戏内实测核对是否包含 `StairExit`、`Elevator`，以及是否漏掉某个第三方入口。
10. **非 portal 载体的退化**：`ownerThing` 之外的自定义载体（未知 Mod 的车辆/载具）无法识别 → 保守未授权；gravship（`Map.Parent` 是 `WorldObject`，载体不在任何 `Map` 上）同样保守未授权。
11. **实施期核查项**（不作为本方案前提）：
    - `OuterrealmStorage` 的取料/送料候选集是否限定"代理所在图"（影响口袋地图上的代理取料）；
    - 第三方以"暂停"而非"销毁"方式卸载地图（`MapParent.HasMap = false`）时，该图是否仍在 `Find.Maps` 并继续 tick；
    - 非玩家阵营的口袋图建筑不会被 `listerBuildings.allBuildingsColonist` 收录，故工作站必须属于玩家阵营（预期行为，但需在文档/UI 中说明）；
    - gravship 搬运是否需要"按星球 Tile 比较"的授权判据。
12. 未在游戏内实测复现，本设计基于反编译源码与静态代码分析。
