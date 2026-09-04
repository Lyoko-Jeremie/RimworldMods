using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using FullyAutomaticOmniCrafter;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    
    [StaticConstructorOnStartup]
    public static class OuterrealmStorageTex
    {
        public static readonly Texture2D VaultAllowDepositIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultAllowDeposit") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D VaultAllowWithdrawIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultAllowWithdraw") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D VaultAllowTakeForUseIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultAllowTakeForUse") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D VaultFrozenIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultFrozen") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D StorageManagerOpenIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_StorageManagerOpen") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D SubspaceAccessManagerOpenIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_SubspaceAccessManagerOpen") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D SubspaceAccessOpenManagerSelfIcon = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_SubspaceAccessOpenManagerSelf") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D VaultRightClickMenuModeIcon_Menu = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultRightClickMenuMode_Menu") ?? 
            BaseContent.WhiteTex;
        
        public static readonly Texture2D VaultRightClickMenuModeIcon_List = 
            ContentFinder<Texture2D>.Get("UI/Commands/OmniStorage_VaultRightClickMenuMode_List") ?? 
            BaseContent.WhiteTex;
        
    }
    
    /// <summary>
    /// 超维存储仓建筑：超维空间的"存储终端"（§4）。
    /// 对外行为 = 1.6 原版容器型存储（Building_OutfitStand / Building_Bookcase 同类）：
    /// 搬运工可存入（HaulToContainer）、可取出（HaulSource / 工作台原料 / 装备取用）、
    /// 有存储清单 UI（ITab_Storage）与优先级；内容始终保存在全局层（GameComponent_OuterrealmStorage），
    /// 本建筑只持有"视图"（OuterrealmVaultViewThingOwner = 全局条目的投影副本缓存）。
    /// 
    /// 继承 Building_Storage（而非 Building）的原因：让第三方 Mod 的"原版存储"判定
    /// （is Building_Storage / is Zone_Stockpile，如鸦族无人机物流箱的"输出到原版存储"）
    /// 直接放行本建筑；内部行为全部按 vault 语义覆盖（见各 `new` 隐藏成员与 override）。
    /// 基类 public 字段 settings / storageGroup / slotGroup 直接复用，不重复声明。
    /// </summary>
    public class Building_OuterrealmVault : Building_Storage,
        IHaulDestination,
        ISlotGroupParent,
        IStoreSettingsParent,
        IHaulSource,
        IThingHolder,
        IThingHolderEvents<Thing>,
        IHaulEnroute,
        ISearchableContents,
        IStorageGroupMember,
        IApparelSource,
        IOuterrealmVaultContext
    {
        /// <summary>独立存储清单（filter 决定本建筑"能看到/存取哪些条目"，§6.2）。复用基类 Building_Storage 的 public settings 字段。</summary>

        /// <summary>视图容器（§3.2：owner=建筑 是 haulable 判定链命门；构造内已设 dontTickContents）。</summary>
        public OuterrealmVaultViewThingOwner view;

        // 存储组（storageGroup）与存储格（slotGroup）复用基类 Building_Storage 的 public 字段，
        // 不再重复声明；GetStoreSettings/GetSlotGroup 等基类非虚实现直接适用。

        /// <summary>外来物品吸收倒计时（§v4）：物品落格 → 倒计时（AbsorbDelayTicks）→ 到期吸收进全局层。
        /// 分散吸收时机（避免统一 60 tick 批处理）且缩短竞争窗口（第三方选中前即吸收）。不序列化（读档后走兕底）。</summary>
        private readonly Dictionary<Thing, int> absorbTimers = new Dictionary<Thing, int>();
        private const int AbsorbDelayTicks = 15; // 0.25s：足够 haul 放置 toil 完成，且竞争窗口极短

        // ── 出入模式（§4.1c；gizmo UI 于 P2 提供） ──
        private bool noDeposit;    // on = 禁止存入（HaulDestinationEnabled=false）
        private bool noWithdraw;   // on = 禁止取出（HaulSourceEnabled=false）
        private bool allowTakeForUse; // 条件开关：noWithdraw 开启时放宽工作台/食物搜索（§5.2 #7，P4 实现）
        private bool frozen;       // 冻结开关：隐藏全部物品并暂停建筑工作，但保持 filter 不变

        // ── 右键菜单显示形态（每建筑独立，随存档保存） ──
        // 原为全局 Mod 设置（跨存档），改为建筑实例字段后每个存储仓可单独选择
        // 原版菜单或自制大列表；由建筑 gizmo 切换，ExposeData 序列化。
        private RightClickMenuMode rightClickMenuMode = RightClickMenuMode.Vanilla;
        /// <summary>启用自定义右键菜单后使用的具体样式；默认保留旧版完整操作列表。</summary>
        private VaultCustomMenuMode customMenuMode = VaultCustomMenuMode.FullOptionList;

        public bool NoDeposit => noDeposit;
        public bool NoWithdraw => noWithdraw;
        public bool AllowTakeForUse => allowTakeForUse;
        public bool Frozen => frozen;

        /// <summary>本建筑的右键菜单显示形态（原版 / 自制大列表），每建筑独立、随存档保存。</summary>
        public RightClickMenuMode RightClickMenuMode => rightClickMenuMode;

        /// <summary>本建筑自定义右键菜单的内部样式（完整操作列表 / 先物品后操作）。</summary>
        public VaultCustomMenuMode CustomMenuMode => customMenuMode;

        // ── InspectString 摘要缓存（§4：InspectPaneFiller 每帧调用，避免每帧拼接几百条目） ──
        private string cachedInspectString;
        private int cachedInspectVersion = -1;

        /// <summary>建筑上次同步的全局版本号（§3.3 懒同步，随 Tick 末微批迁移后仅作状态记录）。</summary>
        private int lastSeenVersion;

        // ── 构造 / 生命周期 ────────────────────────────────────────────────────

        public Building_OuterrealmVault()
        {
            view = new OuterrealmVaultViewThingOwner(this);
            // slotGroup 由基类 Building_Storage 构造函数创建（new SlotGroup(this)）
        }

        public override void PostMake()
        {
            base.PostMake(); // Building_Storage：创建 settings 并 CopyFrom(defaultStorageSettings)
            // 不配置 defaultStorageSettings：PostMake 后 filter 为空 = 默认全禁止（§6.2）
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // 跨地图自动断开存储组已由 Building_Storage.SpawnSetup 处理（Building_Storage.cs:130-140）
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs != null)
            {
                gs.RegisterVault(this);
                // 投影不参与存档；读档/重连后按 GameComponent 的全局固定预算逐步重建，
                // 避免在 SpawnSetup 中同步扫描全部条目。
                view.ClearView();
                view.ResetMaterializationWork();
                view.InitializeFilterSnapshot();
                view.MarkMaterializeDirty();
                lastSeenVersion = gs.Version;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs != null)
            {
                gs.UnregisterVault(this);
            }
            if (view != null)
            {
                view.ReturnAllBorrowed(); // §v4：回收格上借出副本（剩余量回全局；锚点重建仅 Spawned 时执行）
                view.ClearView(); // 内容保留在全局层（§4.1b 断开访问，不落地）
                view.ResetMaterializationWork();
            }
            if (storageGroup != null)
            {
                storageGroup.RemoveMember(this);
                storageGroup = null;
            }
            base.DeSpawn(mode);
        }

        // Destroy 无需 override：Building_Storage.Destroy 已处理存储组移除与
        // BillUtility.Notify_ISlotGroupRemoved（bill 存储模式引用清理，vault 此前缺失，继承后自动补上）。

        public override void Notify_MinifiedThingAboutToBeDestroyed(DestroyMode mode)
        {
            // 防御性钩子（仿 Building_OutfitStand）：本设计内容保留全局层，无需落地；
            // 仅借用此路径确保异常情况下的内部状态被清理。
            base.Notify_MinifiedThingAboutToBeDestroyed(mode);
        }

        // ── 视图同步（§3.3 实时同步方案 B：内容由全局层 Tick 末微批统一驱动） ──

        protected override void Tick()
        {
            base.Tick();
            // §v4：吸收倒计时处理（每 tick 检查小集合；物品到期且条件满足则吸收进全局层）
            if (absorbTimers.Count > 0)
            {
                ProcessAbsorbTimers();
            }
            // §v4：外来物品吸收兜底（rare tick，250 tick ≈ 4s）：只处理未登记异常残留（读档后/绕过钩子），
            // 正常吸收已由每物品倒计时完成，低频兜底足够
            if (this.IsHashIntervalTick(250))
            {
                AbsorbForeignItems();
            }
            if (!this.IsHashIntervalTick(60))
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || view == null)
            {
                return;
            }
            // §v4：回收已释放预留的借出副本（剩余量回全局、重建锚点）
            view.ReturnUnreservedBorrowed();
            // §v5：region 重建脏标记批量刷新（Postfix 只置脏；此处每 60 tick 最多一次
            // O(副本数) region 补注册——静置零开销，重建后 ≤60 tick 内恢复可见性）
            if (view.ConsumeRegionDirty())
            {
                view.RefreshRegionRegistrations();
            }
            // §3.3 事件驱动方案：视图内容同步及溢出恢复均由 GameComponent 的固定预算队列驱动。
        }

        /// <summary>吸收存储格上的外来未预留物品（§v4 兜底）：只处理未登记倒计时的异常残留
        /// （正常路径经 Notify_ReceivedThing 登记后由 ProcessAbsorbTimers 吸收）。</summary>
        private void AbsorbForeignItems()
        {
            List<IntVec3> cells = AllSlotCellsList();
            List<Thing> toAbsorb = null;
            for (int i = 0; i < cells.Count; i++)
            {
                List<Thing> things = MapHeld.thingGrid.ThingsListAt(cells[i]);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j];
                    if (t == null || t.Destroyed || t.def.category != ThingCategory.Item)
                    {
                        continue;
                    }
                    if (absorbTimers.ContainsKey(t) || view.IsBorrowed(t)
                        || OuterrealmVaultUtil.IsOuterrealmBorrowed(t)
                        || MapHeld.reservationManager.IsReserved(t) || !CanAbsorb(t))
                    {
                        continue;
                    }
                    if (toAbsorb == null)
                    {
                        toAbsorb = new List<Thing>();
                    }
                    toAbsorb.Add(t);
                }
            }
            if (toAbsorb == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            for (int i = 0; i < toAbsorb.Count; i++)
            {
                gs?.Deposit(toAbsorb[i], this);
            }
        }

        /// <summary>吸收倒计时处理（§v4）：遍历登记，到期且条件满足 → Deposit；失效（被取走/销毁/预留/filter 禁止）→ 清理登记。</summary>
        private void ProcessAbsorbTimers()
        {
            List<Thing> toAbsorb = null;
            List<Thing> toClear = null;
            foreach (KeyValuePair<Thing, int> kv in absorbTimers)
            {
                Thing t = kv.Key;
                if (t == null || t.Destroyed || !t.Spawned)
                {
                    // 物品被取走/销毁（hauler 拿走、玩家 haul 等）→ 清理登记
                    if (toClear == null)
                    {
                        toClear = new List<Thing>();
                    }
                    toClear.Add(t);
                    continue;
                }
                if (GenTicks.TicksGame < kv.Value)
                {
                    continue; // 未到期
                }
                if (view.IsBorrowed(t) || OuterrealmVaultUtil.IsOuterrealmBorrowed(t)
                    || MapHeld.reservationManager.IsReserved(t) || !CanAbsorb(t))
                {
                    // 到期但被预留使用 / filter 已禁止 / 禁止存入 → 不再吸收（物品归游戏/玩家），清理登记
                    if (toClear == null)
                    {
                        toClear = new List<Thing>();
                    }
                    toClear.Add(t);
                    continue;
                }
                if (toAbsorb == null)
                {
                    toAbsorb = new List<Thing>();
                }
                toAbsorb.Add(t);
                if (toClear == null)
                {
                    toClear = new List<Thing>();
                }
                toClear.Add(t);
            }
            if (toClear != null)
            {
                for (int i = 0; i < toClear.Count; i++)
                {
                    absorbTimers.Remove(toClear[i]);
                }
            }
            if (toAbsorb != null)
            {
                GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
                for (int i = 0; i < toAbsorb.Count; i++)
                {
                    gs?.Deposit(toAbsorb[i], this);
                }
            }
        }

        // ── ISlotGroupParent / 存储格（§v4） ────────────────────────────────────

        /// <summary>存储格容量：每格最多 255 堆（v4 预留驱动物化；预留中物品短暂驻留，不设并发限制）。</summary>
        public override int MaxItemsInCell => 255;

        // GetSlotGroup / AllSlotCells / IgnoreStoredThingsBeauty / SlotYielderLabel /
        // GroupingLabel / GroupingOrder 与基类 Building_Storage 实现一致，直接继承。

        /// <summary>存储格列表：现算不缓存（new 隐藏基类缓存版）——vault 可 rotatable，
        /// 缓存会在旋转后过期（HaulDestinationManager 的格子注册由原版在 SpawnSetup 时设置，
        /// 旋转为边缘场景，拆建即可恢复）。经 ISlotGroupParent 接口（SlotGroup.CellsList）访问同样命中本实现。</summary>
        public new List<IntVec3> AllSlotCellsList()
        {
            List<IntVec3> cells = new List<IntVec3>();
            if (Spawned)
            {
                foreach (IntVec3 c in GenAdj.CellsOccupiedBy(this))
                {
                    cells.Add(c);
                }
            }
            return cells;
        }

        /// <summary>找存储格空位（每格 < MaxItemsInCell 堆；返回 Invalid 表示无空位，§v4）。</summary>
        public IntVec3 FindStorageCellFor(Thing copy)
        {
            List<IntVec3> cells = AllSlotCellsList();
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].GetItemCount(MapHeld) < MaxItemsInCell)
                {
                    return cells[i];
                }
            }
            return IntVec3.Invalid;
        }

        /// <summary>物品落格钩子（Thing.SpawnSetup → Position.GetSlotGroup().parent 触发，§v4）。
        /// 双接口后搬运工存入已走 v3 容器（HaulToContainer → view 吸收），不落格；本钩子只服务
        /// 掉落物/异常 Spawn 在 vault 格上的物品：登记吸收倒计时（15 tick）→ 到期吸收进全局层。
        /// 禁止存入（noDeposit）开启时不登记——物品留格子由玩家处置（§noDeposit 落格门控）。
        /// override 基类 Building_Storage（原版教学提示经 base 保留）。</summary>
        public override void Notify_ReceivedThing(Thing newItem)
        {
            base.Notify_ReceivedThing(newItem); // 原版 storedConceptLearnOpportunity 教学提示
            if (newItem == null || newItem.Destroyed || !Spawned || view == null)
            {
                return;
            }
            if (view.IsBorrowed(newItem) || OuterrealmVaultUtil.IsOuterrealmBorrowed(newItem))
            {
                return; // 借出副本（预留物化）不登记
            }
            if (!CanAbsorb(newItem))
            {
                return; // 禁止存入/冻结/filter 不允许：不吸收，物品留格子由玩家处置
            }
            absorbTimers[newItem] = GenTicks.TicksGame + AbsorbDelayTicks;
        }

        // Notify_LostThing 继承基类空实现。

        // ── IStoreSettingsParent / IHaulDestination / IHaulSource ───────────────

        // StorageTabVisible / GetStoreSettings / GetParentStoreSettings 与基类 Building_Storage
        // 实现一致，直接继承（filter 的读写统一走基类 public settings / storageGroup 字段）。

        /// <summary>new 隐藏基类 Notify_SettingsChanged：除通知原版 haul 目标/来源重排外，
        /// 按新 filter 同步视图（§4 表 + §filter 视图过滤简化）：
        /// filter 只控制视图"可见/可访问"，不触发任何物品物理移动（内容始终在全局层），
        /// 故同步只移除不再允许的副本（O(视图副本数)，"禁止"权限语义须立即生效）；
        /// 新允许条目的物化延迟到后续 Tick 微批（"允许"仅影响可见性，物品不丢失，无紧迫性），
        /// 避免每次 filter 点击触发 O(全局条目数) 全量重建 + 物化 + region 注册导致卡顿。
        /// 组设置变化经 Patch_StorageGroup_Notify_SettingsChanged 补链后同样到达此处（事件驱动，无需轮询）。</summary>
        public new void Notify_SettingsChanged()
        {
            if (!Spawned)
            {
                return;
            }
            view?.RemoveDisallowedCopies(); // 同步移除（先摘除被禁副本，再重排 haul 源/目标）
            MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
            MapHeld.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            view?.RefreshFilterDeltaAndQueue(); // 新允许 Def 优先，特殊过滤条件由完整预算扫描兜底
            GameComponent_OuterrealmStorage.Instance?.NotifyIdentityRoutingChanged(this);
            lastSeenVersion = GameComponent_OuterrealmStorage.Instance != null ? GameComponent_OuterrealmStorage.Instance.Version : lastSeenVersion;
        }

        /// <summary>new 隐藏基类恒 true 的实现：vault 禁止存入或冻结时停止作为 haul 目标。
        /// 冻结必须在此门控，因为原版格子型存储搜索只检查 HaulDestinationEnabled 和
        /// SlotGroup.Settings，不会调用本类带冻结判断的 Accepts。第三方（含鸦族无人机）经
        /// IHaulDestination 接口访问时，接口调度同样命中本实现。</summary>
        public new bool HaulDestinationEnabled => Spawned && !noDeposit && !frozen;

        public bool HaulSourceEnabled => Spawned && !noWithdraw && !frozen;

        // IApparelSource（§装备优化兼容）：让 JobDriver_Wear/JobGiver_OptimizeApparel 走"从衣柜取"路径
        // ——否则视图里的衣物副本被判为普通候选，穿戴流程对未 Spawned 物品必然失败，
        // 形成"Wear job 立即失败 → 重复生成"循环（Mia started 10 jobs in one tick 报错）。
        // ApparelSourceEnabled 与"允许取出"及冻结联动：不可取出时 OptimizeApparel 跳过本建筑衣物。
        public bool ApparelSourceEnabled => Spawned && !noWithdraw && !frozen;

        /// <summary>投影不能通过 bool RemoveApparel 交付；由租出或 Wear 的 Checkout 取得真实衣物。</summary>
        public bool RemoveApparel(Apparel apparel)
        {
            return !OuterrealmVaultUtil.IsProjection(apparel) && view != null && view.Contains(apparel) && view.Remove(apparel);
        }

        /// <summary>new 隐藏基类 Accepts（仅 filter 判定）：vault 增加 frozen 门控——冻结时拒绝一切存入。</summary>
        public new bool Accepts(Thing t)
        {
            // 弹出落地已排除建筑占格（吸收机制只处理落格物品），不再需要防回吸门卫；仅按 filter 门控。
            return CanShow(t);
        }

        /// <summary>是否向该建筑展示/物化某物品：冻结时恒 false；否则取决于 filter。保持 filter 不变。</summary>
        public bool CanShow(Thing t)
        {
            return !frozen && GetStoreSettings().AllowedToAccept(t);
        }

        /// <summary>落格物品是否可吸收进全局层：noDeposit（禁止存入）开启时恒 false（物品留格子由玩家处置，
        /// 与"允许存入"开关语义一致），否则取决于 CanShow（冻结/filter）。</summary>
        private bool CanAbsorb(Thing t)
        {
            return !noDeposit && CanShow(t)
                && !OuterrealmVaultUtil.IsProtectedFromAutomaticDeposit(t);
        }

        // IHaulEnroute：无限容量（§1.2）。注意该值不做 filter 检查——filter 门控在存储选择阶段 Accepts。
        /// <summary>new 隐藏基类 SpaceRemainingFor（按落格堆数计算）：vault 内容在全局层，格上驻留物仅为
        /// 短暂预留副本，容量视为无限（§1.2）。</summary>
        public new int SpaceRemainingFor(ThingDef def)
        {
            return int.MaxValue;
        }

        // ── IThingHolder / ISearchableContents ─────────────────────────────────

        public ThingOwner GetDirectlyHeldThings()
        {
            return view;
        }

        /// <summary>供交易/信标汇报的可售持有物（视图副本）。仿 Building_OutfitStand.HeldItems 的汇报语义。</summary>
        public IEnumerable<Thing> HeldItemsForTrade
        {
            get
            {
                if (view == null)
                {
                    yield break;
                }
                List<Thing> copies = view.InnerListForReading;
                for (int i = 0; i < copies.Count; i++)
                {
                    yield return copies[i];
                }
            }
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, view);
        }

        public ThingOwner SearchableContents => view;

        // ── IThingHolderEvents<Thing>：视图加入/移除钩子 ───────────────────────

        public void Notify_ItemAdded(Thing item)
        {
            if (!Spawned)
            {
                return;
            }
            MapHeld.listerHaulables.Notify_AddedThing(item); // 锁定条目经 #6 短路不加（O(1)）
        }

        public void Notify_ItemRemoved(Thing item)
        {
            if (!Spawned)
            {
                return;
            }
            // 查询视图的移除事件只维护地图索引，不代表消费权威库存。
            if (!item.Spawned)
            {
                MapHeld.listerHaulables.Notify_DeSpawned(item);
            }
        }

        // ── 出入模式 setter（§4.1c；gizmo UI 于 P2） ───────────────────────────

        public void SetNoDeposit(bool value)
        {
            if (noDeposit == value)
            {
                return;
            }
            noDeposit = value;
            if (Spawned)
            {
                MapHeld.haulDestinationManager.Notify_HaulDestinationChangedPriority(); // 目标选择重排序
            }
        }

        public void SetNoWithdraw(bool value)
        {
            if (noWithdraw == value)
            {
                return;
            }
            noWithdraw = value;
            if (Spawned)
            {
                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
            }
            GameComponent_OuterrealmStorage.Instance?.NotifyIdentityRoutingChanged(this);
        }

        public void SetAllowTakeForUse(bool value)
        {
            allowTakeForUse = value;
            GameComponent_OuterrealmStorage.Instance?.NotifyIdentityRoutingChanged(this);
        }

        /// <summary>冻结开关：隐藏全部物品并暂停建筑工作（filter 保持不变）。冻结时视图清空、副本从 listerThings 移除。
        /// §filter 视图过滤简化：冻结 = CanShow 恒 false → 同步移除全部可见副本（O(视图副本数)）；
        /// 解冻 = 同步无操作，置脏由后续 Tick 微批按 filter 重新物化。</summary>
        public void SetFrozen(bool value)
        {
            if (frozen == value)
            {
                return;
            }
            frozen = value;
            if (Spawned)
            {
                view?.RemoveDisallowedCopies(); // 冻结时移除全部可见副本；解冻时无副本可移除（O(1)）
                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
                MapHeld.haulDestinationManager.Notify_HaulDestinationChangedPriority();
                view?.MarkMaterializeDirty(); // 解冻时由后续 Tick 微批重新物化；冻结时置脏无害
            }
            GameComponent_OuterrealmStorage.Instance?.NotifyIdentityRoutingChanged(this);
        }

        // ── IStorageGroupMember（§4.1e，逐行对齐 Building_Storage） ────────────

        /// <summary>存储组读写。仅保留 Group 的隐式实现（含 §4.1e 写回）：
        /// 继承 Building_Storage 后原版"member is Building_Storage"取消链接特判已覆盖本建筑，
        /// 原版写回与本 setter 写回重复执行亦幂等；其余 IStorageGroupMember 成员继承基类显式实现。</summary>
        public StorageGroup Group
        {
            get => storageGroup;
            set
            {
                if (value == storageGroup)
                {
                    return;
                }
                if (value == null && storageGroup != null)
                {
                    // §4.1e 写回：覆盖组 gizmo 取消链接路径的类型特判缺口——
                    // StorageGroupUtility.StorageGroupMemberGizmos 的取消链接只对 Building_Storage
                    // 写回组设置（member is Building_Storage），本建筑须在 setter 置 null 时自行
                    // CopyFrom 保留最新组设置（与 SetStorageGroup 内建写回重复执行亦幂等）。
                    settings.CopyFrom(storageGroup.GetStoreSettings());
                }
                storageGroup = value;
            }
        }

        // Map / StoreSettings / ParentStoreSettings / ThingStoreSettings / StorageGroupTag /
        // DrawConnectionOverlay / DrawStorageTab / ShowRenameButton / DrawExtraSelectionOverlays
        // 均与基类 Building_Storage 一致，直接继承。

        // ── UI ─────────────────────────────────────────────────────────────────

        public override string GetInspectString()
        {
            // base = Building_Storage.GetInspectString（含存储组信息与落格驻留物摘要，
            // 落格物仅短暂驻留 15 tick，平时为空）
            string s = base.GetInspectString();
            if (!s.NullOrEmpty())
            {
                s += "\n";
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            int ver = gs != null ? gs.Version : -1;
            if (cachedInspectVersion != ver)
            {
                cachedInspectVersion = ver;
                if (gs == null || gs.EntriesForReading.Count == 0)
                {
                    cachedInspectString = "OuterrealmVault_InspectEmpty".Translate();
                }
                else
                {
                    gs.GetSummary(out int entryCount, out long totalCount);
                    cachedInspectString = "OuterrealmVault_InspectSummary".Translate(entryCount, totalCount);
                }
            }
            return s + cachedInspectString;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            // base = Building_Storage.GetGizmos（含 CopyPaste 存储设置 / 存储组 gizmos / 选择存储物品），
            // vault 不再重复添加；下面只追加 vault 专属开关。
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            // 出入三开关（§4.1c/d）：Command_Toggle + 相同 groupKey 常量 → 原版多选同步调整。
            // "允许存入/允许取出"为正向语义（开关 on = 允许，默认允许）；"允许拿取"为条件开关。
            // 图标暂用原版 TexCommand 占位（P2 可替换为专用贴图，[StaticConstructorOnStartup] 预加载）。
            if (Faction == Faction.OfPlayer)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultAllowDeposit".Translate(),
                    defaultDesc = "VaultAllowDepositDesc".Translate(),
                    icon = OuterrealmStorageTex.VaultAllowDepositIcon,
                    groupKey = VaultGizmoKeys.AllowDeposit,
                    isActive = () => !noDeposit,
                    toggleAction = () => SetNoDeposit(!noDeposit),
                };
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultAllowWithdraw".Translate(),
                    defaultDesc = "VaultAllowWithdrawDesc".Translate(),
                    icon = OuterrealmStorageTex.VaultAllowWithdrawIcon,
                    groupKey = VaultGizmoKeys.AllowWithdraw,
                    isActive = () => !noWithdraw,
                    toggleAction = () => SetNoWithdraw(!noWithdraw),
                };
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultAllowTakeForUse".Translate(),
                    defaultDesc = "VaultAllowTakeForUseDesc".Translate(),
                    icon = OuterrealmStorageTex.VaultAllowTakeForUseIcon,
                    groupKey = VaultGizmoKeys.AllowTakeForUse,
                    isActive = () => allowTakeForUse,
                    toggleAction = () => SetAllowTakeForUse(!allowTakeForUse),
                    Disabled = !noWithdraw, // 条件开关：允许取出开启时无意义置灰；禁止取出（允许取出 off）时才生效
                    disabledReason = "VaultAllowTakeForUseDisabledReason".Translate(),
                };
                // 冻结开关：隐藏全部物品并暂停建筑工作，但保持 filter 不变
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultFrozen".Translate(),
                    defaultDesc = "VaultFrozenDesc".Translate(),
                    icon = OuterrealmStorageTex.VaultFrozenIcon,
                    groupKey = VaultGizmoKeys.Frozen,
                    isActive = () => frozen,
                    toggleAction = () => SetFrozen(!frozen),
                };
                // 打开全局存储管理器（§6.4：无视 filter 的内容总览与死锁逃生口；含全部弹出/取出功能）
                yield return new Command_Action
                {
                    defaultLabel = "OuterrealmStorageManager_Open".Translate(),
                    defaultDesc = "OuterrealmStorageManager_OpenDesc".Translate(),
                    icon = OuterrealmStorageTex.StorageManagerOpenIcon,
                    action = () => Find.WindowStack.Add(new Dialog_OuterrealmStorageManager()),
                };
                // 超维存储访问能力授权（§v3）：双栏授权界面
                yield return new Command_Action
                {
                    defaultLabel = "SubspaceAccessManagerOpen".Translate(),
                    defaultDesc = "SubspaceAccessManagerOpenDesc".Translate(),
                    icon = OuterrealmStorageTex.SubspaceAccessManagerOpenIcon,
                    action = () => Find.WindowStack.Add(new Dialog_SubspaceAccessManager()),
                };
                // 右键菜单模式切换（§4 自制大列表）：原版 ↔ 大列表，每建筑独立、随存档保存；
                // label 每帧随 GetGizmos 重建，显示本建筑当前模式；Toggle 高亮 = 本建筑大列表模式。
                RightClickMenuMode curMode = rightClickMenuMode;
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultRightClickMenuModeLabel".Translate(
                        curMode == RightClickMenuMode.CustomList
                            ? "VaultRightClickMenuModeCustom".Translate()
                            : "VaultRightClickMenuModeVanilla".Translate()),
                    defaultDesc = "VaultRightClickMenuModeDesc".Translate(),
                    icon = curMode == RightClickMenuMode.CustomList
                        ? OuterrealmStorageTex.VaultRightClickMenuModeIcon_List
                        : OuterrealmStorageTex.VaultRightClickMenuModeIcon_Menu,
                    groupKey = VaultGizmoKeys.RightClickMenuMode,
                    isActive = () => rightClickMenuMode == RightClickMenuMode.CustomList,
                    toggleAction = ToggleRightClickMenuMode,
                };
                // 自定义菜单样式：保留旧版完整操作大列表，同时提供按物品惰性生成操作的两级模式。
                VaultCustomMenuMode curCustomMode = customMenuMode;
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultCustomMenuModeLabel".Translate(
                        curCustomMode == VaultCustomMenuMode.ItemThenOption
                            ? "VaultCustomMenuModeItemThenOption".Translate()
                            : "VaultCustomMenuModeFullOptionList".Translate()),
                    defaultDesc = "VaultCustomMenuModeDesc".Translate(),
                    icon = curCustomMode == VaultCustomMenuMode.ItemThenOption
                        ? OuterrealmStorageTex.VaultRightClickMenuModeIcon_Menu
                        : OuterrealmStorageTex.VaultRightClickMenuModeIcon_List,
                    groupKey = VaultGizmoKeys.CustomMenuMode,
                    isActive = () => customMenuMode == VaultCustomMenuMode.ItemThenOption,
                    toggleAction = ToggleCustomMenuMode,
                    Disabled = rightClickMenuMode != RightClickMenuMode.CustomList,
                    disabledReason = "VaultCustomMenuModeDisabledReason".Translate(),
                };
            }
        }

        /// <summary>切换本建筑的右键菜单显示形态（原版 ↔ 自制大列表）；每建筑独立，随存档保存。</summary>
        private void ToggleRightClickMenuMode()
        {
            rightClickMenuMode = rightClickMenuMode == RightClickMenuMode.CustomList
                ? RightClickMenuMode.Vanilla
                : RightClickMenuMode.CustomList;
            Log.Message("[FAOC] 存储仓右键菜单模式切换为: " + rightClickMenuMode);
        }

        /// <summary>切换本建筑自定义菜单的内部样式；仅在自定义右键菜单启用时由 gizmo 调用。</summary>
        private void ToggleCustomMenuMode()
        {
            customMenuMode = customMenuMode == VaultCustomMenuMode.ItemThenOption
                ? VaultCustomMenuMode.FullOptionList
                : VaultCustomMenuMode.ItemThenOption;
            Log.Message("[FAOC] 存储仓自定义右键菜单样式切换为: " + customMenuMode);
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
            {
                yield return o;
            }
            // §6.3："HaulFromSource" 右键浮菜单不会自动出现，必须主动调用（参照 Bookcase.cs:310 / OutfitStand.cs:538）
            foreach (FloatMenuOption o in HaulSourceUtility.GetFloatMenuOptions(this, selPawn))
            {
                yield return o;
            }
            // §4：穿戴/装备/食用等浮菜单选项交由原版 FloatMenuOptionProvider 统一生成。
            // ThingDef 已设 containedItemsSelectable=true（OuterrealmStorage.xml §v5）→
            // ContainingSelectionUtility.SelectableContainedThings 会把视图副本纳入右键 ClickedThings，
            // 所有 provider（含第三方 mod 新增的操作）自动适配，不再逐项手动生成选项；
            // 副本进入左键选中候选的副作用由 Patch_ThingSelectionUtility_SelectableByMapClick
            // 与 Patch_Selector_* 过滤（右键路径 ForThing() 不设 mustBeSelectable，不受影响）。
        }

        // ── 存档：只存自身状态，不存视图（§4：全局层已深存真相） ───────────────

        public override void ExposeData()
        {
            base.ExposeData(); // Building_Storage：保存 settings / storageGroup / label（节点名不变，旧档兼容）
            Scribe_Values.Look(ref noDeposit, "noDeposit", false);
            Scribe_Values.Look(ref noWithdraw, "noWithdraw", false);
            Scribe_Values.Look(ref allowTakeForUse, "allowTakeForUse", false);
            Scribe_Values.Look(ref frozen, "frozen", false);
            // 每建筑右键菜单形态（默认原版；旧档无此节点自动取默认值，兼容）
            Scribe_Values.Look(ref rightClickMenuMode, "rightClickMenuMode", RightClickMenuMode.Vanilla);
            // 旧档没有该字段时继续使用旧版完整操作列表，行为不变。
            Scribe_Values.Look(ref customMenuMode, "customMenuMode", VaultCustomMenuMode.FullOptionList);
        }
    }

    /// <summary>出入开关的多选合并 groupKey 常量（§4.1d：相同开关在所有建筑实例上必须使用相同 label/icon/groupKey 才能合并同步）。</summary>
    internal static class VaultGizmoKeys
    {
        public const int AllowDeposit = 714201;
        public const int AllowWithdraw = 714202;
        public const int AllowTakeForUse = 714203;
        public const int Frozen = 714204;
        public const int RightClickMenuMode = 714205;
        public const int CustomMenuMode = 714206;
    }
}
