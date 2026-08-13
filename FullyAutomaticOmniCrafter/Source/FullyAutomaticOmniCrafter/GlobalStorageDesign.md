# 全局共享冻结存储系统 — 设计方案报告

> 状态：**设计阶段（暂未修改任何代码）**
> 目标版本：RimWorld 1.6（Assembly-CSharp 1.6.9438）
> 本文所有原版代码引用均来自 rimsage 反编译索引，路径如 `RimWorld/Building_Storage.cs`。

---

## 0. 需求解读

| 需求 | 含义 |
|---|---|
| 与原版多格大容量存储建筑类似 | 视觉上是多格建筑，行为上是一个"存储目的地"：搬运工可存入、可取出、有存储清单 UI、有优先级 |
| 无限容量 | 不受 `maxItemsInCell × 格数`、`stackLimit` 约束 |
| 物品冻结 | 存入后不腐败、不老化、不受温度/环境任何影响（取回时保持原状） |
| 跨建筑/跨地图共享 | 单个存档全局只有一份内容；A 位置存入，B 位置（另一张地图）取出 |
| 每建筑独立存储清单 | filter 控制本建筑"能存入哪些、能看到哪些、能取出哪些" |
| 性能 | 存储海量物品后不能影响游戏性能 |
| 兼容原版 | 对外表现尽量与 `Building_Storage` 等原版存储相同，便于其他 mod 兼容 |
| 扩展（可选） | 被标记（装备/植入物/hediff）的 pawn 可在工作台**原地**取料/存产物，免去来回行走 |

---

## 1. 原版机制深度分析（关键发现）

### 1.1 原版有两套"存储"体系

**A. 格子型存储（SlotGroup）**：`Building_Storage`（储物架/柜）与 `Zone_Stockpile`（仓储区）。

- 实现 `ISlotGroupParent`：`AllSlotCells()` 返回格子集合，`SlotGroup.HeldThings` **实时遍历 `map.thingGrid.ThingsListAt(cell)`** 得到内容（`RimWorld/SlotGroup.cs`）。
- 物品是地图上的真实实体：有渲染、寻路、tick、腐败、合并（listerMergeables）、燃烧等一切开销。
- 结论：**格子型存储无法承载"无限容量 + 冻结 + 跨地图"，且海量实体必然卡顿**。本设计不采用。

**B. 容器型存储（ThingOwner）**：物品存放在一个 `ThingOwner`（IThingHolder）中，**不放在地图格子上**。1.6 官方范例：`Building_OutfitStand`（衣柜，`RimWorld/Building_OutfitStand.cs`）、`Building_Bookcase`（书架，`RimWorld/Building_Bookcase.cs`）、`Building_WorkTableAutonomous`（自动工作台）。

```csharp
// Building_Bookcase 的接口组合（这就是"容器型存储"的标准形态）
public class Building_Bookcase : Building,
  IThingHolderEvents<Book>, IHaulEnroute, ILoadReferenceable,
  IStorageGroupMember, IHaulDestination, IStoreSettingsParent,
  IHaulSource, IThingHolder, ISearchableContents, IBeautyContainer
```

容器型存储的关键性质：

- **未 Spawned 的物品不 tick**（`TickList` 只 tick 地图上的 Thing，`Verse/Thing.cs` `DoTick` 仅由 TickList 驱动）——但持有者被 tick 时 `Thing.DoTick` 会递归 tick 直接持有容器的内容物（`Verse/Thing.cs:492-509`），因此**冻结 = 未 Spawned + 视图容器 `dontTickContents = true` 两者缺一不可**（见 §1.4 与 §3.2 强制项）。
- 物品不在地图上 → **无渲染、寻路、thingGrid、合并、燃烧开销** —— 性能天然安全。
- 搬运工可通过原版路径**自动存入/取出**（见 1.2 / 1.3），工作台、食物、药品搜索**自动可见**（见 1.4）。

### 1.2 存入路径（原版自动支持，无需 patch）

搬运逻辑 `HaulAIUtility.HaulToStorageJob`（`Verse/AI/HaulAIUtility.cs:75`，if 链）：

```csharp
if (haulDestination is ISlotGroupParent)          // 格子型 → 搬到格子
    return HaulToCellStorageJob(JobDefOf.HaulToCell);
if (haulDestination is Thing thing
    && thing.TryGetInnerInteractableThingOwner() != null)  // 容器型 → 搬入容器
    return HaulToContainerJob(JobDefOf.HaulToContainer);
```

即：**只要我们的建筑是 `IHaulDestination` 且 `TryGetInnerInteractableThingOwner()` 返回非空 ThingOwner，搬运工自动走 `JobDriver_HaulToContainer` → `Toils_Haul.DepositHauledThingInContainer` → `carryTracker.innerContainer.TryTransferToContainer(carriedThing, 目标ThingOwner, count)` 存入**（`Verse/AI/Toils_Haul.cs:347-412`）。`TryGetInnerInteractableThingOwner`（`Verse/ThingOwnerUtility.cs:34`）对"自身实现 `IThingHolder` 的 Thing"直接返回 `GetDirectlyHeldThings()`。

搬运工如何选中我们：`StoreUtility.TryFindBestBetterNonSlotGroupStorageFor`（`RimWorld/StoreUtility.cs`）遍历 `AllHaulDestinationsListInPriorityOrder`，按 `GetStoreSettings().Priority` 排序、`Accepts(t)` 过滤 —— 与普通存储完全一致，优先级 UI（ITab_Storage）原版生效。

搬运数量：`HaulToContainerJob` 的 `count = min(t.stackCount, interactableThingOwner.GetCountCanAccept(t))`（`HaulAIUtility.cs:131`）。**`GetCountCanAccept` 是 virtual**（`Verse/ThingOwner.cs:155`），无限容量只需 override 返回 `int.MaxValue`。途中容量检查 `IHaulEnroute.SpaceRemainingFor` 返回 `int.MaxValue` 即可（接口形态参照 `Building_OutfitStand`；其自身返回 0/1 且内含 filter 检查，本设计无容量上限故直接返回 `int.MaxValue`）。注意：`DepositHauledThingInContainer`/`TryTransferToContainer` 只查容量不查 `Accepts`——job 生成后修改 filter 不会阻止已生成的存入 job（原版 Bookcase/OutfitStand 同，非缺陷但需知情）。

### 1.3 取出路径（原版自动支持，无需 patch）

1. **工作台原料**：`WorkGiver_DoBill.TryFindBestIngredientsHelper`（`RimWorld/WorkGiver_DoBill.cs:317-419`）在 `searchRadius` 内遍历 `pawn.Map.haulDestinationManager.AllHaulSourcesListForReading`，用 `ThingOwnerUtility.GetAllThingsRecursively` 把 HaulSource 内容物纳入原料候选。**只要存储建筑在该地图且 `HaulSourceEnabled == true`，其内容就是合法的制作原料**。取料动作：`JobDriver_DoBill` 的收集 toil → `Toils_Haul.StartCarryThing` → `Pawn_CarryTracker.TryStartCarry`（`Verse/Pawn_CarryTracker.cs:63`）= `item.SplitOff(count)` + 放入 carryTracker —— pawn 走到建筑旁，从 ThingOwner 中取走真实堆叠。
2. **产品/原料计数**：`RecipeWorkerCounter.CountProducts`（`Verse/RecipeWorkerCounter.cs:44`）自动遍历 `AllHaulSourcesListForReading` —— 路径自动接入；但原版按候选 `stackCount` 累加（:89），受 §3.2 副本 stackLimit 上限封顶（100 万显示 75），**真实 long 数量需 §5.2 #9 数量替换 patch**。
3. **装备/武器/搬运类取用**：`GenClosest.ClosestThingReachable` 的 `lookInHaulSources` 分支（`Verse/GenClosest.cs:55`）原版即检查 HaulSource 内容物（拾取武器、优化装备、装载运输舱等路径），**装备取用原版兼容**。**食物与治疗例外**：`FoodUtility.BestFoodSourceOnMap` 与 `HealthAIUtility` 原版不搜索 HaulSource——这两个路径需 §5.2 #7 patch 才有（§1.3 的"吃饭、治疗原版兼容"不成立，已修正）。
4. **手动取出**：`ITab_ContentsBase`（`RimWorld/ITab_ContentsBase.cs`）是原版"内容列表 Tab"基类：`container` 抽象属性 + 每行图标/数量 + 丢弃按钮（`OnDropThing` → `GenDrop.TryDropSpawn(t.SplitOff(count), ...)`）。子类化即得原版内容 UI（官方子类：`ITab_ContentsBooks`、`ITab_ContentsOutfitStand`）。

### 1.4 冻结机制的三个层次（原版全部现成）

| 层次 | 原版机制 | 结论 |
|---|---|---|
| 腐败/老化 | 未 Spawned 物品不被 tick（`CompRottable.TickInterval` 只在 Thing 被 tick 时推进） | 天然冻结 ✓ |
| 内容 tick | `ThingOwner.dontTickContents` 字段（`Verse/ThingOwner.cs:28`，原版 `Settlement_TraderTracker`/`SitePartWorker_ItemStash` 用它实现"永久保鲜"） | **必须设 `true`**：`Thing.DoTick`（`Verse/Thing.cs:492-509`）会递归 tick 直接持有容器的内容物，频率 = 持有者的 tick 频率；不设则冻结失效。§3.2 已列为 VaultViewThingOwner 构造的强制项 |
| 温度读数 | `Thing.AmbientTemperature`（`Verse/Thing.cs:379`）：未 Spawned 时沿 ParentHolder 链查 `ThingOwnerUtility.TryGetFixedTemperature` | 该方法是**硬编码 switch**（`Verse/ThingOwnerUtility.cs:401`，只认识 `CompLaunchable`/`Settlement_TraderTracker` 等），**需要 1 个 patch**（见 §5） |

取出的物品以存入时的 `rotProgress`/`HitPoints` 继续原版逻辑 —— "存入期间完全冻结"成立。

### 1.5 跨地图：原版无此机制，需自建全局层

所有相关基础设施都是 **per-Map**：`HaulDestinationManager`（`HaulDestinationManager.cs` 构造参数是 `Map`）、`ListerHaulables`、`thingGrid`。原版没有跨地图共享存储先例。

方案：**`GameComponent` 全局单例**（`Game.FillComponents` 自动实例化、`ExposeData` 随存档深保存、与建筑生命周期完全解耦）。项目有 `GameComponent_OmniResurrector.cs` 先例（静态 `Instance` + `Scribe_Collections` 深存），**但注意**：该先例在 `FinalizeInit` 才赋值 `Instance`，而读档时序中建筑的 `SpawnSetup` 早于 `GameComponent.FinalizeInit`——本系统的 `Instance` 必须在 GameComponent **构造函数**或 `ExposeData(LoadingVars)` 中赋值（或用 `Current.Game.GetComponent<>()` 兜底），不可照搬先例的 FinalizeInit 赋值模式（见 §3.3 读档规则）。

### 1.6 视图内容的 haulable 判定（锁定语义）

`ListerHaulables.ShouldBeHaulable`（`RimWorld/ListerHaulables.cs:141-143`）要求物品**不在最佳存储中**（`!t.IsInValidBestStorage()`）才算"需要搬运"。`IsInValidBestStorage`（`RimWorld/StoreUtility.cs:61-65`）对未 Spawned 物品取 `CurrentHaulDestinationOf = ParentHolder as IHaulDestination`，而 `Thing.ParentHolder = holdingOwner?.Owner`（`Verse/Thing.cs:274`）——**命门前提：视图容器必须以 owner=建筑 构造**（`new VaultViewThingOwner(this)`，§3.2 强制项），条目 ParentHolder 才能解析到建筑；漏传则锁定、放行、MapHeld/温度链全部静默失效。

**锁定语义（已裁决）**：建筑对视图内条目 `Accepts` 恒真 + `IsInValidBestStorage == true` + **§5.2 #6 的 `ShouldBeHaulable` 短路 patch 默认启用**（`Accepts == true` 即锁定，直接返回 false）→ 虚拟物品**恒不 haulable**，内容"锁定"在全局存储中，只通过 UI/工作台/取用路径离开；唯一流出方式是玩家显式"移出"（§6.3 视图放行）。**不再存在"低优先级建筑条目被自动搬到高优先级存储"的语义**——该"低→高自动流出"行为是 M10 无限搬运循环（已消除，见 §6.3/§11）的根源，故与 #6 锁定一并废弃。视图外条目（被 filter 禁止，见 §6.2）不存在于任何视图与 haulables，同样不会被搬走。

---

## 2. 总体架构

