# 设计审查报告：《全局共享冻结存储系统》

> 审查对象：`GlobalStorageDesign.md`（全局共享冻结存储系统设计方案）
> 审查方式：6 个并行子代理按主题深挖 + 主线独立复核关键断言
> 原版证据来源：RimWorld **1.6.9438**（Assembly-CSharp 1.6.9438.37837）反编译源码（rimsage 同源数据）
> 审查期间未修改任何代码或方案文件。

---

## 总评

方案骨架（容器型存储形态、聚合 L1 模型、变更驱动同步）方向正确，且 §6.2「空 filter=全禁止」等关键断言与原版机制吻合。但存在 **3 个方向性缺陷**和一批实现级错误，其中 M10「暂不解决」的决断基础本身是错误的（该循环可由代码确定性推演，不是"待实测"）。

---

## 一、高严重度问题（方向性缺陷，须先拍板）

### H1. A↔B 无限搬运循环是确定性可推演的，不是"待实测"（§6.3 / §11 M10）

- **文档断言**："无限容量不会制造循环：物品最终停在'最高且有空间'的存储"；"`IsInValidBestStorage` 经 `TryFindBestBetterStorageFor` → `IsGoodStoreCell` 检查空间，'高优先级但已满'的存储不参与"；§11 M10"待实现后实测复现"。
- **原版代码事实**：`StoreUtility.TryFindBestBetterNonSlotGroupStorageFor`（StoreUtility.cs:233-304）对容器型目标**没有任何空间/容量检查**——只有 `HaulDestinationEnabled` + `priority > currentPriority` + `Accepts(t)` + faction + reservation + reachability。`IsGoodStoreCell` 只在格子型路径 `TryFindBestBetterStoreCellForWorker`（StoreUtility.cs:222）使用。
- **推演（确定性循环）**：低优先级 Vault 的视图副本被判"不在最佳存储"→ 搬运工搬去高优先级 Vault → 高 Vault 的 `TryAdd` **吸收回全局层（全局数量不减）** → 低 Vault 视图按 §3.2 自己规定的"取空后补回"重新物化 → 再次 haulable → **无限循环**。每趟 ≈ 携带量，全程全局数量不减少。文档引用的"终止循环"论据用错了函数；"单调搬运模型"假设被设计自身的"视图回补"机制打破（单调性分析只看了全局层，没看操作对象是回补的视图副本）。
- **姊妹路径**：§6.3 放行条目会被**另一座** Vault 吸回——放行期间 `CurrentStoragePriorityOf` 返回 Unstored，任何优先级高于 Unstored 且接受的 Vault 都"更好"，条目搬入后被吸收回全局层、数量不减 → 放行"搬空"条件永不满足 → 放行永不完成。防回吸分析只防了本建筑。
- **建议**：把 M10 从"待实测"升级为已推演确定的设计缺陷。二选一：(a) §5.2 #6 短路条件 = "ParentHolder 是本系统建筑且 Accepts==true"→ 锁定条目直接 false（M10 与"低→高自动流出"同时消除，最干净，需升为默认启用）；(b) 接受"低→高流出"语义但条目搬到高 Vault 后低 Vault 该条目隐式禁止。文档须明确 (a) 与 §1.6「更低优先级条目会被自动搬走」的语义**互斥**——现文档同时主张两者，未裁决。放行路线目标选择必须排除"会吸收回全局层的本系统容器"。

### H2. "无限容量"在取出侧完全不可见——视图副本 stackCount≤stackLimit 的核心矛盾（§3.2 vs §1.3）

- **文档断言**：副本 stackCount 上限 = `def.stackLimit`（钢铁 75）；"工作台原料原版自动可用"；"工作台原料搜索读本建筑视图永远即时正确"（§9）。
- **原版代码事实**：
  - 工作台搜索按候选 `stackCount` 累计可用量：`TryFindBestIngredientsInSet_NoMixHelper`（WorkGiver_DoBill.cs:503-510）要求单份需求量 ≤ `availableCounts.GetCount(def)`，而 `availableCounts` 只累加各候选 stackCount（:635-640）。单个副本最多贡献 stackLimit（钢铁=75）。
  - 取料 `StartCarryThing`（Toils_Haul.cs:68-71）`failIfStackCountLessThanJobCount=true`：副本 75 < job.count 100 → 立即 `EndJobWith(Incompletable)`。
  - `RecipeWorkerCounter.CountProducts`（RecipeWorkerCounter.cs:44-45、:89）对 HaulSource 内容用副本 stackCount 累加 → "已有数量"/`targetCount`/`pauseWhenSatisfied`（Bill_Production.cs:254）全部被 stackLimit 封顶失真（100 万显示 75；已存 ≥75 件时错误满足 targetCount≤75 的账单）。
