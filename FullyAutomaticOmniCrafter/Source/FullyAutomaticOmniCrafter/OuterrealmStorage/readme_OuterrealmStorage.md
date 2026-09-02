# OuterrealmStorage 架构与维护契约

本文是超维存储仓（OuterrealmVault）子系统的维护说明，面向人工开发者和 LLM。它描述当前实现的事实、不可破坏的约束、主要流程与兼容边界。

> 核心原则：**全局权威实例拥有库存；仓库视图中的 Thing 只是查询投影。预留投影不等于取出物品，只有在实际携带、装备、交付等最终使用边界才转移权威实例。**

## 1. 不可破坏的系统不变量

1. `GameComponent_OuterrealmStorage` 是全局库存的唯一所有者。所有地图、所有超维存储仓和所有随身访问者共享这一份库存。
2. `OuterrealmEntry.Proto` 与 `AdditionalProtos` 是权威真实物品。它们未 Spawn、无 `holdingOwner`，完整保存 Thing 子类字段、Comp 状态和物品身份。
3. `OuterrealmVaultViewThingOwner` 中的 Thing 是**无库存所有权的投影**。其 `stackCount` 只用于显示、筛选和数量感知，不能存回全局、交付、制作、穿戴或出售。
4. 普通 `Reserve` 只预留投影和数量，**不得**生成实物、扣减库存或把 Job 中全部同类目标替换成同一对象。
5. 实际取出必须调用 `Withdraw` / `WithdrawCanonical`，或通过已接管的 `Thing.SplitOff`、`Pawn_CarryTracker.TryStartCarry` 路径完成。禁止用 `ThingMaker.MakeThing` 复制库存。
6. schema 2 起，权威实例数量是存档真相，`Count` 是必须同步维护的 long 数量缓存。任何数量变化必须同时维护权威堆、`Count`、资源统计、版本与视图通知。
7. 投影、借出实物和权威物是三种不同状态，不得根据 `Spawned` 单独判断身份；应使用 `holdingOwner is OuterrealmVaultViewThingOwner`、`IsProjection`、`IsBorrowed` 等明确标识。
8. 任何失败或 Job 中断路径都必须满足库存守恒：实物已交付则不回收；尚未交付则退回全局；投影永远不能作为回滚物存入。

违反以上任一条都可能造成复制、吞物、跨地图超卖、Job 死循环、财富爆炸或存档永久污染。

## 2. 分层模型

```text
GameComponent_OuterrealmStorage（全局权威层）
  └─ OuterrealmEntry
      ├─ Proto                 第一权威 Thing 堆
      ├─ AdditionalProtos      额外权威 Thing 堆
      └─ Count                 long 数量缓存

Building_OuterrealmVault（地图终端 / 原版存储接口适配）
  └─ OuterrealmVaultViewThingOwner（每建筑视图层）
      └─ Projection Thing      查询投影，不拥有库存

Pawn + Hediff_SubspaceAccess（随身访问）
  └─ 直接把权威主堆作为只读选料候选，不建立完整 Pawn 投影视图
```

建筑不是独立仓库。建造、拆除、冻结或更改某个建筑的筛选，只改变该终端的可见性和访问能力，不改变全局库存。

## 3. 对象身份与生命周期

| 对象 | 如何识别 | Spawn/持有状态 | 能否计入库存 | 能否交付给游戏逻辑 |
|---|---|---|---|---|
| 权威物 | `GameComponent.TryGetCanonicalEntry` 可找到 | 未 Spawn、无 holder | 是 | 先通过 `Withdraw` 转移 |
| 查询投影 | `holdingOwner is OuterrealmVaultViewThingOwner`；离开视图后仍可由 `OuterrealmVaultUtil.IsProjection` 识别 | pseudo-Spawned；在视图中；不进 `thingGrid` | 否 | 否 |
| 显式借出实物 | `view.IsBorrowed` / `OuterrealmVaultUtil.IsOuterrealmBorrowed` | 真 Spawn 在 vault 格，无 holder | 已从全局扣除 | 是；未交付须回收 |
| 普通取出实物 | 已进入 carry、容器、装备栏或地图 | 普通原版状态 | 已从全局扣除 | 是 |

