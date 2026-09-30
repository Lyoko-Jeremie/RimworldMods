using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    // ============================================================================
    // 代理的工作加速层：让代理「每 tick 都能推进一次工作」，并把原版中
    // 「一次推进只走一小步」的离散动作型工作压成一次结算。
    //
    // 背景（原版 1.6 的两条推进通道，均已用 rimsage 核对）：
    // 1) 每 tick：Thing.DoTick → Pawn.Tick → Pawn_JobTracker.JobTrackerTick → JobDriver.DriverTick
    //    （tickAction、Delay 型 toil 的 ticksLeftThisToil 倒计时）。
    // 2) interval：Thing.DoTick 按 period = clamp(Thing.UpdateRateTicks, 1, 15) 触发
    //    TickInterval(delta) → JobTrackerTickInterval → JobDriver.DriverTickInterval
    //    （tickIntervalAction，原版 1.6 工作进度的主要入口）。
    // 代理的工作站通常不在镜头内，Pawn.UpdateRateTicks（非动物）走
    // GenTicks.GetCameraUpdateRate 返回 15，于是所有 interval 型工作都变成「每 15 tick 推进一步」，
    // 即使一次推进就能做完，也要先等 0~14 tick 的节拍边界。
    //
    // 本文件的三个补丁各自独立、条件互不重叠，且只对 FAOC 代理生效：
    // - Patch_OmniWorkProxy_FastIntervalTick：把正在工作的代理的 interval 节拍压到 1 tick。
    // - Patch_OmniWorkProxy_MineInstantBreak：挖掘按原版「最后一击」路径一次凿穿。
    // - Patch_OmniWorkProxy_RepairInstantFill：修复不再逐点回血，Job 启动时一次补满。
    // ============================================================================

    /// <summary>
    /// 正在工作的代理把 interval 节拍压到每 tick 一次。
    ///
    /// Pawn.UpdateRateTicks 决定 Thing.DoTick 的 period（clamp 到 1~15），代理不在镜头内时原值为 15。
    /// 改成 1 后 DriverTickInterval 每 tick 以 delta = 1 调用一次，于是
    /// 「工作量 -= 速度 × delta」这类工作（制作、烹饪、手术、建造、种植、平滑、清雪、
    /// 移除建筑、研究、治疗、屠宰等）在接单后的下一个 tick 内完成，不再受节拍边界影响；
    /// 这些工作的速度 stat 早已被 Hediff FAOC_OmniWorkProxyBoost 的 WorkSpeedGlobal 放大到
    /// 一次推进即完成的量级，缺的只是节拍。
    ///
    /// 只对「手上拿着非空闲 Job」的代理生效（与 Patch_OmniWorkProxy_FreezeWhenInactive 的放行
    /// 判据对称）：空闲、等待、移动类 Job 维持原版节拍，避免为本不存在的进度白付 interval 开销。
    /// 代理的需求已被 Patch_OmniWorkProxy_RemoveAllNeeds 移除，health / skills / records 结构都很轻，
    /// 因此活跃代理每 tick 跑一次 interval 的代价可忽略；代理数量上限 1024，若实测有压力可再加开关。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.UpdateRateTicks), MethodType.Getter)]
    public static class Patch_OmniWorkProxy_FastIntervalTick
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn __instance, ref int __result)
        {
            // 已是最快节拍的 Pawn 直接短路；非代理只付一次 kindDef 引用比较。
            if (__result <= 1) return;
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return;
            Job job = __instance.CurJob;
            if (job == null || MapComponent_OmniWorkstation.IsIdleJob(job)) return;
            __result = 1;
        }
    }

    /// <summary>
    /// 挖掘：代理的每一次挥击直接凿穿目标。
    ///
    /// 原版 JobDriver_Mine 的每次挥击固定造成 80（天然岩石）/ 40 点 Mining 伤害，与 MiningSpeed 无关
    /// （MiningSpeed 只决定挥击间隔，代理已把它压到每 tick 一次），因此花岗岩 900 HP 仍需 12 次、
    /// 压实钢 1500 HP 需要 19 次挥击。这里接管 JobDriver_Mine.DoDamage：对 Mineable 目标直接走
    /// 原版「最后一击」分支（按当前 HP 累积 mining 产量比例 → 清零 HP → DestroyMined → 矿脉洪泛），
    /// 从而保留原版产出、yieldPct、Mined 历史事件与矿脉连锁；随后原版 tickIntervalAction 会看到
    /// mineTarget.Destroyed，照常执行 CheckStruckOre、CellsMined 记录与 ReadyForNextToil。
    ///
    /// 只拦截「目标是 Mineable 且施动者是代理」的情形；其余情况（包括第三方对同一方法的补丁）
    /// 一律返回 true 交还原版，非 Mineable 目标也不改变原有伤害量语义。
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_Mine), "DoDamage")]
    public static class Patch_OmniWorkProxy_MineInstantBreak
    {
        [HarmonyPrefix]
        public static bool Prefix(Thing target, Pawn actor, IntVec3 mineablePos)
        {
            if (actor == null || !OmniWorkProxyUtility.IsProxy(actor)) return true;
            if (!(target is Mineable mineable)) return true;
            Map map = actor.Map;
            if (map == null || mineable.Destroyed) return true;

            bool mineVein = map.designationManager.DesignationAt(mineable.Position,
                DesignationDefOf.MineVein) != null;

            // 与 JobDriver_Mine.DoDamage 的 else 分支逐句一致：先按当前 HP 结算产量比例，再销毁并产出。
            mineable.Notify_TookMiningDamage(target.HitPoints, actor);
            mineable.HitPoints = 0;
            mineable.DestroyMined(actor);

            if (mineVein)
            {
                IntVec3[] adjacent = GenAdj.AdjacentCells;
                for (int i = 0; i < adjacent.Length; i++)
                    Designator_MineVein.FloodFillDesignations(mineablePos + adjacent[i], map, mineable.def);
            }
            return false;
        }
    }

    /// <summary>
    /// 修复：代理不逐点回血，Job 启动时一次性补满目标建筑。
    ///
    /// 原版 JobDriver_Repair 的 tickAction 每 tick 最多 +1 HP（ConstructionSpeed 只决定「多久敲一下」，
    /// 代理已把它压到每 tick 一次），因此修满 900 HP 的建筑要数百 tick。这里在 StartJob 之后直接把
    /// 目标补满；原版修复 toil 会在下一次 tickAction 发现自己已满血，照常执行
    /// Notify_BuildingRepaired、ThingsRepaired 记录与 EndCurrentJob(Succeeded)，
    /// 既不改变 Job 的结束语义，也天然幂等（已满血不做任何处理）。
    ///
    /// 只处理代理的 JobDefOf.Repair；targetA 不是 Building（已销毁、已被换掉）时保持原版行为。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_OmniWorkProxy_RepairInstantFill
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn, Job newJob)
        {
            if (newJob == null || newJob.def != JobDefOf.Repair) return;
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return;
            Building building = newJob.targetA.Thing as Building;
            if (building == null || building.Destroyed) return;
            if (building.HitPoints >= building.MaxHitPoints) return;
            building.HitPoints = building.MaxHitPoints;
        }
    }
}
