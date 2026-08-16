using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 建筑视图容器：全局条目的"投影副本"缓存（§3.2）。
    /// 视图条目 = 全局条目的独立"副本实例"（同一条目在多个建筑视图各物化一份 Thing），
    /// 副本的 stackCount 恒等于 min(全局剩余, stackLimit)（SplitOff postfix / 视图同步维护）。
    ///
    /// 构造强制项（§3.2/§1.6）：
    ///  1. owner = 建筑：未 Spawned 条目经 Thing.ParentHolder = holdingOwner.Owner 解析到建筑，
    ///     是 haulable 判定 / MapHeld / 温度链的前提——漏传则锁定与放行静默失效。
    ///  2. dontTickContents = true：否则持有者被 tick 时内容物被递归 tick，冻结失效。
    ///
    /// 吸收路径（TryAdd）是存入的唯一入口（§3.2 单一入口）：不把物品放入视图列表，
    /// 而是并入全局层（GameComponent_OuterrealmStorage.Deposit），随后刷新本建筑视图副本。
    /// </summary>
    public class OuterrealmVaultViewThingOwner : ThingOwner<Thing>
    {
        public readonly Building_OuterrealmVault Vault;

        /// <summary>视图重建/注销期间抑制 Notify_ItemRemoved 的全局同步（§3.3）。</summary>
        public bool SuppressRemovalSync;

        /// <summary>SplitOff prefix 记录的校正前 stackCount（单线程主线程安全；无嵌套 SplitOff 场景）。</summary>
        private int lastSplitOffStackCount;

        /// <summary>TryAbsorbStack prefix 记录的吸收量（回滚补偿；§3.3）。</summary>
        public int LastAbsorbAmount;

        /// <summary>副本查找索引（§3.3 实时同步方案 E）：key → 副本，FindCopy 由线性扫描降为 O(1)。
        /// 不序列化——读档后索引为空，由 SpawnSetup 的全量 RebuildView 末尾 RebuildIndex 重建；
        /// FindCopy 索引 miss 时回退线性扫描并补索引（自愈）。
        /// 维护约定：增 = EnsureCopyFor 物化处 IndexAdd（RebuildView 由 RebuildIndex 统一重建）；
        /// 删 = override Remove(Thing) 统一处理（所有视图移除路径均经 Remove）。</summary>
        private readonly Dictionary<OuterrealmEntryKey, Thing> copyIndex = new Dictionary<OuterrealmEntryKey, Thing>();

        // ── 预留记账缓存（§P0 预订记账优化，借鉴 Digital-Storage 的 reservedTotals） ──
        // 原 ReservedOn 每次全图扫描 map.reservationManager.ReservationsReadOnly 求 rThis/rAll
        // （CanReserve/Reserve/CanReserveStack/PreSplitOff 高频调用，O(全图 reservation)）。
        // 现改为惰性重建缓存：reservedByKey[key]=本视图内该 key 预留总量（rAll），
        // reservedByCopy[copy]=该副本自身预留量（rThis）；查询 O(1)。重建由 ReservationVersion
        // 版本号驱动（Reserve/Release* 各 patch 调 GameComponent.NotifyReservationChanged 使版本 +1），
        // 仅在版本变化后的首次查询做一次 O(全图 reservation) 重建，摊薄到低频预留变更点。
        // 不序列化——读档后由 RebuildView 置 reservedCacheVersion=-1 强制失效，首次查询懒重建。
        private readonly Dictionary<OuterrealmEntryKey, long> reservedByKey = new Dictionary<OuterrealmEntryKey, long>();
        private readonly Dictionary<Thing, long> reservedByCopy = new Dictionary<Thing, long>();
        private int reservedCacheVersion = -1;

        public OuterrealmVaultViewThingOwner(Building_OuterrealmVault vault)
            : base(vault, false, LookMode.Deep, false)
        {
            Vault = vault;
            dontTickContents = true; // 冻结强制项 2（§1.4）
        }

        // ── 容量：无限（§1.2） ─────────────────────────────────────────────────

        public override int GetCountCanAccept(Thing item, bool canMergeWithExistingStacks = true)
        {
            return int.MaxValue;
        }

        // ── 存入路径：吸收（§3.2 单一入口） ────────────────────────────────────

        public override bool TryAdd(Thing item, bool canMergeWithExistingStacks = true)
        {
            if (item == null || item.stackCount <= 0 || item.holdingOwner != null)
            {
                return false;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return false;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(item);
            gs.Deposit(item); // 吸收（复用为 proto 或销毁）
            // listerHaulables 通知由下方 EnsureCopyFor 物化新副本时的 Notify_ItemAdded 钩子覆盖（锁定条目经 #6 短路不加）；
            // 此处不再手动通知，避免对已吸收（可能已销毁）实例做无意义 Check（§3.2 单一入口）。
            EnsureCopyFor(key);
            return true;
        }

        public override int TryAdd(Thing item, int count, bool canMergeWithExistingStacks = true)
        {
            if (item == null || count <= 0 || item.stackCount <= 0 || item.holdingOwner != null)
            {
                return 0;
            }
            int take = Mathf.Min(item.stackCount, count);
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return 0;
            }
            Thing absorbed = item.SplitOff(take); // 拆分出要吸收的部分（item 保留剩余）
            OuterrealmEntryKey key = OuterrealmEntryKey.From(absorbed);
            gs.Deposit(absorbed);
            EnsureCopyFor(key);
            return take;
        }

        // ── SplitOff 同步（§3.3）：实时校正 + 差额扣减 + 即时补回 ──────────────

        /// <summary>SplitOff prefix：实时校正副本 = min(G − R + r_this, 上限)，防超卖。
        /// 上限 = stackLimit（正常形态）；副本处于 #9 提升状态（stackCount &gt; stackLimit）时放宽为不限，
        /// 使取物量不受 stackLimit 封顶。校正后记录 stackCount——差额扣减必须基于校正后值
        /// （否则虚高值会被当作取走量静默多扣全局，见 §3.3）。</summary>
        public void PreSplitOff(Thing copy)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry e = gs != null ? gs.FindEntry(OuterrealmEntryKey.From(copy)) : null;
            if (e == null || e.Count <= 0)
            {
                lastSplitOffStackCount = copy.stackCount;
                return;
            }
            long rThis;
            long rAll;
            ReservedOn(copy, out rThis, out rAll);
            long available = e.Count - rAll + rThis;
            // 提升状态（#9）下允许按全局量取物；正常形态维持 stackLimit 上限（视图形态约束）
            long cap = copy.stackCount > copy.def.stackLimit ? long.MaxValue : copy.def.stackLimit;
            long capped = available < 0 ? 0 : available;
            if (capped > cap)
            {
                capped = cap;
            }
            if (capped > int.MaxValue)
            {
                capped = int.MaxValue;
            }
            copy.stackCount = (int)capped;
            lastSplitOffStackCount = copy.stackCount; // 校正后记录（差额扣减的基准）
        }

        /// <summary>SplitOff postfix：差额扣减全局 + 即时补回副本（把补回从 60 tick 提前到变更点）。</summary>
        public void PostSplitOff(Thing copy, Thing result)
        {
            if (result == copy)
            {
                return; // 整堆分支：已走 holdingOwner.Remove → Notify_ItemRemoved 同步，此处跳过防双扣（§3.3）
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(copy);
            int diff = lastSplitOffStackCount - copy.stackCount;
            if (diff > 0)
            {
                gs.Subtract(key, diff);
            }
            OuterrealmEntry e = gs.FindEntry(key);
            if (e == null || e.Count <= 0)
            {
                // 条目已空：副本无意义，移除（Notify_ItemRemoved 扣 0 无害，lister 清理）。
                Remove(copy);
            }
            else
            {
                copy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue));
            }
        }

        // ── 预留记账（§3.3 → §P0 预订记账优化）：O(1) 查表 + 版本号惰性重建 ──

        /// <summary>确保预留缓存新鲜：与全局 ReservationVersion 不等时全量重建一次
        /// （O(全图 reservation)，仅在版本变化后的首次查询触发）。</summary>
        private void EnsureReservationCache(GameComponent_OuterrealmStorage gs)
        {
            if (reservedCacheVersion == gs.ReservationVersion)
            {
                return;
            }
            RebuildReservationCache(gs);
        }

        /// <summary>全量重建预留缓存：一次扫描本视图内所有预留，填充 reservedByKey / reservedByCopy。
        /// 语义与原 ReservedOn 逐条扫描完全一致（只统计 holdingOwner == this 的副本），
        /// 只是把 O(n) 从每次查询摊薄到低频的预留变更点。</summary>
        private void RebuildReservationCache(GameComponent_OuterrealmStorage gs)
        {
            reservedByKey.Clear();
            reservedByCopy.Clear();
            Map map = Vault != null ? Vault.MapHeld : null;
            if (map != null)
            {
                List<ReservationManager.Reservation> reservations = map.reservationManager.ReservationsReadOnly;
                if (reservations != null)
                {
                    for (int i = 0; i < reservations.Count; i++)
                    {
                        ReservationManager.Reservation r = reservations[i];
                        Thing t = r.Target.Thing;
                        if (t == null || t.holdingOwner != this)
                        {
                            continue;
                        }
                        int c = r.StackCount == ReservationManager.StackCount_All ? t.stackCount : r.StackCount;
                        if (c <= 0)
                        {
                            continue;
                        }
                        OuterrealmEntryKey key = OuterrealmEntryKey.From(t);
                        long existing;
                        reservedByKey[key] = reservedByKey.TryGetValue(key, out existing) ? existing + c : c;
                        reservedByCopy[t] = reservedByCopy.TryGetValue(t, out existing) ? existing + c : c;
                    }
                }
            }
            reservedCacheVersion = gs.ReservationVersion;
        }

        /// <summary>O(1) 推导该副本自身的预留 r_this 与条目总预留 r_all（缓存命中；版本过期时惰性重建）。</summary>
        private void ReservedOn(Thing copy, out long rThis, out long rAll)
        {
            rThis = 0;
            rAll = 0;
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            EnsureReservationCache(gs);
            OuterrealmEntryKey key = OuterrealmEntryKey.From(copy);
            long v;
            if (reservedByKey.TryGetValue(key, out v))
            {
                rAll = v;
            }
            if (reservedByCopy.TryGetValue(copy, out v))
            {
                rThis = v;
            }
        }

        /// <summary>副本当前可用量 = G − R + r_this（#8 预留检查与 SplitOff 校正共用）。</summary>
        public long AvailableForReserve(Thing copy)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return 0;
            }
            OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
            if (e == null)
            {
                return 0;
            }
            long rThis;
            long rAll;
            ReservedOn(copy, out rThis, out rAll);
            long available = e.Count - rAll + rThis;
            return available < 0 ? 0 : available;
        }

        // ── 副本管理 ───────────────────────────────────────────────────────────

        public Thing FindCopy(OuterrealmEntryKey key)
        {
            Thing copy;
            if (copyIndex.TryGetValue(key, out copy))
            {
                return copy;
            }
            // 索引 miss（读档初期 / 防御性回退）：线性扫描并补索引（自愈）。
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                if (OuterrealmEntryKey.From(list[i]) == key)
                {
                    copyIndex[key] = list[i];
                    return list[i];
                }
            }
            return null;
        }

        private void IndexAdd(Thing copy)
        {
            copyIndex[OuterrealmEntryKey.From(copy)] = copy;
        }

        private void IndexRemove(Thing copy)
        {
            if (copy != null)
            {
                copyIndex.Remove(OuterrealmEntryKey.From(copy));
            }
        }

        /// <summary>
        /// 半 Spawned 投影（§v3）：把副本注册进地图查询索引（listerThings），但不进入 thingGrid / 渲染，
        /// 使直查 listerThings / GenClosest 的拿取路径能发现它，而渲染/美观/爆炸/点击不可见。
        /// 未 Spawned 副本的 Position setter 只改写 positionInt，不触碰 thingGrid。
        /// </summary>
        private void RegisterInLister(Thing copy)
        {
            if (copy == null || copy.Spawned || !Vault.Spawned || Vault.MapHeld == null)
            {
                return;
            }
            copy.Position = Vault.InteractionCell; // 未 Spawned：仅设置 positionInt，保证直接读 Position 的代码不 NRE
            List<Thing> indexed = Vault.MapHeld.listerThings.ThingsOfDef(copy.def);
            if (indexed == null || !indexed.Contains(copy))
            {
                Vault.MapHeld.listerThings.Add(copy);
            }
        }

        /// <summary>半 Spawned 投影：从查询索引移除副本（须在 base.Remove 之前，防残留指向已销毁副本）。</summary>
        private void UnregisterFromLister(Thing copy)
        {
            if (copy == null || !Vault.Spawned || Vault.MapHeld == null)
            {
                return;
            }
            Vault.MapHeld.listerThings.Remove(copy);
        }

        /// <summary>从当前视图列表重建索引（RebuildView 末尾调用；读档后索引为空，全量重建必须重建索引）。</summary>
        private void RebuildIndex()
        {
            copyIndex.Clear();
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                copyIndex[OuterrealmEntryKey.From(list[i])] = list[i];
            }
        }

        /// <summary>统一移除入口：基类行为（holdingOwner 置 null、Notify_ItemRemoved 事件）保留，同步维护索引。
        /// 视图内一切副本移除路径（SyncKey/RebuildView/ClearView/DisposeOrphanCopy/TryDisposeCopyIfObsolete/
        /// PostSplitOff/RemoveApparel）均经此处，索引不会因遗漏而过期。</summary>
        public override bool Remove(Thing item)
        {
            // 先移除索引：整堆 SplitOff 走 holdingOwner.Remove(this) → base.Remove → NotifyRemoved →
            // Notify_ItemRemoved → Subtract(扣到 0) → NotifyEntriesEmptied → SyncKey 这条同步回调链。
            // 若索引滞后（IndexRemove 放在 base.Remove 之后），SyncKey 的 FindCopy 会经 copyIndex 命中
            // 这个"正在被移除"的副本并 copy.Destroy()，导致 splitStack 在 TryAdd 进 carry 之前被销毁
            // （全局已扣 + 物品已销毁 → 拿取即消失）。索引提前失效后 FindCopy 对 innerList 线性扫描
            // 也已移除该副本，返回 null，SyncKey 不会误 Destroy。IndexRemove 幂等，Remove 失败亦无害。
            IndexRemove(item);
            UnregisterFromLister(item); // 半 Spawned 投影：先摘查询索引，避免残留指向即将被销毁的副本
            return base.Remove(item);
        }

        /// <summary>确保本建筑视图包含该条目的副本（物化或更新数字）。filter 不允许则不做。</summary>
        public void EnsureCopyFor(OuterrealmEntryKey key)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntry e = gs.FindEntry(key);
            if (e == null || e.Count <= 0 || !Vault.GetStoreSettings().AllowedToAccept(e.Proto))
            {
                return;
            }
            if (e.Proto is Corpse)
            {
                return; // 尸体为唯一实体（InnerPawn 不可复制）：不物化视图副本，UI 直接显示条目 proto
            }
            Thing copy = FindCopy(key);
            if (copy != null)
            {
                copy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue));
                return;
            }
            Thing newCopy = GameComponent_OuterrealmStorage.Materialize(e.Proto);
            newCopy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(newCopy.def.stackLimit, int.MaxValue));
            // 标准加入（canMerge=false：视图内同 key 恒单副本，避免合并触发 TryAbsorbStack 补回全局的误判）；
            // 触发 Notify_ItemAdded → 建筑 hook → listerHaulables 单物品通知（锁定条目经 #6 短路不加）。
            if (base.TryAdd(newCopy, false))
            {
                IndexAdd(newCopy);
                RegisterInLister(newCopy); // 半 Spawned 投影：进入查询索引（不进入 thingGrid/渲染）
            }
        }

        /// <summary>增量同步单个 key（§3.3 方案 A）：filter 允许则物化/更新，禁止或条目消失则移除。</summary>
        public void SyncKey(OuterrealmEntryKey key)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntry e = gs.FindEntry(key);
            bool allowed = e != null && e.Count > 0 && Vault.GetStoreSettings().AllowedToAccept(e.Proto);
            Thing copy = FindCopy(key);
            if (allowed)
            {
                if (copy == null)
                {
                    EnsureCopyFor(key);
                }
                else
                {
                    copy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue));
                }
            }
            else if (copy != null)
            {
                // 条目不存在（count=0/已移除）：无条件移除副本（即使被预留——无物可取，
                // 引用它的 job 因副本销毁失败一次即恢复）；filter 禁止但条目存在：
                // 被预留则保留（退休：让既有 job 完成取出），否则移除。
                if ((e == null || e.Count <= 0) || !IsReserved(copy))
                {
                    // filter 禁止 / 条目消失 ≠ 取出：视图移除不得扣全局（§6.2），抑制通知并手动清理 lister。
                    SuppressRemovalSync = true;
                    try
                    {
                        Remove(copy);
                    }
                    finally
                    {
                        SuppressRemovalSync = false;
                    }
                    copy.Destroy();
                    if (Vault.Spawned && !copy.Spawned)
                    {
                        Vault.MapHeld.listerHaulables.Notify_DeSpawned(copy);
                    }
                }
            }
        }

        /// <summary>全量重建视图（SpawnSetup / 溢出 / 设置签名变化时；一次性成本 O(L1)）。</summary>
        public void RebuildView()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            SuppressRemovalSync = true;
            try
            {
                // 1. 移除不再允许 / 条目已消失的副本（条目不存在时无条件移除——无物可取；
                //    filter 禁止但条目存在时保留被预留副本，即简化退休 §3.3）。
                for (int i = Count - 1; i >= 0; i--)
                {
                    Thing copy = this[i];
                    OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                    bool allowed = e != null && e.Count > 0 && Vault.GetStoreSettings().AllowedToAccept(e.Proto);
                    if (allowed)
                    {
                        continue;
                    }
                    if (e != null && e.Count > 0 && IsReserved(copy))
                    {
                        continue;
                    }
                    Remove(copy);
                    copy.Destroy();
                }
                // 2. 物化缺失的允许条目（保留副本仅更新数字）。
                List<OuterrealmEntry> list = gs.EntriesForReading;
                for (int i = 0; i < list.Count; i++)
                {
                    OuterrealmEntry e = list[i];
                    if (e.Count <= 0 || !Vault.GetStoreSettings().AllowedToAccept(e.Proto))
                    {
                        continue;
                    }
                    if (e.Proto is Corpse)
                    {
                        continue; // 尸体不物化视图副本（唯一实体）
                    }
                    Thing copy = FindCopy(e.Key);
                    if (copy != null)
                    {
                        copy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue));
                    }
                    else
                    {
                        Thing newCopy = GameComponent_OuterrealmStorage.Materialize(e.Proto);
                        newCopy.stackCount = (int)Mathf.Min(e.Count, Mathf.Min(newCopy.def.stackLimit, int.MaxValue));
                        if (base.TryAdd(newCopy, false))
                        {
                            RegisterInLister(newCopy); // 半 Spawned 投影：读档/重建后随物化进入查询索引
                        }
                    }
                }
            }
            finally
            {
                SuppressRemovalSync = false;
            }
            RebuildIndex(); // 全量重建后统一重建索引（覆盖保留副本与新增副本，读档后索引为空必须重建）
            reservedCacheVersion = -1; // 预留缓存强制失效（§P0）：读档/重建时 reservations 可能尚未加载，下次查询懒重建
            if (Vault.Spawned)
            {
                Vault.MapHeld.listerHaulables.Notify_HaulSourceChanged(Vault);
            }
        }

        /// <summary>注销清理（DeSpawn/minify）：销毁全部视图副本，内容保留在全局层。</summary>
        public void ClearView()
        {
            SuppressRemovalSync = true;
            try
            {
                for (int i = Count - 1; i >= 0; i--)
                {
                    Thing copy = this[i];
                    Remove(copy);
                    copy.Destroy();
                }
            }
            finally
            {
                SuppressRemovalSync = false;
            }
        }

        // ── #9 数量替换：临时提升副本 stackCount（数量感知路径：选料/取料/计数） ──

        /// <summary>把单个副本的 stackCount 临时提升为全局剩余量（int 上限）。</summary>
        public void BoostCopy(Thing copy)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
            if (e != null && e.Count > 0)
            {
                copy.stackCount = (int)Mathf.Min(e.Count, int.MaxValue);
            }
        }

        /// <summary>提升全部副本（TryFindBestIngredientsHelper / CountProducts 使用）。</summary>
        public void BoostAllCopies()
        {
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                BoostCopy(list[i]);
            }
        }

        /// <summary>恢复单个副本为 min(全局剩余, stackLimit)。</summary>
        public void UnboostCopy(Thing copy)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
            copy.stackCount = e != null && e.Count > 0
                ? (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue))
                : 0;
        }

        /// <summary>恢复全部副本为 min(全局剩余, stackLimit)。</summary>
        public void UnboostCopies()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                UnboostCopy(list[i]);
            }
        }

        /// <summary>
        /// 孤儿副本自愈清理（§3.3）：条目已空（e==null / Count&lt;=0）但副本仍残留在视图时调用。
        /// 与 TryDisposeCopyIfObsolete 不同：不检查 IsReserved——条目无物可取，引用它的 job 本就
        /// 无法完成，强制清理使残留副本立即脱离 OptimizeApparel 候选（防"空条目副本被反复选中 →
        /// 预留失败"刷屏循环）；非取出语义，不扣全局。供 Reserve 预留命中空条目的兜底路径调用。
        /// </summary>
        public void DisposeOrphanCopy(Thing copy)
        {
            if (copy == null || !Contains(copy))
            {
                return;
            }
            SuppressRemovalSync = true;
            try
            {
                Remove(copy);
            }
            finally
            {
                SuppressRemovalSync = false;
            }
            copy.Destroy();
            if (Vault.Spawned && !copy.Spawned)
            {
                Vault.MapHeld.listerHaulables.Notify_DeSpawned(copy);
            }
        }

        /// <summary>
        /// 批量清理孤儿副本（§3.3 兜底）：移除视图内所有条目已空（e==null / Count&lt;=0）的残留副本。
        /// 正常路径由 Subtract 取空 → NotifyEntriesEmptied → SyncKey 即时清理；本方法用于
        /// JobGiver_OptimizeApparel 运行前（见 Patch_JobGiver_OptimizeApparel_TryGiveJob），把
        /// "枚举 GetDirectlyHeldThings 与条目取空之间的竞态残留"挡在 Wear job 生成之前——
        /// 否则空条目副本被选中 → StartJob 预留失败 → 原版 "TryMakePreToilReservations() returned
        /// false" 警告。语义与 DisposeOrphanCopy 一致：不检查 IsReserved，非取出语义不扣全局。
        /// </summary>
        public void CleanOrphanCopies()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<Thing> list = InnerListForReading;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Thing copy = list[i];
                OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                if (e == null || e.Count <= 0)
                {
                    DisposeOrphanCopy(copy);
                }
            }
        }

        private bool IsReserved(Thing copy)
        {
            Map map = copy.MapHeld;
            return map != null && map.reservationManager.IsReserved((LocalTargetInfo)copy);
        }

        /// <summary>
        /// 退休副本销毁检查（§3.3 退休副本生命周期）：reservation 释放后调用。
        /// 若副本对应条目已不存在 / filter 已禁止，则从视图移除并销毁（不扣全局——非取出语义）。
        /// 注意：调用时机须在 Release 之后（此时 reservation 已释放）。
        /// </summary>
        public void TryDisposeCopyIfObsolete(Thing copy)
        {
            if (copy == null || !Contains(copy))
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry e = gs != null ? gs.FindEntry(OuterrealmEntryKey.From(copy)) : null;
            bool allowed = e != null && e.Count > 0 && Vault.GetStoreSettings().AllowedToAccept(e.Proto);
            if (allowed)
            {
                return;
            }
            if (IsReserved(copy))
            {
                return;
            }
            SuppressRemovalSync = true;
            try
            {
                Remove(copy);
            }
            finally
            {
                SuppressRemovalSync = false;
            }
            copy.Destroy();
            if (Vault.Spawned && !copy.Spawned)
            {
                Vault.MapHeld.listerHaulables.Notify_DeSpawned(copy);
            }
        }
    }
}
