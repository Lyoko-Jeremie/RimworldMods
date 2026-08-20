using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Rimatomics 研究系统的软依赖兼容层。
    /// 负责枚举 Rimatomics 研究项目、查询完成状态并完成单个项目。
    /// 反射成员在首次使用时初始化并缓存，避免反复反射。
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
        private static FieldInfo priceField;

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
            List<Def> projects = CollectProjects();
            for (int i = 0; i < projects.Count; i++)
            {
                if (TryComplete(projects[i]))
                {
                    completedCount++;
                }
            }
            return completedCount;
        }

        /// <summary>
        /// 枚举全部已加载的 Rimatomics 研究项目（以 Def 基类形式返回，便于统一读取名称与描述）。
        /// </summary>
        public static List<Def> CollectProjects()
        {
            List<Def> result = new List<Def>();
            if (!IsModActive || !TryInitialize())
            {
                return result;
            }

            try
            {
                IEnumerable projects = allProjectsProperty.GetValue(null, null) as IEnumerable;
                if (projects == null)
                {
                    Log.Error("[FullyAutomaticOmniCrafter] 无法读取 Rimatomics 研究项目列表。");
                    return result;
                }

                foreach (object project in projects)
                {
                    if (project is Def def)
                    {
                        result.Add(def);
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 枚举 Rimatomics 研究项目时发生异常：\n" +
                    exception);
            }

            return result;
        }

        /// <summary>
        /// 查询单个 Rimatomics 研究是否已完成。
        /// </summary>
        public static bool IsFinished(Def project)
        {
            if (project == null || !TryInitialize())
            {
                return false;
            }

            try
            {
                return (bool)isFinishedProperty.GetValue(project, null);
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 查询 Rimatomics 研究完成状态时发生异常：\n" +
                    exception);
            }

            return false;
        }

        /// <summary>
        /// 完成单个 Rimatomics 研究项目（标记购买并完成所有研究步骤），返回是否实际完成。
        /// </summary>
        public static bool TryComplete(Def project)
        {
            if (project == null || !TryInitialize())
            {
                return false;
            }

            try
            {
                // 付费项目必须同时标记为已购买，否则研究面板仍会显示银币图标。
                purchaseMethod.Invoke(null, new[] { project });

                bool isFinished = (bool)isFinishedProperty.GetValue(project, null);
                if (isFinished)
                {
                    return false;
                }

                // 使用 Rimatomics 自带的调试完成入口，确保进度和完成状态保持一致。
                debugFinishMethod.Invoke(null, new[] { project });
                return true;
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 自动完成 Rimatomics 研究时发生异常：\n" +
                    exception);
            }

            return false;
        }

        /// <summary>
        /// 读取 Rimatomics 研究的购买价格（银币）。读取失败时返回 -1，表示不可用。
        /// </summary>
        public static int GetProjectPrice(Def project)
        {
            if (project == null || !TryInitialize() || priceField == null)
            {
                return -1;
            }

            try
            {
                return Convert.ToInt32(priceField.GetValue(project));
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 读取 Rimatomics 研究价格时发生异常：\n" +
                    exception);
            }

            return -1;
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
                    priceField = researchDefType.GetField(
                        "price",
                        BindingFlags.Public | BindingFlags.Instance);

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
