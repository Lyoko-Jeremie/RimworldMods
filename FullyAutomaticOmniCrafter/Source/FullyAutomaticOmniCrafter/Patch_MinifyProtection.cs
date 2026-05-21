using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    [HarmonyPatch(typeof(MinifyUtility), nameof(MinifyUtility.MakeMinified))]
    public static class Patch_MinifyUtility_MakeMinified
    {
        public static bool Prefix(Thing thing, ref MinifiedThing __result)
        {
            if (thing is Building_OmniPhantomWall || thing is Building_OmniPhantomWall2)
            {
                // 检查当前是否是由玩家操作触发。
                // 如果是 JobDriver_Uninstall 触发，我们可以检查当前 Pawn。
                
                // 尝试从当前 Job 获取执行者
                if (Current.ProgramState == ProgramState.Playing)
                {
                    // 我们检查当前堆栈中是否有 JobDriver_Uninstall
                    var st = new System.Diagnostics.StackTrace();
                    for (int i = 0; i < st.FrameCount; i++)
                    {
                        var method = st.GetFrame(i).GetMethod();
                        if (method != null && method.DeclaringType == typeof(JobDriver_Uninstall))
                        {
                            // 查找正在对该 thing 执行 Uninstall 任务的 Pawn
                            if (thing.Map != null)
                            {
                                foreach (Pawn p in thing.Map.mapPawns.AllPawnsSpawned)
                                {
                                    if (p.CurJob != null && p.CurJob.def == JobDefOf.Uninstall && p.CurJob.targetA.Thing == thing)
                                    {
                                        if (p.Faction != null && p.Faction != Faction.OfPlayer)
                                        {
                                            Log.Message($"[OmniPhantomWall] 拦截非玩家派系({p.Faction})成员 {p.LabelShort} 打包 {thing.Label}");
                                            __result = null;
                                            return false;
                                        }
                                        return true; // 找到了玩家派系执行者，允许
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Designator_Uninstall), nameof(Designator_Uninstall.CanDesignateThing))]
    public static class Patch_Designator_Uninstall_CanDesignateThing
    {
        public static void Postfix(Thing t, ref AcceptanceReport __result)
        {
            if (!__result.Accepted) return;

            if (t is Building_OmniPhantomWall || t is Building_OmniPhantomWall2)
            {
                // 允许玩家对自己的幻影墙下达打包指令（如果墙体本身是可打包的）。
                // 这里仅拦截非玩家派系的墙体（除非开启上帝模式）。
                if (t.Faction != null && t.Faction != Faction.OfPlayer && !DebugSettings.godMode)
                {
                    __result = false;
                }
            }
        }
    }
}
