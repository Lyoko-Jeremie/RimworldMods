using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
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
        IApparelSource
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

        // ── 放行列表（§6.3 移出路线 A）：放行条目保留在视图但 Accepts 返回 false → 可 haulable，
        //    由搬运工搬到其他存储区；条目搬空后自动清除（CleanupReleasedKeys）。存档随建筑序列化。
        private List<OuterrealmEntryKey> releasedKeys = new List<OuterrealmEntryKey>();

        public bool NoDeposit => noDeposit;
        public bool NoWithdraw => noWithdraw;
        public bool AllowTakeForUse => allowTakeForUse;
        public List<OuterrealmEntryKey> ReleasedKeysForReading => releasedKeys;

        // ── InspectString 摘要缓存（§4：InspectPaneFiller 每帧调用，避免每帧拼接几百条目） ──
        private string cachedInspectString;
        private int cachedInspectVersion = -1;

        // ── 设置签名（§3.3：filter 摘要 + 优先级；覆盖 StorageGroup 通知断链） ──
        private string cachedSettingsSignature;

        /// <summary>建筑上次同步的全局版本号（§3.3 懒同步）。</summary>
        private int lastSeenVersion;

        private readonly List<OuterrealmEntryKey> tmpChangeKeys = new List<OuterrealmEntryKey>();

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
                cachedSettingsSignature = BuildSettingsSignature();
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

        // ── 60 tick 视图同步（§3.3 变更驱动） ──────────────────────────────────

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
            if (gs.NeedFullRebuild || HasSettingsSignatureChanged())
            {
                view.RebuildView();
                lastSeenVersion = gs.Version;
                CleanupReleasedKeys();
                return;
            }
            if (gs.Version == lastSeenVersion)
            {
                return;
            }
            // 增量：只处理变更日志中的 key（O(变化量)，与 L1 总量解耦）
            gs.ReadChangeLog(tmpChangeKeys);
            for (int i = 0; i < tmpChangeKeys.Count; i++)
            {
                view.SyncKey(tmpChangeKeys[i]);
            }
            lastSeenVersion = gs.Version;
            CleanupReleasedKeys(); // 条目搬空后移除放行项（§6.3）
        }

        private bool HasSettingsSignatureChanged()
        {
            string sig = BuildSettingsSignature();
            if (sig != cachedSettingsSignature)
            {
                cachedSettingsSignature = sig;
                return true;
            }
            return false;
        }

        private string BuildSettingsSignature()
        {
            StorageSettings s = GetStoreSettings();
            return (s != null && s.filter != null ? s.filter.ToString() : "null") + "|" + (s != null ? (int)s.Priority : -1);
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
            // filter/优先级变化 → 视图按新 filter 重建（§4 表）；60 tick 签名检查同步覆盖组设置变化
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
            // §6.3：放行条目 Accepts 返回 false（视图保留、可 haulable → 搬运工搬走）；
            // filter 门控照常（允许=可存入，禁止=不可见）。
            return GetStoreSettings().AllowedToAccept(t) && !IsReleased(t);
        }

        // ── 放行（§6.3 移出路线 A） ────────────────────────────────────────────

        public bool IsReleased(Thing t)
        {
            return IsReleased(OuterrealmEntryKey.From(t));
        }

        public bool IsReleased(OuterrealmEntryKey key)
        {
            return releasedKeys.Contains(key);
        }

        /// <summary>设置放行状态（§6.3）：放行 = 该条目可被搬运工搬去其他存储；取消 = 恢复锁定。</summary>
        public void SetReleased(OuterrealmEntryKey key, bool released)
        {
            bool had = releasedKeys.Contains(key);
            if (released && !had)
            {
                releasedKeys.Add(key);
            }
            else if (!released && had)
            {
                releasedKeys.Remove(key);
            }
            else
            {
                return;
            }
            // 放行状态变化 → listerHaulables 重算（放行条目进入 haulables / 恢复锁定）
            if (Spawned)
            {
                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
            }
        }

        /// <summary>放行全部视图条目（gizmo"移出全部"）。</summary>
        public void ReleaseAll()
        {
            if (view == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            bool any = false;
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry e = entries[i];
                if (e.Count > 0 && view.FindCopy(e.Key) != null && !releasedKeys.Contains(e.Key))
                {
                    releasedKeys.Add(e.Key);
                    any = true;
                }
            }
            if (any && Spawned)
            {
                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
            }
        }

        /// <summary>条目搬空后自动移除放行项（§6.3：放行状态保留直到清空），防止同 key 重新存入后立即被搬走。</summary>
        private void CleanupReleasedKeys()
        {
            if (releasedKeys.Count == 0)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            for (int i = releasedKeys.Count - 1; i >= 0; i--)
            {
                OuterrealmEntry e = gs.FindEntry(releasedKeys[i]);
                if (e == null || e.Count <= 0)
                {
                    releasedKeys.RemoveAt(i);
                }
            }
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
                    icon = TexCommand.ForbidOn,
                    groupKey = VaultGizmoKeys.AllowDeposit,
                    isActive = () => !noDeposit,
                    toggleAction = () => SetNoDeposit(!noDeposit),
                };
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultAllowWithdraw".Translate(),
                    defaultDesc = "VaultAllowWithdrawDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    groupKey = VaultGizmoKeys.AllowWithdraw,
                    isActive = () => !noWithdraw,
                    toggleAction = () => SetNoWithdraw(!noWithdraw),
                };
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultAllowTakeForUse".Translate(),
                    defaultDesc = "VaultAllowTakeForUseDesc".Translate(),
                    icon = TexCommand.SelectCarriedThing,
                    groupKey = VaultGizmoKeys.AllowTakeForUse,
                    isActive = () => allowTakeForUse,
                    toggleAction = () => SetAllowTakeForUse(!allowTakeForUse),
                    Disabled = !noWithdraw, // 条件开关：允许取出开启时无意义置灰；禁止取出（允许取出 off）时才生效
                    disabledReason = "VaultAllowTakeForUseDisabledReason".Translate(),
                };
                // 打开全局存储管理器（§6.4：无视 filter 的内容总览与死锁逃生口；含全部弹出/取出功能）
                yield return new Command_Action
                {
                    defaultLabel = "OuterrealmStorageManager_Open".Translate(),
                    defaultDesc = "OuterrealmStorageManager_OpenDesc".Translate(),
                    icon = TexCommand.SelectShelf,
                    action = () => Find.WindowStack.Add(new Dialog_OuterrealmStorageManager()),
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
            // §4 仿 OutfitStand（Building_OutfitStand.cs:540-555）：为视图内容生成穿戴/强制穿戴/装备选项
            if (selPawn.IsColonistPlayerControlled && view != null)
            {
                List<Thing> copies = view.InnerListForReading;
                for (int i = 0; i < copies.Count; i++)
                {
                    Thing copy = copies[i];
                    if (copy is Apparel ap)
                    {
                        yield return GetFloatMenuOptionToWear(selPawn, ap);
                        yield return GetFloatMenuOptionForForceWear(selPawn, ap);
                    }
                    if (copy.def.IsWeapon)
                    {
                        yield return GetFloatMenuOptionToEquipWeapon(selPawn, copy);
                    }
                }
            }
            // §6.1：指定 pawn 拿 X 到背包（自定义 job：以建筑为行走目标，取物目标为视图副本）
            if (selPawn.IsColonistPlayerControlled && view != null)
            {
                GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
                if (gs != null)
                {
                    List<Thing> copies = view.InnerListForReading;
                    for (int i = 0; i < copies.Count; i++)
                    {
                        Thing copy = copies[i];
                        if (copy is Corpse)
                        {
                            continue; // 尸体无"取到背包"意义，且 Corpse.LabelNoCount 在 Bugged 状态会 Log.Error
                        }
                        OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                        if (e == null || e.Count <= 0)
                        {
                            continue;
                        }
                        Thing copyForClosure = copy;
                        OuterrealmEntry entryForClosure = e;
                        string copyLabel = OuterrealmVaultUtil.SafeLabelCapNoCount(copy);
                        yield return new FloatMenuOption("OuterrealmVault_TakeToInventory".Translate(copyLabel), () =>
                        {
                            int max = (int)Mathf.Min(entryForClosure.Count, int.MaxValue);
                            if (max <= 0)
                            {
                                return;
                            }
                            string label = copyForClosure.LabelCapNoCount;
                            Find.WindowStack.Add(new Dialog_Slider(
                                (int v) => label + " x" + v.ToString("N0"),
                                1,
                                max,
                                (int v) =>
                                {
                                    if (v <= 0)
                                    {
                                        return;
                                    }
                                    JobDef jobDef = DefDatabase<JobDef>.GetNamedSilentFail("FAOC_VaultTakeToInventory");
                                    if (jobDef == null)
                                    {
                                        return;
                                    }
                                    Job job = JobMaker.MakeJob(jobDef, this, copyForClosure);
                                    job.count = v;
                                    selPawn.jobs.TryTakeOrderedJob(job);
                                }));
                        });
                    }
                }
            }
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
            // 放行列表存档（§4.1b：放行状态随建筑实例序列化）。OuterrealmEntryKey 为 struct，
            // 经 ToString/TryParse 字符串中转；解析失败项跳过（无害）。
            List<string> releasedKeyStrings = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                releasedKeyStrings = new List<string>(releasedKeys.Count);
                for (int i = 0; i < releasedKeys.Count; i++)
                {
                    releasedKeyStrings.Add(releasedKeys[i].ToString());
                }
            }
            Scribe_Collections.Look(ref releasedKeyStrings, "releasedKeys", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && releasedKeyStrings != null)
            {
                releasedKeys.Clear();
                for (int i = 0; i < releasedKeyStrings.Count; i++)
                {
                    OuterrealmEntryKey key;
                    if (OuterrealmEntryKey.TryParse(releasedKeyStrings[i], out key))
                    {
                        releasedKeys.Add(key);
                    }
                }
            }
        }
    }

    /// <summary>出入开关的多选合并 groupKey 常量（§4.1d：相同开关在所有建筑实例上必须使用相同 label/icon/groupKey 才能合并同步）。</summary>
    internal static class VaultGizmoKeys
    {
        public const int AllowDeposit = 714201;
        public const int AllowWithdraw = 714202;
        public const int AllowTakeForUse = 714203;
    }
}
