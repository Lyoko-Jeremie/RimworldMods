using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ── 蓝图（Blueprint_Build）首次放置时挂钩 ─────────────────────────────────
    // Blueprint.SpawnSetup 由 Blueprint_Build / Blueprint_Install 等所有蓝图子类共享。
    // 仅拦截 Blueprint_Build 类型，且只在非存档加载时触发。
    [HarmonyPatch(typeof(Blueprint_Build), "SpawnSetup")]
    public static class Patch_Blueprint_Build_SpawnSetup
    {
        [HarmonyPostfix]
        public static void Postfix(Blueprint_Build __instance, bool respawningAfterLoad)
        {
            // 存档加载时蓝图批量重新 Spawn，不触发：游戏启动后 TickRare 会扫一次全量
            if (respawningAfterLoad) return;
            if (__instance.Faction != Faction.OfPlayer) return;
            if (__instance.Spawned)
                BlueprintFrameTracker.MarkDirty(__instance.Map);
        }
    }

    // ── 施工框（Frame）首次出现时挂钩 ────────────────────────────────────────
    // Frame 由 Blueprint.TryReplaceWithSolidThing 通过 GenSpawn.Spawn 放置到地图，
    // Frame 本身未重写 SpawnSetup，因此挂钩 TryReplaceWithSolidThing 的 Postfix
    // 是最精确的 Frame 出现时机。
    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.TryReplaceWithSolidThing))]
    public static class Patch_Blueprint_TryReplaceWithSolidThing
    {
        [HarmonyPostfix]
        public static void Postfix(bool __result, Thing createdThing)
        {
            if (!__result) return;
            if (createdThing is Frame frame
                && frame.Spawned
                && frame.Faction == Faction.OfPlayer)
            {
                BlueprintFrameTracker.MarkDirty(frame.Map);
            }
        }
    }
}

