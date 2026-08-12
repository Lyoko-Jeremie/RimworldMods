using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 自动征召（事件驱动，零轮询）：主人 Drafter.Drafted 状态变化时，
    /// 自动征召其全部侍奉女仆并进入守卫模式（SetGuardMode 互斥关闭猎杀）。
    /// - 柜中女仆：展示柜允许唤醒（autoWake）时先 WakeContainedMaid 再征召；
    /// - 留守（standbyMode）女仆：不自动征召、不唤醒；
    /// - 玩家手动解除某女仆征召（主人仍征召）不强制重新征召（仅响应主人状态变化）。
    /// 防递归：主仆关系无环（主人必非女仆），且本 patch 只写女仆不写主人。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    public static class Patch_Pawn_DraftController_Servitude
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_DraftController __instance, bool value)
        {
            Pawn master = __instance.pawn;
            if (master == null || master.Dead || master.Destroyed)
            {
                return;
            }

            ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
            if (mgr == null)
            {
                return;
            }

            List<Pawn> servants = mgr.GetServants(master);
            if (servants.Count == 0)
            {
                return;
            }

            for (int i = 0; i < servants.Count; i++)
            {
                Pawn maid = servants[i];
                if (maid == null || maid == master || maid.Dead || maid.Destroyed)
                {
                    continue;
                }

                CompArtificialMaid comp = CompArtificialMaid.GetCompCached(maid);
                if (comp == null || comp.standbyMode)
                {
                    continue; // 留守：不自动征召、不唤醒
                }

                // 倒地/精神崩溃中的女仆不自动征召（等待救援；守卫无意义）
                if (maid.Downed || maid.InMentalState)
                {
                    continue;
                }

                // 柜内女仆未 Spawned，IsColonistPlayerControlled 不可用 → 用 Faction 判定（在唤醒之前，避免弹出敌对/非玩家女仆）
                if (maid.Faction != Faction.OfPlayer)
                {
                    continue;
                }

                // 柜中分支：展示柜允许唤醒才唤醒（尊重收纳意图）
                if (maid.ParentHolder is Building_ArtificialMaidDisplayCase dc)
                {
                    if (!value || !dc.autoWake)
                    {
                        continue; // 主人解除征召或柜子不允许唤醒 → 保持休眠
                    }

                    dc.WakeContainedMaid(true); // 复用现有 API：弹出 + 组件补齐 + 禁自动休眠
                }

                if (value)
                {
                    if (maid.Drafted)
                    {
                        comp.SetGuardMode(true); // 已征召：确保守卫开（幂等）
                        comp.autoDraftedByMaster = true;
                        continue;
                    }

                    if (maid.drafter == null)
                    {
                        comp.SetGuardMode(true); // 极端防御：无法征召则至少开启守卫
                        continue;
                    }

                    maid.drafter.Drafted = true; // setter 内部会 EndCurrentJob
                    comp.SetGuardMode(true);     // 互斥入口：自动关猎杀
                    comp.autoDraftedByMaster = true;
                    // 立即到主人身边（优先瞬移，冷却中则走向主人）
                    ArtificialMaidServitudeUtility.ImmediatelyJoinMaster(maid, master);
                    Messages.Message(
                        "AM_Servitude_AutoDraftMessage".Translate(maid.LabelShort, master.LabelShort),
                        maid, MessageTypeDefOf.NeutralEvent);
                }
                else if (maid.Drafted && maid.drafter != null && comp.autoDraftedByMaster)
                {
                    // 主人解除征召 → 仅同步解除「由自动征召触发」的女仆（玩家手动征召的不连带）
                    maid.drafter.Drafted = false;
                    comp.autoDraftedByMaster = false;
                }
            }
        }
    }

    /// <summary>
    /// 女仆侧：征召状态被解除（任何来源，含玩家手动）时清除 autoDraftedByMaster 标志，
    /// 保证主人之后解除征召时不会连带解除已被玩家手动接管的女仆。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    public static class Patch_Pawn_DraftController_ManualUndraftClear
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_DraftController __instance, bool value)
        {
            if (value)
            {
                return; // 只处理解除
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(__instance.pawn);
            if (comp != null)
            {
                comp.autoDraftedByMaster = false;
            }
        }
    }
}
