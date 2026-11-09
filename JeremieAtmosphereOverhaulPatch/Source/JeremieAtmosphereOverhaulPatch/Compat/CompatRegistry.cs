using System;
using System.Collections.Generic;
using System.Reflection;
using Jeremie.AtmosphereOverhaulPatch.Util;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 兼容模块注册表（宿主侧）。
    ///
    /// 与旧实现的区别：宿主不再编译期持有任何具体模块。
    /// 启动时扫描「已加载程序集」里所有 <see cref="ICompatModule"/> 实现，
    /// 与内置的模块清单按 packageId 配对：
    ///
    /// - 配上了：走该模块自己的 LoadSetting / Apply；
    /// - 没配上（目标 Mod 不在，条件目录未加载）：界面显示「未安装」，不做任何事。
    ///
    /// 因此「几个 Mod 都不存在」「只存在其中几个」都能正常工作，
    /// 且每个模块可以单独卸载、单独删除目录回退。
    /// </summary>
    public static class CompatRegistry
    {
        /// <summary>兼容 DLL 的程序集名前缀；新增兼容项目时请沿用此前缀。</summary>
        public const string CompatAssemblyPrefix = "Jeremie.AtmosphereOverhaulPatch";

        private static readonly List<CompatModuleInfo> Modules = new List<CompatModuleInfo>();
        private static bool initialized;

        public static IReadOnlyList<CompatModuleInfo> AllModules
        {
            get { return Modules; }
        }

        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            BuildInfos();
            DiscoverImplementations();
            ApplyAll();
            LogSummary();
        }

        /// <summary>
        /// 运行时重新安装全部补丁：先逐模块卸载，再逐模块安装。
        /// 用于「快速回退 / 重新启用」。
        /// </summary>
        public static void Reapply()
        {
            RevertAll();
            ApplyAll();
            LogSummary();
        }

        /// <summary>卸载全部模块（一键回退，不改设置）。</summary>
        public static void RevertAll()
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                ICompatModule module = Modules[i].Instance;
                if (module == null)
                {
                    continue;
                }

                try
                {
                    module.Revert();
                }
                catch (Exception exception)
                {
                    PatchLog.Warn("模块 " + module.PackageId + " 卸载异常：" + exception.Message);
                }
            }
        }

        private static void ApplyAll()
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                ICompatModule module = Modules[i].Instance;
                if (module == null)
                {
                    continue;
                }

                try
                {
                    module.LoadSetting();
                    module.Apply();
                }
                catch (Exception exception)
                {
                    // 单模块异常绝不能截断其余模块
                    PatchLog.Error("模块 " + module.PackageId + " 初始化异常", exception);
                }
            }
        }

        /// <summary>
        /// 内置清单。这里的顺序就是设置界面的显示顺序。
        /// 新增一个目标 Mod 时，只需在此加一行，并在 1.7/Compat/&lt;名字&gt; 放对应 DLL。
        /// </summary>
        private static void BuildInfos()
        {
            Modules.Add(Info(
                "GOS.GoodClimate",
                "AOPatch_Module_GoodClimate",
                "AOPatch_Module_GoodClimateDesc",
                "GoodClimateCompat",
                "1.7/Compat/GoodClimate",
                () => AOPatchMod.Settings != null && AOPatchMod.Settings.EnableGoodClimate,
                v => { if (AOPatchMod.Settings != null) AOPatchMod.Settings.EnableGoodClimate = v; }));

            Modules.Add(Info(
                "VanillaExpanded.Temperature",
                "AOPatch_Module_VTE",
                "AOPatch_Module_VTEDesc",
                "VanillaTemperatureExpandedCompat",
                "1.7/Compat/VanillaTemperatureExpanded",
                () => AOPatchMod.Settings != null && AOPatchMod.Settings.EnableVanillaTemperatureExpanded,
                v => { if (AOPatchMod.Settings != null) AOPatchMod.Settings.EnableVanillaTemperatureExpanded = v; }));

            Modules.Add(Info(
                "Mlie.CentralizedClimateControl",
                "AOPatch_Module_CCC",
                "AOPatch_Module_CCCDesc",
                "CentralizedClimateControlCompat",
                "1.7/Compat/CentralizedClimateControl",
                () => AOPatchMod.Settings != null && AOPatchMod.Settings.EnableCentralizedClimateControl,
                v => { if (AOPatchMod.Settings != null) AOPatchMod.Settings.EnableCentralizedClimateControl = v; }));

            Modules.Add(Info(
                "Jeremie.Fully.Automatic.OmniCrafter",
                "AOPatch_Module_Omni",
                "AOPatch_Module_OmniDesc",
                "OmniCrafterCompat",
                "1.7/Compat/OmniCrafter",
                () => AOPatchMod.Settings != null && AOPatchMod.Settings.EnableOmniCrafter,
                v => { if (AOPatchMod.Settings != null) AOPatchMod.Settings.EnableOmniCrafter = v; }));
        }

        private static CompatModuleInfo Info(
            string packageId,
            string labelKey,
            string hintKey,
            string assemblyName,
            string relativeFolder,
            Func<bool> read,
            Action<bool> write)
        {
            return new CompatModuleInfo
            {
                PackageId = packageId,
                LabelKey = labelKey,
                HintKey = hintKey,
                AssemblyName = assemblyName,
                RelativeFolder = relativeFolder,
                ReadEnabled = read,
                WriteEnabled = write
            };
        }

        /// <summary>
        /// 扫描已加载程序集，找到所有兼容实现并按 packageId 配对。
        ///
        /// 只扫描非动态程序集，且跳过本程序集自身（宿主不实现 ICompatModule）。
        /// </summary>
        private static void DiscoverImplementations()
        {
            Assembly self = typeof(CompatRegistry).Assembly;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null || assembly == self || assembly.IsDynamic)
                {
                    continue;
                }

                // 兼容 DLL 统一以 Jeremie.AtmosphereOverhaulPatch.* 命名。
                // 先按前缀过滤，避免在启动时对 Assembly-CSharp 等巨型程序集做无谓的类型枚举。
                string assemblyName = SafeName(assembly);
                if (!assemblyName.StartsWith(CompatAssemblyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch (Exception exception)
                {
                    PatchLog.Debug("跳过程序集 " + SafeName(assembly) + "：" + exception.Message);
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int j = 0; j < types.Length; j++)
                {
                    Type type = types[j];
                    if (type == null || type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }

                    if (!typeof(ICompatModule).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    ICompatModule instance;
                    try
                    {
                        instance = Activator.CreateInstance(type) as ICompatModule;
                    }
                    catch (Exception exception)
                    {
                        PatchLog.Warn("无法实例化兼容模块 " + type.FullName + "：" + exception.Message);
                        continue;
                    }

                    if (instance == null)
                    {
                        continue;
                    }

                    CompatModuleInfo info = Find(instance.PackageId);
                    if (info == null)
                    {
                        // 未登记的实现也允许工作（前向兼容：新 DLL 配旧宿主）
                        info = new CompatModuleInfo
                        {
                            PackageId = instance.PackageId,
                            LabelKey = instance.LabelKey,
                            HintKey = instance.HintKey,
                            AssemblyName = assembly.GetName().Name,
                            RelativeFolder = "(unregistered)"
                        };
                        Modules.Add(info);
                    }

                    info.Instance = instance;
                    info.AssemblyName = assembly.GetName().Name;
                    PatchLog.Debug("发现兼容模块 " + instance.PackageId + " ← " + assembly.GetName().Name);
                }
            }
        }

        private static CompatModuleInfo Find(string packageId)
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                if (string.Equals(Modules[i].PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return Modules[i];
                }
            }

            return null;
        }

        private static string SafeName(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name;
            }
            catch
            {
                return "(unknown)";
            }
        }

        private static void LogSummary()
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                CompatModuleInfo info = Modules[i];
                string line = info.PackageId + " → " + info.State;
                if (!info.Loaded)
                {
                    line += "（兼容 DLL 未加载：" + info.RelativeFolder + "）";
                }
                else if (!string.IsNullOrEmpty(info.Detail))
                {
                    line += "（" + info.Detail + "）";
                }

                if (info.State == CompatState.Failed)
                {
                    PatchLog.Warn(line);
                }
                else
                {
                    PatchLog.Info(line);
                }
            }
        }
    }
}
