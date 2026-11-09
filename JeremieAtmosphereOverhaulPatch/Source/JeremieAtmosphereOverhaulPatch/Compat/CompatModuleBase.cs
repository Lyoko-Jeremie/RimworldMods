using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Util;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 兼容模块的公共基类。
    ///
    /// 把「生命周期」与「设置读写」放在宿主里，具体补丁逻辑留给各兼容 DLL：
    ///   LoadSetting → Apply（总开关 / 用户开关 / 目标是否存在）→ TryApply
    ///
    /// 每个模块使用独立的 Harmony id，Revert 时只卸载自己，
    /// 因此「单独回退某个模块」不会波及其它模块。
    /// </summary>
    public abstract class CompatModuleBase : ICompatModule
    {
        private Harmony harmony;

        public abstract string PackageId { get; }

        public abstract string LabelKey { get; }

        public abstract string HintKey { get; }

        /// <summary>默认按类型名生成，保证每个模块互不干扰。</summary>
        public virtual string HarmonyId
        {
            get { return "Jeremie.AtmosphereOverhaulPatch." + GetType().Name; }
        }

        public bool EnabledByUser { get; private set; }

        public CompatState State { get; protected set; } = CompatState.Disabled;

        public string Detail { get; protected set; }

        /// <summary>本模块独占的 Harmony 实例，首次使用时惰性创建。</summary>
        protected Harmony Harmony
        {
            get { return harmony ?? (harmony = new Harmony(HarmonyId)); }
        }

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

        protected abstract bool ReadSetting();

        protected abstract void WriteSetting(bool value);

        public void LoadSetting()
        {
            try
            {
                EnabledByUser = ReadSetting();
            }
            catch
            {
                EnabledByUser = false;
            }
        }

        public void SetEnabledByUser(bool value)
        {
            EnabledByUser = value;
            try
            {
                WriteSetting(value);
            }
            catch (Exception exception)
            {
                PatchLog.Warn("写入模块开关失败（" + PackageId + "）：" + exception.Message);
            }
        }

        public void Apply()
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
                State = TryApply(Harmony) ? CompatState.Applied : CompatState.Skipped;
            }
            catch (Exception exception)
            {
                State = CompatState.Failed;
                Detail = exception.GetType().Name + ": " + exception.Message;
                PatchLog.Error("模块 " + PackageId + " 安装补丁失败", exception);
            }
        }

        public void Revert()
        {
            if (harmony == null)
            {
                State = CompatState.Disabled;
                return;
            }

            try
            {
                harmony.UnpatchAll(HarmonyId);
                PatchLog.Debug("模块 " + PackageId + " 的补丁已卸载。");
            }
            catch (Exception exception)
            {
                PatchLog.Warn("模块 " + PackageId + " 卸载失败：" + exception.Message);
            }

            State = CompatState.Disabled;
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
