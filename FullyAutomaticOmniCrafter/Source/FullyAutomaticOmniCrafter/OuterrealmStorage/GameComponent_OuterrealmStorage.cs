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

        // ── 弹出防回吸：弹出落地的物品短期内不被本系统建筑自动吸回（§6.4 分流语义） ──
        // 键为物化实例，值为弹出时的 TicksGame；超时（EjectNoReabsorbTicks）或物品销毁后自动清除，
        // 之后物品恢复可正常存入。不序列化（读档后清空，落地物品属存档实体由玩家处置）。
        private readonly Dictionary<Thing, int> ejectedTicks = new Dictionary<Thing, int>();
        private const int EjectNoReabsorbTicks = 600; // 10 秒：弹出可见期，防立即被搬回

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

        /// <summary>吸收存入（§3.2 吸收路径的全局侧）：把 item 的全部 stackCount 并入对应条目。
        /// Spawned 物品（如弹出后又被搬回/存回的尸体）先 DeSpawn——存入超维空间 = 从地图取出，
        /// 否则 proto 保持 Spawned，再次弹出时 GenSpawn 会报 "already spawned" 并死循环。</summary>
        public void Deposit(Thing item)
        {
            if (item == null || item.stackCount <= 0)
            {
                return;
            }
            if (item.Spawned)
            {
                item.DeSpawn(DestroyMode.Vanish);
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
        /// Corpse（唯一实体，InnerPawn 同一 pawn 只能属于一具尸体）：整体转移 proto 本身（不可复制）。
        /// 条目被取空后立即通知所有建筑视图移除残留副本（防 OptimizeApparel 等选中空条目副本 →
        /// 预留失败 → "TryMakePreToilReservations returned false" 警告 + pawn 卡住循环）。
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
            Thing t;
            if (entry.Proto is Corpse)
            {
                // 尸体不可复制：直接转移 proto 本身（条目随 1 具尸体清空）
                t = entry.Proto;
                t.stackCount = 1;
                entry.Count -= 1;
                if (entry.Count == 0)
                {
                    entry.Proto = null; // 已整体转移给调用方：防止 RemoveEntry 销毁它
                    RemoveEntry(entry);
                    NotifyEntriesEmptied(entry.Key);
                }
            }
            else
            {
                t = Materialize(entry.Proto);
                t.stackCount = (int)take;
                entry.Count -= take;
                if (entry.Count == 0)
                {
                    RemoveEntry(entry);
                    NotifyEntriesEmptied(entry.Key);
                }
            }
            version++;
            AddToChangeLog(entry.Key);
            return t;
        }

        /// <summary>条目被取空后：立即让所有建筑视图移除残留副本（§3.3 同步补到变更点，防空条目副本被自动 job 选中）。</summary>
        private void NotifyEntriesEmptied(OuterrealmEntryKey key)
        {
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.view != null)
                {
                    v.view.SyncKey(key);
                }
            }
        }

        /// <summary>从 Proto 物化新实例并复制 P1 基础属性（def/stuff/耐久/品质/样式/颜色）。
        /// MinifiedThing（打包建筑）：MakeThing 生成的 MinifiedThing 的 InnerThing 为 null，
        /// 会令 ThingFilter.Allows 的 GetInnerIfMinified() 返回 null 而 NRE——须从 proto 复制并设置 InnerThing。
        /// 注意：Corpse 是"唯一实体"（InnerPawn 同一 pawn 只能属于一具尸体），不可物化复制——
        /// 物化/取出尸体必须整体转移（见 Withdraw），此处不为 Corpse 复制 InnerPawn。</summary>
        public static Thing Materialize(Thing proto)
        {
            Thing t = ThingMaker.MakeThing(proto.def, proto.Stuff);
            MinifiedThing sourceMin = proto as MinifiedThing;
            if (t is MinifiedThing minified && sourceMin != null && sourceMin.InnerThing != null)
            {
                // 打包建筑：复制 InnerThing（def/stuff/基础属性）
                Thing inner = ThingMaker.MakeThing(sourceMin.InnerThing.def, sourceMin.InnerThing.Stuff);
                CopyBaseProperties(sourceMin.InnerThing, inner);
                minified.InnerThing = inner;
                return t;
            }
            CopyBaseProperties(proto, t);
            return t;
        }

        /// <summary>复制 P1 基础属性（耐久/品质/样式/颜色）。</summary>
        private static void CopyBaseProperties(Thing from, Thing to)
        {
            if (from.def.useHitPoints)
            {
                to.HitPoints = from.HitPoints;
            }
            CompQuality qFrom = from.TryGetComp<CompQuality>();
            CompQuality qTo = to.TryGetComp<CompQuality>();
            if (qFrom != null && qTo != null)
            {
                qTo.SetQuality(qFrom.Quality, ArtGenerationContext.Colony);
            }
            to.StyleDef = from.StyleDef != null ? from.StyleDef : to.StyleDef;
            CompColorable cFrom = from.TryGetComp<CompColorable>();
            CompColorable cTo = to.TryGetComp<CompColorable>();
            if (cFrom != null && cTo != null && cFrom.Active)
            {
                cTo.DesiredColor = cFrom.Color;
            }
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

        /// <summary>标记弹出物品（落地后短暂防回吸，§6.4）。</summary>
        public void MarkEjected(Thing t)
        {
            if (t != null)
            {
                ejectedTicks[t] = Find.TickManager.TicksGame;
            }
        }

        /// <summary>弹出物品在限时窗口内返回 true（本系统建筑 Accepts 应拒绝，防止弹出自吸回）。</summary>
        public bool IsEjected(Thing t)
        {
            if (t == null || !ejectedTicks.TryGetValue(t, out int tick))
            {
                return false;
            }
            if (t.Destroyed || Find.TickManager.TicksGame - tick > EjectNoReabsorbTicks)
            {
                ejectedTicks.Remove(t);
                return false;
            }
            return true;
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
                int withdrawn = t.stackCount;
                job.Remaining -= withdrawn;
                MarkEjected(t); // 弹出防回吸：落地后短暂不被本系统建筑自动吸回（§6.4）
                Thing dropped;
                // 放置时排除本系统建筑占位格（建筑 PassThroughOnly，物品可落在建筑格上——需放到建筑外附近）
                bool placed = GenDrop.TryDropSpawn(t, FindEjectAnchor(map), map, ThingPlaceMode.Near, out dropped, null, c => !IsVaultCell(c, map));
                // 1.6 放置语义：take ≤ stackLimit 时 TryDropSpawn 成功会把整个堆 Spawn（t.Spawned）
                // 或并入已有堆（t 被吸收销毁）——此时 t.stackCount 不再代表"未放置剩余"，
                // 不能再用它判定 leftover（否则会把刚落地的物品经 Deposit 收回，弹出永远失败）。
                // 仅放置失败（placed=false）或防御性部分放置（t 未 Spawned 且未销毁）时才需退回全局。
                int leftover = 0;
                if (!placed || (t != null && !t.Spawned && !t.Destroyed))
                {
                    leftover = t != null && !t.Destroyed ? t.stackCount : 0;
                }
                if (leftover > 0)
                {
                    // 全部或部分未放置：退回全局，并恢复 Remaining（该部分仍待弹出，避免"弹出静默失败"）
                    Deposit(t);
                    job.Remaining += leftover;
                    job.FailCount++;
                    if (job.FailCount > 20)
                    {
                        // 连续失败（如目标实体状态异常）：放弃任务防死循环刷屏，物品保留在全局层
                        Log.Warning("[OuterrealmStorage] 弹出任务连续失败已放弃: " + job.Key);
                        ejectQueue.RemoveAt(i);
                        continue;
                    }
                }
                else
                {
                    job.FailCount = 0;
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

        /// <summary>该格是否被本系统建筑（含正在弹出锚点的建筑）占用——弹出/丢弃放置须避开建筑
        /// 本体（建筑 PassThroughOnly 允许物品落在建筑格上，须放到建筑外附近）。</summary>
        public static bool IsVaultCell(IntVec3 c, Map map)
        {
            for (int i = 0; i < Instance.vaults.Count; i++)
            {
                Building_OuterrealmVault v = Instance.vaults[i];
                if (v != null && v.Spawned && v.Map == map && v.OccupiedRect().Contains(c))
                {
                    return true;
                }
            }
            return false;
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
                // 弹出防回吸标记不序列化（读档后清空；落地物品属存档实体由玩家处置）。
                ejectedTicks.Clear();
            }
        }
    }

    /// <summary>弹出任务（§6.4）：条目 + 目标地图索引 + 剩余数量。挂在全局层，与建筑生命周期解耦。</summary>
    public class VaultEjectJob
    {
        public OuterrealmEntryKey Key;
        public int MapIndex;
        public long Remaining;
        /// <summary>连续放置失败计数（超限放弃任务，防死循环刷屏）。</summary>
        public int FailCount;
    }
}