### 查询投影

- 由 `GameComponent_OuterrealmStorage.MaterializeProjection` 创建，并通过弱表 `MarkProjection` 标记。
- 类型、def、stuff、品质、耐久、样式、颜色和必要 Comp 信息来自权威物，用于兼容原版及第三方筛选器。
- 正常数量为 `min(entry.Count, def.stackLimit)`；在选料、远行队等数量感知窗口可临时 Boost，随后必须 Unboost。
- 投影注册到必要的 lister、haul source 和 region 查询结构，但故意不进入 `thingGrid`、渲染、光照和普通点击选择。
- pseudo-Spawned 是查询兼容手段，不代表它是地图实物。判断投影必须看 `holdingOwner` 或投影标记，不能写 `thing.Spawned` 作为排除条件。
- `Deposit` 会拒绝任何被标记的投影，即使第三方 Mod 已先将其从视图移除。

### 权威物

- 存入时保留原 Thing，不把状态压缩成模板。
- 可堆叠条目使用原版 `CanStackWith`、`TryAbsorbStack`、`SplitOff`，让 Comp 的堆叠回调正常执行。
- `def.stackLimit <= 1` 的物品不聚合，每个实例独立成条目。尸体按 InnerPawn 身份保持唯一；打包建筑保留 InnerThing。
- `OuterrealmEntryKey` 用于展示签名、存档放行项和兼容识别；实际合并判定以 `CanStackWith` 为准，不能改回仅按 Key 合并。

## 4. 核心数据结构

### `GameComponent_OuterrealmStorage`

职责：

- 保存全局 `entries`、按 def 粗索引、权威 Thing 反向索引和资源总量缓存。
- 提供 `Deposit`、`Withdraw`、`Subtract`、查询、弹出队列和建筑注册。
- 维护内容版本 `Version`、预留版本 `ReservationVersion` 与可续投影同步队列。
- 在 `GameComponentTick` 末集中同步建筑视图，避免每次数量变化立即遍历全部仓库。
- 负责存档格式迁移与运行时缓存重建。

数量变化的标准后处理为：更新权威堆 → 更新 `Count` → `AdjustResourceTotal` → 增加 `version` → `EnqueueProjectionSync`。条目取空时还必须 `RemoveEntry` 并立即 `NotifyEntriesEmptied`，防止孤儿投影继续被 Job 选中。

### `OuterrealmVaultViewThingOwner`

职责：

- 根据建筑 filter 和全局条目创建、更新、退休查询投影。
- 维护 `entry -> copy`、`copy -> entry` 双向索引。
- 将真实外来物品的 `TryAdd` 转发到全局 `Deposit`。
- 通过 `WithdrawCanonical` 把投影请求转换为权威实例转移。
- 全局缓存所有地图、所有终端按条目汇总的 reservation 数量，计算 `AvailableForReserve = Count - reservedTotal`。
- 管理少量显式借出实物及其回收。
- 管理 pseudo-Spawned 的 lister/region 注册、数量 Boost、孤儿投影清理和视图全量重建。

视图本身不序列化。读档或建筑 Spawn 后由全局库存重建。

### `Building_OuterrealmVault`

它是全局库存的地图终端，同时继承 `Building_Storage` 并实现原版存储、搬运、查询、服装源和存储组接口，以提高第三方兼容性。

- `view` 是该建筑的投影缓存。
- `settings` / `storageGroup` / `slotGroup` 复用 `Building_Storage`。
- filter 决定该终端可见和可访问的条目，不删除全局内容。
- 建筑移除时回收借出实物并清空视图；全局库存仍存在。
- 普通搬入走容器路径 `HaulToContainer -> view.TryAdd -> Deposit`。
- 异常落在建筑格上的外来实物经 15 tick 倒计时吸收；250 tick rare-tick 扫描只作兜底。

## 5. 标准数据流

### 5.1 存入

```text
真实 Thing
  → 自动存入先检查工作 claim；玩家手动存入则提示并确认后取消仍引用该物的安装蓝图
  → GameComponent.Deposit
  → DeSpawn / 与兼容权威堆合并 / 新建条目
  → 更新 Count、统计、版本和视图
```

