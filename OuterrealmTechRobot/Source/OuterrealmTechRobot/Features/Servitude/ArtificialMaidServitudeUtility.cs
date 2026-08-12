using System.Collections.Generic;
using RimWorld;
using Verse;

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
    }
}
