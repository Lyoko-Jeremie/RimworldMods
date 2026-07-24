using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆在地图与容器之间转移时使用的安全工具。
    /// </summary>
    public static class ArtificialMaidTransferUtility
    {
        /// <summary>
        /// 检查 Pawn 是否已正确生成在指定地图。
        /// </summary>
        public static bool IsSafelySpawned(Pawn pawn, Map map)
        {
            return pawn != null &&
                   map != null &&
                   !pawn.Destroyed &&
                   !pawn.Dead &&
                   pawn.Spawned &&
                   pawn.Map == map &&
                   pawn.ParentHolder == map;
        }

        /// <summary>
        /// 将 Pawn 生成到首选位置附近。Pawn 可以尚未生成，也可以仍由容器持有；
        /// 原版 GenSpawn 会在通过前置检查后再将其从容器移除。
        /// </summary>
        public static bool TrySpawnNear(Pawn pawn, Map map, IntVec3 preferredCell, out IntVec3 spawnedCell)
        {
            spawnedCell = IntVec3.Invalid;
            if (pawn == null || map == null || pawn.Destroyed || pawn.Dead)
            {
                return false;
            }

            if (IsSafelySpawned(pawn, map))
            {
                spawnedCell = pawn.Position;
                return true;
            }

            if (pawn.Spawned)
            {
                return false;
            }

            try
            {
                IntVec3 root = preferredCell.IsValid && preferredCell.InBounds(map)
                    ? preferredCell
                    : map.Center;

                // 与原版 RandomSpawnCellForPawnNear 保持一致：附近搜索失败时退回首选格。
                // Pawn 可以与制造者短暂处于同一格，生成后的寻路器会自行恢复位置。
                IntVec3 cell = CellFinder.RandomSpawnCellForPawnNear(root, map);

                Thing spawned = GenSpawn.Spawn(pawn, cell, map);
                if (spawned == pawn && IsSafelySpawned(pawn, map))
                {
                    spawnedCell = pawn.Position;
                    return true;
                }

                return false;
            }
            catch (System.Exception ex)
            {
                LogTransferFailure("SpawnNear", pawn, ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// 生成校验失败时输出各项状态，避免只有笼统错误而无法定位。
        /// </summary>
        public static string DescribeSpawnState(Pawn pawn, Map expectedMap)
        {
            if (pawn == null)
            {
                return "pawn=null";
            }

            return "destroyed=" + pawn.Destroyed +
                   ", dead=" + pawn.Dead +
                   ", discarded=" + pawn.Discarded +
                   ", spawned=" + pawn.Spawned +
                   ", mapMatches=" + (pawn.Map == expectedMap) +
                   ", parentHolder=" + pawn.ParentHolder?.ToStringSafe() +
                   ", def=" + pawn.def?.defName +
                   ", kindDef=" + pawn.kindDef?.defName;
        }

        /// <summary>
        /// 当地图恢复和容器恢复都失败时，将 Pawn 保存在世界 Pawn 列表中，
        /// 避免对象完全脱离存档对象树。
        /// </summary>
        public static bool TryKeepInWorld(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                return false;
            }

            if (pawn.Spawned || pawn.ParentHolder != null)
            {
                return true;
            }

            if (Find.WorldPawns.Contains(pawn))
            {
                return true;
            }

            if (pawn.Discarded)
            {
                return false;
            }

            try
            {
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                return Find.WorldPawns.Contains(pawn);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 将未生成 Pawn 放入指定容器，并验证 ParentHolder 已正确建立。
        /// </summary>
        public static bool TryAddToContainer(Pawn pawn, ThingOwner container, IThingHolder expectedOwner)
        {
            if (pawn == null || container == null || expectedOwner == null || pawn.Spawned)
            {
                return false;
            }

            try
            {
                return container.TryAdd(pawn) &&
                       container.Contains(pawn) &&
                       pawn.ParentHolder == expectedOwner;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 记录转移失败，便于从日志中定位极端状态和 Mod 冲突。
        /// </summary>
        public static void LogTransferFailure(string operation, Pawn pawn, string detail)
        {
            Log.Error("[OuterrealmTechRobot] Artificial Maid transfer failed. Operation=" + operation +
                      ", pawn=" + pawn?.ToStringSafe() + ", detail=" + detail);
        }
    }
}
