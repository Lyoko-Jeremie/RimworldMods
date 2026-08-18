# 超维存储仓「存储格接口 + 预留驱动」方案设计（v4）

> 状态：设计评审稿（未实施）
> 关联问题：Raven Mod 休闲 job「started 10 jobs in one tick」循环（第三方消费方把 vault 未 Spawned 副本当普通物品）
> 原版机制引用：rimsage 查证 Assembly-CSharp 1.6.9438 源码

---

## 1. 背景与问题根因

Raven Mod 的休闲 job（`Raven_Job_PlayWithFluid`）把 vault 里的渡鸦养乐多当作玩耍目标后，1 tick 内循环 10 次报错。根因链：

1. vault 视图副本是**未 Spawned** 的 Thing（半 Spawn 投影：注册进 `listerThings` 但不在 `thingGrid`/渲染）；
2. `Patch_GenClosest_ClosestThing_Global_Reachable` Postfix 在 `__result == null` 时把未 Spawned 副本返回给任意调用方；
3. Raven 的 `JoyGiver_InteractBuilding.FindBestGame` 拿到副本 → `TryGivePlayJob` 通过（`CanReserveAndReach` 经 vault 分支放行）→ 派 job；
4. job 第一个 toil 的 `FailOnDespawnedNullOrForbidden(A)` 检查 `DespawnedOrNull` → **未 Spawned → 立即 Incompletable 失败** → 循环。

**本质矛盾**：半 Spawn 副本"可见但不可交互"。本 Mod 内部已在穿戴路径踩过同一坑（见 `Building_OuterrealmVault.cs` IApparelSource 注释：Mia started 10 jobs in one tick），当时的解法是给适配消费方加专用接口（IApparelSource），治标不治本——未适配的第三方（Raven）依然会踩。

## 2. 设计目标（v4 修订版）

1. 根治"未 Spawned 副本被第三方消费"这一类问题；
2. **默认全部物品在全局层**（超维空间），不被世界看见；
3. **需要预留时补货**：job 对物品发起 `Reserve` 时，将对应数量物化为**真 Spawned** 物品放到存储格；
4. **取消预留时自动回收**：`Release` 时格上剩余物自动收回全局层；
5. **filter 同时控制可见性与存储格可放置性**；
6. **外来物品放置到存储格且未被预留 → 自动回收进全局层**；
7. 存储格容量：`maxItemsInCell = 255`。

## 3. 原版机制确认（rimsage）

| 机制 | 结论 | 出处 |
|---|---|---|
| 存储格物品 Spawned 状态 | 真 Spawned：`SlotGroup.HeldThings` 从 `map.thingGrid.ThingsListAt(cell)` 取 | RimWorld/SlotGroup.cs |
| 每格多物品堆 | `Building.MaxItemsInCell = def.building.maxItemsInCell`；`GetMaxItemsAllowedInCell(cell) = 格上 edifice.MaxItemsInCell`；一格可叠多堆（`ThingsListAt` 返回列表） | Verse/Building.cs、RimWorld/BuildingProperties.cs、Verse/GridsUtility.cs |
| 存储格来源 | `ISlotGroupParent.AllSlotCells()`；`Building_Storage` = `GenAdj.CellsOccupiedBy(建筑占格)` | RimWorld/Building_Storage.cs |
| **Spawn 与容器脱离** | `GenSpawn.Spawn(Thing)` 在 Spawn 前**自动执行 `newThing.holdingOwner.Remove(newThing)`** → 已挂容器的副本可被直接 Spawn，脱离 view 容器 | Verse/GenSpawn.cs:164 |
| 空间余量 | `SpaceRemainingFor = maxItemsInCell × 面积 − 已持有堆数` | RimWorld/Building_Storage.cs:118 |
| 每格上限 | 格上 edifice 决定（vault 设 255 即每格 255 堆） | Verse/GridsUtility.cs:210 |

**vault 现状**：已实现 `IHaulDestination` / `IStoreSettingsParent` / `IHaulSource` / `IStorageGroupMember` / `IHaulEnroute`，Def `size(2,1)`、`PassThroughOnly`、`storageGroupTag FAOC_Vault`；已有 `Patch_ReservationManager_Reserve/Release/ReleaseAllForTarget/...`（预留挂点已具备）。**缺 `ISlotGroupParent`（物理存储格）**。

## 4. 架构：全局层 + 存储格「交互端口」（预留驱动）

