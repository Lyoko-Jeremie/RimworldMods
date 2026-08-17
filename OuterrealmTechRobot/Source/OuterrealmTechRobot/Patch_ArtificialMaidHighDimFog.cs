using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维飞行"飞过即开雾"（仿渡鸦族 / ChezhouLib 的 UnfogAround 模式）：
    /// 高维女仆移动经过的位置，以当前位置为中心打开周围迷雾（固定半径 3 格方形）。
    /// - patch Pawn.DrawPos getter（每帧渲染调用），仅"位置发生变化"时才执行开雾（字典去重）；
    /// - 只对雾中格调用 fogGrid.Unfog（原版公开方法，自动刷新 FogOfWar|Things 网格与事件）；
    /// - 非高维女仆首行即返回；Dictionary 加锁（1.6 渲染存在并行路径，遵守多线程规范）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawPos), MethodType.Getter)]
    public static class Patch_Pawn_DrawPos_HighDimFog
    {
        /// <summary>开雾半径（格）：高维无高度概念，固定小半径。7x7 方格。</summary>
        private const int UnfogRadius = 3;

        /// <summary>每个高维女仆上次开雾的格子（thingIDNumber → cell），数量有限。</summary>
        private static readonly Dictionary<int, IntVec3> lastUnfoggedCell = new Dictionary<int, IntVec3>();

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (__instance == null || __instance.Dead || !__instance.Spawned || __instance.Map == null)
            {
                return;
            }

            if (!ArtificialMaidHighDimUtility.IsHighDim(__instance))
            {
                return;
            }

            IntVec3 pos = __instance.Position;
            lock (lastUnfoggedCell)
            {
                if (lastUnfoggedCell.TryGetValue(__instance.thingIDNumber, out IntVec3 last) && last == pos)
                {
                    return; // 未换格：不重复开雾
                }

                lastUnfoggedCell[__instance.thingIDNumber] = pos;
            }

            UnfogAround(__instance.Map, pos);
        }

        /// <summary>以中心格打开半径为 UnfogRadius 的方形迷雾。</summary>
        private static void UnfogAround(Map map, IntVec3 center)
        {
            FogGrid fogGrid = map.fogGrid;
            for (int i = -UnfogRadius; i <= UnfogRadius; i++)
            {
                for (int j = -UnfogRadius; j <= UnfogRadius; j++)
                {
                    IntVec3 c = center + new IntVec3(i, 0, j);
                    if (c.InBounds(map) && fogGrid.IsFogged(c))
                    {
                        fogGrid.Unfog(c);
                    }
                }
            }
        }
    }
}
