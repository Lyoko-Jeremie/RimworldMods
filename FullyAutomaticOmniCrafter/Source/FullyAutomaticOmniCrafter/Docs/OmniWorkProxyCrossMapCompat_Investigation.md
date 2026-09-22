# 万能工作代理跨图兼容性故障调查报告

> **参与组合**：RV with built-in PD + SimplePortal + RV Auto-Embark & Disembark（+ MultiFloors 回归约束）
> **调查对象**：`FullyAutomaticOmniCrafter` 的万能工作代理（OmniWorkProxy）跨图派发链路
> **关联设计文档**：`Docs/OmniWorkProxyCrossMapDesign.md`（跨图设计与入口授权的**设计基线**，本报告是其实现偏差的补充）
> **报告日期**：调查基于 RimWorld 1.6 与上述 Mod 的当前版本
> **核心价值**：设计文档第 16 节第 12 条自述"未在游戏内实测复现"。本报告用玩家的**实测现象**定位了三处实现缺陷，并给出**不破坏 MultiFloors 既有正常工作**的修复方案。

---

## 0. 结论速览

| 编号 | 缺陷 | 直接表现 | 位置 | 证据强度 |
|---|---|---|---|---|
| **A** | `SafeGetTargetMap` 第 ② 步被"孤儿 `PocketMapExit`"截胡，导致 SimplePortal 入口**永远解析不出目标图** | 打开跨图开关也不会跨图工作 | `OmniWorkProxyEntryAuthorization.SafeGetTargetMap`（`:196`） | **已确证**（双侧源码） |
| **B** | `runtime.nextSearchTick = int.MaxValue` 是单向门，**驻外代理无法解锁**归属站的搜索状态 | 代理被带走一次后，该工作站**永久不再派发任何工作** | `OmniWorkstation.cs`（`:2070`/`:2059`/`:1785` 置位，`:1731` 唯一重置，`:2996` 回收不重置） | **已确证**（代码路径闭合） |
| **C** | RVAutoHome 把工作代理当作"殖民者"，向它派发跨图进出房 job | "代理全部出现 → 走进车辆出口 → 回站 → 再也不工作" | `RVAutoHome.WorkGiver_TendRoom` + `HomeDecision.CanLeave`；本项目侧无拦截点 | **已确证**（`Pawn.IsColonistPlayerControlled` 对代理为 true） |
| — | （RV 口袋图上的独立问题） | 关掉自动上下车后代理仍完全不工作 | 归因于缺陷 B 的**残留死锁**；不能排除另有口袋图特有原因 | **部分确证 / 待日志** |

**一句话根因**：`SafeGetTargetMap` 对 SimplePortal 恒返回 `null` → 跨图永远未授权（A）；授权失败让代理被带出归属图 → 触发 `nextSearchTick` 永久锁死（B）；而把代理带出去的正是 RVAutoHome 的"把代理当殖民者"的 WorkGiver（C）。

---

## 1. 调查对象与运行环境

### 1.1 参与组合的 Mod

| Mod | packageId | 版本 | Steam 路径 | 作用 |
|---|---|---|---|---|
| RV with built-in PD | `flammpfeil.rv` | 0.2.25 | `F:\SteamLibrary\steamapps\workshop\content\294100\3342334887` | RV 车辆 + 内置 PersonalDimension 口袋图；提供 `InteriorSpaceMapComponent` 载体 |
| SimplePortal | `flammpfeil.SimplePortal` | 0.2.19 | `F:\SteamLibrary\steamapps\workshop\content\294100\3325512144` | `SimplePortal_Building : MapPortal`，用 `linkedPortal` 双向链接两张地图 |
| RV Auto-Embark & Disembark | `Prethoryn.rvautoembark` | 1.6.0 | `F:\SteamLibrary\steamapps\workshop\content\294100\3801427298` | `WorkGiver_TendRoom` 让"殖民者"自动进出 RV 房间工作 |
| **MultiFloors**（回归约束） | `telardo.MultiFloors` | 1.6.1.3 | `F:\SteamLibrary\steamapps\workshop\content\294100\3384660931` | 楼层；`StairEntrance/StairExit/Elevator : Stair : MapPortal` + `JobGiver_Work` Postfix 跨层扫描 |

> **注意**：MultiFloors 当前**已经可以正常工作**，本报告的所有修复方案都必须以"不破坏它"为硬约束。见第 4、9.4 节。

### 1.2 反编译方法（后续开发者可复现）

```powershell
# 需要全局工具 ilspycmd（本机已装：~\.dotnet\tools\ilspycmd.exe，v11.0.0）
ilspycmd -o <输出目录> -p "F:\SteamLibrary\steamapps\workshop\content\294100\3384660931\1.6\Assemblies\MultiFloors.dll"
ilspycmd -o <输出目录> -p "F:\SteamLibrary\steamapps\workshop\content\294100\3801427298\1.6\Assemblies\RVAutoHome.dll"
ilspycmd -o <输出目录> -p "F:\SteamLibrary\steamapps\workshop\content\294100\3325512144\1.6\Assemblies\SimplePortalLib.dll"
ilspycmd -o <输出目录> -p "F:\SteamLibrary\steamapps\workshop\content\294100\3342334887\1.6\Assemblies\RVwithPD.dll"
```

原版源码通过 `rimsage` MCP 查询（`search_source` / `read_file`），路径形如 `Source/RimWorld/MapPortal.cs`。

> 反编译产物属于临时工件，**不要提交进仓库**（参见 `AGENTS.md` 的 scratch 清理要求）。

---

## 2. 复现现象（玩家实测）

在启用上述 Mod 的游戏内存档中，观察到以下四个现象。

**现象 1 —— 打开跨图开关也不会跨图工作**
在工作站上开启"跨图 / 跨层工作"开关后，代理仍然只在归属图内活动，不会进入 RV 房间（或其它图）工作。

**现象 2 —— 全部关闭跨图 + 在 RV 房间内建站 + 打开"自动上下车"**
> "房车地图中的代理会全部出现并进入房车中的车辆出口，随后回到工作站并再也不执行任何工作。"

拆解：代理从休眠舱出来（全部出现）→ 走向房间里的 SimplePortal（车辆出口）→ 被换图到基地图 → 之后被送回工作站位置 → **此后永久不再工作**。

**现象 3 —— 同上，但关闭"自动上下车"**
> "房车地图中的代理完全不会做任何工作。"

**现象 4（背景）—— 同时使用三个 Mod 时，工作代理"无法自动开始工作"**

---

## 3. 本项目相关机制速查（背景）

理解缺陷 B/C 必须先建立这套心智模型。全部来自 `OmniWorkstation.cs`。

### 3.1 代理是什么

- `PawnKindDef FAOC_OmniWorkProxy`，**`<race>Human</race>`**，由 `PawnGenerator.GeneratePawn(..., Faction.OfPlayer)` 生成（`CreateProxy`，`:2756`）。
- 因此 **`Pawn.IsColonistPlayerControlled == true`**（rimsage 确证 `Source/Verse/Pawn.cs:425`）：

```csharp
public bool IsColonistPlayerControlled
    => Spawned && IsColonist && MentalStateDef == null && (HostFaction == null || IsSlave);
```

  → 这是第三方 Mod 把它当普通殖民者的**根本原因**（缺陷 C）。
- 代理被 `MapPawns.DeRegisterPawn` 移出普通注册表（`Patch_OmniWorkProxy_KeepOutOfPawnRegistry`，`:3787`），所以**基于 `mapPawns` 列表**的第三方遍历抓不到它，但**基于 `pawn.IsColonistPlayerControlled` 的判定**会抓在它。
- 归属关系权威表在 `GameComponent_OmniWorkProxyRegistry`（`OmniWorkProxyRegistry.cs`），代理身上另有 `Hediff_OmniWorkProxyHome` 镜像。

