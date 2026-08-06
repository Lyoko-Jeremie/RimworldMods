using System.Collections.Generic;
using System.Linq;
using FullyAutomaticOmniCrafter.UtilApi;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能重生平台的 Comp 属性配置。
    /// </summary>
    public class CompProperties_OmniResurrector : CompProperties
    {
        /// <summary>每次复活一次性消耗的电量（Wd）。</summary>
        public float energyCostWd = 100000f;

        /// <summary>允许 Infinite 模式的 Omni 发电机覆盖储能缺口（项目惯例）。</summary>
        public bool allowInfiniteGenerator = true;

        /// <summary>是否根据尸体腐烂程度附加副作用（复活病等，仿原版复活着血清）。</summary>
        public bool sideEffects = false;

        /// <summary>复活时留下疤痕的概率（0-1）。</summary>
        public float gettingScarsChance = 0f;

        /// <summary>复活时清除"XX死了"相关的思想。</summary>
        public bool removeDiedThoughts = true;

        /// <summary>复活敌对派系 Pawn 时不自动生成袭击 Lord。</summary>
        public bool noLord = true;

        public CompProperties_OmniResurrector()
        {
            this.compClass = typeof(CompOmniResurrector);
        }
    }

    /// <summary>
    /// 万能重生平台图标（预加载，缺失时用白块兜底）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OmniResurrectorTex
    {
        public static readonly Texture2D IconResurrect =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniResurrector", false) ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 万能重生平台。
    /// 在建筑的操作界面中选择已死亡的 Pawn（无论是否留下尸体）进行复活。
    /// 复活为即时操作：选择时只要电网电量足够，一次性扣电后 Pawn 立即在建筑中心附近重生。
    /// 全程只由玩家操作 UI 界面完成，不需要小人参与。
    /// 已登记（受 GC 保护）与未登记的 Pawn 共用同一个列表，已登记优先显示在上方。
    /// </summary>
    public class CompOmniResurrector : ThingComp
    {
        public CompProperties_OmniResurrector Props => (CompProperties_OmniResurrector)this.props;

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "OpenOmniResurrectorUI".Translate(),
                defaultDesc = "OpenOmniResurrectorUIDesc".Translate(),
                icon = OmniResurrectorTex.IconResurrect,
                action = () => Find.WindowStack.Add(new Dialog_OmniResurrector(this))
            };
        }

        public override string CompInspectStringExtra()
        {
            GameComponent_OmniResurrector mgr = GameComponent_OmniResurrector.Instance;
            int registeredCount = mgr?.Registered?.Count ?? 0;
            string text = "OmniResurrector_InspectRegistered".Translate(registeredCount);
            PowerNet net = parent.GetComp<CompPowerTrader>()?.PowerNet;
            if (net != null)
            {
                OmniPowerNetStorageState state = OmniPowerNetUtility.GetPowerNetStorageState(net, 0f, Props.allowInfiniteGenerator);
                text += "\n" + "OmniResurrector_InspectEnergy".Translate(state.AvailableStoredEnergyWd.ToString("F0"));
            }
            return text;
        }

        /// <summary>
        /// 立即复活指定 Pawn：
        /// 1. 校验 Pawn 状态；
        /// 2. 从本建筑所在电网一次性扣除所需电量；
        /// 3. 调用原版 ResurrectionUtility.TryResurrect 复活（无尸体也支持）；
        /// 4. 在建筑中心附近重生；
        /// 5. 若该 Pawn 已登记则自动解除登记。
        /// </summary>
        public bool TryResurrectNow(Pawn pawn)
        {
            if (pawn == null || !pawn.Dead || pawn.Discarded || parent.Map == null)
            {
                return false;
            }
            PowerNet net = parent.GetComp<CompPowerTrader>()?.PowerNet;
            if (!OmniPowerNetUtility.CanDeductFromPowerNet(net, Props.energyCostWd, out _, Props.allowInfiniteGenerator))
            {
                return false;
            }
            if (!OmniPowerNetUtility.TryDrainFromPowerNet(net, Props.energyCostWd, out _, allowInfiniteOmniPowerGenerator: Props.allowInfiniteGenerator))
            {
                return false;
            }

            if (Props.sideEffects)
            {
                // 仿原版 TryResurrectWithSideEffects 的副作用，但由本方法控制重生位置。
                AddResurrectionSideEffects(pawn);
            }

            if (!ResurrectionUtility.TryResurrect(pawn, new ResurrectionParams
            {
                dontSpawn = true,
                removeDiedThoughts = Props.removeDiedThoughts,
                gettingScarsChance = Props.gettingScarsChance,
                noLord = Props.noLord
            }))
            {
                return false;
            }

            IntVec3 cell = FindRespawnCell();
            GenSpawn.Spawn(pawn, cell, parent.Map);

            // 特效与音效。
            FleckMaker.ThrowLightningGlow(pawn.DrawPos, parent.Map, 1.5f);
            SoundDefOf.MechSerumUsed?.PlayOneShot(SoundInfo.InMap((TargetInfo)pawn));

            // 复活完成，解除登记（若登记过）。
            GameComponent_OmniResurrector.Instance?.Unregister(pawn);
            return true;
        }

        /// <summary>
        /// 查找建筑中心附近的可用重生格：
        /// 优先建筑外围一圈（ExpandedBy(1)），失败则扩大半径随机搜索，最后兜底用中心格。
        /// </summary>
        private IntVec3 FindRespawnCell()
        {
            Map map = parent.Map;
            IntVec3 center = parent.OccupiedRect().CenterCell;
            foreach (IntVec3 c in parent.OccupiedRect().ExpandedBy(1))
            {
                if (IsValidRespawnCell(c, map))
                {
                    return c;
                }
            }
            if (CellFinder.TryFindRandomCellNear(center, map, 4,
                c => IsValidRespawnCell(c, map), out IntVec3 result))
            {
                return result;
            }
            return center;
        }

        private static bool IsValidRespawnCell(IntVec3 c, Map map)
        {
            return c.InBounds(map)
                && c.Walkable(map)
                && !map.fogGrid.IsFogged(c)
                && !map.thingGrid.CellContains(c, ThingCategory.Pawn);
        }

        /// <summary>
        /// 根据尸体腐烂天数附加复活副作用（复活病、痴呆、失明、复活精神病）。
        /// 数值曲线与原版 ResurrectionUtility 一致（腐烂 0.1 天时 2% 概率，5 天时 80% 概率）。
        /// </summary>
        private static readonly SimpleCurve RotSideEffectChanceCurve = new SimpleCurve
        {
            { new CurvePoint(0.1f, 0.02f), true },
            { new CurvePoint(5f, 0.8f), true }
        };

        private void AddResurrectionSideEffects(Pawn pawn)
        {
            Corpse corpse = pawn.Corpse;
            float rotDays = corpse == null ? 0f : corpse.GetComp<CompRottable>().RotProgress / 60000f;
            BodyPartRecord brain = pawn.health.hediffSet.GetBrain();

            Hediff sickness = HediffMaker.MakeHediff(HediffDefOf.ResurrectionSickness, pawn);
            if (!pawn.health.WouldDieAfterAddingHediff(sickness))
            {
                pawn.health.AddHediff(sickness);
            }

            if (brain != null && Rand.Chance(RotSideEffectChanceCurve.Evaluate(rotDays)))
            {
                Hediff dementia = HediffMaker.MakeHediff(HediffDefOf.Dementia, pawn, brain);
                if (!pawn.health.WouldDieAfterAddingHediff(dementia))
                {
                    pawn.health.AddHediff(dementia);
                }
            }

            if (Rand.Chance(RotSideEffectChanceCurve.Evaluate(rotDays)))
            {
                foreach (BodyPartRecord eye in pawn.health.hediffSet.GetNotMissingParts()
                    .Where(x => x.def == BodyPartDefOf.Eye))
                {
                    if (!pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(eye))
                    {
                        Hediff blindness = HediffMaker.MakeHediff(HediffDefOf.Blindness, pawn, eye);
                        pawn.health.AddHediff(blindness);
                    }
                }
            }

            if (brain != null && Rand.Chance(RotSideEffectChanceCurve.Evaluate(rotDays)))
            {
                Hediff psychosis = HediffMaker.MakeHediff(HediffDefOf.ResurrectionPsychosis, pawn, brain);
                if (!pawn.health.WouldDieAfterAddingHediff(psychosis))
                {
                    pawn.health.AddHediff(psychosis);
                }
            }
        }
    }
}