```
┌─────────────────────────────────────────────────────────────┐
│  GameComponent_GlobalStorage  （全局层，唯一"真相"）           │
│  · 聚合条目列表 List<GlobalEntry>（每类物品一条，含 long 计数） │
│  · 全局 API：Deposit(Thing) / Withdraw(条目,count) / 查询       │
│  · 版本号 int（内容变更 +1，供建筑懒同步）                      │
└───────────────────────┬─────────────────────────────────────┘
        Map A           │          Map B
┌───────────────────┐   │   ┌───────────────────┐
│ Building_GlobalVault│◄──┼──►│ Building_GlobalVault│  （每个实例独立）
│ · StorageSettings   │   │   │ · StorageSettings   │  （独立 filter）
│ · 视图 ThingOwner   │   │   │ · 视图 ThingOwner   │  （= filter 允许的全局条目镜像）
│ · IHaulDestination  │   │   │ · IHaulSource       │
│ · IHaulSource       │   │   │ · IHaulDestination  │
└───────────────────┘   │   └───────────────────┘
                        │
             原版机制直接对接（无需 patch）：
             HaulToContainerJob（存入） / WorkGiver_DoBill（工作台原料）
             RecipeWorkerCounter（计数） / GenClosest（食物药品）
             ITab_Storage（清单） / ITab_ContentsBase（内容）
```

**核心原则：全局层是真相，建筑视图是缓存。** 所有原版代码只接触"视图 ThingOwner"里的代表 Thing（未 Spawned 的真实 Thing 实例），全局层负责跨建筑/跨地图一致性。

---

## 3. 数据模型（性能的关键）

### 3.1 聚合条目（GlobalEntry）

```csharp
public class GlobalEntry : IExposable
{
    public Thing proto;      // 代表 Thing：未 Spawned，携带完整属性（def/stuff/quality/hp/comp 状态）
    public long count;       // 真实数量（可超 int.MaxValue）
    public int lastSeenVersion; // 视图刷新检查用（全局版本号比对）
}
```

**分组键 = 属性同质性**：`(ThingDef, StuffDef, Quality, HitPoints 分段(10%), 关键 comp 状态签名)`。
同一分组内的物品属性完全一致，因此：

- 钢铁 100 万 → **1 条**（存档 1 条、UI 1 行、tick 0）
- 食物 5 万 → 1~2 条（新鲜度已被冻结，不会随时间分裂）
- 武器 1000 把 → 按 (def, 品质, 耐久段) 拆成几条到几十条
- 极端情况（每把武器耐久都不同）→ 条目数增长，但**条目数 ≈ UI 行数 = 存档大小**，与"地图上放 1000 把武器"相比开销小几个数量级

### 3.2 建筑视图（VaultViewThingOwner : ThingOwner）

每个建筑一个自定义 ThingOwner 子类。**视图条目 = 全局条目的独立"副本实例"**（同一条目在多个建筑视图各物化一份 Thing）——**不可共享同一实例**：`ThingOwner` 的 `holdingOwner` 是单所有权不变式（`Contains(item)` 以它为据，`Remove` 清空它，`SplitOff` 的 `count>=stackCount` 分支调用 `holdingOwner?.Remove`），共享实例会被其他视图的操作破坏。副本约束：

- **构造强制项 1（owner=建筑）**：视图容器必须以 `new VaultViewThingOwner(this)` 构造——未 Spawned 条目经 `Thing.ParentHolder = holdingOwner.Owner`（`Verse/Thing.cs:274`）解析到建筑，是 haulable 判定/`MapHeld`/温度链的前提（§1.6）；漏传则锁定与放行静默失效、alwaysHaulable 物品被判 haulable 搬空。
- **构造强制项 2（dontTickContents = true）**：不设则持有者被 tick 时内容物被递归 tick（§1.4），冻结失效。
- `filter.Allows(entry)` 决定条目是否出现在本建筑视图（§6.2：禁止 = 不可见）。
- **副本 `stackCount` 上限 = 实时读取 `item.def.stackLimit`**（不缓存：其他 mod 可 per-def 修改 stackLimit，每种物品可能不同）：任何原版/第三方代码对副本的合并类操作（`TryAbsorbStack` 的 `stackLimit - stackCount` 计算）都保持假设成立；副本被取空后由 SplitOff postfix **即时**补回（§3.3）。**注意该上限只服务于合并假设**：一切"数量感知"路径（工作台搜索/取料、`RecipeWorkerCounter` 计数、UI 滑条/丢弃）一律经 §5.2 #9 数量替换 patch 使用全局数量——否则"无限容量"在这些路径被 stackLimit 封顶（钢铁=75），单份需求 >75 的账单永不生成（§1.3）。
- **MinifiedThing 例外**：MinifiedThingDef 未定义 stackLimit → 默认 1，打包建筑条目副本 stackCount=1（每件=1 语义），UI 与搬运量按 1 计算，属预期行为。
- 真实数量（long）在全局层；**UI 显示用全局计数**：`ITab_VaultContents` 自定义行渲染，且**滑条与丢弃按钮必须一并自定义**（`ITab_ContentsBase` 的行渲染/滑条/丢弃都以 `stackCount` 为源，只改行渲染则玩家永远拿不到 stackLimit 以上数量），避免 21 亿截断。

override 方法（均满足原版虚方法约定）：

| 方法 | override 行为 |
|---|---|
| `GetCountCanAccept` | 返回 `int.MaxValue`（无限容量） |
| `TryAdd(Thing item, bool canMerge)` —— **存入路径的实际入口**（`TryTransferToContainer` 调用的就是 bool 版，`ThingOwner.cs:324`；int 版只是基类包装，`SplitOff` 后转调 bool 版） | 不放入视图列表，而是**吸收**：`GlobalStorage.Deposit(item 的属性快照 + 全部 stackCount)` → 同步刷新对应视图条目 → 返回 true。**返回值契约：true ⟺ 全量吸收；false ⟺ 零吸收或已按 `item.stackCount` 减值体现部分吸收**（`TryTransferToContainer` 的回滚公式 `result = num - thing.stackCount` 依赖 stackCount 减值量） |
| `TryAdd(Thing item, int count, bool canMerge)` | 同样 override：按 `min(item.stackCount, count)` 吸收并返回该数量（保持基类"调用方可用返回 count 记账"的契约） |

**吸收路径的同步职责（单一入口，防双同步）**：吸收 `TryAdd` **自行完成** 全局 Deposit + 视图条目刷新 + `listerHaulables.Notify_AddedThing(item)`（未 Spawned 物品不经过 Thing.SpawnSetup，必须手动通知）；吸收不调用 `base.NotifyAdded`，§4 的 `Notify_ItemAdded` 钩子**不用于存入路径**（否则双同步/漏同步）。

其余方法（`SplitOff`/`Take`/`TryDrop`/枚举）走原版实现，代表 Thing 的 `stackCount` 变化即"取出量"，由同步协议落到全局层。吸收掉的物品实例可复用为新的代表 Thing——但注意：`DepositHauledThingInContainer` 会把被吸收实例放入 `job.placedThings`（DoBill job 场景，`Toils_Haul.cs`），复用前需检查该引用（或延迟复用/新建实例）；且 Thing 的 def 构造期不可变，"零 GC 复用"仅限同 def 场景。

### 3.3 同步协议（变更驱动，性能与条目总数解耦）

**原则：所有数量变化都在"变更点"即时同步；周期性对账只作低频兜底。** 全部操作发生在主线程（RimWorld 逻辑单线程），无需锁。

| 变更 | 同步方式 | 成本 |
|---|---|---|
| 存入（`TryAdd` 吸收） | 即时 `GlobalStorage.Deposit` + 全局版本号 `++` | O(1) |
| 取出（UI/工作台/搬运/任意路径） | **Harmony patch `Thing.SplitOff`（只 patch virtual 声明处一处）**：`ThingWithComps.SplitOff` 与 `MinifiedThing.SplitOff` 都是 `base.SplitOff(count)` 薄包装，patch 一处即覆盖全部，**三处全 patch 会导致 prefix/postfix 双触发**。prefix：判断 `holdingOwner` 是否为我们的视图容器 → **实时校正副本 = min(全局剩余, stackLimit)**（防超卖）；postfix：按差额扣减全局，并**即时补回副本 = min(全局剩余, stackLimit)**；`count >= stackCount` 整堆分支走 `holdingOwner.Remove`（触发 `Notify_ItemRemoved`）——**postfix 判 `__result == __instance`（整堆）跳过差额扣减**，防双扣（详见下节） | O(1) + 一次 `is` 类型判断 |
| 移除/销毁（`Remove`/`Take`/`Destroy`） | `IThingHolderEvents.Notify_ItemRemoved` 即时同步 | O(1) |
| 跨建筑/跨地图可见性 | 每建筑 60 tick 比较版本号 → **只处理增量变更日志**中的条目 key（§3.3 视图重建小节），不全量 diff | O(变化量 × 建筑数) / 60 tick |
| 兜底 | 每 6000 tick 或**存档时**做一次全量对账。**对账方向（定案）：以全局层为真相，只修副本（副本重算 = min(全局剩余, stackLimit)）；全局计数只增不凭空减；发现差异（如回滚漂移产生的孤儿 piece）写入日志由人工/后续逻辑处置** | 存档时一次性 |

> 相比旧设计的"每 60 tick 全量对账（O(条目数)/60 tick）"，变更驱动把常规开销降为 **O(变更量)**。`SplitOff` patch 用类型判断短路，对全游戏其他路径零影响（仅当物品持有者是我们的视图容器时才进入同步分支）。

**视图规模守则（与问题 2 直接相关）**：无论全局层存了多少个体，**建筑视图永远只包含 L1 聚合条目**（def/stuff/品质/耐久段组合，通常几十~几百条）。个体级数据（见 §3.5）不进入视图，因此对账、UI、listerHaulables Check 的工作量与个体总数无关。

**视图重建（增量变更日志，方案 A 定案）**：解决"频繁变动 + 超大库存"下全量 diff 成本随 L1 总量线性增长的问题（旧设计每 60 tick O(L1×建筑数)，超大库存时白扫海量未变化条目）。

- **全局层维护**：`List<EntryKey> changeLog`（环形缓冲，容量 ~4096）+ 同窗口去重（`HashSet`）+ 溢出标记；每次存入/取出/移出/退休时追加条目 key（同窗口同 key 只记一次）。
- **建筑 60 tick**：版本号变化 → 只遍历 `changeLog` 中的 key 逐个处理（filter 可见性检查 → 更新数字 / 物化新副本 / 退休移除），共用"单 key 更新"函数——**成本 O(变化量 × 建筑数) / 60 tick，与 L1 总量解耦**。**同时比较本建筑 `GetStoreSettings()` 签名（filter 摘要 + 优先级）**：签名变化 → 全量重建本建筑视图——这是存储组（§4.1e）filter 变更的通道（`StorageGroup.Notify_SettingsChanged` 只通知 `ISlotGroupParent` 成员，容器型建筑收不到组通知，靠签名检查补足，零额外 patch）。
- **溢出**（单窗口变化 > 4096）：清空日志 + 置"需全量重建"标记 → 走原全量 diff 路径（保留共用）。
- **兜底**：每 6000 tick 或存档时全量对账一次（检测漏网/漂移，正常不触发；方向见同步表"兜底"行）。
- **初始化/重连**（SpawnSetup，含 minify 放回）：仍全量重建（一次性）。
- **存档读档（关键规则）**：变更日志/版本号**不序列化**（读档后天然为空）。读档顺序为"全局层先恢复 → 建筑 SpawnSetup 全量重建"；**版本号与 `lastSeenVersion` 的重置必须发生在全局层 `ExposeData(PostLoadInit)`（早于 SpawnSetup）**，SpawnSetup 重建时设 `lastSeenVersion = 当前版本号`——若重置落到 `GameComponent.FinalizeInit`（晚于 SpawnSetup），会与已设的 lastSeenVersion 冲突引发错误同步。全局层静态 `Instance` 必须在 GameComponent 构造函数或 `ExposeData(LoadingVars)` 赋值（§1.5）。
- 版本号 `int` 回绕处理（比较用差值而非大小）。

**退休副本生命周期（退休列表，补全定义）**：视图重建时若某条目的旧副本仍被引用（有 reservation 或 job 持有），不销毁它，而是从视图"退休"——移入建筑的 `retiredCopies` 列表，新副本物化进视图。规则：

