using System.Reflection;
using HarmonyLib;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// Vanilla Temperature Expanded（VanillaExpanded.Temperature）兼容。
    ///
    /// 现状：该 Mod 声明支持 1.7，1.7 复用其 1.6 目录，程序集为
    /// VanillaTemperatureExpanded.dll 与 ProxyHeat.dll。
    /// 它自己绕开 Room.Temperature，直接用 ProxyHeat 维护房间温度，
    /// 因此与 AO 的「房间温度写入钩子」不冲突，但也不会把温度写进 AO 的气场。
    ///
    /// 本模块目前只做「存在性 + 类型清单」诊断，行为补丁留待按需实现：
    /// 正确做法是为其温控设备注册 AO 的持续源（AtmosphereSourceRates），
    /// 而不是继续改 Room.Temperature（AO 地图上会被写钩子翻译成逐格注入）。
    /// </summary>
    internal sealed class VanillaTemperatureExpandedCompat : CompatModule
    {
        private const string AssemblyName = "VanillaTemperatureExpanded";

        public override string PackageId
        {
            get { return "VanillaExpanded.Temperature"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_VTE"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_VTEDesc"; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings != null && AOPatchMod.Settings.EnableVanillaTemperatureExpanded;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableVanillaTemperatureExpanded = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            Assembly assembly = FindAssembly(AssemblyName);
            if (assembly == null)
            {
                Detail = "assembly not loaded";
                return false;
            }

            Detail = "assembly " + assembly.GetName().Name + ", types " + SafeTypeCount(assembly) + ", behavior patch pending";
            return false;
        }
    }
}
