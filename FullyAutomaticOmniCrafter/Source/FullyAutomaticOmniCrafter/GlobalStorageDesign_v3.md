# 超维存储 · 万能取用方案 v3（“半 Spawned”投影）

> 本文档记录一种**非逐条 patch** 的通用取用接入方案，用于解决超维存储仓（vault）中的物品
> 对“直查 `listerThings` / `thingGrid` 的拿取路径”不可见的问题。
>
> 核心结论：RimWorld 中“查询索引”（`ListerThings`）与“物理存在”（`thingGrid`）是**两套独立注册系统**，
> 可以只注册前者、不注册后者，从而得到一种“**查询可见、物理隐形**”的半 Spawned 状态。
> 这才是满足“任意拿取路径都能拿到 vault 物品，但不真正落地、也不逐条 patch 拿取逻辑”的突破口。

---

## 1. 背景与问题

### 1.1 现状

vault 的物品被吸收进全局层（`GameComponent_OuterrealmStorage`），每个建筑只持有**未 Spawned 的视图副本**
（`OuterrealmVaultViewThingOwner` 中的投影 `Thing`）。

原版中，拿取物品的路径分两类：

1. **走 `IHaulSource` / `IThingHolder` 接口**（工作台原料、搬运、穿戴/装备、账单计数等）——vault 已实现这些接口，
   天然可见，无需处理。
2. **直查地面索引**：`map.listerThings.ThingsOfDef(def)` / `ThingsMatching(request)` / `GenClosest.ClosestThingReachable` 等。
   这些路径只认 **Spawned 且已注册进 `listerThings`/`thingGrid`** 的物品，vault 副本不在其中 → 不可见。

### 1.2 为什么不能逐条 patch

直查地面的拿取路径数量极大，且第三方 mod 会无限增加，逐条适配不可穷尽。
之前否决“直接 patch `ThingsOfDef`”的理由是：返回未 Spawned 副本会污染所有需要“真实、可寻路、可交互物品”的调用者。
本方案改为在**更底层的注册边界**上做文章，而非在查询结果里临时塞假物品。

---

## 2. 核心洞察：`ListerThings` 与 `thingGrid` 是分离的

### 2.1 源码依据

`Verse/Thing.cs` 的 `SpawnSetup` 里，两套注册是**分开的独立调用**：

```csharp
map.listerThings.Add(this);                 // ① 进入“按 def / group 的查询索引”
map.thingGrid.Register(this);               // ② 进入“按格子的物理存在”
...
map.dynamicDrawManager.RegisterDrawable(this); // ③ 动态渲染列表（仅 drawerType != MapMeshOnly）
```

而 `Verse/ListerThings.cs` 的 `Add` **只做索引，无任何物理副作用**：

```csharp
public void Add(Thing t)
{
    if (!EverListable(t.def, use)) return;
    listsByDef[t.def].Add(t);                    // ThingsOfDef / ThingsMatching 查这里
    if (t is IHaulSource) haulSources.Add(t);
    foreach (group) if (GroupIncludes(t, group)) listsByGroup[group].Add(t);
    // 不碰 thingGrid、不渲染、不触发美观/爆炸/点击
}
```

`ListerThings.ThingsOfDef` / `ThingsInGroup` 都收敛到 `ThingsMatching`，而 `ThingsMatching` 读的就是
`listsByDef` / `listsByGroup` 这两个索引。

> 结论：**把副本 `Add` 进 `map.listerThings`（进 `listsByDef` + 所有 `listsByGroup`），但不 `Register` 进 `map.thingGrid`，
> 并在 `Map.ExposeData` 存档时临时摘除、存档后加回。这样“按 def / 按 group 的查询”都能看到副本，
> 而“渲染/美观/爆炸/点击/存档”都不感知它。**

---

## 3. 方案设计

### 3.1 目标状态

对 vault 视图副本维护“半 Spawned”状态：

