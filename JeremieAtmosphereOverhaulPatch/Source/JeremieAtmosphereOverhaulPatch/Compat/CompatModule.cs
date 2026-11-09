using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Util;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 兼容模块基类。
    ///
    /// 每个目标 Mod 对应一个模块，生命周期统一为：
    ///   LoadSetting → Apply（检查总开关 / 用户开关 / 目标是否存在）→ TryApply
    ///
    /// 之所以把「目标是否存在」放在 TryApply 之前，是为了让任意子集（包括全都不存在）
    /// 都能正常工作：缺少目标时状态为 NotInstalled，不做任何反射与补丁动作。
    /// </summary>
    internal abstract class CompatModule
    {
        /// <summary>目标 Mod 的 packageId（用于 ModLister 检测）。</summary>
        public abstract string PackageId { get; }

        /// <summary>设置界面上显示的名称（i18n key）。</summary>
        public abstract string LabelKey { get; }

        /// <summary>设置界面上显示的说明（i18n key）。</summary>
        public abstract string HintKey { get; }

        public bool EnabledByUser { get; set; }

        public CompatState State { get; protected set; } = CompatState.Disabled;

        /// <summary>附加诊断信息（缺失的成员、异常摘要等），可为 null。</summary>
        public string Detail { get; protected set; }

        public bool IsTargetLoaded
        {
            get
            {
                try
                {
                    return ModLister.GetActiveModWithIdentifier(PackageId, true) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>从设置里读取本模块的用户开关。</summary>
        protected abstract bool ReadSetting();

        /// <summary>把用户开关写回设置。</summary>
        protected abstract void WriteSetting(bool value);

        public void LoadSetting()
        {
            EnabledByUser = ReadSetting();
        }

        public void SetEnabledByUser(bool value)
        {
            EnabledByUser = value;
            WriteSetting(value);
        }

        public void Apply(Harmony harmony)
        {
            Detail = null;

            if (AOPatchMod.Settings == null || !AOPatchMod.Settings.MasterEnabled)
            {
                State = CompatState.Disabled;
                Detail = "master-off";
                return;
            }

            if (!EnabledByUser)
            {
                State = CompatState.Disabled;
                return;
            }

            if (!IsTargetLoaded)
            {
                State = CompatState.NotInstalled;
                return;
            }

            try
            {
                State = TryApply(harmony) ? CompatState.Applied : CompatState.Skipped;
            }
            catch (Exception exception)
            {
                State = CompatState.Failed;
                Detail = exception.GetType().Name + ": " + exception.Message;
                PatchLog.Error("模块 " + PackageId + " 安装补丁失败", exception);
            }
        }

        /// <summary>安装补丁。返回 false 表示「跳过」（不算失败）。</summary>
        protected abstract bool TryApply(Harmony harmony);

        // ------------------------------------------------------------------
        // 子类共用的小工具
        // ------------------------------------------------------------------

        protected static Assembly FindAssembly(string simpleName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    if (assemblies[i].GetName().Name == simpleName)
                    {
                        return assemblies[i];
                    }
                }
                catch
                {
                    // 忽略无法读取元数据的程序集
                }
            }

            return null;
        }

        protected static int SafeTypeCount(Assembly assembly)
        {
            if (assembly == null)
            {
                return 0;
            }

            try
            {
                return assembly.GetTypes().Length;
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types == null ? 0 : exception.Types.Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
