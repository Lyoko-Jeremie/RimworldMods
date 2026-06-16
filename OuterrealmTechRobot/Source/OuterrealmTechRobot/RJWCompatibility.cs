using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    public static class RJWCompatibility
    {
        private static bool? _active;
        private static MethodInfo _sexualizeMethod;
        private static System.Type _compSexPartType;
        private static System.Type _genitalFamilyType;
        private static FieldInfo _genitalFamilyField;
        private static FieldInfo _baseSizeField;

        public static bool Active
        {
            get
            {
                if (!_active.HasValue)
                {
                    _active = ModsConfig.IsActive("rim.job.world");
                    if (!_active.Value)
                    {
                        Log.Warning("RJW is not find.");
                    }
                    else
                    {
                        Log.Message("RJW is find.");
                    }
                }

                return _active.Value;
            }
        }

        public static void Sexualize(Pawn pawn)
        {
            if (!Active || pawn == null) return;

            if (_sexualizeMethod == null)
            {
                System.Type type = AccessTools.TypeByName("rjw.Sexualizer");
                if (type != null)
                {
                    _sexualizeMethod = AccessTools.Method(type, "sexualize_pawn");
                    if (_sexualizeMethod == null)
                    {
                        Log.Error("RJW: Sexualize method not found.");
                    }
                }
                else
                {
                    Log.Error("RJW: Sexualizer type not found.");
                }
            }

            if (_sexualizeMethod != null)
            {
                _sexualizeMethod.Invoke(null, new object[] { pawn });
            }
        }

        public static void InitializeMaidOrgans(Pawn pawn)
        {
            if (!Active || pawn == null) return;

            // 1. 初始化反射缓存
            if (_compSexPartType == null) _compSexPartType = AccessTools.TypeByName("rjw.HediffComp_SexPart");
            if (_genitalFamilyType == null) _genitalFamilyType = AccessTools.TypeByName("rjw.GenitalFamily");

            if (_compSexPartType == null || _genitalFamilyType == null)
            {
                Log.Error("RJW: HediffComp_SexPart or GenitalFamily type not found.");
                return;
            }

            if (_genitalFamilyField == null)
            {
                System.Type defType = AccessTools.TypeByName("rjw.HediffDef_SexPart");
                if (defType != null)
                {
                    _genitalFamilyField = AccessTools.Field(defType, "genitalFamily");
                    if (_genitalFamilyField == null)
                    {
                        Log.Error("RJW: GenitalFamily field not found.");
                    }
                }
                else
                {
                    Log.Error("RJW: HediffDef_SexPart type not found.");
                }
            }

            if (_genitalFamilyField == null) return;

            if (_baseSizeField == null)
            {
                _baseSizeField = AccessTools.Field(_compSexPartType, "baseSize");
            }

            // 2. 检查是否已经有性器官，避免重复生成
            bool hasOrgans = false;
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h is HediffWithComps hwc && hwc.comps != null)
                {
                    foreach (var c in hwc.comps)
                    {
                        if (_compSexPartType.IsAssignableFrom(c.GetType()))
                        {
                            hasOrgans = true;
                            break;
                        }
                    }
                }

                if (hasOrgans) break;
            }

            if (!hasOrgans)
            {
                // 3. 只有没有器官时才调用基础生成逻辑
                Sexualize(pawn);
            }

            // 4. 强制设置/修复器官属性
            // 准备枚举值对比
            object breastsEnum = System.Enum.Parse(_genitalFamilyType, "Breasts");
            object vaginaEnum = System.Enum.Parse(_genitalFamilyType, "Vagina");
            object anusEnum = System.Enum.Parse(_genitalFamilyType, "Anus");

            // 遍历并强制设置属性
            var hediffs = pawn.health.hediffSet.hediffs;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                var hediff = hediffs[i];
                object sexPartComp = null;
                if (hediff is HediffWithComps hwc && hwc.comps != null)
                {
                    foreach (var c in hwc.comps)
                    {
                        if (_compSexPartType.IsAssignableFrom(c.GetType()))
                        {
                            sexPartComp = c;
                            break;
                        }
                    }
                }

                if (sexPartComp != null)
                {
                    object family = _genitalFamilyField.GetValue(hediff.def);

                    if (family != null)
                    {
                        float bodySize = pawn.BodySize;
                        if (family.Equals(breastsEnum))
                        {
                            hediff.Severity = 1.2f; // Massive (RJW 阶段：Enormous 1.0, Massive 1.2)
                            if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 1.2f * bodySize);
                        }
                        else if (family.Equals(vaginaEnum))
                        {
                            hediff.Severity = 0.25f; // Tight (RJW 阶段：Micro 0.01, Tight 0.20, Average 0.40)
                            if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 0.25f * bodySize);
                        }
                        else if (family.Equals(anusEnum))
                        {
                            hediff.Severity = 0.25f; // Tight
                            if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 0.25f * bodySize);
                        }
                    }
                }
            }
        }
    }
}