- **按 def / 按 group 查询均可见**：进 `listsByDef` + 所有 `listsByGroup`，`ThingsOfDef` / `ThingsInGroup` / `GenClosest` 全局兜底都能找到副本。
- **物理隐形**：不进 `thingGrid`、不进 `dynamicDrawManager`、不进 region lister（渲染/美观/爆炸/点击不感知）。
- **存档隐形（动态摘除）**：`Map.ExposeData` 存档时临时从 `listerThings` 摘除，存档后加回，避免被 `AllThings` 遍历保存。
- **可寻路**：寻路目标是副本的 `PositionHeld`（经 `ParentHolder` 解析到 vault 建筑位置）。
- **拿取即物化**：任何路径走到副本并执行 `SplitOff` / `TryStartCarry` 时，命中已有的物化 patch。

### 3.2 三个改动点

1. **维护副本的查询索引（进 `listsByDef` + 所有 `listsByGroup`）**
   - 物化副本处（`OuterrealmVaultViewThingOwner.EnsureCopyFor` / `RebuildView` 中 `base.TryAdd` 成功处）
     调用 `Vault.MapHeld.listerThings.Add(newCopy)`（走原版 Add，进 `listsByDef` + 所有 group）。
   - 新增 `Patch_Map_ExposeData`：`Saving` 时 Prefix 把本地图所有 vault 副本从 `listerThings` 移除（不进 `AllThings`，
     不被存档），Postfix 加回（Harmony 的 Postfix 在 finally 语义下执行，异常时也会恢复）。
   - 新增 `Patch_Thing_DrawGUIOverlay` + `Patch_ThingWithComps_DrawGUIOverlay`：屏蔽副本的堆叠数字 overlay。
   - 统一移除处（`OuterrealmVaultViewThingOwner.Remove(Thing)` 里）调用 `Vault.MapHeld.listerThings.Remove(item)`。
   - 副本 `Spawned == false`，因此 `Thing.Position` 的 setter 只改写 `positionInt`、不触发 `thingGrid.Deregister/Register`；
     物化时将 `copy.positionInt` 设为 `Vault.InteractionCell`（保证任何直接读 `Position` 的代码不 NRE）。

   **存读档两条硬性要求（必须满足）：**
   - `listerThings.Add` 只挂在“物化成功处”（`EnsureCopyFor` 与 `RebuildView` 的 `base.TryAdd` 成功之后），
     不要挂在 `RebuildView` 的“仅更新数字”分支——否则同一次重建会重复 Add（`ListerThings.Add` 不幂等，
     会在 `listsByDef`/`listsByGroup` 产生重复项）。
   - `listerThings.Remove` 挂在 `Remove(Thing)` 统一入口，且必须在 `base.Remove` 之前执行；`ClearView`（DeSpawn / minify）
     逐个 `Remove` 时即自动清理索引，避免残留指向已 `Destroy` 副本的引用。
   - `Patch_Map_ExposeData` 只处理 `Scribe.mode == Saving`，且按 `v.MapHeld == __instance` 过滤本地图 vault，
     避免跨地图误摘/误加。

2. **补齐 `ClosestThing_Global_Reachable` 的 Spawned 豁免（Postfix 兜底，非 Transpiler）**
   - `Verse/GenClosest.cs` 里两条路径对未 Spawned 物品的容忍度不一致：
     - `ClosestThing_Global`（`ClosestThingReachable` 的全局兜底）：
       `if (!t.Spawned && !HaulAIUtility.IsInHaulableInventory(t)) return;` —— **豁免未 Spawned 但处于 HaulSource 容器的物品**。
     - `ClosestThing_Global_Reachable`（染料/血包等少数路径用）：
       `if (t == null || !t.Spawned) return;` —— **硬性拒绝**。
   - 该硬性检查位于编译器生成的局部函数 `Process`（display class 实例方法）内部，`Transpiler` 无法稳定定位
     `Thing.get_Spawned` 调用点。因此改用 **Postfix 兜底**：原方法返回 `null` 时，遍历 `searchSet` 中
     “未 Spawned 但 `IsInHaulableInventory`”的副本，复刻其可达性 + validator + priority 判定并回填 `__result`。
   - **一处 patch，覆盖所有走 `ClosestThing_Global_Reachable` 的路径。**

