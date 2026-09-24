# 万能工作站「虚拟存储」设计方案（Virtual Store）

> 状态：**设计已确认，尚未实现**
> 目标建筑：`OmniWorkstation`（万能工作站）
> 参照实现：`RimWorld/Building_Bookcase.cs`（原版书架，容器式存储）
> 事实来源：rimsage 查询 RimWorld 1.6 反编译源码（`Assembly-CSharp.dll`）+ 本项目既有文档 `docs/vanilla-storage-buildings.md`

---

## 1. 目标与已确认需求

给万能工作站挂一个"看起来像书架、但物品看不见"的存储：

1. **容器式存储**，物品真实存在于建筑内部容器中，不占地图格、不渲染、不可单独点击选中。
2. 玩家通过一个**类似万能制造机（`Dialog_OmniCrafter`）的界面**为每种 `ThingDef` 设定**目标数量 N**，存储**始终维持 N 个**（取走多少就补回多少）。
3. **取出不消耗电力/资源**；**不限制目标数量上限**。
4. **只能取出，不能存入**（任何原版/第三方搬运路径都不得把它当存储目的地）。
5. **一个停用开关**：关闭 = **直接销毁**容器内全部物品 + **暂停自动补货**；清单保留，重新开启即恢复。
6. **"丢到地上"按钮**：把容器内物品全部丢到建筑附近地面（不是销毁）。
7. 生效范围：**全殖民地通用**（任何 Pawn 的制作取料等原版路径都能取用）。
8. **施工自动配送本期不做**，方案见第 8 节（二期）。

### 本轮已定项

| 决策项 | 结论 |
|---|---|
| 结构 | **采用中间基类 `Building_VirtualStoreHost`**（接口转发集中于基类，Comp 承载全部逻辑） |
| 补货节奏 | **每 tick 节流补货**（`CompTick` 每 tick 对账，单次补货量设上限） |
| 施工配送 | **稍后实现**（二期，第 8 节） |
| 右键菜单 | **丢到地上**（不做"取出到手上"的搬运 Job） |

---

## 2. 为什么走"容器式"而不是"投影式"

项目里 `OuterrealmStorage`（超维存储仓）用"伪 Spawn 投影 + 大量 patch"实现了"看不见但能取出"，其维护契约（`Source/FullyAutomaticOmniCrafter/OuterrealmStorage/readme_OuterrealmStorage.md`）明确：**投影不拥有库存**、**禁止把 `MakeThing` 当作取出库存**。本功能的语义是"凭空维持 N 个"，与 vault 的库存守恒不变量冲突，因此**不复用 vault 的权威层，也不复用其投影类型或弱标记**（否则会被 `OuterrealmSourceResolver` 误判）。

原版 `Building_Bookcase` 提供了现成的极简范式：内部 `ThingOwner` + 若干原版接口，**几乎零 patch** 即被原版搬运/制作体系接纳。

### 2.1 与既有 vault 设计文档的边界

`Source/FullyAutomaticOmniCrafter/Docs/OuterrealmVaultStorageCellDesign.md`（vault「存储格 + 预留驱动」v4）描述的是**超维存储仓**的物化机制（全局层 → 存储格 → 预留时真 Spawn → 释放回收），其核心不变量是**库存守恒**。本方案是**凭空补货**的虚拟源，目标不同、互不复用：不复用 `OuterrealmEntry` / `OuterrealmVaultViewThingOwner`，也不接入 vault 的 Reserve/Release 物化挂点。

该文档确认的两条事实对本方案的影响：

- `<surfaceType>Item</surfaceType>` 只对"提供存储格（`ISlotGroupParent`）"的建筑必需（否则 `StoreUtility.NoStorageBlockersIn` 会把建筑判为存储 blocker、格子不可用）。本方案**不提供存储格**，因此 `OmniWorkstation` **无需**该字段。
- `maxItemsInCell`（每格最多堆数）是**格子式**存储的容量约束；本方案走容器式，容量不受它限制——这正是不限数量上限的基础。

---

## 3. 已核实的关键技术事实

