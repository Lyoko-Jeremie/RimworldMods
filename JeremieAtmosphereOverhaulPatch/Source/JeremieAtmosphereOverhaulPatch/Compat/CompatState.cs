namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>单个兼容模块的运行时状态。</summary>
    public enum CompatState
    {
        /// <summary>目标 Mod 未安装（或未以对应 packageId 激活）。</summary>
        NotInstalled,

        /// <summary>被设置或总开关关闭。</summary>
        Disabled,

        /// <summary>补丁已安装。</summary>
        Applied,

        /// <summary>目标存在，但本模块主动跳过（例如尚未实现行为补丁）。</summary>
        Skipped,

        /// <summary>安装过程抛出异常。</summary>
        Failed
    }
}
