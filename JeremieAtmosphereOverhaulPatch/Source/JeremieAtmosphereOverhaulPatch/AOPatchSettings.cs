using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// 本 Mod 的设置。
    ///
    /// 骨架阶段只保留总开关；后续每个模块接入时再各自追加字段，
    /// 字段名一旦发布就不要改，否则老存档的设置会丢。
    /// </summary>
    public class AOPatchSettings : ModSettings
    {
        /// <summary>总开关。关闭时任何模块都不安装补丁。</summary>
        public bool MasterEnabled = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref MasterEnabled, "masterEnabled", true);
        }
    }
}