### 3.2 派发泵与"热续"

`MapComponentTick`（`:1986`）是唯一派发入口，关键常量（`:1388-1398`）：

```csharp
AssignmentInterval = 60;      // 维护周期
EmptySearchBackoffBase = 60;  EmptySearchBackoffMax = 120;
IdleRetryInterval = 30;       IdleGraceTicks = 180;
IdlePumpFallbackInterval = 60;
StalledJobTimeoutTicks = 2500;
```

派发流程（每个泵步只唤醒一个代理）：

```
TryGetNextDueStation(tick)                 // 只看 runtime.nextSearchTick <= tick 的站
  └─ 挑不到 → ReclaimIdleProxies(); nextPumpTick = earliestTick==int.MaxValue ? tick+60 : earliestTick; return
  └─ 挑到 → TryGetNextIdleProxy(...)
       └─ WakeProxyForVanillaSearch(record, station)   // :3142
            ├─ WakeProxyAtStation → GenSpawn.Spawn 到工作站格
            ├─ record.waitingForWork = true
            ├─ runtime.probePawn = pawn; stationRuntime.nextSearchTick = int.MaxValue
            └─ StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 1))
```

1 tick 的 `Wait_MaintainPosture` 结束后，**原版** `Pawn_JobTracker` 自行进入思考树选取真实工作（本项目**不**主动调用任何 WorkGiver）。

### 3.3 入口授权（跨图准入）

`Patch_OmniWorkProxy_EntryAuthorization`（`:3676`）是 `JobGiver_Work.TryIssueJobPackage` 的 **Prefix**：

```csharp
if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
Map queryMap = pawn.Map;
Map homeMap = GameComponent_OmniWorkProxyRegistry.HomeMapOf(pawn);
if (homeMap == null || homeMap == queryMap) return true;          // 同图放行

MapComponent_OmniWorkstation homeManager = homeMap.GetComponent<MapComponent_OmniWorkstation>();
if (homeManager != null && !homeManager.AnyStationAllowsCrossMapWork())
{
    __result = ThinkResult.NoJob; return false;                   // 硬拒绝，与 strict/warn 无关
}
if (OmniWorkProxyEntryAuthorization.IsAuthorized(homeMap, queryMap)) return true;
if (OmniWorkProxyEntryAuthorization.StrictMode) { __result = ThinkResult.NoJob; return false; }
OmniWorkProxyEntryAuthorization.NotifyUnauthorized(homeMap, queryMap, pawn);
return true;                                                      // 仅警告模式放行
```

- `HomeMapOf`（`OmniWorkProxyRegistry.cs:139`）**优先读登记表**，未登记才退化为 `pawn.Map`。这是"临时改写 `pawn.Map` 的第三方 Mod 也会被约束"的关键。
- `allowCrossMapWork` 默认 **`false`**（`OmniWorkstation.cs:45`）。
- `StrictMode` 默认 **true**（`OmniWorkProxyEntryAuthorization.cs:31`）。
- 授权表由 `Refresh(pool)`（同文件 `:102`）在每个维护周期全量重算。

### 3.4 驻外看护

- `MaintainAbroadProxies(tick)`（`:2625`）在维护周期里遍历 `EnumerateForeign`（`OmniWorkProxyRegistry.cs:173`，条件是 `record.abroadMap != null && abroadMap != pool`）。
- `abroadMap` 只由 `Patch_OmniWorkProxy_TrackMapChange`（`Pawn.SpawnSetup`，`:3606`）和 `Patch_OmniWorkProxy_TrackDeSpawn`（`:3624`）更新，即**必须经过 `SpawnSetup`/`DeSpawn`** 才会被标记为驻外。
- 超时后 `ReclaimAbroadProxy(pawn)`（`:2666`）→ `StopIssuedJob` + `PutProxyToSleep`。

### 3.5 代理网格（导航）

`OmniWorkProxyNavigation`（`OmniWorkProxyNavigation.cs`）：代理使用自定义全通网格 `FAOC_OmniWorkProxyPathGrid`（`Defs/PathGridDef/OmniWorkProxyPathGrid.xml`，`workerType = FullyAutomaticOmniCrafter.OmniWorkProxyPathGrid`），恒定成本 10，任意有效格连通，**不借用原版 Region / 门 / 地形**。挂接点见 `Patch_OmniWorkProxy_PathGrid`（`:3800`，patch `Pawn.GetPathContext`、`Pathing.For(TraverseParms)`、`Reachability.CanReach`、`GenGrid.StandableBy`、`Pawn.Flying` 等）。

> 说明：现象 2 中代理"能走到出口"证明**导航链路本身是通的**，问题不在网格。

---

## 4. MultiFloors 的跨层机制（反编译确证，**修复的硬约束**）

> MultiFloors 目前**工作正常**。本节把它的机制完整记下来，目的有二：
> ① 后续任何对 `SafeGetTargetMap` / 入口授权的改动，都必须逐条核对本节结论；
> ② 它是"第三方 Mod 如何与本项目交互"的最佳样本。

### 4.1 入口继承体系

```csharp
// MultiFloors/Stair.cs:13
[HotSwappable]
public abstract class Stair : MapPortal, IStoreSettingsParent
{
    // :140  基类【故意抛异常】—— 本项目注释里提到的就是这里
    public override Map GetOtherMap()       => throw new NotImplementedException();
    public override IntVec3 GetDestinationLocation() => throw new NotImplementedException();

    public abstract Stair ConnectedStair { get; }          // :43
    public Map ConnectedMap => ConnectedStair?.Map;        // :45
}

// MultiFloors/StairEntrance.cs:12
public class StairEntrance : Stair
{
    public StairExit Exit;                                  // :15
    public override Stair ConnectedStair => Exit;

    // :48  ★ 会惰性生成目标楼层图
    public override Map GetOtherMap()
    {
        if (base.ConnectedMap == null) GenerateDestinationMap();
        return base.ConnectedMap;
    }
}

// MultiFloors/StairExit.cs:11
public class StairExit : Stair
{
    public override Map GetOtherMap() => base.ConnectedMap;   // :105
}

// MultiFloors/Elevator.cs:14
public class Elevator : Stair { public override Map GetOtherMap() { ... } }   // :294
```

**关键点：`Stair` 从不设置 `MapPortal.exit`**（`Stair.ExposeData`，`Stair.cs:182`，只转调 `((MapPortal)this).ExposeData()`）。

### 4.2 因此 `SafeGetTargetMap` 对楼梯**恰好落在第 ④ 步**

对照 `OmniWorkProxyEntryAuthorization.SafeGetTargetMap`（`:196`）：

| 步骤 | 对 `StairEntrance` 是否命中 | 原因 |
|---|---|---|
| ① `portal is PocketMapExit` | 否 | 楼梯不是 `PocketMapExit` |
| ② `portal.exit != null` | **否** | `Stair` 从不设置 `exit`，`MapPortal.ExposeData` 读出来是 `null` |
| ③ `portal.PocketMapExists` | 否 | 楼层层图是**普通地图**（`MF_LevelMapComp.MapByLevel`），不是 pocket map |
| ④ `GetType() != typeof(MapPortal)` → `GetOtherMap()` | **是** ✔ | `StairEntrance` 有实现，返回上层图 |

**结论：MultiFloors 的跨层授权完全依赖第 ④ 步。** 任何把 ④ 提前、删除、或改变其触发条件的改动，都可能让 MultiFloors 的跨层工作直接失效。

### 4.3 跨层扫描：`JobGiver_Work` 的 Postfix

`MultiFloors.HarmonyPatches/HarmonyPatch_ScanJobsOnOtherLevel.cs`：

