using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// 检测 VTE-ProxyHeatPatch（Harmony id <c>ewolfy.vtePatch</c>）。
    ///
    /// 这个补丁用 Transpiler 把 <c>ProxyHeatManager.GetTemperatureOutcomeFor</c> 的
    /// 室内提前返回删掉（brfalse_S → Nop，并删 Ldarg_2; Ret），导致 IL 栈不平衡。
    /// 一旦方法 JIT 失败，我们挂的任何 Prefix/Postfix 都不会生效——JIT 先于 Harmony。
    ///
    /// 所以只能提示玩家移除，不做兼容尝试。
    /// </summary>
    public static class VtePatchDetector
    {
        private const string AssemblyFragment = "VTE_ProxyHeatPatch";

        /// <summary>程序集是否已加载。不依赖 Harmony id 表，最省事也最稳。</summary>
        public static bool Detect()
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly == null)
                    {
                        continue;
                    }
                    AssemblyName name = assembly.GetName();
                    if (name == null || name.Name == null)
                    {
                        continue;
                    }
                    if (name.Name.IndexOf(AssemblyFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[AOPatch] 检测 VTE-ProxyHeatPatch 失败：" + e.GetType().Name + ": " + e.Message);
            }
            return false;
        }

        /// <summary>检测到就发一次蓝信。由设置的 WarnedVtePatch 去重。</summary>
        public static void WarnIfPresent()
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || settings.WarnedVtePatch)
            {
                return;
            }
            if (!Detect())
            {
                return;
            }
            settings.WarnedVtePatch = true;
            Find.LetterStack.ReceiveLetter(
                "AOPatch_VteProxyHeatTitle".Translate(),
                "AOPatch_VteProxyHeatDesc".Translate(),
                LetterDefOf.NeutralEvent);
        }
    }
}
