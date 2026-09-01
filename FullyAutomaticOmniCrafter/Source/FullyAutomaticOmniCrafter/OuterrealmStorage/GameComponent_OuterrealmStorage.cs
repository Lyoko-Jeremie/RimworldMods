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

        // ── 存档格式版本 ──────────────────────────────────────────────────────
        // 0/1：旧格式，Proto 是“属性模板”，Count 才是库存真相；Proto.stackCount 可能曾被
        //      数量感知 Boost 临时改大，绝不能反向抬高 Count。
        // 2：权威实例格式，Proto + AdditionalProtos 保存真实物品，Count 是运行时数量缓存。
        // 显式版本是迁移正确性的必要条件：同一个字段在两代格式中语义不同，不能再通过
        // canonical 与 ledger 谁更大来猜测，否则一次读档即可把显示数量永久变成真实库存。
        private const int CurrentStorageSchemaVersion = 2;
        private int storageSchemaVersion = CurrentStorageSchemaVersion;

        // MaterializeProjection 调用 ThingMaker.MakeThing 时，其他 Mod 的全局 Postfix 也会执行。
        // 线程局部深度仅标识“正在制作查询投影”的极短调用窗口，供可选兼容补丁跳过会给投影
        // 随机写入业务数据的逻辑。RimWorld 1.6 的加载线程可能不是主线程，故不能使用普通静态 bool。
        [ThreadStatic]
        private static int projectionMaterializationDepth;

        public static bool ProjectionMaterializationActive => projectionMaterializationDepth > 0;

        private List<OuterrealmEntry> entries = new List<OuterrealmEntry>();
        // §B 设计（CanStackWith 动态分组）：条目不再按 OuterrealmEntryKey 哈希索引——
        // 合并判据 = 原版 Thing.CanStackWith + def.stackLimit > 1（原版容量语义：stackLimit=1
        // 的基因组/异种胚芽等"唯一实体"类物品永不合并，每实例独立条目，内容不丢失）。
        // 查找经 byDef 粗索引（def 不同必不能堆叠）缩小候选后线性 CanStackWith 判定（O(同 def 条目数)）。
        private Dictionary<ThingDef, List<OuterrealmEntry>> byDef = new Dictionary<ThingDef, List<OuterrealmEntry>>();

        /// <summary>权威 Thing → 条目。供随身访问把全局实例作为只读候选，并在预约成功时 O(1) 结账。</summary>
        private readonly Dictionary<Thing, OuterrealmEntry> entryByCanonical = new Dictionary<Thing, OuterrealmEntry>();

        /// <summary>资源栏物品的增量总量。只含 CountAsResource 的内层 ThingDef，避免每 204 tick 扫描全部条目。</summary>
        private readonly Dictionary<ThingDef, long> resourceTotals = new Dictionary<ThingDef, long>();

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
        private readonly Dictionary<Map, int> vaultCountByMap = new Dictionary<Map, int>();

        // ── 管理器视图筛选（§6.4 UI 层） ──
        // 旧版按分类保存继承式状态，会令多分类物品抵消分类点击，现仅用于旧存档迁移。
        private Dictionary<string, bool> managerCatShow = new Dictionary<string, bool>();
        // 原版 ThingFilter 的核心语义是“每个 ThingDef 有独立允许状态”；这里以稀疏字典只记录
        // 被隐藏的 ThingDef（缺失 = 默认显示），避免序列化全部已允许 Def。分类点击与原版
        // ThingFilter.SetAllow(ThingCategoryDef) 相同，遍历 DescendantThingDefs 统一写入。
        private Dictionary<string, bool> managerThingShow;

        /// <summary>
        /// 是否让轨道商人无需信标、地图或终端权限即可直接枚举全部全局库存。
        /// 这是殖民地级规则，随当前存档保存；默认关闭以保持旧存档行为。
        /// </summary>
        private bool exposeAllToOrbitalTrade;

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
        public Dictionary<ThingDef, long> ResourceTotalsForReading => resourceTotals;
        public bool ExposeAllToOrbitalTrade
        {
            get => exposeAllToOrbitalTrade;
            set => exposeAllToOrbitalTrade = value;
        }

        public GameComponent_OuterrealmStorage(Game game)
        {
            Instance = this;
            SubspaceAccessUtility.ResetRuntimeState();
            OuterrealmIdentityRouting.ResetRuntimeState();
        }

        // ── 存入 ────────────────────────────────────────────────────────────────

        /// <summary>吸收存入（§3.2 吸收路径的全局侧）：把 item 的全部 stackCount 并入对应条目。
        /// Spawned 物品（如弹出后又被搬回/存回的尸体）先 DeSpawn——存入超维空间 = 从地图取出，
        /// 否则 proto 保持 Spawned，再次弹出时 GenSpawn 会报 "already spawned" 并死循环。
        /// §B：合并判据 = 原版 CanStackWith + stackLimit>1（FindEntry 动态判定）；不满足则新建条目。
        /// 返回条目（新增或已有），供视图层物化/更新副本。</summary>
        public OuterrealmEntry Deposit(Thing item, Building_OuterrealmVault preferredHome = null)
        {
            if (item == null || item.stackCount <= 0)
            {
                return null;
            }
            if (OuterrealmVaultUtil.IsProjection(item))
            {
                // 投影的 stackCount 只是查询/显示数量，把它存回权威层等价于复制物品。
                // 必须在 DeSpawn 前拒绝，因为第三方可能已经把投影从 view 移除，此时仅检查
                // holdingOwner 已无法区分投影与真实物品。
                Log.ErrorOnce("[OuterrealmStorage] Rejected an attempt to deposit a query projection as canonical inventory: "
                    + item.ToStringSafe(), 0x4F535052 ^ item.thingIDNumber);
                return null;
            }
            // 权威身份锚点是伪 Spawned 查询对象，不在 thingGrid。先走专用注销，禁止把它
            // 交给原版 DeSpawn 的完整地图注销链。
            OuterrealmIdentityRouting.DetachAnchorForDeposit(item);
            if (item.Spawned)
            {
                item.DeSpawn(DestroyMode.Vanish);
            }
            ThingDef resourceDef = ResourceDefOf(item);
            OuterrealmEntry existing = FindEntry(item);
            if (existing != null)
            {
                int depositedCount = item.stackCount;
                // 权威库存必须走原版堆叠协议：TryAbsorbStack 会调用各 Comp 的
                // PreAbsorbStack；超出 int 容量时保留为附加权威堆。禁止只加 Count 后
                // Destroy 原物，否则腐烂、原料、充能及第三方 Comp 状态会静默丢失。
                MergeIntoCanonicalStacks(existing, item);
                existing.Count += depositedCount;
                AdjustResourceTotal(resourceDef, depositedCount);
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
            OuterrealmIdentityRouting.OnEntryAdded(entry, item, preferredHome);
            AdjustResourceTotal(resourceDef, entry.Count);
            version++;
            AddToChangeLog(entry);
            return entry;
        }

        /// <summary>
        /// 把真实物品并入条目的权威堆。每个权威 Thing 的 stackCount 不超过 int.MaxValue；
        /// 合并和必要的部分拆分均调用原版 TryAbsorbStack/SplitOff，让原版及第三方 Comp
        /// 通过 PreAbsorbStack/PostSplitOff 维护自身状态。
        /// </summary>
        private void MergeIntoCanonicalStacks(OuterrealmEntry entry, Thing item)
        {
            if (entry == null || item == null || item.Destroyed || item.stackCount <= 0)
            {
                return;
            }
            if (entry.Proto == null)
            {
                entry.Proto = item;
                entryByCanonical[item] = entry;
                return;
            }
            if (TryMergeIntoCanonical(entry.Proto, item))
            {
                return;
            }
            List<Thing> extras = entry.AdditionalProtos;
            if (extras != null)
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    Thing dst = extras[i];
                    if (dst != null && !dst.Destroyed && TryMergeIntoCanonical(dst, item))
                    {
                        return;
                    }
                }
            }
            if (item.Destroyed || item.stackCount <= 0)
            {
                return;
            }
            if (entry.AdditionalProtos == null)
            {
                entry.AdditionalProtos = new List<Thing>();
            }
            entry.AdditionalProtos.Add(item);
            entryByCanonical[item] = entry;
        }

        /// <summary>尽量把 source 并入 destination；返回 source 是否已全部吸收。</summary>
        private static bool TryMergeIntoCanonical(Thing destination, Thing source)
        {
            if (destination == null || source == null || destination.Destroyed || source.Destroyed
                || source.stackCount <= 0 || !destination.CanStackWith(source))
            {
                return false;
            }
            int capacity = int.MaxValue - destination.stackCount;
            if (capacity <= 0)
            {
                return false;
            }
            if (source.stackCount <= capacity)
            {
                destination.TryAbsorbStack(source, false);
                return source.Destroyed || source.stackCount <= 0;
            }
            // source 超过当前权威堆的 int 容量：先按原版协议切出可容纳部分。
            Thing piece = source.SplitOff(capacity);
            destination.TryAbsorbStack(piece, false);
            return source.Destroyed || source.stackCount <= 0;
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
            // 原版不可堆叠物品每个实例独立成条目。提前返回避免基因组、武器等
            // stackLimit=1 物品随库存增长产生同 Def 线性扫描，连续存入退化为 O(n²)。
            if (item.def.stackLimit <= 1)
            {
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
            RegisterCanonicalThings(entry);
        }

        /// <summary>O(1) 判断 Thing 是否为仍在超维空间内的权威实例。</summary>
        public bool TryGetCanonicalEntry(Thing thing, out OuterrealmEntry entry)
        {
            if (thing != null && entryByCanonical.TryGetValue(thing, out entry)
                && entry != null && entry.Count > 0 && !thing.Destroyed)
            {
                return true;
            }
            entry = null;
            return false;
        }

        /// <summary>
        /// 按持久 ThingID 查找权威实例。仅用于读档兼容：旧版曾把唯一物品权威锚点同时
        /// 深保存到全局条目与 Map.things，加载时需要在注册交叉引用及 Spawn 地图物品前
        /// 精确识别后者。该路径只在读档阶段调用，避免为正常运行额外维护一份 ID 索引。
        /// </summary>
        public bool TryGetCanonicalThingById(int thingIdNumber, out Thing canonical)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                Thing proto = entry?.Proto;
                if (proto != null && proto.thingIDNumber == thingIdNumber)
                {
                    canonical = proto;
                    return true;
                }
                List<Thing> extras = entry?.AdditionalProtos;
                if (extras == null)
                {
                    continue;
                }
                for (int j = 0; j < extras.Count; j++)
                {
                    Thing extra = extras[j];
                    if (extra != null && extra.thingIDNumber == thingIdNumber)
                    {
                        canonical = extra;
                        return true;
                    }
                }
            }
            canonical = null;
            return false;
        }

        /// <summary>按 Def 返回条目粗索引，只读借用；搜索期唯一锚点解析避免扫描全部库存。</summary>
        public List<OuterrealmEntry> EntriesOfDefForReading(ThingDef def)
        {
            List<OuterrealmEntry> list;
            return def != null && byDef.TryGetValue(def, out list) ? list : null;
        }

        private void RegisterCanonicalThings(OuterrealmEntry entry)
        {
            if (entry?.Proto != null && !entry.Proto.Destroyed)
            {
                entryByCanonical[entry.Proto] = entry;
            }
            List<Thing> extras = entry?.AdditionalProtos;
            if (extras == null)
            {
                return;
            }
            for (int i = 0; i < extras.Count; i++)
            {
                Thing extra = extras[i];
                if (extra != null && !extra.Destroyed)
                {
                    entryByCanonical[extra] = entry;
                }
            }
        }

        private static ThingDef ResourceDefOf(Thing thing)
        {
            Thing inner = thing?.GetInnerIfMinified();
            ThingDef def = inner?.def;
            return def != null && def.CountAsResource ? def : null;
        }

        private void AdjustResourceTotal(ThingDef def, long delta)
        {
            if (def == null || delta == 0L)
            {
                return;
            }
            long current;
            resourceTotals.TryGetValue(def, out current);
            long next;
            if (delta > 0L && current > long.MaxValue - delta)
            {
                next = long.MaxValue;
            }
            else
            {
                next = current + delta;
            }
            if (next > 0L)
            {
                resourceTotals[def] = next;
            }
            else
            {
                resourceTotals.Remove(def);
            }
        }

        // ── 查询 ────────────────────────────────────────────────────────────────

        /// <summary>管理器视图筛选：等价于原版 ThingFilter.Allows(ThingDef)。</summary>
        public bool ManagerAllows(ThingDef def)
        {
            if (def == null)
            {
                return true;
            }
            EnsureManagerThingShow();
            bool show;
            return !managerThingShow.TryGetValue(def.defName, out show) || show;
        }

        /// <summary>设置管理器视图筛选：逐个修改该分类的 DescendantThingDefs，完整复刻原版
        /// ThingFilter.SetAllow(ThingCategoryDef) 的传播范围。~（混合）点击后由 CheckboxMulti
        /// 转为隐藏，再点转为显示；多分类 ThingDef 也只有一个直接状态，不会互相抵消。
        /// 仅影响管理器右侧列表显示，不改内容版本号（不触发建筑视图同步）。</summary>
        public void SetManagerCatShow(ThingCategoryDef c, bool show)
        {
            if (c == null)
            {
                return;
            }
            EnsureManagerThingShow();
            foreach (ThingDef def in c.DescendantThingDefs)
            {
                if (show)
                {
                    managerThingShow.Remove(def.defName);
                }
                else
                {
                    managerThingShow[def.defName] = false;
                }
            }
        }

        private void EnsureManagerThingShow()
        {
            if (managerThingShow == null)
            {
                managerThingShow = new Dictionary<string, bool>();
            }
        }

        /// <summary>把旧版“分类继承 + 多分类 OR”状态一次性折算为逐 ThingDef 状态，
        /// 保证升级存档后当前可见/隐藏结果不突变；之后所有交互均使用原版 ThingFilter 模型。</summary>
        private void MigrateLegacyManagerCategoryFilter()
        {
            if (managerCatShow == null || managerCatShow.Count == 0)
            {
                return;
            }
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                if (!LegacyManagerAllows(def))
                {
                    managerThingShow[def.defName] = false;
                }
            }
        }

        private bool LegacyManagerAllows(ThingDef def)
        {
            if (def == null || def.thingCategories == null || def.thingCategories.Count == 0)
            {
                return true;
            }
            for (int i = 0; i < def.thingCategories.Count; i++)
            {
                ThingCategoryDef category = def.thingCategories[i];
                while (category != null)
                {
                    bool show;
                    if (managerCatShow.TryGetValue(category.defName, out show))
                    {
                        if (show)
                        {
                            return true;
                        }
                        break;
                    }
                    category = category.parent;
                }
                if (category == null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>该地图上是否存在已 Spawned 的超维存储仓（决定该地图能否访问全局存储内容）。</summary>
        public bool HasVaultOnMap(Map map)
        {
            if (map == null)
            {
                return false;
            }
            int count;
            return vaultCountByMap.TryGetValue(map, out count) && count > 0;
        }

        /// <summary>终端 filter/出入/冻结状态变化后重算唯一物品默认锚点与临时出口。</summary>
        public void NotifyIdentityRoutingChanged(Building_OuterrealmVault vault)
        {
            OuterrealmIdentityRouting.OnVaultSettingsChanged(vault);
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
            ThingDef resourceDef = ResourceDefOf(entry.Proto);
            long requested = Math.Min(amount, entry.Count);
            long removed = 0L;
            while (removed < requested)
            {
                int part = (int)Math.Min(requested - removed, int.MaxValue);
                Thing canonical = TakeFromCanonicalStacks(entry, part);
                if (canonical == null || canonical.Destroyed || canonical.stackCount <= 0)
                {
                    break;
                }
                removed += canonical.stackCount;
                canonical.Destroy();
            }
            if (removed <= 0)
            {
                Log.Error("[OuterrealmStorage] Failed to subtract canonical quantity for " + entry.Key
                    + ": requested=" + requested + ".");
                return;
            }
            entry.Count -= removed;
            AdjustResourceTotal(resourceDef, -removed);
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

        /// <summary>移除条目（计数归零时）。默认销毁仍留在条目中的全部权威堆；
        /// 整体转移路径传 destroyProto=false（权威物已交给调用方，仅解除引用）。</summary>
        private void RemoveEntry(OuterrealmEntry entry, bool destroyProto = true)
        {
            if (entry == null)
            {
                return;
            }
            // 先注销唯一物品的权威查询锚点，再销毁/转移 Proto。
            OuterrealmIdentityRouting.OnEntryRemoving(entry);
            // 先取 def（byDef 索引键）再销毁 Proto——索引清理依赖 Proto.def。
            ThingDef def = entry.Proto != null ? entry.Proto.def : entry.Key.Def;
            if (entry.Proto != null)
            {
                entryByCanonical.Remove(entry.Proto);
            }
            if (destroyProto && entry.Proto != null)
            {
                entry.Proto.Destroy();
            }
            if (entry.AdditionalProtos != null)
            {
                if (destroyProto)
                {
                    for (int i = 0; i < entry.AdditionalProtos.Count; i++)
                    {
                        Thing extra = entry.AdditionalProtos[i];
                        if (extra != null)
                        {
                            entryByCanonical.Remove(extra);
                        }
                        if (extra != null && !extra.Destroyed)
                        {
                            extra.Destroy();
                        }
                    }
                }
                entry.AdditionalProtos.Clear();
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
        /// 从全局层取出 count 个：转移权威原物，或从权威堆调用原版 SplitOff 后扣减全局。
        /// 返回实例由调用方负责放置（GenDrop.TryDropSpawn / carryTracker 等）。
        /// 不再调用 Materialize，因此 Thing 子类字段、所有 Comp 状态、物品身份及外部引用均保留。
        /// 条目被取空后立即通知所有建筑视图移除残留副本（防 OptimizeApparel 等选中空条目副本 →
        /// 预留失败 → "TryMakePreToilReservations returned false" 警告 + pawn 卡住循环）。
        /// </summary>
        public Thing Withdraw(OuterrealmEntry entry, int count)
        {
            if (entry == null || count <= 0 || entry.Count <= 0)
            {
                return null;
            }
            ThingDef resourceDef = ResourceDefOf(entry.Proto);
            OuterrealmIdentityRouting.PrepareCheckout(entry);
            long take = count;
            if (take > entry.Count)
            {
                take = entry.Count;
            }
            Thing t = TakeFromCanonicalStacks(entry, (int)take);
            if (t == null || t.Destroyed || t.stackCount <= 0)
            {
                OuterrealmIdentityRouting.CancelCheckout(entry);
                Log.Error("[OuterrealmStorage] Canonical inventory is inconsistent for " + entry.Key
                    + ": requested=" + take + ", count=" + entry.Count + ".");
                return null;
            }
            OuterrealmIdentityRouting.RememberHomeForCheckout(entry, t);
            entry.Count -= t.stackCount;
            AdjustResourceTotal(resourceDef, -t.stackCount);
            if (entry.Count < 0)
            {
                entry.Count = 0;
            }
            if (entry.Count == 0)
            {
                // TakeFromCanonicalStacks 已把交给调用方的权威实例从条目解除；正常情况下
                // 不再有剩余权威堆。destroyProto=true 仅清理不一致的账外残留，绝不销毁返回物。
                RemoveEntry(entry);
                NotifyEntriesEmptied(entry);
            }
            version++;
            AddToChangeLog(entry);
            return t;
        }

        /// <summary>从条目的权威堆取出最多 count 个，并尽量合并成单个返回堆。</summary>
        private Thing TakeFromCanonicalStacks(OuterrealmEntry entry, int count)
        {
            Thing result = null;
            int remaining = count;
            while (remaining > 0)
            {
                Thing source = entry.Proto;
                if (source == null || source.Destroyed || source.stackCount <= 0)
                {
                    PromoteNextCanonical(entry);
                    source = entry.Proto;
                    if (source == null || source.Destroyed || source.stackCount <= 0)
                    {
                        break;
                    }
                }
                int partCount = Math.Min(remaining, source.stackCount);
                Thing part;
                if (partCount >= source.stackCount)
                {
                    part = source;
                    DetachPrimaryCanonical(entry);
                }
                else
                {
                    // 原版 ThingWithComps.SplitOff 会调用每个 Comp.PostSplitOff。
                    part = source.SplitOff(partCount);
                }
                if (part == null || part.Destroyed || part.stackCount <= 0)
                {
                    break;
                }
                if (result == null)
                {
                    result = part;
                }
                else if (!result.TryAbsorbStack(part, false))
                {
                    // 同一条目的权威堆理论上必可合并；失败时把本次 part 放回条目，
                    // 返回已成功取得的部分，避免丢物。
                    MergeIntoCanonicalStacks(entry, part);
                    break;
                }
                remaining -= partCount;
            }
            return result;
        }

        /// <summary>解除第一权威堆并把附加堆首项提升为 Proto。</summary>
        private void DetachPrimaryCanonical(OuterrealmEntry entry)
        {
            if (entry.Proto != null)
            {
                entryByCanonical.Remove(entry.Proto);
            }
            entry.Proto = null;
            PromoteNextCanonical(entry);
        }

        private static void PromoteNextCanonical(OuterrealmEntry entry)
        {
            List<Thing> extras = entry.AdditionalProtos;
            if (extras == null)
            {
                return;
            }
            while (extras.Count > 0)
            {
                Thing next = extras[0];
                extras.RemoveAt(0);
                if (next != null && !next.Destroyed && next.stackCount > 0)
                {
                    entry.Proto = next;
                    return;
                }
            }
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

        /// <summary>
        /// 创建不拥有库存数量的查询投影。投影必须保持原物 Thing 类型，才能兼容原版和第三方
        /// 对 def、Comp、食物、装备等属性的筛选；但它永远不能被 Deposit 当作真实物品。
        /// 创建作用域还允许 Common Sense 等对 ThingMaker 的全局补丁识别并跳过“随机补业务数据”。
        /// </summary>
        public static Thing MaterializeProjection(Thing proto)
        {
            projectionMaterializationDepth++;
            try
            {
                Thing projection = Materialize(proto);
                OuterrealmVaultUtil.MarkProjection(projection);
                return projection;
            }
            finally
            {
                projectionMaterializationDepth--;
            }
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
            // 回滚条目的 Count 已由调用方保存，必须恢复到这个精确数量；不能用临时权威堆
            // 的偶发 stackCount 反向改写账目。复用旧格式规范化函数可同时补足或裁掉差额。
            MigrateLegacyCanonicalQuantity(entry);
            AddEntry(entry);
            AdjustResourceTotal(ResourceDefOf(entry.Proto), entry.Count);
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
                // 溢出状态必须一直保留到所有视图完成一次全量重建；否则溢出后、
                // 帧末消费前的下一次变化会错误清除标记，永久漏掉早期变更。
                return;
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
            if (vault != null && !vaults.Contains(vault))
            {
                vaults.Add(vault);
                Map map = vault.Map;
                if (vault.Spawned && map != null)
                {
                    int count;
                    vaultCountByMap.TryGetValue(map, out count);
                    vaultCountByMap[map] = count + 1;
                }
                OuterrealmIdentityRouting.OnVaultRegistered(vault);
            }
        }

        public void UnregisterVault(Building_OuterrealmVault vault)
        {
            if (vault == null || !vaults.Remove(vault))
            {
                return;
            }
            Map map = vault.Map;
            int count;
            if (map != null && vaultCountByMap.TryGetValue(map, out count))
            {
                if (count <= 1)
                {
                    vaultCountByMap.Remove(map);
                }
                else
                {
                    vaultCountByMap[map] = count - 1;
                }
            }
            OuterrealmIdentityRouting.OnVaultUnregistered(vault);
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
            OuterrealmIdentityRouting.Tick();
            // 正常取物会在进入 carry 时即时解除跟踪；每 60 tick 只扫描极小的“已物化但尚未取走”集合，
            // 回收被取消/中断 Job 遗留的真实物品，不与全局库存规模相关。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                SubspaceAccessUtility.ReturnUnreservedPending();
            }
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
                // 变更窗口溢出：增量会丢变更，全量重建完成后才解除溢出状态。
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

        /// <summary>
        /// 统计条目内仍然存在的权威实例数量，并顺便清理 null、已销毁或空的附加堆。
        /// 本方法只回答“实例实际代表多少”，不决定该数量是否可信；旧格式由 Count 主导迁移，
        /// 新格式则由本统计结果反向重建 Count，二者通过显式 schema 分流，禁止互相猜测。
        /// </summary>
        private static long RepresentedCanonicalQuantity(OuterrealmEntry entry)
        {
            if (entry == null || entry.Proto == null)
            {
                return 0L;
            }
            long represented = entry.Proto.Destroyed || entry.Proto.stackCount <= 0
                ? 0L
                : entry.Proto.stackCount;
            if (entry.AdditionalProtos != null)
            {
                for (int i = entry.AdditionalProtos.Count - 1; i >= 0; i--)
                {
                    Thing extra = entry.AdditionalProtos[i];
                    if (extra == null || extra.Destroyed || extra.stackCount <= 0)
                    {
                        entry.AdditionalProtos.RemoveAt(i);
                    }
                    else
                    {
                        represented += extra.stackCount;
                    }
                }
            }
            return represented;
        }

        /// <summary>
        /// 旧格式迁移：旧 Count 是唯一可信数量，Proto 只是属性模板。若模板曾被 Boost 导致
        /// stackCount 大于 Count，必须裁掉多余模板数量，绝不能把 Count 抬高；若模板数量不足，
        /// 再按旧 Count 补齐权威堆。该迁移只在 schema &lt; 2 时执行一次。
        /// </summary>
        private static void MigrateLegacyCanonicalQuantity(OuterrealmEntry entry)
        {
            if (entry == null || entry.Proto == null || entry.Count <= 0)
            {
                return;
            }
            long represented = RepresentedCanonicalQuantity(entry);
            if (represented > entry.Count)
            {
                // 这是旧格式中 Boost 可能留下的预期迁移输入，不属于运行时故障；静默裁回账本数量，
                // 避免玩家每次首次加载旧档仍收到一条“已知且已自动修复”的黄色警告。
                TrimCanonicalQuantity(entry, represented - entry.Count);
                represented = RepresentedCanonicalQuantity(entry);
            }
            long missing = entry.Count - represented;
            if (missing <= 0)
            {
                return;
            }
            int primaryCapacity = int.MaxValue - entry.Proto.stackCount;
            if (primaryCapacity > 0)
            {
                int add = (int)Math.Min(missing, primaryCapacity);
                entry.Proto.stackCount += add;
                missing -= add;
            }
            while (missing > 0)
            {
                int amount = (int)Math.Min(missing, int.MaxValue);
                Thing migrated = Materialize(entry.Proto);
                migrated.stackCount = amount;
                if (entry.AdditionalProtos == null)
                {
                    entry.AdditionalProtos = new List<Thing>();
                }
                entry.AdditionalProtos.Add(migrated);
                missing -= amount;
            }
        }

        /// <summary>从附加堆尾部开始裁剪旧模板的账外数量，最后才裁主堆，尽量保留主实例及其 Comp 状态。</summary>
        private static void TrimCanonicalQuantity(OuterrealmEntry entry, long excess)
        {
            List<Thing> extras = entry.AdditionalProtos;
            if (extras != null)
            {
                for (int i = extras.Count - 1; i >= 0 && excess > 0; i--)
                {
                    Thing extra = extras[i];
                    if (extra == null || extra.Destroyed || extra.stackCount <= 0)
                    {
                        extras.RemoveAt(i);
                        continue;
                    }
                    int remove = (int)Math.Min(excess, extra.stackCount);
                    if (remove >= extra.stackCount)
                    {
                        excess -= extra.stackCount;
                        extras.RemoveAt(i);
                        extra.Destroy();
                    }
                    else
                    {
                        // 迁移裁剪的是旧“模板数量”，不是一次游戏内拆堆操作；直接规范化数字可避免
                        // ThingMaker/PostSplitOff 等第三方业务补丁把迁移误认为生成了真实物品。
                        extra.stackCount -= remove;
                        excess -= remove;
                    }
                }
            }
            if (excess <= 0 || entry.Proto == null || entry.Proto.Destroyed)
            {
                return;
            }
            int primaryRemove = (int)Math.Min(excess, Math.Max(0, entry.Proto.stackCount - 1));
            if (primaryRemove > 0)
            {
                entry.Proto.stackCount -= primaryRemove;
            }
        }

        /// <summary>
        /// 新格式读档：权威实例是唯一真相，Count 只从权威堆重建，不再在两份可写状态之间猜测。
        /// 正常保存中两者应相等；不相等说明旧运行时曾中途退出或有第三方直接改写权威实例，
        /// 记录一次诊断后使用实际仍存在的权威物数量。
        /// </summary>
        private static void RebuildCountFromCanonical(OuterrealmEntry entry)
        {
            long represented = RepresentedCanonicalQuantity(entry);
            if (represented != entry.Count)
            {
                Log.Warning("[OuterrealmStorage] Rebuilt cached count from canonical inventory for "
                    + (entry.Proto?.def != null ? entry.Proto.def.defName : "null")
                    + ": canonical=" + represented + ", cached=" + entry.Count + ".");
                entry.Count = represented;
            }
        }

        /// <summary>
        /// 清理旧版锚点重复保存后又被 vault 吸收并再次保存的状态。此时两个唯一条目持有
        /// 不同对象引用但相同 ThingID；交叉引用目录已经统一保留首次出现的权威实例，故此处
        /// 同样保留首条并移除后续条目。正常游戏中 ThingID 全局唯一，精确重复不可能代表两件合法物品。
        /// </summary>
        private int RemoveDuplicateUniqueCanonicalIdsAfterLoad()
        {
            HashSet<int> seen = new HashSet<int>();
            int removed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                Thing proto = entry?.Proto;
                if (proto?.def == null || proto.def.category != ThingCategory.Item
                    || proto.def.stackLimit > 1 || proto is Corpse)
                {
                    continue;
                }
                if (seen.Add(proto.thingIDNumber))
                {
                    continue;
                }
                entries.RemoveAt(i);
                i--;
                removed++;
            }
            return removed;
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref storageSchemaVersion, "storageSchemaVersion", 0);
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            // managerCatShow 仅为读取旧版存档保留；新逻辑保存逐 ThingDef 的稀疏状态。
            Scribe_Collections.Look(ref managerCatShow, "managerCatShow", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref managerThingShow, "managerThingShow", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref exposeAllToOrbitalTrade, "exposeAllToOrbitalTrade", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (managerCatShow == null)
                {
                    managerCatShow = new Dictionary<string, bool>();
                }
                if (managerThingShow == null)
                {
                    managerThingShow = new Dictionary<string, bool>();
                    MigrateLegacyManagerCategoryFilter();
                }
                managerCatShow.Clear();

                // 清理管理器视图筛选中已不存在的 ThingDef（mod 卸载/改名残留），避免悬挂 defName。
                List<string> staleKeys = null;
                foreach (KeyValuePair<string, bool> kv in managerThingShow)
                {
                    if (DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key) == null)
                    {
                        if (staleKeys == null)
                        {
                            staleKeys = new List<string>();
                        }
                        staleKeys.Add(kv.Key);
                    }
                }
                if (staleKeys != null)
                {
                    for (int i = 0; i < staleKeys.Count; i++)
                    {
                        managerThingShow.Remove(staleKeys[i]);
                    }
                }
                if (entries == null)
                {
                    entries = new List<OuterrealmEntry>();
                }
                int removedDuplicateCanonicals = RemoveDuplicateUniqueCanonicalIdsAfterLoad();
                if (removedDuplicateCanonicals > 0)
                {
                    Log.Warning("[OuterrealmStorage] Recovered " + removedDuplicateCanonicals
                        + " duplicated unique canonical item(s) from an old save. Save the game again "
                        + "to permanently clean the file.");
                }
                // 重建 byDef 粗索引 + 从 Proto 重建展示签名（Key）；重置版本号/变更日志
                // （早于建筑 SpawnSetup 的视图重建）。§B：合并不再依赖 Key，仅作展示/统计/放行签名。
                byDef.Clear();
                entryByCanonical.Clear();
                resourceTotals.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry e = entries[i];
                    if (e == null || e.Proto == null)
                    {
                        entries.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (storageSchemaVersion < CurrentStorageSchemaVersion)
                    {
                        MigrateLegacyCanonicalQuantity(e);
                    }
                    else
                    {
                        RebuildCountFromCanonical(e);
                    }
                    if (e.Count <= 0 || e.Proto.Destroyed)
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
                    RegisterCanonicalThings(e);
                    AdjustResourceTotal(ResourceDefOf(e.Proto), e.Count);
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
                vaultCountByMap.Clear();
                SubspaceAccessUtility.ResetRuntimeState();
                OuterrealmIdentityRouting.ResetRuntimeState();
                // PostLoadInit 完成后内存状态已经是当前格式；下一次保存会写出显式版本。
                storageSchemaVersion = CurrentStorageSchemaVersion;
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