- **后果**：① 单份需求 > stackLimit 的账单**永不生成 job**；② 连续制作每单之间空转 8-10 秒（副本取空后 60 tick 才补回，而账单搜索重试是 500-600 tick 的 `ReCheckFailedBillTicksRange`）；③ 账单"已有数量"与暂停逻辑失真。
- **结论**：§3.2 的 stackLimit 上限只保住了 `TryAbsorbStack` 合并假设，却牺牲了"无限容量对外可见"，两者矛盾文档未察觉——这是整个取出路径设计的核心矛盾。
- **建议**：方案 A：patch `WorkGiver_DoBill.TryFindBestIngredientsHelper`/`TryFindBestBillIngredientsInSet`、`Toils_Haul.StartCarryThing` 与 `RecipeWorkerCounter.CountProducts`，把副本 stackCount 视图替换为全局剩余量（即把 §5.2#7 的"开关放宽"扩展为"数量替换"）；方案 B：SplitOff postfix 即时把副本补回 `min(全局剩余, stackLimit)`（把补回从 60 tick 拉到变更点即时），并接受搜索阶段可用量仍受 stackLimit 限制的残余问题（大订单需结合方案 A 的搜索 patch）。

### H3. SplitOff 同步 patch 按文档实现会直接出错（§3.3 / §5.2#5）

- **双触发**：`ThingWithComps.SplitOff`（:419-428）与 `MinifiedThing.SplitOff`（:129-142）都是 `base.SplitOff(count)` 薄包装——**只 patch `Thing.SplitOff`（virtual 声明处，Thing.cs:1205）一处即可覆盖全部 override**；三处全 patch 则对 ThingWithComps/MinifiedThing 每次调用 prefix/postfix 执行两遍 → 双校正、双扣减。
- **双扣**：整堆分支走 `holdingOwner?.Remove(this)`（Thing.cs:1214）→ 已触发 `Notify_ItemRemoved` 同步扣全局；postfix 再按"旧−新"差额扣 = 全局双扣。文档只说"需防重复同步"未给机制；实现必须判 `__result == __instance`（整堆）跳过差额扣减。
- **失败回滚漂移**：SplitOff 调用方失败时用 `TryAbsorbStack(piece, false)` 把 piece 合并回原堆（`ThingOwner<T>.TryAdd` :115-121、ThingOwner.cs:335/426）——合并使副本 stackCount 涨回，但 postfix 已扣全局 → **全局凭空少件**。真实触发路径：`TakeToInventory` 背包 TryAdd 失败、`TryTransferToContainer` 失败。`TryAbsorbStack` 不在 patch 清单。需新增 `Thing.TryAbsorbStack`/`ThingWithComps.TryAbsorbStack` patch 或 patch 失败分支，回滚合并时补回全局。
- **prefix 校正时机错位**：`StartCarryThing`（Toils_Haul.cs:74）与 `TryStartCarry`（Pawn_CarryTracker.cs:84）都在 SplitOff **之前**用旧 stackCount 计算 count。prefix 校正把 stackCount 调小后，count > stackCount → 走整堆分支（Thing.cs:1212 `Log.Error`）→ 副本整体 Remove；随后 `StartCarryThing`（:87-100）`num6 < 旧stackCount` 时把**已整体移出视图的副本**重新插回 job queue（putRemainderInQueue）→ 后续 toil 拿着悬挂引用继续跑。文档 §3.3"极端竞态下打 Log.Error，功能仍正确"的判断**不成立**。
- 另注意：`ThingWithComps.SplitOff` 整堆分支也对 `piece==this` 调用 `PostSplitOff(this)`（:422-426），patch postfix 改 stackCount/全局时注意 comp 副作用时机。

### H4. §1.6 锁定机制的命门前提未写明：view 构造必须传 owner=建筑（§1.6/§3.2）

- **原版代码事实**：`Thing.ParentHolder => holdingOwner?.Owner`（Thing.cs:274）；未 Spawned 物品的 `CurrentHaulDestinationOf = t.ParentHolder as IHaulDestination`（StoreUtility.cs:18-21）。视图条目的 holdingOwner 是 VaultViewThingOwner，`ParentHolder as IHaulDestination` 拿到的是 **view.Owner**。
- **文档缺口**：§3.2/§4 只写 `public VaultViewThingOwner view;`，未写构造参数。原版模式是 `new VaultViewThingOwner(this)`（Bookcase/OutfitStand 的 innerContainer 均如此）——owner=建筑时 §1.6/§6.3 全链成立；owner=null（无参构造，实现者的自然写法）时：普通物品 `IsInAnyStorage()=false` → `ShouldBeHaulable` 短路 false（锁定只是假象）；**alwaysHaulable 物品（银、零件等）`IsInValidBestStorage()=false` → 被判 haulable，被搬运工搬空**；放行机制静默失效（放行条目也不会 haulable）；`MapHeld`/`PositionHeld`/温度链全断（TryFindBestBetterNonSlotGroupStorageFor 的距离基准失效）。
- **失败模式是静默的**（不报错、功能全无），最难排查。
- **建议**：§3.2/§4 明确写死"view 构造必须传 owner=this，使条目 ParentHolder 解析到建筑（haulable 判定链的前提）"。

### H5. 食物/治疗"原版兼容"断言错误，allowTakeForUse 开关落空（§1.3-3 / §4.1c / §5.2#7）

