using System;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 携带系统核心工具：让女仆把主人（含清醒的活人）抱起来随行移动。
    /// 借鉴 WolfeinMihoRatkinCarry 的"活物携带"方案——原版 carryTracker 只支持
    /// 倒地/昏迷者的正常背人（经 SplitOff 隐式 DeSpawn），清醒活人必须显式：
    ///   停 job → DeSpawn（从地图与 mapPawns 移除，停止一切自主行为）→ 塞入 innerContainer。
    /// 被抱者 ParentHolder 变为女仆的 carryTracker，Pawn.CarriedBy 自动指向女仆；
    /// 渲染由 Patch_CarryRender 手动绘制（被抱者已 DeSpawn，地图不画它）。
    /// 性能：全部 O(1)/字典/def 比较判定；CarrierHolding 用 FieldRefAccess 缓存字段访问，避免反射。
    /// </summary>
    public static class ArtificialMaidCarryUtility
    {
        /// <summary>缓存 Pawn_CarryTracker.pawn 字段访问（一次反射，之后零开销）。</summary>
        private static readonly AccessTools.FieldRef<Pawn_CarryTracker, Pawn> CarryTrackerPawnField =
            AccessTools.FieldRefAccess<Pawn_CarryTracker, Pawn>("pawn");

        /// <summary>女仆是否正抱着自己的主人（活体携带）。</summary>
        public static bool IsCarryingMaster(Pawn maid)
        {
            if (maid == null || maid.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return false;
            }

            if (!(maid.carryTracker?.CarriedThing is Pawn carried))
            {
                return false;
            }

            return ArtificialMaidServitudeManager.Get()?.GetMaster(maid) == carried;
        }

        /// <summary>该 pawn 是否正被其主人的女仆抱着。</summary>
        public static bool IsMasterCarriedByMaid(Pawn pawn)
        {
            return pawn != null && CarrierHolding(pawn) is Pawn maid && IsCarryingMaster(maid);
        }

        /// <summary>谁在抱着该 pawn（ParentHolder 为 Pawn_CarryTracker 时返回载体，否则 null）。</summary>
        public static Pawn CarrierHolding(Pawn pawn)
        {
            if (pawn?.ParentHolder is Pawn_CarryTracker tracker && tracker.CarriedThing == pawn)
            {
                return CarryTrackerPawnField(tracker);
            }

            return null;
        }

        /// <summary>
        /// 抱起主人（核心三步）：中断主人行为 → 从地图移除（不销毁）→ 塞入女仆携带容器。
        /// 失败则把主人还原回原位置（绝不让主人凭空消失）。
        /// </summary>
        public static bool TryStartCarryMaster(Pawn maid, Pawn master)
        {
            if (maid == null || master == null || maid.carryTracker == null ||
                maid.carryTracker.CarriedThing != null || master.carryTracker?.CarriedThing != null)
            {
                return false;
            }

            Map map = master.Map;
            IntVec3 position = master.Position;
            if (master.Spawned)
            {
                // Pawn.DeSpawn 覆写：jobs.StopAll + 从 mapPawns 反注册 + 组件移除，行为完全停止
                master.DeSpawn(DestroyMode.Vanish);
            }

            if (maid.carryTracker.innerContainer.TryAdd(master, true))
            {
                return true;
            }

            // 失败还原
            if (!master.Spawned && map != null && position.IsValid)
            {
                GenSpawn.Spawn(master, position, map);
            }

            return false;
        }

        /// <summary>放下主人（经 DropGuard 放行，避免被掉落拦截补丁误拦）。</summary>
        public static void DropCarriedMaster(Pawn maid)
        {
            if (maid?.carryTracker?.CarriedThing == null)
            {
                return;
            }

            ArtificialMaidCarryDropGuard.AllowDropNow(maid, () =>
                maid.carryTracker.TryDropCarriedThing(maid.Position, ThingPlaceMode.Near, out Thing _));
        }

        /// <summary>同步被抱主人位置/朝向到女仆（携带期间调用；渲染基于 drawLoc 自动跟随，此处仅维护数据一致性）。</summary>
        public static void SyncCarriedMasterPosition(Pawn maid)
        {
            if (!(maid?.carryTracker?.CarriedThing is Pawn carried))
            {
                return;
            }

            carried.Position = maid.Position;
            carried.Rotation = maid.Rotation;
        }
    }

    /// <summary>被抱主人手动绘制期间的状态标记（防递归/双绘，仿 WFRC_DrawState）。</summary>
    public static class ArtificialMaidCarryDrawState
    {
        public static bool Active;

        public static void Begin()
        {
            Active = true;
        }

        public static void End()
        {
            Active = false;
        }
    }

    /// <summary>
    /// 掉落拦截守卫（仿 WFRC_CarryDropGuard）：
    /// 游戏在征召/切换 job 时会强制调用 TryDropCarriedThing 丢弃携带物，
    /// 本守卫决定是否拦截——女仆抱着主人时，除"自己主动放下"与"原版合法携带 job"外一律拦截。
    /// ThreadStatic：允许同一线程内的主动放下动作（AllowDropNow 包裹），避免与游戏自身多线程渲染冲突。
    /// </summary>
    public static class ArtificialMaidCarryDropGuard
    {
        [ThreadStatic]
        private static Pawn allowedDropPawn;

        /// <summary>临时放行一次主动放下动作（try/finally 恢复）。</summary>
        public static void AllowDropNow(Pawn pawn, Action action)
        {
            Pawn prev = allowedDropPawn;
            allowedDropPawn = pawn;
            try
            {
                action();
            }
            finally
            {
                allowedDropPawn = prev;
            }
        }

        /// <summary>是否应拦截该携带者的丢弃请求。</summary>
        public static bool ShouldBlockDrop(Pawn pawn)
        {
            if (pawn == null || pawn == allowedDropPawn)
            {
                return false;
            }

            // 载体倒地/死亡：放行，让主人掉出来。
            // 原版 DropAndForbidEverything（Pawn_HealthTracker 倒地/死亡时）走 TryDropCarriedThing，
            // 若拦截则主人留在容器里，会随载体 ThingOwner 清理而永久消失。
            if (pawn.Dead || pawn.Downed)
            {
                return false;
            }

            // 原版合法携带/背人 job（救援/逮捕/送床等）一律放行，避免干扰原版流程
            string defName = pawn.CurJobDef?.defName;
            if (defName != null)
            {
                switch (defName)
                {
                    case "Arrest":
                    case "Capture":
                    case "CarryDownedPawnDrafted":
                    case "CarryDownedPawnToExit":
                    case "CarryDownedPawnToPortal":
                    case "CarryToCryptosleepCasket":
                    case "CarryToCryptosleepCasketDrafted":
                    case "CarryToPrisonerBedDrafted":
                    case "DeliverToBed":
                    case "EscortPrisonerToBed":
                    case "Rescue":
                    case "TakeDownedPawnToBedDrafted":
                    case "TakeToBedToOperate":
                    case "TakeWoundedPrisonerToBed":
                        return false;
                }
            }

            // 女仆自己的携带 job（护送/放床上）→ 放行
            if (defName == ArtificialMaidDefOf.AM_Job_CarryMaster?.defName)
            {
                return false;
            }

            // 其余场景：仅拦截"女仆抱着主人"时的丢弃（征召/战斗/换 job 时绝不丢下主人）
            return ArtificialMaidCarryUtility.IsCarryingMaster(pawn);
        }
    }
}