```csharp
[HarmonyPatch(typeof(JobGiver_Work), "TryIssueJobPackage")]
[HarmonyPostfix]
[HarmonyPriority(100)]
public static void ScanJobsOnOtherLevel(ref ThinkResult __result, JobGiver_Work __instance,
                                       Pawn pawn, JobIssueParams jobParams)
{
    if (!ScanningOtherLevel && __result == ThinkResult.NoJob && !pawn.StayOnCurrentMap()
        && pawn.Map.TryGetLevelControllerOnCurrentTile(out var controller))
    {
        ScanningOtherLevel = true;
        ScanWorksOnOtherLevelVerticallyOutWard(__instance, pawn, jobParams, controller, out __result);
        ScanningOtherLevel = false;
    }
}
```

内部 `TryFindJob`（同文件 `:74`）的核心：

```csharp
if (!LevelUtility.UnsafeTeleportTo(pawn, destMap, newPosition)) return false;
try
{
    ThinkResult val = ((ThinkNode)instance).TryIssueJobPackage(pawn, jobParams);   // ★ 递归重跑
    if (val != ThinkResult.NoJob && !controller.TileDeferredJobValidator.CollideWithExistingThinkResults(val))
    {
        Job stairJob = CrossLevelJobFactory.MakeChangeLevelThroughStairJob(destStair, destMap);
        thinkResult = new ThinkResult(stairJob, val.SourceNode, val.Tag, val.FromQueue);
        pawn.NextJobThinkResult() = val;
        LevelUtility.UnsafeTeleportBack(pawn);
        return true;
    }
}
catch (Exception arg) { Log.Error($"[MultiFloors] {arg} was thrown from vanilla or modded work givers."); }
LevelUtility.UnsafeTeleportBack(pawn);
return false;
```

**交互要点**：

1. Postfix 只在 `__result == ThinkResult.NoJob` 时启动扫层。本项目的 EntryAuth Prefix 在未授权时会**主动设置 `__result = NoJob`**，因此**会**让 MultiFloors 继续扫其它层 —— 这是符合预期的（本层拒绝 ≠ 阻断第三方扫层机制）。
2. 递归调用期间 `pawn.Map == destMap`。此时本项目的 EntryAuth Prefix 会读到 `queryMap = destMap != homeMap` → 走 `AnyStationAllowsCrossMapWork()` + `IsAuthorized(homeMap, destMap)`。**授权通过与否，直接决定 MultiFloors 能否在该层找到工作。**
3. `pawn.StayOnCurrentMap()` 是 MultiFloors 自己的扩展属性（`PawnLevelSettings`，可被玩家按小人开关，见 `PawnColumnWorker_StayOnCurrentMap.cs`）。返回 `true` 时 MultiFloors 完全不扫其它层。
4. `CrossLevelJobFactory.MakeChangeLevelThroughStairJob(Thing stair, Map destMap)` —— 注意 **`stair` 可以为 `null`**（如 `MakeChangeLevelThroughStairJob(null, map)`，见 `HarmonyPatch_MainColonistBehavior.cs`、`Dialog_GatherPawns.cs` 等多处）。后续设计"按 job target 判定是否为跨图 job"的规则时，必须容忍 target 为 null 的情况。

### 4.4 `UnsafeTeleportTo` 的语义（**极关键**）

```csharp
// MultiFloors/LevelUtility.cs:93
public static bool UnsafeTeleportTo(Pawn pawn, Map newMap, IntVec3 newPosition)
{
    if (newPosition == IntVec3.Invalid) return false;
    sbyte mapIndexOrState = (sbyte)Find.Maps.IndexOf(newMap);
    pawn.OldMap()      = pawn.Map;
    pawn.OldPosition() = pawn.Position;
    pawn.mapIndexOrState = mapIndexOrState;     // ★ 直接改私有字段
    pawn.positionInt     = newPosition;         // ★ 直接改私有字段
    return true;
}

// :113  还原
public static void UnsafeTeleportBack(Pawn pawn) { /* 写回 OldMap / OldPosition */ }
```

**它绕过了 `DeSpawn` / `SpawnSetup` / `GenSpawn`。** 三个直接推论：

| 推论 | 影响 |
|---|---|
| `Patch_OmniWorkProxy_TrackMapChange`（`Pawn.SpawnSetup` Postfix）**不触发** | `registry` 的 `abroadMap` 保持 null |
| → `MaintainAbroadProxies` 不会把代理当成"驻外"回收 | **这就是 MultiFloors 场景没出现 RV 那种"永久停摆"的原因** |
| 所有依赖 `pawn.Map` 的逻辑在递归瞬间会看到 `destMap` | `HomeMapOf` 仍返回登记表的归属图（`OmniWorkProxyRegistry.cs:139-145`），判定因此正确 |

### 4.5 Prioritized 变体（`UsePrioritizedScanner`）

`HarmonyPatch_ScanJobsOnOtherLevelPrioritized.cs` 是同一机制的高优先级版：

- `Prepare()` 返回 `MultiFloorsModHandler.Settings.UsePrioritizedScanner`（与上一版本互斥）。
- 同样有 `JobGiver_Work.TryIssueJobPackage` 的 `HarmonyPostfix`（`HarmonyPriority(100)`），同样调用 `HarmonyPatch_ScanJobsOnOtherLevel.ScanWorksOnOtherLevelVerticallyOutWard`。
- 另有 `HarmonyPrefix CheckShouldReplaceWorkGiverList`（`void`，**不阻断**）和 `HarmonyTranspiler ReplaceWorkGiverList`（`Priority = 2147483646`），后者只把 `Pawn_WorkSettings.WorkGiversInOrderNormal` 的 getter 替换成自己的 High/Low 分组版本：

```csharp
// 只做一次 getter 替换，不改变方法返回语义
.MatchEndForward(CodeMatch.Calls(AccessTools.PropertyGetter(typeof(Pawn_WorkSettings), "WorkGiversInOrderNormal")))
.RemoveInstruction().Insert(new CodeInstruction(OpCodes.Call, GetWorkGiversInOrderNormal))
```

→ **对本项目的 Prefix（`return false` + 设置 `__result`）无破坏性**；但该 Transpiler 与 `ApplyStationWorkSettings`（把所有允许的 workType 优先级设为 1）叠加后的扫描轮次行为属于**待实测项**（设计文档第 16 节第 4 条已记录）。

### 4.6 与入口授权的整体时序

```
[归属图] JobGiver_Work.TryIssueJobPackage
   ├─ 本项目 Prefix                       → 同图，放行
   ├─ 原版扫描（WorkGiver 链）
   └─ MultiFloors Postfix (__result == NoJob)
        └─ ScanWorksOnOtherLevelVerticallyOutWard
             └─ 对每个 destMap:
                  UnsafeTeleportTo(pawn, destMap, stairPos)
                  └─ 递归 JobGiver_Work.TryIssueJobPackage
                       ├─ 本项目 Prefix  → homeMap != destMap
                       │                   → AnyStationAllowsCrossMapWork()?
                       │                   → IsAuthorized(homeMap, destMap)?   ← 由 SafeGetTargetMap 决定
                       └─ 放行 → 该层找到工作
                  UnsafeTeleportBack
                  → 产出 ChangeLevelThroughStair job（仅当上面放行）
```

### 4.7 MultiFloors 回归约束（修复前必读）

修复代码**必须**同时满足：

1. `SafeGetTargetMap` 对 `Stair`/`StairEntrance`/`StairExit`/`Elevator` 的返回值**保持不变**（当前为：②不命中 → ③不命中 → ④ `GetOtherMap()`）。
2. **不得**把第 ④ 步提前到第 ② / ③ 步之前。
3. **不得**削弱 `portal.GetType() != typeof(MapPortal)` 的类型保护（否则精确类型 `MapPortal` 会被调用 `GetOtherMap()` 而惰性生成 pocket map）。
4. **不得**去掉 ④ 的 `try/catch`（`Stair` 基类会 `throw new NotImplementedException()`）。
5. 任何"启动 job 时校验"的新规则，必须对 **`target` 为 `null` 的跨层 job** 跳过（MultiFloors 大量使用这种形式）。
6. 针对"驻外代理"的修复要注意：MultiFloors 的 teleport **不产生 `abroadMap`**，不要新增依赖 `pawn.Map` 的驻外判定。