- **退休条件**：重建要替换该条目副本，且旧副本 `reservationManager` 中存在以它为 target 的 reservation，或 `job.placedThings`/任何活 job 引用它。
- **退休语义**：退休副本**不参与 listerHaulables/工作台搜索**（从视图移除时同步 `listerHaulables.Notify_DeSpawned(item)`）；它只为既有 reservation/job 的引用有效性而保留（预留记账的 R 扫描必须覆盖退休副本）。
- **销毁时机**：其 reservation 全部释放且无活 job 引用后立即销毁（在 Release 族 patch 与 job 结束回调中检查）；**绝不允许"永不销毁"**（否则每 job 泄漏一个 Thing 实例）。
- **多副本并存约束**：同一条目的新旧副本并存期间，工作台候选去重（`processedThings` 按实例引用）会失效——退休副本因"不参与搜索"已被排除，故候选集中只有新副本，无双计问题；listerHaulables 同理（退休副本已被 Notify_DeSpawned 移除）。
- **job 中断清理**：pawn 死亡/取消导致 job 中断时，原版释放路径（`ReleaseClaimedBy` 等）释放 reservation → 销毁检查立即执行，退休副本随之销毁。

**严格杜绝超卖（多建筑并发取同一条目）**：取物入口统一为 `Thing.SplitOff` 单点 patch，采用"实时校正 + 差额扣减 + 即时补回"：

- **prefix**：若原 Thing 的 `holdingOwner` 是我们的视图容器 → `副本.stackCount = min(G − R + r_this, def.stackLimit)`（实时校正：调用方永远基于当前真实值计算取数）。
- **postfix**：若 `__result == __instance`（整堆分支，原版已走 `holdingOwner.Remove` → `Notify_ItemRemoved` 同步扣减）→ **跳过差额扣减**；否则按 `旧 stackCount − 新 stackCount` 差额即时扣减全局（clamp 0 兜底）。随后**即时补回副本 = min(全局剩余, stackLimit)**（把补回从 60 tick 视图刷新提前到变更点——否则工作台连续制作在每单之间因"副本取空 + 500-600 tick 搜索重试"空转 8-10 秒）。
- **失败回滚补偿**：SplitOff 调用方失败时用 `TryAbsorbStack(piece, false)` 把 piece 合并回副本（`ThingOwner<T>.TryAdd`/`TryTransferToContainer` 失败路径）——postfix 已扣全局但副本涨回 → 计数漂移。**须一并 patch `Thing.TryAbsorbStack`/`ThingWithComps.TryAbsorbStack`（§5.2 #5 配套）**：合并进我们的视图副本时把 `piece.stackCount` 补回全局。
- **数量感知路径经 §5.2 #9 使用全局量**：工作台搜索/取料 job.count 基于全局剩余计算，正常路径下 `job.count ≤ 校正后副本 stackCount`，不触发整堆分支。残余竞态（同 tick 另一建筑取同一条目导致校正后副本 < job.count）：job 按实际拆分数量完成（部分取物），无超卖；极端时原版整堆分支打 `Log.Error` 属低概率可接受噪音（数量仍正确）。
并发推演（同 tick 两个 pawn 取同一 1000 条目的 750）：pawn1 取 750 → 全局 250；pawn2 的 prefix 校正副本=250 → 实际取 250（部分取物，job 按实际数计）→ 总量 750+250=1000，全局 0，**无超卖**。版本号比较仅用于视图刷新，不再是防超卖依赖。

**预留记账（Reservation Ledger，库存预留协议）**：解决"pawn 保留物品后行走期间"的数量一致性（场景：pawnA 从建筑A 保留并前往取 200 个中的一部分，pawnB 从建筑B 取同一条目——数量足够则 B 不受影响，不足则**在保留阶段阻止 B**）。

**记账量**（单线程模型下天然原子；记账操作封装为原子方法接口，防御未来多线程）：
- `G` = 条目全局数量；`R` = 条目总预留（**扫描推导**：`map.reservationManager.ReservationsReadOnly` 中 target 为本系统视图/退休副本的 reservation 预留量之和）；`r_this` = 某副本自身的预留量。

| 操作 | 规则 |
|---|---|
| 保留（patch `ReservationManager.CanReserve` + `Reserve` + `CanReserveStack`，target 是副本时） | 检查 `N ≤ G − R`。**N 的解析（与原版对齐）**：reservation 的 `stackCount = -1`（`StackCount_All`）解析为"该副本当前 stackCount"（原版 `CanReserve` 的 `num1`/`num3` 语义），**与 job.count 无关**——不引入任何解析表（解析表方案会系统性低估 R，造成超卖预留，已废弃）。`CanReserveStack`（用药/进食量计算入口）同样替换数量检查。**patch 在 CanReserve 入口无条件执行数量检查**（不因 `ignoreOtherReservations` 跳过——否则 `playerForced` 强制保留会绕过 G−R 强抢已锁实物）。通过 → 原版 reservation 照常创建；不通过 → 保留失败（**静默返回 false，不打 Log.Error**——原版 `errorOnFailed=true` 会为每次拒绝刷屏；pawn 放弃该 job（Incompletable）/重新找工作，非"重试同一 job"）→ **数量不足时阻止保留 ✓** |
| 取物（`Thing.SplitOff` patch 扩展） | prefix 校正 `副本.stackCount = min(G − R + r_this, stackLimit)`；postfix `G −= 实际取走量`，`r_this −= min(实际取走量, r_this)`（兑现自己的预留）；即时补回副本（见上节） |
| 释放（patch `Release`/`ReleaseAllForTarget`/`ReleaseClaimedBy`/`ReleaseAllClaimedBy`） | 无需维护任何表（R 本身扫描推导）；释放后顺带执行退休副本销毁检查（§3.3 退休副本生命周期）。pawn 死亡/任务中断/强制覆盖的原版释放路径自动覆盖 |

**一致性推演**：
- 数量足够（G=400）：A 保留 100（R=100）→ B 保留 100：检查 100 ≤ 300 ✓（**B 不受影响**）→ A 取 100（G=300，r_A=0）→ B 取 100（G=200，R=0）✓
- 数量不足（G=150）：A 保留 100（R=100）→ B 保留 100：检查 100 ≤ 50 ✗ → **B 的保留被阻止** ✓
- 无保留者插队（C 未保留直接取）：校正 `G − R` 限制其取走量 ≤ 未预留部分，预留实物不被抢占 ✓
- 任何时刻 `R ≤ G`（保留检查保证）、取走量 ≤ 可用量（校正保证）→ 全局计数永不为负、无超卖 ✓

**边界**：① `maxPawns` 保持原版默认（同一副本同时 1 个取物者；**不同建筑副本互不影响**——核心场景）；② UI/管理面板取出受 `G − R` 限制（预留锁定，不兑现预留）；③ 预留中的条目（R>0）禁止 L2 分裂；④ 存入只增 G（R 不变，可用量上升）；⑤ 读档无需重建任何解析表（无解析表）；⑥ 与退休副本生命周期（§3.3）互补（退休副本实例保留 → reservation 引用有效，销毁检查见退休小节）。

### 3.4 性能结论（逐项对照需求）

| 开销项 | 量级 | 说明 |
|---|---|---|
| 地图实体 | **0** | 内容物不 Spawned，无渲染/寻路/thingGrid/合并/燃烧 |
| tick | **每 tick 1 次廉价 hash 判断** | 内容物不 tick（`dontTickContents`）；建筑 `tickerType=Normal`，`Tick()` 内 `IsHashIntervalTick(60)` 门控：每 tick 只做一次 hash 判断，每 60 tick 才可能执行版本号/设置签名比较与同步 |
| 数量同步 | **O(变更量)** | 存入/取出在变更点即时同步（§3.3），无常驻扫描 |
| listerHaulables | 每 tick ≤4 个 HaulSource 全量 Check（HaulSource 数 ≤4 时**每个每 tick 一次**） | Check 对象数 = **L1 条目数**（几十~几百，与个体总量无关）；§5.2 #6 短路 patch（**必需，默认启用**）把锁定条目 Check 降为 O(1) 比较；注意原版轮转 `index2 = num2 + index1` 无取模，HaulSource ≥6 时部分来源永不周期检查（事件通知兜底） |
| UI | 行数 = L1 条目数 | `ITab_ContentsBase` 逐行渲染 |
| 存档 | L1 条目数 × 单条深度（全局层**单份**；建筑只存 settings，不存视图）；L2 个体用紧凑序列化 | 100 万钢铁 = 1 条；百万独立个体 ≈ 100MB+（用户显式选择个体保留模式时） |
| 内存 | L1 条目数 × Thing 实例 + L2 紧凑记录 | 代表 Thing 未 Spawned，无 map 关联开销 |

### 3.5 两级数据模型（应对"百万级无法合并的物品"）

**问题本质**：如果必须保留每个个体的独立属性（耐久、品质、附魔…），数据量本身就有物理下限（每个体至少一条紧凑记录 ≈ 100-200 字节 → 100 万个体 ≈ 100-200MB 内存 + 等量存档），任何实现都无法回避；原版把个体作为地图实体更糟（额外渲染/寻路/tick 开销）。因此设计分两级：

- **L1 聚合级（默认，视图/UI/对账/Check 唯一数据源）**：分组键 = `(ThingDef, StuffDef, Quality, 耐久段(10%), 关键 comp 状态)`。看似"完全无法合并"的场景（如 10 万把耐久各异的武器）实际合并到 **≤10 个耐久段 × 品质段 × def**，条目数仍是组合级（几十~几百）。
- **L2 个体级（可选模式，按条目启用）**：当玩家明确需要逐个体管理（如传奇装备库）时，对应 L1 条目下挂个体明细 `List<GlobalItemRecord>`。**个体数据以紧凑记录存储（不物化为 Thing 实例）**，仅在"取出该个体"时 `ThingMaker.MakeThing` + 恢复属性。序列化用自定义紧凑格式（单个 `Scribe_Values.Look<string>` 字段装压缩数据），避免 Scribe 逐对象深存导致百万级读档慢。

启用规则建议：默认全 L1；某个 L1 条目因"玩家要求按个体取出/丢弃/排序"而进入精细模式时自动升级为 L2（例如 UI 对该条目点"精细管理"）。L2 条目不进入视图，视图仍显示其 L1 汇总行。

由此，**"百万级完全无法合并的物品"在默认模式下的性能与 100 万钢铁完全一致（O(组合数)）；个体保留模式性能与个体数线性，属于用户显式选择的物理极限**。

---

## 4. 建筑类设计（对外行为 = 原版容器型存储）

```csharp
public class Building_GlobalVault : Building,
    IHaulDestination, IStoreSettingsParent,   // 存入 + 清单/优先级 UI
    IHaulSource, IThingHolder,                 // 取出（工作台/食物/药品/搬运）
    IThingHolderEvents<Thing>,                 // 存入/取出即时钩子
    IHaulEnroute,                              // 搬运途中容量检查（返回 int.MaxValue）
    ISearchableContents                        // 原版"内容可搜索"
{
    public StorageSettings settings;           // 独立清单（用户核心需求）
    public VaultViewThingOwner view;           // 视图容器（必须 new VaultViewThingOwner(this) 构造：owner=建筑 是 haulable 判定链命门，§1.6/§3.2；且构造内必须设 dontTickContents = true）
}
```