入口包括：搬运到 vault 容器、授权 Pawn 右键存入、授权制作产物自动存入、格上外来物吸收和失败回滚。

存入前必须保证对象是真实 Thing。不要把视图 `Remove` 得到的投影直接传给 `Deposit`。

### 5.2 检索与预留

```text
原版/第三方搜索 lister 或 IHaulSource
  → 找到投影
  → CanReserve 按全局 Count - 全部预留量检查
  → Reserve 只写原版 reservation
  → 全局库存和地图实物均不变化
```

这是制作循环修复的关键。若在 Reserve 时借出实物，搬运者可能在执行者真正拿取前移走它；Job 中断后再次预留会重复生成实物。

### 5.3 普通实际取用

```text
Pawn 到达 vault
  → Toils_Haul.StartCarryThing
  → Pawn_CarryTracker.TryStartCarry 补丁识别 holdingOwner=view
  → view.BoostCopy（仅数量计算窗口）
  → projection.SplitOff(count)
  → SplitOff 补丁调用 view.WithdrawCanonical
  → 转移权威原实例/原版拆堆，并扣减 Count
  → 实物进入 Pawn carry
  → 只替换 Job 当前 A/B/C 引用
```

`ReplaceJobThingReference` 只替换当前目标；若尚处预预约阶段，最多替换队列中的第一个匹配项。严禁把队列里所有相同投影替换成同一真实堆，否则 reservation、`countQueue`、`placedThings` 与实际物品会失配。

原版 `JobDriver_DoBill` 在实物放到工作台后建立 physical reservation，并把真实对象记入 `placedThings`。因此制作过程中原料保持原版语义，不再由 vault 租约系统管理。

### 5.4 显式提前租约

只有无法在 `TryStartCarry` 等最终边界兑现，或第三方必须从 `thingGrid` 看见实物时才允许使用 `TryLendCopy`。

标准租约顺序：预留投影 → 释放投影 reservation → `TryLendCopy` 扣库存并真 Spawn 实物 → 精确改写目标 → 预留实物 → Job 结束时 `ReturnCopy`。

当前合法例外：

- 穿戴：原版 Wear 没有通用携带边界，启动前租出真实 Apparel。
- 牵引光束：其扫描只看 `thingGrid`，先借出一个真实种子，使原方法建立 batch；其余 transfer 延迟到 Lift 时从权威库存取出。
- 随身自动取料：候选是未 Spawn 的权威主堆而非建筑投影，Reserve 成功后在 Pawn 处 Checkout，并由 `PendingCheckouts` 回收未取得的余量。

新增例外前必须证明无法在实际消费边界完成 Checkout，并提供确定性的失败回收路径。

### 5.5 弹出

管理器与 ITab 调用 `EnqueueEject`。全局 GameComponent 每 tick 最多处理 4 堆，每堆不超过原版 stackLimit，并避开 vault 建筑占格。放置失败的剩余实物重新 `Deposit`。弹出队列不存档，因为地图索引在重载后不可靠。

## 6. Reservation、Job 与回滚规则

- `CanReserve` / `CanReserveStack` 对投影使用全局可用量，不受投影当前显示堆上限误导。
- vault 建筑本体按无限容量目的地处理，不建立会阻塞多搬运工的互斥 reservation。
- 任意 Reserve/Release 变化都会增加 `ReservationVersion`，视图下次查询时惰性重建预留缓存。
- 投影被预留期间可以继续存在；条目取空或 filter 禁止后，在 reservation 释放时退休。
- `Thing.SplitOff` 会动态覆盖所有已加载 Thing 子类的 override，统一将投影拆分重定向到权威库存。不要只补丁 Thing 基类。
- carry 失败时，已 Withdraw 且仍无 holder、未 Spawn 的真实物必须重新 `Deposit`。
- 借出实物仅在仍 Spawn 于对应 vault 存储格时回收；已进入 carry、装备栏或其他 holder 表示已经交付，只清理租约标记，不抢回。
- 不要依赖“稍后 Tick 会修好”作为主要回滚。Tick/rare-tick 只能是异常兜底。

