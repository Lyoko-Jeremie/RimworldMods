using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 穹顶 / 生物圈的「几何隔离」：把覆盖格在 AO 的障碍掩膜里置为不可通行，
    /// 使压力、速度、温度、湿度、气体都无法穿过边界。
    ///
    /// 挂 <c>MapObstacleMaskBuilder.Rebuild</c> 的 Postfix：
    /// 1. 这是 AO 每次掩码重建的唯一入口（AtmosphereMapComponent.RebuildObstacleMaskUnsafe 内）；
    /// 2. Postfix 位于 ReconcileOpenedCells 之前，冻结格的新掩码为 0，
    ///    不会被当作「新开放格」用四邻平均插值填充；
    /// 3. 只重算冻结格受影响的四个面，不能全量重算 faceOpen——
    ///    Rebuild 末尾 BuildVents 会为通风口写特殊面开度，全量重算会把它们冲掉。
    ///
    /// 与开关的关系：<c>UseGeometricIsolation</c> 只控制本模块是否写掩码。
    /// 六通道硬固定（Patch_AbsoluteIsolation）始终生效，不受本开关影响。
    ///
    /// 线程：Rebuild 只在主线程跑，掩码写入安全；模拟线程只读同一个数组。
    /// </summary>
    internal static class Patch_OmniGeometricIsolation
    {
        private static MethodInfo _rebuild;

        public static bool Install(Harmony harmony)
        {
            if (!AtmosphereFieldBridge.FaceOpenAvailable)
            {
                return false;
            }

            Type builderType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.MapObstacleMaskBuilder");
            Type engineType = AccessTools.TypeByName("AtmosphereOverhaul.Simulation.LocalAtmosphereEngine");
            if (builderType == null || engineType == null)
            {
                return false;
            }

            _rebuild = AccessTools.Method(builderType, "Rebuild", new[] { typeof(Map), engineType });
            if (_rebuild == null)
            {
                return false;
            }

            harmony.Patch(_rebuild, postfix: new HarmonyMethod(
                typeof(Patch_OmniGeometricIsolation), nameof(RebuildPostfix)));
            return true;
        }

        /// <summary>
        /// 主线程：掩码重建后按当前开关决定是否封住穹顶。
        /// 关闭隔离时不做任何事，掩码保持 AO 刚算出的自然值。
        /// </summary>
        private static void RebuildPostfix(Map __0, object __1)
        {
            Map map = __0;
            object engine = __1;
            if (map == null || engine == null || map.Disposed)
            {
                return;
            }

            MapComponent_OmniAtmosphereTargets targets = MapComponent_OmniAtmosphereTargets.Get(map);
            if (targets == null || !targets.UseGeometricIsolation)
            {
                return;
            }

            int[] cells = OmniDomeZoneCache.EnsureBuilt(map);
            if (cells.Length == 0)
            {
                return;
            }

            float[] mask = AtmosphereFieldBridge.GetObstacleMask(engine);
            float[] faceOpenX = AtmosphereFieldBridge.GetFaceOpenX(engine);
            float[] faceOpenZ = AtmosphereFieldBridge.GetFaceOpenZ(engine);
            int width = AtmosphereFieldBridge.GetWidth(engine);
            if (mask == null || faceOpenX == null || faceOpenZ == null || width <= 0)
            {
                return;
            }

            int size = mask.Length;
            for (int k = 0; k < cells.Length; k++)
            {
                int index = cells[k];
                if (index < 0 || index >= size)
                {
                    continue;
                }

                mask[index] = 0f;

                // 只动这个格四周的面，避免碰到 BuildVents 写好的通风口面开度。
                faceOpenX[index] = 0f;
                if (index % width > 0)
                {
                    faceOpenX[index - 1] = 0f;
                }

                faceOpenZ[index] = 0f;
                if (index >= width)
                {
                    faceOpenZ[index - width] = 0f;
                }
            }
        }
    }
}
