using System.Reflection;
using HarmonyLib;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// Centralized Climate Control (Continued)（Mlie.CentralizedClimateControl）兼容。
    ///
    /// 重要事实：该 Mod 目前（packageId Mlie.CentralizedClimateControl）的
    /// About.xml 只声明到 RimWorld 1.6，LoadFolders.xml 没有 &lt;v1.7&gt; 节点。
    /// 因此在 1.7 下它通常根本不会加载，本模块会自然落到 NotInstalled。
    ///
    /// 如果将来作者补上 1.7 支持，届时再把它的大管道/风机接入 AO 的持续源；
    /// 现在保持「检测 + 跳过」，避免对不存在的程序集做任何假设。
    /// </summary>
    public sealed class CentralizedClimateControlCompat : CompatModuleBase
    {
        private const string AssemblyName = "CentralizedClimateControl";

        public override string PackageId
        {
            get { return "Mlie.CentralizedClimateControl"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_CCC"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_CCCDesc"; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings != null && AOPatchMod.Settings.EnableCentralizedClimateControl;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableCentralizedClimateControl = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            Assembly assembly = FindAssembly(AssemblyName);
            if (assembly == null)
            {
                Detail = "assembly not loaded (expected on 1.7: mod declares up to 1.6)";
                return false;
            }

            Detail = "assembly " + assembly.GetName().Name + ", types " + SafeTypeCount(assembly) + ", no 1.7 support upstream";
            return false;
        }
    }
}
