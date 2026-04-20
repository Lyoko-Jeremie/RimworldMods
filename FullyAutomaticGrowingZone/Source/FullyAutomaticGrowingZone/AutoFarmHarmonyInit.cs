using HarmonyLib;
using Verse;

namespace FullyAutomaticGrowingZone
{
    [StaticConstructorOnStartup]
    public static class AutoFarmHarmonyInit
    {
        static AutoFarmHarmonyInit()
        {
            // 替换成你的包名
            var harmony = new Harmony("Jeremie.Fully.Automatic.GrowingZone");
            harmony.PatchAll();
            Log.Message("[FullyAutomaticGrowingZone] Harmony patches applied successfully!");
        }
    }
}