## 7. 建筑权限语义

| 状态 | 行为 |
|---|---|
| 允许存入（`!NoDeposit`） | 可作为搬运目的地，可吸收允许的外来实物 |
| 禁止存入（`NoDeposit`） | 不作为搬入目的地；落格实物不吸收 |
| 允许取出（`!NoWithdraw`） | 作为普通 `IHaulSource` 暴露投影 |
| 禁止取出（`NoWithdraw`） | 禁止普通搬出 |
| 允许拿取使用（`AllowTakeForUse`） | 仅在禁止普通取出时，放宽制作、进食等使用路径 |
| 冻结（`Frozen`） | `CanShow` 恒 false，清空视图、停止存取，但保留 filter 与全局内容 |

`CanShow` 负责视图可见性；`CanAbsorb` 还要考虑禁止存入。不要把两者混为一个会移动库存的操作。

## 8. 随身访问

`Hediff_SubspaceAccess` 是授权状态，随 Pawn 存档并跨地图/远行队存在：

- `autoTake`：制作选料时由 `SubspaceAccessUtility.InjectGlobalEntries` 把权威主堆作为只读候选注入。候选扫描不得 MakeThing 或扣库存。
- `autoStore`：制作完成后直接 Deposit 产物。
- `autoStoreFiltered`：只有当前地图至少一个可接收该产物的 vault 时才自动存入。
- 手动右键存入和管理器取出不受自动开关限制。

随身候选没有建筑投影可供寻路，因此是“Reserve 后提前 Checkout”的明确例外。`PendingCheckouts` 只跟踪尚未被 Pawn 取得的实物；reservation 释放或 Job 中断后退回全局。

`OuterrealmMarkUtility.IsMarked` 同时兼容新的访问 Hediff 与旧 `FAOC_QuantumLinkImplant`。

## 9. 视图同步与性能

- 内容变化写入 GameComponent 的去重可续队列；在后续 Tick 内按预算同步到已注册 vault。
- 内容变化进入去重、可续、带代次的“条目 × 仓”工作队列；不设 4096 固定窗口，也不再因溢出退化为全量扫描。每 tick 保底处理 256 对，并在 1ms/2048 对上限内自适应突发。
- filter 禁止和冻结立即移除投影；新允许 Def 的前 256 个条目进入高优先队列，品质/耐久等特殊过滤条件由后台完整扫描兜底。
- 加载、重连和 filter 全量兜底使用轮转自适应预算：每 tick 保底 512、最多 4096 条目并以约 1ms CPU 时间截止。暂停时 Tick 不推进，投影恢复也不推进。
- 精确 Def 搜索在原版枚举 lister/region 前，利用 `byDef` 为最近可服务终端按需补齐最多 32 个条目。
- 软租约使用 64 槽时间轮；reservation 释放事件会立即协调对应唯一锚点，时间轮仅承担到期与异常兜底。
- 投影规模按可见条目数而非库存 Count 增长：百万个可堆叠资源仍只有一个权威条目及每仓一个投影。
- region 重建补丁只置 dirty；建筑每 60 tick 最多批量补注册一次。
- `TotalCountOf`、InspectString、预留总量和 UI 可见列表均有版本缓存。
- 高频路径避免 LINQ、重复反射和临时集合；必要反射必须静态缓存。
- 静态可变状态须考虑 RimWorld 1.6 多线程。投影创建作用域使用 `[ThreadStatic]` 深度，不得改成普通静态 bool。

### 9.1 Tick 与渲染帧的强制边界