| 事实 | 证据 | 意义 |
|---|---|---|
| `Thing.SpawnSetup` 按 `this is IHaulSource / IHaulDestination` **自动注册**到 `map.haulDestinationManager` | `Verse/Thing.cs:599-601` | 实现接口即被原版接纳，无需 patch；**接口必须由 Thing（建筑）类实现，Comp 无法代劳** |
| 原版制作选料遍历 `AllHaulSourcesListForReading` 并用 `ThingOwnerUtility.GetAllThingsRecursively(holder)` 取内容物 | `RimWorld/WorkGiver_DoBill.cs:357` 起 | 容器内容物**直接成为原料候选**；且要求 `holder is Thing t && t.Spawned && t.Position...` |
| 代理选料 patch 同样遍历 haul sources | `Source/FullyAutomaticOmniCrafter/Patch_OmniWorkIngredientSearch.cs` | 万能工作站代理能从自己的容器取料（核心目标） |
| `IHaulSource : IStoreSettingsParent, IThingHolder` | `RimWorld/IHaulSource.cs:12-17` | 接口清单 |
| `IStoreSettingsParent` 成员：`StorageTabVisible` / `GetStoreSettings` / `GetParentStoreSettings` / `Notify_SettingsChanged` | `RimWorld/IStoreSettingsParent.cs:10-19` | 需实现 4 个成员；用**显式接口实现**返回 `StorageTabVisible => false` 可隐藏原版存储页签与会调用它的 gizmo（`ITab_Storage.cs:73`、`StorageGroupUtility.cs:33`） |
| `ThingOwner<T>.TryAdd(item, canMergeWithExistingStacks: true)` 自动按 `stackLimit` 合并/开新堆，仅受 `maxStacks` 限制 | `Verse/ThingOwner\`1.cs:129-177` | 补货只需 `MakeThing` + `TryAdd`，无需手工分堆；容器容量不受 1×1 单格限制 |
| 所有存储判定都经过 `Accepts`：`StoreUtility.cs:35/46/52/64/93/257` | `RimWorld/StoreUtility.cs` | **`Accepts` 恒 false = 彻底不能存入**，无需额外 patch |
| `ThingComp.CompGetGizmosExtra()` / `CompFloatMenuOptions(Pawn)` 由 `ThingWithComps` 自动收集 | 大量原版 Comp 覆写，如 `CompFlickable.cs:89`、`CompBiosculpterPod.cs:383/519`、`CompMannable.cs:56`、`CompPushable.cs:30` | **按钮与右键菜单可全部写在 Comp 里**，建筑侧零改动；`Building_OmniWorkstation.GetGizmos` 已调用 `base.GetGizmos()`（`OmniWorkstation.cs:281-283`） |
| `Building_Bookcase` 范式：`ThingOwner` 容器 + `Accepts` + `HaulSourceEnabled` + `ExposeData` 存容器；`MaximumBooks = def.building.maxItemsInCell * def.size.Area` | `RimWorld/Building_Bookcase.cs` | 本方案骨架 |
| `HaulSourceUtility.GetFloatMenuOptions` 只生成"搬到更好的存储"（`HaulFromSource`），不含"取到手上" | `RimWorld/HaulSourceUtility.cs:15-47` | 玩家手动取出需自建入口（本方案用"丢到地上"） |
| `Accepts` / `SpaceRemainingFor` 会在搬运寻路中被**高频调用**，必须保持轻量 | `docs/vanilla-storage-buildings.md` §5 | `Accepts => false` 天然满足 |

---

## 4. 架构

```
Building_VirtualStoreHost（新增中间基类，约 20 行，纯接口转发）
    : Building, IHaulSource, IHaulDestination, IStoreSettingsParent, IThingHolder
    · Accepts(Thing) => false                          // 不可存入
    · SpaceRemainingFor(ThingDef) => 0
    · HaulDestinationEnabled => false                  // 不作为搬运目的地
    · HaulSourceEnabled => Store?.Enabled ?? false     // 停用即对原版搜索隐身
    · GetDirectlyHeldThings() => Store?.Contents
    · GetStoreSettings/GetParentStoreSettings          // filter 全禁的 StorageSettings
    · StorageTabVisible                                 // 显式接口实现 => false

Building_OmniWorkstation : Building_VirtualStoreHost（仅改 1 行继承）
    └─ CompOmniVirtualStore（承载 100% 逻辑）
         ├─ ThingOwner<Thing> contents        // 真实物品容器（随存档深保存）
         ├─ List<VirtualStoreItem> wanted     // (ThingDef def, int targetCount)，无上限
         ├─ bool enabled                      // 停用开关
         ├─ CompTick / CompTickRare           // 节流补货对账
         ├─ CompGetGizmosExtra()              // 开关 / 丢到地上 / 打开配置
         ├─ CompFloatMenuOptions(Pawn)        // 丢到地上
         ├─ CompInspectStringExtra()          // 状态与清单摘要
         └─ ExposeData()                      // contents + wanted + enabled
```

