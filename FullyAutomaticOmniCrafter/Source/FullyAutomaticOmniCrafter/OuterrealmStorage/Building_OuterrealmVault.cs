using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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
        
    }
    
    /// <summary>
    /// 超维存储仓建筑：超维空间的"存储终端"（§4）。
    /// 对外行为 = 1.6 原版容器型存储（Building_OutfitStand / Building_Bookcase 同类）：
    /// 搬运工可存入（HaulToContainer）、可取出（HaulSource / 工作台原料 / 装备取用）、
    /// 有存储清单 UI（ITab_Storage）与优先级；内容始终保存在全局层（GameComponent_OuterrealmStorage），
    /// 本建筑只持有"视图"（OuterrealmVaultViewThingOwner = 全局条目的投影副本缓存）。
    /// </summary>
    public class Building_OuterrealmVault : Building,
        IHaulDestination,
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
        /// <summary>独立存储清单（filter 决定本建筑"能看到/存取哪些条目"，§6.2）。</summary>
        public StorageSettings settings;

        /// <summary>视图容器（§3.2：owner=建筑 是 haulable 判定链命门；构造内已设 dontTickContents）。</summary>
        public OuterrealmVaultViewThingOwner view;

        private StorageGroup storageGroup;

        // ── 出入模式（§4.1c；gizmo UI 于 P2 提供） ──
        private bool noDeposit;    // on = 禁止存入（HaulDestinationEnabled=false）
        private bool noWithdraw;   // on = 禁止取出（HaulSourceEnabled=false）
        private bool allowTakeForUse; // 条件开关：noWithdraw 开启时放宽工作台/食物搜索（§5.2 #7，P4 实现）
        private bool frozen;       // 冻结开关：隐藏全部物品并暂停建筑工作，但保持 filter 不变

        public bool NoDeposit => noDeposit;
        public bool NoWithdraw => noWithdraw;
        public bool AllowTakeForUse => allowTakeForUse;
        public bool Frozen => frozen;

        // ── InspectString 摘要缓存（§4：InspectPaneFiller 每帧调用，避免每帧拼接几百条目） ──
        private string cachedInspectString;
        private int cachedInspectVersion = -1;

        /// <summary>建筑上次同步的全局版本号（§3.3 懒同步，随帧末微批迁移后仅作状态记录）。</summary>
        private int lastSeenVersion;

        // ── 构造 / 生命周期 ────────────────────────────────────────────────────

        public Building_OuterrealmVault()
        {
            view = new OuterrealmVaultViewThingOwner(this);
        }

        public override void PostMake()
        {
            base.PostMake();
            settings = new StorageSettings(this);
            if (def.building.defaultStorageSettings != null)
            {
                settings.CopyFrom(def.building.defaultStorageSettings);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // 跨地图自动断开存储组（原版模式，Building_Storage.cs:141-153）
            if (storageGroup != null && map != storageGroup.Map)
            {
                StorageSettings storeSettings = storageGroup.GetStoreSettings();
                storageGroup.RemoveMember(this);
                storageGroup = null;
                settings.CopyFrom(storeSettings);
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs != null)
            {
                gs.RegisterVault(this);
                view.RebuildView(); // 初始化/重连：全量重建（§3.3）
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
                view.ClearView(); // 内容保留在全局层（§4.1b 断开访问，不落地）
            }
            if (storageGroup != null)
            {
                storageGroup.RemoveMember(this);
                storageGroup = null;
            }
            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            base.Destroy(mode);
            if (storageGroup == null)
            {
                return;
            }
            storageGroup.RemoveMember(this);
            storageGroup = null;
        }

        public override void Notify_MinifiedThingAboutToBeDestroyed(DestroyMode mode)
        {
            // 防御性钩子（仿 Building_OutfitStand）：本设计内容保留全局层，无需落地；
            // 仅借用此路径确保异常情况下的内部状态被清理。
            base.Notify_MinifiedThingAboutToBeDestroyed(mode);
        }

        // ── 视图同步（§3.3 实时同步方案 B：内容由全局层帧末微批统一驱动） ──

        protected override void Tick()
        {
            base.Tick();
            if (!this.IsHashIntervalTick(60))
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || view == null)
            {
                return;
            }
            // §3.3 事件驱动方案：视图内容同步由 GameComponent_OuterrealmStorage 的帧末微批统一驱动，
            // 设置变化（含 StorageGroup 组设置，经 Patch_StorageGroup_Notify_SettingsChanged 补链）由
            // Notify_SettingsChanged 事件即时触发 RebuildView——无需轮询。此处仅保留变更日志溢出的
            // 全量重建兜底。
            if (gs.NeedFullRebuild)
            {
                view.RebuildView();
                lastSeenVersion = gs.Version;
            }
        }

        // ── IStoreSettingsParent / IHaulDestination / IHaulSource ───────────────

        public bool StorageTabVisible => true;

        public StorageSettings GetStoreSettings()
        {
            return storageGroup != null ? storageGroup.GetStoreSettings() : settings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            return def.building.fixedStorageSettings ?? StorageSettings.EverStorableFixedSettings();
        }

        public void Notify_SettingsChanged()
        {
            if (!Spawned)
            {
                return;
            }
            MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
            MapHeld.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            // filter/优先级变化 → 视图按新 filter 重建（§4 表）；组设置变化经
            // Patch_StorageGroup_Notify_SettingsChanged 补链后同样到达此处（事件驱动，无需轮询）
            view?.RebuildView();
            lastSeenVersion = GameComponent_OuterrealmStorage.Instance != null ? GameComponent_OuterrealmStorage.Instance.Version : lastSeenVersion;
        }

        public bool HaulDestinationEnabled => Spawned && !noDeposit;

        public bool HaulSourceEnabled => Spawned && !noWithdraw;

        // IApparelSource（§装备优化兼容）：让 JobDriver_Wear/JobGiver_OptimizeApparel 走"从衣柜取"路径
        // ——否则视图里的衣物副本被判为普通候选，穿戴流程对未 Spawned 物品必然失败，
        // 形成"Wear job 立即失败 → 重复生成"循环（Mia started 10 jobs in one tick 报错）。
        // ApparelSourceEnabled 与"允许取出"联动：禁止取出时 OptimizeApparel 跳过本建筑衣物。
        public bool ApparelSourceEnabled => !noWithdraw;

        /// <summary>从视图移除衣物（穿戴即取出：经 Notify_ItemRemoved 扣全局并补回，§3.3）。</summary>
        public bool RemoveApparel(Apparel apparel)
        {
            return view != null && view.Contains(apparel) && view.Remove(apparel);
        }

        public bool Accepts(Thing t)
        {
            // §6.4：弹出防回吸——刚弹出的物品限时内不被本建筑自动吸回。
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs != null && gs.IsEjected(t))
            {
                return false;
            }
            return CanShow(t);
        }

        /// <summary>是否向该建筑展示/物化某物品：冻结时恒 false；否则取决于 filter。保持 filter 不变。</summary>
        public bool CanShow(Thing t)
        {
            return !frozen && GetStoreSettings().AllowedToAccept(t);
        }

        // IHaulEnroute：无限容量（§1.2）。注意该值不做 filter 检查——filter 门控在存储选择阶段 Accepts。
        public int SpaceRemainingFor(ThingDef def)
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
            if (view.SuppressRemovalSync)
            {
                return; // 视图重建/注销期间（§3.3）
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs != null)
            {
                // 整堆移除（SplitOff 整堆 / Take / TryDrop 整堆 / Destroy）：按副本当前量扣减全局
                OuterrealmEntryKey key = OuterrealmEntryKey.From(item);
                gs.Subtract(key, item.stackCount);
                OuterrealmEntry e = gs.FindEntry(key);
                if (e != null && e.Count > 0)
                {
                    view.EnsureCopyFor(key); // 即时补回新副本（§3.3）
                }
            }
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
        }

        public void SetAllowTakeForUse(bool value)
        {
            allowTakeForUse = value;
        }

        /// <summary>冻结开关：隐藏全部物品并暂停建筑工作（filter 保持不变）。冻结时视图清空、副本从 listerThings 移除。</summary>
        public void SetFrozen(bool value)
        {
            if (frozen == value)
            {
                return;
            }
            frozen = value;
            if (Spawned)
            {
                view?.RebuildView(); // 冻结：CanShow 恒 false → 全部副本被移除；解冻：按 filter 重新物化
                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
                MapHeld.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            }
        }

        // ── IStorageGroupMember（§4.1e，逐行对齐 Building_Storage） ────────────

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

        Map IStorageGroupMember.Map => MapHeld;

        public StorageSettings StoreSettings => GetStoreSettings();

        public StorageSettings ParentStoreSettings => GetParentStoreSettings();

        public StorageSettings ThingStoreSettings => settings;

        public string StorageGroupTag => def.building.storageGroupTag;

        public bool DrawConnectionOverlay => Spawned;

        public bool DrawStorageTab => true;

        public bool ShowRenameButton => Faction == Faction.OfPlayer;

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            StorageGroupUtility.DrawSelectionOverlaysFor(this);
        }

        // ── UI ─────────────────────────────────────────────────────────────────

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (!s.NullOrEmpty())
            {
                s += "\n";
            }
            // 存储组信息（§4.1e，对齐 Building_Storage.GetInspectString）
            if (storageGroup != null)
            {
                s += "StorageGroupLabel".Translate() + ": " + storageGroup.RenamableLabel.CapitalizeFirst() + " ";
                s += storageGroup.MemberCount > 1
                    ? "(" + "NumBuildings".Translate(storageGroup.MemberCount) + ")"
                    : "(" + "OneBuilding".Translate() + ")";
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
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            foreach (Gizmo g in StorageSettingsClipboard.CopyPasteGizmosFor(GetStoreSettings()))
            {
                yield return g;
            }
            if (StorageTabVisible && MapHeld != null)
            {
                foreach (Gizmo g in StorageGroupUtility.StorageGroupMemberGizmos(this))
                {
                    yield return g;
                }
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
            }
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
            // ThingDef 已设 containedItemsSelectable=true → ContainingSelectionUtility.SelectableContainedThings
            // 会把视图副本纳入右键 ClickedThings，所有 provider（含第三方 mod 新增的操作）自动适配，
            // 不再逐项手动生成选项。
        }

        // ── 穿戴/装备浮菜单选项（§4，逐行照抄 Building_OutfitStand.cs:554-704 原版逻辑） ──

        /// <summary>
        /// 仿原版 FloatMenuUtility.DecoratePrioritizedTask 的装饰，但拆分两个 target：
        /// ReservedBy 检查针对"副本"（衣物/武器自身预留状态，语义准确，避免建筑被搬运预留时的误报）；
        /// revalidateClickTarget 用"建筑"——原版若传副本（未 Spawned），FloatMenuMap.StillValid
        /// （!revalidateClickTarget.Spawned → option.Disabled=true）会把所有穿戴/装备选项置灰。
        /// </summary>
        private FloatMenuOption DecorateVaultOption(FloatMenuOption option, Pawn selPawn, Thing apparelOrWeapon)
        {
            if (option.action == null)
            {
                return option;
            }
            if (selPawn != null && !selPawn.CanReserve(apparelOrWeapon) && selPawn.CanReserve(apparelOrWeapon, ignoreOtherReservations: true))
            {
                Pawn reserver = selPawn.Map.reservationManager.FirstRespectedReserver(apparelOrWeapon, selPawn)
                    ?? selPawn.Map.physicalInteractionReservationManager.FirstReserverOf(apparelOrWeapon);
                if (reserver != null)
                {
                    option.Label = option.Label + ": " + "ReservedBy".Translate(reserver.LabelShort, reserver);
                }
            }
            option.revalidateClickTarget = this; // 建筑（Spawned），FloatMenu 有效性校验通过
            return option;
        }

        private FloatMenuOption GetFloatMenuOptionToWear(Pawn selPawn, Apparel apparel)
        {
            string key1 = "CannotWear";
            string key2 = "ForceWear";
            if (apparel.def.apparel.LastLayer.IsUtilityLayer)
            {
                key1 = "CannotEquipApparel";
                key2 = "ForceEquipApparel";
            }
            if (!selPawn.CanReach((LocalTargetInfo)(Thing)apparel, PathEndMode.ClosestTouch, Danger.Deadly))
                return new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "NoPath".Translate().CapitalizeFirst()), (Action)null, (Thing)apparel, Color.white);
            if (apparel.IsBurning())
                return new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "Burning".Translate()), (Action)null, (Thing)apparel, Color.white);
            if (selPawn.apparel.WouldReplaceLockedApparel(apparel))
                return new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "WouldReplaceLockedApparel".Translate().CapitalizeFirst()), (Action)null, (Thing)apparel, Color.white);
            if (selPawn.IsMutant && selPawn.mutant.Def.disableApparel)
                return new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + selPawn.mutant.Def.LabelCap), (Action)null, (Thing)apparel, Color.white);
            if (!ApparelUtility.HasPartsToWear(selPawn, apparel.def))
                return new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "CannotWearBecauseOfMissingBodyParts".Translate().CapitalizeFirst()), (Action)null, (Thing)apparel, Color.white);
            string cantReason;
            return !EquipmentUtility.CanEquip((Thing)apparel, selPawn, out cantReason) ? new FloatMenuOption((string)(key1.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + cantReason), (Action)null, (Thing)apparel, Color.white) : DecorateVaultOption(new FloatMenuOption((string)key2.Translate((NamedArgument)apparel.LabelShort, (NamedArgument)(Thing)apparel), (Action)(() =>
            {
                Action confirmAct = (Action)(() => selPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wear, (LocalTargetInfo)(Thing)apparel)));
                Apparel replacedByNewApparel = ApparelUtility.GetApparelReplacedByNewApparel(selPawn, apparel);
                if (replacedByNewApparel != null && ModsConfig.BiotechActive && MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing(selPawn, (Thing)replacedByNewApparel, confirmAct))
                    return;
                confirmAct();
            }), (Thing)apparel, Color.white), selPawn, apparel);
        }

        private FloatMenuOption GetFloatMenuOptionForForceWear(Pawn selPawn, Apparel apparel)
        {
            string cannotForceTargetText = "CannotForceTargetToWear";
            string key = "ForceTargetToWear";
            if (apparel.def.apparel.LastLayer.IsUtilityLayer)
            {
                cannotForceTargetText = "CannotForceTargetToEquipApparel";
                key = "ForceTargetToEquipApparel";
            }
            if (!selPawn.CanReach((LocalTargetInfo)(Thing)apparel, PathEndMode.ClosestTouch, Danger.Deadly))
                return new FloatMenuOption((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "NoPath".Translate().CapitalizeFirst()), (Action)null, (Thing)apparel, Color.white);
            return apparel.IsBurning() ? new FloatMenuOption((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "Burning".Translate()), (Action)null, (Thing)apparel, Color.white) : DecorateVaultOption(new FloatMenuOption((string)key.Translate((NamedArgument)apparel.LabelShort, (NamedArgument)(Thing)apparel), (Action)(() =>
            {
                bool queueOrder = KeyBindingDefOf.QueueOrder.IsDownEvent;
                Find.Targeter.BeginTargeting(TargetingParameters.ForForceWear(selPawn), (Action<LocalTargetInfo>)(target =>
                {
                    Pawn targetPawn;
                    if (!target.TryGetPawn(out targetPawn))
                    {
                        if (!ModsConfig.OdysseyActive || !(target.Thing is Building_OutfitStand thing2))
                            return;
                        if (!thing2.CanEverStoreThing((Thing)apparel))
                        {
                            Messages.Message((string)"CannotStoreThingOnTarget".Translate(apparel.Named("THING"), thing2.Named("TARGET")), MessageTypeDefOf.RejectInput, false);
                        }
                        else
                        {
                            Pawn_JobTracker jobs = selPawn.jobs;
                            Job job = JobMaker.MakeJob(JobDefOf.PutApparelOnOutfitStand, (LocalTargetInfo)(Thing)apparel, (LocalTargetInfo)(Thing)thing2);
                            bool flag = queueOrder;
                            JobTag? tag = new JobTag?(JobTag.Misc);
                            int num = flag ? 1 : 0;
                            jobs.TryTakeOrderedJob(job, tag, num != 0);
                        }
                    }
                    else if (targetPawn.apparel.WouldReplaceLockedApparel(apparel))
                        Messages.Message((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "WouldReplaceLockedApparel".Translate().CapitalizeFirst()), (LookTargets)(Thing)targetPawn, MessageTypeDefOf.RejectInput, false);
                    else if (targetPawn.IsMutant && targetPawn.mutant.Def.disableApparel)
                        Messages.Message((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + targetPawn.mutant.Def.LabelCap), (LookTargets)(Thing)targetPawn, MessageTypeDefOf.RejectInput, false);
                    else if (!ApparelUtility.HasPartsToWear(targetPawn, apparel.def))
                    {
                        Messages.Message((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + "CannotWearBecauseOfMissingBodyParts".Translate().CapitalizeFirst()), (LookTargets)(Thing)targetPawn, MessageTypeDefOf.RejectInput, false);
                    }
                    else
                    {
                        string cantReason;
                        if (!EquipmentUtility.CanEquip((Thing)apparel, targetPawn, out cantReason))
                        {
                            Messages.Message((string)(cannotForceTargetText.Translate((NamedArgument)apparel.Label, (NamedArgument)(Thing)apparel) + ": " + cantReason), (LookTargets)(Thing)targetPawn, MessageTypeDefOf.RejectInput, false);
                        }
                        else
                        {
                            Action confirmAct = (Action)(() =>
                            {
                                Pawn_JobTracker jobs = selPawn.jobs;
                                Job job = JobMaker.MakeJob(JobDefOf.ForceTargetWear, (LocalTargetInfo)(Thing)targetPawn, (LocalTargetInfo)(Thing)apparel);
                                bool flag = queueOrder;
                                JobTag? tag = new JobTag?(JobTag.Misc);
                                int num = flag ? 1 : 0;
                                jobs.TryTakeOrderedJob(job, tag, num != 0);
                            });
                            Apparel replacedByNewApparel = ApparelUtility.GetApparelReplacedByNewApparel(targetPawn, apparel);
                            if (replacedByNewApparel != null && ModsConfig.BiotechActive && MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing(targetPawn, (Thing)replacedByNewApparel, confirmAct))
                                return;
                            confirmAct();
                        }
                    }
                }));
            }), (Thing)apparel, Color.white), selPawn, apparel);
        }

        private FloatMenuOption GetFloatMenuOptionToEquipWeapon(Pawn selPawn, Thing weapon)
        {
            if (!weapon.HasComp<CompEquippable>())
                return (FloatMenuOption)null;
            string labelShort = weapon.LabelShort;
            if (weapon.def.IsWeapon && selPawn.WorkTagIsDisabled(WorkTags.Violent))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "IsIncapableOfViolenceLower".Translate((NamedArgument)selPawn.LabelShort, (NamedArgument)(Thing)selPawn)), (Action)null, weapon, Color.white);
            if (weapon.def.IsRangedWeapon && selPawn.WorkTagIsDisabled(WorkTags.Shooting))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "IsIncapableOfShootingLower".Translate((NamedArgument)(Thing)selPawn)), (Action)null, weapon, Color.white);
            if (!selPawn.CanReach((LocalTargetInfo)weapon, PathEndMode.ClosestTouch, Danger.Deadly))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "NoPath".Translate().CapitalizeFirst()), (Action)null, weapon, Color.white);
            if (!selPawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "Incapable".Translate().CapitalizeFirst()), (Action)null, weapon, Color.white);
            if (weapon.IsBurning())
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "BurningLower".Translate()), (Action)null, weapon, Color.white);
            if (selPawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(weapon, selPawn))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + "QuestRelated".Translate().CapitalizeFirst()), (Action)null, weapon, Color.white);
            string cantReason;
            if (!EquipmentUtility.CanEquip(weapon, selPawn, out cantReason, false))
                return new FloatMenuOption((string)("CannotEquip".Translate((NamedArgument)labelShort) + ": " + cantReason.CapitalizeFirst()), (Action)null, weapon, Color.white);
            string label1 = (string)"Equip".Translate((NamedArgument)labelShort);
            if (weapon.def.IsRangedWeapon && selPawn.story != null && selPawn.story.traits.HasTrait(TraitDefOf.Brawler))
                label1 = (string)(label1 + (" " + "EquipWarningBrawler".Translate()));
            if (!EquipmentUtility.AlreadyBondedToWeapon(weapon, selPawn))
                return DecorateVaultOption(new FloatMenuOption(label1, (Action)(() =>
                {
                    string confirmationText = EquipmentUtility.GetPersonaWeaponConfirmationText(weapon, selPawn);
                    if (!confirmationText.NullOrEmpty())
                        Find.WindowStack.Add((Window)new Dialog_MessageBox((TaggedString)confirmationText, (string)"Yes".Translate(), (Action)(() => Equip()), (string)"No".Translate()));
                    else
                        Equip();
                }), weapon, Color.white), selPawn, weapon);
            string label2 = (string)(label1 + (" " + "BladelinkAlreadyBonded".Translate()));
            TaggedString dialogText = "BladelinkAlreadyBondedDialog".Translate(selPawn.Named("PAWN"), weapon.Named("WEAPON"), selPawn.equipment.bondedWeapon.Named("BONDEDWEAPON"));
            return DecorateVaultOption(new FloatMenuOption(label2, (Action)(() => Find.WindowStack.Add((Window)new Dialog_MessageBox(dialogText))), weapon, Color.white, MenuOptionPriority.High), selPawn, weapon);

            void Equip()
            {
                weapon.SetForbidden(false);
                selPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Equip, (LocalTargetInfo)weapon));
                FleckMaker.Static(weapon.PositionHeld, weapon.MapHeld, FleckDefOf.FeedbackEquip);
                PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.EquippingWeapons, KnowledgeAmount.Total);
            }
        }

        // ── 存档：只存自身状态，不存视图（§4：全局层已深存真相） ───────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref settings, "settings", this);
            Scribe_References.Look(ref storageGroup, "storageGroup");
            Scribe_Values.Look(ref noDeposit, "noDeposit", false);
            Scribe_Values.Look(ref noWithdraw, "noWithdraw", false);
            Scribe_Values.Look(ref allowTakeForUse, "allowTakeForUse", false);
            Scribe_Values.Look(ref frozen, "frozen", false);
        }
    }

    /// <summary>出入开关的多选合并 groupKey 常量（§4.1d：相同开关在所有建筑实例上必须使用相同 label/icon/groupKey 才能合并同步）。</summary>
    internal static class VaultGizmoKeys
    {
        public const int AllowDeposit = 714201;
        public const int AllowWithdraw = 714202;
        public const int AllowTakeForUse = 714203;
        public const int Frozen = 714204;
    }
}
