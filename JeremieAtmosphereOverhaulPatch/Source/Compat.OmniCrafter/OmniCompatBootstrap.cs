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
            if (!Patch_OmniBuildingAtmosphere.Install(harmony))
            {
                Log.Warning("[AOPatch] 超维建筑风免疫未挂载。");
            }

            if (!Patch_OmniWindExemption.Install(harmony))
            {
                Log.Warning("[AOPatch] 护盾风豁免未完整挂载。");
            }

            if (!Patch_OmniPawnProtection.Install(harmony))
            {
                Log.Warning("[AOPatch] 随身气压/氧保护未挂载。");
            }

            if (!AtmosphereFieldBridge.SixChannelAvailable)
            {
                Log.Warning("[AOPatch] 超维科技穹顶绝对隔离停用：AO 六通道成员不完整。原因："
                    + AtmosphereFieldBridge.SixChannelFailure);
                InstallMapCleanup(harmony);
                return;
            }

            bool isolation = Patch_AbsoluteIsolation.Install(harmony);
            if (!isolation)
            {
                Log.Warning("[AOPatch] 穹顶绝对隔离补丁未能挂载，AO 场不会按目标值硬固定。");
            }

            bool gizmos = Patch_OmniAtmosphereGizmos.Install(harmony);
            if (!gizmos)
            {
                Log.Warning("[AOPatch] 部分超维科技 UI 补丁挂载失败，目标值面板可能不完整。");
            }

            bool temperature = Patch_OmniAtmosphereTemperature.Install(harmony);
            if (!temperature)
            {
                Log.Warning("[AOPatch] 温度读数覆盖补丁挂载失败，穹顶/生物圈读数可能与 AO 场不一致。");
            }

            InstallMapCleanup(harmony);
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