**为什么接口必须在建筑类上（Comp 无法承载的部分）**：`Thing.SpawnSetup` 用 `this is IHaulSource` 判定注册；`WorkGiver_DoBill` 要求 `holder is Thing t && t.Spawned`。即使另造一个实现 `IHaulSource` 的适配器对象注册进 `HaulDestinationManager`，也会因不是 `Thing` 而被原版跳过（除非用 transpiler 改那一行，违反项目规范）。故用中间基类收纳这 4 个接口的转发，换取"任何建筑 + 该 Comp"即可复用。

**Comp 承载的部分**：全部状态、补货、销毁、丢地、Gizmo、右键菜单、检查面板文本、存档、配置窗口。**不含任何建筑专属内容**，可移植。

---

## 5. 数据结构与存档

```csharp
public class VirtualStoreItem : IExposable
{
    public ThingDef thingDef;
    public int targetCount = 1;   // 无上限
    public void ExposeData() { Scribe_Defs.Look(ref thingDef, "thingDef");
                               Scribe_Values.Look(ref targetCount, "targetCount", 1); }
}
```

- 存档：`Comp.ExposeData` 中 `Scribe_Deep.Look(ref contents, "contents", this)` + `Scribe_Collections.Look(ref wanted, "wanted", LookMode.Deep)` + `Scribe_Values.Look(ref enabled, "enabled", true)`。
- 容器内物品是真实 `Thing`，随存档深保存；`holdingOwner` 由 `ThingOwner` 维护，无需手工处理。
- 读档后（`PostLoadInit`）若 `enabled` 为真则立即安排一次补货对账，修正可能的差额。

---

## 6. 行为细节

### 6.1 自动补货（每 tick 节流）
- `CompTick()` 每 tick 调用（工作站 Def 已 `tickerType=Normal`）。
- 节流参数（`CompProperties` 可配，给默认值）：
  - `ticksBetweenChecks`（默认 1，即每 tick 检查）；
  - `maxStacksPerTick`（默认 200）：单次最多新增的堆数，避免清单数量巨大时一帧生成上万个 `Thing` 造成卡顿。
- 对账逻辑：对 `wanted` 每条 → 统计容器内该 def 总量（遍历 `contents`，堆数 = `ceil(N / stackLimit)`，成本与清单条目数成线性且常数极小）→ 差额 > 0 则 `ThingMaker.MakeThing` 若干堆后 `contents.TryAdd(thing, canMergeWithExistingStacks: true)`。
- 生成物品复用 `Building_OmniCrafter.MakeThing` 的成熟处理（打包建筑走 `MinifiedThing`、品质/艺术设置），避免对建筑类 def 生成裸 `Thing` 出错。
- 空清单或 `!enabled` 时立即 return，零开销。

### 6.2 不可存入（三重保证，全部无 patch）
1. `Accepts(Thing) => false` —— 覆盖 `StoreUtility` 的全部判定路径（`IsGoodStoreCell`、`TryFindBestBetterStoreCellFor`、目的地枚举）。
2. `HaulDestinationEnabled => false` + `GetStoreSettings()` 返回 **filter 全禁**（`SetDisallowAll`）的 `StorageSettings`，`GetParentStoreSettings()` 返回固定全禁设置。
3. 不实现任何落格吸收：掉在工作站格的物品留在原地，不会被吸入。

### 6.3 停用开关（销毁）
- 关闭：`contents.ClearAndDestroyContents(DestroyMode.Vanish)` → `enabled = false`。
- 效果：内容立即消失且不再补货；`HaulSourceEnabled` 变 false，原版搜索（DoBill 等）立刻看不到它。
- 清单配置保留；重新开启：`enabled = true` + 立即安排一次补货。
- 只在"玩家明确关闭"时销毁；建筑 `DeSpawn`/`Destroy`/打包时也清空容器，避免内容物随 `MinifiedThing` 残留或泄漏。

