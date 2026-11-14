using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Jeremie.AtmosphereOverhaulPatch.Patches;
using Jeremie.AtmosphereOverhaulPatch.ProxyHeat;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// 补丁安装入口。
    ///
    /// 必须延后到 StaticConstructorOnStartup：
    /// Mod 构造函数执行时 DefDatabase 尚未填充，此时 Harmony 建立 detour 会提前触发
    /// 被 patch 类型的静态构造，把它的 DefDatabase&lt;T&gt;.GetNamed 字段永久钉成 null。
    ///
    /// 安装失败一律降级为「不接管」并写日志，绝不抛异常中断加载：
    /// AO / ProxyHeat 未装、改名、版本不匹配时，游戏仍应能正常开局。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            AOPatchMod mod = AOPatchMod.Instance;
            if (mod == null)
            {
                return;
            }

            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings != null && !settings.MasterEnabled)
            {
                return;
            }

            Harmony harmony = AOPatchMod.HarmonyInstance;
            if (harmony == null)
            {
                harmony = new Harmony(AOPatchMod.HarmonyId);
                AOPatchMod.HarmonyInstance = harmony;
            }

            InstallTempOutputShape(harmony);
            InstallProxyHeatRelay(harmony, settings);

            // 这一项不依赖 AO / ProxyHeat，也不依赖 Replace Stuff：
            // 它修的是原版 Building.DeSpawn 会清空同格 edificeGrid 的缺陷。
            InstallEdificeGridRestore(harmony);
        }

        /// <summary>
        /// 安装 edificeGrid 回填修复。
        ///
        /// 原版 EdificeGrid 每格只存一个 Building：DeSpawn 的 DeRegister 会把占格全部写成
        /// null，同格另一个 edifice（典型是墙与壁挂通风口）就此从 edificeGrid 消失。
        /// AO 的 obstacleMask 直接读 edificeGrid，会把这格误判成空气可自由通行。
        /// </summary>
        private static void InstallEdificeGridRestore(Harmony harmony)
        {
            if (!EdificeGridRestorePatch.Install(harmony))
            {
                Log.Warning("[AOPatch] 未能挂载 Verse.Building.DeSpawn，edificeGrid 回填修复停用。");
            }
        }

        private static void InstallTempOutputShape(Harmony harmony)
        {
            AoBridge.Resolve();
            if (!AoBridge.Available)
            {
                Log.Warning("[AOPatch] AO 连续源 API 不完整，温控输出形状模块停用。原因：" + AoBridge.Failure);
                return;
            }

            if (!TempOutputShapePatch.Install(harmony))
            {
                Log.Warning("[AOPatch] 未能挂载 FacilityPatchUtility.RunTempControl，温控输出形状模块停用。");
            }
        }

        private static void InstallProxyHeatRelay(Harmony harmony, AOPatchSettings settings)
        {
            if (settings != null && !settings.ProxyHeatRelayEnabled)
            {
                return;
            }

            ProxyHeatBridge.Resolve();
            if (!ProxyHeatBridge.Available)
            {
                Log.Warning("[AOPatch] ProxyHeat 未安装或成员不完整，范围效果模块停用。原因：" + ProxyHeatBridge.Failure);
                return;
            }

            if (!AtmosphereFieldBridge.Available)
            {
                Log.Warning("[AOPatch] AO 模拟场 API 不完整，范围效果模块停用。原因：" + AtmosphereFieldBridge.Failure);
                return;
            }

            bool ok = ProxyHeatRelayPatches.Install(harmony);
            if (!ok)
            {
                Log.Warning("[AOPatch] 部分范围效果补丁挂载失败，功能可能不完整。");
            }

            // ProxyHeatMod.settings 要等 Mod 实例构造完成，延后到加载收尾再强制关闭读值层。
            LongEventHandler.ExecuteWhenFinished(ProxyHeatPostInit);
        }

        /// <summary>
        /// 加载收尾：关掉 ProxyHeat 自己的读值层，并保证照射区包含室内格。
        ///
        /// enableProxyHeatEffectIndoors 必须为 true：CompTemperatureSource.CanWorkIn 靠它
        /// 决定室内格是否进 AffectedCells；为 false 时室内格根本不进照射区，
        /// 逐建筑「室内也生效」开关就没有东西可注入。
        /// </summary>
        private static void ProxyHeatPostInit()
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.MasterEnabled || !settings.ProxyHeatRelayEnabled)
            {
                return;
            }
            if (!ProxyHeatBridge.Available)
            {
                return;
            }

            ProxyHeatBridge.SetEnableProxyHeat(false);
            ProxyHeatBridge.SetEnableIndoors(true);

            VtePatchDetector.WarnIfPresent();
        }
    }
}
