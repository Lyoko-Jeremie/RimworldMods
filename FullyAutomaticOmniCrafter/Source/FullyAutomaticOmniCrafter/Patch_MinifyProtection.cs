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
                Pawn actor = null;
                
                // 尝试从当前 Job 获取执行者
                if (Current.ProgramState == ProgramState.Playing)
                {
                    // 检查当前是否在玩家的设计模式下（上帝模式除外）
                    // 如果有其他 Mod 赋予了 Minifiable 属性，敌人可能尝试通过 Job 移除它。
                    
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
                                        break; // 找到了执行者，检查完毕
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
                // 确保只有玩家派系可以指定打包。
                // 实际上原版 Designator_Uninstall 已经检查了 Faction.OfPlayer。
                // 但如果有其他 Mod 修改了 Designator 逻辑，这里做二次确认。
                if (t.Faction != Faction.OfPlayer && !DebugSettings.godMode)
                {
                    __result = false;
                }
            }
        }
    }
}