### 6.4 丢到地上
- Gizmo 按钮与右键菜单均调用：`contents.TryDropAll(building.Position, building.Map, ThingPlaceMode.Near)`（`Building_Casket.EjectContents` 同款做法）。
- 不销毁；若周边无空间则保留在容器内，并给出提示消息。
- 本操作不改变 `wanted`，因此下一 tick 会按目标数量重新补货（这是预期语义：丢地 = 取出）。

### 6.5 与既有系统的交互
- 容器内容物未 Spawn、在容器内：不计入地图财富格、不进 `thingGrid`/region、不参与寻路与渲染。
- 会被遍历 haul source 的统计看到（如 `RecipeWorkerCounter`、`OmniCrafterCache` 的存储计数），与"货架里确实存着东西"一致，属预期。
- 与 `OuterrealmStorage` 无冲突：工作站是普通 haul source，不是 vault 终端，也不会进入 vault 的投影/权威层。

---

## 7. UI

### 7.1 Gizmo（全部由 `CompGetGizmosExtra` 提供，建筑零改动）
| 按钮 | 行为 |
|---|---|
| 启用 / 停用（Toggle，显示当前状态） | 关闭 → 销毁内容 + 暂停补货；开启 → 恢复补货 |
| 丢到地上 | 把容器内全部物品丢到附近地面 |
| 打开配置 | 打开 `Window_OmniVirtualStoreConfig` |

### 7.2 右键菜单（`CompFloatMenuOptions(Pawn)`）
- 丢到地上（与 Gizmo 同一动作）。
- 若 `wanted` 为空或 `!enabled`，显示不可用原因。

### 7.3 配置窗口 `Window_OmniVirtualStoreConfig`
模板：`Dialog_OmniCrafter`（`Source/FullyAutomaticOmniCrafter/OmniCrafterUi.cs:163`），分类树复用 `Listing_TreeCategorySelect`（同文件 `:20`）。

| 区域 | 内容 |
|---|---|
| 顶栏 | 标题、总开关、搜索框（复用 `PinyinSearchEngine`）、"全部/已选"切换 |
| 左侧 | 分类树（`ThingDef` 分类） |
| 中间 | 物品列表：图标 + 名称 + 模块名筛选 |
| 右侧 | 当前清单：图标 + 名称 + **目标数量输入框**（≥1，**无上限**）+ 移除；批量清空 |

数量超过 `stackLimit × 20` 时在提示条显示"将占用约 X 个堆（内存/存档体积上升）"。
全部文案走 `Translate()`，写入 `Languages/{ChineseSimplified (简体中文),English}/Keyed/CompOmniVirtualStore.xml`。

---

## 8. 能力边界（重要）

| 路径 | 本期 | 原因 |
|---|---|---|
| 万能工作站代理制作取料 | ✅ | `Patch_OmniWorkIngredientSearch` 遍历 haul sources + `GetAllThingsRecursively` |
| 普通 Pawn 制作取料 | ✅ | `WorkGiver_DoBill.cs:357` 同一机制 |
| 玩家手动取出 | ✅ | 我方"丢到地上"（Gizmo / 右键） |
| 原版"搬到更好的存储" | ⚠️ 受限 | 容器内容物不在 `listerHaulables`，`HaulSourceUtility` 选项通常不出现 |
| 原版**施工自动配送**（送料到蓝图/Frame） | ❌ 二期 | 见下 |
| 交易 / 商队 / 轨道贸易 / 远行队 / 运输舱 | ❌ 明确不接入 | 否则等于无限刷钱/无限带货 |

### 二期：施工自动配送需要做什么

现状阻隔（已核实）：

1. **"材料是否可得"判定只扫地图**：`WorkGiver_ConstructDeliverResources.cs:75` → `ItemAvailability.ThingsAvailableAnywhere`（`RimWorld/ItemAvailability.cs:22`）只遍历 `map.listerThings.ThingsOfDef(need)` → 容器内容不可见 → 原版认为材料不可得，**直接不派单**。该函数全游戏唯一调用方就在这里。
2. **找"要搬的东西"也只扫地图**：`WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor` 用 `GenClosest.ClosestThingReachable(..., ThingRequest.ForDef(...), PathEndMode.ClosestTouch, ...)`，容器内容不在其中。
3. **多趟凑批**：`FindAvailableNearbyResources` 用 `GenRadial.RadialDistinctThingsAround(..., 5f, ...)` 只聚合地图上的同类物品，容器内容不参与，单趟只取一个堆。

