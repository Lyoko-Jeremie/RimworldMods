using System.Collections.Generic;
using HarmonyLib;
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
        /// <summary>上下文（§v3）：建筑 vault 或授权 pawn 随身视图。随身上下文 Spawned=false，副本不进 lister。</summary>
        public readonly IOuterrealmVaultContext Context;

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

        /// <summary>§v5 伪 Spawned：Thing.mapIndexOrState 是 private sbyte 字段，反射读写并缓存
        /// FieldInfo（高频注册/撤销路径，避免每次反射查找）。字段名与 1.6 反编译一致；
        /// 若为 null（版本异常）则 RegisterInLister 回退为原半 Spawned 行为（不提升状态）。</summary>
        private static readonly System.Reflection.FieldInfo MapIndexOrStateField =
            AccessTools.Field(typeof(Thing), "mapIndexOrState");

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

        public OuterrealmVaultViewThingOwner(IOuterrealmVaultContext context)
            : base(context, false, LookMode.Deep, false)
        {
            Context = context;
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
            Map map = Context != null ? Context.MapHeld : null;
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

        // ── 借出/回收（§v4 预留驱动物化） ──────────────────────────────────────
        // 借出 = 预留时把锚点副本真 Spawn 到 vault 存储格（借出即取出：扣全局全量，
        // 消耗差额不再扣；剩余由 ReturnCopy 存回 → 账目守恒）。回收 = reservation 全部
        // 释放后剩余量 Deposit 回全局并重建锚点。

        private readonly HashSet<Thing> borrowedCopies = new HashSet<Thing>();
        private readonly Dictionary<OuterrealmEntryKey, Thing> borrowedByKey = new Dictionary<OuterrealmEntryKey, Thing>();

        public bool IsBorrowed(Thing copy)
        {
            return copy != null && borrowedCopies.Contains(copy);
        }

        /// <summary>预留物化：把锚点副本借出到存储格（§v4）。返回 false = 无空位/无效状态（调用方应拒绝预留）。</summary>
        public bool TryLendCopy(Thing copy)
        {
            if (copy == null || !Contains(copy) || IsBorrowed(copy) || !Context.Spawned)
            {
                return false;
            }
            Building_OuterrealmVault vault = Context as Building_OuterrealmVault;
            if (vault == null || !vault.Spawned)
            {
                return false;
            }
            IntVec3 cell = vault.FindStorageCellFor(copy);
            if (!cell.IsValid)
            {
                return false; // 存储格满：预留调用方拒绝（等价原版"存储区满"）
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(copy);
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return false;
            }
            // 1) 登记借出（保护：Subtract 取空触发的 SyncKey 清理跳过本副本）
            borrowedCopies.Add(copy);
            borrowedByKey[key] = copy;
            // 2) 借出即取出：扣全局全量（账目守恒）
            gs.Subtract(key, copy.stackCount);
            // 3) §v5 伪 Spawned：先撤销伪 Spawned（真 Spawn 要求未 Spawned——SpawnSetup
            //    对已 Spawned 物品 Log.Error 并拒绝），再真 Spawn 到存储格。
            //    GenSpawn 自动从 view 容器移除（view.Remove → Notify_ItemRemoved →
            //    IsBorrowed 短路，防双扣）；Notify_ReceivedThing 经 IsBorrowed 跳过吸收。
            UnregisterFromLister(copy);
            GenSpawn.Spawn(copy, cell, vault.MapHeld);
            return true;
        }

        /// <summary>回收借出副本：剩余量存回全局，重建锚点（§v4）。</summary>
        public void ReturnCopy(Thing copy)
        {
            if (copy == null || !IsBorrowed(copy))
            {
                return;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(copy);
            borrowedCopies.Remove(copy);
            borrowedByKey.Remove(key);
            // 仅回收仍驻留存储格的借出副本：被 job 取走（carry/穿戴等，已扣全局全量）属"取出"语义，
            // 剩余物已离开 vault，不再回收；被销毁（爆炸等）无剩余。
            bool stillInStorageCell = copy.Spawned
                && Context is Building_OuterrealmVault vault && vault.AllSlotCellsList().Contains(copy.Position);
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (stillInStorageCell && !copy.Destroyed && copy.stackCount > 0 && gs != null)
            {
                gs.Deposit(copy); // 剩余量存回全局（DeSpawn + 并入条目）
            }
            else if (stillInStorageCell && !copy.Destroyed)
            {
                copy.Destroy(); // 无剩余：直接销毁借出副本
            }
            if (Context.Spawned)
            {
                EnsureCopyFor(key); // 重建半 Spawn 锚点（条目仍存在且可见时）
            }
        }

        /// <summary>回收所有已无 reservation 引用的借出副本（vault Tick 调用，§v4）。</summary>
        public void ReturnUnreservedBorrowed()
        {
            if (borrowedCopies.Count == 0)
            {
                return;
            }
            List<Thing> toReturn = null;
            foreach (Thing copy in borrowedCopies)
            {
                if (!IsReserved(copy))
                {
                    if (toReturn == null)
                    {
                        toReturn = new List<Thing>();
                    }
                    toReturn.Add(copy);
                }
            }
            if (toReturn == null)
            {
                return;
            }
            for (int i = 0; i < toReturn.Count; i++)
            {
                ReturnCopy(toReturn[i]);
            }
        }

        /// <summary>回收全部借出副本（vault 拆除时，§v4）。</summary>
        public void ReturnAllBorrowed()
        {
            if (borrowedCopies.Count == 0)
            {
                return;
            }
            List<Thing> copies = new List<Thing>(borrowedCopies);
            for (int i = 0; i < copies.Count; i++)
            {
                ReturnCopy(copies[i]);
            }
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
        /// 伪 Spawned 投影（§v5）：把副本注册为"伪 Spawned"——mapIndexOrState = map.Index，
        /// 使 Thing.Spawned / Map / MapHeld / Position 全部表现为"在地图上"；并注册进地图级
        /// listerThings 与 region.ListerThings（GenClosest region 搜索可见）。
        /// 刻意不注册 thingGrid / coverGrid / gasGrid / mapMesh → 无物理存在、无渲染、不可点击。
        /// 这样所有第三方"thing.Spawned 校验 + region 搜索"的取货逻辑（Raven 物流箱、
        /// 牵引光束搬运器等）无需补丁即可发现 vault 物品；执行经预留物化（TryLendCopy）
        /// 走真 Spawned 原版路径（层 2）。
        /// 顺序要求：先设 positionInt（此时未 Spawned，Position setter 只写 positionInt），
        /// 再提升 mapIndexOrState（Spawned 后 Position setter 会做完整地理注册，见 Thing.Position）。
        /// Position 必须落在 vault 占格（GetProjectionCell）：伪 Spawned 后
        /// StoreUtility.CurrentHaulDestinationOf 的 Spawned 分支按 SlotGroupParentAt(Position)
        /// 判定副本"所在存储"——若用建筑占格外的交互格，副本会被判定为"位于交互格处存储/无存储"
        /// 而非"位于 vault"，优先级错乱后 TryFindBestBetterStorageFor 把 vault 自身选为"更好存储"，
        /// 搬运工取出（v4 物化）→ 放入（v3 HaulToContainer 吸收）→ 副本重生 → 无限搬运循环。
        /// </summary>
        private void RegisterInLister(Thing copy)
        {
            if (copy == null || copy.Spawned || !Context.Spawned || Context.MapHeld == null)
            {
                return;
            }
            copy.Position = GetProjectionCell(); // 伪 Spawned 前：未 Spawned 仅写 positionInt
            Map map = Context.MapHeld;
            if (MapIndexOrStateField != null)
            {
                MapIndexOrStateField.SetValue(copy, (sbyte)map.Index); // 提升为伪 Spawned
            }
            // 判重不用 ThingsOfDef：对 MinifiedThing（打包建筑）会触发 RimWorld 防御性报错，
            // 且 ThingsOfDef 按 def 索引会把所有打包建筑混为一组。Contains 直接按实例判重，语义更准确。
            if (!map.listerThings.Contains(copy))
            {
                map.listerThings.Add(copy);
            }
            RegionListersUpdater.RegisterInRegions(copy, map); // region 级索引（幂等：Contains 判重）
        }

        /// <summary>伪 Spawned 副本的投影格：vault 上下文须用建筑锚点格（占格，SlotGroup 内），
        /// 使 CurrentHaulDestinationOf → SlotGroupParentAt(Position) = vault（见 RegisterInLister 注释）；
        /// 非 vault 上下文（随身视图，不注册 lister）退回 InteractionCell。</summary>
        private IntVec3 GetProjectionCell()
        {
            if (Context is Building_OuterrealmVault vault && vault.Spawned)
            {
                return vault.Position;
            }
            return Context.InteractionCell;
        }

        /// <summary>伪 Spawned 投影：撤销注册并恢复未 Spawned（须在 base.Remove 之前，
        /// 防残留指向已销毁副本）。只处理伪 Spawned 锚点（IsPseudoSpawned）；真 Spawned
        /// 借出副本由正常 DeSpawn 流程处理，未注册副本无需处理。</summary>
        private void UnregisterFromLister(Thing copy)
        {
            if (copy == null || !Context.Spawned || Context.MapHeld == null)
            {
                return;
            }
            if (!IsPseudoSpawned(copy))
            {
                return;
            }
            RegionListersUpdater.DeregisterInRegions(copy, Context.MapHeld);
            Context.MapHeld.listerThings.Remove(copy);
            copy.ForceSetStateToUnspawned(); // mapIndexOrState = -1（public API，恢复未 Spawned）
        }

        /// <summary>伪 Spawned 判定：视图持有（holdingOwner == this）且 Spawned 的副本。
        /// 真 Spawned 借出副本经 GenSpawn.Spawn 已从视图移除（holdingOwner 置 null），不满足。</summary>
        private bool IsPseudoSpawned(Thing copy)
        {
            return copy != null && copy.Spawned && copy.holdingOwner == this;
        }

        /// <summary>
        /// 从 listerHaulables 摘除副本（须在 base.Remove 之前，此时 holdingOwner 尚未置 null）。
        /// 恒执行（不受 SuppressRemovalSync 影响）：视图重建/注销抑制了 Notify_ItemRemoved 的
        /// Notify_DeSpawned，若不在此摘除，已 Destroy 副本会残留 listerHaulables，
        /// 被 TryOpportunisticJob → PawnCanAutomaticallyHaulFast → Fogged 命中（MapHeld=null → NRE）。
        /// §v5 伪 Spawned：副本 Spawned=true 时也会被 ShouldBeHaulable 加入 haulables，
        /// 同样须摘除（Notify_DeSpawned 内部按集合 Contains 判空，幂等安全）。
        /// </summary>
        private void UnregisterFromHaulables(Thing copy)
        {
            if (copy == null || !Context.Spawned || Context.MapHeld == null)
            {
                return;
            }
            Context.MapHeld.listerHaulables.Notify_DeSpawned(copy);
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
            UnregisterFromHaulables(item); // 恒摘 listerHaulables（含 SuppressRemovalSync 期间），防残留已 Destroy 副本被 TryOpportunisticJob 命中 → MapHeld=null NRE
            return base.Remove(item);
        }

        /// <summary>确保本建筑视图包含该条目的副本（物化或更新数字）。filter 不允许则不做。</summary>
        public void EnsureCopyFor(OuterrealmEntryKey key)
        {
            if (borrowedByKey.ContainsKey(key))
            {
                return; // §v4：条目副本已借出（预留物化中），不重建锚点
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntry e = gs.FindEntry(key);
            if (e == null || e.Count <= 0 || !Context.CanShow(e.Proto))
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
            bool allowed = e != null && e.Count > 0 && Context.CanShow(e.Proto);
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
                if (IsBorrowed(copy))
                {
                    return; // §v4：借出副本（预留物化中）不清理，由 ReturnCopy 回收
                }
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
                    if (Context.Spawned && !copy.Spawned)
                    {
                        Context.MapHeld.listerHaulables.Notify_DeSpawned(copy);
                    }
                }
            }
        }

        /// <summary>
        /// 全量重建视图（SpawnSetup / 溢出时；一次性成本 O(副本数 + 全局条目数)）。
        /// = RemoveDisallowedCopies（同步移除）+ MaterializeMissingCopies（物化缺失）+ 索引/注册收尾。
        /// filter 变更路径不走本方法：改为同步移除 + 帧末微批物化（见 Building_OuterrealmVault.Notify_SettingsChanged），
        /// 避免每次 filter 点击触发全量重建（§filter 视图过滤简化）。
        /// </summary>
        public void RebuildView()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            RemoveDisallowedCopies();
            MaterializeMissingCopies();
            RebuildIndex(); // 全量重建后统一重建索引（覆盖保留副本与新增副本，读档后索引为空必须重建）
            reservedCacheVersion = -1; // 预留缓存强制失效（§P0）：读档/重建时 reservations 可能尚未加载，下次查询懒重建
            EnsureAllRegistered(); // §v5：读档后副本 mapIndexOrState 被原版重置为 -1，重新注册为伪 Spawned（幂等）
            if (Context.Spawned && Context is IHaulSource haulSource)
            {
                Context.MapHeld.listerHaulables.Notify_HaulSourceChanged(haulSource);
            }
        }

        /// <summary>
        /// 移除 filter 不再允许 / 条目已消失的副本（filter 变更的同步部分，O(视图副本数)）。
        /// 条目不存在时无条件移除（无物可取）；filter 禁止但条目存在时保留被预留副本，即简化退休 §3.3。
        /// 同步执行：filter 是视图过滤语义，"禁止"须立即生效（不可见 / 不可访问）。
        /// 注意：filter 禁止 / 条目消失 ≠ 取出——视图移除不得扣全局，包 SuppressRemovalSync（§6.2）。
        /// </summary>
        public void RemoveDisallowedCopies()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            SuppressRemovalSync = true;
            try
            {
                for (int i = Count - 1; i >= 0; i--)
                {
                    Thing copy = this[i];
                    OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                    bool allowed = e != null && e.Count > 0 && Context.CanShow(e.Proto);
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
            }
            finally
            {
                SuppressRemovalSync = false;
            }
        }

        /// <summary>
        /// 物化缺失的允许条目（filter 变更的异步部分，帧末微批执行；O(全局条目数)）。
        /// 保留副本仅更新数字；尸体不物化（唯一实体）。filter 允许但副本缺失时物化——
        /// filter 是视图过滤语义，"允许"的生效无紧迫性（物品始终在全局层，不丢失不移动），
        /// 延迟到帧末微批执行，避免阻塞 UI 点击（§filter 视图过滤简化）。
        /// 新物化副本经 base.TryAdd → Notify_ItemAdded 钩子自动进入 listerHaulables。
        /// </summary>
        public void MaterializeMissingCopies()
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<OuterrealmEntry> list = gs.EntriesForReading;
            for (int i = 0; i < list.Count; i++)
            {
                OuterrealmEntry e = list[i];
                if (e.Count <= 0 || !Context.CanShow(e.Proto))
                {
                    continue;
                }
                if (e.Proto is Corpse)
                {
                    continue; // 尸体不物化视图副本（唯一实体）
                }
                if (borrowedByKey.ContainsKey(e.Key))
                {
                    continue; // §v4：条目副本已借出（预留物化中），不重建锚点
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
                        RegisterInLister(newCopy); // 半 Spawned 投影：物化后进入查询索引
                    }
                }
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

        // ── §v5 伪 Spawned：注册兜底（读档 / region 拓扑重建） ─────────────────

        /// <summary>region 重建脏标记（§v5 批量合并）：Postfix（RegisterAllAt 命中 vault 格）
        /// 只置位 O(1)，由 Building_OuterrealmVault.Tick 每 60 tick 检查并批量刷新一次——
        /// 避免建设高峰每帧多次重建时每次都做 O(副本数) 全量 region 注册。
        /// 主线程单线程访问，无需同步。</summary>
        private bool regionDirty;

        /// <summary>置脏（重建事件触发，O(1)）。</summary>
        public void MarkRegionDirty()
        {
            regionDirty = true;
        }

        /// <summary>读取并清除脏标记（vault Tick 60 tick 调用）；返回是否有重建发生。</summary>
        public bool ConsumeRegionDirty()
        {
            bool wasDirty = regionDirty;
            regionDirty = false;
            return wasDirty;
        }

        // ── §filter 视图过滤简化：物化脏标记 ─────────────────────────────────
        // filter 变更（Notify_SettingsChanged / SetFrozen）只同步移除不再允许的副本（O(副本数)），
        // 新允许条目的物化延迟到帧末微批（GameComponent_OuterrealmStorage.MaterializeDirtyViews），
        // 把 O(全局条目数) 物化从 UI 点击同步路径移出。主线程单线程访问，无需同步。

        private bool materializeDirty;

        /// <summary>置脏（filter 变更后，O(1)）：请求帧末微批物化缺失的允许条目。</summary>
        public void MarkMaterializeDirty()
        {
            materializeDirty = true;
        }

        /// <summary>读取并清除脏标记（帧末微批调用）；返回是否需要物化。</summary>
        public bool ConsumeMaterializeDirty()
        {
            bool wasDirty = materializeDirty;
            materializeDirty = false;
            return wasDirty;
        }

        /// <summary>确保视图内全部锚点副本处于伪 Spawned 注册状态（RebuildView 末尾调用）。
        /// 读档后原版 ExposeData 会把所有物品 mapIndexOrState 重置为 -1，视图内已有副本
        /// （非新物化）须在此重新注册；幂等——已伪 Spawned 的副本被 RegisterInLister 的
        /// copy.Spawned 检查跳过。</summary>
        private void EnsureAllRegistered()
        {
            if (!Context.Spawned || Context.MapHeld == null)
            {
                return;
            }
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                Thing copy = list[i];
                if (copy != null && !copy.Spawned)
                {
                    RegisterInLister(copy);
                }
            }
        }

        /// <summary>region 拓扑重建后补注册（§v5 兜底）：region 合并/分割时 Region.ListerThings
        /// 随 region 实例重建而清空，而伪 Spawned 副本不在 thingGrid，重建不会自动恢复注册
        /// （region 搜索因此短暂不可见）。Building_OuterrealmVault.Tick 每 60 tick 调用，
        /// 幂等（RegisterInRegions 内部 Contains 判重）；副本数量有限，开销可接受。</summary>
        public void RefreshRegionRegistrations()
        {
            if (!Context.Spawned || Context.MapHeld == null)
            {
                return;
            }
            Map map = Context.MapHeld;
            List<Thing> list = InnerListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                Thing copy = list[i];
                if (IsPseudoSpawned(copy))
                {
                    RegionListersUpdater.RegisterInRegions(copy, map);
                }
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
            if (copy == null || !Contains(copy) || IsBorrowed(copy))
            {
                return; // §v4：借出副本不清理（由 ReturnCopy 回收）
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
            if (Context.Spawned && !copy.Spawned)
            {
                Context.MapHeld.listerHaulables.Notify_DeSpawned(copy);
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
            if (copy == null || !Contains(copy) || IsBorrowed(copy))
            {
                return; // §v4：借出副本不清理（由 ReturnCopy 回收）
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry e = gs != null ? gs.FindEntry(OuterrealmEntryKey.From(copy)) : null;
            bool allowed = e != null && e.Count > 0 && Context.CanShow(e.Proto);
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
            if (Context.Spawned && !copy.Spawned)
            {
                Context.MapHeld.listerHaulables.Notify_DeSpawned(copy);
            }
        }
    }
}
