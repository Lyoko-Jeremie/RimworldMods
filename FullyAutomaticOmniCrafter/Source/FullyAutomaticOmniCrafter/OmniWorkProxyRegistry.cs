using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站代理的全局管理组件。
    ///
    /// 归属语义：代理属于"创建它的那台地图的池"，与它当前所在的图无关。跨图工作期间归属不变，
    /// 工作结束后由归属图回收。归属表随存档保存（Scribe_Collections.Look + IExposable），
    /// 因此驻外代理读档后仍能回到原池。
    ///
    /// 实例获取：Instance 在构造函数中赋值。**不可等 FinalizeInit** —— 建筑的 SpawnSetup
    /// 早于 GameComponent.FinalizeInit，而 SpawnSetup 阶段就需要查询归属。
    ///
    /// 注意：代理的 def 是原版 Human（kindDef 才是 FAOC_OmniWorkProxy），因此无法给代理挂
    /// 专属 ThingComp（会给所有人类挂上）；归属只能由本组件统一持表。
    /// </summary>
    public sealed class GameComponent_OmniWorkProxyRegistry : GameComponent
    {
        /// <summary>一个代理的归属记录（随存档保存）。</summary>
        public sealed class ProxyHomeRecord : IExposable
        {
            public Pawn pawn;
            public Map homeMap;          // 归属池所在图（= 创建代理时工作站所在的图）
            public int stationThingId = -1;
            public Map abroadMap;        // 当前驻外图（null 表示正在归属图）
            public bool inContainer;     // 驻外期间被某个 IThingHolder 接住（车辆 / 门户 / 平台）
            public int abroadSinceTick = -1;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_References.Look(ref homeMap, "homeMap");
                Scribe_Values.Look(ref stationThingId, "stationThingId", -1);
                Scribe_References.Look(ref abroadMap, "abroadMap");
                Scribe_Values.Look(ref inContainer, "inContainer", false);
                Scribe_Values.Look(ref abroadSinceTick, "abroadSinceTick", -1);
            }
        }

        private List<ProxyHomeRecord> records = new List<ProxyHomeRecord>();
        private readonly Dictionary<Pawn, ProxyHomeRecord> byPawn = new Dictionary<Pawn, ProxyHomeRecord>();

        // 复用列表：这些接口都可能被每 60 tick 的维护周期调用，避免反复分配。
        private readonly List<Pawn> pawnScratch = new List<Pawn>();
        private readonly List<Map> poolScratch = new List<Map>();

        private static GameComponent_OmniWorkProxyRegistry instance;

        /// <summary>全局总闸：false 时所有池停止派发并把场上代理收回休眠舱（不销毁）。随存档保存。</summary>
        private bool globalWorkEnabled = true;

        public bool GlobalWorkEnabled => globalWorkEnabled;

        /// <summary>
        /// 代理状态悬浮监视面板是否可见。**存档级**状态：新存档与缺该字段的老存档一律默认关闭，
        /// 玩家手动打开后随存档保存。此前它存放在全局 Mod 配置（OmniCrafterSettings）里，
        /// 结果是"开过一次之后每个存档都会自动弹出来"。面板的位置与尺寸仍留在全局配置中共享。
        /// </summary>
        private bool monitorVisible;

        public bool MonitorVisible
        {
            get => monitorVisible;
            set => monitorVisible = value;
        }

        public List<Pawn> Scratch => pawnScratch;

        public static GameComponent_OmniWorkProxyRegistry Instance
        {
            get
            {
                if (instance == null && Current.Game != null)
                    instance = Current.Game.GetComponent<GameComponent_OmniWorkProxyRegistry>();
                return instance;
            }
        }

        public GameComponent_OmniWorkProxyRegistry(Game game)
        {
            instance = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            instance = this;
            RebuildIndex();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref records, "proxyHomes", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (records == null) records = new List<ProxyHomeRecord>();
                RebuildIndex();
            }
            Scribe_Values.Look(ref globalWorkEnabled, "globalWorkEnabled", true);
            // 监视面板可见性：存档级，默认关闭（缺字段的老存档读入后也是关闭）。
            Scribe_Values.Look(ref monitorVisible, "monitorVisible", false);
        }

        private void RebuildIndex()
        {
            byPawn.Clear();
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ProxyHomeRecord record = records[i];
                if (record == null || record.pawn == null || record.pawn.Destroyed)
                {
                    records.RemoveAt(i);
                    continue;
                }
                byPawn[record.pawn] = record;
            }
        }

        // ── 查询 ────────────────────────────────────────────────────────────────

        public bool TryGetHome(Pawn pawn, out ProxyHomeRecord record)
        {
            record = null;
            return pawn != null && byPawn.TryGetValue(pawn, out record);
        }

        public bool IsRegistered(Pawn pawn)
        {
            return pawn != null && byPawn.ContainsKey(pawn);
        }

        /// <summary>代理的归属图；未登记时退化为"当前所在图"，保证无第三方 Mod 时的行为与改动前一致。</summary>
        public static Map HomeMapOf(Pawn pawn)
        {
            GameComponent_OmniWorkProxyRegistry reg = Instance;
            if (reg != null && pawn != null && reg.byPawn.TryGetValue(pawn, out ProxyHomeRecord record))
                return record.homeMap;
            return pawn?.Map;
        }

        /// <summary>代理的归属图组件（所有"我方管理动作"都必须走它，而不是代理当前所在图的组件）。</summary>
        public static MapComponent_OmniWorkstation ManagerOf(Pawn pawn)
        {
            Map home = HomeMapOf(pawn);
            return home?.GetComponent<MapComponent_OmniWorkstation>();
        }

        public MapComponent_OmniWorkstation ManagerFor(Map pool)
        {
            return pool?.GetComponent<MapComponent_OmniWorkstation>();
        }

        /// <summary>某池的全部代理（含驻外）。</summary>
        public void EnumeratePool(Map pool, List<Pawn> into)
        {
            into.Clear();
            if (pool == null) return;
            for (int i = 0; i < records.Count; i++)
            {
                ProxyHomeRecord record = records[i];
                if (record?.pawn == null || record.pawn.Destroyed) continue;
                if (record.homeMap == pool) into.Add(record.pawn);
            }
        }

        /// <summary>某池当前驻外的代理。</summary>
        public void EnumerateForeign(Map pool, List<Pawn> into)
        {
            into.Clear();
            if (pool == null) return;
            for (int i = 0; i < records.Count; i++)
            {
                ProxyHomeRecord record = records[i];
                if (record?.pawn == null || record.pawn.Destroyed) continue;
                if (record.homeMap == pool && record.abroadMap != null && record.abroadMap != pool)
                    into.Add(record.pawn);
            }
        }

        public void EnumerateAll(List<Pawn> into)
        {
            into.Clear();
            for (int i = 0; i < records.Count; i++)
            {
                ProxyHomeRecord record = records[i];
                if (record?.pawn == null || record.pawn.Destroyed) continue;
                into.Add(record.pawn);
            }
        }

        public int TotalCount => records.Count;

        /// <summary>所有出现过的池（用于"重建全部池"与统计）。</summary>
        public void CollectPools(List<Map> into)
        {
            into.Clear();
            for (int i = 0; i < records.Count; i++)
            {
                Map pool = records[i]?.homeMap;
                if (pool != null && !into.Contains(pool)) into.Add(pool);
            }
        }

        // ── 归属登记 ────────────────────────────────────────────────────────────

        public void Register(Pawn pawn, Map homeMap, int stationThingId)
        {
            if (pawn == null) return;
            if (byPawn.TryGetValue(pawn, out ProxyHomeRecord existing))
            {
                // 已有记录时只补齐缺失字段，不覆盖已有的驻外状态。
                if (existing.homeMap == null) existing.homeMap = homeMap;
                if (existing.stationThingId < 0) existing.stationThingId = stationThingId;
                WriteMirror(pawn, existing.homeMap, existing.stationThingId);
                return;
            }
            ProxyHomeRecord record = new ProxyHomeRecord
            {
                pawn = pawn,
                homeMap = homeMap,
                stationThingId = stationThingId,
                abroadMap = (homeMap != null && pawn.Spawned && pawn.Map != homeMap) ? pawn.Map : null
            };
            records.Add(record);
            byPawn[pawn] = record;
            // 归属同时在代理身上留一份镜像（Hediff 随存档深保存），索引损坏时可据此自愈。
            WriteMirror(pawn, homeMap, stationThingId);
        }

        public void Unregister(Pawn pawn)
        {
            if (pawn == null) return;
            if (!byPawn.TryGetValue(pawn, out ProxyHomeRecord record)) return;
            byPawn.Remove(pawn);
            records.Remove(record);
            RemoveMirror(pawn);
        }

        // ── 归属镜像（Hediff）────────────────────────────────────────────────────

        private static Hediff_OmniWorkProxyHome FindMirror(Pawn pawn)
        {
            List<Hediff> hediffs = pawn?.health?.hediffSet?.hediffs;
            if (hediffs == null) return null;
            for (int i = 0; i < hediffs.Count; i++)
                if (hediffs[i] is Hediff_OmniWorkProxyHome mirror) return mirror;
            return null;
        }

        /// <summary>把归属写到代理身上；已存在则原地更新，不重复添加。</summary>
        private static void WriteMirror(Pawn pawn, Map homeMap, int stationThingId)
        {
            if (pawn == null || pawn.Destroyed || pawn.health == null) return;
            Hediff_OmniWorkProxyHome mirror = FindMirror(pawn);
            if (mirror == null)
            {
                HediffDef def = OmniWorkstationDefOf.FAOC_OmniWorkProxyHome;
                if (def == null) return;
                mirror = pawn.health.AddHediff(def) as Hediff_OmniWorkProxyHome;
                if (mirror == null) return;
            }
            mirror.homeMapUniqueId = homeMap?.uniqueID ?? -1;
            mirror.stationThingId = stationThingId;
        }

        private static void RemoveMirror(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.health == null) return;
            Hediff_OmniWorkProxyHome mirror = FindMirror(pawn);
            if (mirror != null) pawn.health.RemoveHediff(mirror);
        }

        /// <summary>
        /// 用代理身上的镜像把归属补回索引（索引缺失或整体损坏时调用）。
        /// 返回 false 表示代理身上没有可用的归属信息。
        /// </summary>
        public bool TryRestoreFromMirror(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return false;
            if (byPawn.ContainsKey(pawn)) return true;
            Hediff_OmniWorkProxyHome mirror = FindMirror(pawn);
            if (mirror == null || mirror.homeMapUniqueId < 0) return false;

            Map home = FindMapByUniqueId(mirror.homeMapUniqueId);
            if (home == null) return false;
            Register(pawn, home, mirror.stationThingId);
            return true;
        }

        /// <summary>
        /// 校验代理身上的镜像，并按**权威表**回写所有不一致处；
        /// 权威表里没有该代理时反向尝试用镜像把归属救回。
        /// 返回该代理最终是否有有效归属。
        /// </summary>
        public bool VerifyMirror(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return false;

            if (!byPawn.TryGetValue(pawn, out ProxyHomeRecord record))
            {
                if (TryRestoreFromMirror(pawn)) return true;
                // 没有任何权威归属的代理不该留着归属镜像，否则下次会把它"救"回一个已废弃的池。
                RemoveMirror(pawn);
                return false;
            }

            int wantMapId = record.homeMap?.uniqueID ?? -1;
            Hediff_OmniWorkProxyHome mirror = FindMirror(pawn);
            if (mirror == null || mirror.homeMapUniqueId != wantMapId ||
                mirror.stationThingId != record.stationThingId)
                WriteMirror(pawn, record.homeMap, record.stationThingId);
            return true;
        }

        /// <summary>低频周期校验：对全表代理做一次镜像一致性检查。返回校验数量。</summary>
        public int VerifyAllMirrors()
        {
            EnumerateAll(pawnScratch);
            for (int i = 0; i < pawnScratch.Count; i++) VerifyMirror(pawnScratch[i]);
            return pawnScratch.Count;
        }

        private static Map FindMapByUniqueId(int uniqueId)
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
                if (maps[i].uniqueID == uniqueId) return maps[i];
            return null;
        }

        // ── 观测：跨图 / 容器 ───────────────────────────────────────────────────

        /// <summary>由 Pawn.SpawnSetup 补丁回调：代理出现在某张图上。</summary>
        public void NotifySpawned(Pawn pawn, Map map)
        {
            if (pawn == null || map == null) return;
            if (!byPawn.TryGetValue(pawn, out ProxyHomeRecord record)) return;
            if (record.homeMap == null) record.homeMap = map;

            if (record.homeMap == map)
            {
                record.abroadMap = null;
                record.abroadSinceTick = -1;
                record.inContainer = false;
            }
            else if (record.abroadMap != map)
            {
                record.abroadMap = map;
                record.abroadSinceTick = CurrentTick;
                record.inContainer = false;
            }
        }

        /// <summary>由 Pawn.DeSpawn 补丁回调：代理离开地图（入舱或被第三方容器接住）。</summary>
        public void NotifyDespawned(Pawn pawn)
        {
            if (pawn == null) return;
            if (!byPawn.TryGetValue(pawn, out ProxyHomeRecord record)) return;
            // 只有"驻外期间"被容器接住才需要标记；入我方休眠舱时 abroadMap 为 null。
            record.inContainer = record.abroadMap != null && pawn.ParentHolder != null;
        }

        /// <summary>归属图被销毁：归属该图者全部强删，避免留下无主代理。</summary>
        public void NotifyHomeMapRemoved(Map pool)
        {
            if (pool == null) return;
            EnumeratePool(pool, pawnScratch);
            for (int i = 0; i < pawnScratch.Count; i++) ForceDestroy(pawnScratch[i]);
            // 归属记录中残留的该图条目（例如休眠舱里的）一并清掉。
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ProxyHomeRecord record = records[i];
                if (record == null || record.homeMap != pool && record.abroadMap != pool) continue;
                if (record.homeMap == pool) Unregister(record.pawn);
                else record.abroadMap = null;
            }
        }

        // ── 管理：强删 / 重建 / 总开关 ──────────────────────────────────────────

        /// <summary>强删单个代理：任意图、任意容器、任意 Job 状态都可用。</summary>
        public bool ForceDestroy(Pawn pawn)
        {
            if (pawn == null) return false;
            if (pawn.Destroyed)
            {
                Unregister(pawn);
                return false;
            }

            MapComponent_OmniWorkstation manager = ManagerOf(pawn);
            manager?.StopIssuedJobForRecord(pawn);
            OmniWorkProxyUtility.Unassign(pawn);
            OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);
            if (pawn.holdingOwner != null) pawn.holdingOwner.Remove(pawn);
            if (pawn.Spawned) pawn.DeSpawn(DestroyMode.Vanish);
            Unregister(pawn);
            if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
            PurgeFailuresAcrossMaps(pawn);
            return true;
        }

        /// <summary>重建单个代理：销毁它并让所属池补建一个替代品。</summary>
        public bool RecreateSingle(Pawn pawn)
        {
            if (pawn == null) return false;
            Map pool = HomeMapOf(pawn);
            if (!ForceDestroy(pawn)) return false;
            MapComponent_OmniWorkstation manager = ManagerFor(pool);
            manager?.EnsureProxyCount();
            manager?.WakePumpNow();
            return true;
        }

        /// <summary>强制该代理停止工作并立即回收回**所属池**（不销毁）。</summary>
        public bool ReclaimNow(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return false;
            if (!TryGetHome(pawn, out ProxyHomeRecord record)) return false;
            MapComponent_OmniWorkstation manager = ManagerFor(record.homeMap);
            if (manager == null) return false;

            manager.StopIssuedJobForRecord(pawn);
            OmniWorkProxyUtility.Unassign(pawn);
            if (pawn.holdingOwner != null && !ReferenceEquals(pawn.holdingOwner.Owner, manager))
            {
                OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);
                pawn.holdingOwner.Remove(pawn);
            }
            manager.ForceSleep(pawn);
            manager.WakePumpNow();
            return true;
        }

        /// <summary>重建单池：只处理归属恰好为该图的代理（含驻外与容器内），其他池不受影响。</summary>
        public int ForceRecreatePool(Map pool)
        {
            if (pool == null) return 0;
            MapComponent_OmniWorkstation manager = ManagerFor(pool);

            int destroyed = 0;
            EnumeratePool(pool, pawnScratch);
            for (int i = 0; i < pawnScratch.Count; i++)
                if (ForceDestroy(pawnScratch[i])) destroyed++;
            manager?.ClearSleepingPool();
            manager?.EnsureProxyCount();
            manager?.WakePumpNow();
            return destroyed;
        }

        /// <summary>重建所有池（含口袋地图上的池）。</summary>
        public int ForceRecreateAllPools()
        {
            int destroyed = 0;
            CollectPools(poolScratch);
            for (int i = 0; i < poolScratch.Count; i++)
                destroyed += ForceRecreatePool(poolScratch[i]);
            return destroyed;
        }

        /// <summary>全局总闸：关闭时所有池停止派发，并把场上代理收回各自归属池的休眠舱（不销毁）。</summary>
        public void SetGlobalWorkEnabled(bool enabled)
        {
            globalWorkEnabled = enabled;
            CollectPools(poolScratch);
            for (int i = 0; i < poolScratch.Count; i++)
            {
                MapComponent_OmniWorkstation manager = ManagerFor(poolScratch[i]);
                if (manager == null) continue;
                if (enabled) manager.WakePumpNow();
                else manager.ReclaimAllActive();
            }
        }

        private static void PurgeFailuresAcrossMaps(Pawn pawn)
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
                maps[i].GetComponent<MapComponent_OmniWorkstation>()?.PurgeFailureFor(pawn);
        }

        private static int CurrentTick => Find.TickManager?.TicksGame ?? 0;

        /// <summary>清理失效记录（代理已销毁或归属图已不存在）。低频调用。</summary>
        public void PruneInvalid()
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ProxyHomeRecord record = records[i];
                if (record == null || record.pawn == null || record.pawn.Destroyed)
                {
                    if (record?.pawn != null) byPawn.Remove(record.pawn);
                    records.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 修复单个池（R-9）：丢掉失效记录，清掉休眠舱里的残留条目，用镜像把可救回的归属补回索引，
        /// 最后按配置补齐数量。返回新补建的代理数。
        /// </summary>
        public int RepairPool(Map pool)
        {
            if (pool == null) return 0;
            MapComponent_OmniWorkstation manager = ManagerFor(pool);
            if (manager == null) return 0;

            PruneInvalid();
            manager.ClearSleepingPoolOfInvalid();
            manager.VerifyPoolMirrors();

            int before = manager.TotalProxyCount;
            manager.EnsureProxyCount();
            manager.WakePumpNow();
            return manager.TotalProxyCount - before;
        }

        /// <summary>修复所有已登记过的池（R-9）。返回新补建的代理总数。</summary>
        public int RepairAllPools()
        {
            int created = 0;
            CollectPools(poolScratch);
            for (int i = 0; i < poolScratch.Count; i++) created += RepairPool(poolScratch[i]);
            return created;
        }

        // ── 全局界面请求（在 GameComponentTick 中执行）────────────────────────────
        // 管理界面的按钮运行在 OnGUI 阶段，而销毁/生成 Pawn 会改动地图集合，
        // 因此界面只登记意图，实际动作统一交回本组件的 tick 执行。

        private bool pendingForceRecreateAll;
        private bool pendingRepairAll;

        /// <summary>登记"重建所有池的代理"（R-7）。</summary>
        public void RequestForceRecreateAll() { pendingForceRecreateAll = true; }

        /// <summary>登记"修复所有池"（R-9）。</summary>
        public void RequestRepairAll() { pendingRepairAll = true; }

        // ── 待补发的到达回调（W-1）────────────────────────────────────────────
        // 导航补丁在 PatherTick（即 Toil.initAction 的调用栈）里只登记，真正调用原版 PatherArrived
        // 放在本组件的 tick 栈中，避免到达回调递归推进 job 链时把后续 Toil 留在“代理已离开地图”的
        // 状态里（那会让 JobDriver.Map => pawn.MapHeld 变成 null 引用，并使原版错误恢复也一起失败）。

        private static readonly List<Pawn> pendingArrivals = new List<Pawn>();

        /// <summary>登记一个待补发的到达回调（去重；只在主线程调用）。</summary>
        public static void RequestArrival(Pawn pawn)
        {
            if (pawn == null) return;
            for (int i = 0; i < pendingArrivals.Count; i++)
            {
                if (pendingArrivals[i] == pawn) return;
            }
            pendingArrivals.Add(pawn);
        }

        private static void ProcessPendingArrivals()
        {
            if (pendingArrivals.Count == 0) return;
            for (int i = 0; i < pendingArrivals.Count; i++)
            {
                Pawn pawn = pendingArrivals[i];
                // 防御：代理已被回收、已离开地图或已没有 job/driver 时，绝不推进 toil 链
                //（原版 JobDriver.Map => pawn.MapHeld，JobDriver_Wait 的 initAction 首句即访问它）。
                if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.MapHeld == null ||
                    pawn.jobs == null || pawn.jobs.curJob == null || pawn.jobs.curDriver == null ||
                    pawn.pather == null)
                {
                    continue;
                }
                Patch_OmniNavigation_Tick.FireArrival(pawn.pather);
            }
            pendingArrivals.Clear();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            ProcessPendingArrivals();
            // 无请求时立即返回：这是每 tick 都会经过的路径，必须保持零成本。
            if (!pendingForceRecreateAll && !pendingRepairAll) return;

            if (pendingForceRecreateAll)
            {
                pendingForceRecreateAll = false;
                int destroyed = ForceRecreateAllPools();
                Messages.Message("OmniWorkstation_ManagerRecreateAllDone".Translate(destroyed),
                    MessageTypeDefOf.TaskCompletion, false);
            }

            if (pendingRepairAll)
            {
                pendingRepairAll = false;
                int created = RepairAllPools();
                Messages.Message("OmniWorkstation_ManagerRepairAllDone".Translate(created),
                    MessageTypeDefOf.TaskCompletion, false);
            }
        }
    }
}