| 成员 | 实现要点 |
|---|---|
| `Accepts(Thing)` | `settings.AllowedToAccept(t)`（无限容量 → 不再检查格子/堆叠）；filter 语义见 §6.2（允许=可见可存取，禁止=不可见） |
| `SpaceRemainingFor(ThingDef)` | `int.MaxValue`（注意语义：该值不做 filter 检查，filter 门控只发生在存储选择阶段 `Accepts`；job 生成后修改 filter 不会阻止已生成的存入 job，原版同） |
| `GetStoreSettings/GetParentStoreSettings` | 同 `Building_Storage`：`storageGroup != null ? storageGroup.GetStoreSettings() : settings` / `def.building.fixedStorageSettings`（若 def 未配置则 `StorageSettings.EverStorableFixedSettings()`——该静态设置为**全允许**（全部 EverStorable 物品），对本建筑不构成限制，与 §6.2"空 filter=全禁止"（指本建筑自身 settings）是两个不同对象，勿混淆）；组支持见 §4.1e |
| `Notify_SettingsChanged()` | filter/优先级/出入模式变化的主入口：listerHaulables 重算 + `haulDestinationManager.Notify_HaulDestinationChangedPriority()`（优先级重排序，漏实现则 `AllHaulDestinationsListInPriorityOrder` 失序 → 目标选择错误）+ 视图按新 filter 重建（照抄 `Building_Storage` 模式） |
| 组取消链接 | `Group` setter 置 null 时先 `settings.CopyFrom(storageGroup.GetStoreSettings())` 再断开（对齐原版 `StorageGroupUtility.SetStorageGroup` 的写回语义；覆盖组 gizmo 取消链接路径——该路径原版只特判 `Building_Storage` 类型，§4.1e） |
| `HaulDestinationEnabled` / `HaulSourceEnabled` | 可独立切换的出入模式（§4.1c）：双向（默认）/ 只入不出（`HaulSourceEnabled=false`）/ 只出不入（`HaulDestinationEnabled=false`），gizmo + 存档 |
| `GetDirectlyHeldThings()` | 返回 `view`（TryGetInnerInteractableThingOwner 自动接入 HaulToContainer） |
| `Notify_ItemAdded/Removed` | 全局层同步始终执行；`listerHaulables.Notify_AddedThing(item)` / `Notify_DeSpawned(item)`（**单物品 Check，O(1)**）**仅在 `Spawned` 时调用**（DeSpawn 后 MapHeld=null 会 NPE，参照 OutfitStand/Bookcase 的 `if (Spawned)` 守卫）；`Notify_ItemAdded` 不用于吸收存入路径（§3.2 单一入口）；全量 `Notify_HaulSourceChanged` 仅用于 filter/模式/放行等设置变化——**避免存入高频时全量扫描**（性能审查修正） |
| `GetInspectTabs()` | 返回 `ITab_Storage`（清单+优先级，原版）+ `ITab_VaultContents : ITab_ContentsBase`（内容，原版基类）。**注意：tab 必须在 def XML 的 `<inspectorTabs>` 声明**（`Thing.GetInspectTabs` 默认返回 `def.inspectorTabsResolved`），或 override 自行返回（§4.1 def 已配置） |
| `GetGizmos()` | `StorageSettingsClipboard.CopyPasteGizmosFor(settings)`（原版复制粘贴清单，含多选同步，§4.2）+ 出入三开关（§4.1d）+ 存储组 gizmo（§4.1e）+ 打开全局管理面板 |
| `GetFloatMenuOptions()` | **必须主动调用 `HaulSourceUtility.GetFloatMenuOptions(this, selPawn)`**（"HaulFromSource"浮菜单不会自动出现；参照 Bookcase.cs:310 / OutfitStand.cs:538），否则 §6.3 放行后的右键手动搬运入口缺失 |
| `GetInspectString()` | 仿 `Building_Storage.GetInspectString`：总条目数/总数量/内容摘要；**摘要字符串缓存 + 版本号失效**（`InspectPaneFiller.cs:147` 每帧调用，避免每帧拼接几百条目，性能审查修正） |
| `Destroy/DeSpawn` | **内容保留在全局层**（不落地、不丢失）——与"与建筑解耦"同哲学；DeSpawn 同时向全局层注销（minify/拆除均走此路径，§4.1b） |
| 存档 | 建筑**只存 `settings` + 出入开关/放行列表等自身状态，不存视图容器**——全局层已深存真相，视图深存 = 存档体积×建筑数 + 读档白跑 + "忘记丢弃→双真相"隐患；视图由 SpawnSetup 全量重建（§3.3） |

### 4.1 Def 设计（`Defs/ThingDefs_Buildings/`）

```xml
<ThingDef ParentName="BaseBuilding">
  <defName>FAOC_GlobalVault</defName>
  <thingClass>FullyAutomaticOmniCrafter.Building_GlobalVault</thingClass>
  <size>2,2</size>            <!-- 多格外观（"多格大容量存储建筑"） -->
  <category>Building</category>
  <buildable>true</buildable>
  <designationCategory>Storage</designationCategory>
  <tickerType>Normal</tickerType> <!-- 视图刷新的驱动：override Tick() 内 IsHashIntervalTick(60) 门控（1.6 中 Rare=250 tick 且走 TickRare() 不调 Tick()，无法实现 60 tick 比较，勿用） -->
  <inspectorTabs>
    <li>ITab_Storage</li>
    <li>FullyAutomaticOmniCrafter.ITab_VaultContents</li>
  </inspectorTabs>
  <building>
    <!-- 不配置 defaultStorageSettings：PostMake 后 filter 为空 = 默认全禁止，新建筑不接受任何物品，玩家显式勾选后才工作（防意外吸走，§6.2） -->
    <ignoreStoredThingsBeauty>true</ignoreStoredThingsBeauty>
    <storageGroupTag>FAOC_Vault</storageGroupTag>  <!-- 启用原版存储组（§4.1e）：自定义 tag，与原版 Shelf/Bookcase 隔离；组=共享 filter+优先级，内容仍全局共享 -->
  </building>
</ThingDef>
```

不需要 `maxItemsInCell`（无限）、不需要 `fixedStorageSettings`（除非想锁定清单）。

### 4.1b 连接生命周期（非正常状态即断开，含 minify）

**规则：建筑只在"正常状态"（Spawned 且未 Destroyed）时与全局存储空间保持连接；任何非正常状态（minify 打包中、蓝图/Frame、被摧毁、拆除、地图卸载）立即断开连接。** 断开只影响该建筑的访问（视图清空、不可存不可取、不参与搬运），**全局层数据不变**；重新恢复正常状态时自动重连（重建视图、恢复放行状态）。

- **注册/注销**：`SpawnSetup` → 向全局层注册；`DeSpawn` → 注销（视图副本销毁）。minify（DeSpawn 进 `MinifiedThing`）与放回（SpawnSetup）自动走同一路径——即使其他 mod 强制打包（无视 def `<minifiable>false</minifiable>`）也安全。
- 断开期间：视图保持空、不重建；原版 `Thing.DeSpawn` 自动注销 HaulDestination/HaulSource 注册（`Thing.cs:702-705`），无需额外处理；`Notify_MinifiedThingAboutToBeDestroyed`（仿 `Building_OutfitStand`）作为防御性钩子清理内部状态——注意其**原版用途是内容落物**（注册清理发生在 minify 时的 `Thing.DeSpawn`，与它无关），本设计内容保留全局层，只借用它防止异常路径下的残留。
- 重连：`SpawnSetup` 时重建视图（按当前 filter/出入模式与全局层内容）并恢复该建筑此前未完成的放行列表（放行状态随建筑实例序列化）。
- `settings`（filter/优先级/出入模式）随建筑实例序列化，断开往返不丢失。

### 4.1c 出入模式（只入不出 / 只出不入）

**原版机制查证**（rimsage）：
- **只入不出**：① `IHaulSource.HaulSourceEnabled` 是原版"自动取出"开关——`Building_OutfitStand.allowRemovingItems` 是玩家可切换先例（gizmo `SetAllowHauling` + `Scribe_Values` 存档，`Building_OutfitStand.cs:45/124/535/701/775`）；② 更彻底的是不实现 `IHaulSource`：`Building_CorpseCasket`（棺材）只实现 `IHaulDestination`，搬运工只能存入不能取，且 UI 也禁用移除（`ITab_ContentsCasket.canRemoveThings = false`）。
- **只出不入**：`IHaulDestination.HaulDestinationEnabled = false` 即可让搬运工不再把建筑选为存储目标（`StoreUtility.TryFindBestBetterNonSlotGroupStorageFor` / `TryFindBestBetterStoreCellFor` 均检查该标志）；原版没有现成建筑使用（均为 true），但机制完整存在。

**本系统支持**（两个独立布尔开关 + 一个条件开关，全部走原版接口标志，无自定义状态机）：

| 开关 | 原版标志映射 | 语义 |
|---|---|---|
| 禁止存入 `noDeposit` | `HaulDestinationEnabled = !noDeposit` | on：搬运工不再把本建筑选为存储目标（只出不入，即 §6.3"仅手动存入"） |
| 禁止取出 `noWithdraw` | `HaulSourceEnabled = !noWithdraw` | on：搬运工不能自动取走；**同时关闭工作台原料与装备类搜索**（原版标志语义：`WorkGiver_DoBill.cs:359` 检查同一标志）。**注意：食物/治疗路径原版本就不搜索 HaulSource（§1.3-3），开关对它们无影响**；另注意 `RecipeWorkerCounter.CountProducts` 不做该检查——"只入不出"模式下账单"已有数量"仍会计入本建筑内容（计数与可获取性不一致，已文档化） |
| 禁止取出时允许拿取 `allowTakeForUse`（条件开关，仅当 `noWithdraw=on` 有效） | 不映射原版标志，由 §5.2 第 7 项 patch 放宽使用路径（patch 目标是 `FoodUtility`/`HealthAIUtility` 等真实使用路径，**不是** `GenClosest.ClosestThing_Global_Reachable`——其 `canLookInHaulableSources` 参数原版无任何调用方传 true，patch 无效） | on：工作台原料/食物药品搜索仍可见本建筑内容；**搬运工 haul 仍禁止**（`ListerHaulables.ShouldBeHaulable` 不放宽） |

状态组合：

| noDeposit | noWithdraw | allowTakeForUse | 语义 |
|---|---|---|---|
| off | off | —（忽略） | 正常（双向） |
| on | off | —（忽略） | 只出不入（禁止存入） |
| off | on | off | 只入不出（严格：自动与使用路径全关） |
| off | on | on | 只入不出 + 工作台/进食可用 |
| on | on | —（忽略） | 完全禁用（不存不取，含使用路径） |

### 4.1d 出入开关 UI 设计（双开关 + 条件开关，利用原版多选同步）

**交互形态：三个 `Command_Toggle` 布尔开关**（原版标准 toggle 外观：图标 + 右下角 checkbox 勾选框 + 激活态），取代三态循环——`Command_Toggle` + 相同 `groupKey` 自动获得**原版多选同步调整**能力，无需任何自定义多选逻辑。

**1. 原版多选同步机制（已查证，`GizmoGridDrawer`/`Command`）**：
- 多选时 `GizmoGridDrawer.DrawGizmoGrid`（`Verse/GizmoGridDrawer.cs`）按 `Command.GroupsWith`（`Verse/Command.cs:225`：`groupable && hotKey/Label/icon/groupKey` 相同）把同组 gizmo 合并为一个显示；
- 点击合并组时（`GizmoGridDrawer.cs:307-315`）对组内 gizmo 逐个调用 `ProcessInput`，但**有 `interactedGiz.InheritInteractionsFrom(other)` 门控**（`GizmoGridDrawer.cs:311-312`）——对 `Command_Toggle` 而言组内 `isActive` **全部一致**时每个选中建筑的 `toggleAction` 才都被执行 = 多选同步切换；**状态不一致时只有代表对象被切换**（不是无条件全组同步，两条语义合并理解）；
- 约束：`Command_Toggle.InheritInteractionsFrom`（`Verse/Command_Toggle.cs:44-46`）要求组内 `isActive` 全部一致才继承交互（原版防误触设计，如实保留）；`activateIfAmbiguous` 控制不一致时的显示策略（默认 true）；
- **注意**：1.6 `Command` 新增 `groupKeyIgnoreContent` 合并路径（`Command.cs:31-32`），默认 -1 无影响；本设计三开关只设 `groupKey` 常量，**勿给 `groupKeyIgnoreContent` 设非 -1 值**。

**2. 三个开关**（每个开关在所有建筑实例上使用**相同的 label/icon/groupKey 常量**——合并的硬性条件）：

| 开关 | label | 说明 | 合并 key |
|---|---|---|---|
| 禁止存入 | "VaultNoDeposit" | on = `HaulDestinationEnabled=false` | `groupKey` 常量 A |
| 禁止取出 | "VaultNoWithdraw" | on = `HaulSourceEnabled=false`；desc 提示"同时关闭工作台/食物搜索，除非开启'允许拿取'" | `groupKey` 常量 B |
| 禁止取出时允许拿取 | "VaultAllowTakeForUse" | `Disabled = () => !noWithdraw`（条件开关：禁止取出未开时置灰不可点，`disabledReason` 说明）；on = 放宽使用路径（§5.2 第 7 项） | `groupKey` 常量 C |

```csharp
yield return new Command_Toggle
{
    defaultLabel = "VaultNoDeposit".Translate(),
    defaultDesc  = "VaultNoDepositDesc".Translate(),
    icon         = VaultGizmoTex.NoDeposit,
    groupKey     = VaultGizmoKeys.NoDeposit,      // 常量：多选合并的关键
    isActive     = () => noDeposit,
    toggleAction = () => SetNoDeposit(!noDeposit), // 切换 + Notify_SettingsChanged + listerHaulables 重算
};
// 禁止取出开关同理；"允许拿取"开关额外：Disabled = () => !noWithdraw, disabledReason = ...
```

