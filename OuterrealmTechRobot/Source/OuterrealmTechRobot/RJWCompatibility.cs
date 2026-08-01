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
        private static MethodInfo _addGenitalsMethod;
        private static MethodInfo _addBreastsMethod;
        private static MethodInfo _addAnusMethod;
        private static System.Type _compSexPartType;
        private static System.Type _genitalFamilyType;
        private static FieldInfo _genitalFamilyField;
        private static FieldInfo _baseSizeField;
        private static FieldInfo _discoveredField;

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

                System.Type sexPartAdderType = AccessTools.TypeByName("rjw.SexPartAdder");
                if (sexPartAdderType != null)
                {
                    _addGenitalsMethod = AccessTools.Method(sexPartAdderType, "add_genitals");
                    _addBreastsMethod = AccessTools.Method(sexPartAdderType, "add_breasts");
                    _addAnusMethod = AccessTools.Method(sexPartAdderType, "add_anus");
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
                    _discoveredField = AccessTools.Field(_compSexPartType, "discovered");

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

            if (_compSexPartType == null || _genitalFamilyType == null || _genitalFamilyField == null ||
                _breastsEnum == null || _vaginaEnum == null || _anusEnum == null)
            {
                Log.ErrorOnce("[OuterrealmTechRobot] RJW compatibility types are incomplete; cannot repair maid organs.",
                    58292);
                return;
            }

            try
            {
                // 按类别检查，避免只剩一个器官时被误判为完整。
                bool hasBreasts = false;
                bool hasVagina = false;
                bool hasAnus = false;
                var hediffs = pawn.health.hediffSet.hediffs;
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (TryGetSexPart(hediffs[i], out _, out object family))
                    {
                        if (family.Equals(_breastsEnum)) hasBreasts = true;
                        else if (family.Equals(_vaginaEnum)) hasVagina = true;
                        else if (family.Equals(_anusEnum)) hasAnus = true;
                    }
                }

                // 优先使用 RJW 分类添加器，只补缺少的类别，避免生成重复器官。
                bool usedCategoryAdders = true;
                if (!hasVagina) usedCategoryAdders &= InvokePartAdder(_addGenitalsMethod, pawn);
                if (!hasBreasts) usedCategoryAdders &= InvokePartAdder(_addBreastsMethod, pawn);
                if (!hasAnus) usedCategoryAdders &= InvokePartAdder(_addAnusMethod, pawn);

                // 旧版 RJW 没有分类 API 时退回完整初始化。
                if ((!hasBreasts || !hasVagina || !hasAnus) && !usedCategoryAdders)
                {
                    Sexualize(pawn);
                }

                // 统一尺寸，并把修复生成的器官标记为已发现，确保健康面板可见。
                hediffs = pawn.health.hediffSet.hediffs;
                for (int i = hediffs.Count - 1; i >= 0; i--)
                {
                    Hediff hediff = hediffs[i];
                    if (!TryGetSexPart(hediff, out object sexPartComp, out object family))
                    {
                        continue;
                    }

                    float bodySize = pawn.BodySize;
                    if (family.Equals(_breastsEnum))
                    {
                        hediff.Severity = 1.2f;
                        if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 1.2f * bodySize);
                    }
                    else if (family.Equals(_vaginaEnum) || family.Equals(_anusEnum))
                    {
                        hediff.Severity = 0.25f;
                        if (_baseSizeField != null) _baseSizeField.SetValue(sexPartComp, 0.25f * bodySize);
                    }

                    if (_discoveredField != null) _discoveredField.SetValue(sexPartComp, true);
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("[OuterrealmTechRobot] Failed to repair RJW organs for " + pawn.ToStringSafe() + ": " + ex);
            }
        }

        private static bool InvokePartAdder(MethodInfo method, Pawn pawn)
        {
            if (method == null)
            {
                return false;
            }

            method.Invoke(null, new object[] { pawn, null, Gender.Female });
            return true;
        }

        private static bool TryGetSexPart(Hediff hediff, out object sexPartComp, out object family)
        {
            sexPartComp = null;
            family = null;
            if (!(hediff is HediffWithComps hwc) || hwc.comps == null)
            {
                return false;
            }

            for (int i = 0; i < hwc.comps.Count; i++)
            {
                object comp = hwc.comps[i];
                if (comp != null && _compSexPartType.IsAssignableFrom(comp.GetType()))
                {
                    sexPartComp = comp;
                    family = _genitalFamilyField.GetValue(hediff.def);
                    return family != null;
                }
            }

            return false;
        }
    }
}
