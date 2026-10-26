using System;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// AutoBlink 桥接宿主：按当前游戏版本（编译宏）反射加载对应的桥接程序集。
    /// 桥接程序集与主程序集位于同一目录（1.6/Assemblies 或 1.7/Assemblies），
    /// 由 RimWorld 的 ModAssemblyHandler 一并加载，因此这里只需按类型名取到实例。
    /// 任何失败都只禁用 AutoBlink 联动，不能影响主程序集其他功能。
    /// </summary>
    public static class AutoBlinkBridgeHost
    {
        public static IAutoBlinkBridge Instance { get; private set; }

        public static bool IsAvailable => Instance != null;

        public static bool Initialize(Harmony harmony)
        {
            if (Instance != null)
            {
                return true;
            }

#if RIMWORLD_17
            const string assemblyName = "OuterrealmTechRobot.AutoBlink17";
            const string typeName = "OuterrealmTechRobot.AutoBlink17.AutoBlinkBridge17";
#else
            const string assemblyName = "OuterrealmTechRobot.AutoBlink16";
            const string typeName = "OuterrealmTechRobot.AutoBlink16.AutoBlinkBridge16";
#endif

            try
            {
                Type type = FindType(assemblyName, typeName);
                if (type == null)
                {
                    Log.Warning("[OuterrealmTechRobot] 未找到 AutoBlink 桥接程序集 " + assemblyName +
                                "，本次启动 AutoBlink 联动功能禁用。");
                    return false;
                }

                var bridge = (IAutoBlinkBridge)Activator.CreateInstance(type);
                Instance = bridge;
                if (!bridge.Initialize(harmony))
                {
                    Instance = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Instance = null;
                Log.Error("[OuterrealmTechRobot] 初始化 AutoBlink 桥接失败，AutoBlink 联动功能禁用: " + ex);
                return false;
            }
        }

        /// <summary>先按 类型+程序集 查找；失败再扫描已加载程序集（兼容 .NET Core 的加载上下文差异）。</summary>
        private static Type FindType(string assemblyName, string typeName)
        {
            Type type = Type.GetType(typeName + ", " + assemblyName, false);
            if (type != null)
            {
                return type;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != assemblyName)
                {
                    continue;
                }

                type = asm.GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