**3. 图标**（3 张，`Textures/UI/Commands/`，`[StaticConstructorOnStartup]` 预加载——项目惯例，参考 `SubspaceAssetBlackHoleTex`）：
- 禁止存入：存入箭头 + 红斜杠（禁入）；禁止取出：取出箭头 + 红斜杠（禁出）；允许拿取：取用手图标。
- 开关开/关状态由原版 checkbox（右下角勾选框）表现，图标可保持单色系。

**4. 存档**：三个 bool 字段 `Scribe_Values.Look`（`noDeposit` / `noWithdraw` / `allowTakeForUse`）。

**5. 辅助展示与边界**：
- `GetInspectString` 在非默认状态时追加提示（如"禁止取出"）；可选增强：非默认状态下经 `map.overlayDrawer` 绘制小状态图标便于扫视；
- 蓝图/未 Spawned：gizmo 不可用，默认全 off（正常），建造后切换（§4.1b 断开规则不受影响）；
- 管理面板（§6.4）每建筑行显示三开关状态，可远程切换（可选功能）；
- 与其他机制**正交无冲突**：开关不影响视图内容（视图只由 filter 决定）；放行/防回吸（§6.3）独立；玩家 UI/管理面板手动取出不受"禁止取出"限制。

### 4.1e 存储组（StorageGroup）兼容（默认启用）

**结论：可以兼容，且与当前设计无冲突**——组共享的是"设置"（filter + 优先级），内容始终全局共享，两者正交；"每建筑独立清单"保持为默认能力（不组时），组是玩家**自愿**的批量管理（组间仍可不同）。

**机制**（逐行照抄 `Building_Storage` 的组处理，`RimWorld/Building_Storage.cs`）：
- def：`<storageGroupTag>FAOC_Vault</storageGroupTag>`（自定义 tag，与原版 `Shelf`/`Bookcase`/`OutfitStand`/`Hopper` 隔离）；
- 实现 `IStorageGroupMember`：`Group`（get/set）、`StoreSettings`、`ParentStoreSettings`、`ThingStoreSettings`、`StorageGroupTag`、`DrawConnectionOverlay`、`DrawStorageTab`、`ShowRenameButton`（`Building_Storage.cs:35-73` 照抄）；
- `GetStoreSettings()`：`storageGroup != null ? storageGroup.GetStoreSettings() : settings`；
- 生命周期：`SpawnSetup` 跨地图自动断开组（`Building_Storage.cs:141-153`）；`Destroy` → `storageGroup?.RemoveMember(this); storageGroup = null`；`ExposeData` → `Scribe_References.Look(ref storageGroup, "storageGroup")`；
- UI：`GetGizmos` 加 `StorageGroupUtility.StorageGroupMemberGizmos(this)`（链接/取消链接/选择组内全部/重命名）、`DrawExtraSelectionOverlays` 加连接线、`GetInspectString` 加组名与成员数（`Building_Storage.cs:164-196/208-215`）；
- 蓝图/Frame 自动继承组：`Verse/Building.cs:273`、`RimWorld/Frame.cs:146/250/322`（原版机制，建造完成自动入组，零成本）。

**语义边界（重要，需文档化）**：

| 项 | 组内行为 |
|---|---|
| filter + 优先级 | **共享**（原版组语义：组 settings 单一实例）→ 组内建筑可见/可存取内容一致 |
| 内容 | **始终全局共享**（组不改变内容共享，只共享"设置"）——与 StorageGroup 对格子存储的"内容独立"本质不同 |
| 出入开关（§4.1c） | **不共享**（不属于 StorageSettings，每建筑独立）——组控制"存什么"，开关控制"能否存取"，正交 |
| 放行列表（§6.3） | **不共享**（建筑级临时状态） |
| 跨地图 | 组是 per-Map（原版限制）：建筑跨地图 Spawn 自动断开组 |
| minify | 组引用保留（原版：组移除只发生在 Destroy）；与 §4.1b"断开内容访问"正交 |
| 内容合并/显示 | **不存在"内容合并显示"问题**：本建筑不是 `ISlotGroupParent`（无格子），组级 `HeldThings`/`CellsList` 对本组成员为空壳（无消费方依赖）；每建筑内容显示（ITab_VaultContents）仍按各自视图——组内同 filter → 显示相同内容，这与"组"无关（全局共享本就如此），需文档说明避免玩家误解 |
| 账单取料范围（includeGroup） | **不适用且无坑**：账单"仅从该存储区取料"下拉只列 `AllGroupsListInPriorityOrder` 中的 SlotGroup（`Dialog_BillConfig.FillOutputDropdownOptions`，`Dialog_BillConfig.cs:418-462`），本建筑非 SlotGroup → 建筑与组均不会出现在下拉；默认"从所有来源"路径自动包含本建筑内容（HaulSource 分支），玩家显式限定到原版存储/组时本建筑内容按原版语义排除（合理） |
| 取消链接 | **保留最新组设置**：`IStorageGroupMember.Group` setter 置 null 时先把组 settings `CopyFrom` 到成员 settings 再断开——玩家在组内做的修改不丢失。**定性修正**：这并非"自定义增强"——1.6 原版 `StorageGroupUtility.SetStorageGroup`（:145）已内建写回；本设计 setter 写回的实际价值是**覆盖组 gizmo 取消链接路径**（`StorageGroupUtility` 该路径只特判 `Building_Storage` 类型，不覆盖本建筑），且与原版写回重复执行幂等 |
| 组 filter 变更通知 | **原版断链，已由 §3.3 补足**：`StorageGroup.Notify_SettingsChanged` 只通知 `ISlotGroupParent` 成员（`StorageGroup.cs:128-135`），本建筑收不到组 filter 变化通知——视图刷新靠 §3.3 的"设置签名检查"（每 60 tick 比较 `GetStoreSettings()` 签名，变更即全量重建），零额外 patch |
| 死锁逃生 | 不变：管理面板（§6.4）无视 filter |

**冲突分析**：与"每建筑独立清单"不冲突（独立是默认，组是自愿共享）；与 §6.2 filter 语义（允许=可见可存取）不冲突（组 settings 的 filter 同样驱动视图/存取）；与多选 ITab_Storage 兼容（同组多选 → 组 settings 一次调整，原版自动）；视图（§3.2）按 `GetStoreSettings().filter` 过滤 → 组内一致；**组设置变化的视图刷新通道见上表"组 filter 变更通知"行**（原版组通知链不覆盖容器型成员，由 §3.3 设置签名检查承担）。

**"组 = 自动同步"语义确认**：组共享同一 `StorageSettings` 实例 = 改一个全组同步，正是"自动同步"的技术形态；与"内容合并"无关——内容层面本系统**本来就全局共享**（所有建筑内容相同），组连接不改变内容，只同步设置（§4.1e 语义边界表）。

**取消链接保留组设置（默认行为）**：`IStorageGroupMember.Group` setter 置 null 时先把组 settings `CopyFrom` 到成员 `settings`（保留最新组设置）再置 null——贴合"组 = 自动同步"语义，玩家在组内做的修改在取消链接后不丢失。定性已在上表修正（对齐原版 `SetStorageGroup` 写回语义、覆盖 gizmo 取消链接类型特判缺口）。实现注意：`Group` setter 会被多处调用（`StorageGroupManager.Notify_MemberRemoved` 的 `SetStorageGroup(null)`、`Building.cs:273`/`Frame.cs` 的组继承路径、跨地图自动断开），统一"先写回再断开"无副作用：已验证 Frame 建造完成（`Frame.cs:250` 入组 + `:251` `ThingStoreSettings.CopyFrom` 在后）、`Building.cs:273` 重建路径、SpawnSetup 跨地图、Destroy——所有路径下写回均安全、不会覆盖未初始化 settings；写回后成员 settings = 组 settings，若后续重新入组由 `InitFrom` 再次同步。

### 4.2 与其他 mod 的兼容性

- 对外形态 = **1.6 原版容器型存储**（`Building_OutfitStand`/`Building_Bookcase` 同类）——任何用 `IHaulDestination`/`IHaulSource`/`IThingHolder`/`IStoreSettingsParent` 通用接口工作的 mod（自动搬运、工作台 mod、传送带 mod 等）自动兼容。
- 不实现 `ISlotGroupParent` → 不参与 `StorageGroup`、不干扰格子型存储体系；`BillUtility.Notify_ISlotGroupRemoved`、`GenThing.TryDirtyAdjacentGroupContainers` 等格子型钩子无需处理。
- 已知差异（可接受或可选 patch，见 §5）：虚拟物品不在 `map.listerThings` 中 → **不直接计入交易列表与财富统计**（与衣柜/书架行为一致）。
- **filter 语义差异（重要，见 §6.2）**：原版"禁止 = 已有物品被搬运工搬走"在本系统不适用——禁止 = 不可见、不可取（视图里没有该物品）。移出必须走显式操作（§6.3 视图放行 / §6.4 管理面板弹出）。
- **存储设置剪贴板完全兼容（零 patch，已查证）**：
  - 复制：`StorageSettingsClipboard.Copy(ourSettings)` 把 Priority + filter（标准 `ThingFilter`）写入全局静态剪贴板 → 可粘贴到任何原版存储/其他 mod 容器；
  - 粘贴：`ourSettings.CopyFrom(clipboard)` → `TryNotifyChanged` → 本建筑 `Notify_SettingsChanged`（listerHaulables 重算 + 视图重建）→ 原版 filter 的 `Allows` 直接驱动我们的"可见/可存取"语义，无需转换；Priority setter 同时触发 `HaulDestinationManager` 重排序；
  - **多选同步粘贴**：`CopyPasteGizmosFor` 的 gizmo 无 groupKey（默认 -1），同 def 建筑 `hotKey/Label/icon` 相同 → `GizmoGridDrawer` 合并；`Gizmo.alsoClickIfOtherInGroupClicked = true`（`Verse/Gizmo.cs:18`，默认）→ 点击粘贴时组内**每个**建筑各自的 action 都执行（闭包捕获各自 settings）→ 与 `Building_Storage` 多选粘贴行为一致；
  - **混合多选**（本建筑 + 原版架子/仓储区/其他 mod 存储）同样合并同步——原版行为，无风险；
  - **已知取舍**：剪贴板只覆盖 filter + 优先级，**不覆盖出入开关**（`noDeposit`/`noWithdraw`/`allowTakeForUse` 不属于 `StorageSettings`）——粘贴后出入模式保持各建筑自身状态；如需"复制全部设置"（含出入模式），需自定义复制 gizmo（可选增强，默认用原版）。
- **StorageGroup（存储组）兼容（默认启用，§4.1e）**：实现 `IStorageGroupMember` + def `<storageGroupTag>FAOC_Vault</storageGroupTag>`，组处理逐行照抄 `Building_Storage`——组 gizmo/连接线/检查字符串/蓝图 Frame 继承组/跨地图自动断开全部原版自动；语义：组 = 共享 filter + 优先级（玩家自愿），内容始终全局共享（正交），出入开关/放行列表不共享；自定义 tag 与原版 `Shelf`/`Bookcase` 等组体系隔离；不组时"多选同步粘贴"仍可用于批量设置独立 filter（上一项）。

---

## 5. 需要修改原版的部分（完整 patch 清单）

> 用户特别关注此项。修订后清单：**1 个必需 + 9 个可选**（其中 4 个默认启用：#5 取出同步、#6 锁定短路、#8 预留记账、#9 数量替换——#9 是"无限容量对外可见"的关键）。

### 5.1 必需（1 个）

**`ThingOwnerUtility.TryGetFixedTemperature`**（`Verse/ThingOwnerUtility.cs:401`）— Harmony：

- 原因：`Thing.AmbientTemperature` 对容器内物品查此方法（`Verse/Thing.cs:389-391`），而它是硬编码 switch，无法从 mod 扩展；不 patch 时容器内物品的温度读数 = **建筑所在格温度**（`AmbientTemperature` 链上失败后走 `SpawnedOrAnyParentSpawned` 分支，`Thing.cs:379-398`；仅未 Spawned 且无 Tile 时才回落 21°C），"冻结"语义在 UI 上不完整。
- **方法签名（决定 patch 写法）**：`public static bool TryGetFixedTemperature(IThingHolder holder, Thing forThing, out float temperature)`——`__result` 对应 **bool 返回值**，温度必须写入 **out 参数**（Harmony 中声明 `ref float temperature`）。
- 正确写法（prefix）：

```csharp
static bool Prefix(IThingHolder holder, ref float temperature, out bool __result)
{
    if (holder is Building_GlobalVault)   // 严格限定本 mod holder，其余放行
    {
        temperature = -30f;   // 显示为"冷冻"
        __result = true;
        return false;         // 短路原逻辑
    }
    __result = false;         // 或让原逻辑执行
    return true;
}
```

