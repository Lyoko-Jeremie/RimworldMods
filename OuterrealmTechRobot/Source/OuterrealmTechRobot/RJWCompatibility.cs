using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    public static class RJWCompatibility
    {
        private static readonly object _syncRoot = new object();
        private static bool _initialized;
        private static bool _active;

        private static MethodInfo _sexualizeMethod;
        private static System.Type _compSexPartType;
        private static System.Type _genitalFamilyType;
        private static FieldInfo _genitalFamilyField;
        private static FieldInfo _baseSizeField;

        private static object _breastsEnum;
        private static object _vaginaEnum;
        private static object _anusEnum;

        public static bool Active
        {
            get
            {
                EnsureInitialized();
                return _active;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_syncRoot)
            {
                if (_initialized) return;

                _active = ModsConfig.IsActive("rim.job.world");
                if (_active)
                {
                    Log.Message("RJW found, initializing compatibility...");
                    InitializeInternal();
                }
                else
                {
                    Log.Warning("RJW not found.");
                }

                _initialized = true;
            }
        }

        private static void InitializeInternal()
        {
            try
            {
                System.Type sexualizerType = AccessTools.TypeByName("rjw.Sexualizer");
                if (sexualizerType != null)
                {
                    _sexualizeMethod = AccessTools.Method(sexualizerType, "sexualize_pawn");
                }

                _compSexPartType = AccessTools.TypeByName("rjw.HediffComp_SexPart");
                _genitalFamilyType = AccessTools.TypeByName("rjw.GenitalFamily");

                if (_compSexPartType != null && _genitalFamilyType != null)
                {
                    System.Type defType = AccessTools.TypeByName("rjw.HediffDef_SexPart");
                    if (defType != null)
                    {
                        _genitalFamilyField = AccessTools.Field(defType, "genitalFamily");
                    }
                    _baseSizeField = AccessTools.Field(_compSexPartType, "baseSize");

                    _breastsEnum = System.Enum.Parse(_genitalFamilyType, "Breasts");
                    _vaginaEnum = System.Enum.Parse(_genitalFamilyType, "Vagina");
                    _anusEnum = System.Enum.Parse(_genitalFamilyType, "Anus");
                }
            }
            catch (System.Exception e)
            {
                Log.Error("Error initializing RJWCompatibility: " + e.Message);
            }
        }

        public static void Sexualize(Pawn pawn)
        {
            if (!Active || pawn == null) return;

            if (_sexualizeMethod != null)
            {
                _sexualizeMethod.Invoke(null, new object[] { pawn });
            }
            else
            {
                Log.ErrorOnce("RJW: Sexualize method not found.", 58291);
            }
        }

        public static void InitializeMaidOrgans(Pawn pawn)
        {
            if (!Active || pawn == null) return;

            if (_compSexPartType == null || _genitalFamilyType == null || _genitalFamilyField == null)
            {
                return;
            }

            // 1. 检查是否已经有性器官，避免重复生成
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
                // 2. 只有没有器官时才调用基础生成逻辑
                Sexualize(pawn);
            }

            // 3. 强制设置/修复器官属性
            if (_breastsEnum == null || _vaginaEnum == null || _anusEnum == null) return;

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
                        if (family.Equals(_breastsEnum))
                        {
                            hediff.Severity = 1.2f; // Massive
                            if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 1.2f * bodySize);
                        }
                        else if (family.Equals(_vaginaEnum))
                        {
                            hediff.Severity = 0.25f; // Tight
                            if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 0.25f * bodySize);
                        }
                        else if (family.Equals(_anusEnum))
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