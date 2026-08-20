using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 渡鸦（Raven Race）自定义研究 Provider。
    /// 通过反射软依赖 ZuoYao_RavenRace 程序集，枚举 RavenResearchProjectDef
    /// 并调用其中央中枢完成研究。反射成员在首次使用时缓存，避免反复反射。
    /// 前置检查自行实现（渡鸦前置查中枢完成状态、原版前置查 IsFinished），
    /// 与渡鸦内部 PrerequisitesCompleted 逻辑等价。
    /// </summary>
    public class RavenResearchProvider : IResearchUnlockProvider
    {
        private const string RavenPackageId = "ZuoYao.RavenRace";
        private const string ResearchDefTypeName = "RavenRace.Features.Research.RavenResearchProjectDef, ZuoYao_RavenRace";
        private const string HubTypeName = "RavenRace.Features.CentralHub.GameComponent_RavenCentralHubSystem, ZuoYao_RavenRace";
        private const string MaterialCostTypeName = "RavenRace.Features.Research.RavenResearchMaterialCost, ZuoYao_RavenRace";

        private static readonly object InitializationLock = new object();
        private static bool initializationAttempted;
        private static bool initialized;

        private static Type researchDefType;
        private static Type materialCostType;

        // 中枢成员
        private static PropertyInfo currentProperty;
        private static MethodInfo researchLevelMethod;       // ResearchLevel(RavenResearchProjectDef)
        private static MethodInfo isCompletedMethod;         // IsCompleted(RavenResearchProjectDef)
        private static MethodInfo markCompletedMethod;       // MarkCompleted(RavenResearchProjectDef)
        private static MethodInfo isMaxLevelReachedMethod;   // IsMaxLevelReached(RavenResearchProjectDef)

        // 研究 Def 成员
        private static PropertyInfo allDefsProperty;         // DefDatabase<RavenResearchProjectDef>.AllDefsListForReading
        private static FieldInfo prerequisitesField;
        private static FieldInfo baseCostField;
        private static FieldInfo materialCostsField;
        private static PropertyInfo isInfiniteProperty;

        // 材料成员
        private static FieldInfo materialThingDefField;
        private static FieldInfo materialCountField;

        public bool IsActive =>
            ModLister.GetActiveModWithIdentifier(RavenPackageId, true) != null && TryInitialize();

        public string GroupNameKey => "OmniCrafter_Research_GroupRaven";

        public List<ResearchUnlockEntry> CollectEntries(bool ignorePrerequisites)
        {
            List<ResearchUnlockEntry> result = new List<ResearchUnlockEntry>();
            if (!IsActive)
            {
                return result;
            }

            try
            {
                object hub = currentProperty.GetValue(null, null);
                if (hub == null)
                {
                    Log.Error("[FullyAutomaticOmniCrafter] 渡鸦中央中枢实例为空，无法读取渡鸦研究。");
                    return result;
                }

                IList allDefs = (IList)allDefsProperty.GetValue(null, null);
                if (allDefs == null)
                {
                    return result;
                }

                // defName → Def 索引（前置检查用，避免每次反射查 DefDatabase）
                Dictionary<string, Def> ravenDefsByName = new Dictionary<string, Def>(allDefs.Count);
                for (int i = 0; i < allDefs.Count; i++)
                {
                    Def def = (Def)allDefs[i];
                    if (def != null && !ravenDefsByName.ContainsKey(def.defName))
                    {
                        ravenDefsByName.Add(def.defName, def);
                    }
                }

                for (int i = 0; i < allDefs.Count; i++)
                {
                    Def def = (Def)allDefs[i];
                    if (def == null)
                    {
                        continue;
                    }

                    ResearchUnlockEntry entry = new ResearchUnlockEntry
                    {
                        Def = def,
                        Provider = this,
                        RawProject = def
                    };

                    // 完成度：有限研究满级视为完成；无限研究完成一次（等级 ≥ 1）即视为已解锁。
                    int level = (int)researchLevelMethod.Invoke(hub, new object[] { def });
                    bool infinite = (bool)isInfiniteProperty.GetValue(def, null);
                    bool maxed = (bool)isMaxLevelReachedMethod.Invoke(hub, new object[] { def });

                    if ((!infinite && maxed) || (infinite && level >= 1))
                    {
                        entry.State = ResearchEntryState.Unlocked;
                    }
                    else
                    {
                        bool prereqOk = PrerequisitesMet(def, hub, ravenDefsByName);
                        if (prereqOk || ignorePrerequisites)
                        {
                            entry.State = ResearchEntryState.Available;
                        }
                        else
                        {
                            entry.State = ResearchEntryState.PrerequisiteMissing;
                        }
                    }

                    // 前置信息
                    List<string> prerequisites = (List<string>)prerequisitesField.GetValue(def);
                    if (prerequisites != null)
                    {
                        for (int j = 0; j < prerequisites.Count; j++)
                        {
                            string prereqName = prerequisites[j];
                            Def prereqDef;
                            if (ravenDefsByName.TryGetValue(prereqName, out prereqDef))
                            {
                                entry.PrerequisiteLabels.Add(prereqDef.LabelCap.ToString());
                                entry.PrerequisiteMet.Add((bool)isCompletedMethod.Invoke(hub, new object[] { prereqDef }));
                            }
                            else
                            {
                                ResearchProjectDef vanilla = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(prereqName);
                                if (vanilla != null)
                                {
                                    entry.PrerequisiteLabels.Add(vanilla.LabelCap.ToString());
                                    entry.PrerequisiteMet.Add(vanilla.IsFinished);
                                }
                                else
                                {
                                    // 找不到的引用原样展示并视为未满足
                                    entry.PrerequisiteLabels.Add(prereqName);
                                    entry.PrerequisiteMet.Add(false);
                                }
                            }
                        }
                    }

                    entry.CostText = BuildCostText(def);

                    result.Add(entry);
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 收集渡鸦研究条目时发生异常：\n" +
                    exception);
            }

            return result;
        }

        public bool TryComplete(ResearchUnlockEntry entry)
        {
            if (!IsActive || entry?.RawProject == null)
            {
                return false;
            }

            try
            {
                object hub = currentProperty.GetValue(null, null);
                if (hub == null)
                {
                    return false;
                }

                markCompletedMethod.Invoke(hub, new object[] { entry.RawProject });
                return true;
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 完成渡鸦研究时发生异常：\n" +
                    exception);
            }

            return false;
        }

        /// <summary>
        /// 判断渡鸦研究的前置是否全部满足：渡鸦前置查中枢完成状态，原版前置查 IsFinished。
        /// </summary>
        private static bool PrerequisitesMet(Def project, object hub, Dictionary<string, Def> ravenDefsByName)
        {
            List<string> prerequisites = (List<string>)prerequisitesField.GetValue(project);
            if (prerequisites == null || prerequisites.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < prerequisites.Count; i++)
            {
                string prereqName = prerequisites[i];
                Def prereqDef;
                if (ravenDefsByName.TryGetValue(prereqName, out prereqDef))
                {
                    if (!(bool)isCompletedMethod.Invoke(hub, new object[] { prereqDef }))
                    {
                        return false;
                    }
                }
                else
                {
                    ResearchProjectDef vanilla = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(prereqName);
                    if (vanilla == null || !vanilla.IsFinished)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 生成成本文本：研究点数 + 材料清单（材料读取失败时仅显示点数）。
        /// </summary>
        private static string BuildCostText(Def project)
        {
            try
            {
                float baseCost = (float)baseCostField.GetValue(project);
                string text = baseCost.ToString("N0");

                IList materials = (IList)materialCostsField.GetValue(project);
                if (materials != null && materials.Count > 0 && materialThingDefField != null && materialCountField != null)
                {
                    List<string> parts = new List<string>(materials.Count);
                    for (int i = 0; i < materials.Count; i++)
                    {
                        object material = materials[i];
                        if (material == null)
                        {
                            continue;
                        }

                        ThingDef thingDef = (ThingDef)materialThingDefField.GetValue(material);
                        int count = (int)materialCountField.GetValue(material);
                        if (thingDef != null && count > 0)
                        {
                            parts.Add(count + " " + thingDef.label);
                        }
                    }

                    if (parts.Count > 0)
                    {
                        text += " + " + string.Join(", ", parts.ToArray());
                    }
                }

                return text;
            }
            catch (Exception exception)
            {
                Log.Error(
                    "[FullyAutomaticOmniCrafter] 读取渡鸦研究成本时发生异常：\n" +
                    exception);
            }

            return null;
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
                    researchDefType = Type.GetType(ResearchDefTypeName, false);
                    Type hubType = Type.GetType(HubTypeName, false);
                    materialCostType = Type.GetType(MaterialCostTypeName, false);
                    if (researchDefType == null || hubType == null)
                    {
                        Log.Error("[FullyAutomaticOmniCrafter] 未找到渡鸦研究类型，无法启用渡鸦研究兼容。");
                        return false;
                    }

                    currentProperty = hubType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                    researchLevelMethod = hubType.GetMethod(
                        "ResearchLevel",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { researchDefType },
                        null);
                    isCompletedMethod = hubType.GetMethod(
                        "IsCompleted",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { researchDefType },
                        null);
                    markCompletedMethod = hubType.GetMethod(
                        "MarkCompleted",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { researchDefType },
                        null);
                    isMaxLevelReachedMethod = hubType.GetMethod(
                        "IsMaxLevelReached",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { researchDefType },
                        null);

                    prerequisitesField = researchDefType.GetField("prerequisites", BindingFlags.Public | BindingFlags.Instance);
                    baseCostField = researchDefType.GetField("baseCost", BindingFlags.Public | BindingFlags.Instance);
                    materialCostsField = researchDefType.GetField("materialCosts", BindingFlags.Public | BindingFlags.Instance);
                    isInfiniteProperty = researchDefType.GetProperty("IsInfinite", BindingFlags.Public | BindingFlags.Instance);

                    Type defDatabaseType = typeof(DefDatabase<>).MakeGenericType(researchDefType);
                    allDefsProperty = defDatabaseType.GetProperty(
                        "AllDefsListForReading",
                        BindingFlags.Public | BindingFlags.Static);

                    if (materialCostType != null)
                    {
                        materialThingDefField = materialCostType.GetField("thingDef", BindingFlags.Public | BindingFlags.Instance);
                        materialCountField = materialCostType.GetField("count", BindingFlags.Public | BindingFlags.Instance);
                    }

                    initialized = currentProperty != null
                                  && researchLevelMethod != null
                                  && isCompletedMethod != null
                                  && markCompletedMethod != null
                                  && isMaxLevelReachedMethod != null
                                  && prerequisitesField != null
                                  && baseCostField != null
                                  && materialCostsField != null
                                  && isInfiniteProperty != null
                                  && allDefsProperty != null;

                    if (!initialized)
                    {
                        Log.Error("[FullyAutomaticOmniCrafter] 渡鸦研究 API 不完整，无法启用渡鸦研究兼容。");
                    }
                }
                catch (Exception exception)
                {
                    Log.Error(
                        "[FullyAutomaticOmniCrafter] 初始化渡鸦研究兼容时发生异常：\n" +
                        exception);
                }

                return initialized;
            }
        }
    }
}
