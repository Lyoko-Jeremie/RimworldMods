using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 陪伴互动（Def 驱动）：按 AM_ServitudeBond Def 中的互动目录随机触发。
    /// 每个互动按"前置状态 + 概率 + 冷却 + 主人状态"判定，命中后写冷却、喷粒子、可选发信件、返回对应 Job。
    /// 新增互动 = 加 XML 条目（+ 可选新 JobDef/JobDriver），不修改本类。
    /// </summary>
    public class JobGiver_AMCompanion : ThinkNode_JobGiver_ServitudeBase
    {
        // 缓存互动目录 Def（Def 加载完成后首次访问即缓存，避免每 60 tick 字符串查找）
        private static ArtificialMaidServitudeDef cachedServitudeDef;

        private static ArtificialMaidServitudeDef ServitudeDef
        {
            get
            {
                if (cachedServitudeDef == null)
                {
                    cachedServitudeDef = DefDatabase<ArtificialMaidServitudeDef>.GetNamed("AM_ServitudeBond", false);
                }
                return cachedServitudeDef;
            }
        }

        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 分频：思考树节拍之外再限 60 tick，避免高频概率判定
            if (!pawn.IsHashIntervalTick(60))
            {
                return null;
            }

            ArtificialMaidServitudeDef def = ServitudeDef;
            if (def == null)
            {
                return null;
            }

            ArtificialMaidServitudeExtension ext = def.GetModExtension<ArtificialMaidServitudeExtension>();
            if (ext == null || ext.interactions == null || ext.interactions.Count == 0)
            {
                return null;
            }

            foreach (ArtificialMaidServitudeInteraction interaction in ext.interactions.InRandomOrder<ArtificialMaidServitudeInteraction>())
            {
                // 主人前置状态
                if (interaction.requiredMasterState == ArtificialMaidMasterState.Resting && !IsResting(master))
                {
                    continue;
                }

                if (interaction.requiredMasterState == ArtificialMaidMasterState.Awake && IsResting(master))
                {
                    continue;
                }

                // 概率
                if (!Rand.Chance(interaction.baseChance))
                {
                    continue;
                }

                // 冷却
                if (interaction.jobDef == null || mgr.IsOnCooldown(pawn, interaction.jobDef))
                {
                    continue;
                }

                // 主人被征召/战斗/亲密中不打扰
                if (master.Drafted)
                {
                    continue;
                }

                if (master.CurJob != null &&
                    (master.CurJob.def == JobDefOf.AttackMelee || master.CurJob.def == JobDefOf.AttackStatic ||
                     master.CurJob.def == JobDefOf.Lovin))
                {
                    continue;
                }

                // 可达性
                if (!pawn.CanReserveAndReach(master, PathEndMode.Touch, Danger.None))
                {
                    continue;
                }

                // 命中：写冷却
                mgr.StartCooldown(pawn, interaction.jobDef, interaction.cooldownTicks);

                // 粒子
                if (interaction.fleckDef != null)
                {
                    FleckMaker.ThrowMetaIcon(pawn.Position, pawn.Map, interaction.fleckDef);
                    FleckMaker.ThrowMetaIcon(master.Position, master.Map, interaction.fleckDef);
                }

                // 信件（可选）
                if (!string.IsNullOrEmpty(interaction.letterLabelKey) && !string.IsNullOrEmpty(interaction.letterTextKey))
                {
                    Find.LetterStack.ReceiveLetter(
                        interaction.letterLabelKey.Translate(),
                        interaction.letterTextKey.Translate(pawn.LabelShort, master.LabelShort),
                        LetterDefOf.NeutralEvent,
                        new LookTargets(pawn, master));
                }

                return JobMaker.MakeJob(interaction.jobDef, master);
            }

            return null;
        }

        /// <summary>主人是否处于休息/维持姿势状态（膝枕等互动的前置）。</summary>
        private static bool IsResting(Pawn master)
        {
            if (master.CurJob == null)
            {
                return false;
            }

            return master.CurJob.def == JobDefOf.LayDown || master.CurJob.def == JobDefOf.Wait_MaintainPosture;
        }
    }
}