3. **复用已有的拿取物化 patch**
   - `Patch_Thing_SplitOff` 与 `Patch_Pawn_CarryTracker_TryStartCarry` 已对
     `holdingOwner is OuterrealmVaultViewThingOwner` 的副本做 Boost → SplitOff → 入 carry 的物化，
     并即时同步全局数量。副本进入 `listerThings` 后，任意拿取路径走到它、执行 `SplitOff`/`TryStartCarry` 时即自然命中。

### 3.3 为什么未 Spawned 副本能通过寻路

- `HaulAIUtility.IsInHaulableInventory(thing)` = `!thing.Spawned && thing.ParentHolder is IHaulSource`
  （`Verse/AI/HaulAIUtility.cs`）。
- `Thing.ParentHolder => holdingOwner?.Owner`（`Verse/Thing.cs`），vault 副本的 `holdingOwner` 是
  `OuterrealmVaultViewThingOwner`，其 `Owner` 是 `Building_OuterrealmVault`（实现 `IHaulSource`）。
  → `IsInHaulableInventory(副本) == true`，天然满足 `ClosestThing_Global` 的豁免。
- `LocalTargetInfo.Cell => thingInt != null ? thingInt.PositionHeld : cellInt`（`Verse/LocalTargetInfo.cs`），
  `PositionHeld` 经父容器解析到 vault 建筑位置 → pawn 自动走到建筑旁拿取。

---

## 4. 副作用验证矩阵

| 系统 | 遍历来源 | 副本是否可见 | 结论 |
|---|---|---|---|
| `ThingsOfDef` / `ThingsMatching(ForDef)`（按 def 查询） | `listsByDef` | ✅ 可见 | 目标行为 |
| `ThingsInGroup` / `ThingsMatching(ForGroup)`（按 group 查询） | `listsByGroup` | ✅ 可见 | 目标行为（食物/药/毒品/书等） |
| `GenClosest.ClosestThingReachable`（全局兜底） | `listerThings.ThingsMatching` | ✅ 可见 | 目标行为 |
| 存档 `Map.ExposeData`（遍历 `AllThings` 保存） | `listerThings.AllThings` | ⏸ 存档时摘除 | **Patch 摘除后不被保存**（修复“读档后物品落地”） |
| 堆叠数字 `ThingOverlays`（HasGUIOverlay） | `listerThings.ThingsInGroup(HasGUIOverlay)` | ✅ 可见 | 由 DrawGUIOverlay patch 屏蔽 |
| 美观 `BeautyUtility` | `map.thingGrid.ThingsListAt(c)` | ❌ 不可见 | 无美观污染 |
| 爆炸伤害 `DamageWorker` | `map.thingGrid` | ❌ 不可见 | 不会被炸 |
| 爆炸 `Notify_Explosion` | region 的 `ListerThings.AllThings`（且判 `Spawned`） | ❌ 不可见 | 不受影响 |
| 渲染（动态 / 静态网格） | `dynamicDrawManager.drawThings` / `thingGrid` | ❌ 不可见 | 不渲染 |
| 点击选中 | `thingGrid.ThingsAt` | ❌ 不可见 | 不可选中 |

> 依据：`Map.ExposeData` 的 Saving 分支遍历 `listerThings.AllThings` 并 `Scribe_Deep.Look` 保存不可压缩 Thing——
> 副本通过 `Patch_Map_ExposeData` 在 Saving 时被临时摘除，故不进 `AllThings`、不被保存。
> `RimWorld/BeautyUtility.cs` 使用 `map.thingGrid.ThingsListAt(c)`；`Verse/Explosion.cs` 的
> `RegionTraverser` 遍历 region `ListerThings.AllThings` 且 `if (allThings[index].Spawned)`，
> 对物品的直接伤害走 `thingGrid`。副本不进 region lister、不进 thingGrid、未 `RegisterDrawable`，故均不受影响。

---

## 5. 残余边界与风险（待实测）

