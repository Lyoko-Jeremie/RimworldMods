using System;
using System.Collections.Generic;
using HarmonyLib;
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
        // §B 设计（CanStackWith 动态分组）：条目不再按 OuterrealmEntryKey 哈希索引——
        // 合并判据 = 原版 Thing.CanStackWith + def.stackLimit > 1（原版容量语义：stackLimit=1
        // 的基因组/异种胚芽等"唯一实体"类物品永不合并，每实例独立条目，内容不丢失）。
        // 查找经 byDef 粗索引（def 不同必不能堆叠）缩小候选后线性 CanStackWith 判定（O(同 def 条目数)）。
        private Dictionary<ThingDef, List<OuterrealmEntry>> byDef = new Dictionary<ThingDef, List<OuterrealmEntry>>();

        // ── 按 ThingDef 聚合总量缓存（TotalCountOf 用，§兼容） ──
        // 万能制造机补货判断/UI 每帧按订单查询 vault 存量；entries 遍历 O(n)
        // 摊薄到内容变更后的首次查询（version 不等即重建，其余调用 O(1) 命中）。
        // 不序列化——读档重建条目后显式失效。所有数量变更点均已 version++。
        private Dictionary<ThingDef, long> totalByDef;
        private int totalByDefVersion = -1;

        /// <summary>内容变更版本号（每次存入/取出/移除 +1，供建筑懒同步）。
        /// 使用不等比较（非大小比较）：天然抗 int 回绕——回绕周期 2^32 次变更，
        /// 游戏生命周期内不可能重合到旧值。</summary>
        private int version;

        /// <summary>原版 ReservationManager 预留变更版本号（每次 Reserve/Release* +1）。
        /// 供各 vault 视图的预留缓存（reservedByEntry/reservedByCopy）做 O(1) 失效判定：
        /// 视图缓存记录上次重建时的 ReservationVersion，不等即重建。不序列化——
        /// 读档时重置为 0，且 RebuildView 会把视图缓存版本置 -1 强制失效（§P0 预订记账优化）。</summary>
        private int reservationVersion;

        // ── 增量变更日志（§3.3 方案 A）：环形去重窗口 ──
        // §B：日志改存条目引用（key 不再标识条目——stackLimit=1 物品多条目可同 key）。
        private List<OuterrealmEntry> changeLog = new List<OuterrealmEntry>();
        private HashSet<OuterrealmEntry> changeLogSet = new HashSet<OuterrealmEntry>();
        private bool changeLogOverflow;
        private const int ChangeLogCapacity = 4096;

        /// <summary>已注册的建筑实例（SpawnSetup 注册 / DeSpawn 注销）。</summary>
        private List<Building_OuterrealmVault> vaults = new List<Building_OuterrealmVault>();

        /// <summary>帧末微批同步用缓存列表（§3.3 实时同步方案 B：复用避免每帧分配）。</summary>
        private readonly List<OuterrealmEntry> tmpSyncKeys = new List<OuterrealmEntry>();

        // ── 弹出队列（§6.4）：与建筑生命周期解耦，建筑拆除后队列继续运行 ──
        private List<VaultEjectJob> ejectQueue = new List<VaultEjectJob>();

        public int Version => version;
        public int ReservationVersion => reservationVersion;
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
        /// 否则 proto 保持 Spawned，再次弹出时 GenSpawn 会报 "already spawned" 并死循环。
        /// §B：合并判据 = 原版 CanStackWith + stackLimit>1（FindEntry 动态判定）；不满足则新建条目。
        /// 返回条目（新增或已有），供视图层物化/更新副本。</summary>
        public OuterrealmEntry Deposit(Thing item)
        {
            if (item == null || item.stackCount <= 0)
            {
                return null;
            }
            if (item.Spawned)
            {
                item.DeSpawn(DestroyMode.Vanish);
            }
            OuterrealmEntry existing = FindEntry(item);
            if (existing != null)
            {
                existing.Count += item.stackCount;
                if (existing.Proto == null)
                {
                    existing.Proto = item;
                }
                else
                {
                    // 已吸收：实例不再需要（属性已并入条目）。立即销毁防止泄漏。
                    item.Destroy();
                }
                version++;
                AddToChangeLog(existing);
                return existing;
            }
            OuterrealmEntry entry = new OuterrealmEntry
            {
                Key = OuterrealmEntryKey.From(item),
                Proto = item,
                Count = item.stackCount
            };
            AddEntry(entry);
            version++;
            AddToChangeLog(entry);
            return entry;
        }

        /// <summary>合并判据（§B）：原版"是否允许合并放置"= CanStackWith（def/stuff/非relic/Item 类别/
        /// 各 comp AllowStackWith，含第三方覆写）+ 原版容量语义 stackLimit>1（stackLimit=1 的
        /// 基因组/异种胚芽/尸体/武器等"唯一实体"永不合并，内容随实例保存，杜绝合并丢失）。
        /// 纯通用判定、无物品类型特判（Corpse 的 stackLimit=1 自然走"不合并"分支，与原 UniqueId
        /// 分组行为等价：每具尸体独立条目）。MinifiedThing（打包建筑）def 未写 stackLimit（默认 1）
        /// → 按原版语义亦不可堆叠、每实例独立条目（对齐原版；旧版按 InnerThing.def 聚合的行为取消，
        /// 数量不丢）。不检查"当前堆未满"：vault 条目是多个原版堆的聚合，long 计数可超 stackLimit。</summary>
        private static bool CanMergeInto(OuterrealmEntry entry, Thing item)
        {
            if (entry == null || entry.Count <= 0 || entry.Proto == null || entry.Proto.Destroyed
                || item == null || item.Destroyed)
            {
                return false;
            }
            ThingDef def = item.def;
            if (def == null || def.stackLimit <= 1 || def.category != ThingCategory.Item)
            {
                return false;
            }
            return entry.Proto.CanStackWith(item);
        }

        /// <summary>动态查找可合并条目（§B）：byDef 粗索引 + CanStackWith 线性判定。
        /// 语义 = 原版"该物品可并入该堆"；stackLimit=1 物品恒返回 null（永不合并，调用方新建条目）。
        /// 唯一特判：Corpse（唯一实体，InnerPawn 同一 pawn 只能属于一具尸体）按 InnerPawn.thingIDNumber
        /// 匹配原条目（保留 UniqueId 分组语义）；其余物品一律走通用 CanStackWith 判定。
        /// 注意：仅用于存入合并查找；副本→条目的关联由视图层 GetEntryOf 维护（见 OuterrealmVaultViewThingOwner）。</summary>
        public OuterrealmEntry FindEntry(Thing item)
        {
            if (item == null || item.def == null)
            {
                return null;
            }
            // 尸体特判（唯一 pawn 标识）：同一具尸体再次存入时并入原条目。
            if (item is Corpse corpse && corpse.InnerPawn != null)
            {
                int id = corpse.InnerPawn.thingIDNumber;
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry e = entries[i];
                    if (e != null && e.Count > 0 && e.Proto is Corpse cp
                        && cp.InnerPawn != null && cp.InnerPawn.thingIDNumber == id)
                    {
                        return e;
                    }
                }
                return null;
            }
            List<OuterrealmEntry> cands;
            if (!byDef.TryGetValue(item.def, out cands))
            {
                return null;
            }
            for (int i = 0; i < cands.Count; i++)
            {
                OuterrealmEntry e = cands[i];
                if (e != null && CanMergeInto(e, item))
                {
                    return e;
                }
            }
            return null;
        }

        /// <summary>按 def 粗索引登记条目（Proto.def 为键：CanStackWith 要求 def 相同，粗过滤足够）。</summary>
        private void AddEntry(OuterrealmEntry entry)
        {
            if (entry == null || entry.Proto == null)
            {
                return;
            }
            entries.Add(entry);
            ThingDef def = entry.Proto.def;
            List<OuterrealmEntry> list;
            if (!byDef.TryGetValue(def, out list))
            {
                list = new List<OuterrealmEntry>();
                byDef[def] = list;
            }
            list.Add(entry);
        }

        // ── 查询 ────────────────────────────────────────────────────────────────

        /// <summary>该地图上是否存在已 Spawned 的超维存储仓（决定该地图能否访问全局存储内容）。</summary>
        public bool HasVaultOnMap(Map map)
        {
            if (map == null)
            {
                return false;
            }
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.Spawned && v.Map == map)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>全局层中指定 ThingDef 的总数量（仅统计 Proto 经 GetInnerIfMinified 后 def 匹配的条目，即原始资源类物品）。
        /// 经 totalByDef 缓存 O(1) 查询：version 不变时直接命中，变更后首次调用重建（O(entries)）。</summary>
        public long TotalCountOf(ThingDef def)
        {
            if (def == null)
            {
                return 0L;
            }
            EnsureTotalByDef();
            long total;
            return totalByDef.TryGetValue(def, out total) ? total : 0L;
        }

        /// <summary>按 ThingDef 聚合总量缓存：version 变化（任意数量变更点 version++）后惰性重建。</summary>
        private void EnsureTotalByDef()
        {
            if (totalByDef != null && totalByDefVersion == version)
            {
                return;
            }
            Dictionary<ThingDef, long> dict = new Dictionary<ThingDef, long>();
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry e = entries[i];
                if (e == null || e.Count <= 0)
                {
                    continue;
                }
                // 分组键 Def 与 TotalCountOf 原逐条判断语义一致：
                // MinifiedThing 的 key.Def 已是 InnerThing.def（OuterrealmEntryKey.From 内层化），
                // Corpse 的 key.Def 即 Corpse def（GetInnerIfMinified 返回自身）。
                ThingDef d = e.Key.Def;
                if (d == null)
                {
                    continue;
                }
                long old;
                dict.TryGetValue(d, out old);
                dict[d] = old + e.Count;
            }
            totalByDef = dict;
            totalByDefVersion = version;
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

        /// <summary>按条目扣减全局数量；扣到 0 时移除条目。§B：调用方持有条目引用（视图 GetEntryOf / 动态 FindEntry）。</summary>
        public void Subtract(OuterrealmEntry entry, long amount)
        {
            if (entry == null || amount <= 0)
            {
                if (entry == null && amount > 0)
                {
                    // §B 防御路径：副本→条目映射 miss（stackLimit=1 物品异常场景）时静默不扣账
                    // 会导致物品复制——记录以便定位（正常路径不触发，频率极低）。
                    Log.Warning("[OuterrealmStorage] Subtract with null entry, amount=" + amount + ": copy->entry mapping miss, global count NOT deducted.");
                }
                return;
            }
            if (entry.Count <= 0)
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
                // 与 Withdraw 取空一致：立即通知所有建筑视图移除残留副本（§3.3 同步补到变更点）。
                // Subtract 取空路径包括穿戴取出（RemoveApparel → Notify_ItemRemoved）、整堆移除等，
                // 若不通知，残留副本要等 60 tick 懒同步才清理——窗口期内 JobGiver_OptimizeApparel
                // 会反复选中空条目副本（枚举 GetDirectlyHeldThings 且无冷却）→ Wear job 预留失败 →
                // "Could not reserve ... No existing reserver" 每 30 tick 刷屏。
                NotifyEntriesEmptied(entry);
            }
            version++;
            AddToChangeLog(entry);
        }

        /// <summary>移除条目（计数归零时）。Proto 随之销毁；尸体整体转移路径传 destroyProto=false
        ///（Proto 已交给调用方，仅解除引用，byDef 索引仍正常清理）。</summary>
        private void RemoveEntry(OuterrealmEntry entry, bool destroyProto = true)
        {
            if (entry == null)
            {
                return;
            }
            // 先取 def（byDef 索引键）再销毁 Proto——索引清理依赖 Proto.def。
            ThingDef def = entry.Proto != null ? entry.Proto.def : null;
            if (destroyProto && entry.Proto != null)
            {
                entry.Proto.Destroy();
            }
            entry.Proto = null;
            entries.Remove(entry);
            if (def != null)
            {
                List<OuterrealmEntry> list;
                if (byDef.TryGetValue(def, out list))
                {
                    list.Remove(entry);
                    if (list.Count == 0)
                    {
                        byDef.Remove(def);
                    }
                }
            }
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
                    // 已整体转移给调用方：移除条目但不销毁 Proto（byDef 索引照常清理）
                    RemoveEntry(entry, false);
                    NotifyEntriesEmptied(entry);
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
                    NotifyEntriesEmptied(entry);
                }
            }
            version++;
            AddToChangeLog(entry);
            return t;
        }

        /// <summary>条目被取空后：立即让所有建筑视图移除残留副本（§3.3 同步补到变更点，防空条目副本被自动 job 选中）。</summary>
        private void NotifyEntriesEmptied(OuterrealmEntry entry)
        {
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.view != null)
                {
                    v.view.SyncEntry(entry);
                }
            }
        }

        /// <summary>GeneSetHolderBase.geneSet 字段反射引用（§B 物化基因内容复制的通用补丁；
        /// 静态缓存避免重复反射。字段名为 1.6 反编译一致，null 时跳过复制（防御）。</summary>
        private static readonly System.Reflection.FieldInfo GeneSetField =
            AccessTools.Field(typeof(GeneSetHolderBase), "geneSet");

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
            // §B 基因组修复（通用补丁）：GeneSetHolderBase 是 Genepack / Xenogerm 的公共基类，
            // 基因内容存于 protected geneSet 字段。ThingMaker.MakeThing 会触发 PostMake——
            // Genepack.PostMake 随机生成 geneSet、Xenogerm 的 geneSet 保持 null，物化实例的
            // 基因内容必然与 Proto 不一致（"基因组类型变了"的根因 1）。此处从基类统一复制
            // geneSet（GeneSet.Copy 保留 name），一次覆盖全部子类，不针对具体子类特判。
            // 反射字段引用静态缓存（低频物化路径，避免每次反射查找）。
            GeneSetHolderBase gFrom = from as GeneSetHolderBase;
            GeneSetHolderBase gTo = to as GeneSetHolderBase;
            if (gFrom != null && gTo != null && GeneSetField != null && gFrom.GeneSet != null)
            {
                GeneSetField.SetValue(gTo, gFrom.GeneSet.Copy());
            }
        }

        // ── 变更日志（§3.3 方案 A） ─────────────────────────────────────────────

        /// <summary>内容变更统一入口：版本号 +1 并写入变更日志（所有数量变动点调用）。§B：条目引用。</summary>
        public void NotifyContentChanged(OuterrealmEntry entry)
        {
            version++;
            AddToChangeLog(entry);
        }

        /// <summary>原版 ReservationManager 预留变更通知：版本号 +1（§P0 预订记账优化）。
        /// 由 ReservationManager 的 Reserve/Release/ReleaseAllForTarget/ReleaseClaimedBy/
        /// ReleaseAllClaimedBy 各 patch 在预留增删后调用，使各 vault 视图的预留缓存失效，
        /// 把 AvailableForReserve/PreSplitOff 从"每次全图扫描 reservations"降为 O(1) 查表。
        /// 仅版本号 +1（O(1)），不定位具体副本——失效与重建在视图查询侧惰性完成。</summary>
        public void NotifyReservationChanged()
        {
            reservationVersion++;
        }

        /// <summary>回滚重建条目（TryAbsorbStack 回滚时条目已被取空移除；§3.3 回滚补偿）。</summary>
        public void RestoreEntry(OuterrealmEntry entry)
        {
            if (entry == null || entry.Proto == null || entry.Count <= 0)
            {
                return;
            }
            AddEntry(entry);
            version++;
            AddToChangeLog(entry);
        }

        private void AddToChangeLog(OuterrealmEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            if (changeLogOverflow)
            {
                // 溢出窗口后的首个新变化：重置日志，恢复正常记录。
                changeLogOverflow = false;
                changeLog.Clear();
                changeLogSet.Clear();
            }
            if (changeLogSet.Add(entry))
            {
                changeLog.Add(entry);
                if (changeLog.Count >= ChangeLogCapacity)
                {
                    changeLogOverflow = true;
                    changeLog.Clear();
                    changeLogSet.Clear();
                }
            }
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

        /// <summary>把 count 个条目物化弹出到指定地图（追加到弹出队列，逐 tick 限速执行）。§B：条目引用。</summary>
        public void EnqueueEject(OuterrealmEntry entry, Map map, long count)
        {
            EnqueueEject(entry, map, count, IntVec3.Invalid);
        }

        /// <summary>弹出到指定锚点（§v3 随身弹出：anchor = pawn 位置；Invalid = FindEjectAnchor 默认）。</summary>
        public void EnqueueEject(OuterrealmEntry entry, Map map, long count, IntVec3 anchor)
        {
            if (count <= 0 || map == null || entry == null || entry.Count <= 0)
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
                if (job.Entry == entry && job.MapIndex == mapIndex && job.Anchor == anchor)
                {
                    job.Remaining += count;
                    return;
                }
            }
            ejectQueue.Add(new VaultEjectJob { Entry = entry, MapIndex = mapIndex, Remaining = count, Anchor = anchor });
        }

        /// <summary>每 tick 物化 ≤ 4 堆（每堆 ≤ stackLimit）到目标地图，防瞬间物化爆炸（§6.4/§6.5）。</summary>
        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (ejectQueue.Count > 0)
            {
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
                    OuterrealmEntry entry = job.Entry;
                    if (entry == null || entry.Count <= 0 || entry.Proto == null)
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
                    Thing dropped;
                    // 锚点：随身弹出用 pawn 位置；否则 vault 交互格 / 地图中心。
                    IntVec3 anchor = job.Anchor.IsValid ? job.Anchor : FindEjectAnchor(map);
                    // 放置时排除本系统建筑占位格（建筑 PassThroughOnly，物品可落在建筑格上——需放到建筑外附近）
                    bool placed = GenDrop.TryDropSpawn(t, anchor, map, ThingPlaceMode.Near, out dropped, null, c => !IsVaultCell(c, map));
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
                            Log.Warning("[OuterrealmStorage] 弹出任务连续失败已放弃: " + (job.Entry != null && job.Entry.Proto != null ? job.Entry.Proto.LabelCap : "null"));
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
            } // 弹出队列处理块（§6.4）
            SyncViewsToChangeLog(); // 帧末微批（§3.3 实时同步方案 B）
            MaterializeDirtyViews(); // §filter 视图过滤简化：消费各视图物化脏标记（filter 变更的异步物化）
        }

        /// <summary>
        /// 帧末微批（§3.3 实时同步方案 B）：每帧统一消费变更日志并同步所有 vault 视图，
        /// 替代各建筑 60 tick 懒同步（Building_OuterrealmVault.Tick 已相应精简）。
        /// 视图滞后 ≤1 tick，消除"条目已空但副本残留 60 tick"窗口（OptimizeApparel 等
        /// 选中空条目副本 → 预留失败刷屏的根因）。
        /// 空帧 O(1) 短路（changeLog.Count == 0）；有变更帧 O(变更条目 × vault 数 × SyncEntry)，
        /// SyncEntry 经副本索引（方案 E）为 O(1)。统一消费后清空日志——所有 vault 同帧消费，
        /// 日志不再需要多消费者保留（原 60 tick 各建筑独立消费的机制随之移除）。
        /// </summary>
        private void SyncViewsToChangeLog()
        {
            if (vaults.Count == 0)
            {
                return;
            }
            if (changeLogOverflow)
            {
                // 变更窗口溢出：增量会丢变更，全量重建兜底（AddToChangeLog 溢出后首个新变更会复位溢出标记）
                for (int i = 0; i < vaults.Count; i++)
                {
                    Building_OuterrealmVault v = vaults[i];
                    if (v != null && v.view != null)
                    {
                        v.view.RebuildView();
                    }
                }
                changeLogOverflow = false;
                changeLog.Clear();
                changeLogSet.Clear();
                return;
            }
            if (changeLog.Count == 0)
            {
                return;
            }
            tmpSyncKeys.Clear();
            tmpSyncKeys.AddRange(changeLog);
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v == null || v.view == null)
                {
                    continue;
                }
                for (int j = 0; j < tmpSyncKeys.Count; j++)
                {
                    v.view.SyncEntry(tmpSyncKeys[j]);
                }
            }
            // 统一消费：清空（所有 vault 已同步完毕）
            changeLog.Clear();
            changeLogSet.Clear();
        }

        /// <summary>消费各 vault 视图的物化脏标记（§filter 视图过滤简化）：filter 变更后由建筑
        /// 置脏（O(1)），帧末统一物化缺失的允许条目（O(视图数 × 全局条目数)）——把物化从
        /// UI 点击同步路径移到帧末微批，避免 filter 点击卡顿。空标记帧 O(1) 短路。
        /// 注：暂停时本方法不执行（GameComponentTick 仅运行 tick 调用），新允许条目的可见性
        /// 由 UI 以 CanShow 直接判定（副本未物化时用 proto 渲染），取用路径在运行时才需要副本。</summary>
        private void MaterializeDirtyViews()
        {
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.view != null && v.view.ConsumeMaterializeDirty())
                {
                    v.view.MaterializeMissingCopies();
                }
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
                // 重建 byDef 粗索引 + 从 Proto 重建展示签名（Key）；重置版本号/变更日志
                // （早于建筑 SpawnSetup 的视图重建）。§B：合并不再依赖 Key，仅作展示/统计/放行签名。
                byDef.Clear();
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
                    ThingDef def = e.Proto.def;
                    List<OuterrealmEntry> list;
                    if (!byDef.TryGetValue(def, out list))
                    {
                        list = new List<OuterrealmEntry>();
                        byDef[def] = list;
                    }
                    list.Add(e);
                }
                version = 0;
                reservationVersion = 0;
                totalByDef = null; // 总量缓存失效（version 重置为 0，显式置空避免误命中旧会话缓存）
                totalByDefVersion = -1;
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
        /// <summary>目标条目引用（§B：条目可能被取空移除，引用保留但 Count/Proto 置空，执行时校验跳过）。</summary>
        public OuterrealmEntry Entry;
        public int MapIndex;
        public long Remaining;
        /// <summary>连续放置失败计数（超限放弃任务，防死循环刷屏）。</summary>
        public int FailCount;
        /// <summary>弹出锚点（§v3 随身弹出）：Invalid = 用 FindEjectAnchor（vault 交互格 / 地图中心）。</summary>
        public IntVec3 Anchor = IntVec3.Invalid;
    }
}
