using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRoadProject.Startup
{
    [StaticConstructorOnStartup]
    public static class OuterrealmTechRoadProjectStartup
    {
        static OuterrealmTechRoadProjectStartup()
        {
            new Harmony("OuterrealmTechRoadProject").PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
