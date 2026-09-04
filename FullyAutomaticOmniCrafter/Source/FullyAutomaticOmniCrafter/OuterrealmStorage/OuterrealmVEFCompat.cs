using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// Vanilla Expanded Framework 的待摧毁物品兼容。
    /// 不引用 VEF 程序集：启动时严格校验已知类型、字段和标记方法，并把字段读取编译为委托。
    /// </summary>
    internal static class OuterrealmVEFCompat
    {
        private const string CompTypeName = "VEF.AnimalBehaviours.CompDestroyThisItem";
        private const string PendingFieldName = "itemNeedsDestruction";
        private const string MarkMethodName = "SetObjectForDestruction";

        private static readonly Type CompType;
        private static readonly Func<ThingComp, bool> IsPendingReader;
        internal static readonly MethodInfo SetObjectForDestructionMethod;

        static OuterrealmVEFCompat()
        {
            Type type = AccessTools.TypeByName(CompTypeName);
            if (type == null || type.Assembly.GetName().Name != "VEF" || !typeof(ThingComp).IsAssignableFrom(type))
            {
                return;
            }

            FieldInfo pendingField = type.GetField(PendingFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            MethodInfo markMethod = type.GetMethod(MarkMethodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (pendingField == null || pendingField.FieldType != typeof(bool) || pendingField.IsStatic
                || markMethod == null || markMethod.ReturnType != typeof(void) || markMethod.IsStatic)
            {
                Log.Warning("[OuterrealmStorage] 检测到 VEF，但摧毁物品协议签名不匹配；已停用该可选兼容。");
                return;
            }

            try
            {
                // 热路径不调用 FieldInfo.GetValue，避免每个搬运候选产生装箱分配。
                DynamicMethod reader = new DynamicMethod(
                    "OuterrealmStorage_ReadVEFDestroyPending",
                    typeof(bool), new[] { typeof(ThingComp) }, typeof(OuterrealmVEFCompat), true);
                ILGenerator il = reader.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, type);
                il.Emit(OpCodes.Ldfld, pendingField);
                il.Emit(OpCodes.Ret);
                IsPendingReader = (Func<ThingComp, bool>)reader.CreateDelegate(typeof(Func<ThingComp, bool>));
                CompType = type;
                SetObjectForDestructionMethod = markMethod;
            }
            catch (Exception error)
            {
                Log.Warning("[OuterrealmStorage] 无法建立 VEF 摧毁物品状态读取器；已停用该可选兼容。\n" + error);
            }
        }

        internal static bool Available => CompType != null
            && IsPendingReader != null && SetObjectForDestructionMethod != null;

        internal static bool IsPendingDestruction(Thing item)
        {
            if (!Available || !(item is ThingWithComps withComps))
            {
                return false;
            }
            System.Collections.Generic.List<ThingComp> comps = withComps.AllComps;
            if (comps == null)
            {
                return false;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp != null && comp.GetType() == CompType)
                {
                    return IsPendingReader(comp);
                }
            }
            return false;
        }
    }

    /// <summary>VEF 新增摧毁意图时，中止已经领取同一物品的超维存入任务。</summary>
    [HarmonyPatch]
    internal static class Patch_VEF_SetObjectForDestruction_AutoDepositProtection
    {
        private static bool Prepare()
        {
            return OuterrealmVEFCompat.Available;
        }

        private static MethodBase TargetMethod()
        {
            return OuterrealmVEFCompat.SetObjectForDestructionMethod;
        }

        private static void Postfix(object __instance)
        {
            Thing item = (__instance as ThingComp)?.parent;
            if (item != null && OuterrealmVEFCompat.IsPendingDestruction(item))
            {
                OuterrealmVaultUtil.InterruptVaultDepositForNewWorkClaim(item);
            }
        }
    }
}