- RimWorld 游戏逻辑以 Tick 推进；正常速度目标为每秒 60 Tick。Tick 与 FPS 无关，暂停时不会产生新的游戏 Tick。
- 投影后台恢复、增量同步、软租约、pending 回收和任何以“若干 Tick 后”为语义的工作，只能由 `GameComponentTick`、Thing Tick 或明确的游戏事件推进。禁止使用 `GameComponentUpdate`、`Time.frameCount`、`Time.deltaTime` 或 `Time.realtimeSinceStartup` 推进这些状态。
- 暂停期间不得自主消耗恢复队列。玩家在暂停时主动修改 filter 等命令仍可同步提交其直接结果（例如立即撤销禁止项），但不得因为 UI 重绘或菜单刷新继续执行后台批次。
- `Stopwatch` 只允许作为单个 Tick 内的 CPU 占用保护，例如“完成保底数量后若已用 1ms 则让出”；它不能计算租约到期、队列年龄或 700 Tick 截止时间。
- 截止时间统一使用 `Find.TickManager.TicksGame`：`remainingTicks = queuedAtTick + deadlineTicks - TicksGame`。默认速度下 700 Tick 约为 11.67 秒；暂停时剩余 Tick 不减少。
- `Time.frameCount` 与真实时间只允许用于纯 UI 行为，例如右键菜单多久重绘一次。UI 重绘可能触发搜索，因此普通需求物化限制为“同一地图、同一 Def、每 Tick 最多一批”，唯一锚点搜索限制为“同一条目每 Tick 最多一次”；两者在暂停时均不执行，防止高 FPS 改变投影或路由状态。

审查新增代码时，凡注释出现“每帧”“帧末”“真实秒后”都必须先判断它是纯 UI 还是游戏状态；游戏状态应改写成 Tick 或同步事件语义。

## 10. 存档格式与迁移

当前 `storageSchemaVersion = 2`。

- schema 0/1：`Count` 是旧格式唯一可信数量，Proto 曾是模板且 `stackCount` 可能被 Boost 放大。迁移时以 Count 为准，裁掉账外模板数量或补足权威堆，绝不能用较大的 Proto 数量抬高 Count。
- schema 2：`Proto + AdditionalProtos` 是唯一真相。读档时从实际权威堆重建 Count；不再比较两者后取较大值。
- 建筑视图、投影、借出集合、reservation 缓存、变更日志、弹出队列和随身 PendingCheckout 均为运行时状态，不序列化。
- 所有引用 `Thing`、`Map`、`Pawn`、仓库或条目的运行时路由状态归属当前 `GameComponent`；组件构造函数不得清理上一局静态状态。
- `Game.ExposeData` Saving 期间建立全局隔离屏障：按运行时注册索引暂停投影和唯一锚点，组件保存权威对象后由 Finalizer 恢复。`Map.ExposeData` 仅作为第三方直接保存地图时的按图兜底。
- 唯一物品权威锚点虽然临时注册在地图 `listerThings`，保存地图前也必须与普通投影一起摘除；它只能作为 `OuterrealmEntry.Proto` 随全局库存深保存一次。旧版重复保存产生的地图副本在 `LoadedObjectDirectory.RegisterLoaded` 与 `Map.FinalizeLoading` 两个边界按权威 ThingID 精确清理；若副本曾被重新吸收并保存，则在全局库存 `PostLoadInit` 中保留首个相同 ID 的权威条目。读档后重新保存即可永久净化旧存档。
- 建筑只保存 filter/存储组及 `noDeposit`、`noWithdraw`、`allowTakeForUse`、`frozen`、右键菜单模式。

修改存档字段语义时必须提升 schema，并写单向、幂等迁移。禁止通过“哪个数字更大”猜测权威来源。

## 11. 兼容补丁分组

`Patch_OuterrealmStorage.cs` 的补丁可按职责理解：

- 库存守恒：`Thing.SplitOff`、`TryAbsorbStack`、`Pawn_CarryTracker.TryStartCarry`、ReservationManager。
- 原版存储适配：haul destination/source、施工配送、蓝图/Frame、资源计数。
- 查询可见性：GenClosest、食物搜索、治疗、右键菜单、可达性、region/lister 注册。
- 操作路径：穿戴、装备、食用、治疗、建造取料、远行队收集。
- UI/副作用隔离：存档时临时摘除投影、禁止绘制 overlay/glow、过滤左键选择。
- 交易与运输：轨道贸易、商队交易去重、远行队/运输舱候选和数量修正。
- 统计：RecipeWorkerCounter、ResourceCounter、可选财富排除。
- 第三方：Common Sense、Manipulator Beam。

关键兼容约定：