对应改动（均为 prefix/postfix，不使用 transpiler）：

1. `ItemAvailability.ThingsAvailableAnywhere` 的 postfix：原判 false 时，并入本图已启用虚拟存储中该 def 的容器总量再判断；沿用原版缓存 key 语义，不写入伪造结果（vault 已做过同口径补丁，见其 readme §9 最后一条）。
2. `WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor` 的来源查找：原版 `GenClosest` 找不到时代之以容器内容物作为代表性 `foundRes`（`Thing.PositionHeld` 对容器内容物返回宿主位置，可达性判定成立）。
3. **待验证点**：该 job 为 `JobDefOf.HaulToContainer` + `HaulMode.ToContainer`，取物经 `Toils_Haul.StartCarryThing` 的 `takeFromOther` 分支。实现前必须用 rimsage 核对 `JobDriver_HaulToContainer` 的 toil 是否传 `takeFromOther: true`；若不传，需补一处 prefix 支持从容器取物。这是本期唯一的实质性不确定点。

---

## 9. 改动清单

**新增**
- `Source/FullyAutomaticOmniCrafter/CompOmniVirtualStore.cs`（`CompProperties_OmniVirtualStore` + `Comp`：容器、清单、开关、补货、销毁、丢地、三种 UI 扩展点、存档）
- `Source/FullyAutomaticOmniCrafter/Building_VirtualStoreHost.cs`（4 个接口的转发基类）
- `Source/FullyAutomaticOmniCrafter/Window_OmniVirtualStoreConfig.cs`（配置窗口）
- `Languages/ChineseSimplified (简体中文)/Keyed/CompOmniVirtualStore.xml`
- `Languages/English/Keyed/CompOmniVirtualStore.xml`
- `FullyAutomaticOmniCrafter.csproj` 中补充上述 `.cs` 引用

**修改**
- `Source/FullyAutomaticOmniCrafter/OmniWorkstation.cs`：`Building_OmniWorkstation` 继承改为 `Building_VirtualStoreHost`（1 行）
- `Defs/ThingDefs_Buildings/OmniWorkstation.xml`：`<comps>` 增加一条 `CompProperties_OmniVirtualStore`

**不做**
- 不修改 `OmniWorkstation` 的 `GetGizmos` / `GetFloatMenuOptions`（Comp 扩展点已覆盖）
- 不改动 vault 的任何投影/权威层代码
- 不新增 transpiler

---

## 10. 性能与多线程

- 补货成本 = 清单条目数 × 容器堆数（堆数 = `ceil(N / stackLimit)`），与地图规模无关；空清单/停用时零开销。
- `Accepts` / `SpaceRemainingFor` 在搬运寻路中高频调用，实现必须常数级（`false` / `0`）。
- 单次补货量节流（`maxStacksPerTick`），避免极端 N 一次性生成海量 `Thing`。
- **不做** `stackCount > stackLimit` 的超量堆（会破坏 `SplitOff`/合并语义）。
- 全部状态挂在 Comp 实例上，无新增静态可变状态，天然规避 1.6 多线程竞争。
- 不使用 LINQ，避免每次调用分配临时集合。

---

## 11. 风险

1. **不限数量上限的堆数成本**：N 极大时（如 100 万钢铁 ≈ 13,334 个 `Thing`）内存与存档体积明显上升；靠分帧节流 + UI 提示缓解，极端值由玩家自担。
2. **原版预留互斥**：同一堆被多个 Pawn 同时需要时，第二个会在 `ReservationManager` 上排队（真实存储亦如此），取走后 `SplitOff` 出余量即可继续，可接受。
3. **施工配送尚未支持**（第 8 节），玩家需要留意"自动送料到蓝图"仍只看地图实物。
4. **无限资源**：零成本 + 自动补货是既定语义，会显著改变难度曲线。
5. **第三方存储 Mod（如 ASF）**：可能对 `IHaulSource` / `listerThings` 有额外假设；必要时按项目惯例做可选的反射缓存兼容（本期不做）。

---

## 12. 验收标准（进游戏验证清单）

