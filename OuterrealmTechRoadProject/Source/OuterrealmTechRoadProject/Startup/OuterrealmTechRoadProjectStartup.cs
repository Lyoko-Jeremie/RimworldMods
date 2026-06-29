using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRoadProject.Startup
{
    /// <summary>
    /// Mod 启动入口。
    /// StaticConstructorOnStartup 会在 RimWorld 加载程序集后执行，用来注册本 Mod 的 Harmony 补丁。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OuterrealmTechRoadProjectStartup
    {
        static OuterrealmTechRoadProjectStartup()
        {
            // PatchAll 会扫描当前程序集里所有带 HarmonyPatch 的类型。
            // 目前用于让拥有超维链路的不可通行世界 tile 参与世界寻路。
            new Harmony("OuterrealmTechRoadProject").PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