- Common Sense：仅在 `MaterializeProjection` 的线程局部作用域内跳过其 ThingMaker Postfix，避免生食等没有产出配方的物品触发空集合 RandomElement；正常 ThingMaker 行为不变。
- Manipulator Beam：反射目标全部缓存，未安装时 `Prepare` 跳过；batch 扫描只借一个真实种子，Lift 时统一从权威库存转移；禁止存入时不能把 vault 当光束目的地。
- 打包建筑：玩家确认手动存入后才取消仍引用它的安装蓝图；延迟 Checkout 后把蓝图引用从投影重定向到真实实例。
- 自动存入保护：安装/再种植蓝图是优先于普通存储的工作 claim。自动搬运候选、运行中任务和最终
  TryAdd/落格吸收均不得取消蓝图；蓝图建立晚于搬运任务时，应终止旧搬运并由原版 Job 清理安全
  落物。蓝图晚于未提示的手动存入命令建立时也终止旧命令；只有玩家在蓝图已经存在时明确选择
  手动存入并确认提示后，才取消蓝图并继续 Deposit。
- 交易/远行队：投影可能同时经 lister 与 haul source 被枚举，必须按实例去重；最终收集仍走 TryStartCarry Checkout。
- 轨道贸易：默认只暴露通电信标覆盖终端中的普通投影与唯一物品当前锚点。管理器的“无需信标向轨道贸易暴露全部库存”是随存档保存的全局规则；开启后直接遍历全局条目，普通物品使用无地图临时交易投影，唯一物品使用权威实例。两者都必须在成交 `SplitOff` 边界按临时来源映射调用 `Withdraw`，禁止把普通权威 `Proto` 直接交给交易系统。
- 财富：是否排除全局库存由 `OmniCrafterSettings.vaultExcludeFromWealth` 控制，不能通过伪造 Count 解决财富问题。

## 12. 文件职责索引

| 文件 | 主要职责 |
|---|---|
| `GameComponent_OuterrealmStorage.cs` | 全局库存、权威堆、数量、索引、变更日志、弹出、存档迁移 |
| `OuterrealmStorageRuntimeState.cs` | 每局运行时所有权、按地图注册索引、保存隔离快照、软租约时间轮 |
| `OuterrealmEntry.cs` | 条目与展示签名数据结构 |
| `OuterrealmVaultViewThingOwner.cs` | 投影视图、预留数量缓存、Checkout、显式借出、lister/region 生命周期 |
| `Building_OuterrealmVault.cs` | 地图终端、原版存储接口、权限、filter、吸收、Gizmo、生命周期 |
| `OuterrealmVaultUtil.cs` | 投影/借出弱标记、安全 UI、温度、打包建筑蓝图兼容 |
| `Patch_OuterrealmStorage.cs` | 原版流程接入及主要兼容补丁 |
| `Patch_ManipulatorBeamCompat.cs` | Manipulator Beam 可选兼容 |
| `SubspaceAccessUtility.cs` | 随身授权选料注入、PendingCheckout 与回收 |
| `Hediff_SubspaceAccess.cs` | Pawn 授权状态和自动取用/存入设置 |
| `OuterrealmMarkUtility.cs` | 新旧授权标记统一判断 |
| `OuterrealmTradeSourceRegistry.cs` | 无信标轨道贸易的临时来源到权威条目弱映射 |
| `JobDriver_VaultDepositFromGround.cs` | 授权 Pawn 手动存入 |
| `JobDriver_VaultDeliverResources.cs` | 从 vault 向蓝图/Frame 配送材料 |
| `Dialog_OuterrealmStorageManager.cs` | 全局库存管理与批量弹出 |
| `ITab_OuterrealmVaultContents.cs` | 单建筑可见内容与弹出 |
| `CustomFloatMenuUtil.cs` / `Patch_OuterrealmFloatMenu.cs` | 旧版完整操作大列表、搜索、分类、刷新策略及两级菜单早期拦截 |
| `Dialog_OuterrealmVaultItemFloatMenu.cs` | 两级右键菜单：先显示轻量物品列表，再只为所选目标生成原版/第三方操作 |
| `Dialog_SubspaceAccessManager.cs` | Pawn 授权管理 UI |