---

## 5. SimplePortal 的跨图机制（反编译确证）

### 5.1 它确实是原版 `MapPortal` 子类

```csharp
// SimplePortalLib/SimplePortal_Building.cs:12
[StaticConstructorOnStartup]
public class SimplePortal_Building : MapPortal, IRenameable
{
    public CompSimplePortal comp = null;
    public CompSimplePortal Portal { get { ... TryGetComp<CompSimplePortal>(this) ... } }

    // :251  ★ 真正正确的跨图链接：对端 portal 建筑所在的图
    public override Map GetOtherMap()
    {
        if (StunnedByEMP) return null;
        CompSimplePortal portal = Portal;
        return portal?.linkedPortal?.MapHeld;
    }
}
```

→ 它**也会**被 `SafeGetTargetMap` 的第 ④ 步正确处理 …… **前提是能走到第 ④ 步**。

### 5.2 但它的 `exit` 是一个"孤儿"，正好卡在第 ② 步

```csharp
// SimplePortalLib/SimplePortal_Building.cs:413
public override void ExposeData()
{
    base.exit = null;                       // 先清空，避免 MapPortal 走 exit 分支
    ((MapPortal)this).ExposeData();
    Scribe_Values.Look<string>(ref name, "Portal_Name", null, false);
    base.exit = new PocketMapExit();        // ★ 无条件塞回一个【全新的、未 spawn 的】PocketMapExit
}
```

原版字段定义（rimsage 确证）：

```csharp
// Source/RimWorld/MapPortal.cs:26
public PocketMapExit exit;
// :119
Scribe_References.Look<PocketMapExit>(ref this.exit, "exit");
```

未 spawn 的 `PocketMapExit` → `MapHeld == null`。于是：

```csharp
if (portal.exit != null) return portal.exit.MapHeld;   // ← 命中，返回 null
```

`Refresh(pool)` 里紧接着 `if (target == null || target == pool) continue;` → **SimplePortal 的入口永远不会写进授权表**。

### 5.3 并存的 `CompSimplePortal.GetOtherMap()`

`SimplePortalLib/CompSimplePortal.cs:324` 也有一个 `GetOtherMap()`，但 `SafeGetTargetMap` 只调 `MapPortal` 上的虚方法，与它无关。

---

## 6. RVwithPD 的载体机制

```csharp
// RVwithPD/InteriorSpaceMapComponent.cs:12,18
public class InteriorSpaceMapComponent : CustomMapComponent
{
    public ThingWithComps ownerThing;      // ★ 车辆本体（永远在基地图上）
    public bool Closing => closing || ThingUtility.DestroyedOrNull(ownerThing);
    // :36  MapComponentTick → 每 300 tick 检查车辆是否销毁，销毁则 DestroyPocketMap
}
```

本项目的载体探测（`OmniWorkProxyEntryAuthorization.BuildCarrierGetter`，`:223`）按**类型名 + 字段名**软探测：

```csharp
foreach (Type type in AccessTools.AllTypes())
    if (type.Name == "InteriorSpaceMapComponent" && typeof(MapComponent).IsAssignableFrom(type)) { ... }
FieldInfo field = componentType.GetField("ownerThing", Public | NonPublic | Instance);
```

**已核对一致**：`InteriorSpaceMapComponent` 继承 `CustomMapComponent`（→ `MapComponent`），字段名 `ownerThing` 类型 `ThingWithComps` ✔ → 载体探测**可以成功**。

载体授权路径（`Refresh`，`:136`）：

```csharp
// 非 portal 型跨图：载体（车辆）位置落在本池工作站范围内才授权。
if (CarrierGetter == null) return;
for (int i = 0; i < Find.Maps.Count; i++)
{
    Map candidate = Find.Maps[i];
    if (candidate == pool || row.ContainsKey(candidate)) continue;   // ★ 已被 portal 判定过的图不再按载体重判
    Thing carrier = CarrierGetter(candidate);
    if (carrier == null) continue;
    if (IsCarrierAuthorized(pool, carrier)) row[candidate] = true;
}
```

**局限（缺陷 A 的成因之一）**：`IsCarrierAuthorized` 要求 `carrier.MapHeld == pool` 且车辆位置被本池工作站覆盖（`IsCoveredByPool`）。工作站若建在 **RV 房间内部**，车辆在基地图上、远离工作站半径 → **永不授权**。

---

## 7. RVAutoHome 的侵入方式

### 7.1 它把工作代理当成"可自动接管的殖民者"

```csharp
// RVAutoHome/HomeUtility.cs:452
public static bool IsAutoHomeWorker(Pawn p)
{
    if (p == null || p.Destroyed || p.Dead) return false;
    if (p.IsColonistPlayerControlled) return true;      // ★ 代理满足此条（见 3.1）
    return CanWorkBug(p);
}
```

### 7.2 它挂在 vanilla 工作类型上，因此**绕过本项目的 giver 过滤**

`1.6/Defs/WorkGiverDefs/RVAutoHome_WorkGivers.xml` 定义了一组 `WorkGiverDef`（`giverClass = RVAutoHome.WorkGiver_TendRoom`），分别挂在 `Hauling` / `Warden` / `Doctor` / `Construction` / `Mining` / `PlantCutting` / `Growing` / `Cleaning` / `Firefighter` / `Research` / `Cooking` / `Crafting` / `Smithing` / `Tailoring` / `Art` 上，`priorityInType = 20`、`nonColonistsCanDo = true`。

> 本项目的 `OmniWorkCatalog.UnclassifiedWorkGivers` + `Patch_OmniWorkProxy_AppendUnclassifiedWorkGivers`（`:3456`）**只处理未分类 giver**；这些有正规 `workType` 的 giver 走原版排序，**完全绕过本项目过滤**。唯一存在的排除表 `OmniCrossLevelCompat.SkipForProxy`（`:683`）只针对 `HaulToInventory` / `PickUpAndHaul.WorkGiver_HaulToInventory`，且需检测到 MultiFloors 类型才生效。

### 7.3 代理位于房间图时，它派发的是"离开房间"

```csharp
// RVAutoHome/WorkGiver_TendRoom.cs
public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
{
    HomeRecord home = FindHome(pawn, t);
    if (home == null) return false;
    if (pawn.MapHeld == home.roomMap) return HomeDecision.CanLeave(pawn, home);      // ★ 站在房间里
    return HomeDecision.CanEnterForWork(pawn, home, Kind, GiverWorkType);
}
public override Job JobOnThing(...)
{
    return (pawn.MapHeld == home.roomMap) ? HomeDecision.MakeLeaveJob(pawn, home)
                                          : HomeDecision.MakeEnterForWorkJob(pawn, home, Kind, GiverWorkType);
}
```

`HomeDecision.CanEnterForWork`（`HomeDecision.cs:58`）的判定链只包含：`ReadyToDecide`（含 `IsAutoHomeWorker`）、`settings.autoEnterByWork`、家门可用、`NeedsLow`（Rest/Food < 0.5，代理无需求 → 放行）、流向设置、`RoomHasWorkFor`（只看 `pawn.workSettings` 中该 workType 是否启用 —— 而代理的工作表正是被 `ApplyStationWorkSettings` 投影成 1）、冷却。**没有任何"这是别的 Mod 的代理"的排除。**

产出的 job 是 SimplePortal 的 `EnterSimplePortal`，target = `roomPortal.parent`（房间里的出口）或 `vehiclePortal.parent`。

### 7.4 两个副作用补丁

