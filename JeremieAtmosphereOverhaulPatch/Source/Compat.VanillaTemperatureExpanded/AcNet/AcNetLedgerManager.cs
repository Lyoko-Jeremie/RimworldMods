using System.Collections.Generic;
using VanillaTemperatureExpanded;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet
{
    /// <summary>
    /// 每个 AcPipeNet 一个账本实例。
    ///
    /// 账本不持久化：中间量每 250 tick 全量重算，读档后自然恢复；
    /// 只有写回 net.Efficiency 的满足率会随 VTE 自己的字段存活。
    /// </summary>
    internal static class AcNetLedgerManager
    {
        private const int CleanupIntervalTicks = 5000;
        private const int StaleTicks = 5000;

        private static readonly Dictionary<AcPipeNet, AcNetLedger> Ledgers =
            new Dictionary<AcPipeNet, AcNetLedger>();

        private static int _lastCleanupTick;

        public static AcNetLedger Get(AcPipeNet net)
        {
            if (net == null)
            {
                return null;
            }

            AcNetLedger ledger;
            if (!Ledgers.TryGetValue(net, out ledger))
            {
                ledger = new AcNetLedger();
                Ledgers[net] = ledger;
            }
            return ledger;
        }

        /// <summary>清掉已销毁或长期没跑过的网络，避免拆管道后字典无限增长。</summary>
        public static void Cleanup(int tick)
        {
            if (tick - _lastCleanupTick < CleanupIntervalTicks)
            {
                return;
            }
            _lastCleanupTick = tick;

            List<AcPipeNet> dead = null;
            foreach (KeyValuePair<AcPipeNet, AcNetLedger> pair in Ledgers)
            {
                AcPipeNet net = pair.Key;
                if (net == null || net.map == null || tick - pair.Value.LastRunTick > StaleTicks)
                {
                    if (dead == null)
                    {
                        dead = new List<AcPipeNet>();
                    }
                    dead.Add(net);
                }
            }

            if (dead == null)
            {
                return;
            }
            for (int i = 0; i < dead.Count; i++)
            {
                if (dead[i] != null)
                {
                    Ledgers.Remove(dead[i]);
                }
            }
        }
    }
}