## 13. 开发时的决策顺序

新增一种“从 vault 使用物品”的功能时，按以下顺序判断：

1. 搜索阶段能否只使用投影？通常可以。
2. 能否在 `TryStartCarry`、`SplitOff` 或最终消费回调处 Checkout？能则使用标准延迟路径。
3. 若必须提前生成真实 Spawned Thing，原因是否来自无法改写的原版/第三方接口？若不是，不得建立租约。
4. 租约失败、中断、取消、目标销毁和部分取走分别如何回收？必须在实现前明确。
5. Job 里是否有多个相同投影目标？只能替换当前目标，不能全队列替换。
6. 第三方是否可能先 Remove/DeSpawn 投影再存入？所有存入仍须经过 `Deposit` 的投影防线。
7. 是否同步维护 Count、权威堆、资源缓存、版本、变更日志和空条目视图清理？
8. 是否在无对应第三方 Mod 时安全跳过？反射是否已缓存？
9. 是否引入每 tick 全量扫描、LINQ 分配、重复反射或非线程安全静态状态？
10. 新增显示文本是否已加入 `../../Languages`，新增 Def 是否放入 `../../Defs`？

## 14. 禁止模式

- 禁止在 Reserve 成功后普遍调用 `TryLendCopy`。
- 禁止用投影 `stackCount` 增加全局 Count。
- 禁止把 `Materialize` / `ThingMaker.MakeThing` 当作取出库存。
- 禁止把投影直接加入 carry、装备栏、工作台、交易清单或其他真实容器。
- 禁止把 Job 队列中所有相同投影替换成同一个实物。
- 禁止只改 Count 而不修改权威堆，或只改权威堆而不更新 Count 和缓存。
- 禁止仅用 `Spawned` 判断对象是否为投影。
- 禁止把视图或运行时借出集合写入存档。
- 禁止用高频轮询代替已有的版本、事件和 dirty 标记机制。
- 非必要不要新增 transpiler；必须先用 rimsage 核对 RimWorld 1.6 原始实现和方法签名。

## 15. 最低回归测试清单

每次改动库存、预留、视图或 Job 路径后至少验证：

- 生食在 vault 中，机械体连续烹饪；其他搬运者不能搬走尚未 Checkout 的“原料”，中断后库存守恒。
- 一个账单需要多个同类/不同类原料，含重复队列项、部分堆与需求大于 stackLimit。
- 多 Pawn/多地图同时预留同一唯一物品和同一种堆叠物。
- Job 在预留后、拿取前、携带后、放置后分别中断。
- 穿戴、强制穿戴、装备、自动进食、治疗取药。
- 蓝图/Frame 配送与打包建筑安装。
- 普通搬入、禁止存入、禁止取出、允许拿取使用、冻结与 filter 变更。
- 随身自动取用、自动存入、关闭开关及 Job 中断回收。
- 管理器弹出、放置失败回滚、拆除最后一个 vault 后库存仍存在。
- 商队、运输舱、轨道交易、资源计数与财富开关。
- 安装/不安装 Common Sense 和 Manipulator Beam 两种环境。
- 从 schema 0/1 旧档加载被 Boost 放大的 Proto：Count 不增加、无原黄字；再次保存读取后 schema 2 数量稳定。
- 游戏内直接连续读取地图数量不同的存档，并覆盖安装 Faction Editor 的环境：组件构造和反序列化不得出现旧地图索引异常。
- `保存 → 读取 → 保存 → 读取` 后比对条目 Count 与唯一 ThingID；地图 `<things>` 中不得出现投影或全局唯一锚点副本。
- 删除/放弃含 vault 的地图后读取其他存档；注销只能使用 runtime 记录的地图，不得反查已失效的 `Thing.Map`。
- 万级条目加载与 filter 放开时确认投影分 tick 建立；百万可堆叠数量不应生成百万个投影。

完成修改后执行：

```powershell
dotnet build -c Debug
```

必须达到 0 个编译错误；涉及运行时补丁或存档迁移的改动不能只依赖编译结果，应按上述场景进游戏验证。
