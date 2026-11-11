using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Jeremie.AtmosphereOverhaulPatch.Patches;
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
    /// AO 未装、AO 改名、AO 版本不匹配时，游戏仍应能正常开局。
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

            if (AOPatchMod.Settings != null && !AOPatchMod.Settings.MasterEnabled)
            {
                return;
            }

            Harmony harmony = AOPatchMod.HarmonyInstance;
            if (harmony == null)
            {
                harmony = new Harmony(AOPatchMod.HarmonyId);
                AOPatchMod.HarmonyInstance = harmony;
            }

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
    }
}