```csharp
// RVAutoHome/Core.cs:144-150  默认【压制】SimplePortal 的两个补丁
if (settings == null || settings.suppressSimplePortalBedrooms)
{
    SuppressMethod(harmony, "SimplePortal.HarmonyPatches.Patch_SimplePortal_RouteToBedroom:Postfix");
    SuppressMethod(harmony, "SimplePortal.HarmonyPatches.Patch_SimplePortal_ReturnOnWake:Postfix");
}
// 实现方式：给这两个 Postfix 方法本身挂一个 return false 的 Prefix（HarmonyPriority 800）
```

`Patch_SimplePortal_ReturnOnWake` 正是 patch 在 `Pawn_JobTracker.StartJob` 上（与本项目 `Patch_OmniWorkProxy_TrackVanillaJobStart` 同一目标），作用是"殖民者被唤醒/闲逛时送回注册卧室"。被压制后，**代理误入别的图后自己走回来的兜底被削弱**（不过该补丁只对 `LayDown`/`GotoWander`/`Wait_Wander` 生效，而这三个 job 都在本项目的 `BlacklistedJobDefs`（`:3248`）里，代理本就不会跑，故实际影响有限）。

---

## 8. 根因分析

### 8.1 缺陷 A —— `SafeGetTargetMap` 被"孤儿 `PocketMapExit`"截胡

**证据（双侧源码，确证）**：

本项目当前实现（`OmniWorkProxyEntryAuthorization.cs:196-219`）：

```csharp
public static Map SafeGetTargetMap(MapPortal portal)
{
    if (portal == null) return null;
    try
    {
        if (portal is PocketMapExit exit) return exit.entrance?.Map;   // ①
        if (portal.exit != null) return portal.exit.MapHeld;           // ② ★ 命中即 return
        if (portal.PocketMapExists) return portal.PocketMap;           // ③
        if (portal.GetType() != typeof(MapPortal)) return portal.GetOtherMap();  // ④ 到不了
        return null;
    }
    catch (Exception error) { /* WarningOnce */ return null; }
}
```

SimplePortal 侧（`SimplePortal_Building.cs:413`）：`ExposeData()` 里 `base.exit = new PocketMapExit()` —— **一个全新的、未 spawn 的 `PocketMapExit`**。

未 spawn → `MapHeld == null` → ② 返回 `null` → `Refresh` 里 `if (target == null || target == pool) continue;` → **该入口被永久排除出授权表** → `IsAuthorized(pool, pocketMap)` 永远为 `false` → 即使 `allowCrossMapWork = true` 也永远不授权。

**设计文档第 10.3 节的原始判断（"第三方子类都 override 了且实现安全"）本身没错**，错在漏掉了"第 ② 步可能先被无效 `exit` 短路"这一情形 —— 注释里 ④ 的说明写得很清楚，代码却到不了 ④。

**为什么 MultiFloors 不受影响**：它的 `exit` 是 `null`，② 本就不命中，一路走到 ④（见 4.2）。

**残余通路**：只剩载体路径（第 6 节），要求 RV 车辆本体落在工作站半径内。工作站建在 RV 房间内部时该条件天然不成立 → 完全没有跨图。

### 8.2 缺陷 B —— `nextSearchTick = int.MaxValue` 是单向门

**置位点（3 处）**：

```csharp
// OmniWorkstation.cs:2070  WakeProxyForVanillaSearch 成功后
stationRuntime.nextSearchTick = int.MaxValue;
stationRuntime.probePending = true;
// :2059  TryGetNextIdleProxy 失败
stationRuntime.nextSearchTick = int.MaxValue;
// :1785  NotifyProxyBecameIdle 空闲宽限中
runtime.nextSearchTick = int.MaxValue;
```

**唯一的重置入口 `ResetStationSearchState`（`:1731`）**：`nextSearchTick = tick; consecutiveFailures = 0;`，调用者只有
`NotifyConfigurationChanged`（`:1682`）、`NotifyWorkFilterChanged`（`:1712`）、`WakeAllStations`（`:1747`）、`NotifyDesignationAdded`（`:1873`）
—— **全部是"玩家动作"或"新 Designation"**。另有两个隐式重置：`NotifyProxyStartedJob`（`:1846`，`= CurrentTick + 1`）、`NotifyProxyBecameIdle` 宽限超时分支（`:1793`）。

**解锁路径为什么全部失效**：

```csharp
// :3107-3109  TryGetNextDueStation
if (runtime.nextSearchTick < earliestTick) earliestTick = runtime.nextSearchTick;
if (runtime.nextSearchTick > tick) continue;                 // ← int.MaxValue 永远 continue
```

```csharp
// :1871  NotifyDesignationAdded 被 probePending 挡住
if (runtime.probePending) continue;
if (runtime.nextSearchTick <= tick) continue;
ResetStationSearchState(runtime, tick);
```

```csharp
// :3538  DeactivateOnJobEnd —— 驻外代理【不会】触发 NotifyProxyBecameIdle
if (manager.OwningMap != ___pawn.Map) return;
// :3594  TrackVanillaJobStart 同样
if (manager.OwningMap != ___pawn.Map) return;
```

```csharp
// :2996-3001  PutProxyToSleep（驻外回收路径）只清这两个
if (record.station != null && stationStates.TryGetValue(record.station, out StationRuntime runtime) &&
    runtime.probePawn == pawn)
{
    runtime.probePawn = null;
    runtime.probePending = false;          // ← nextSearchTick 保持 int.MaxValue
}
```

**闭合结论（确证）**：代理一旦被带离归属图，`NotifyProxyBecameIdle` 这条唯一能重置 `nextSearchTick` 的常规路径就被 `OwningMap != pawn.Map` 拦住；`MaintainAbroadProxies` 的回收只清 `probePending`；`NotifyDesignationAdded` 又被 `probePending` 挡住。于是该工作站**永久停摆**，泵退化为每 60 tick 空转（`nextPumpTick = tick + IdlePumpFallbackInterval`），只有玩家改工作站设置才能复活。

**为什么 MultiFloors 不受影响**：`UnsafeTeleportTo` 不触发 `SpawnSetup` → `abroadMap` 不写 → 不入 `MaintainAbroadProxies`，也不产生"驻外"状态（见 4.4）。

### 8.3 缺陷 C —— RVAutoHome 把代理当殖民者，且本项目在 job 层无拦截

- 代理 `IsColonistPlayerControlled == true`（3.1）→ `HomeUtility.IsAutoHomeWorker` 直接放行 → `WorkGiver_TendRoom` 会为代理做进出房决策（7.1–7.3）。
- 代理位于房间图时走 `HomeDecision.CanLeave` → 产出 `EnterSimplePortal` job，target 是房间里的出口。
- **关键缺口**：这份 job 是在**归属图上**被选中的（`pawn.Map == homeMap`），因此 `Patch_OmniWorkProxy_EntryAuthorization` 的 Prefix 直接 `return true` 放行 —— **它的判定时机在"换图之后"，而这份 job 的目标是"换图之前就要走出去"**。`allowCrossMapWork = false` 也拦不住它。
- 之后代理在基地图上 → EntryAuth 硬拒绝（`AnyStationAllowsCrossMapWork() == false` → `ThinkResult.NoJob`）→ 在基地图上什么也做不了 → `MaintainAbroadProxies` 超时回收 → 送回工作站 → 触发缺陷 B 锁死。

### 8.4 现象与缺陷对照表

| 现象 | 主要成因 | 说明 |
|---|---|---|
| 1. 打开跨图开关也不跨图 | **缺陷 A** | 入口解析恒为 `null`；载体路径又要求车辆在站内 |
| 2. 关跨图 + 站建房间内 + 开自动上下车 → 全部出现 / 走进出口 / 回站 / 永不工作 | **缺陷 C 触发 → 缺陷 B 锁死** | "全部出现"是死锁成型前泵连续唤醒的残留效应；"回站"是驻外回收 |
| 3. 同上但关自动上下车 → 完全不工作 | **缺陷 B 残留**（部分确证） | 若发生在同一存档内，是最合理的解释；**不能排除**另有口袋图特有原因，需日志区分（见第 11 节） |
| 4. 三 Mod 组合下"无法自动开始工作" | A + B + C 综合 | — |

