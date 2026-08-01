using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆健康修复的统一入口。
    /// 只移除伤病和致命状态，不清空健康系统，从而保留植入物及其他 Mod 的良性 Hediff。
    /// </summary>
    public static class ArtificialMaidHealthUtility
    {
        [System.ThreadStatic]
        private static HashSet<Pawn> repairingPawns;

        [System.ThreadStatic]
        private static List<Hediff> hediffsToRemove;

        public static bool IsRepairing(Pawn pawn)
        {
            return pawn != null && repairingPawns != null && repairingPawns.Contains(pawn);
        }

        /// <summary>
        /// 修复缺失部位、伤口、疾病和其他明确有害的健康状态。
        /// 返回是否实际修改了健康状态。
        /// </summary>
        public static bool RepairHarmfulHealthConditions(Pawn pawn)
        {
            if (pawn?.health?.hediffSet == null || IsRepairing(pawn))
            {
                return false;
            }

            if (repairingPawns == null)
            {
                repairingPawns = new HashSet<Pawn>();
            }

            repairingPawns.Add(pawn);
            bool changed = false;
            try
            {
                // 不触发中间态检查；所有修改完成后由外层健康流程统一收尾。
                List<Hediff_MissingPart> missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors();
                for (int i = missingParts.Count - 1; i >= 0; i--)
                {
                    pawn.health.RestorePart(missingParts[i].Part, null, false);
                    changed = true;
                }

                if (hediffsToRemove == null)
                {
                    hediffsToRemove = new List<Hediff>();
                }
                else
                {
                    hediffsToRemove.Clear();
                }

                List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
                for (int i = 0; i < hediffs.Count; i++)
                {
                    Hediff hediff = hediffs[i];
                    if (ShouldRepair(hediff))
                    {
                        hediffsToRemove.Add(hediff);
                    }
                }

                for (int i = 0; i < hediffsToRemove.Count; i++)
                {
                    Hediff hediff = hediffsToRemove[i];
                    if (pawn.health.hediffSet.hediffs.Contains(hediff))
                    {
                        pawn.health.RemoveHediff(hediff);
                        changed = true;
                    }
                }

                hediffsToRemove.Clear();
            }
            finally
            {
                repairingPawns.Remove(pawn);
            }

            // 若 Pawn 在修复前已经倒地，允许原版在安全状态下执行 MakeUndowned 等收尾。
            if (changed && !pawn.health.ShouldBeDead() && !pawn.health.ShouldBeDowned())
            {
                pawn.health.CheckForStateChange(null, null);
            }

            return changed;
        }

        private static bool ShouldRepair(Hediff hediff)
        {
            if (hediff?.def == null)
            {
                return false;
            }

            // 良性植入物、RJW 身体部件和女仆恢复系统均不会满足这些条件。
            return hediff is Hediff_Injury ||
                   hediff.def.isBad ||
                   hediff.def.IsAddiction ||
                   hediff.def.chronic ||
                   hediff.CauseDeathNow();
        }
    }
}
