using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 超维科技兼容模块的安装入口。
    ///
    /// 这个程序集只在超维科技同时启用时由 LoadFolders 加载，
    /// 所以这里可以直接引用它的类型；但 AO 是否在位仍要运行时判断，
    /// 缺 AO 时整套 AO 专用补丁静默跳过，不影响超维原功能。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OmniCompatBootstrap
    {
        static OmniCompatBootstrap()
        {
            AOPatchMod mod = AOPatchMod.Instance;
            if (mod == null)
            {
                return;
            }

            if (AOPatchMod.Settings != null && !AOPatchMod.Settings.MasterEnabled)
            {
                return;
            }

            Harmony harmony = AOPatchMod.HarmonyInstance;
            if (harmony == null)
            {
                harmony = new Harmony(AOPatchMod.HarmonyId + ".OmniCrafter");
                AOPatchMod.HarmonyInstance = harmony;
            }

            // 条目 1/2/5 只依赖 AO 的风害与 pawn 通道，不需要六通道能力。
            if (!SafeInstall("超维建筑风免疫", harmony, Patch_OmniBuildingAtmosphere.Install))
            {
                Log.Warning("[AOPatch] 超维建筑风免疫未挂载。");
            }

            if (!SafeInstall("护盾风豁免", harmony, Patch_OmniWindExemption.Install))
            {
                Log.Warning("[AOPatch] 护盾风豁免未完整挂载。");
            }

            if (!SafeInstall("随身气压/氧保护", harmony, Patch_OmniPawnProtection.Install))
            {
                Log.Warning("[AOPatch] 随身气压/氧保护未挂载。");
            }

            if (!AtmosphereFieldBridge.SixChannelAvailable)
            {
                Log.Warning("[AOPatch] 超维科技穹顶绝对隔离停用：AO 六通道成员不完整。原因："
                    + AtmosphereFieldBridge.SixChannelFailure);
                SafeInstallMapCleanup(harmony);
                return;
            }

            if (!SafeInstall("穹顶绝对隔离", harmony, Patch_AbsoluteIsolation.Install))
            {
                Log.Warning("[AOPatch] 穹顶绝对隔离补丁未能挂载，AO 场不会按目标值硬固定。");
            }

            if (!SafeInstall("穹顶几何隔离", harmony, Patch_OmniGeometricIsolation.Install))
            {
                Log.Warning("[AOPatch] 穹顶几何隔离未挂载：AO 面开度接口不完整，隔离开关将不生效。");
            }

            if (!SafeInstall("目标值面板", harmony, Patch_OmniAtmosphereGizmos.Install))
            {
                Log.Warning("[AOPatch] 部分超维科技 UI 补丁挂载失败，目标值面板可能不完整。");
            }

            if (!SafeInstall("温度读数覆盖", harmony, Patch_OmniAtmosphereTemperature.Install))
            {
                Log.Warning("[AOPatch] 温度读数覆盖补丁挂载失败，穹顶/生物圈读数可能与 AO 场不一致。");
            }

            SafeInstallMapCleanup(harmony);
            Log.Message("[AOPatch/OT] 超维科技兼容补丁已安装。");
        }

        /// <summary>
        /// 单个补丁组安装失败不应拖垮其它组：Harmony 的参数绑定错误、字段缺失等都会抛异常，
        /// 一旦从静态构造函数抛出，整个兼容程序集都不会再被初始化（所有 OT 功能一起消失）。
        /// </summary>
        private static bool SafeInstall(string label, Harmony harmony, Func<Harmony, bool> install)
        {
            try
            {
                return install(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[AOPatch] " + label + " 安装异常：" + e);
                return false;
            }
        }

        private static void SafeInstallMapCleanup(Harmony harmony)
        {
            try
            {
                InstallMapCleanup(harmony);
            }
            catch (Exception e)
            {
                Log.Warning("[AOPatch] 地图卸载清理补丁安装异常：" + e);
            }
        }

        /// <summary>
        /// 地图卸载时扔掉穹顶格缓存，避免静态字典把 Map 对象钉在内存里。
        /// </summary>
        private static void InstallMapCleanup(Harmony harmony)
        {
            MethodInfo dispose = AccessTools.Method(typeof(Map), "Dispose", Type.EmptyTypes);
            if (dispose == null)
            {
                return;
            }

            harmony.Patch(dispose, postfix: new HarmonyMethod(
                typeof(OmniCompatBootstrap), nameof(MapDisposePostfix)));
        }

        private static void MapDisposePostfix(Map __instance)
        {
            OmniDomeZoneCache.Forget(__instance);
            Patch_AbsoluteIsolation.Forget(__instance);
            OmniProtection.Forget(__instance);
        }
    }
}
