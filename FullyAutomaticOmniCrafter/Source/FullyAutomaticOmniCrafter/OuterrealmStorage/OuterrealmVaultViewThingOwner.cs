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

        // ── 预留记账（§3.3）：R 扫描推导 ───────────────────────────────────────

        /// <summary>扫描地图 reservationManager，推导该副本自身的预留 r_this 与条目总预留 r_all。</summary>
        private void ReservedOn(Thing copy, out long rThis, out long rAll)
        {
            rThis = 0;
            rAll = 0;
            Map map = copy.MapHeld;
            if (map == null)
            {
                return;
            }
            List<ReservationManager.Reservation> reservations = map.reservationManager.ReservationsReadOnly;
            if (reservations == null)
            {
                return;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(copy);
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
                if (t == copy)
                {
                    rThis += c;
                }
                if (OuterrealmEntryKey.From(t) == key)
                {
                    rAll += c;
                }
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
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                if (OuterrealmEntryKey.From(list[i]) == key)
                {
                    return list[i];
                }
            }
            return null;
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
            base.TryAdd(newCopy, false);
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
                        base.TryAdd(newCopy, false);
                    }
                }
            }
            finally
            {
                SuppressRemovalSync = false;
            }
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
                Thing copy = list[i];
                OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                copy.stackCount = e != null && e.Count > 0
                    ? (int)Mathf.Min(e.Count, Mathf.Min(copy.def.stackLimit, int.MaxValue))
                    : 0;
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
