using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 条目 5：随身温控覆盖的 pawn 不受 AO 环境影响。
    ///
    /// 温度与体感温度已经由超维自己的 Thing.AmbientTemperature / SafeTemperatureAtCell
    /// 补丁覆盖（Priority.Last，晚于 AO 的 800），这里补的是 AO 独有的通道：
    ///
    /// - 气压与氧气：AO 的所有低压/高压/低氧/高氧 hediff 都从 PawnAtmosphere.TrySample 取数，
    ///   在 Prefix 里直接返回地图目标值即可让整条链路（TryGetBodyAir、套装 UI、呼吸供给）一致；
    /// - 风：踉跄与抛飞已在 Patch_OmniWindExemption 里按 IsPawnWindProtected 一并豁免。
    /// </summary>
    internal static class Patch_OmniPawnProtection
    {
        internal static bool Install(Harmony harmony)
        {
            // 优先走公共接口：AO 的 PawnAtmosphere.TrySample 会先问所有 provider，
            // 返回 true 就直接用我们给的气压/氧分压，覆盖整条缺氧/高氧/压力病链路。
            if (OmniAtmosphereApi.TryInstallPawnAir())
            {
                return true;
            }

            // 回退路径：AO 版本没有 IAtmospherePawnAirProvider 时 patch TrySample。
            Type type = AccessTools.TypeByName("AtmosphereOverhaul.Pawns.PawnAtmosphere");
            MethodInfo trySample = type == null ? null : AccessTools.Method(type, "TrySample");
            if (trySample == null)
            {
                Log.Warning("[AOPatch] 随身保护：未找到 AO PawnAtmosphere.TrySample，补丁未挂载。");
                return false;
            }

            harmony.Patch(trySample, prefix: new HarmonyMethod(
                typeof(Patch_OmniPawnProtection), nameof(TrySamplePrefix)));
            return true;
        }

        /// <summary>
        /// 随身 pawn 的气压/氧直接取地图目标值；返回 false 阻止原方法，避免再读 AO 场。
        /// </summary>
        private static bool TrySamplePrefix(Pawn pawn, ref bool __result,
            out float pressureHpa, out float oxygenHpa)
        {
            pressureHpa = 0f;
            oxygenHpa = 0f;

            if (pawn == null || !OmniProtection.IsPersonalProtected(pawn))
            {
                return true;
            }

            Map map = pawn.MapHeld;
            MapComponent_OmniAtmosphereTargets targets = MapComponent_OmniAtmosphereTargets.Get(map);

            float ambientPa = targets != null ? targets.AmbientPressureHpa * 100f : 0f;
            float targetPa = targets != null ? targets.ResolveTargetPressurePa(ambientPa) : 101325f;
            float oxygenPercent = targets != null ? targets.TargetOxygenPercent : 21f;

            pressureHpa = targetPa / 100f;
            oxygenHpa = pressureHpa * oxygenPercent / 100f;
            __result = true;
            return false;
        }
    }
}