- **文档断言**："吃饭、治疗、装备等取用路径原版兼容"（经 GenClosest 的 `canLookInHaulableSources` 分支）；"禁止取出同时关闭工作台原料/食物药品搜索"（引 GenClosest.cs:295）；allowTakeForUse 通过 patch `ClosestThing_Global_Reachable` 放宽。
- **原版代码事实**：`ClosestThing_Global_Reachable` 的 `canLookInHaulableSources`（GenClosest.cs:251/295）**原版无任何调用方传 true**。真正使用 HaulSource 搜索的是 `ClosestThingReachable(lookInHaulSources:)`（GenClosest.cs:55），但只覆盖装备/武器/搬运类（JobGiver_PickUpOpportunisticWeapon、JobGiver_OptimizeApparel、EnterPortalUtility、GatherItemsForCaravanUtility、LoadTransportersJobUtility）。**食物**（`FoodUtility.BestFoodSourceOnMap` 不搜 HaulSource）与**治疗**（`HealthAIUtility`）都不搜 HaulSource。
- **后果**：① §1.3 第 3 点结论错误——"吃饭、治疗"必须由 mod 自己 patch（FoodUtility/HealthAIUtility 或 ListerThings）才有；② §4.1c "禁止取出关闭食物药品搜索"不成立（这些路径本来就看不到容器内容，开关不影响）；③ §5.2#7 patch `ClosestThing_Global_Reachable` 无效，allowTakeForUse 在食物/治疗上实现不了。
- **建议**："使用路径"的支持改由 patch `ListerThings` 或 FoodUtility/HealthAIUtility 实现；§4.1c/§5.2#7 的机制描述改写。"零 patch 复用原版"的覆盖面从 8 条降为约 5 条（工作台原料、存入、haul 移出、装备取用、ITab 框架成立；食物/治疗/精确计数不成立）。

### H6. 读档时序坑：先例 FinalizeInit 赋值 Instance 晚于 SpawnSetup（§1.5/§3.3）

- **原版代码事实**：`Game.LoadGame` 真实顺序：ExposeSmallComponents（GameComponent.ExposeData-LoadingVars）→ World → maps 深读 → `Scribe.loader.FinalizeLoading`（ResolvingCrossRefs + PostLoadInit）→ **建筑 SpawnSetup**（Map.FinalizeLoading → GenSpawn）→ **GameComponent.FinalizeInit** → LoadedGame。
- **结论**：文档"全局层先恢复 → 建筑 SpawnSetup 全量重建"总体**成立**（ExposeData 三阶段全部早于 SpawnSetup）；但引用的先例 `GameComponent_OmniResurrector` 在 **FinalizeInit** 才赋值 static Instance（晚于 SpawnSetup）——照搬则读档时 SpawnSetup 经 Instance 访问全局层得到 null，视图重建失败/抛 NRE。
- **配套问题**：§3.3"清空日志 + 重置版本号"排在最后——若实现者落在 FinalizeInit，会在 SpawnSetup 已设 `lastSeenVersion` 之后把全局版本号重置为初始值 → 下一个 60-tick 检查触发错误同步（重建/清空视图）。日志本身不序列化（读档后天然为空）无需清。
- **建议**：Instance 改在 GameComponent **构造函数**（Game.FillComponents 在 `new Game()` 时实例化组件）或 ExposeData(LoadingVars) 里赋值；或 SpawnSetup 用 `Current.Game.GetComponent<GameComponent_GlobalStorage>()` 兜底。版本号重置移到全局层 ExposeData(PostLoadInit)（早于 SpawnSetup）完成；§1.5 注明"先例的 FinalizeInit 赋值模式不可照搬"。

### H7. §5.1 唯一必需 patch 的 Harmony 写法错误（§5.1）

- **文档写法**："prefix 检查 holder 是否为 Building_GlobalVault，是则 `__result = 冻结温度`（如 -30°C）、return false 短路"。
- **原版代码事实**：方法签名为 `public static bool TryGetFixedTemperature(IThingHolder holder, Thing forThing, out float temperature)`（ThingOwnerUtility.cs:401-404）。Harmony 中 `__result` 注入的是**方法返回值**（bool）；out 参数在 IL 层即 ref，prefix 需声明 `ref float temperature` 写入。调用方 `Thing.AmbientTemperature`（Thing.cs:389-391）在方法返回 true 时立即读取 out 变量。
- **后果**：按文档字面写 `__result = -30f`，类型 float≠bool，Harmony 注入失败/运行时异常；若误写成 `__result = true` 而不写 temperature，调用方读未初始化浮点值。
- **正确写法**：`static bool Prefix(IThingHolder holder, ref float temperature, out bool __result) { if (holder is Building_GlobalVault) { temperature = -30f; __result = true; return false; } return true; }`。
- **附**：文档"不 patch 时回落 21°C"的理由不准确——`Thing.AmbientTemperature`（Thing.cs:379-398）链上全部失败后先走 `SpawnedOrAnyParentSpawned` 分支读**建筑所在格温度**，只有未 Spawned 且无 Tile 才回 21f。patch 必要性不变（要显示"冷冻"而非房间温度），但依据要改。另建议改用 **postfix**（原方法返回 false 时覆盖 `ref temperature` 并 `__result = true`）降低与其他 mod 扩展 TryGetFixedTemperature 的 prefix 冲突风险。

