using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维移动开雾工具：
    /// - 使用原版 FloodUnfogAdjacent 展开当前位置相连的迷雾房间；
    /// - 再按玩家为该女仆设置的半径打开圆形范围；
    /// - 由 CompArtificialMaid.CompTick 在换格时调用，不从并行渲染路径修改地图数据。
    /// </summary>
    public static class ArtificialMaidHighDimFogUtility
    {
        public const int DefaultRevealRadius = 5;
        public const int MinRevealRadius = 1;
        public const int MaxRevealRadius = 500;

        public static int ClampRevealRadius(int radius)
        {
            return radius < MinRevealRadius
                ? MinRevealRadius
                : radius > MaxRevealRadius ? MaxRevealRadius : radius;
        }

        /// <summary>打开当前位置相连的房间，并揭开指定半径内的圆形区域。</summary>
        public static void RevealAt(
            Map map,
            IntVec3 center,
            int radius,
            IntVec3 previousCenter,
            int previousRadius)
        {
            if (map == null || !center.InBounds(map))
            {
                return;
            }

            FogGrid fogGrid = map.fogGrid;

            // 高维女仆可能停在墙体中；只有处于可行走格时才按原版逻辑展开房间，
            // 避免穿墙途中同时揭开墙体两侧的房间。
            if (center.Walkable(map))
            {
                fogGrid.FloodUnfogAdjacent(center, false);
            }

            radius = ClampRevealRadius(radius);
            long radiusSquared = (long)radius * radius;
            bool hasPreviousCircle = previousCenter.IsValid
                && previousRadius >= MinRevealRadius;
            long previousRadiusSquared = (long)previousRadius * previousRadius;

            // 当前圆完全包含在上一次已处理的圆内时，没有新的半径格需要检查。
            if (hasPreviousCircle && previousRadius >= radius)
            {
                long centerDx = center.x - previousCenter.x;
                long centerDz = center.z - previousCenter.z;
                long radiusDifference = previousRadius - radius;
                if (centerDx * centerDx + centerDz * centerDz
                    <= radiusDifference * radiusDifference)
                {
                    return;
                }
            }

            // 半径覆盖地图四角时直接使用原版整图开雾，避免扫描超大包围盒。
            int maxX = map.Size.x - 1;
            int maxZ = map.Size.z - 1;
            if (IsWithinRadius(center, 0, 0, radiusSquared)
                && IsWithinRadius(center, maxX, 0, radiusSquared)
                && IsWithinRadius(center, 0, maxZ, radiusSquared)
                && IsWithinRadius(center, maxX, maxZ, radiusSquared))
            {
                fogGrid.ClearAllFog();
                return;
            }

            // 将扫描矩形裁剪到地图内部，且移动时跳过上一次圆内的重叠区域。
            int minX = center.x - radius < 0 ? 0 : center.x - radius;
            int scanMaxX = center.x + radius > maxX ? maxX : center.x + radius;
            int minZ = center.z - radius < 0 ? 0 : center.z - radius;
            int scanMaxZ = center.z + radius > maxZ ? maxZ : center.z + radius;
            for (int z = minZ; z <= scanMaxZ; z++)
            {
                long dz = z - center.z;
                long remainingSquared = radiusSquared - dz * dz;
                int halfWidth = IntegerSqrt(remainingSquared);
                int rowMinX = center.x - halfWidth < minX ? minX : center.x - halfWidth;
                int rowMaxX = center.x + halfWidth > scanMaxX ? scanMaxX : center.x + halfWidth;

                for (int x = rowMinX; x <= rowMaxX; x++)
                {
                    if (hasPreviousCircle && IsWithinRadius(previousCenter, x, z, previousRadiusSquared))
                    {
                        continue;
                    }

                    IntVec3 cell = new IntVec3(x, 0, z);
                    if (fogGrid.IsFogged(cell))
                    {
                        fogGrid.Unfog(cell);
                    }
                }
            }
        }

        private static bool IsWithinRadius(IntVec3 center, int x, int z, long radiusSquared)
        {
            long dx = x - center.x;
            long dz = z - center.z;
            return dx * dx + dz * dz <= radiusSquared;
        }

        /// <summary>无分配整数平方根，用于计算圆形扫描区间。</summary>
        private static int IntegerSqrt(long value)
        {
            int result = (int)System.Math.Sqrt(value);
            while ((long)(result + 1) * (result + 1) <= value)
            {
                result++;
            }
            while ((long)result * result > value)
            {
                result--;
            }
            return result;
        }
    }
}