### 8.5 可复现的最小触发链（供后续回归）

```
工作站 D 建在 RV 口袋图 R 上，归属图 = R
① 泵唤醒代理 A：nextSearchTick = int.MaxValue, probePending = true
② A 在 R 上思考；RVAutoHome（若开启）给出 EnterSimplePortal（目标为基地图 B）
③ A 通过 SimplePortal 换图到 B（此步【不经过】EntryAuth 判定）
④ A 在 B 上：EntryAuth 因 allowCrossMapWork=false 或未授权 → ThinkResult.NoJob
⑤ A 在 B 上空闲，但 EndCurrentJob 通知被 `OwningMap != pawn.Map` 吞掉
⑥ MaintainAbroadProxies 超时 → ReclaimAbroadProxy → PutProxyToSleep（只清 probePending）
⑦ 此后 nextSearchTick 恒为 int.MaxValue → 泵永久空转 → "再也不工作"
```

---

## 9. 修复方案（修订版）

> ⚠️ **勘误**：本报告早期版本曾建议"把第 ④ 步提前到第 ②/③ 之前（第三方子类优先 `GetOtherMap()`）"。
> **该写法必须废弃** —— 它会让 MultiFloors 的 `Stair` 体系在原本能成功的路径上先失败（见 4.2、4.7）。
> 下文 9.1 是**修订后**的正确形态。

### 9.1 修复 1（必修）—— 只给第 ② 步加有效性判定，**顺序与条件一律不动**

文件：`OmniWorkProxyEntryAuthorization.cs`，方法 `SafeGetTargetMap`。

```csharp
public static Map SafeGetTargetMap(MapPortal portal)
{
    if (portal == null) return null;
    try
    {
        // ① 原版口袋图出口：反向链接回入口所在图，无副作用。【不动】
        if (portal is PocketMapExit exit) return exit.entrance?.Map;

        // ② 原版正向链接（入口 → 出口）。
        //    ★ 新增有效性判定：第三方子类可能只 new 一个【未生成的 PocketMapExit】占位。
        //      SimplePortal_Building.ExposeData() 正是如此：
        //          base.exit = null; base.ExposeData(); base.exit = new PocketMapExit();
        //      此时 MapHeld 恒为 null。若据此 return null，该入口会被永久抹出授权表。
        PocketMapExit original = portal.exit;
        if (original != null && original.MapHeld != null) return original.MapHeld;

        // ③ 已经生成过的口袋图：只读属性，不触发生成。【不动】
        if (portal.PocketMapExists) return portal.PocketMap;

        // ④ 第三方子类（MultiFloors 的 StairEntrance / StairExit / Elevator，
        //    SimplePortal 的 SimplePortal_Building）都 override 了 GetOtherMap()。
        //    ★ 顺序与条件【必须原样保留】：
        //      - MultiFloors 的 Stair 基类版本会 throw new NotImplementedException()，靠下面的 catch 兜住；
        //      - MultiFloors 正是靠这一步通过授权的（其 Stair 从不设置 exit）。
        if (portal.GetType() != typeof(MapPortal)) return portal.GetOtherMap();
        return null;
    }
    catch (Exception error)
    {
        Log.WarningOnce("[OmniWorkstation] portal target lookup failed: " + error, portal.thingIDNumber);
        return null;
    }
}
```

**为什么"加 `MapHeld != null`"是安全的最小改动**：

| 情形 | 改前 | 改后 |
|---|---|---|
| MultiFloors `Stair*`（`exit == null`） | ②不命中 → ③不命中 → ④ `GetOtherMap()` | **完全一致** |
| 原版已链接 portal（`exit` 已 spawn） | ②命中返回 `exit.MapHeld` | **完全一致** |
| 原版未链接 `MapPortal` | 落到 `return null` | **完全一致**（④ 的类型保护未动） |
| SimplePortal 孤儿 `exit`（未 spawn） | ②命中 → `null`（入口被抹掉） | ②不命中 → ④ → `linkedPortal.MapHeld` ✔ |

### 9.2 修复 2（必修）—— 打断 `nextSearchTick` 的单向门

可选实现（任一即可，建议前两条都做）：

1. **回收即解锁**：`ReclaimAbroadProxy`（`:2666`）/ `PutProxyToSleep`（`:2991`）中，在清 `probePawn/probePending` 的同时，对归属站的 `StationRuntime` 调用 `ResetStationSearchState(runtime, CurrentTick)`。
2. **泵兜底**：`TryGetNextDueStation`（`:3095`）对满足 `nextSearchTick == int.MaxValue && !probePending && 本池无在场活跃代理` 的站做一次重置（避免"正处于探路等待中的代理"被误判）。
3. **放行 Designation 唤醒**：`NotifyDesignationAdded`（`:1862`）不再被 `probePending` 完全挡住（例如仅在 `probePawn` 仍然有效时才跳过）。

**对 MultiFloors 的影响：无**（它不产生 `abroadMap`，根本不走这条路径）。

### 9.3 修复 3（重做）—— 从"过滤 WorkGiver"改为"启动 job 时校验入口"

> **勘误**：早期版本建议"扩展 `OmniCrossLevelCompat.SkipForProxy`，按 `giverClass`/`defName` 剔除 RVAutoHome 的 giver"。
> 该写法**不采用**：依赖 mod 身份、且挡不住同类模式的其他 Mod。改用**能力判定**。

插入点：已有的 `Patch_OmniWorkProxy_TrackVanillaJobStart`（`Pawn_JobTracker.StartJob` Postfix，`:3576`）。

```
对代理刚启动的 job：
  homeMap = HomeMapOf(pawn)
  对 targetA / targetB / targetC 中的每一个 target：
      if (!target.IsValid || !target.HasThing) continue        // ★ MultiFloors 大量 target == null
      portal = target.Thing as MapPortal
      if (portal == null) continue                             // 非入口目标一律不拦
      dest = SafeGetTargetMap(portal)
      if (dest == null || dest == homeMap) continue            // 不跨图 → 不拦
      homeManager = homeMap.GetComponent<MapComponent_OmniWorkstation>()
      if (homeManager != null && !homeManager.AnyStationAllowsCrossMapWork())
          → 取消该 job + 记入 WorkFailures（与 EntryAuth 的硬拒绝语义一致）
      if (!OmniWorkProxyEntryAuthorization.IsAuthorized(homeMap, dest))
          → 取消该 job + 记入 WorkFailures
```

**为什么这对 MultiFloors 安全（关键论证）**：
MultiFloors 的跨层 job（`CrossLevelJobFactory.MakeChangeLevelThroughStairJob` → `JobDriver_ChangeLevelThroughStair`）**只会在 Postfix 的递归 `TryIssueJobPackage` 成功之后才被产出**，而递归成功本身就意味着 `IsAuthorized(homeMap, destMap)` 已为 `true` → 新规则**不会拦到它**。
此外 MultiFloors 大量调用形式是 `MakeChangeLevelThroughStairJob(null, map)`（target 为 null）→ 新规则第一步就 `continue`。

**为什么这能解决问题 2**：RVAutoHome 的 `EnterSimplePortal` 在代理位于房间图时启动，`dest = 基地图 ≠ 归属图` 且未授权 → 被取消。`allowCrossMapWork = false` 时代理**再也不会被送出房间**。

### 9.4 三项修复对 MultiFloors 的无害性论证（对照 4.7）