### H8. 预留记账的 `-1` 语义错误（§3.3 记账根基）

- **文档断言**："N = reservation 的 stackCount，`-1` 解析为 job.count 并记入 `Dictionary<Reservation,int>` 解析表"；边界⑤"读档后解析表重建（按 job.count 重解析）"。
- **原版代码事实**：`-1` = `StackCount_All` 常量（ReservationManager.cs:23），在 CanReserve/Reserve 内解析为"**当前 target.Thing.stackCount**"（:118、:245）；所有取物路径（`ReserveAsManyAsPossible`，ReservationUtility.cs:170）都以 `-1` 保留整堆，**与 job.count 无关**。
- **后果**：按 job.count 记账，pawn 想要 100 而副本有 500 时 R 只计 100，其余 400 仍可被其他 pawn 通过 G−R 检查再次预留 → **超卖预留**。
- **建议**：删除"job.count 解析表"整套（含读档重建、释放清理）。R 扫描时对 `StackCount == -1` 的 reservation 按"该副本当前 stackCount"计（与原版 CanReserve 的 `num3 += num1` 一致）——配合已有的实时校正（副本 stackCount = min(G−R+r_this, stackLimit)）自然自洽。

### H9. 组 filter 变更通知链断点（§4.1e"零额外代码"不成立）

- **文档断言**："组设置变化经组 Notify_SettingsChanged → 成员视图刷新（已实现，零额外代码）"。
- **原版代码事实**：`StorageGroup.Notify_SettingsChanged`（StorageGroup.cs:128-135）**只遍历 `ISlotGroupParent` 成员**；本建筑（容器型，非格子型）在组内改 filter 后收不到任何通知。
- **后果**：玩家在组 UI 改 filter 后，本建筑视图/可存取内容不刷新——"禁止=不可见"语义下条目不消失、放行的视图重建不发生；玩家看到过期内容。优先级变化恰好无此问题（Priority setter 走 listerHaulables 重算，含组的 HaulSourcesList），但 filter 路径确实断链。
- **建议**：① patch `StorageGroup.Notify_SettingsChanged` 追加通知非 SlotGroupParent 成员（新增 1 个 patch 点）；② 或建筑 60t tick 比较 `GetStoreSettings()` 签名（filter 摘要 + 优先级），变更时重建视图——零 patch、成本低。文档"零额外代码"结论必须改写。

### H10. 60 tick vs Rare=250 tick 的矛盾（§4.1 def vs §3.3/§3.4/§9）

- 1.6 中 Rare = **250 tick**（TickList.cs:28-29），且 Rare ticker 下 `DoTick` 走 `TickRare()` 分支（Thing.cs:482-485，`IsHashIntervalTick(250)`），**`Tick()` override 根本不会被调用**；CompTickRare 同受 250 间隔约束。
- 按 def 实施则视图刷新滞后 250 tick（§9"滞后 60 tick"错误）；要 60 tick 必须 def 改 `tickerType=Normal` + Tick() 内 `IsHashIntervalTick(60)` 或计数器判断，并统一修改 §3.4/§9 的措辞（"每 tick 一次廉价 hash 判断、每 60 tick 才可能执行同步"）。

### H11. "退休列表"只有名字没有机制（§3.3/§8 P1/§11 E1）

- 文档多处引用"方案 A：数字更新 + 退休列表 + 增量变更日志"，§8 P1 验收标准把它列为交付物，但正文只有"退休移除"四个字（§3.3:187）和边界⑥"退休副本实例保留 → reservation 引用有效"；§11 E1 又说"job 持有副本**永不**被销毁"——与 §3.3 直接矛盾。
- 未定义项：① 退休条件（filter 变化？取空？属性签名变化？）；② 退休副本何时销毁——若"永不销毁"则每 job 泄漏一个 Thing 实例，若 job 结束销毁则 E1 表述错误；③ job 中断（pawn 死亡/取消）后退休副本的清理责任；④ 同条目多副本并存时的影响：`WorkGiver_DoBill.TryFindBestIngredientsHelper` 用 `processedThings.Contains` 按**实例引用**去重（:364/367/388）失效 → 同一逻辑条目双份候选/双倍计数；listerHaulables.Check 按实例逐项判定；ReservationManager 的 target 绑定具体实例（取物走新副本 → 预留记账 R 扫描必须覆盖退休副本）。
- **建议**：在 §3.3 增补"退休副本生命周期"小节，明确定义退休条件、销毁时机（如 job 完成或中断回调统一清理，而非"永不销毁"）、并规定退休副本从视图移除时必须走 `Notify_DeSpawned`（不参与 listerHaulables/搜索）；修正 E1 措辞。

### H12. §3.2 只文档化了 TryAdd 的 int 版，而存入路径实际调 bool 版（§3.2/§1.2）

