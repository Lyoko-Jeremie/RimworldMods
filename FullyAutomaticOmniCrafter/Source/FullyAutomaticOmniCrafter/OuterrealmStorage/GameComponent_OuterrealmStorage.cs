using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储的全局层（超维空间本身）：唯一"真相"。
    /// 聚合条目列表（每类物品一条，含 long 计数）+ 版本号 + 增量变更日志（§3.3 方案 A）。
    /// 由 Game.FillComponents 自动实例化并随存档深保存，与建筑生命周期完全解耦。
    ///
    /// 读档时序注意（§1.5/§3.3）：Instance 必须在构造函数中赋值——
    /// 建筑的 SpawnSetup 早于 GameComponent.FinalizeInit，不能照搬 GameComponent_OmniResurrector
    /// 的 FinalizeInit 赋值模式；版本号与 lastSeenVersion 的重置必须发生在
    /// ExposeData(PostLoadInit)（早于任何建筑 SpawnSetup 的全量视图重建）。
    /// </summary>
    public class GameComponent_OuterrealmStorage : GameComponent
    {
        public static GameComponent_OuterrealmStorage Instance;

        private List<OuterrealmEntry> entries = new List<OuterrealmEntry>();
        private Dictionary<OuterrealmEntryKey, OuterrealmEntry> index = new Dictionary<OuterrealmEntryKey, OuterrealmEntry>();

        /// <summary>内容变更版本号（每次存入/取出/移除 +1，供建筑懒同步）。
        /// 使用不等比较（非大小比较）：天然抗 int 回绕——回绕周期 2^32 次变更，
        /// 游戏生命周期内不可能重合到旧值。</summary>
        private int version;

        // ── 增量变更日志（§3.3 方案 A）：环形去重窗口 ──
        private List<OuterrealmEntryKey> changeLog = new List<OuterrealmEntryKey>();
        private HashSet<OuterrealmEntryKey> changeLogSet = new HashSet<OuterrealmEntryKey>();
        private bool changeLogOverflow;
        private const int ChangeLogCapacity = 4096;

        /// <summary>已注册的建筑实例（SpawnSetup 注册 / DeSpawn 注销）。</summary>
        private List<Building_OuterrealmVault> vaults = new List<Building_OuterrealmVault>();

        // ── 弹出队列（§6.4）：与建筑生命周期解耦，建筑拆除后队列继续运行 ──
        private List<VaultEjectJob> ejectQueue = new List<VaultEjectJob>();

        public int Version => version;
        public bool NeedFullRebuild => changeLogOverflow;
        public List<OuterrealmEntry> EntriesForReading => entries;
        public List<Building_OuterrealmVault> VaultsForReading => vaults;
        public List<VaultEjectJob> EjectQueueForReading => ejectQueue;

        public GameComponent_OuterrealmStorage(Game game)
        {
            Instance = this;
        }

        // ── 存入 ────────────────────────────────────────────────────────────────

        /// <summary>吸收存入（§3.2 吸收路径的全局侧）：把 item 的全部 stackCount 并入对应条目。</summary>
        public void Deposit(Thing item)
        {
            if (item == null || item.stackCount <= 0)
            {
                return;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(item);
            OuterrealmEntry entry;
            if (index.TryGetValue(key, out entry))
            {
                entry.Count += item.stackCount;
                if (entry.Proto == null)
                {
                    entry.Proto = item;
                }
                else
                {
                    // 已吸收：实例不再需要（属性已并入条目）。立即销毁防止泄漏。
                    item.Destroy();
                }
            }
            else
            {
                entry = new OuterrealmEntry { Key = key, Proto = item, Count = item.stackCount };
                entries.Add(entry);
                index[key] = entry;
            }
            version++;
            AddToChangeLog(key);
        }

        // ── 查询 ────────────────────────────────────────────────────────────────

        public OuterrealmEntry FindEntry(OuterrealmEntryKey key)
        {
            OuterrealmEntry entry;
            return index.TryGetValue(key, out entry) ? entry : null;
        }

        public long CountOf(OuterrealmEntryKey key)
        {
            OuterrealmEntry entry = FindEntry(key);
            return entry != null ? entry.Count : 0L;
        }

        /// <summary>全局层总条目数与总数量（InspectString 用）。</summary>
        public void GetSummary(out int entryCount, out long totalCount)
        {
            entryCount = entries.Count;
            long total = 0L;
            for (int i = 0; i < entries.Count; i++)
            {
                total += entries[i].Count;
            }
            totalCount = total;
        }

        // ── 扣减（SplitOff 差额 / 整堆移除，§3.3） ──────────────────────────────

        /// <summary>按条目扣减全局数量；扣到 0 时移除条目。</summary>
        public void Subtract(OuterrealmEntryKey key, long amount)
        {
            if (amount <= 0)
            {
                return;
            }
            OuterrealmEntry entry;
            if (!index.TryGetValue(key, out entry))
            {
                // 条目已被取空移除（副本残留的账外量，如退休副本最后被取走）：全局已为 0，静默返回，
                // 避免"全局已空 + reservation 残留副本"场景刷屏（§3.3 退休副本生命周期）。
                return;
            }
            entry.Count -= amount;
            if (entry.Count < 0)
            {
                // 防御：不应发生（SplitOff 校正与预留记账保证不超卖），clamp 并记录。
                Log.Warning("[OuterrealmStorage] Entry count went negative: " + entry.Key + " amount=" + amount + ". Clamped to 0.");
                entry.Count = 0;
            }
            if (entry.Count == 0)
            {
                RemoveEntry(entry);
            }
            version++;
            AddToChangeLog(key);
        }

        /// <summary>移除条目（计数归零时）。Proto 随之销毁。</summary>
        private void RemoveEntry(OuterrealmEntry entry)
        {
            if (entry.Proto != null)
            {
                entry.Proto.Destroy();
                entry.Proto = null;
            }
            entries.Remove(entry);
            index.Remove(entry.Key);
        }

        // ── 取出（物化，未放置） ────────────────────────────────────────────────

        /// <summary>
        /// 从全局层取出 count 个：物化新 Thing（从 Proto 复制全部属性）并扣减全局。
        /// 物化实例由调用方负责放置（GenDrop.TryDropSpawn / carryTracker 等）。
        /// 超过 stackLimit 的数量由调用方分批处理；此处单次最多取 int 上限。
        /// </summary>
        public Thing Withdraw(OuterrealmEntry entry, int count)
        {
            if (entry == null || count <= 0 || entry.Count <= 0)
            {
                return null;
            }
            long take = count;
            if (take > entry.Count)
            {
                take = entry.Count;
            }
            Thing t = Materialize(entry.Proto);
            t.stackCount = (int)take;
            entry.Count -= take;
            if (entry.Count == 0)
            {
                RemoveEntry(entry);
            }
            version++;
            AddToChangeLog(entry.Key);
            return t;
        }

        /// <summary>从 Proto 物化新实例并复制 P1 基础属性（def/stuff/耐久/品质/样式/颜色）。</summary>
        public static Thing Materialize(Thing proto)
        {
            Thing t = ThingMaker.MakeThing(proto.def, proto.Stuff);
            if (proto.def.useHitPoints)
            {
                t.HitPoints = proto.HitPoints;
            }
            CompQuality qFrom = proto.TryGetComp<CompQuality>();
            CompQuality qTo = t.TryGetComp<CompQuality>();
            if (qFrom != null && qTo != null)
            {
                qTo.SetQuality(qFrom.Quality, ArtGenerationContext.Colony);
            }
            t.StyleDef = proto.StyleDef != null ? proto.StyleDef : t.StyleDef;
            CompColorable cFrom = proto.TryGetComp<CompColorable>();
            CompColorable cTo = t.TryGetComp<CompColorable>();
            if (cFrom != null && cTo != null && cFrom.Active)
            {
                cTo.DesiredColor = cFrom.Color;
            }
            return t;
        }

        // ── 变更日志（§3.3 方案 A） ─────────────────────────────────────────────

        /// <summary>内容变更统一入口：版本号 +1 并写入变更日志（所有数量变动点调用）。</summary>
        public void NotifyContentChanged(OuterrealmEntryKey key)
        {
            version++;
            AddToChangeLog(key);
        }

        /// <summary>回滚重建条目（TryAbsorbStack 回滚时条目已被取空移除；§3.3 回滚补偿）。</summary>
        public void RestoreEntry(OuterrealmEntry entry)
        {
            if (entry == null || entry.Proto == null || entry.Count <= 0)
            {
                return;
            }
            entries.Add(entry);
            index[entry.Key] = entry;
            version++;
            AddToChangeLog(entry.Key);
        }

        private void AddToChangeLog(OuterrealmEntryKey key)
        {
            if (changeLogOverflow)
            {
                // 溢出窗口后的首个新变化：重置日志，恢复正常记录。
                changeLogOverflow = false;
                changeLog.Clear();
                changeLogSet.Clear();
            }
            if (changeLogSet.Add(key))
            {
                changeLog.Add(key);
                if (changeLog.Count >= ChangeLogCapacity)
                {
                    changeLogOverflow = true;
                    changeLog.Clear();
                    changeLogSet.Clear();
                }
            }
        }

        /// <summary>读取窗口内全部变更 key（不清空；各建筑独立消费）。</summary>
        public void ReadChangeLog(List<OuterrealmEntryKey> outList)
        {
            outList.Clear();
            outList.AddRange(changeLog);
        }

        // ── 建筑注册 ────────────────────────────────────────────────────────────

        public void RegisterVault(Building_OuterrealmVault vault)
        {
            if (!vaults.Contains(vault))
            {
                vaults.Add(vault);
            }
        }

        public void UnregisterVault(Building_OuterrealmVault vault)
        {
            vaults.Remove(vault);
        }

        // ── 弹出队列（§6.4） ────────────────────────────────────────────────────

        /// <summary>把 count 个条目物化弹出到指定地图（追加到弹出队列，逐 tick 限速执行）。</summary>
        public void EnqueueEject(OuterrealmEntryKey key, Map map, long count)
        {
            if (count <= 0 || map == null || !FindEntryExists(key))
            {
                return;
            }
            int mapIndex = Find.Maps.IndexOf(map);
            if (mapIndex < 0)
            {
                return;
            }
            for (int i = 0; i < ejectQueue.Count; i++)
            {
                VaultEjectJob job = ejectQueue[i];
                if (job.Key == key && job.MapIndex == mapIndex)
                {
                    job.Remaining += count;
                    return;
                }
            }
            ejectQueue.Add(new VaultEjectJob { Key = key, MapIndex = mapIndex, Remaining = count });
        }

        private bool FindEntryExists(OuterrealmEntryKey key)
        {
            OuterrealmEntry e = FindEntry(key);
            return e != null && e.Count > 0;
        }

        /// <summary>每 tick 物化 ≤ 4 堆（每堆 ≤ stackLimit）到目标地图，防瞬间物化爆炸（§6.4/§6.5）。</summary>
        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (ejectQueue.Count == 0)
            {
                return;
            }
            int spawnedThisTick = 0;
            for (int i = ejectQueue.Count - 1; i >= 0 && spawnedThisTick < 4; i--)
            {
                VaultEjectJob job = ejectQueue[i];
                if (job.MapIndex < 0 || job.MapIndex >= Find.Maps.Count || Find.Maps[job.MapIndex] == null)
                {
                    ejectQueue.RemoveAt(i);
                    continue;
                }
                Map map = Find.Maps[job.MapIndex];
                OuterrealmEntry entry = FindEntry(job.Key);
                if (entry == null || entry.Count <= 0)
                {
                    ejectQueue.RemoveAt(i);
                    continue;
                }
                int stackLimit = entry.Proto != null && entry.Proto.def != null ? entry.Proto.def.stackLimit : 75;
                int take = (int)Math.Min(job.Remaining, Math.Min((long)stackLimit, entry.Count));
                take = Math.Min(take, int.MaxValue);
                if (take <= 0)
                {
                    ejectQueue.RemoveAt(i);
                    continue;
                }
                Thing t = Withdraw(entry, take);
                if (t == null)
                {
                    ejectQueue.RemoveAt(i);
                    continue;
                }
                job.Remaining -= t.stackCount;
                Thing dropped;
                if (GenDrop.TryDropSpawn(t, FindEjectAnchor(map), map, ThingPlaceMode.Near, out dropped))
                {
                    if (t.stackCount > 0)
                    {
                        Deposit(t); // 放置未完全（Near 模式极少）：退回全局，job.Remaining 保持待处理
                    }
                }
                else
                {
                    Deposit(t); // 放置失败：退回全局
                }
                if (job.Remaining <= 0)
                {
                    ejectQueue.RemoveAt(i);
                }
                spawnedThisTick++;
            }
        }

        /// <summary>弹出锚点：优先该地图上任一 Vault 的交互格，其次地图中心。</summary>
        private static IntVec3 FindEjectAnchor(Map map)
        {
            for (int i = 0; i < Instance.vaults.Count; i++)
            {
                Building_OuterrealmVault v = Instance.vaults[i];
                if (v != null && v.Spawned && v.Map == map)
                {
                    return v.InteractionCell;
                }
            }
            return map.Center;
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (entries == null)
                {
                    entries = new List<OuterrealmEntry>();
                }
                // 重建索引 + 从 Proto 重建分组键；重置版本号/变更日志（早于建筑 SpawnSetup 的视图重建）。
                index.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry e = entries[i];
                    if (e == null || e.Proto == null)
                    {
                        entries.RemoveAt(i);
                        i--;
                        continue;
                    }
                    e.Key = OuterrealmEntryKey.From(e.Proto);
                    e.LastSeenVersion = 0;
                    index[e.Key] = e;
                }
                version = 0;
                changeLog.Clear();
                changeLogSet.Clear();
                changeLogOverflow = false;
                // 弹出队列不序列化（目标地图索引在重载后不可靠；条目仍在全局层，玩家可重新弹出）。
                ejectQueue.Clear();
            }
        }
    }

    /// <summary>弹出任务（§6.4）：条目 + 目标地图索引 + 剩余数量。挂在全局层，与建筑生命周期解耦。</summary>
    public class VaultEjectJob
    {
        public OuterrealmEntryKey Key;
        public int MapIndex;
        public long Remaining;
    }
}