| 约束（4.7） | 修复 1 | 修复 2 | 修复 3 |
|---|---|---|---|
| `Stair*` 的 `SafeGetTargetMap` 返回值不变 | ✔ `exit==null` 时路径不变 | 不涉及 | ✔ 依赖同一函数 |
| 不把 ④ 提前 | ✔ 仅加 ② 的有效性条件 | 不涉及 | 不涉及 |
| 保留 `GetType() != typeof(MapPortal)` 保护 | ✔ 未动 | 不涉及 | 不涉及 |
| 保留 ④ 的 `try/catch` | ✔ 未动 | 不涉及 | 不涉及 |
| 容忍 target 为 null 的跨层 job | 不涉及 | 不涉及 | ✔ 显式跳过 |
| 不新增依赖 `pawn.Map` 的驻外判定 | 不涉及 | ✔ 只改 `nextSearchTick` | ✔ 用 `HomeMapOf`（读登记表） |

---

## 10. 回归验证清单

**编译**：`dotnet build -c Debug`（`AGENTS.md` 要求）。

**MultiFloors 主场景（最高优先级，验证"没被破坏"）**：
1. 基地图建工作站、开启跨图开关、楼梯在工作站半径内、目标小人 `StayOnCurrentMap()==false`。
2. 预期：代理可跨层取活，行为与修复前**完全一致**。
3. 建议加一次性日志或断点核对：`SafeGetTargetMap(StairEntrance)` 仍返回上层图；`IsAuthorized(homeMap, destMap)` 仍为 `true`。

**原版场景**：
4. 原版口袋图 / 坑道门（`exit` 已 spawn）→ 授权行为与修复前一致。
5. 无任何 `MapPortal` 的普通地图 → `SafeGetTargetMap` 行为不变。

**RV + SimplePortal 场景（验证缺陷 A 已修）**：
6. 工作站覆盖基地图上的 SimplePortal 入口 + 开启跨图 → 口袋图应被授权（`UncoveredPortals` 计数应下降）。
7. 关闭跨图 → 代理**不应**再被 RVAutoHome 送出房间（验证缺陷 A + C）。

**死锁回归（验证缺陷 B 已修）**：
8. 人为制造一次代理跨图（或在 RV 场景下重复现象 2），确认该工作站**能自行恢复**派发，而不是永久停摆。
9. 观测点：工作站详情窗口的 `SearchState` / `TicksUntilNextSearch` 不应长期停在 `Waiting` 且 `nextSearchTick == int.MaxValue`。

---

## 11. 未验证项与后续待办

**未验证（如实记录）**：

1. **未做游戏内复现**：全部结论来自反编译源码 + 静态代码路径分析。设计文档第 16 节第 12 条同样如此。
2. **现象 3 的完整归因未闭合**：缺陷 B 的代码路径已确证闭合，但"关闭自动上下车后从干净状态就完全不工作"这一情形**不能排除**另有口袋图特有原因（例如口袋图上目标扫描失败）。区分办法见第 10 节第 9 条，或把 `entryAuthStrict` 切到"仅警告模式"观察日志。
3. **`Stair` 的 `exit` 在读档后是否可能非 null**：从 `Stair.ExposeData` 只转调 `base.ExposeData()` 且从未赋值推断为 `null`，未跑存档验证。即便为非 null，修复 1 的 `MapHeld != null` 判定同样会让它落到 ④，结果仍一致。
4. **是否存在未 override `GetOtherMap()` 的 `Stair` 子类**：本报告确认了 `StairEntrance` / `StairExit` / `Elevator` 三者有实现；若还有其它子类，它在 ④ 会抛异常并被吞掉（现状亦然）。
5. **`pawn.StayOnCurrentMap()` 对代理的默认返回值**：从"MultiFloors 已能正常工作"反推为 `false`，未直接读实现。
6. **`UsePrioritizedScanner` 开启时**的扫描轮次行为（设计文档第 16 节第 4 条已列为待实测）。
7. **`ThingRequestGroup.MapPortal` 是否包含 `StairExit` / `Elevator`**（设计文档第 16 节第 9 条已列为待实测）。

**后续待办（建议顺序）**：

- [x] 实施修复 1 → `dotnet build -c Debug` 通过 → 仍待用第 10 节第 6/7 条做游戏内验证。
- [x] 实施修复 2 → 构建通过 → 仍待用第 10 节第 8/9 条验证。
- [x] 实施修复 3 → 构建通过 → 仍待用第 10 节第 1–3 条**重点回归 MultiFloors**。
- [ ] 把"未覆盖入口 / 可去地图"的界面反馈做扎实（设计文档 10.6），降低玩家在"开了开关却没反应"时的困惑。
- [ ] （可选）`Refresh` 中对"入口解析失败"加弱缓存，避免每个维护周期重复触发第三方 `GetOtherMap()` 的副作用。

---

## 12. 附录：证据索引

### 12.1 本项目（相对 `Source/FullyAutomaticOmniCrafter`）

| 主题 | 位置 |
|---|---|
| 入口授权 Prefix | `OmniWorkstation.cs:3676`（`Patch_OmniWorkProxy_EntryAuthorization`） |
| 授权表刷新 / 查询 | `OmniWorkProxyEntryAuthorization.cs:102`（`Refresh`）、`:51`（`IsAuthorized`）、`:31`（`StrictMode`） |
| **`SafeGetTargetMap`** | `OmniWorkProxyEntryAuthorization.cs:196` |
| 载体探测 | `OmniWorkProxyEntryAuthorization.cs:223`（`BuildCarrierGetter`）、`:183`（`IsCarrierAuthorized`） |
| 派发泵 | `OmniWorkstation.cs:1986`（`MapComponentTick`）、`:3095`（`TryGetNextDueStation`）、`:3142`（`WakeProxyForVanillaSearch`） |
| `nextSearchTick` 置位 / 重置 | `:2070`、`:2059`、`:1785` / `:1731`（`ResetStationSearchState`）、`:1846`、`:1793`、`:1645` |
| 驻外看护 | `:2625`（`MaintainAbroadProxies`）、`:2666`（`ReclaimAbroadProxy`）、`:2991`（`PutProxyToSleep`，清理段自 `:2996` 起） |
| job 生命周期补丁 | `:3525`（`DeactivateOnJobEnd`）、`:3576`（`TrackVanillaJobStart`）、`:3606`（`TrackMapChange`）、`:3624`（`TrackDeSpawn`） |
| giver 缓存补丁 | `:3456`（`AppendUnclassifiedWorkGivers`）、`:683`（`OmniCrossLevelCompat.SkipForProxy`） |
| 代理定义 | `Defs/ThingDefs_Buildings/OmniWorkstation.xml`（`FAOC_OmniWorkProxy`，`<race>Human</race>`） |
| 代理网格 | `Defs/PathGridDef/OmniWorkProxyPathGrid.xml`、`OmniWorkProxyNavigation.cs:16` |
| 归属权威表 | `OmniWorkProxyRegistry.cs:139`（`HomeMapOf`）、`:173`（`EnumerateForeign`） |
| 归属镜像 | `Hediff_OmniWorkProxyHome.cs` |

### 12.2 原版（rimsage `Source/…`）

| 结论 | 位置 |
|---|---|
| `IsColonistPlayerControlled` 定义（代理满足） | `Source/Verse/Pawn.cs:425` |
| `MapPortal.exit` 字段类型为 `PocketMapExit` 及其序列化 | `Source/RimWorld/MapPortal.cs:26`、`:119` |

### 12.3 MultiFloors（`3384660931`，反编译）

