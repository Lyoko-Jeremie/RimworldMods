using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// LWM Deep Storage 兼容：该 Mod 的 <c>Settings.MakeAllStorageDeepStorage</c>（默认开启）
    /// 会在 DefsLoaded 阶段把"所有 thingClass 继承 Building_Storage 的 ThingDef"批量改造为深储：
    /// 向 def.comps 追加 LWM.DeepStorage.Properties（CompDeepStorage 的 props）、追加
    /// ITab_DeepStorage_Inventory、并打开 drawGUIOverlay。
    ///
    /// 本模组的超维存储仓为了第三方"原版存储"判定而继承 Building_Storage，因此也会被一起改造。
    /// 但仓储语义完全由本模组接管；LWM 的部分补丁对"已 DeSpawn / 地图已为 null 的存储建筑"
    /// 仍会访问 __instance.Map，在拆除/过渡期抛 NullReferenceException，并连带打断 Destroy
    /// 尾部清理，留下"已销毁但地图上仍有残影、且无法再次销毁"的建筑。
    ///
    /// 因此在启动完成后（Defs 已加载、游戏 Thing 尚未创建）把 LWM 强加给本模组存储仓的深储
    /// 配置移除：之后新建/读档生成的本建筑不再带 CompDeepStorage，LWM 以 TryGetComp 判定的
    /// 补丁会自然早退。未安装 LWM 时本类零开销。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class OuterrealmLwmDeepStorageCompat
    {
        private const string LwmPropertiesTypeName = "LWM.DeepStorage.Properties";
        private const string LwmInventoryTabTypeName = "LWM.DeepStorage.ITab_DeepStorage_Inventory";

        // 幂等标志：静态构造的延迟回调与 GameComponent 构造都会调用 Apply，
        // 避免重复扫描/重复移除（启动阶段与读档阶段均为单线程，用 Interlocked 兜底）。
        private static int appliedFlag;

        static OuterrealmLwmDeepStorageCompat()
        {
            // 在 Defs 加载/静态构造阶段结束后执行：此时 LWM 的 DefsLoaded 改造已完成，
            // 而游戏 Thing 实例尚未创建，移除后不会出现"已创建实例缺 comp"的问题。
            LongEventHandler.ExecuteWhenFinished(Apply);
        }

        /// <summary>
        /// 移除本模组存储建筑上由 LWM 强加的深储配置。可重复调用（幂等）。
        /// GameComponent_OuterrealmStorage 构造函数也会调用一次：GameComponent 在读档
        /// 创建 Thing 之前构造，可保证即使静态构造早于 LWM 的 DefsLoaded 执行也最终生效。
        /// </summary>
        internal static void Apply()
        {
            if (System.Threading.Interlocked.Exchange(ref appliedFlag, 1) == 1)
            {
                return;
            }
            try
            {
                Type propertiesType = FindType(LwmPropertiesTypeName);
                Type inventoryTabType = FindType(LwmInventoryTabTypeName);
                if (propertiesType == null && inventoryTabType == null)
                {
                    return; // 未安装 LWM Deep Storage
                }
                int cleanedDefs = 0;
                List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    ThingDef def = defs[i];
                    if (def == null || def.thingClass == null
                        || !typeof(Building_OuterrealmVault).IsAssignableFrom(def.thingClass))
                    {
                        continue;
                    }
                    if (CleanDef(def, propertiesType, inventoryTabType))
                    {
                        cleanedDefs++;
                    }
                }
                if (cleanedDefs > 0)
                {
                    Log.Message("[OuterrealmStorage] LWM Deep Storage compatibility: removed its deep-storage "
                        + "comp/tab from " + cleanedDefs + " vault ThingDef(s).");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OuterrealmStorage] LWM Deep Storage compatibility cleanup failed: " + ex);
            }
        }

        /// <summary>移除单个 Def 上由 LWM 动态追加的深储配置；返回是否有改动。</summary>
        private static bool CleanDef(ThingDef def, Type propertiesType, Type inventoryTabType)
        {
            bool changed = false;
            List<CompProperties> comps = def.comps;
            if (propertiesType != null && comps != null)
            {
                for (int i = comps.Count - 1; i >= 0; i--)
                {
                    CompProperties props = comps[i];
                    if (props == null)
                    {
                        continue;
                    }
                    Type type = props.GetType();
                    if (type == propertiesType || type.FullName == LwmPropertiesTypeName)
                    {
                        comps.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            if (inventoryTabType != null)
            {
                List<Type> tabs = def.inspectorTabs;
                if (tabs != null)
                {
                    for (int i = tabs.Count - 1; i >= 0; i--)
                    {
                        Type type = tabs[i];
                        if (type == inventoryTabType
                            || (type != null && type.FullName == LwmInventoryTabTypeName))
                        {
                            tabs.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
                List<InspectTabBase> resolved = def.inspectorTabsResolved;
                if (resolved != null)
                {
                    for (int i = resolved.Count - 1; i >= 0; i--)
                    {
                        InspectTabBase tab = resolved[i];
                        if (tab != null && tab.GetType().FullName == LwmInventoryTabTypeName)
                        {
                            resolved.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
            }
            if (def.drawGUIOverlay)
            {
                def.drawGUIOverlay = false;
                changed = true;
            }
            if (def.drawGUIOverlayQuality)
            {
                def.drawGUIOverlayQuality = false;
                changed = true;
            }
            return changed;
        }

        /// <summary>按完整类型名在已加载程序集中查找（避免对 LWM 程序集的编译期引用）。</summary>
        private static Type FindType(string fullName)
        {
            System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