- 备注：`TryGetFixedTemperature` 是热门 mod patch 点，也可改用 **postfix**（原方法返回 false 时覆盖 `ref temperature` 并 `__result = true`）天然与其他 mod 共存；物品本身不 tick，温度只影响读数与取出瞬间的腐败速率起点，不会破坏任何原版计算。

### 5.2 可选（按需启用，均有本项目先例）

| # | 目标 | 目的 | 本项目先例 |
|---|---|---|---|
| 1 | `WealthWatcher.WealthItems`（getter） | 虚拟物品计入殖民地财富（默认不计，与衣柜一致；启用则与普通存储一致） | `Patch_WealthWatcher_Items`（SubspaceAssetBlackhole.cs） |
| 2 | `TradeUtility.AllSellableThings` / 交易列表构建 | 虚拟物品可直接交易（需要把条目物化进交易 UI；工作量中等） | — |
| 3 | `ResourceCounter` 相关计数 | 全局存储计入资源计数（部分 UI 已通过 §5.2 #9 的数量替换覆盖） | `Pawn_CarryTracker.TryStartCarry` 中的 `resourceCounter.UpdateResourceCounts` |
| 4 | `ThingListGroupHelper`/`def.thingClass` 相关 | 无需 patch：`Building_GlobalVault` 的 thingClass 实现 `IHaulSource` 后，`ThingListGroupHelper.cs:196-197` 的判定自动生效 | — |
| 5 | **`Thing.SplitOff`（只 patch virtual 声明处一处）**（默认启用） | 取出即同步 + 防超卖（§3.3）：`ThingWithComps.SplitOff`/`MinifiedThing.SplitOff` 均为 `base.SplitOff` 薄包装，patch 一处覆盖全部，**三处全 patch 会 prefix/postfix 双触发**。prefix 实时校正副本 = min(G−R+r_this, stackLimit)；postfix 判 `__result == __instance`（整堆分支，`Notify_ItemRemoved` 已同步）跳过差额扣减，否则按差额扣减全局，并即时补回副本 = min(全局剩余, stackLimit)。**配套：一并 patch `Thing.TryAbsorbStack` / `ThingWithComps.TryAbsorbStack`**——SplitOff 调用方失败回滚（`TakeToInventory`/`TryTransferToContainer`）时 piece 合并回副本，须把 `piece.stackCount` 补回全局（§3.3 回滚补偿） | 本项目 Harmony patch 常规手法 |
| 6 | **`ListerHaulables.ShouldBeHaulable`**（**必需，默认启用**） | 短路条件精确为"`t.ParentHolder` 是本系统建筑/视图且 `Accepts(t) == true`"→ 直接返回 false（锁定，§1.6）：跳过 `IsInValidBestStorage`（内含 `TryFindBestBetterStorageFor` 全图搜索）把每次 Check 降为 O(1)；**放行条目（Accepts=false）绝不短路**（否则 §6.3 移出机制失效）；此 patch 同时消除 M10 无限搬运循环。注意频率：原版 `ListerHaulablesTick` 每 tick 运行，HaulSource 数 ≤4 时**每个每 tick 全量 Check**（不是"每 4 tick"） | — |
| 7 | **使用路径放宽**（`FoodUtility.BestFoodSourceOnMap` + `HealthAIUtility` 相关搜索 + `WorkGiver_DoBill.TryFindBestIngredientsHelper`，可选，由 `allowTakeForUse` 驱动） | "禁止取出 + 允许拿取"模式：`HaulSourceEnabled` 检查放宽为 `\|\| (holder is Building_GlobalVault v && v.AllowTakeForUse)`；**不放宽** `ListerHaulables.ShouldBeHaulable`（搬运工 haul 仍禁止）；仅当某建筑开关开启时生效。**注意：不要 patch `GenClosest.ClosestThing_Global_Reachable`**——其 `canLookInHaulableSources` 参数原版无任何调用方传 true，patch 无效；食物/治疗的真实路径是 FoodUtility/HealthAIUtility（§1.3-3） | 本项目 Harmony patch 常规手法 |
| 8 | **`ReservationManager` 族**（`CanReserve`/`Reserve`/`CanReserveStack`/`Release`/`ReleaseAllForTarget`/`ReleaseClaimedBy`/`ReleaseAllClaimedBy`，默认启用） | 预留记账（§3.3）：对视图/退休副本 target 用全局可用量 `G−R` 做数量检查（替代原版 `num1 = target.Thing.stackCount`），阻止超卖预留；**在 CanReserve 入口无条件执行检查**（不因 `ignoreOtherReservations` 跳过，防 playerForced 强抢已锁实物）；对本系统条目数量不足**静默返回 false**（不打 Log.Error，防刷屏）；`stackCount = -1`（StackCount_All）按"该副本当前 stackCount"计（原版语义，无解析表）；释放路径顺带执行退休副本销毁检查（§3.3） | 薄封装，本项目 Harmony patch 常规手法 |
| 9 | **数量替换（"无限容量对外可见"）**（**默认启用**）：`WorkGiver_DoBill.TryFindBestIngredientsHelper`/`TryFindBestBillIngredientsInSet` + `Toils_Haul.StartCarryThing` + `RecipeWorkerCounter.CountProducts` | 把视图副本的 `stackCount` 视图替换为全局剩余量：搜索/取料/计数全部用全局数量——否则副本上限 = stackLimit（钢铁=75）导致单份需求 >75 的账单永不生成、连续制作每单空转 8-10 秒、账单"已有数量"被 75 封顶（§3.2/§1.3） | 本项目 Harmony patch 常规手法 |

### 5.3 明确不需要修改的原版部分（验证过）

- `HaulAIUtility.HaulToStorageJob` / `HaulToContainerJob`（存入路径）✓ 原版自动
- `JobDriver_HaulToContainer` / `Toils_Haul.DepositHauledThingInContainer`（存入动作）✓ 原版自动
- `WorkGiver_DoBill.TryFindBestIngredientsHelper`（工作台原料**接入**）✓ 原版自动（半径限制见 §7 扩展；**数量替换**见 §5.2 #9）
- `RecipeWorkerCounter.CountProducts`（计数**接入**）✓ 原版自动（**数量替换**见 §5.2 #9）
- `GenClosest.ClosestThingReachable`（装备/武器类取用）✓ 原版自动；食物/治疗不搜索 HaulSource，需 §5.2 #7（如实说明，非"零 patch"）
- `StoreUtility` / `HaulDestinationManager` / `ListerHaulables`（存储选择、优先级、haulable 判定**框架**）✓ 原版自动（锁定短路见 §5.2 #6）
- `ITab_Storage` / `ITab_ContentsBase` / `StorageSettingsClipboard`（UI）✓ 原版自动（复制/粘贴/多选同步零 patch，§4.2）
- `SlotGroup` / `StorageGroup` / `Building_Storage`（格子型体系，完全绕开）✓ 不触碰

---

## 6. 玩家取出、使用与转移操作

### 6.1 操作矩阵

| 玩家操作 | 入口 | 实现方式 | 原版对接 |
|---|---|---|---|
| 手动取出若干 | 内容 Tab 行按钮（数量滑条） | 全局 `Withdraw(条目, count)` → 按 `stackLimit` 分批物化 → `GenDrop.TryDropSpawn`（掉在建筑旁） | `ITab_ContentsBase.OnDropThing` 模式 |
| 指定 pawn 拿到背包 | 右键建筑浮菜单"取 X 到背包"（自定义选项） | 自定义 job：以**建筑**为行走目标（走到 InteractionCell）→ `Toils_Haul.TakeToInventory(targetA, count)`——该 toil 对未 Spawned 目标直接 `thing.SplitOff(num)` + 背包 `TryAdd`（`Toils_Haul.cs:464-510`），无需物化到地上 | `JobDefOf.TakeCountToInventory` 形态；**注意**：原版 `TakeInventory` 系 job 的 `GotoThing` 对未 Spawned 物品无效（无法走到），所以行走目标必须改为建筑本身 |
| 使用（吃/用药品/治疗） | 内容 Tab 行右键"使用" | 物化到 pawn 手中 → 原版 `Toils_Ingest`/治疗路径；或物化到地上后走原版流程 | 自定义浮菜单（仿 `ITab_ContentsOutfitStand` 的行操作） |
| 穿戴 | 内容 Tab 行按钮"穿戴" | 物化 → `pawn.apparel.Wear(...)` | 自定义浮菜单 |
| 移到其他存储（正常存储区） | 建筑 gizmo"移出该类"（视图放行，§6.3 路线 A）→ 搬运工自动搬走；或管理面板强制弹出（§6.4） | §6.3 显式移出 | **零 patch**（原版 haul 全流程） |
| 搬空整个存储 | 建筑"移出全部" / 管理面板全选弹出（§6.4） | 放行全部条目 + 限速物化 | 同上 |

### 6.2 filter 语义与"全禁用死锁"（重要设计约束）

**语义约定（与原版不同，必须文档化）**：

| | 原版存储区 | 本系统（每建筑） |
|---|---|---|
| filter 允许 | 可存入；已有物品不被搬走 | **可存入、可取出、可看到**（视图含该条目） |
| filter 禁止 | 不可存入；**已有物品会被搬运工搬走**（物品仍在格子上可见） | **不可存入；条目从视图移除：不可见、不可取**（原版"搬走"语义不适用——视图里没有该物品，搬运工无从下手） |

**死锁场景**：若存储空间中存在物品 X，而**所有**建筑的 filter 都禁止 X → X 不在任何视图 → 玩家无法通过任何建筑看到、取出或触发搬运 → X 被永久锁在全局存储中。此场景必须有逃生机制（§6.4 全局管理面板）。

**默认配置（防意外吸走）**：
- **默认 filter = 全禁止（空 filter）**：新放置的建筑不接受、不显示任何物品，玩家显式勾选允许类型后才开始工作。理由：无限容量建筑若默认全允许，放置即把殖民地所有可存物品吸进本建筑；而全禁止不会造成死锁——死锁需要"全局层已有物品"，物品只能经由某个允许的建筑存入，与默认状态无关。
- 优先级默认 Normal（与原版一致），玩家可自由调整（§5.2 #6 锁定短路保证跨建筑无自动搬运与反复搬运，§6.3 已分析）。

### 6.3 "移出/转移"操作（显式触发，不再依赖 filter 隐式放行）

"把某类物品移出存储空间、送入正常存储区"是**显式玩家操作**（filter 变化只影响可见性，不自动触发移出）。两条路线：

**路线 A：视图放行（推荐，复用原版 haul 全流程）**
1. 玩家在建筑 gizmo 或管理面板点"移出该类"→ 该条目进入建筑的**放行列表**（临时状态，不修改 filter）。
2. 放行条目**保留在视图**，但 `Accepts(t)` 对该类返回 false → `IsInValidBestStorage=false`（注：不是 `IsInValidStorage`——后者只出现在 `PawnCanAutomaticallyHaul`，自动搬运经 `WorkGiver_Haul.JobOnThing` 用 `PawnCanAutomaticallyHaulFast`，不经过它）→ `ListerHaulables.ShouldBeHaulable=true`（§5.2 #6 短路**只覆盖 Accepts==true 的锁定条目，放行条目不短路**）→ 进入 haulables 集合（`Notify_HaulSourceChanged`，仿 `Building_Bookcase.Notify_SettingsChanged`，`Building_Bookcase.cs:125-130`）。
3. 原版 `WorkGiver_Haul` → `HaulToStorageJob`（目标 = 其他存储区/容器，按优先级/距离选择）→ 走到建筑 → `StartCarryThing` → `SplitOff`（§3.3 即时扣减全局）→ 搬到目标。**每趟 ≤ pawn 携带容量，天然分批、全套原版寻路/动画/优先级/禁止区逻辑**。
4. 搬运完成 → 条目从全局层消失；目标存储无空间时物品留在 pawn 手上/原地（与原版一致），放行状态保留直到清空。
5. 原版右键浮菜单 **"HaulFromSource"**（`HaulSourceUtility.GetFloatMenuOptions`，`RimWorld/HaulSourceUtility.cs:15-47`）在放行条目存在时出现——需建筑在 `GetFloatMenuOptions` 里主动调用它（§4 表已列）。

**防回吸（关键）**：放行期间该类 `Accepts` 返回 false（视图保留条目、**不修改 filter**、条目可 haulable）——否则条目被搬入外部存储后，搬运工又会因本建筑优先级更高把它吸回来（搬出又搬回的反复）。**目标选择必须排除本系统容器**：放行期间条目优先级按 Unstored 算，任何优先级高于 Unstored 且接受的 HaulDestination 都"合格"——若目标选中**另一座** Building_GlobalVault，条目被其 `TryAdd` 吸收回全局层、数量不减 → 放行"搬空"条件永不满足、无限循环。实现方式：放行条目的搬运目标过滤（patch `TryFindBestBetterNonSlotGroupStorageFor` 的该场景或放行改用"物化到地面再分流"，见路线 B 的限速版）。放行列表项 = (条目, 状态)，条目搬空后自动移除、`Accepts` 恢复；可选"移出并禁止"：放行同时把该类写入 filter 禁止（此后永不回吸，玩家需显式改回才恢复）。

