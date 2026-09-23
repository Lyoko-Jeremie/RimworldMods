using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 星环公司（[SRC]Miho, Star Ring Corporation）通讯台交易界面兼容。
    ///
    /// SRM 的 <c>Dialog_SRInteraction.GetSilver()</c> 只遍历通电通电信标覆盖格上的实物白银
    /// （Building_OrbitalTradeBeacon.TradeableCells → GridsUtility.GetThingList），而超维查询投影
    /// 刻意不进入 thingGrid，因此界面上的“殖民地可用白银”永远看不到仓库库存。
    ///
    /// 本类不引用 SRM 程序集：启动时严格校验类型、字段和方法签名，任一不匹配即整体停用。
    /// 可见性口径与 TradeUtility 补丁完全一致：全局直连开关关闭时只并入信标覆盖的终端，
    /// 开启时并入全部全局条目。
    /// </summary>
    internal static class OuterrealmSRCCompat
    {
        private const string SrmAssemblyName = "SRM";
        private const string DialogTypeName = "Dialog_SRInteraction";
        private const string MapFieldName = "Map";
        private const string SilverAmountFieldName = "silverAmount";
        private const string GetSilverMethodName = "GetSilver";

        private static readonly MethodInfo GetSilverMethod;

        static OuterrealmSRCCompat()
        {
            try
            {
                Type dialogType = FindDialogType();
                if (dialogType == null)
                {
                    return;
                }

                FieldInfo mapField = dialogType.GetField(MapFieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                FieldInfo silverField = dialogType.GetField(SilverAmountFieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                MethodInfo method = dialogType.GetMethod(GetSilverMethodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (mapField == null || mapField.IsStatic || mapField.FieldType != typeof(Map)
                    || silverField == null || silverField.IsStatic || silverField.FieldType != typeof(int)
                    || method == null || method.IsStatic || method.ReturnType != typeof(void))
                {
                    Log.Warning("[OuterrealmStorage] 检测到 SRM，但星环公司交易界面签名不匹配；已停用该可选兼容。");
                    return;
                }

                GetSilverMethod = method;
            }
            catch (Exception error)
            {
                Log.Warning("[OuterrealmStorage] 建立星环公司交易界面兼容时出错；已停用该可选兼容。\n" + error);
            }
        }

        /// <summary>
        /// 按程序集名 + 类型简单名定位目标窗口，避免依赖 SRM 的命名空间。
        /// 只在静态构造中执行一次，定位结果由 GetSilverMethod 缓存，不产生热路径反射。
        /// </summary>
        private static Type FindDialogType()
        {
            Assembly srmAssembly = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly candidate = assemblies[i];
                if (candidate == null || candidate.IsDynamic)
                {
                    continue;
                }
                try
                {
                    if (candidate.GetName().Name == SrmAssemblyName)
                    {
                        srmAssembly = candidate;
                        break;
                    }
                }
                catch (Exception)
                {
                    // 读取程序集名失败时跳过该程序集。
                }
            }
            if (srmAssembly == null)
            {
                return null;
            }

            Type[] types;
            try
            {
                types = srmAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException loadError)
            {
                types = loadError.Types;
            }
            catch (Exception)
            {
                return null;
            }
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type != null && type.Name == DialogTypeName && typeof(Window).IsAssignableFrom(type))
                {
                    return type;
                }
            }
            return null;
        }

        internal static bool Available => GetSilverMethod != null;

        internal static MethodBase TargetMethod()
        {
            return GetSilverMethod;
        }

        // GetSilver 只在通讯台窗口 PostOpen 时按需调用；列表按线程隔离并复用，避免重复分配。
        [ThreadStatic] private static List<OuterrealmEntry> scratchEntries;

        /// <summary>
        /// 把超维库存中的白银并入界面的“殖民地可用白银”。
        /// 原方法统计的是信标覆盖格上的实物白银，投影既不在 thingGrid 也不与这些实物重叠，
        /// 因此直接相加不会重复计数。
        /// </summary>
        internal static void AppendVaultSilver(Map map, ref int silverAmount)
        {
            if (map == null)
            {
                return;
            }

            List<OuterrealmEntry> entries = scratchEntries;
            if (entries == null)
            {
                entries = new List<OuterrealmEntry>();
                scratchEntries = entries;
            }
            OuterrealmLaunchableResourceUtility.CollectAccessibleEntries(map, null, ThingDefOf.Silver, entries);

            long vaultSilver = 0L;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                if (entry != null && entry.Count > 0L)
                {
                    vaultSilver += entry.Count;
                }
            }
            if (vaultSilver <= 0L)
            {
                return;
            }

            long total = (long)silverAmount + vaultSilver;
            silverAmount = total >= int.MaxValue ? int.MaxValue : (int)total;
        }
    }

    /// <summary>
    /// 星环公司交易界面：让“殖民地可用白银”包含超维库存中的白银。
    /// 命名前缀 Patch_Optional_ 使其安装失败只记日志，不会截断其它补丁的安装。
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_Optional_SRDialog_GetSilver
    {
        private static bool Prepare()
        {
            return OuterrealmSRCCompat.Available;
        }

        private static MethodBase TargetMethod()
        {
            return OuterrealmSRCCompat.TargetMethod();
        }

        private static void Postfix(Map ___Map, ref int ___silverAmount)
        {
            OuterrealmSRCCompat.AppendVaultSilver(___Map, ref ___silverAmount);
        }
    }
}
