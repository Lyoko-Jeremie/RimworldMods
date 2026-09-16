using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 「可穿过幻影墙」的视线判定工具。
    ///
    /// 实现方式是逐行复刻 Verse.GenSight 的 Bresenham 视线算法，
    /// 唯一的区别：幻影墙格不计为视线阻挡（其他建筑仍然阻挡）。
    ///
    /// 本工具不修改任何全局状态，只在「我方射击/索敌」的判定里被显式调用，
    /// 因此不会影响爆炸（GenExplosion / DamageWorker）、建造、社交、AI 危险区等
    /// 其它使用原版 LOS 的系统。
    ///
    /// 性能：全部使用局部变量与 struct，零堆分配、无 Linq。
    /// </summary>
    internal static class PhantomWallSightUtility
    {
        /// <summary>
        /// 等价于 GenSight.LineOfSight，但幻影墙格不阻挡视线。
        /// </summary>
        internal static bool LineOfSight(
            IntVec3 start,
            IntVec3 end,
            Map map,
            bool skipFirstCell = false,
            Func<IntVec3, bool> validator = null,
            int halfXOffset = 0,
            int halfZOffset = 0)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
                return false;

            bool flag = start.x != end.x ? start.x < end.x : start.z < end.z;
            int num1 = Mathf.Abs(end.x - start.x);
            int num2 = Mathf.Abs(end.z - start.z);
            int x = start.x;
            int z = start.z;
            int num3 = 1 + num1 + num2;
            int num4 = end.x > start.x ? 1 : -1;
            int num5 = end.z > start.z ? 1 : -1;
            int num6 = num1 * 4;
            int num7 = num2 * 4;
            int num8 = num6 + halfXOffset * 2;
            int num9 = num7 + halfZOffset * 2;
            int num10 = num8 / 2 - num9 / 2;

            IntVec3 c = default(IntVec3);
            for (; num3 > 1; --num3)
            {
                c.x = x;
                c.z = z;

                if ((!skipFirstCell || c != start)
                    && (IsBlockingVision(c, map) || (validator != null && !validator(c))))
                    return false;

                if (num10 > 0 || (num10 == 0 && flag))
                {
                    x += num4;
                    num10 -= num9;
                }
                else
                {
                    z += num5;
                    num10 += num8;
                }
            }
            return true;
        }

        /// <summary>
        /// 等价于 GenSight.LineOfSightToEdges，但幻影墙格不阻挡视线。
        /// </summary>
        internal static bool LineOfSightToEdges(
            IntVec3 start,
            IntVec3 end,
            Map map,
            bool skipFirstCell = false,
            Func<IntVec3, bool> validator = null)
        {
            if (LineOfSight(start, end, map, skipFirstCell, validator))
                return true;

            int squared = (start * 2).DistanceToSquared(end * 2);
            for (int i = 0; i < 4; i++)
            {
                if ((start * 2).DistanceToSquared(end * 2 + GenAdj.CardinalDirections[i]) <= squared
                    && LineOfSight(start, end, map, skipFirstCell, validator,
                        GenAdj.CardinalDirections[i].x, GenAdj.CardinalDirections[i].z))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 该格是否阻挡视线。幻影墙格视为不阻挡。
        /// </summary>
        private static bool IsBlockingVision(IntVec3 c, Map map)
        {
            // CanBeSeenOverFast 返回 true 表示该格上方可见（不阻挡）。
            if (c.CanBeSeenOverFast(map))
                return false;

            return !PhantomWallCombatRules.IsPhantomWallAt(c, map);
        }
    }
}
