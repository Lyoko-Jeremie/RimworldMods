using System;
using System.Collections.Generic;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Util;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 兼容模块注册表。
    ///
    /// 所有模块在启动时统一安装；模块之间不互相依赖，
    /// 任何一个缺失/跳过/失败都不会影响其它模块。
    /// </summary>
    internal static class CompatRegistry
    {
        private static readonly List<CompatModule> Modules = new List<CompatModule>();
        private static bool initialized;

        public static IReadOnlyList<CompatModule> AllModules
        {
            get { return Modules; }
        }

        public static void Initialize(Harmony harmony)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            Modules.Add(new GoodClimateCompat());
            Modules.Add(new VanillaTemperatureExpandedCompat());
            Modules.Add(new CentralizedClimateControlCompat());
            Modules.Add(new OmniCrafterCompat());

            for (int i = 0; i < Modules.Count; i++)
            {
                CompatModule module = Modules[i];
                try
                {
                    module.LoadSetting();
                    module.Apply(harmony);
                }
                catch (Exception exception)
                {
                    // 单模块异常绝不能截断其余模块
                    PatchLog.Error("模块 " + module.PackageId + " 初始化异常", exception);
                }
            }

            LogSummary();
        }

        /// <summary>
        /// 在运行时重新安装全部补丁。
        /// 先 UnpatchAll 再逐模块重装，用于「快速回退」后重新启用。
        /// </summary>
        public static void Reapply(Harmony harmony)
        {
            if (harmony == null)
            {
                PatchLog.Warn("Reapply 被调用但 Harmony 实例为空。");
                return;
            }

            try
            {
                harmony.UnpatchAll(AOPatchMod.HarmonyId);
            }
            catch (Exception exception)
            {
                PatchLog.Warn("UnpatchAll 失败：" + exception.Message);
            }

            for (int i = 0; i < Modules.Count; i++)
            {
                CompatModule module = Modules[i];
                try
                {
                    module.Apply(harmony);
                }
                catch (Exception exception)
                {
                    PatchLog.Error("模块 " + module.PackageId + " 重装异常", exception);
                }
            }

            LogSummary();
        }

        private static void LogSummary()
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                CompatModule module = Modules[i];
                string line = module.PackageId + " → " + module.State;
                if (!string.IsNullOrEmpty(module.Detail))
                {
                    line += "（" + module.Detail + "）";
                }

                if (module.State == CompatState.Failed)
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