| 结论 | 位置 |
|---|---|
| `Stair : MapPortal`；`GetOtherMap()` 抛 `NotImplementedException` | `Stair.cs:13`、`:140` |
| `StairEntrance`（`GetOtherMap()` 会惰性生成目标层图） | `StairEntrance.cs:12`、`:48` |
| `StairExit` | `StairExit.cs:11`、`:105` |
| `Elevator : Stair` | `Elevator.cs:14`、`:294` |
| 跨层扫描 Postfix | `HarmonyPatches/HarmonyPatch_ScanJobsOnOtherLevel.cs:29-42`、`TryFindJob` `:74-115` |
| Prioritized 变体 | `HarmonyPatches/HarmonyPatch_ScanJobsOnOtherLevelPrioritized.cs:53`、`:63`、`:121` |
| `UnsafeTeleportTo` / `UnsafeTeleportBack`（**绕过 SpawnSetup/DeSpawn**） | `LevelUtility.cs:93`、`:113` |
| 跨层 job | `CrossLevelJobFactory`、`Jobs/JobDriver_ChangeLevelThroughStair.cs:9` |

### 12.4 SimplePortal（`3325512144`，反编译）

| 结论 | 位置 |
|---|---|
| `SimplePortal_Building : MapPortal`；`GetOtherMap()` = `linkedPortal.MapHeld` | `SimplePortal_Building.cs:12`、`:251` |
| **`ExposeData` 里的孤儿 `PocketMapExit`** | `SimplePortal_Building.cs:413` |
| `CompSimplePortal.GetOtherMap()`（未被本项目使用） | `CompSimplePortal.cs:324` |
| 卧室路由 / 唤醒返程补丁（被 RVAutoHome 默认压制） | `SimplePortal.HarmonyPatches/Patch_SimplePortal_RouteToBedroom.cs`、`Patch_SimplePortal_ReturnOnWake.cs` |

### 12.5 RVwithPD（`3342334887`，反编译）

| 结论 | 位置 |
|---|---|
| `InteriorSpaceMapComponent : CustomMapComponent`；`ownerThing` 字段 | `InteriorSpaceMapComponent.cs:12`、`:18` |
| 内部图生成（全部地形 + 边界墙） | `GenStep_InteriorSpace.cs` |

### 12.6 RVAutoHome（`3801427298`，反编译 + Defs）

| 结论 | 位置 |
|---|---|
| `WorkGiver_TendRoom`（`HasJobOnThing` / `JobOnThing` 的 `MapHeld == roomMap` 分支） | `WorkGiver_TendRoom.cs` |
| 挂在各 vanilla workType 上的 WorkGiverDef | `1.6/Defs/WorkGiverDefs/RVAutoHome_WorkGivers.xml` |
| `CanEnterForWork` / `ReadyToDecide` | `HomeDecision.cs:58`、`:795` |
| `IsAutoHomeWorker`（`IsColonistPlayerControlled` 直接放行） | `HomeUtility.cs:452` |
| 压制 SimplePortal 两个补丁 | `Core.cs:144-150` |
| 需求路由补丁（`JobGiver_GetFood/GetRest/GetJoy/Meditate`） | `Patch_JobGiver_Get{*}_RouteToRoom.cs` |
| 跨图 job 定义 | `1.6/Defs/JobDefs/RVTransport.xml`、`SimplePortalDefOf.EnterSimplePortal` |

---

## 13. 实施进度（代码实况）

**状态**：修复 1、2、3 均已落地，`dotnet build -c Debug` 通过（0 警告 0 错误）。
**游戏内回归尚未执行**（见本节末）。

### 13.1 修复 1 —— `SafeGetTargetMap` 第 ② 步

文件：`OmniWorkProxyEntryAuthorization.cs`

```csharp
// 改前
if (portal.exit != null) return portal.exit.MapHeld;
// 改后
PocketMapExit originalExit = portal.exit;
if (originalExit != null && originalExit.MapHeld != null) return originalExit.MapHeld;
```

第 ①③④ 步、判定顺序、`GetType() != typeof(MapPortal)` 类型保护、`try/catch` **全部未动**。
→ SimplePortal 的孤儿 `PocketMapExit` 不再短路，可落到 ④ `GetOtherMap()`；MultiFloors 的 `exit == null` 路径行为完全不变。

### 13.2 修复 2 —— 解锁 `nextSearchTick` 单向门

文件：`OmniWorkstation.cs`

- **2a `PutProxyToSleep`**：站点清理段改为无条件取 `StationRuntime`，并新增

  ```csharp
  if (runtime.nextSearchTick == int.MaxValue)
      ResetStationSearchState(runtime, CurrentTick);
  ```

  仅在"仍停在单向门"时解锁，正常宽限退避值不受影响。覆盖 `ReclaimAbroadProxy`（驻外回收）等全部入舱路径。

- **2b `TryGetNextDueStation`**：新增"探针已失效"兜底解锁 + 辅助方法

  ```csharp
  private static bool IsDeadProbe(Pawn probe)   // 已销毁，或不在 registry 登记表中
  ```

  覆盖"代理被第三方删除/吞掉、`PutProxyToSleep` 没机会执行"的极端情形。只按探针失效精确识别，不改变正常的等待与退避节奏。

### 13.3 修复 3 —— 入口型 Job 的启动期校验

文件：`OmniWorkProxyEntryAuthorization.cs`、`OmniWorkstation.cs`

- 新增 `OmniWorkProxyEntryAuthorization.DeniesEntryJob(Pawn, Job)` 与私有 `DeniesEntryTarget(Map, LocalTargetInfo)`：
  遍历 `targetA/B/C`，`target` 无效或无 `Thing` 时直接跳过（兼容 MultiFloors 的 `target == null` 形式）；
  命中 `MapPortal` 后按与 EntryAuth Prefix **相同的顺序**判定（先 `AnyStationAllowsCrossMapWork()`，再 `IsAuthorized`）。
  该文件同时补了 `using Verse.AI;`（`Job` 与 `LocalTargetInfo` 所在命名空间）。
- `MapComponent_OmniWorkstation` 新增 `CancelUnauthorizedEntryJob(Pawn)`：保存 `record.station` → `EndCurrentJobForManagement` → 清 `Active` → `PutProxyToSleep`（内含 2a 的解锁）→ **给该站退避**（`consecutiveFailures++` + `ExhaustedBackoffTicks`）。
  **不调用 `WakePumpNow`**：否则泵下一 tick 就会重新派发，而 RVAutoHome 的 giver 目标固定是同一座入口，会形成"唤醒→取消"的高频抖动。
- `Patch_OmniWorkProxy_TrackVanillaJobStart` 的 Postfix 中，于 `IsForbiddenGearJob` 分支之后接入该判定。

**对 MultiFloors 的安全性**：它的跨层 Job 只在"递归 `TryIssueJobPackage` 已获授权"之后才产出，到达 `StartJob` 时 `IsAuthorized` 必为 `true`，不会被拦；`target == null` 形式则由第一步跳过。

### 13.4 尚未执行（后续接手请照做）

1. **游戏内回归**：按第 10 节清单，重点是**第 1–3 条 MultiFloors 主场景**（确认跨层工作与修复前一致）。
2. **观察日志**：把 `entryAuthStrict` 切到"仅警告模式"可看到 `[OmniWorkstation] proxy work denied on unauthorized map`，用于确认修复 3 真的拦下了 RVAutoHome 的 `EnterSimplePortal`。
3. **第 13.3 节的已知局限**：RVAutoHome 这类"入口型 giver"仍会被周期性选中并取消（目标固定，无法在 giver 层屏蔽）；退避使节奏与"本站暂时无活"一致，代理不会被带出归属图，但**不会**真正去执行那份工作 —— 这符合"未授权就不该跨图"的语义。
   若后续希望更彻底地消除这次空转，可考虑在 `Pawn_WorkSettings.CacheWorkGiversInOrder` 的 Postfix 里、当 `!station.AllowCrossMapWork` 时剔除 `PotentialWorkThingRequest.group == ThingRequestGroup.MapPortal` 的 giver（已确认 `MapPortal == 76`）；这会**沉默禁用**第三方的入口型 giver，属于需要玩家/维护者拍板的取舍，故本次**未**实施。