**优先级与"反复搬运"分析（M10 已解决）**：原版 `TryFindBestBetterNonSlotGroupStorageFor` 对容器型目标**没有任何空间/容量检查**（`IsGoodStoreCell` 只在格子型路径使用，StoreUtility.cs:222 vs 233-304）——"高优先级但已满不参与"的论证对容器型目标不成立。若不处理，低优先级 Vault 的视图副本会被判 haulable → 搬去高优先级 Vault → 被吸收回全局层（数量不减）→ 低 Vault 视图回补 → 再 haulable → **确定性无限循环**（原 M10）。**已由 §5.2 #6 锁定短路解决**：`Accepts == true` 即不 haulable，条目恒锁定在本建筑视图，跨建筑"自动流出"不存在；唯一流出是显式放行（本条）。残余风险：若 #6 patch 被第三方 mod 冲突禁用，M10 循环会回归——实现期须实测验证 #6 在目标 mod 环境生效（§9 风险表）。

> **已废弃**："两建筑优先级不同导致的 A↔B 反复搬运暂不解决"（旧 M10 决策）已被 §5.2 #6 锁定短路消除，不再成立。

**可选"仅手动存入"模式**：即 §4.1c 的"只出不入"（`HaulDestinationEnabled=false`）——搬运工不再把本建筑作为存储目标（`StoreUtility.TryFindBestBetterNonSlotGroupStorageFor` 检查该标志），物品只能通过 UI/管理面板/传送 API 存入。适合"专用仓库"：外部存储保持一定数量，Vault 只收显式存入的过剩物资。

**路线 B：直接弹出（管理面板）**：见 §6.4——物化到地面（限速分批），落地后由原版 haul 自动分流到允许的存储区。

> 设计约束：**不要**把"filter 变化"当作隐式放行（旧版曾如此设计）——在"禁止=不可见"语义下，被禁条目从视图消失，与"留在视图等搬运"矛盾。移出必须是显式操作，全局管理面板是唯一兜底出口。

### 6.4 全局管理面板（必须实现）

**结论：需要。** 理由：① 全禁用死锁（§6.2）的唯一逃生口；② 跨建筑/跨地图的真实内容总览——任何单建筑 filter 都提供不了全貌（玩家无法回答"我到底存了多少 X"）；③ 搜索与审计；④ 批量强制移出（某类/全部到指定地图）。

**入口**：任意 `Building_GlobalVault` 的 gizmo"打开全局存储管理器"（`Command_Action`）；数据由 `GameComponent_GlobalStorage` 持有，不依赖特定建筑存活。**建筑全部拆除时内容仍保留在全局层**：实现时在拆除最后一个建筑时弹警告（"存储中仍有 N 件物品，请先用管理面板清空"），避免入口消失后内容永久封存。

**功能清单**：

| 功能 | 说明 |
|---|---|
| 内容列表 | 所有 L1 聚合条目（**无视任何建筑 filter**）：图标、名称、数量（long）、分组属性（品质/耐久段）、"当前可见建筑数"（帮助定位死锁条目） |
| 搜索/筛选 | 原版 `QuickSearchWidget`（`ITab_Storage` 同款）按名称/defName 过滤；快捷筛选"当前不可见条目"（= 死锁条目） |
| 强制取出 | 选中条目 → 数量输入（long）/全部 → **弹出到地面**（见下） |
| 强制转移 | 弹出到指定地图（多地图时顶部地图选择器；单地图隐藏）→ 落地后原版 haul 自动分流到允许的存储区 |
| 移出放行 | 对某条目执行 §6.3 路线 A（视图放行），搬运工自动搬往允许的存储区 |
| 统计 | 总条目数、总数量、条目内个体数（L2）；可选估计财富（复用 §5.2 财富逻辑） |

**弹出机制（性能安全）**：
- 弹出队列挂在**全局层**（`VaultEjectQueue`）：条目 + 目标地图 + 剩余数量，与建筑生命周期解耦（建筑拆除后队列继续）。
- 每 tick 物化 ≤ 4 堆（每堆 ≤ `stackLimit`），`GenDrop.TryDropSpawn` 到目标地图安全空位（优先 Vault 建筑 InteractionCell 附近，其次地图内可达空位）。
- 落地物品是普通实体 → 原版 haul 自动搬入允许的存储区（无允许存储则原地滞留，与原版一致）。
- 百万级弹出：队列持续运行直到清空，期间无卡顿；UI 显示进度与剩余数量。

**UI 性能**：列表行数 = L1 条目数（组合级，通常几十~几百）→ ScrollView 直接渲染；L2 精细条目默认折叠为汇总行，展开仅加载前 N 条明细 + "加载更多"（避免百万行渲染）。

**实现参考**：`Dialog_ManageOutfits`/`Dialog_ManageDrugPolicies` 的列表窗口形态 + `QuickSearchWidget` + `ITab_ContentsBase.DoThingRow` 行渲染模式。

### 6.5 已知问题与解决

| 问题 | 解决 |
|---|---|
| `int` 上限：`stackCount`/UI 滑条/`SplitOff` 参数都是 `int`（约 21 亿） | 全局计数存 `long`；UI 数量输入与"全部取出"用自定义 long 版（分批循环直到清空）；`stackCount` 只承载 `min(count, int.MaxValue)` 的显示值 |
| 物化堆叠超过 `stackLimit`（破坏原版合并/放置/搬运假设） | 原版 `GenPlace`（GenPlace.cs:251）放置时已自动按 stackLimit 拆分；本设计物化仍按 `stackLimit` 分批（保险，且 `GenDrop.TryDropSpawn` 本身不查 stackLimit、会把剩余留在传入 thing 中——调用方必须处理剩余，否则丢数量） |
| 取出物属性与分组键漂移（`SplitOff` 部分拆分的 piece 由 `ThingMaker.MakeThing` 重建，只复制 stackCount/HitPoints，其余依赖 comp 的 `PostSplitOff`——第三方 comp 空实现时会漂移） | 默认 L1 聚合模式下可接受（取出物属性以 piece 为准，分组键漂移只影响后续合并粒度）；**L2 个体模式必须避开 SplitOff**：全局 `Withdraw` 直接用 `ThingMaker.MakeThing` + 从全局条目 proto 显式恢复全部属性（§3.5 取出路径） |
| 百万级"取出全部"瞬时物化爆炸（渲染/寻路/合并/存档瞬间激增） | 取出限速：每 tick 物化一批（如 4 堆），或优先走"移出 + 搬运"路径（按携带量天然分批）；UI 提示剩余数量 |
| `SplitOff` 不通知容器 → 计数漂移 | patch `Thing.SplitOff`（§3.3 / §5.2 第 5 项） |
| 跨地图取用 | 原版 job 无法跨地图寻路：取用/搬运动作只发生在 pawn 所在地图上的建筑；内容由全局层共享，B 地图建筑取出即从全局扣减（A 地图数量同步为 0） |
| 锁定条目被误搬 | 建筑 `Accepts` 对视图内条目恒真 + 高优先级 → `IsInValidBestStorage` 恒真 → 不 haulable；只有玩家显式"移出"（§6.3）才放行 |
| "HaulFromSource"/放行后目标存储无空间 | 与原版一致：`JobFailReason`（"没有空位"），物品留在存储中不丢失 |
| 放行的条目在搬运途中被再次存入本建筑 | 不可能：放行状态使 `Accepts` 返回 false；搬运途中物品在 pawn 手里 |
| **所有建筑都禁用某物品 → 完全无法访问（死锁）** | 全局管理面板（§6.4）无视 filter 查看并强制弹出/放行——唯一出口，必须实现 |

---

## 7. 扩展设计：标记 pawn 工作台原地取存

### 7.1 目标

被标记（特定装备/植入物/hediff，例如"量子链路植入体"）的我方 pawn，在**任意工作台**制作时：
- 原料不走到存储建筑拿，而是**原地**（工作台旁/手中）从全局存储取；
- 产物/收获物**原地**存入全局存储。

### 7.2 原版取料链路回顾（含天然限制与 1.6 原生免行走分支）

1. `WorkGiver_DoBill.TryFindBestIngredientsHelper` 的 HaulSource 分支要求：
   `holder is Thing t && t.Spawned && t.Position.InHorDistOf(billGiver.Position, searchRadius)`（`WorkGiver_DoBill.cs:357-371`）→ **有距离限制、且只查当前地图**。
2. `JobDriver_DoBill` 收集原料 toil 走 `Toils_Goto.GotoThing(ingredientInd)` → 走到存储建筑 → `StartCarryThing` → `TryStartCarry`（SplitOff）。→ **取料动作绑定"走到建筑"**。
3. **产物链（修正）**：制作完成后 `Toils_Recipe.FinishRecipeAndStartStoringProduct`（`Toils_Recipe.cs:152-270`）按 `BillStoreMode` 三分支：DropOnFloor 模式 `GenPlace.TryPlaceThing` 就地放置；BestStockpile/SpecificStockpile 模式 pawn 自持产物并立即启动 `HaulToCellStorageJob`——**不存在"放工作台旁等搬运工"的中间态**（`PlaceHauledThingInCell` 是**原料**放置 toil，`JobDriver_DoBill.cs:112`，勿混淆）。
4. **1.6 原生免行走分支（本扩展的锚点）**：`TryFindBestIngredientsHelper` 在 348-356 行有 `billGiver is Building_WorkTableAutonomous → relevantThings.AddRange(innerContainer)`（无 Spawned/距离/地图限制）；`JobDriver_DoBill.cs:82/119` 对自动工作台启用 placeInBillGiver/容器存入分支——方案 A2/B 可直接对齐该形态（让任意工作台走该分支即实现"传送"）。

### 7.3 方案 A（推荐）：patch 取料/放料两处，传送语义

**A1. 原料搜索**（`WorkGiver_DoBill.TryFindBestIngredientsHelper`，prefix/postfix）：
- 当 `pawn` 带标记时，额外把**所有地图**上我们存储建筑中 `filter.Allows(原料)` 的全局条目加入 `relevantThings`（绕过 Spawned/距离/地图限制，形态对齐 7.2-4 的 WorkTableAutonomous 分支），并把它们标记为"可传送原料"（例如用一个 `HashSet<Thing>` 静态表记录本次 job 的传送清单，或给代表 Thing 挂临时标记；**注意静态表须在 job 结束/中断时清理**）。
- `TryFindBestBillIngredientsInSet`（选择逻辑）不用动 —— 候选列表多出虚拟条目即可。

**A2. 取料动作**（`JobDriver_DoBill` 的收集 toil 或 `Toils_Haul.StartCarryThing`，prefix）：
- 当目标原料是"可传送原料"且 pawn 带标记：跳过 `GotoThing`，改为**传送物化** —— 从全局存储 `Withdraw(条目, count)` → `ThingMaker.MakeThing` + 恢复属性 → `pawn.carryTracker.TryStartCarry(thing)`（或直接放入工作台 `innerContainer` 若工作台是容器型，**参照** `JumpIfTargetInsideBillGiver` 的免行走语义——注意该方法是 `private static`（`JobDriver_DoBill.cs:123-144`），不能直接调用，需 transpiler 或复制逻辑）。保留 reservation/计数逻辑与原版一致。

**A3. 产物原地存入**（**patch 点修正：`Toils_Recipe.FinishRecipeAndStartStoringProduct`**，而非"产物放置 toil"）：
- patch 其 initAction 在 `MakeRecipeProducts`（`Toils_Recipe.cs:179`）之后、pawn `TryStartCarry` 之前：若 pawn 带标记 → 直接 `GlobalStorage.Deposit(产物)`（跳过放置/自持搬运），并给出浮字提示（"已存入量子存储"）。
- **必须按 `BillStoreMode` 三分支区分**：DropOnFloor 模式是否拦截需单独决策（拦截则产物不进世界直接入库；不拦截则维持原版就地放置语义）。

优点：不改变非标记 pawn 的任何行为；原版失败回退路径完整（传送失败 → 走原版行走路径）；实现集中在 2~3 个 patch。
缺点：需要处理 job 计数/reservation 与原版的差异（难度中等，1.6 的 `Building_WorkTableAutonomous` 容器分支是现成参照）。