- **原版代码事实**：存入链 `DepositHauledThingInContainer → TryTransferToContainer` 调用的是 **`otherContainer.TryAdd(thing, canMergeWithExistingStacks)`（bool 版）**（ThingOwner.cs:324）。int 版 `TryAdd(Thing,int,bool)` 是基类具体实现：`item.SplitOff(num)` 后转调 bool 版；bool 版才是真正把物品放进 innerList 的抽象终点。两个都需要 override。
- **后果**：若实现者按 §3.2 表格只 override int 版，搬运工存入时走**基类 bool 版**——真实物品与视图副本 TryAbsorbStack 合并或 `innerList.Add` + NotifyAdded：物品实际进入视图容器、全局层永不收到 Deposit、视图被真实物品污染、计数漂移，且 NotifyAdded 意外触发 §4 的 `Notify_ItemAdded` 同步钩子（双重副作用）。核心设计直接失效。
- **返回值契约（必须规定）**：`TryTransferToContainer` 失败回滚用 `result = num - thing.stackCount`（ThingOwner.cs:329-344，以 stackCount 减值量作为"已转入量"）——bool TryAdd 返回 true ⟺ 全量吸收；返回 false ⟺ 零吸收或已按 stackCount 减值体现部分吸收。
- **同步钩子冲突**：吸收实现不调 `base.NotifyAdded`，§4 规划的 `Notify_ItemAdded` 钩子**不会自动触发**——文档中"TryAdd 内同步"与"Notify_ItemAdded 同步"两条路径必须明确单一职责（吸收 TryAdd 自行完成全局 Deposit + 视图刷新 + `listerHaulables.Notify_AddedThing`；§4 的 Notify_ItemAdded 不用于存入路径），否则双同步/漏同步。

### H13. §7.2#3 把原料 toil 误认为产物 toil（§7 扩展）

- **文档断言**："产物制作完成后 `Toils_Haul.PlaceHauledThingInCell` 放工作台旁 → 搬运工再搬入存储"。
- **原版代码事实**：产物链是 `Toils_Recipe.FinishRecipeAndStartStoringProduct`（Toils_Recipe.cs:152-270）：DropOnFloor 模式 `GenPlace.TryPlaceThing` 就地放；BestStockpile/SpecificStockpile 模式 pawn 自己 `TryStartCarry` + 立即启动 `HaulToCellStorageJob`——**不存在"放工作台旁等搬运工"**。`PlaceHauledThingInCell` 只用于**原料**放置（JobDriver_DoBill.cs:112）。
- **后果**：按文档实施 A3（"产物放置 toil postfix"）会 patch 错对象（可能误伤原料 toil）；正确 patch 点是 `FinishRecipeAndStartStoringProduct` 的 initAction 在 `MakeRecipeProducts` 之后、TryStartCarry 之前，且必须按 `BillStoreMode` 三分支区分（DropOnFloor 是否拦截需决策）。
- **附带**：§7.3 A2"复用 `JumpIfTargetInsideBillGiver`"不可行——该方法是 `private static`（JobDriver_DoBill.cs:123-144），mod 无法调用，只能 transpiler 或复制逻辑。另外 §7.2 漏掉了 1.6 已原生内置的 `Building_WorkTableAutonomous` 免行走候选分支（WorkGiver_DoBill.cs:348-356 + JobDriver_DoBill.cs:82/119），方案 B 的"低风险"可以更直接地借力原版。

---

## 二、中等问题（实现期必踩）

