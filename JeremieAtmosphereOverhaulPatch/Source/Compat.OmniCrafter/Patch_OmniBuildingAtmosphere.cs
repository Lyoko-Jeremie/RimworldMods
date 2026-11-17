using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 条目 1：超维科技的所有建筑不受风影响（既不被打伤，也不被吹动/吹走）。
    ///
    /// 做法不是改 XML，而是在 AO 的 AirFill 出口处按 def 归属改写：
    /// AirFill 是 WindPassableLoad 成员筛选与 DampingMask 的唯一入口，
    /// 返回 0.01 即可让超维建筑退出「可穿透物风载荷」名单。
    ///
    /// 为什么不注入 airPassable=true + airFillPercent=0.01：
    /// airPassable 还会让 MapObstacleMaskBuilder.AirCanMoveThrough 返回 true，
    /// 对 fillPercent=1 的建筑（幻影墙）会把它从 obstacleMask 里抹掉，
    /// 从而破坏它原本的挡风/挡气能力；只改 AirFill 不会动阻挡几何。
    /// </summary>
    internal static class Patch_OmniBuildingAtmosphere
    {
        private const string OmniPackageId = "Jeremie.Fully.Automatic.OmniCrafter";

        private static readonly Dictionary<ThingDef, bool> OmniDefCache = new Dictionary<ThingDef, bool>();

        internal static bool Install(Harmony harmony)
        {
            MethodInfo airFill = AccessTools.Method(
                AccessTools.TypeByName("AtmosphereOverhaul.Simulation.BuildingAtmosphereExtension"),
                "AirFill", new[] { typeof(ThingDef) });
            if (airFill == null)
            {
                Log.Warning("[AOPatch] 未找到 AO BuildingAtmosphereExtension.AirFill，超维建筑风免疫未挂载。");
                return false;
            }

            harmony.Patch(airFill, postfix: new HarmonyMethod(
                typeof(Patch_OmniBuildingAtmosphere), nameof(AirFillPostfix)));
            return true;
        }

        private static void AirFillPostfix(ThingDef def, ref float __result)
        {
            if (def == null || __result <= 0.1f)
            {
                return;
            }

            if (IsOmniBuilding(def))
            {
                __result = 0.01f;
            }
        }

        private static bool IsOmniBuilding(ThingDef def)
        {
            bool cached;
            if (OmniDefCache.TryGetValue(def, out cached))
            {
                return cached;
            }

            ModContentPack pack = def.modContentPack;
            bool result = pack != null
                && def.building != null
                && string.Equals(pack.PackageId, OmniPackageId, StringComparison.OrdinalIgnoreCase);

            OmniDefCache[def] = result;
            return result;
        }
    }
}
