using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 侍奉系统共享工具：通用守卫判定 + 威胁扫描（供 ThinkNode_JobGiver_ServitudeBase 与 JobGiver_AMGuardMaster 复用，避免重复代码）。
    /// 性能：全部 O(1)/廉价判定；威胁扫描仅小半径径向，不做全图遍历。
    /// </summary>
    public static class ArtificialMaidServitudeUtility
    {
        /// <summary>
        /// 通用守卫快速失败链：def 短路 → 存活/生成 → 关系存在且主人同图 → 活动区域尊重 → 留守总开关。
        /// 不包含征召/高维分支（由调用方按模式决定）。
        /// </summary>
        public static bool CanServe(Pawn pawn, out CompArtificialMaid comp, out Pawn master, out ArtificialMaidServitudeManager mgr)
        {
            comp = null;
            master = null;
            mgr = null;

            // ① def 短路（女仆专属，非女仆不消耗任何额外判定）
            if (pawn.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return false;
            }

            // ② 存活/生成/地图
            if (pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.Map == null)
            {
                return false;
            }

            // ③ 关系存在、主人存活且同图
            mgr = ArtificialMaidServitudeManager.Get();
            if (mgr == null)
            {
                return false;
            }

            master = mgr.GetMaster(pawn);
            if (master == null || master.Dead || master.Downed || master.Map != pawn.Map)
            {
                return false;
            }

            // ④ 活动区域尊重
            if (pawn.playerSettings != null && pawn.playerSettings.RespectsAllowedArea)
            {
                Area area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                if (area != null && !area[master.Position])
                {
                    return false;
                }
            }

            // ⑤ 留守总开关
            comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp == null || comp.standbyMode)
            {
                return false;
            }

            return true;
        }

        /// <summary>在目标周围小半径内寻找与目标敌对的存活 Pawn（径向扫描 + ThingGrid，不做全图遍历）。</summary>
        public static Pawn FindHostileThreatNear(Pawn target, float radius)
        {
            Map map = target.Map;
            if (map == null)
            {
                return null;
            }

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(target.Position, radius, true))
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }

                List<Thing> things = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Pawn p && p.Spawned && !p.Dead && !p.Downed && p.HostileTo(target))
                    {
                        return p;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 跨图传送：主人已生成在地图而女仆在别处（其他图/未生成）时，将女仆生成到主人附近可站立格。
        /// 跳过守卫：商队中/容器中/被携带/正在携带/留守。低频调用方负责分频。
        /// </summary>
        public static bool TryTeleportToMaster(Pawn maid, Pawn master)
        {
            if (maid == null || master == null || maid.Dead || master.Dead || maid.Destroyed || master.Destroyed)
            {
                return false;
            }

            if (maid.Map == master.Map)
            {
                return false; // 同图（含双方均未生成）无需传送
            }

            // 主人不在任何地图（商队/世界）→ 无法传送
            Map targetMap = master.Map;
            if (targetMap == null)
            {
                return false;
            }

            // 女仆在商队中 → 不传送（尊重商队结构，避免拆散远行队）
            if (maid.GetCaravan() != null)
            {
                return false;
            }

            // 女仆在容器中（展示柜/运输盒等）→ 尊重收纳，不强制取出
            if (maid.ParentHolder != null && !(maid.ParentHolder is Map))
            {
                return false;
            }

            // 女仆被携带（ParentHolder 为其他 Pawn）→ 不传送
            if (maid.ParentHolder is Pawn)
            {
                return false;
            }

            // 女仆正在携带物品 → 不传送（避免丢失携带物）
            if (maid.carryTracker != null && maid.carryTracker.CarriedThing != null)
            {
                return false;
            }

            // 留守 → 不传送（完全静默）
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(maid);
            if (comp != null && comp.standbyMode)
            {
                return false;
            }

            // 传送：先验证落点再注销生成（避免落点无效时女仆悬空丢失）
            try
            {
                IntVec3 cell = CellFinder.RandomSpawnCellForPawnNear(master.Position, targetMap);
                if (!cell.IsValid || !cell.InBounds(targetMap))
                {
                    return false;
                }

                if (maid.Spawned)
                {
                    maid.DeSpawn();
                }

                GenSpawn.Spawn(maid, cell, targetMap);
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[OuterrealmTech] 侍奉跨图传送失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 立即到主人身边（征召联动 / 跨图后归位）：
        /// 异图 → 立即跨图传送；同图 → 优先 AutoBlink 瞬移到主人旁（冷却内置），失败则立即走向主人。
        /// 事件驱动调用（征召瞬间），非轮询。
        /// </summary>
        public static void ImmediatelyJoinMaster(Pawn maid, Pawn master)
        {
            if (maid == null || master == null || maid.Dead || master.Dead || maid.Destroyed || master.Destroyed)
            {
                return;
            }

            // 异图：立即传送（含柜中唤醒后异图场景）
            if (maid.Map != master.Map)
            {
                TryTeleportToMaster(maid, master);
                return;
            }

            if (!maid.Spawned || !master.Spawned)
            {
                return;
            }

            // 已紧贴（≤10 格）无需瞬移，交给守卫/跟随的持续紧跟
            if (maid.Position.DistanceToSquared(master.Position) <= 10f * 10f)
            {
                return;
            }

            // 优先瞬移（AutoBlink 冷却允许时）
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(maid);
            if (comp != null && comp.TryBlinkToTarget(master))
            {
                return;
            }

            // 冷却中/瞬移失败 → 立即走向主人
            if (maid.jobs != null)
            {
                maid.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, master.Position), JobTag.Misc);
            }
        }
    }
}