1. **ListerHaulables 频率断言错误（§9/§5.2#6/§3.4）**：`ListerHaulablesTick` 每 tick 运行；`HaulSourcesCheckTick`（ListerHaulables.cs:90-103）`num1 = CeilToInt(count/4)`，每 tick 处理 min(4,N) 个非空 HaulSource 并**全量 Check 其全部直接持有物**——HaulSource 数 ≤4 时**每个每 tick 全量 Check**（不是"每 4 tick"）。且轮转 `index2 = num2 + index1` 无取模：count≥6 时部分 HaulSource（如 N=8 时 index 5-7）永不周期检查，只能靠事件通知兜底。性能估算低估约 4 倍。#6 patch 从"可选"实为**必需**（§9 与 §5.2 的定位互相矛盾）。
2. **§5.2 #6 与 §1.6/§6.3 语义互斥未声明**：#6 短路"Accepts==true 即锁定"会抹除"低→高自动流出"行为；同时它是 M10 循环（H1）的现成解药。且 #6 的短路条件必须精确排除放行条目（Accepts=false），否则 §6.3 移出机制整体失效。文档须统一口径并交叉引用。
3. **Reserve 的 playerForced 绕过（§3.3/§5.2#8）**：`Reserve`（:256-279）`job.playerForced` 时若普通 CanReserve 失败，会再调 `CanReserve(ignoreOtherReservations:true)` 通过则**直接 Add 并强行打断他人 job**——可击穿 G−R 检查（不超卖但"保留阶段阻止"语义被破坏）。patch 需无条件执行数量检查。
4. **Log.Error 刷屏风险（§3.3）**：数量不足拒绝在 `errorOnFailed=true`（TryMakePreToilReservations 默认）下每次都打完整 `Log.Error`（ReservationManager.cs:575-624，含现有保留者列表）。跨建筑共享下"数量不足"比原版频繁得多——patch 中对本系统条目拒绝应静默返回 false。且"pawn 走原版失败重试路径"描述不准：实际是 `EndCurrentJob(Incompletable)`（Toils_Reserve.cs:33）或 job 不开始、pawn 被 WorkGiver 重新分配工作（非"重试同一 job"）。
5. **CanReserveStack 未纳入 patch 清单**：`CanReserveStack`（ReservationManager.cs:185-224）是独立入口（用药路径 Toils_Tend/JobDriver_TendPatient），它只对同一 target 累计他人预留，看不见其他建筑副本对同一全局条目的预留——B 建筑医生仍可保留已锁实物（SplitOff 校正兜底不超卖，但体验不一致）。
6. **MinifiedThing 的 stackLimit=1（§3.2）**：MinifiedThingDef 未定义 stackLimit → 默认 1。全局存 100 个打包建筑的条目，视图副本 stackCount=1：UI 滑条/丢弃一次只能拿 1、搬运量按 1 计算。需对 MinifiedThing 类副本例外处理。
7. **视图容器深存是纯负收益（§4 表'存档'行/§3.4）**：全局层已深存真相，建筑视图（每建筑一份物化副本）再 LookMode.Deep = 存档体积×建筑数（§3.4 估算漏乘）、读档三阶段 ExposeData 白跑后丢弃、并埋下"忘记丢弃→双真相"隐患。建筑 ExposeData 只存 settings（+出入开关/放行列表等状态）即可；视图在 SpawnSetup 重建。
8. **dontTickContents=true 未写成强制项（§3.2/§4.1）**：§1.1"未 Spawned 不 tick → 冻结天然成立"与 §1.4"必须设 true"自相矛盾；实际持有者被 tick 时 `Thing.DoTick`（Thing.cs:492-509）递归 tick 内容物（持有者 Rare 则内容物每 250 tick 被 tick 一次，§1.4"每 tick 被 tick"表述不准）。§3.2 必须写死"VaultViewThingOwner 构造必须 `dontTickContents = true`"。
9. **§4.1e"取消链接写回是增强"定性错误**：1.6 原版已内建写回（`StorageGroupUtility.SetStorageGroup` :145 `member.StoreSettings.CopyFrom(...)`、SpawnSetup 跨地图 :139）；真正缺口是**组 gizmo 取消链接路径只特判 `Building_Storage` 类型**（:89-90）——Group setter 写回恰好补上该缺口，因此设计是**必要且正确**的，应改述为"对齐原版写回语义、覆盖 gizmo 取消链接路径"，并注明与原版写回重复执行幂等。已验证：Frame 建造完成（:250 入组 + :251 CopyFrom 在后）、Building.cs:273、SpawnSetup 跨地图、Destroy——所有路径下 setter 写回均安全、不会覆盖未初始化 settings。
10. **def XML 缺 `<inspectorTabs>` 配置（§4.1）**：`Thing.GetInspectTabs` 返回 `def.inspectorTabsResolved`（Thing.cs:1114-1117），tab 靠 def XML 声明（ITab_Storage 的 IsVisible 靠 IStoreSettingsParent 只是显隐条件）。照抄 §4.1 XML 模板会得到零 inspect tab。需增加 `<inspectorTabs><li>ITab_Storage</li><li>FAOC_ITab_VaultContents</li></inspectorTabs>`（或注明 override GetInspectTabs 返回）。
11. **Notify_ItemAdded/Removed 缺 Spawned 守卫（§4 表）**：`Building_OutfitStand`（:153/166）与 `Building_Bookcase`（:127-129）均有 `if (Spawned)` 守卫——DeSpawn 后 MapHeld 为 null，无条件调 `listerHaulables.Notify_DeSpawned` 会 NPE；minify 的 DeSpawn 先于视图清空发生。照文档实现会在 minify/拆除时崩。守卫之外，全局层同步始终执行。
12. **HaulFromSource 浮菜单需建筑主动调用（§6.3/§4 表）**：`HaulSourceUtility.GetFloatMenuOptions(this, selPawn)` 需建筑在 `GetFloatMenuOptions` 里主动调用（Bookcase.cs:310/OutfitStand.cs:538 先例），不会"自动出现"。§4 表未列此项。
13. **§4 表缺 Notify_SettingsChanged 条目**：filter/优先级变化时 listerHaulables 重算 + `haulDestinationManager.Notify_HaulDestinationChangedPriority()` 重排序的主入口（Building_Storage 先例）。漏实现则 `AllHaulDestinationsListInPriorityOrder` 失序 → `TryFindBestBetterNonSlotGroupStorageFor` 的 `else break` 提前退出 → 目标选择错误。
14. **SpaceRemainingFor=MaxValue 无 filter 检查的语义需文档化**：`DepositHauledThingInContainer`/`TryTransferToContainer` 只查容量不查 Accepts——job 生成后修改 filter 不会阻止已生成的存入 job（原版 Bookcase/OutfitStand 同，非缺陷但必须知情）。
15. **§4.1d 第 1 条多选同步表述误导**：点击合并组并非无条件对组内每个 gizmo 调 ProcessInput——有 `interactedGiz.InheritInteractionsFrom(other)` 门控（GizmoGridDrawer.cs:311-312）；Command_Toggle 组内 isActive 不一致时**只有代表对象被切换**。第 1/3 条应合并表述。
16. **SplitOff 部分拆分属性复制依赖 comp 的 PostSplitOff 完整性（§3.1/§3.5）**：`Thing.SplitOff` 部分分支（Thing.cs:1217-1225）`ThingMaker.MakeThing(def, Stuff)` 只显式复制 stackCount/HitPoints，其余靠 `ThingWithComps.SplitOff` 的 `PostSplitOff(piece)`。第三方 comp 的 PostSplitOff 空实现时取出物属性与分组键漂移（品质/耐久段/comp 状态），L2 个体模式尤其致命。取出路径可考虑避开 SplitOff（全局 Withdraw 直接 ThingMaker.MakeThing + 从全局条目 proto 显式恢复全部属性）。
17. **ITab_VaultContents 必须连同滑条/丢弃逻辑一起重写（§3.2/§1.3-4）**：`ITab_ContentsBase` 行渲染（:68-74）、滑条 1..count（:99-100）、丢弃按钮都以 stackCount 为源——只自定义"行渲染"不够，否则玩家在内容 Tab 上永远拿不到 stackLimit 以上数量。
18. **"禁止取出"不关闭 RecipeWorkerCounter 计数（§4.1c）**：`CountProducts`（RecipeWorkerCounter.cs:44-45）遍历 HaulSources 时无 `HaulSourceEnabled` 检查——"只入不出"模式下工作台清单仍显示"已有 N 个 X"但取料搜索找不到。应文档化或补 patch。

