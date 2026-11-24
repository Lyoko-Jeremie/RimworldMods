using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>一个搬运需求的交付终点类型（对应三类收口）。</summary>
    internal enum HaulIntentKind
    {
        /// <summary>存储格：DeSpawn 后 GenPlace 到目标格。</summary>
        Cell,

        /// <summary>ThingOwner：DeSpawn 后 TryAdd，并通知 INotifyHauledTo。</summary>
        Container,

        /// <summary>燃料：CompRefuelable.Refuel(float) + SplitOff/Destroy。</summary>
        Refuel
    }

    /// <summary>由原版 Job 解析出的「谁给谁、多少」。</summary>
    internal struct HaulIntent
    {
        public Thing Source;
        public Thing DestThing;
        public IntVec3 DestCell;
        public int Count;
        public HaulIntentKind Kind;
    }

    /// <summary>
    /// 填充通道：复用原版 Hauling 分类下的 WorkGiver 做需求发现，
    /// 把 Job 解析成 HaulIntent，再用三类交付原语落地。
    ///
    /// 不派 Pawn、不生成 Job、不占用工作类型、不进入 JobDriver。
    /// 判定窗口只在 HasJobOnThing / JobOnThing 期间打开，交付阶段已退出窗口，
    /// 避免 DeSpawn / GenPlace 与伪造状态互相干扰。
    ///
    /// 覆盖范围（一期）：燃料、炮塔装弹、料斗、运输舱、基因库、生长槽、
    /// 生物雕塑舱、次核扫描仪、原子化器、发酵桶、建造框架/蓝图、传送门。
    /// 反向清理（蛋箱/垃圾/取酒）留二期。
    /// </summary>
    public static class HaulDemandRunner
    {
        // 判定间隔由 Building_AutoItemCollector.FillScanInterval 提供（玩家可按建筑单独设置，
        // 默认 60 tick）。全图档下每个候选都可能触发一次全图搜索，因此默认值没有取得太激进。

        /// <summary>
        /// 单个 WorkGiver 每轮最多检查的候选 Thing 数。
        /// 配合 RotateStart 轮转游标，保证候选很多时（例如几十座燃料建筑）
        /// 也能在若干轮内覆盖全部，而不是永远只看列表前几个。
        /// </summary>
        private const int MaxCandidatesPerGiver = 64;

        private static readonly HashSet<string> WhitelistedDefNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Refuel",
                "RearmTurrets",
                "CookFillHopper",
                "LoadTransporters",
                "HaulToGeneBank",
                "HaulToGrowthVat",
                "HaulToBiosculpterPod",
                "CarryToSubcoreScanner",
                "HaulToAtomizer",
                "FillFermentingBarrel",
                "DeliverResourcesToFrames",
                "DeliverResourcesToBlueprints",
                "HaulToPortal"
            };

        private static readonly HashSet<Type> WhitelistedGiverClasses = new HashSet<Type>
        {
            typeof(WorkGiver_Refuel),
            typeof(WorkGiver_Refuel_Turret),
            typeof(WorkGiver_CookFillHopper),
            typeof(WorkGiver_LoadTransporters),
            typeof(WorkGiver_HaulToGeneBank),
            typeof(WorkGiver_HaulToGrowthVat),
            typeof(WorkGiver_HaulToBiosculpterPod),
            typeof(WorkGiver_HaulToSubcoreScanner),
            typeof(WorkGiver_HaulToAtomizer),
            typeof(WorkGiver_FillFermentingBarrel),
            typeof(WorkGiver_ConstructDeliverResourcesToFrames),
            typeof(WorkGiver_ConstructDeliverResourcesToBlueprints),
            typeof(WorkGiver_HaulToPortal)
        };

        private static readonly Dictionary<long, int> NextCheckTick = new Dictionary<long, int>();
        private static readonly Dictionary<int, int> LastRunTick = new Dictionary<int, int>();
        /// <summary>每个 (建筑, giver) 的候选轮转起点，key 同 CacheKey。</summary>
        private static readonly Dictionary<long, int> RotateStart = new Dictionary<long, int>();

        private static long CacheKey(int buildingId, WorkGiverDef def)
        {
            return ((long)buildingId << 32) ^ (uint)def.shortHash;
        }

        private static bool IsWhitelisted(WorkGiverDef def)
        {
            if (def == null) return false;
            if (!def.defName.NullOrEmpty() && WhitelistedDefNames.Contains(def.defName)) return true;
            return def.giverClass != null && WhitelistedGiverClasses.Contains(def.giverClass);
        }

        /// <summary>
        /// 为单个收集器尝试执行一次填充。
        /// 返回实际完成的交付数量（0 表示这次没有可做的）。
        /// </summary>
        public static int TryRun(Building_AutoItemCollector collector)
        {
            if (collector == null || !collector.Spawned || collector.Map == null) return 0;
            if (!OmniHaulJudge.Available) return 0;

            Map map = collector.Map;
            int now = Find.TickManager.TicksGame;
            int buildingId = collector.thingIDNumber;
            int interval = Mathf.Max(1, collector.FillScanInterval);

            int last;
            if (LastRunTick.TryGetValue(buildingId, out last) && now - last < interval)
            {
                return 0;
            }
            LastRunTick[buildingId] = now;
            PruneCachesIfNeeded(now);

            WorkTypeDef hauling = WorkTypeDefOf.Hauling;
            if (hauling == null || hauling.workGiversByPriority == null) return 0;

            Pawn judge = OmniHaulJudge.GetOrCreate(map);
            if (judge == null) return 0;

            bool wholeMap = collector.ReachabilityMode == CollectorReachabilityMode.WholeMap;
            List<WorkGiverDef> givers = hauling.workGiversByPriority;

            for (int i = 0; i < givers.Count; i++)
            {
                WorkGiverDef def = givers[i];
                if (!IsWhitelisted(def)) continue;

                long key = CacheKey(buildingId, def);
                int next;
                if (NextCheckTick.TryGetValue(key, out next) && now < next) continue;

                NextCheckTick[key] = now + interval;

                WorkGiver_Scanner scanner = def.Worker as WorkGiver_Scanner;
                if (scanner == null) continue;

                if (TryRunGiver(collector, judge, scanner, map, wholeMap))
                {
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// 在判定窗口内找一个可解析的 Job；退出窗口后再交付。
        /// </summary>
        private static bool TryRunGiver(
            Building_AutoItemCollector collector,
            Pawn judge,
            WorkGiver_Scanner scanner,
            Map map,
            bool wholeMap)
        {
            ThingRequest request;
            try
            {
                request = scanner.PotentialWorkThingRequest;
            }
            catch (Exception error)
            {
                Log.ErrorOnce("[FAOC] WorkGiver 取扫描组失败：" + error, 197403401);
                return false;
            }

            // 完全未定义（既没有 singleDef 也没有 group）的 WorkGiver 只能靠
            // PotentialWorkThingsGlobal 给候选集，这类自带 Pawn 位置语义，一期不处理。
            if (request.IsUndefined) return false;

            // ForDef 的 WorkGiver（料斗、发酵桶、基因包、生长槽等）必须按 def
            // 精确取列表：同一 ThingRequestGroup 可能有成百上千个建筑，
            // 用组列表会把目标挤到 MaxCandidatesPerGiver 之外。
            List<Thing> candidates;
            if (request.singleDef != null)
            {
                candidates = map.listerThings.ThingsOfDef(request.singleDef);
            }
            else
            {
                candidates = map.listerThings.ThingsInGroup(request.group);
            }
            if (candidates == null || candidates.Count == 0)
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[FAOC][fill] " + scanner.def.defName + " 候选为空 request=" + request);
                }
                return false;
            }

            StorageSettings settings = collector.GetStoreSettings();
            ThingFilter filter = settings == null ? null : settings.filter;
            Faction player = Faction.OfPlayer;

            HaulIntent intent = default(HaulIntent);
            bool foundIntent = false;
            bool parseFailed = false;
            int checkedCount = 0;
            int hasJobCount = 0;
            int filterRejected = 0;
            int quickRejected = 0;
            int examined = 0;
            int eligibilityLogged = 0;
            int quickDetailLogged = 0;

            if (!OmniHaulJudge.Begin(judge, map, collector.Position, wholeMap))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[FAOC][fill] " + scanner.def.defName + " 判定窗口打开失败 anchor=" + collector.Position);
                }
                return false;
            }

            try
            {
                if (scanner.ShouldSkip(judge))
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("[FAOC][fill] " + scanner.def.defName + " ShouldSkip=true");
                    }
                    return false;
                }
                long rotateKey = CacheKey(collector.thingIDNumber, scanner.def);
                int start = 0;
                RotateStart.TryGetValue(rotateKey, out start);
                if (start < 0 || start >= candidates.Count) start = 0;

                int limit = Mathf.Min(MaxCandidatesPerGiver, candidates.Count);
                for (int n = 0; n < limit; n++)
                {
                    int index = start + n;
                    if (index >= candidates.Count) index -= candidates.Count;
                    examined++;

                    Thing candidate = candidates[index];
                    if (!IsEligibleCandidate(candidate, collector, map, filter, player))
                    {
                        bool isFuelBuilding = candidate.TryGetComp<CompRefuelable>() != null
                            || candidate is Building_Turret;
                        bool needsAttention = isFuelBuilding && !IsRefuelableFull(candidate);
                        if (Prefs.DevMode && (needsAttention
                            || (isFuelBuilding ? eligibilityLogged < 4 : eligibilityLogged < 3)))
                        {
                            eligibilityLogged++;
                            Log.Message("[FAOC][fill] " + scanner.def.defName + " 资格拒绝 "
                                + candidate.LabelShort + " forbidden=" + candidate.IsForbidden(player)
                                + " spawned=" + candidate.Spawned + " stack=" + candidate.stackCount
                                + " everHaulable=" + (candidate.def != null && candidate.def.EverHaulable));
                        }

                        continue;
                    }

                    if (!QuickEligible(scanner, candidate, out string quickReason))
                    {
                        quickRejected++;
                        // 燃料建筑是排查重点：未满的燃料建筑（例如玩家刚调高阈值）
                        // 一定值得看，不受条数限制；已满的只打前几条避免刷屏。
                        bool isFuelBuilding = candidate.TryGetComp<CompRefuelable>() != null
                            || candidate is Building_Turret;
                        bool needsAttention = isFuelBuilding && !IsRefuelableFull(candidate);
                        if (Prefs.DevMode && (needsAttention
                            || (isFuelBuilding ? quickDetailLogged < 4 : quickDetailLogged < 3)))
                        {
                            quickDetailLogged++;
                            Log.Message("[FAOC][fill] " + scanner.def.defName + " 预筛拒绝 "
                                + candidate.LabelShort + " 原因=" + quickReason);
                        }

                        continue;
                    }

                    checkedCount++;

                    bool hasJob;
                    Job job;
                    try
                    {
                        // 原版 CanRefuel 等判定会把拒绝原因写进 JobFailReason，
                        // DevMode 下读出来可直接定位是哪一条前置条件卡住。
                        JobFailReason.Clear();
                        hasJob = scanner.HasJobOnThing(judge, candidate, false);
                        if (!hasJob)
                        {
                            if (Prefs.DevMode)
                            {
                                Log.Message("[FAOC][fill] " + scanner.def.defName + " 拒绝 "
                                    + candidate.LabelShort + " 原因="
                                    + (JobFailReason.HaveReason ? JobFailReason.Reason : "(无)"));
                            }
                            continue;
                        }
                        job = scanner.JobOnThing(judge, candidate, false);
                    }
                    catch (Exception error)
                    {
                        Log.ErrorOnce("[FAOC] WorkGiver 判定异常：" + error, 197403402);
                        continue;
                    }

                    if (job == null) continue;

                    hasJobCount++;

                    HaulIntent parsed;
                    if (!TryParse(job, judge, out parsed))
                    {
                        parseFailed = true;
                        if (Prefs.DevMode)
                        {
                            Log.Message("[FAOC] 无法解析的搬运 Job：" + job.def.defName);
                        }
                        continue;
                    }

                    // 用户红线：只搬建筑筛选器允许的物品。
                    // 注意过滤的是解析出的「源物品」，不是候选目标
                    // （Refuel/LoadTransporters 的候选是建筑，不是物品）。
                    if (filter != null && !filter.Allows(parsed.Source))
                    {
                        filterRejected++;
                        continue;
                    }

                    intent = parsed;
                    foundIntent = true;
                    break;
                }

                RotateStart[rotateKey] = (start + examined) % candidates.Count;
            }
            finally
            {
                OmniHaulJudge.End();
            }

            if (Prefs.DevMode && (checkedCount > 0 || hasJobCount > 0 || filterRejected > 0 || quickRejected > 0))
            {
                Log.Message("[FAOC][fill] " + scanner.def.defName
                    + " 候选=" + candidates.Count
                    + " 扫描=" + examined
                    + " 预筛拒绝=" + quickRejected
                    + " 已检查=" + checkedCount
                    + " 有Job=" + hasJobCount
                    + " 解析失败=" + (parseFailed ? 1 : 0)
                    + " 筛选拒绝=" + filterRejected
                    + " 命中=" + (foundIntent ? "是" : "否"));
            }

            if (!foundIntent)
            {
                if (parseFailed && Prefs.DevMode)
                {
                    Log.Message("[FAOC] 本次填充判定全部无法解析，giver=" + scanner.def.defName);
                }
                return false;
            }

            bool delivered = Deliver(intent);
            if (!delivered && Prefs.DevMode)
            {
                Log.Message("[FAOC][fill] 交付失败 kind=" + intent.Kind
                    + " source=" + (intent.Source == null ? "null" : intent.Source.LabelShort)
                    + " dest=" + (intent.DestThing == null ? "null" : intent.DestThing.LabelShort));
            }
            else if (delivered && Prefs.DevMode)
            {
                CompRefuelable deliveredFuel = intent.DestThing == null ? null : intent.DestThing.TryGetComp<CompRefuelable>();
                Log.Message("[FAOC][fill] 交付成功 kind=" + intent.Kind
                    + " source=" + (intent.Source == null ? "null" : intent.Source.LabelShort + "x" + intent.Source.stackCount)
                    + " dest=" + (intent.DestThing == null ? "null" : intent.DestThing.LabelShort)
                    + (deliveredFuel == null ? "" : " fuel=" + deliveredFuel.Fuel.ToString("0.#") + "/" + deliveredFuel.TargetFuelLevel.ToString("0.#")));
            }

            return delivered;
        }

        /// <summary>
        /// O(1) 预筛：把原版判定里最便宜、最常拒绝的条件提前做掉，
        /// 避免每个候选都进入 HasJobOnThing 触发一次 GenClosest 全图搜索。
        /// 只做「原版一定会拒绝」的等价判断，不额外放行。
        /// </summary>
        private static bool QuickEligible(WorkGiver_Scanner scanner, Thing t, out string rejectReason)
        {
            rejectReason = null;

            if (scanner is WorkGiver_Refuel_Turret)
            {
                // 炮塔装弹 giver 只处理炮塔，与原版 CanRefuelThing 一致。
                if (!(t is Building_Turret))
                {
                    rejectReason = "非炮塔";
                    return false;
                }
            }
            else if (scanner is WorkGiver_Refuel)
            {
                if (t is Building_Turret)
                {
                    rejectReason = "炮塔应由装弹 giver 处理";
                    return false;
                }
            }

            CompRefuelable refuelable = t.TryGetComp<CompRefuelable>();
            if (refuelable != null)
            {
                if (refuelable.IsFull)
                {
                    // 带上数值：玩家看到的 UI 是「燃料/容量」，而 IsFull 比的是
                    // TargetFuelLevel，两者不同，必须打出来才能判断到底是不是「已满」。
                    rejectReason = "已满 fuel=" + refuelable.Fuel.ToString("0.#")
                        + "/target=" + refuelable.TargetFuelLevel.ToString("0.#")
                        + " 容量=" + refuelable.Props.fuelCapacity.ToString("0.#");
                    return false;
                }
                if (!refuelable.allowAutoRefuel)
                {
                    rejectReason = "未开自动加注";
                    return false;
                }
                if (!refuelable.ShouldAutoRefuelNow)
                {
                    rejectReason = "未达加注阈值 fuel=" + refuelable.Fuel.ToString("0.#")
                        + "/target=" + refuelable.TargetFuelLevel.ToString("0.#")
                        + " pct=" + refuelable.FuelPercentOfTarget.ToStringPercent()
                        + " 触发点=" + refuelable.Props.autoRefuelPercent.ToStringPercent();
                    return false;
                }
            }

            if (scanner is WorkGiver_LoadTransporters)
            {
                CompTransporter transporter = t.TryGetComp<CompTransporter>();
                if (transporter == null || !transporter.AnythingLeftToLoad)
                {
                    rejectReason = "无待装载需求";
                    return false;
                }
            }

            return true;
        }

        private static bool IsRefuelableFull(Thing t)
        {
            CompRefuelable comp = t.TryGetComp<CompRefuelable>();
            return comp != null && comp.IsFull;
        }

        private static bool IsEligibleCandidate(
            Thing candidate,
            Building_AutoItemCollector collector,
            Map map,
            ThingFilter filter,
            Faction player)
        {
            if (candidate == null || candidate == collector) return false;
            if (candidate.Destroyed || !candidate.Spawned || candidate.Map != map) return false;
            if (player != null && candidate.IsForbidden(player)) return false;

            // 只有「候选本身就是可搬物品」时才用建筑筛选器过滤；
            // Refuel/LoadTransporters/Hopper 等的候选是目标建筑，
            // 对它们套用物品筛选器会导致永远无法命中。
            if (filter != null && candidate.def != null && candidate.def.EverHaulable
                && !filter.Allows(candidate))
            {
                return false;
            }

            return candidate.def != null && candidate.stackCount > 0;
        }

        /// <summary>
        /// Job → (source, destination, count)。
        /// 以「哪个目标是物品、哪个是容器」判断方向，driverClass 只做兜底，
        /// 避免硬编码类名导致 mod/版本改名后失效。
        /// </summary>
        private static bool TryParse(Job job, Pawn judge, out HaulIntent intent)
        {
            intent = default(HaulIntent);
            if (job == null || job.def == null) return false;

            Thing a = job.targetA.Thing;
            Thing b = job.targetB.Thing;
            Thing queued = FirstQueuedThing(job.targetQueueB);

            bool aIsItem = IsItem(a);
            bool bIsItem = IsItem(b);
            bool qIsItem = IsItem(queued);

            Thing source = null;
            Thing destThing = null;
            IntVec3 destCell = IntVec3.Invalid;
            int forcedCount = 0;

            if (aIsItem && !bIsItem)
            {
                source = a;
                destThing = b;
                if (destThing == null) destCell = job.targetB.Cell;
            }
            else if (bIsItem && !aIsItem)
            {
                source = b;
                destThing = a;
            }
            else if (qIsItem)
            {
                source = queued;
                destThing = b != null ? b : a;
            }
            else if (aIsItem && bIsItem)
            {
                // 两个都是物品：优先取 A（HaulToCell 的目标格不会走这里）。
                source = a;
                destThing = b;
            }

            // 运输舱的 Job 在构造时 targetA 是空的，真正的物品要等
            // JobDriver_HaulToTransporter.Notify_Starting 才写入；
            // 这里复用原版的「找剩余需求」逻辑把物品补出来。
            if (source == null && b != null)
            {
                CompTransporter transporter = b.TryGetComp<CompTransporter>();
                if (transporter != null && transporter.AnythingLeftToLoad)
                {
                    ThingCount needed = LoadTransportersJobUtility.FindThingToLoad(judge, transporter);
                    if (needed.Thing != null && needed.Count > 0)
                    {
                        source = needed.Thing;
                        destThing = b;
                        forcedCount = needed.Count;
                    }
                }
            }

            if (source == null) return false;
            if (source.stackCount <= 0 || source.Destroyed || !source.Spawned) return false;
            if (source == destThing) return false;

            HaulIntentKind kind;
            if (destThing != null)
            {
                if (destThing.Destroyed || !destThing.Spawned || destThing.Map != source.Map) return false;

                if (destThing.TryGetComp<CompRefuelable>() != null)
                {
                    kind = HaulIntentKind.Refuel;
                }
                else if (destThing.TryGetInnerInteractableThingOwner() != null)
                {
                    kind = HaulIntentKind.Container;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (!destCell.IsValid) return false;
                kind = HaulIntentKind.Cell;
            }

            int count = forcedCount > 0
                ? forcedCount
                : (job.count > 0 ? job.count : source.stackCount);
            count = Mathf.Min(count, source.stackCount);
            if (count <= 0) return false;

            intent.Source = source;
            intent.DestThing = destThing;
            intent.DestCell = destCell;
            intent.Count = count;
            intent.Kind = kind;
            return true;
        }

        private static Thing FirstQueuedThing(List<LocalTargetInfo> queue)
        {
            if (queue == null) return null;
            for (int i = 0; i < queue.Count; i++)
            {
                Thing thing = queue[i].Thing;
                if (thing != null && !thing.Destroyed && thing.Spawned) return thing;
            }
            return null;
        }

        private static bool IsItem(Thing thing)
        {
            return thing != null && thing.def != null && thing.def.EverHaulable && thing.stackCount > 0;
        }

        // ── 交付层（三类收口） ──────────────────────────────────────────────

        private static bool Deliver(HaulIntent intent)
        {
            Thing source = intent.Source;
            if (source == null || source.Destroyed || !source.Spawned) return false;

            Map map = source.Map;
            Faction player = Faction.OfPlayer;
            // 判定与交付之间玩家可能把物品禁用：交付前再查一次（用户红线）。
            if (player != null && source.IsForbidden(player)) return false;

            switch (intent.Kind)
            {
                case HaulIntentKind.Refuel:
                    return DeliverFuel(source, intent.DestThing);
                case HaulIntentKind.Container:
                    return DeliverContainer(source, intent.DestThing, intent.Count);
                case HaulIntentKind.Cell:
                    return DeliverCell(source, intent.DestCell, map, intent.Count);
                default:
                    return false;
            }
        }

        private static bool DeliverFuel(Thing source, Thing dest)
        {
            if (dest == null || dest.Destroyed || !dest.Spawned) return false;

            CompRefuelable comp = dest.TryGetComp<CompRefuelable>();
            if (comp == null || comp.IsFull) return false;

            int need = comp.GetFuelCountToFullyRefuel();
            if (need <= 0) return false;

            if (comp.Props.atomicFueling)
            {
                // 原子加注意味着「一次必须加满」：原版由 Job 的 FindAllFuel 凑齐多堆，
                // 这里如果只拿判定给出的那一堆，目标阈值调高后就会出现永远凑不够、
                // 一直不填充的假死。所以不足时在地图上补找同种燃料，凑齐后交给
                // Refuel(List) 一次性消耗（它会自己 SplitOff + Destroy）。
                if (source.stackCount < need)
                {
                    List<Thing> fuel = CollectAtomicFuel(source, dest.MapHeld, need);
                    if (fuel == null) return false;

                    comp.Refuel(fuel);
                    return true;
                }

                comp.Refuel(new List<Thing> { source });
                return true;
            }

            int take = Mathf.Min(need, source.stackCount);
            if (take <= 0) return false;

            comp.Refuel((float)take);
            source.SplitOff(take).Destroy();
            return true;
        }

        /// <summary>
        /// 为原子加注凑齐指定数量的同种燃料。找不到足够数量时返回 null（不部分填充）。
        /// 只取未禁用、无预约的堆叠，避免与正在搬运/建造的小人抢同一批物资。
        /// </summary>
        private static List<Thing> CollectAtomicFuel(Thing primary, Map map, int need)
        {
            if (map == null || primary == null || primary.def == null) return null;

            List<Thing> fuel = new List<Thing> { primary };
            int total = primary.stackCount;
            if (total >= need) return fuel;

            Faction player = Faction.OfPlayer;
            List<Thing> all = map.listerThings.ThingsOfDef(primary.def);
            for (int i = 0; i < all.Count; i++)
            {
                Thing thing = all[i];
                if (thing == primary || thing.Destroyed || !thing.Spawned) continue;
                if (thing.Map != map || thing.stackCount <= 0) continue;
                if (player != null && thing.IsForbidden(player)) continue;
                if (map.reservationManager.IsReserved(thing)) continue;
                if (thing.PositionHeld.Fogged(map)) continue;

                fuel.Add(thing);
                total += thing.stackCount;
                if (total >= need) return fuel;
            }

            return null;
        }

        private static bool DeliverContainer(Thing source, Thing dest, int count)
        {
            if (dest == null || dest.Destroyed || !dest.Spawned) return false;
            if (source == null || source.Destroyed || !source.Spawned) return false;

            int moveCount = Mathf.Min(count, source.stackCount);
            if (moveCount <= 0) return false;

            ThingOwner owner = dest.TryGetInnerInteractableThingOwner();
            if (owner == null) return false;

            IntVec3 oldPosition = source.PositionHeld;
            Map oldMap = source.MapHeld;

            // ThingOwner 只能整体接收一个堆叠：需要多少就切出多少，
            // 剩余部分留在地面上，等下一轮或由普通收集逻辑处理。
            Thing moving = moveCount < source.stackCount ? source.SplitOff(moveCount) : source;

            if (owner.GetCountCanAccept(moving) <= 0)
            {
                if (!ReferenceEquals(moving, source) && !moving.Destroyed)
                {
                    moving.DeSpawn();
                    GenPlace.TryPlaceThing(moving, oldPosition, oldMap, ThingPlaceMode.Near);
                }
                return false;
            }

            moving.DeSpawn();

            if (owner.TryAdd(moving))
            {
                NotifyHauledTo(dest, moving, moveCount);
                return true;
            }

            if (!moving.Destroyed)
            {
                GenPlace.TryPlaceThing(moving, oldPosition, oldMap, ThingPlaceMode.Near);
            }
            return false;
        }

        private static bool DeliverCell(Thing source, IntVec3 cell, Map map, int count)
        {
            if (source == null || source.Destroyed || !source.Spawned) return false;
            if (map == null || !cell.IsValid || !cell.InBounds(map)) return false;

            int moveCount = Mathf.Min(count, source.stackCount);
            if (moveCount <= 0) return false;

            // 存储格收口：先按原版 IsGoodStoreCell 判定；
            // 若该格属于某个 SlotGroup（料斗、存储建筑），则按 SlotGroup 语义放行，
            // 因为原版 PlaceHauledThingInCell 也是以 SlotGroup.AllowedToAccept 为准。
            bool slotGroupAccepts = false;
            SlotGroup group = map.haulDestinationManager.SlotGroupAt(cell);
            if (group != null && group.parent != null && group.parent.HaulDestinationEnabled)
            {
                slotGroupAccepts = true;
            }

            if (!slotGroupAccepts
                && !StoreUtility.IsGoodStoreCell(cell, map, source, null, Faction.OfPlayer))
            {
                return false;
            }

            IntVec3 oldPosition = source.PositionHeld;
            Map oldMap = source.MapHeld;
            Thing moving = moveCount < source.stackCount ? source.SplitOff(moveCount) : source;

            moving.DeSpawn();

            if (GenPlace.TryPlaceThing(moving, cell, map, ThingPlaceMode.Direct))
            {
                return true;
            }

            if (!moving.Destroyed)
            {
                GenPlace.TryPlaceThing(moving, oldPosition, oldMap, ThingPlaceMode.Near);
            }
            return false;
        }

        private static void NotifyHauledTo(Thing dest, Thing item, int count)
        {
            if (dest == null || item == null || count <= 0) return;

            INotifyHauledTo direct = dest as INotifyHauledTo;
            if (direct != null) direct.Notify_HauledTo(null, item, count);

            ThingWithComps withComps = dest as ThingWithComps;
            if (withComps != null)
            {
                List<ThingComp> comps = withComps.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    INotifyHauledTo comp = comps[i] as INotifyHauledTo;
                    if (comp != null) comp.Notify_HauledTo(null, item, count);
                }
            }
        }

        // ── 缓存维护 ────────────────────────────────────────────────────────

        private static void PruneCachesIfNeeded(int now)
        {
            if (LastRunTick.Count < 512 && NextCheckTick.Count < 4096) return;

            List<int> staleBuildings = null;
            foreach (KeyValuePair<int, int> pair in LastRunTick)
            {
                if (now - pair.Value > 6000)
                {
                    if (staleBuildings == null) staleBuildings = new List<int>();
                    staleBuildings.Add(pair.Key);
                }
            }

            if (staleBuildings != null)
            {
                for (int i = 0; i < staleBuildings.Count; i++)
                {
                    LastRunTick.Remove(staleBuildings[i]);
                }
            }

            if (NextCheckTick.Count >= 4096)
            {
                List<long> staleKeys = null;
                foreach (KeyValuePair<long, int> pair in NextCheckTick)
                {
                    if (now > pair.Value + 6000)
                    {
                        if (staleKeys == null) staleKeys = new List<long>();
                        staleKeys.Add(pair.Key);
                    }
                }

                if (staleKeys != null)
                {
                    for (int i = 0; i < staleKeys.Count; i++)
                    {
                        NextCheckTick.Remove(staleKeys[i]);
                        RotateStart.Remove(staleKeys[i]);
                    }
                }

                if (NextCheckTick.Count >= 4096)
                {
                    NextCheckTick.Clear();
                    RotateStart.Clear();
                }
            }
        }

        /// <summary>玩家改了填充间隔后清掉该建筑的节流记忆，让它下一 tick 立即重新判定。</summary>
        public static void ForgetBuilding(Building_AutoItemCollector collector)
        {
            if (collector == null) return;
            ForgetBuilding(collector.thingIDNumber);
        }

        public static void ForgetBuilding(int buildingId)
        {
            LastRunTick.Remove(buildingId);

            List<long> keys = null;
            foreach (KeyValuePair<long, int> pair in NextCheckTick)
            {
                if ((int)(pair.Key >> 32) == buildingId)
                {
                    if (keys == null) keys = new List<long>();
                    keys.Add(pair.Key);
                }
            }

            if (keys == null) return;

            for (int i = 0; i < keys.Count; i++)
            {
                NextCheckTick.Remove(keys[i]);
                RotateStart.Remove(keys[i]);
            }
        }

        /// <summary>地图卸载 / 新游戏时清空。</summary>
        public static void ClearAllCaches()
        {
            NextCheckTick.Clear();
            LastRunTick.Clear();
            RotateStart.Clear();
        }
    }
}