```
┌────────────────────────── 超维空间（全局层，唯一真相，默认状态） ─────────────────────────┐
│ entries[]：每类物品一条（Proto + long Count）；无限容量、冻结、跨建筑/跨地图共享              │
└──────────────────────────────────────────────────────────────────────────────────────┘
        ▲ 回收（Release：剩余量 Deposit）          │ 物化（Reserve：Withdraw → 真 Spawn 到格）
        │                                          ▼
┌────────────────────────── 存储格（ISlotGroupParent，每个 vault 一份） ────────────────────┐
│ 格子 = 建筑占格 2×1 × maxItemsInCell=255（每格最多 255 堆，总容量 510 堆）                  │
│ 格上物品 = 真 Spawned（thingGrid/listerThings/渲染）——「预留中的物品」临时驻留               │
│ 外来放置且未预留的物品 → 自动 Deposit 吸收回全局层                                          │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

- **存储格 = vault 与世界的交互端口**：取出的物品经此物化（真 Spawned，第三方 job 可正常交互）；存入的物品经此吸收。
- **默认不可见**：未预留的物品不在格子上、不在 listerThings（全局层物品对地图查询不可见）→ 第三方（Raven）选不到未预留物品。
- **预留即物化**：job 的 `Reserve` 使对应数量成为真 Spawned 物品 → `FailOnDespawned` 检查通过 → 正常执行 → **循环根治**。

## 5. 核心机制设计

### 5.1 存储格布局
- vault 实现 `ISlotGroupParent`：`AllSlotCells()` 返回 `GenAdj.CellsOccupiedBy(this)`（2×1 建筑占格）。
- **255 为最大数量**（每格最多 255 堆、两格共 510 堆同时驻留），是真实容量上限。**实现**：`Building_OuterrealmVault` override `MaxItemsInCell => 255`（Def XML 在写根限制外未改；原版 `GetMaxItemsAllowedInCell` 读 `edifice.MaxItemsInCell`，效果等同）。
- **性能立场（用户决策）**：因预留而驻留存储格的物品为**短暂存在**（Reserve → Spawn → job 使用 → Release 回收），短时间数量多（含接近 255 的极端并发）不构成持续性能负担，**不做并发预留限制**。
- 建筑 `PassThroughOnly` + Shelf 贴图 → 物品渲染在格子上。

### 5.2 物化（Reserve 时补货）
- **挂点**：`Patch_ReservationManager_Reserve` 的 vault 分支（Prefix，reserve 成功判定后）。
- **流程**（借出副本语义，记账延续）：
  1. 目标为 vault 视图副本（`holdingOwner is OuterrealmVaultViewThingOwner`）且全局有货；
  2. 在视图内把该副本标记为**「已借出」**（`borrowedCopies` 集合登记，防 SyncKey/RebuildView 误重建/误清理）；
  3. `GenSpawn.Spawn(副本, 存储格, map)` —— 自动从 view 容器移除（GenSpawn.cs:164）并成为真 Spawned 物品；
  4. **目标一致性**：job 的 `targetA.Thing` 仍指向该副本（同一实例）→ toil 的 `FailOnDespawned` 检查 `Spawned == true` → **通过**。
- **记账延续**：副本虽 Spawned，但仍是「view 的借出副本」→ 现有 `Patch_Thing_SplitOff/TryAbsorbStack` 的判定条件从 `holdingOwner is view` **扩展为「holdingOwner is view 或 borrowedCopies 包含」** → 消耗差额照常扣全局（如 Raven 玩耍 `SplitOff(1).Destroy()` → 差额 1 → `gs.Subtract(1)`）。
- **多份并发**：同一条目被多次 Reserve（多 pawn 同时取用）→ 每次借出时若原副本已借出，则 `Withdraw` 物化**额外临时副本**（真物品）到格子并登记；Release 各自回收。副本容量 = 条目 stackLimit 或 255 约束内。
- **性能立场**：物化物品为**短暂驻留**（用完即回收），峰值堆数 = 并发预留数（极端可达 255 上限），因驻留时间短不构成持续性能负担；**不设并发预留限制**。

### 5.3 回收（Release 时自动回收）
- **挂点**：`Patch_ReservationManager_Release / ReleaseAllForTarget / ReleaseClaimedBy / ReleaseAllClaimedBy` 的 vault 分支（Postfix，reservation 已释放后）。
- **流程**：
  1. 该条目在格子上仍有**借出副本/临时副本**（未被取走/未销毁）→ 剩余 `stackCount` 存入全局（`gs.Deposit`，自动 DeSpawn + 并入条目）→ 副本销毁；
  2. 条目仍存在 → 重建半 Spawn 锚点副本回 view（`EnsureCopyFor`，恢复 listerThings 可见）；
  3. 借出登记清除。
- **语义**：job 用完即还，**物品不外流**（vault 现有"内容保留全局层"行为保持）。

### 5.4 存入（外来物品放置 → 自动吸收）
- vault 实现 `ISlotGroupParent` 后 haul 存入自动变格子型（原版 `HaulToStorageJob` 对 `ISlotGroupParent` 走 `HaulToCellStorageJob`）：hauler 把物品**真 Spawn 到存储格**（物品架语义）。
- **每物品吸收倒计时（§v4 实现）**：物品落格 → `Notify_ReceivedThing` 登记 `absorbTimers[物品] = TicksGame + 15`（0.25s）→ 到期且未预留/filter 允许 → `Deposit` 吸收进全局层。
  - 分散吸收时机（无统一 60 tick 批处理）；竞争窗口仅 0.25s（第三方选中前即吸收）；haul 放置 toil 先完成（不误判失败）。
  - 到期时被预留使用 / filter 已禁止 / 物品被取走 → 清理登记（物品归游戏/玩家，不强制吸收）。
- 兜底：异常路径（读档后无登记、绕过钩子）由 vault `Tick`（**rare tick，250 tick ≈ 4s**）扫格子吸收未登记物品。
- **预留中的物品不会被误吸收**（借出登记排除）。
- 物品架式交互：玩家也可手动把物品放到格子上（与 haul 一致）。

### 5.5 filter 语义（可见性 + 可放置性统一）
- `CanShow(t)`（现有，filter 判定）**同时**控制：
  - 全局层可见性（UI/计数、是否物化锚点副本）；
  - 存储格可放置性（`Accepts`：filter 禁止的物品不可 haul 入格子、不可被 Reserve 物化）；
  - 冻结时恒 false（现状保留）。

### 5.6 半 Spawn 锚点（保留，作为可预留目标）
- 副本（半 Spawn，listerThings 投影）**继续存在**——它是 `FindBestGame`/`CanReserve`/`Reserve` 的目标锚点，让 job 能"找到"并预留 vault 物品；
- **与"默认全部在全局"不矛盾**：锚点只是"可预留标记"（数量仍在全局层），物品实体默认不在世界；
- listerThings 注册 + `Patch_GenClosest_ClosestThing_Global_Reachable` Postfix **保留**（锚点发现机制）。

## 6. 兼容矩阵

| 现有功能 | 迁移方式 |
|---|---|
| Raven 等第三方休闲 job | 副本锚点可见 → `Reserve` 物化 → 真 Spawned → 正常执行；Release 回收 → **循环根治且不外流** ✓ |
| 取食/取药/选料/穿戴（我们适配路径） | 沿用"全局直取/借出"逻辑；物化后目标为真 Spawned，现有 view 分支扩展 `borrowedCopies` 判定即可，回归面小 |
| `Patch_Thing_SplitOff/TryAbsorbStack` | 判定条件扩展（`holdingOwner is view` **或** 借出登记） |
| `Patch_ReservationManager_Reserve/Release*` | 增加物化/回收逻辑（挂点已存在） |
| 交易计数/`ResourceCounter`/存档 | 不变（半 Spawn 锚点体系保留） |
| 存储组（`storageGroupTag FAOC_Vault`） | 不变；vault 增加 `ISlotGroupParent` 后格子参与 `SlotGroup` 语义 |

## 7. 边界情况

| 场景 | 处理 |
|---|---|
| 多 pawn 同时预留同一条目 | 借出原副本 + `Withdraw` 临时副本；各 Release 独立回收 |
| job 中断/取消（Release 异常路径） | 借出副本仍在格子 → 由 `ReleaseAllForTarget/ReleaseClaimedBy` 等兜底回收；视图重建（RebuildView）时清点格子回收残留 |
| 借出副本被摧毁（爆炸/火焰） | 普通物品行为；`Destroyed` 时经现有 Notify 链扣全局（差额=全量）→ 无残留 |
| 存储格满（255×2 堆） | 拒绝新物化（Reserve 时若无空位：暂缓/失败，全局仍可直取）；`maxItemsInCell` 可调 |
| vault 拆除/打包 | 格上借出副本 → 剩余量 Deposit 回全局 → 锚点清理（与现有 `ClearView` 合并） |
| 读档 | 借出副本是真 Spawned 存档实体；`borrowedCopies` 登记不序列化，读档后经视图重建清点格子重建状态 |
| filter 变更（禁止某条目） | 借出副本等待 Release 回收（退休语义，现有 `IsReserved` 保护）；未借出的锚点按现有 SyncKey 移除 |
| 性能 | 物化物品短暂驻留（Reserve/Release 驱动，峰值 = 并发预留数，上限 255）；存入经 `Notify_ReceivedThing` 实时吸收防**长期**堆积；**无并发预留限制** |

## 8. 工作量与风险

- **工作量**：中（相比"常驻陈列"版更小——不需要常驻补货轮询）。
  - P0：`ISlotGroupParent`（255）+ 物化（Reserve 借出 Spawn）+ 回收（Release Deposit）+ 外来吸收（格子型 HaulDestination + Tick 检测）（约 400~600 行，含 Def 修改）。
  - P1：`borrowedCopies` 记账延续（SplitOff/TryAbsorbStack 判定扩展）+ 多份并发 + 边界（约 200~400 行）。
  - P2：拆除/读档/视图重建兜底 + 回归测试（取食/取药/选料/穿戴/Raven 场景）。
- **风险**：
  1. **job.targetA 与 reservation 目标的一致性**：借出副本方案下 targetA 不变（同一实例），但 **reservation 目标=副本、物化后副本 Spawned**——原版 reservation 对 Spawned 目标的处理需回归验证；
  2. 格子型 HaulDestination 改造影响「存入」路径（HaulToContainer → HaulToCell），回归面中等；
  3. 极端并发（峰值接近 255 堆/格）的瞬时开销与同格渲染重叠：属短暂驻留，接受（用户决策）；若实测异常可后续调整 `maxItemsInCell`。

## 9. 结论

「存储格 + 预留驱动」方案在用户约束（默认全局 / 预留补货 / 释放回收 / filter 统一 / 外物吸收 / 255 容量）下**可行**：

- **循环根治**：预留时物化 → 第三方 job 操作真 Spawned 物品 → `FailOnDespawned` 通过；释放回收 → 物品不外流；
- **保留 vault 核心语义**：默认不可见、无限容量、冻结、共享、filter 门控；
- **改动可控**：半 Spawn 锚点体系保留（改动面小于"常驻陈列"版），主要增量是 `ISlotGroupParent` + Reserve/Release 挂点逻辑 + 记账判定扩展；
- 待实施阶段重点回归：目标一致性、存入路径（格子型）、多份并发、拆除/读档兜底。

## 10. 实现状态（v4 P0 已实施，2026 编译通过）

| 组件 | 实现 | 文件 |
|---|---|---|
| 存储格 | `ISlotGroupParent`（AllSlotCells = 建筑占格）+ Def `<maxItemsInCell>255</maxItemsInCell>`（PS 写入）+ 代码 `MaxItemsInCell => 255` 双保险 + SlotGroup 生命周期 | Building_OuterrealmVault.cs / Defs |
| 物化 | `Reserve` Postfix → `TryLendCopy`（借出=扣全局全量 + GenSpawn 到存储格）；CanReserve/Reserve 存储格满拒绝 | Patch_OuterrealmStorage.cs / OuterrealmVaultViewThingOwner.cs |
| 回收 | vault Tick → `ReturnUnreservedBorrowed`（IsReserved 判定，覆盖所有 Release 路径）；拆除 → `ReturnAllBorrowed` | Building_OuterrealmVault.cs / OuterrealmVaultViewThingOwner.cs |
| 外来吸收 | **每物品吸收倒计时**（落格登记 15 tick → 到期 Deposit）+ rare tick（250）兜底（未登记异常残留） | Building_OuterrealmVault.cs |
| 记账延续 | 借出副本 holdingOwner=null 天然跳过 view 记账；`Notify_ItemRemoved` 加 IsBorrowed 防双扣 | Building_OuterrealmVault.cs |
| 锚点保护 | `borrowedByKey` 防双锚点（EnsureCopyFor/RebuildView）；SyncKey/DisposeOrphanCopy/TryDisposeCopyIfObsolete 跳过借出副本 | OuterrealmVaultViewThingOwner.cs |

验证：`dotnet build -c Debug` 通过（0 错误 0 警告）。待游戏内回归：Raven 休闲循环、取食/取药/选料/穿戴、多份并发、拆除/读档。
