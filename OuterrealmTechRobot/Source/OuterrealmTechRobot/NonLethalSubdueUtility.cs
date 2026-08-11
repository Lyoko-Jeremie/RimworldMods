using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    // 非致命制服系统的核心工具类：负责施加与解除“被制服”状态。
    // 状态由 Hediff（能力压制 + 临时防御 + 工作禁用）+ 特性（标记）+ 思绪（情绪）三部分构成，全部可逆。
    public static class NonLethalSubdueUtility
    {
        public static bool IsSubdued(Pawn pawn)
        {
            return pawn != null && pawn.health != null &&
                   pawn.health.hediffSet.HasHediff(ArtificialMaidDefOf.ArtificialMaidNonLethalSubdue);
        }

        // 施加非致命制服状态：能力与技能全部压制、强制击倒倒地，并添加特性与思绪。
        // 注意：必须在添加 Hediff 之前设置 forceDowned，使首次倒地结算时跳过“倒地即死”概率判定。
        public static void ApplySubdue(Pawn target)
        {
            if (target == null || target.health == null || target.Dead || IsSubdued(target))
            {
                return;
            }
            if (target.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                return; // 不对女仆自身生效
            }

            // 1. 先标记强制倒地（使 CheckForStateChange 跳过倒地死亡概率判定），再添加能力压制 Hediff。
            target.health.forceDowned = true;
            target.health.GetOrAddHediff(ArtificialMaidDefOf.ArtificialMaidNonLethalSubdue);

            // 2. 添加“被制服”特性标记（仅有人物故事的目标）。
            if (target.story?.traits != null &&
                !target.story.traits.HasTrait(ArtificialMaidDefOf.ArtificialMaidTrait_Subdued))
            {
                target.story.traits.GainTrait(new Trait(ArtificialMaidDefOf.ArtificialMaidTrait_Subdued));
            }

            // 3. 添加思绪（stackLimit 为 1，重复添加不会堆积）。
            if (target.needs?.mood?.thoughts?.memories != null)
            {
                target.needs.mood.thoughts.memories.TryGainMemory(ArtificialMaidDefOf.ArtificialMaidSubdued_Mood);
            }

            // 4. 兜底结算一次状态：目标必然倒地。
            target.health.CheckForStateChange(null, null);
        }

        // 解除非致命制服状态：移除 Hediff/特性/思绪，能力恢复后由 CheckForStateChange 自动使其重新站立。
        public static void ReleaseSubdue(Pawn target)
        {
            if (target == null || target.health == null)
            {
                return;
            }

            // 1. 移除能力压制 Hediff（意识恢复后 ShouldBeDowned 变为 false，会自动站起；
            //    同时 PostRemoved 会触发残留清理）。
            if (IsSubdued(target))
            {
                for (int i = target.health.hediffSet.hediffs.Count - 1; i >= 0; i--)
                {
                    Hediff h = target.health.hediffSet.hediffs[i];
                    if (h.def == ArtificialMaidDefOf.ArtificialMaidNonLethalSubdue)
                    {
                        target.health.RemoveHediff(h);
                        break;
                    }
                }
            }

            // 2. 清理残留的特性、思绪与强制倒地标记。
            CleanupSubdueRemnants(target);

            // 3. 结算状态（若仍处于倒地状态则自动站起）。
            target.health.CheckForStateChange(null, null);
        }

        // 清理被制服状态的残留物：强制倒地标记、特性、思绪。不触碰 Hediff 本身。
        public static void CleanupSubdueRemnants(Pawn target)
        {
            if (target == null || target.health == null)
            {
                return;
            }

            target.health.forceDowned = false;

            if (target.story?.traits != null &&
                target.story.traits.HasTrait(ArtificialMaidDefOf.ArtificialMaidTrait_Subdued))
            {
                Trait t = target.story.traits.GetTrait(ArtificialMaidDefOf.ArtificialMaidTrait_Subdued);
                if (t != null)
                {
                    target.story.traits.RemoveTrait(t);
                }
            }

            if (target.needs?.mood?.thoughts?.memories != null)
            {
                target.needs.mood.thoughts.memories.RemoveMemoriesOfDef(ArtificialMaidDefOf.ArtificialMaidSubdued_Mood);
            }
        }
    }
}
