using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{

    [HarmonyPatch(typeof(OrbitalStrike), "Tick")]
    public static class Patch_OrbitalStrike_Tick
    {
        public static void Postfix(OrbitalStrike __instance)
        {
            if (!__instance.Spawned || __instance.Map == null) return;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            float radius = 0f;
            if (__instance is PowerBeam) radius = 15f;
            else if (__instance is Bombardment b) radius = b.impactAreaRadius + 8f; // 加上爆炸半径

            if (tracker.IsAreaProtected(__instance.Position, radius, __instance.instigator, out _))
            {
                __instance.Destroy();
            }
        }
    }

    [HarmonyPatch(typeof(Verb), "CanHitTargetFrom")]
    public static class Patch_Verb_LaunchProjectile_CanHitTargetFrom
    {
        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || !(__instance is Verb_LaunchProjectile) || __instance.caster?.Map == null) return;

            var tracker = __instance.caster.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 如果攻击者是敌人，且目标位置受保护，拦截
            if (tracker.IsTargetProtected(targ.Thing, __instance.caster) || 
                (targ.HasThing == false && tracker.IsCellProtected(targ.Cell, __instance.caster, out _)))
            {
                __result = false;
                return;
            }

            // 如果攻击者自己就在受保护位置（敌方护盾内），也不能向外射击
            if (tracker.IsCellProtected(root, __instance.caster, out _))
            {
                __result = false;
                return;
            }
        }
    }

}