---

## 三、低严重度与勘误

1. **行号偏差**：HaulAIUtility.cs:73→实际 75、:124→实际 131；Pawn_CarryTracker.cs:63→SplitOff 在 86 行；Building_Bookcase.cs:126-131→实际 125-130；Building_Storage.cs:141-153→实测 130-140、:35-73→实测 35-55。UI 相关行号（Command.cs:225、Gizmo.cs:18、GizmoGridDrawer.cs:307-315、Building.cs:273、Frame.cs:146/250/322）全部精确吻合。
2. **§6.5"物化必须按 stackLimit 分批"必要性弱**：`GenPlace`（GenPlace.cs:251）已自动按 stackLimit 拆分后放置，`GenDrop.TryDropSpawn` 本身不查 stackLimit。显式分批无害但非"必须"；注意 TryDropSpawn 会把剩余留在传入 thing 里，调用方忽略会丢数量。
3. **存档对账修复方向未定义（§3.3）**："存档时全量对账（校验不一致并修复）"未说明以谁为准（全局 vs 副本）。按"全局层是真相"原则应重算副本并谨慎处理已拆出的悬挂 piece（回滚漂移产生的孤儿物品），建议明确"对账只修副本、全局只增不凭空减、差异写入日志"。
4. **被吸收实例与 `job.placedThings` 的引用冲突（§3.2"零 GC 复用"）**：`DepositHauledThingInContainer` 存入成功后 `HaulAIUtility.UpdateJobWithPlacedThings(curJob, carriedThing, num2)` 把被吸收实例放进 `job.placedThings`（DoBill job 场景）；若立即复用实例为新条目代表 Thing，该引用被污染。复用前需检查引用（或延迟复用/新建实例）。另注意 Thing 实例的 def 是构造期不可变的，"零 GC 复用"仅限同 def 场景。
5. **`EverStorableFixedSettings()` 是全允许（§4 表澄清）**：其 filter = 全部 EverStorable 物品（`CreateOnlyEverStorableThingFilter`），与 §6.2"空 filter=全禁止"并列时易误读，需注明"该静态父设置为全允许，对本建筑不构成限制"。
6. **1.6 `Command` 新增 `groupKeyIgnoreContent` 合并路径**（Command.cs:31-32、:225）文档未提；默认 -1 无影响，但实现时勿给三开关设非 -1 值。
7. **`ISearchableContents` 基准成员与 1.6 不符**：1.6 接口只有 `SearchableContents` 属性（ISearchableContents.cs:12-15），无 GetSearchableContents/SearchCache（旧版形态）。文档只写"实现 ISearchableContents"，无实际影响，仅记录。
8. **§4.1b 对 `Notify_MinifiedThingAboutToBeDestroyed` 的用途描述不准确**：注册清理发生在 minify 时的 `Thing.DeSpawn`（Thing.cs:702-705）；该回调（MinifiedThing.cs:204 调用、OutfitStand.cs:303-308 实现）原版用途是**内容落物**，与注册无关。对本设计（内容保留全局层）可作为防御性钩子，但理由应改为"防止异常路径下的残留/清理内部状态"。
9. **§1.2"参考 Building_OutfitStand 实现"论证 SpaceRemainingFor 返回 MaxValue 不精确**：OutfitStand 实际返回 0/1（且内含 AllowedToAccept 检查），它只是接口形态参照。MaxValue 安全性经运算链验证成立（EnrouteUtility、StoreUtility、JobDriver_HaulToContainer、Toils_Haul 共 6 处调用点的 min/max/==0 运算下均安全）。
10. **§6.3 措辞**："Accepts=false → IsInValidStorage=false"应为 **IsInValidBestStorage**（`IsInValidStorage` 只出现在 `PawnCanAutomaticallyHaul`，自动搬运经 `WorkGiver_Haul.JobOnThing` 用 `PawnCanAutomaticallyHaulFast`，不经过它）。

