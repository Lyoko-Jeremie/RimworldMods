using HarmonyLib;
using RimWorld;
using System.Reflection;
using System.Text;
using Verse;

namespace EggSafeBox
{
    internal class Patch_Temp
    {
        public static Building_Storage GetSafeBoxForEgg(Thing egg)
        {
            if (egg.Map == null)
                return (Building_Storage)null;
            foreach (Thing thing in egg.Position.GetThingList(egg.Map))
            {
                // MARK : TODO change this to a flag check instead of defName check
                //if (thing is Building_Storage incubatorForEgg &&
                //    incubatorForEgg.def.defName == "MF_Building_AdvanceEggHatcher")
                //    return incubatorForEgg;
                if (thing is Building_Storage incubatorForEgg)
                {
                    if (incubatorForEgg.GetComp<CompEggSafeBox>() != null)
                    {
                        return incubatorForEgg;
                    }
                }
            }

            return (Building_Storage)null;
        }

        [HarmonyPatch(typeof(CompHatcher), "CompTick")]
        public static class Patch_CompHatcher_Speedup
        {
            [HarmonyPrefix]
            public static void Prefix(CompHatcher __instance)
            {
                CompEggSafeBox comp1 = Patch_Temp.GetSafeBoxForEgg((Thing)__instance.parent)
                    ?.GetComp<CompEggSafeBox>();
                if (comp1 == null)
                    return;
                if (comp1.Props.frozenSafeBox)
                {
                    CompPowerTrader comp2 = comp1.parent.TryGetComp<CompPowerTrader>();
                    if (comp2 != null && comp2.PowerOn)
                    {
                        // if are we, set hatch progress to 0
                        FieldInfo field = typeof(CompHatcher).GetField("gestateProgress",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        field.SetValue((object)__instance, (object)0f);
                        //float num1 = (float)(1.0 / ((double)__instance.Props.hatcherDaystoHatch * 60000.0));
                        //float hatchSpeedMultiplier = comp1.Props.hatchSpeedMultiplier;
                        //FieldInfo field = typeof(CompHatcher).GetField("gestateProgress",
                        //    BindingFlags.Instance | BindingFlags.NonPublic);
                        //float num2 = (float)field.GetValue((object)__instance) + num1 * hatchSpeedMultiplier;
                        //if ((double)num2 > 1.0)
                        //    num2 = 1f;
                        //field.SetValue((object)__instance, (object)num2);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(CompHatcher), "CompInspectStringExtra")]
        public static class Patch_CompHatcher_InspectString
        {
            [HarmonyPostfix]
            public static void Postfix(CompHatcher __instance, ref string __result)
            {
                if (__instance.parent == null || __instance.parent.Map == null)
                    return;
                CompEggSafeBox comp1 = Patch_Temp.GetSafeBoxForEgg((Thing)__instance.parent)
                    ?.GetComp<CompEggSafeBox>();
                if (comp1 == null)
                    return;
                if (comp1.Props.frozenSafeBox)
                {
                    CompPowerTrader comp2 = comp1.parent.TryGetComp<CompPowerTrader>();
                    if (comp2 == null)
                        return;

                    float f = (float)typeof(CompHatcher)
                        .GetField("gestateProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue((object)__instance);

                    //string originalCode = (string)(
                    //    "EggProgress".Translate() + ": " + f.ToStringPercent() + "\n" +
                    //    "HatchesIn".Translate() + ": " +
                    //    "PeriodDays".Translate(
                    //        (NamedArgument)(__instance.Props.hatcherDaystoHatch * (1f - f)).ToString("F1")));

                    //float hatchSpeedMultiplier = comp1.Props.hatchSpeedMultiplier;
                    //float num = __instance.Props.hatcherDaystoHatch * (1f - f) / hatchSpeedMultiplier;

                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.Append((string)("EggProgress".Translate() + ": " + f.ToStringPercent() + "\n"));

                    if (comp2.PowerOn)
                    {
                        //float num = __instance.Props.hatcherDaystoHatch * (1f - f) * 0;
                        //stringBuilder.Append((string)("HatchesIn".Translate() + ": " +
                        //                              "PeriodDays".Translate((NamedArgument)num.ToString("F1"))));
                        stringBuilder.Append((string)("HatchPeriodProgressBeFrozen".Translate()));

                        stringBuilder.Append((string)("\n" + "JeremieEggSafeBoxIsWorking".Translate()));
                    }
                    else
                    {
                        float num = __instance.Props.hatcherDaystoHatch * (1f - f);
                        stringBuilder.Append((string)("HatchesIn".Translate() + ": " +
                                                      "PeriodDays".Translate((NamedArgument)num.ToString("F1"))));

                        stringBuilder.Append((string)("\n" + "JeremieEggSafeBoxIsNotWorking".Translate()));
                    }

                    __result = stringBuilder.ToString();
                }
            }
        }

        //[HarmonyPatch(typeof(CompHatcher), "TemperatureDamaged")]
        //[HarmonyPatch]
        [HarmonyPatch(typeof(CompHatcher), "TemperatureDamaged", MethodType.Getter)]
        public static class Patch_CompHatcher_TemperatureDamaged
        {
            [HarmonyPrefix]
            public static bool Prefix(CompHatcher __instance, ref bool __result)
            {
                CompEggSafeBox comp1 = Patch_Temp.GetSafeBoxForEgg((Thing)__instance.parent)
                    ?.GetComp<CompEggSafeBox>();
                if (comp1 != null && comp1.Props.frozenSafeBox)
                {
                    // if are we, not damage it
                    CompPowerTrader comp2 = comp1.parent.TryGetComp<CompPowerTrader>();
                    if (comp2 != null && comp2.PowerOn)
                    {
                        __result = false;
                        return false;
                    }
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(CompTemperatureDamaged), "CheckTakeDamage")]
        public static class Patch_NoTempDamageInIncubator
        {
            [HarmonyPrefix]
            public static bool Prefix(CompTemperatureDamaged __instance)
            {
                if (__instance.parent == null)
                    return true;
                CompEggSafeBox comp1 = Patch_Temp.GetSafeBoxForEgg((Thing)__instance.parent)
                    ?.GetComp<CompEggSafeBox>();
                if (comp1 != null && comp1.Props.frozenSafeBox)
                {
                    // if are we, not damage it
                    CompPowerTrader comp2 = comp1.parent.TryGetComp<CompPowerTrader>();
                    if (comp2 != null && comp2.PowerOn)
                        return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(CompTemperatureRuinable), "CompTick")]
        public static class Patch_CompTemperatureRuinable_Freeze
        {
            [HarmonyPrefix]
            public static bool Prefix(CompTemperatureRuinable __instance)
            {
                if (__instance.parent == null)
                    return true;
                CompEggSafeBox comp1 = Patch_Temp.GetSafeBoxForEgg((Thing)__instance.parent)
                    ?.GetComp<CompEggSafeBox>();
                if (comp1 != null && comp1.Props.frozenSafeBox)
                {
                    // if are we, set damage progress to 0
                    CompPowerTrader comp2 = comp1.parent.TryGetComp<CompPowerTrader>();
                    if (comp2 != null && comp2.PowerOn)
                    {
                        Traverse.Create((object)__instance).Field("ruinedPercent").SetValue((object)0.0f);
                        return false;
                    }
                }

                return true;
            }
        }
    }
}