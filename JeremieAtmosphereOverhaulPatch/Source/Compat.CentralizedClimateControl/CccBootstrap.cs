using System;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// CCC 兼容层的安装入口。
    /// 只在 CCC 活跃时这个程序集才会被加载（LoadFolders 的条件目录），所以这里不做模型存在性检查。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CccBootstrap
    {
        static CccBootstrap()
        {
            if (AOPatchMod.Instance == null)
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
                return;
            }

            AoBridge.Resolve();
            if (!AoBridge.VolumeSourceAvailable)
            {
                Log.Warning("[AOPatch/CCC] AO 体积源 API 不完整，气袋停用，CCC 退回原版行为。原因：" + AoBridge.Failure);
                return;
            }

            try
            {
                harmony.PatchAll(typeof(CccBootstrap).Assembly);
                Log.Message("[AOPatch/CCC] 气流回路已接管：气袋按 AO 模拟步推进，原版三处 TickRare 已拦截。");
            }
            catch (Exception e)
            {
                Log.Error("[AOPatch/CCC] 接管失败，已退回原版行为：" + e);
            }
        }
    }
}
