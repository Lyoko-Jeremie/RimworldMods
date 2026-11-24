using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 把超维的「护盾覆盖格」与「随身温控 pawn」接进 AO 的公共接口：
    ///
    /// 1. IAtmosphereShield：AO 自己在结构载荷、可穿透物、屋顶、物品抛飞、pawn 抛飞、
    ///    踉跄、飞行物撞击七个点调 IsShielded，命中即整段豁免。我们只提供一个 O(1) 的
    ///    Covers（查护盾掩膜），不再逐个 patch AO 的内部循环。
    /// 2. IAtmospherePawnAirProvider：AO 的 PawnAtmosphere.TrySample 会先问所有 provider，
    ///    返回 true 就用我们给的气压/氧分压，替代整条缺氧/高氧/压力病/套装 UI 链路。
    ///
    /// 接口实例由 AoBridge.CreateForwardingProxy 动态生成，编译期不引用 AO。
    /// 注册失败时返回 false，调用方回退到旧的 Harmony 补丁。
    /// </summary>
    internal static class OmniAtmosphereApi
    {
        private const string InterfaceTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereInterface";
        private const string ShieldTypeName = "AtmosphereOverhaul.RimWorld.IAtmosphereShield";
        private const string PawnAirTypeName = "AtmosphereOverhaul.RimWorld.IAtmospherePawnAirProvider";

        private static bool _shieldTried;
        private static bool _shieldInstalled;
        private static bool _pawnAirTried;
        private static bool _pawnAirInstalled;

        internal static bool ShieldInstalled
        {
            get { return _shieldInstalled; }
        }

        internal static bool PawnAirInstalled
        {
            get { return _pawnAirInstalled; }
        }

        /// <summary>
        /// 注册护盾。成功返回 true，之后 AO 的所有风害/压差/飞行物判定都按格豁免。
        /// </summary>
        internal static bool TryInstallShield()
        {
            if (_shieldTried)
            {
                return _shieldInstalled;
            }
            _shieldTried = true;

            try
            {
                Type interfaceType = AccessTools.TypeByName(InterfaceTypeName);
                Type shieldType = AccessTools.TypeByName(ShieldTypeName);
                if (interfaceType == null || shieldType == null)
                {
                    return false;
                }

                MethodInfo register = AccessTools.Method(
                    interfaceType,
                    "RegisterShield",
                    new[] { shieldType });
                if (register == null)
                {
                    return false;
                }

                MethodInfo target = typeof(OmniAtmosphereApi).GetMethod(
                    nameof(CoversForApi),
                    BindingFlags.NonPublic | BindingFlags.Static);
                object proxy = AoBridge.CreateForwardingProxy(shieldType, "Covers", target);
                if (proxy == null)
                {
                    return false;
                }

                register.Invoke(null, new[] { proxy });
                _shieldInstalled = true;
                Log.Message("[AOPatch] 护盾风豁免已切到 AO 的 IAtmosphereShield 公共接口。");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(
                    "[AOPatch] 注册 IAtmosphereShield 失败，回退旧风害补丁："
                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 注册随身温控的气压/氧提供器。成功返回 true，之后 AO 的 PawnAtmosphere
        /// 对受保护 pawn 直接使用地图目标值。
        /// </summary>
        internal static bool TryInstallPawnAir()
        {
            if (_pawnAirTried)
            {
                return _pawnAirInstalled;
            }
            _pawnAirTried = true;

            try
            {
                Type interfaceType = AccessTools.TypeByName(InterfaceTypeName);
                Type providerType = AccessTools.TypeByName(PawnAirTypeName);
                if (interfaceType == null || providerType == null)
                {
                    return false;
                }

                MethodInfo register = AccessTools.Method(
                    interfaceType,
                    "RegisterPawnAirProvider",
                    new[] { providerType });
                if (register == null)
                {
                    return false;
                }

                MethodInfo target = typeof(OmniAtmosphereApi).GetMethod(
                    nameof(TryGetAirForApi),
                    BindingFlags.NonPublic | BindingFlags.Static);
                object proxy = AoBridge.CreateForwardingProxy(providerType, "TryGetAir", target);
                if (proxy == null)
                {
                    return false;
                }

                register.Invoke(null, new[] { proxy });
                _pawnAirInstalled = true;
                Log.Message("[AOPatch] 随身温控气压/氧已切到 AO 的 IAtmospherePawnAirProvider 公共接口。");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(
                    "[AOPatch] 注册 IAtmospherePawnAirProvider 失败，回退旧补丁："
                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// IAtmosphereShield.Covers 的静态转发入口。
        /// AO 在风害 tick（主线程）高频调用，必须 O(1)：直接查护盾掩膜。
        /// </summary>
        private static bool CoversForApi(Map map, IntVec3 cell)
        {
            return OmniProtection.IsShieldProtected(cell, map);
        }

        /// <summary>
        /// IAtmospherePawnAirProvider.TryGetAir 的静态转发入口。
        /// 只有启用了随身温控的 pawn 才接管；其余返回 false，让 AO 走原逻辑。
        /// </summary>
        private static bool TryGetAirForApi(Pawn pawn, out float pressureHpa, out float oxygenHpa)
        {
            pressureHpa = 0f;
            oxygenHpa = 0f;

            if (pawn == null || !OmniProtection.IsPersonalProtected(pawn))
            {
                return false;
            }

            Map map = pawn.MapHeld;
            MapComponent_OmniAtmosphereTargets targets = MapComponent_OmniAtmosphereTargets.Get(map);

            float ambientPa = targets != null ? targets.AmbientPressureHpa * 100f : 0f;
            float targetPa = targets != null ? targets.ResolveTargetPressurePa(ambientPa) : 101325f;
            float oxygenPercent = targets != null ? targets.TargetOxygenPercent : 21f;

            pressureHpa = targetPa / 100f;
            oxygenHpa = pressureHpa * oxygenPercent / 100f;
            return true;
        }
    }
}