1. **regionwise 短路**：`ClosestThingReachable` 先做 region 就近搜索（region lister，副本不在其中），
   只有“遍历满 `searchRegionsMax`（默认 30）仍未命中”才会进入全局兜底。极小地图或 pawn 处于角落时，
   region 数可能不足 30，导致 regionwise 遍历完全部 region 后提前终止而**不触发全局兜底**，从而偶尔找不到副本。
   这是原版对容器内物品的既有行为，非本方案新引入，但需评估是否需要额外处理。

2. **发狂破坏天然免疫（无需 patch）**：`TantrumMentalStateUtility.GetSmashableThingsNear` 遍历的是
   **region lister**（`r.ListerThings.ThingsInGroup(HaulableEver)`），而副本不进 region lister（未 `RegisterInRegions`）；
   且其 `CanSmash` 有硬性 `if (thing.Destroyed || !thing.Spawned || ...) return false;`。双重保障下，
   副本不会被发狂 pawn 当作破坏目标。爆炸同理（region lister + thingGrid，副本均不在）。

3. **存档/读档一致性（Patch_Map_ExposeData）**：副本进 `AllThings`，故必须由 patch 在 `Saving` 时摘除、存档后加回；
   `Postfix` 走 finally 语义，异常时也会恢复。需验证：多地图存档时按 `v.MapHeld == __instance` 过滤正确、
   读档后 `RebuildView` 重新物化并重建索引（`Remove` 统一入口保证无残留、无重复）。

4. **读档时序**：副本索引在 `SpawnSetup → RebuildView` 阶段重建，需确保读档后 `listerThings` 的增删
   与视图生命周期严格同步（增在物化处、删在 `Remove` 统一入口），避免索引残留或重复。

---

## 6. 实现步骤（建议顺序）

1. 在 `OuterrealmVaultViewThingOwner` 增加“查询索引维护”：
   - 物化副本成功处 `MapHeld.listerThings.Add(copy)` + 设置 `positionInt = Vault.InteractionCell`（幂等防御：Add 前用 `ThingsOfDef(def).Contains(copy)` 判重）；
   - `Remove(Thing)` 里在 `base.Remove` 之前 `MapHeld.listerThings.Remove(copy)`；
   - `ClearView` / `RebuildView` / `SyncKey` 等路径统一经 `Remove`，索引自动一致；
   - 读档后 view 为空、`RebuildView` 走物化分支，索引随物化自动重建（见 §3.2 存读档要求）。
2. 新增 `Patch_GenClosest_ClosestThing_Global_Reachable`（Postfix 兜底），在原方法返回 `null` 时把
   “未 Spawned 但 `IsInHaulableInventory`”的 vault 副本按可达性/validator/priority 回填进 `__result`。
3. 用现有拿取闭环（`JobDriver_VaultTakeToInventory` / `JobDriver_VaultDeliverResources` / 工作台原料）
   做回归，确认 Boost/SplitOff/预留记账未被索引改动破坏。
4. 专项验证 §5 的三类残余风险（regionwise 边界、`AllThings` 遍历、haul group 误判）。
5. 若 regionwise 边界影响明显，再评估是否对 `ClosestThingReachable` 增加“强制全局兜底”的最小 patch。

---

## 7. 与既有方案的取舍对比

| 方案 | 覆盖范围 | patch 面 | 副作用 |
|---|---|---|---|
| 逐条 patch 拿取路径 | 有限，第三方 mod 不可穷尽 | 大 | 每路径定制 |
| 真 Spawned 前台缓冲 + 自动补货 | 全部（任意拿取） | 小（保护缓冲堆语义） | 物理落地、占格、有限堆叠 |
| **本方案（半 Spawned 投影）** | 极广（所有走 `listerThings`/`GenClosest` 的路径） | **极小**（索引维护 + 1 处 GenClosest Postfix 兜底 + 已有物化） | 无渲染/美观/爆炸/点击副作用；残余见 §5 |

本方案保留了 vault 的“不落地、无限容量、冻结、跨地图”特性，同时把“被任意拿取路径发现”的兼容面最大化，
是“既不真 Spawned、又不逐条 patch”这一约束下的最优解。
