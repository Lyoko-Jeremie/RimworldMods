using System.Collections.Generic;
using RimWorld;
using Verse;

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
        IStorageGroupMember
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
            set => storageGroup = value;
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
            // 图标暂用原版 TexCommand 占位（P2 可替换为专用贴图，[StaticConstructorOnStartup] 预加载）。
            if (Faction == Faction.OfPlayer)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultNoDeposit".Translate(),
                    defaultDesc = "VaultNoDepositDesc".Translate(),
                    icon = TexCommand.ForbidOn,
                    groupKey = VaultGizmoKeys.NoDeposit,
                    isActive = () => noDeposit,
                    toggleAction = () => SetNoDeposit(!noDeposit),
                };
                yield return new Command_Toggle
                {
                    defaultLabel = "VaultNoWithdraw".Translate(),
                    defaultDesc = "VaultNoWithdrawDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    groupKey = VaultGizmoKeys.NoWithdraw,
                    isActive = () => noWithdraw,
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
                    Disabled = !noWithdraw, // 条件开关：禁止取出未开启时置灰（gizmo 随 UI 刷新重新求值）
                    disabledReason = "VaultAllowTakeForUseDisabledReason".Translate(),
                };
                // 移出全部 / 停止移出全部（§6.3 路线 A：视图放行，搬运工自动搬往其他存储区）
                yield return new Command_Action
                {
                    defaultLabel = (releasedKeys.Count > 0 ? "OuterrealmVault_StopReleaseAll" : "OuterrealmVault_ReleaseAll").Translate(),
                    defaultDesc = "OuterrealmVault_ReleaseAllDesc".Translate(),
                    icon = TexCommand.SelectShelf,
                    action = () =>
                    {
                        if (releasedKeys.Count > 0)
                        {
                            releasedKeys.Clear();
                            if (Spawned)
                            {
                                MapHeld.listerHaulables.Notify_HaulSourceChanged(this);
                            }
                        }
                        else
                        {
                            ReleaseAll();
                        }
                    },
                };
                // 打开全局存储管理器（§6.4：无视 filter 的内容总览与死锁逃生口）
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

    /// <summary>出入三开关的多选合并 groupKey 常量（§4.1d：相同开关在所有建筑实例上必须使用相同 label/icon/groupKey 才能合并同步）。</summary>
    internal static class VaultGizmoKeys
    {
        public const int NoDeposit = 714201;
        public const int NoWithdraw = 714202;
        public const int AllowTakeForUse = 714203;
    }
}