- [ ] 放置万能工作站 → 配置窗口可搜索/添加物品并设定目标数量 → 容器内出现物品（地图上不可见、不可点击选中）。
- [ ] 万能工作站代理制作的账单能从此存储取料并完成制作。
- [ ] 普通殖民者（非代理）的账单也能从此存储取料。
- [ ] 任意搬运任务都不会把物品搬入工作站；蓝图/Frame 配送不会送进工作站。
- [ ] 停用开关：容器内容立即销毁、不再补货、原版搜索不再发现它；重新开启后按目标数量补货。
- [ ] "丢到地上"：内容落到工作站附近地面；下一 tick 按目标数量补货。
- [ ] 数量设为远超 `stackLimit`（如 500）：容器自动分成多堆，且补货不卡顿。
- [ ] 保存 → 读取：容器内容、清单、开关状态保持。
- [ ] 拆除 / 打包 / 被摧毁建筑：容器内容被清空，无残留物品或存档污染。
- [ ] 与超维存储仓共存时无异常（互不干扰）。
- [ ] `dotnet build -c Debug` 0 错误。

---

## 13. 实施顺序（一期）

1. `Building_VirtualStoreHost.cs`（接口转发基类）
2. `CompOmniVirtualStore.cs`（容器/清单/开关/补货/销毁/丢地/UI 扩展点/存档）
3. `Building_OmniWorkstation` 改继承 + `OmniWorkstation.xml` 加 comp
4. `Window_OmniVirtualStoreConfig.cs`（配置界面）
5. 语言文件（中/英）+ `.csproj` 引用
6. `dotnet build -c Debug` + 按第 12 节进游戏验证

---

## 14. 实施状态（一期已落地）

状态：**代码完成，`dotnet build -c Debug` 通过（0 错误 / 0 警告）**；第 12 节的进游戏清单尚待执行。

| 文件 | 改动 |
|---|---|
| `Source/FullyAutomaticOmniCrafter/Building_VirtualStoreHost.cs` | 新增：抽象基类，实现 `IHaulSource` / `IHaulDestination` / `IStoreSettingsParent` / `IThingHolder` 并转发给组件；`Accepts` 与 `HaulDestinationEnabled` 恒 false，`StorageTabVisible` 用显式接口实现返回 false |
| `Source/FullyAutomaticOmniCrafter/CompOmniVirtualStore.cs` | 新增：`VirtualStoreItem` / `CompProperties_OmniVirtualStore` / `CompOmniVirtualStore`（ThingOwner 容器、目标数量清单、开关、每 tick 节流补货、销毁、丢到地上、Gizmo / 右键菜单 / 检查文本、存档） |
| `Source/FullyAutomaticOmniCrafter/Window_OmniVirtualStoreConfig.cs` | 新增：配置窗口（分类树 + 搜索 + 目标数量清单，数量不设上限） |
| `Source/FullyAutomaticOmniCrafter/OmniWorkstation.cs` | `Building_OmniWorkstation` 改为继承 `Building_VirtualStoreHost` |
| `Source/FullyAutomaticOmniCrafter/OmniCrafter.cs` | `MakeThing` 由 `private` 改为 `internal static`，供虚拟存储补货复用 |
| `Defs/ThingDefs_Buildings/OmniWorkstation.xml` | `<comps>` 增加 `CompProperties_OmniVirtualStore`（`ticksBetweenChecks=1`、`maxStacksPerTick=200`） |
| `Source/FullyAutomaticOmniCrafter/FullyAutomaticOmniCrafter.csproj` | 增加 3 个新源文件引用 |
| `Languages/{ChineseSimplified (简体中文),English}/Keyed/CompOmniVirtualStore.xml` | 新增：24 个界面文案 key |

构建期修正（对照编译器输出）：

1. `CompOmniVirtualStore` 原显式实现 `IThingHolder.ParentHolder => parent` 失败（`ThingWithComps` 不是 `IThingHolder`，CS0266）→ 改为复用 `ThingComp.ParentHolder`（即 `parent.ParentHolder`）。
2. `TreeNode_ThingCategory.ChildCategoryNodes` 是 `IEnumerable<TreeNode_ThingCategory>` 而非 `List<>`（CS0266）→ 改为 `IEnumerable<>` + `foreach`。

部署提醒：`.csproj` 没有 PostBuildEvent，`dotnet build` 只产出 `bin\Debug\FullyAutomaticOmniCrafter.dll`；进游戏前需用平时的 Rider 构建流程（会写入 `Assemblies`）或手动把该 dll 复制到 `Mods/FullyAutomaticOmniCrafter/Assemblies/`。
