using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 通用「装备提升技能等级」组件属性，可挂到任意 Apparel 上复用。
    ///
    /// 用法：在 ThingDef 的 comps 里加入
    /// <c>&lt;li Class="FullyAutomaticOmniCrafter.CompProperties_SkillLevelBoost"&gt;</c>，
    /// 并可用 <c>&lt;skillLevel&gt;</c> 指定目标等级、<c>&lt;skills&gt;</c> 限定技能。
    /// 穿戴者穿着期间，其技能等级会被提升到该目标等级；脱下立即还原。
    ///
    /// 实现要点：不修改 SkillRecord.levelInt，只在外层拦截等级查询结果，
    /// 因此脱下装备、读档、切换装备都会自然还原，也不会污染存档。
    /// 语义与原版天赋（Aptitude，来自基因/特质/身体状态）一致：
    /// 只抬高用于显示与计算的等级，不改变已学经验与升级进度。
    /// </summary>
    public class CompProperties_SkillLevelBoost : CompProperties
    {
        /// <summary>穿戴期间希望达到的技能等级，默认 20（原版上限）。</summary>
        public int skillLevel = 20;

        /// <summary>只作用于这些技能；留空或省略表示作用于全部技能。</summary>
        public List<SkillDef> skills;

        public CompProperties_SkillLevelBoost()
        {
            compClass = typeof(CompSkillLevelBoost);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef))
                yield return error;

            if (skillLevel <= 0)
                yield return "CompProperties_SkillLevelBoost.skillLevel 必须大于 0，否则该组件不会产生任何效果。";

            if (skillLevel > SkillRecord.MaxLevel)
                yield return "CompProperties_SkillLevelBoost.skillLevel 高于原版上限 " + SkillRecord.MaxLevel
                    + "：等级数值仍会参与运算，但技能面板的等级描述会显示为 Unknown。";

            if (skills == null) yield break;

            for (int i = 0; i < skills.Count; i++)
                if (skills[i] == null)
                    yield return "CompProperties_SkillLevelBoost.skills 第 " + i + " 项为空。";
        }
    }

    /// <summary>通用「装备提升技能等级」组件本体：只读配置，不持有任何运行时状态。</summary>
    public class CompSkillLevelBoost : ThingComp
    {
        public CompProperties_SkillLevelBoost Props => (CompProperties_SkillLevelBoost)props;

        /// <summary>本组件对指定技能的目标等级；返回 0 表示该技能不受本组件影响。</summary>
        public int TargetLevelFor(SkillDef skill)
        {
            CompProperties_SkillLevelBoost props = Props;
            List<SkillDef> list = props.skills;
            if (list != null && list.Count > 0)
            {
                bool affected = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == skill)
                    {
                        affected = true;
                        break;
                    }
                }
                if (!affected) return 0;
            }
            return props.skillLevel;
        }
    }

    /// <summary>
    /// 技能等级提升的查询工具。每次查询只做定长小循环，不分配内存、不维护全局状态，
    /// 因此在 1.6 的多线程环境下也不存在数据竞争。
    /// </summary>
    internal static class SkillLevelBoostUtility
    {
        /// <summary>
        /// 汇总穿戴者身上所有「技能等级提升」组件的目标等级并取最大值；无则为 0。
        /// </summary>
        public static int GetBoostedLevel(Pawn pawn, SkillDef skill)
        {
            if (pawn == null || skill == null) return 0;

            Pawn_ApparelTracker apparel = pawn.apparel;
            // 动物等没有 Pawn_ApparelTracker 的穿戴者直接跳过。
            if (apparel == null || apparel.WornApparelCount == 0) return 0;

            List<Apparel> worn = apparel.WornApparel;
            int best = 0;
            for (int i = 0; i < worn.Count; i++)
            {
                CompSkillLevelBoost comp = worn[i].GetComp<CompSkillLevelBoost>();
                if (comp == null) continue;

                int target = comp.TargetLevelFor(skill);
                if (target > best) best = target;
            }
            return best;
        }
    }

    /// <summary>
    /// SkillRecord 的等级计算出口，作用于所有按等级计算的逻辑（工作速度、成功率、技能需求等）
    /// 以及 SkillRecord.Level 属性。只做抬高，不做降低，因此不会干扰其它补丁（例如
    /// OmniWorkProxy 把等级上限放宽到 999 的补丁）已经算出的结果。
    /// </summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevel))]
    public static class Patch_SkillLevelBoost_GetLevel
    {
        [HarmonyPostfix]
        public static void Postfix(SkillRecord __instance, bool includeAptitudes, ref int __result)
        {
            // includeAptitudes == false 表示调用方只要「已学等级」，原版天赋同样不计入，保持一致。
            if (!includeAptitudes) return;
            // 被完全禁用的技能维持 0，与原版「TotallyDisabled 优先于天赋」的语义一致。
            if (__instance.TotallyDisabled) return;

            int target = SkillLevelBoostUtility.GetBoostedLevel(__instance.Pawn, __instance.def);
            if (target > __result) __result = target;
        }
    }

    /// <summary>技能面板的等级显示走 GetLevelForUI，必须同步，否则面板与实际效果不一致。</summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevelForUI))]
    public static class Patch_SkillLevelBoost_GetLevelForUI
    {
        [HarmonyPostfix]
        public static void Postfix(SkillRecord __instance, bool includeAptitudes, ref int __result)
        {
            if (!includeAptitudes) return;
            // 与 GetLevel 使用同一套禁用判定，而不是原版 GetLevelForUI 只看的 PermanentlyDisabled。
            // 原版 GetLevelForUI 对“临时禁用”仍返回已学等级，但装备加成必须遵循 GetLevel 的语义：
            // 技能一旦被禁用（例如被其它 Mod 用 Hediff 的 disabledWorkTags 整体压制），装备不得把等级抬回来。
            if (__instance.TotallyDisabled) return;

            int target = SkillLevelBoostUtility.GetBoostedLevel(__instance.Pawn, __instance.def);
            if (target > __result) __result = target;
        }
    }
}