---

## 四、验证通过的核心断言（设计可放心依赖）

| 断言 | 依据 |
|---|---|
| 存入主链：`HaulToStorageJob` 分支、`HaulToContainerJob` count 公式、`TryGetInnerInteractableThingOwner`、`DepositHauledThingInContainer → TryTransferToContainer` | HaulAIUtility.cs:75-133、ThingOwnerUtility.cs:34-43、Toils_Haul.cs:347-412 |
| `GetCountCanAccept` 是 virtual | ThingOwner.cs:155 |
| **§6.2「空 filter = 全禁止」与原版 ThingFilter 语义一致** | `ThingFilter.Allows(ThingDef) => allowedDefs.Contains(def)`（ThingFilter.cs:703），空集合恒 false；原版 Building_Storage.PostMake 在无 defaultStorageSettings 时同样留空。全禁止 = 空 filter 或 SetDisallowAll()（不存在 SetAllow(null,false) 这种 API） |
| 读档"全局层先于建筑 SpawnSetup 恢复" | Game.LoadGame：ExposeSmallComponents → maps 深读 → FinalizeLoading → SpawnSetup → GameComponent.FinalizeInit |
| `Thing.DeSpawn`/`SpawnSetup` 自动注销/注册 HaulDestination/HaulSource | Thing.cs:598-601、702-705 |
| OutfitStand `allowRemovingItems` ↔ `HaulSourceEnabled` 先例行号（:45/124/535/701/775） | Building_OutfitStand.cs，全部吻合 |
| `HaulDestinationEnabled=false` 使搬运工不再选中 | StoreUtility.TryFindBestBetterNonSlotGroupStorageFor（:251）、TryFindBestBetterStoreCellFor（:170） |
| UI 多选同步：GroupsWith 合并条件、InheritInteractionsFrom、activateIfAmbiguous 默认 true、alsoClickIfOtherInGroupClicked | Command.cs:223-225、Command_Toggle.cs:20/44-46、Gizmo.cs:18 |
| StorageSettingsClipboard 复制/粘贴/多选同步（无 groupKey、hotKey Misc4/Misc5） | StorageSettingsClipboard.cs |
| Priority setter 触发 HaulDestinationManager 重排序 | StorageSettings.cs:28-56 |
| 预留记账 R 扫描可行（Reservation.StackCount 公开 getter） | ReservationManager.cs:645 |
| ITab_ContentsBase：container 抽象、OnDropThing → SplitOff(count) → GenDrop | ITab_ContentsBase.cs:32/83-86 |
| `TakeToInventory` 对未 Spawned 目标直接 SplitOff + 背包 TryAdd | Toils_Haul.cs:491-501 |
| 蓝图/Frame 存储组继承（Building.cs:273、Frame.cs:146/250/322） | 行号精确吻合 |
| ThingListGroupHelper 对 IHaulSource 的判定自动生效 | ThingListGroupHelper.cs:196-197 |

---

## 五、建议行动（按优先级）

1. **先裁决方向性缺陷**：H1（M10 循环 → 建议 #6 升级默认 patch + 锁定语义，一并消除"低→高流出"歧义）、H2（取出侧可见性 → 新增搜索/取料/计数数量替换 patch 或即时补回）、H4（view owner 一行命门，写入 §3.2/§4 强制条款）。
2. **修订同步协议**：H3（SplitOff 单点 patch + 防双扣 + TryAbsorbStack 回滚补偿）、H8（删除 -1 解析表，按副本当前 stackCount 计 R）。
3. **修订读档与 patch 写法**：H6（Instance 赋值时机 + 版本号重置前移）、H7（ref float temperature 写法）。
4. **修订 UI/组相关**：H9（组 filter 通知链 patch 或 60t 签名比较）、H10（tickerType=Normal）、def XML 补 inspectorTabs、Notify_* 加 Spawned 守卫。
5. **补齐缺失定义**：H11（退休副本生命周期小节，含销毁时机与 job 中断清理）。
6. **§7 扩展修正**：H13（产物 toil 是 FinishRecipeAndStartStoringProduct，按 BillStoreMode 三分支设计；利用 1.6 原生 WorkTableAutonomous 分支）。
7. **勘误**：第三节所列行号与措辞修正。

> 备注：本报告中"主线核验"指审查者直接阅读原版反编译源码（Assembly-CSharp 1.6.9438.37837）确认的关键断言：Thing.SplitOff（Thing.cs:1205-1227）、ListerHaulables 检查频率与 ShouldBeHaulable（ListerHaulables.cs:62-151）、StoreUtility 的 CurrentHaulDestinationOf/IsInValidBestStorage/TryFindBestBetterNonSlotGroupStorageFor（StoreUtility.cs:18-304）、StorageGroup.Notify_SettingsChanged（StorageGroup.cs:128-135）。其余条目由并行子代理基于同源反编译源码验证并附行号，个别行号可能有 ±几行反编译漂移（已多处对照校对）。