### 7.4 方案 B（低风险替代）：工作台"关联存储"容器

仿 `Building_WorkTableAutonomous`（`RimWorld/Building_WorkTableAutonomous.cs`）——**1.6 已原生内置该机制**（7.2-4），方案 B 本质是把该分支扩展到普通工作台：给工作台加一个"关联到最近存储建筑"的引用与小型 `ThingOwner`（或直接透传视图）。
- 原料：patch 让任意工作台走 `WorkGiver_DoBill.cs:348-356` 的 innerContainer 候选分支（关联对象内容），改动小于 A1；
- 取料：原料"传送"进工作台容器，pawn 走到工作台即取（`JobDriver_DoBill.cs:82/119` 的 placeInBillGiver/容器存入路径已支持"原料在工作台内"的免行走）；
- 产物：`Notify_FormingCompleted` 后由搬运工/标记 pawn 存入。

优点：改动最小、最贴近原版（工作台容器是原版机制，直接扩展原生分支）。
缺点：需玩家手动"关联"工作台与存储建筑（或自动找最近）；"原地"体验弱于 A（原料先入工作台容器）。

### 7.5 方案 C（不推荐）：工作台自身成为 HaulSource 镜像

工作台实现 `IHaulSource` 直接暴露全局存储内容。改动面大（所有工作台类都要处理），与大量工作台 mod 的交互风险高。仅作记录。

> 建议：**先交付 §2-§6 的存储本体与玩家操作，扩展按 A 实现**（A1/A2/A3 可独立分阶段启用）。

---

## 8. 分阶段实现计划（建议）

| 阶段 | 内容 | 验证标准 |
|---|---|---|
| P0 | `GameComponent_GlobalStorage` + 聚合条目 + 存取 API + 存档 | 单机调试：Deposit/Withdraw 正确、读档保留 |
| P1 | `Building_GlobalVault` + 视图 ThingOwner + HaulDestination/Source 接入 + `SplitOff` 同步 patch（含 TryAbsorbStack 回滚补偿）+ 预留记账（§3.3）+ 视图重建（增量变更日志 + 退休副本生命周期 + 设置签名检查，§3.3）+ 锁定短路 #6 + 数量替换 #9 | 搬运工可存入/取出；工作台大订单（单份需求 > stackLimit）可用原料且连续制作无空转；装备取用可用（食物/治疗依赖 #7，P1 不验证）；并发场景一致：A 保留期间 B 按数量可/不可保留，取物不因视图重建失败；高频变动 + 超大库存下重建成本随变化量而非总量 |
| P2 | UI：ITab_Contents 子类、InspectString、gizmo、filter 隔离验证 + 出入双开关（§4.1c/d，含多选同步）+ 存储组（§4.1e） | 两建筑不同 filter：A 存 B 不可见/不可取；双开关各模式生效；多选建筑同步切换；组内共享 filter、取消链接恢复独立 |
| P3 | 冻结 + 温度 patch（§5.1） | 食物存入后 rot 不推进；取出恢复原版腐败 |
| P4 | 跨地图验证 + 变更驱动同步 + 移出/转移路径（§6.3）+ 防回吸/优先级验证（含 M10 消除：锁定 #6 生效，优先级调整无反复搬运）+ minify 验证 + 性能基准 | 两张地图共享内容；移出后不回吸（目标排除本系统容器）；#6 锁定短路在目标 mod 环境生效（§11 V1）；minify/放回内容不丢；10 万钢铁/1 万食物/1000 武器无卡顿 |
| P5 | §6 操作补齐：取到背包 job、使用/穿戴浮菜单、"取出全部"限速 + **§6.4 全局管理面板**（列表/搜索/强制弹出/移出放行/死锁逃生） | 玩家各操作路径可用；全禁用条目可经面板取出；百万条目取出不卡 |
| P6 | 扩展 §7（A1→A2→A3） | 标记 pawn 原地取料/存产物 |

---

## 9. 风险与已知取舍

| 风险/取舍 | 说明与对策 |
|---|---|
| 大堆叠代表 Thing | `stackCount` 是 `int`（上限约 21 亿）；真实计数存 `long`。绝大多数代码只读它做"可用量"判断，唯一注意点是 `SplitOff` 上限（取物天然受 carryTracker 上限约束）与 UI 显示（自定义格式 `x1,000,000`）。超过 21 亿的显示需自定义 label（报告 §3.2 已按此设计） |
| 存档体积 | 默认 L1 聚合深存，量级=组合数。L2 个体模式用自定义紧凑序列化（单个字符串字段装压缩数据），百万个体 ≈ 100MB+，属用户显式选择 |
| 百万级"完全无法合并"的物品（如耐久各异的武器） | 默认经 hp 段(10%)聚合到 L1 组合级（§3.5），性能与个体数无关；确需逐个体保留时启用 L2，此时内存/存档/取物与个体数线性相关——这是"保留每个个体独立属性"的物理下限，原版直接存放只会更差 |
| 交易/财富不可见 | 与原版衣柜/书架一致；§5.2 提供了可选 patch |
| 跨建筑数量同步延迟 | 数量一致性在变更点即时（§3.3，SplitOff postfix 即时补回副本）；跨建筑视图刷新最多滞后 60 tick（版本号比较）；工作台原料/取料/计数经 §5.2 #9 使用全局数量，不受视图刷新滞后影响 |
| 与格子型 mod 的差异 | 我们不是 `ISlotGroupParent`，依赖 1.6 的 `HaulToContainer` 路径；若目标环境有 mod 强行要求 `ISlotGroupParent`，可加"可选 SlotGroup 兼容模式"（占用格子在 thingGrid 注册占位 + 虚拟 HeldThings），成本较高，默认不做 |
| `TryGetFixedTemperature` patch 的影响面 | 仅对我们的 holder 类型短路，不影响原版类型分支；该方法属热门 mod patch 点，必要时改用 postfix 降低冲突（§5.1） |
| 管理面板列表渲染 | 行数 = L1 组合级条目（几十~几百），ScrollView 直接渲染；L2 明细折叠 + 分页加载，避免百万行渲染；搜索用原版 `QuickSearchWidget` 先行过滤 |
| 存储组（§4.1e）语义边界 | 组共享 filter + 优先级、不共享出入开关/放行列表；取消链接保留最新组设置（对齐原版 SetStorageGroup 写回）；组 filter 变更经 §3.3 设置签名检查刷新视图（原版组通知链不覆盖容器型成员）；组不可跨地图（原版限制）；均已文档化 |
| 预留记账（§3.3） | 保留检查为 O(保留数) 扫描（保留数通常 <100、取物低频，可接受）；`maxPawns=1` 使同建筑同条目同时仅一个取物者（原版堆叠语义，不同建筑副本互不影响）；`-1` 按副本当前 stackCount 计（无解析表）；CanReserve 无条件检查防 playerForced 强抢；本系统拒绝静默不打 Log.Error；预留中条目禁 L2 分裂 |
| **ListerHaulables 每 tick 全量 Check**（HaulSource 数 ≤4 时） | §5.2 #6 锁定短路（**必需，默认启用**）把每次 Check 降为 O(1) 比较；放行条目少（真实 Check）可接受。另注意原版轮转覆盖缺口（HaulSource ≥6 时部分来源永不周期检查，靠事件通知兜底，非本系统问题） |
| **锁定短路 #6 失效的残余风险** | 若 #6 被第三方 mod 冲突禁用，§6.3 所述 M10 无限搬运循环会回归；实现期须实测验证 #6 在目标 mod 环境生效（P4 验证标准含此项） |
| **`GetInspectString` 每帧调用** | 已修正：摘要缓存 + 版本号失效（§4 表），避免每帧拼接几百条 L1 条目 |
| **存入路径通知粒度** | 已修正：吸收 `TryAdd` 用单物品 `Notify_AddedThing`（O(1)）；`Notify_ItemAdded` 不用于存入路径（§3.2 单一入口）；全量 `Notify_HaulSourceChanged` 仅限设置变化（§4 表），避免搬运工高频存入时 O(L1)×频率 |
| 视图重建 | **增量变更日志（§3.3，方案 A 定案）**：成本 O(变化量×建筑数)/60tick，与库存总量解耦；溢出（>4096/窗口）回退全量；变更日志/版本号不序列化、版本号重置在 ExposeData(PostLoadInit)（早于 SpawnSetup）；版本号 int 回绕处理；退休副本生命周期已补全（§3.3） |
| 预留记账扫描 | 每次保留/取物 O(保留数)（<100），取物/保留低频；如未来压力大可缓存 R（reservation 变更时增量更新），默认不必要 |

---

## 10. 结论

1. **可行，且大部分行为复用原版**：1.6 的"容器型存储"（`Building_OutfitStand`/`Building_Bookcase`）已把 `IHaulDestination + IHaulSource + IThingHolder + IStoreSettingsParent` 的存入/取出/装备取用/UI 框架打通；本项目按同一形态实现建筑，把 ThingOwner 换成"全局存储的视图缓存"，另需 §5.2 的 patch 清单补足**锁定短路（#6）、数量替换（#9）、食物/治疗路径（#7）**——"零 patch"覆盖面已如实修订（§5.3）。
2. **冻结是免费的**：未 Spawned + 不 tick + `dontTickContents`，仅需 1 个温度读数 patch 完善语义。
3. **性能的关键是"聚合 + 变更驱动"**：全局层按属性同质分组（L1 条目数 = 组合数），视图/UI/对账/Check 只看 L1 与物品总量无关；数量同步在变更点即时完成（`SplitOff` patch），无常驻扫描。
4. **独立模块化**：`GameComponent` 全局层 + 建筑层 + 视图层三个模块，与现有项目（OmniCrafter 等）无耦合，可独立交付。
5. **扩展（原地取存）可行**：推荐方案 A（patch `WorkGiver_DoBill` 搜索 + 取料/放料 toil），非标记 pawn 行为零影响。
6. **全禁用死锁由全局管理面板兜底**：每建筑 filter 决定"可见/可存取"（原版"禁止即搬走"语义不适用），全局面板无视 filter 提供真实内容总览、搜索与强制弹出——任何条目都能离开存储空间。

---

## 11. 已知限制与待决问题（设计审查记录）

> 第二版设计审查（对照 `GlobalStorageDesign_issue.md` 逐项修订）发现的问题已全部修正到正文：高严重度项（M10 循环、取出侧可见性、SplitOff 双触发/双扣/回滚、view owner、食物治疗路径、读档时序、温度 patch 写法、-1 语义、组通知链、60vs250 tick、退休列表、TryAdd 重载、产物 toil）与中低项均已按报告建议落地（§1.3/§1.4/§1.5/§1.6、§3.2/§3.3、§4/§4.1、§5、§6.3/§6.5、§7、§9），正文即为权威。本节仅保留**待实现期验证**与**未拍板**的内容。

### 待实现期验证（已决断，保留备忘）

| # | 项 | 决断 |
|---|---|---|
| V1 | M10 循环的消除依赖 §5.2 #6 锁定短路 patch | 已由设计推演定案（§6.3）；**实现期须实测验证 #6 在目标 mod 环境生效**（第三方 mod 冲突会使其失效、M10 回归，§9 风险表）；P4 验证标准含"优先级调整无反复搬运" |
| V2 | 数量替换 patch（§5.2 #9）的工作台吞吐 | P1/P4 实测：单份需求 > stackLimit 的账单可开工、连续制作无 8-10 秒空转 |

### 可选增强（待决，未拍板）

| # | 增强 | 说明 |
|---|---|---|
| E1 | 取物 job 防中断优化 | **已定案**：视图重建方案 A（数字更新 + 退休副本生命周期 + 增量变更日志，§3.3）与预留记账互补——数量一致性由预留记账保证（保留期锁定实物），实例稳定性由退休副本生命周期保证（job 引用的副本退休保留，reservation 释放后销毁；**已废除"永不销毁"旧表述**）；实施于 P1/P4 |
| E2 | "复制全部设置"gizmo | 自定义复制按钮：filter + 优先级 + 出入开关一并复制粘贴；原版 `StorageSettingsClipboard` 只覆盖 filter + 优先级（§4.2） |
| E3 | 管理面板远程切换出入模式 | 每建筑行显示/切换三开关（§4.1d 标注的可选功能） |
| E4 | 非默认状态 MapOverlay 图标 | 建筑处于非默认出入状态时在地图上绘制小图标（§4.1d 标注的可选增强） |
| E5 | 可选 SlotGroup 兼容模式 | 对强行要求 `ISlotGroupParent` 的 mod 提供格子占位 + 虚拟 HeldThings（§9 风险表，成本较高） |
