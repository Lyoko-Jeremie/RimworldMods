using System;
using System.Collections;
using System.Reflection;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Rimatomics 研究系统的软依赖兼容层。
    /// </summary>
    internal static class RimatomicsResearchCompat
    {
        private const string RimatomicsPackageId = "Dubwise.Rimatomics";
        private const string ResearchDefTypeName = "Rimatomics.RimatomicResearchDef, Rimatomics";
        private const string ResearchBenchTypeName = "Rimatomics.Building_RimatomicsResearchBench, Rimatomics";

        private static readonly object InitializationLock = new object();

        private static bool initializationAttempted;
        private static bool initialized;
        private static PropertyInfo allProjectsProperty;
        private static PropertyInfo isFinishedProperty;
        private static MethodInfo purchaseMethod;
        private static MethodInfo debugFinishMethod;

        public static bool IsModActive =>
            ModLister.GetActiveModWithIdentifier(RimatomicsPackageId, true) != null;

        /// <summary>
        /// 完成所有已加载的 Rimatomics 研究，包括其他 Mod 添加的扩展项目。
        /// </summary>
        public static int FinishAllResearch()
        {
            if (!IsModActive || !TryInitialize())
            {
                return 0;
            }

            int completedCount = 0;

            try
            {
                IEnumerable projects = allProjectsProperty.GetValue(null, null) as IEnumerable;
                if (projects == null)
                {
                    Log.Error("[FullyAutomaticOmniCrafter] 无法读取 Rimatomics 研究项目列表。");
                    return 0;
                }

                foreach (object project in projects)
                {
                    if (project == null)
                    {
                        continue;
                    }

                    // 付费项目必须同时标记为已购买，否则研究面板仍会显示银币图标。
                    purchaseMethod.Invoke(null, new[] { project });

                    bool isFinished = (bool)isFinishedProperty.GetValue(project, null);
                    if (isFinished)
                    {
                        continue;
                    }

                    // 使用 Rimatomics 自带的调试完成入口，确保进度和完成状态保持一致。
                    debugFinishMethod.Invoke(null, new[] { project });
                    completedCount++;
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 自动完成 Rimatomics 研究时发生异常：\n" +
                    exception);
            }

            return completedCount;
        }

        private static bool TryInitialize()
        {
            lock (InitializationLock)
            {
                if (initializationAttempted)
                {
                    return initialized;
                }

                initializationAttempted = true;

                try
                {
                    Type researchDefType = Type.GetType(ResearchDefTypeName, false);
                    Type researchBenchType = Type.GetType(ResearchBenchTypeName, false);
                    if (researchDefType == null || researchBenchType == null)
                    {
                        Log.Error("[FullyAutomaticOmniCrafter] 未找到 Rimatomics 研究类型，无法启用研究兼容。");
                        return false;
                    }

                    Type defDatabaseType = typeof(DefDatabase<>).MakeGenericType(researchDefType);
                    allProjectsProperty = defDatabaseType.GetProperty(
                        "AllDefsListForReading",
                        BindingFlags.Public | BindingFlags.Static);
                    isFinishedProperty = researchDefType.GetProperty(
                        "IsFinished",
                        BindingFlags.Public | BindingFlags.Instance);
                    purchaseMethod = researchBenchType.GetMethod(
                        "Purchase",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { researchDefType },
                        null);
                    debugFinishMethod = researchBenchType.GetMethod(
                        "DebugFinish",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { researchDefType },
                        null);

                    initialized = allProjectsProperty != null
                                  && isFinishedProperty != null
                                  && purchaseMethod != null
                                  && debugFinishMethod != null;

                    if (!initialized)
                    {
                        Log.Error("[FullyAutomaticOmniCrafter] Rimatomics 研究 API 不完整，无法启用研究兼容。");
                    }
                }
                catch (Exception exception)
                {
                    Log.Error(
                        "[FullyAutomaticOmniCrafter] 初始化 Rimatomics 研究兼容时发生异常：\n" +
                        exception);
                }

                return initialized;
            }
        }
    }
}
