using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_OmniAutoScanner : CompProperties
    {
        public CompProperties_OmniAutoScanner()
        {
            this.compClass = typeof(CompOmniAutoScanner);
        }
    }

    /// <summary>
    /// 全自动万能扫描器 ，能够全自动发现全图的异常，并根据设置清除异常状态或对隐形敌人反隐（不杀死隐形敌人）
    /// </summary>
    public class CompOmniAutoScanner : ThingComp
    {
        // 玩家的设置开关
        public bool autoCureMetalhorror = true;
        public bool autoVisitableEntities = true;
        public bool autoPurgeFood = true;

        // 保存设置到存档
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref autoCureMetalhorror, "autoCureMetalhorror", true);
            Scribe_Values.Look(ref autoVisitableEntities, "autoVisitableEntities", true);
            Scribe_Values.Look(ref autoPurgeFood, "autoPurgeFood", true);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 治愈金属怪形开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动清除体内异象",
                defaultDesc = "开启后，瞬间无痛抹除全图殖民者体内的金属怪形寄生状态。",
                icon = TexCommand.Draft, // 这里可以换成你自己的图标
                isActive = () => autoCureMetalhorror,
                toggleAction = () => { autoCureMetalhorror = !autoCureMetalhorror; }
            };

            // 秒杀隐形生物开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动反隐隐形实体",
                defaultDesc = "开启后，全图任何带有隐形状态的敌对实体将立即反隐。",
                icon = TexCommand.ForbidOff,
                isActive = () => autoVisitableEntities,
                toggleAction = () => { autoVisitableEntities = !autoVisitableEntities; }
            };
        }

        public override void CompTickRare()
        {
            base.CompTickRare();

            // 检查是否通电（如果有关联电力组件的话）
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return;

            Map map = parent.Map;
            if (map == null) return;

            // 1. 无痛治愈金属怪形
            if (autoCureMetalhorror)
            {
                // 遍历所有殖民者和囚犯
                foreach (Pawn pawn in map.mapPawns.FreeColonistsAndPrisoners)
                {
                    // 假设 Anomaly 的寄生 Hediff 叫做 MetalhorrorImplant (需查阅源码核实确切 DefName)
                    Hediff parasite = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("MetalhorrorImplant"));
                    if (parasite != null)
                    {
                        pawn.health.RemoveHediff(parasite);
                        Messages.Message($"{pawn.Name.ToStringShort} 体内的异象已被扫描仪安全抹除。", pawn,
                            MessageTypeDefOf.PositiveEvent);
                    }
                }
            }

            // 2. 破除隐形实体 (亡魂、潜见者)
            if (autoVisitableEntities)
            {
                // 遍历全图所有 Pawn
                List<Pawn> allPawns = map.mapPawns.AllPawns;
                // 为了防止在循环中直接杀死对象导致集合改变报错，先收集目标
                List<Pawn> targetsToKill = new List<Pawn>();

                for (int i = 0; i < allPawns.Count; i++)
                {
                    Pawn p = allPawns[i];
                    if (p.HostileTo(Faction.OfPlayer))
                    {
                        // 检查是否有隐形状态
                        if (p.health.hediffSet.HasHediff(HediffDefOf.Invisibility) ||
                            p.def.defName == "Revenant" || p.def.defName == "Sightstealer")
                        {
                            // TODO
                        }
                    }
                }

                // 处决目标
                foreach (Pawn target in targetsToKill)
                {
                    // 方式 A：直接消除（连尸体都不剩）
                    // target.Destroy(DestroyMode.Vanish); 

                    // 方式 B：判定死亡，留下尸体供研究
                    // target.Kill(null, null);
                    Messages.Message($"全自动扫描仪已反隐隐形异象: {target.Label}", new TargetInfo(target.Position, map),
                        MessageTypeDefOf.PositiveEvent);
                }
            }
        }
    }
}
