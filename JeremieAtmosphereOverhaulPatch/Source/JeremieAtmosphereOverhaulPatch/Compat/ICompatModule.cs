using HarmonyLib;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 兼容模块契约。
    ///
    /// 这是「宿主 DLL」与「各目标 Mod 的兼容 DLL」之间唯一的编译期依赖：
    /// - 宿主只认这个接口，不认任何具体兼容实现；
    /// - 每个兼容 DLL 各自实现一份，放在只有目标 Mod 存在时才会加载的条件目录里；
    /// - 宿主启动时用反射扫描已加载程序集，把实现收进注册表。
    ///
    /// 这样一来，兼容实现可以独立编译、独立删除、独立回退，
    /// 任何一个缺失或损坏都不会影响宿主与其它模块。
    /// </summary>
    public interface ICompatModule
    {
        /// <summary>目标 Mod 的 packageId（用于 ModLister 检测与设置界面匹配）。</summary>
        string PackageId { get; }

        /// <summary>设置界面上显示的名称（i18n key）。</summary>
        string LabelKey { get; }

        /// <summary>设置界面上显示的说明（i18n key）。</summary>
        string HintKey { get; }

        /// <summary>本模块独占的 Harmony id，用于精确卸载。</summary>
        string HarmonyId { get; }

        /// <summary>用户是否在设置里启用了本模块。</summary>
        bool EnabledByUser { get; }

        /// <summary>运行时状态。</summary>
        CompatState State { get; }

        /// <summary>附加诊断信息，可为 null。</summary>
        string Detail { get; }

        /// <summary>从设置里读取用户开关。</summary>
        void LoadSetting();

        /// <summary>把用户开关写回设置。</summary>
        void SetEnabledByUser(bool value);

        /// <summary>安装补丁（内部会依次检查总开关、用户开关、目标是否存在）。</summary>
        void Apply();

        /// <summary>卸载本模块安装的全部补丁。</summary>
        void Revert();
    }
}
