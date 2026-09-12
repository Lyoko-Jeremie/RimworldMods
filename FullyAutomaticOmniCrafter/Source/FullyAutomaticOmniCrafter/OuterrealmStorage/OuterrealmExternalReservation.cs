using System;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 外部数量预留提供者：为「不经过原版 ReservationManager、自行管理 claim/队列」的第三方
    /// 搬运系统提供超维存储的条目级数量预留参与。实现方自行保证线程安全。
    ///
    /// 未注册任何提供者时，超维存储的可用量口径与不带任何适配 mod 时完全一致：
    /// ReservedTotal 返回 0、AnyReserved/AnyExtracting 与 HasForeignReservation 返回 false、
    /// AdjustAvailable/AdjustRequest 为恒等变换。
    /// </summary>
    public interface IOuterrealmExternalReservation
    {
        /// <summary>该条目被本系统占用、尚未兑现的数量。</summary>
        long ReservedFor(OuterrealmEntry entry);

        /// <summary>该条目正处于提交（取出中）：对外表现为整条目不可用，直到提交结束。</summary>
        bool IsExtracting(OuterrealmEntry entry);

        /// <summary>本系统是否已在他人（非 caller）名下占用该条目，用于唯一锚点互斥。</summary>
        bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry);

        /// <summary>原版 CanReserve 被本系统的操作者调用时的可用量修正；默认返回 available。</summary>
        long AdjustAvailable(Pawn caller, Thing query, long available);

        /// <summary>原版 CanReserve 被本系统的操作者调用时的请求量修正；默认返回 requested。</summary>
        int AdjustRequest(Pawn caller, Thing query, int requested, long available);

        /// <summary>仓库注销时清理本系统在该仓库范围内的未兑现预留。</summary>
        void ForgetVault(Building_OuterrealmVault vault);

        /// <summary>地图移除时清理本系统在该地图范围内的未兑现预留。</summary>
        void ForgetMap(Map map);
    }

    /// <summary>
    /// 外部预留提供者注册表与汇总口径。热路径（ReservedCountOf / CanReserve）只读快照，
    /// 不取锁不分配；注册/注销时在锁内重建快照数组。
    /// </summary>
    public static class OuterrealmExternalReservationRegistry
    {
        private static readonly object gate = new object();
        private static IOuterrealmExternalReservation[] snapshot = new IOuterrealmExternalReservation[0];
        private static int snapshotCount;

        public static void Register(IOuterrealmExternalReservation provider)
        {
            if (provider == null)
            {
                return;
            }
            lock (gate)
            {
                IOuterrealmExternalReservation[] current = snapshot;
                for (int i = 0; i < snapshotCount; i++)
                {
                    if (ReferenceEquals(current[i], provider))
                    {
                        return;
                    }
                }
                IOuterrealmExternalReservation[] next = new IOuterrealmExternalReservation[snapshotCount + 1];
                Array.Copy(current, next, snapshotCount);
                next[snapshotCount] = provider;
                snapshot = next;
                snapshotCount++;
            }
        }

        public static void Unregister(IOuterrealmExternalReservation provider)
        {
            if (provider == null)
            {
                return;
            }
            lock (gate)
            {
                IOuterrealmExternalReservation[] current = snapshot;
                int index = -1;
                for (int i = 0; i < snapshotCount; i++)
                {
                    if (ReferenceEquals(current[i], provider))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                {
                    return;
                }
                IOuterrealmExternalReservation[] next = new IOuterrealmExternalReservation[snapshotCount - 1];
                if (index > 0)
                {
                    Array.Copy(current, 0, next, 0, index);
                }
                if (index < snapshotCount - 1)
                {
                    Array.Copy(current, index + 1, next, index, snapshotCount - index - 1);
                }
                snapshot = next;
                snapshotCount--;
            }
        }

        /// <summary>所有提供者在该条目上的未兑现预留总量。</summary>
        public static long ReservedTotal(OuterrealmEntry entry)
        {
            if (entry == null)
            {
                return 0L;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            long total = 0L;
            for (int i = 0; i < snapshotCount; i++)
            {
                total += providers[i].ReservedFor(entry);
            }
            return total;
        }

        /// <summary>是否有任一提供者占用了该条目（用于唯一锚点的归仓抑制）。</summary>
        public static bool AnyReserved(OuterrealmEntry entry)
        {
            if (entry == null)
            {
                return false;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                if (providers[i].ReservedFor(entry) > 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否有任一提供者正对该条目执行取出提交。</summary>
        public static bool AnyExtracting(OuterrealmEntry entry)
        {
            if (entry == null)
            {
                return false;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                if (providers[i].IsExtracting(entry))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry)
        {
            if (entry == null)
            {
                return false;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                if (providers[i].HasForeignReservation(caller, query, entry))
                {
                    return true;
                }
            }
            return false;
        }

        public static long AdjustAvailable(Pawn caller, Thing query, long available)
        {
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                available = providers[i].AdjustAvailable(caller, query, available);
            }
            return available;
        }

        public static int AdjustRequest(Pawn caller, Thing query, int requested, long available)
        {
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                requested = providers[i].AdjustRequest(caller, query, requested, available);
            }
            return requested;
        }

        /// <summary>仓库注销时通知全部提供者清理该仓库范围内的预留。</summary>
        public static void NotifyVaultRemoved(Building_OuterrealmVault vault)
        {
            if (vault == null)
            {
                return;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                providers[i].ForgetVault(vault);
            }
        }

        /// <summary>地图移除时通知全部提供者清理该地图范围内的预留。</summary>
        public static void NotifyMapRemoved(Map map)
        {
            if (map == null)
            {
                return;
            }
            IOuterrealmExternalReservation[] providers = snapshot;
            for (int i = 0; i < snapshotCount; i++)
            {
                providers[i].ForgetMap(map);
            }
        }

        // ── 供提供者回调主 mod（替代原先直接引用第三方账本的两个委托） ──

        /// <summary>提供者的预留发生变化：使全局预留汇总缓存失效。</summary>
        public static void NotifyReservationChanged()
        {
            GameComponent_OuterrealmStorage.Instance?.NotifyReservationChanged();
        }

        /// <summary>提供者释放了对某唯一锚点的占用：立即协调该锚点的归仓。</summary>
        public static void NotifyIdentityReservationReleased(Thing query)
        {
            OuterrealmIdentityRouting.NotifyReservationReleased(query);
        }
    }
}
