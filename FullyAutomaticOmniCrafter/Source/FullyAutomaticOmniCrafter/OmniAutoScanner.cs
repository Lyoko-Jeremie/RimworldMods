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
    /// 全自动万能扫描器 OmniAutoScanner
    /// 当前支持全自动发现全图的异常，并根据设置清除异常状态或对隐形敌人反隐（不杀死隐形敌人）
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

            if (ModsConfig.AnomalyActive)
            {
                // 治愈金属怪形开关
                yield return new Command_Toggle
                {
                    defaultLabel = "自动清除体内异象",
                    defaultDesc = "开启后，瞬间无痛抹除全图殖民者体内的金属怪形寄生状态。",
                    icon = TexCommand.Draft, // 这里可以换成你自己的图标
                    isActive = () => autoCureMetalhorror,
                    toggleAction = () => { autoCureMetalhorror = !autoCureMetalhorror; }
                };
            }

            // 破除隐形开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动反隐隐形实体",
                defaultDesc = "开启后，全图任何带有隐形状态的敌对实体将立即反隐。",
                icon = TexCommand.ForbidOff,
                isActive = () => autoVisitableEntities,
                toggleAction = () => { autoVisitableEntities = !autoVisitableEntities; }
            };

            if (ModsConfig.AnomalyActive)
            {
                // 自动清除受污染食物开关
                yield return new Command_Toggle
                {
                    defaultLabel = "自动销毁受污染食物",
                    defaultDesc = "开启后，自动寻找并销毁带有异常污染的食物（如金属怪形污染）。",
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Deconstruct"),
                    isActive = () => autoPurgeFood,
                    toggleAction = () => { autoPurgeFood = !autoPurgeFood; }
                };
            }
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
            if (autoCureMetalhorror && ModsConfig.AnomalyActive)
            {
                // 遍历所有殖民者、囚犯和奴隶
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.Faction != Faction.OfPlayer && !pawn.IsPrisonerOfColony && !pawn.IsSlaveOfColony) continue;

                    Hediff parasite = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.MetalhorrorImplant);
                    if (parasite != null)
                    {
                        pawn.health.RemoveHediff(parasite);
                        Messages.Message($"{pawn.Name.ToStringShort} 体内的金属怪形已被扫描仪安全抹除。", pawn,
                            MessageTypeDefOf.PositiveEvent);
                    }
                }
            }

            // 2. 破除隐形实体 (亡魂、潜见者)
            if (autoVisitableEntities)
            {
                var allPawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < allPawns.Count; i++)
                {
                    Pawn p = allPawns[i];
                    if (p.HostileTo(Faction.OfPlayer))
                    {
                        // 遍历所有 Hediff 寻找具有隐形组件的
                        List<Hediff> hediffs = p.health.hediffSet.hediffs;
                        for (int j = hediffs.Count - 1; j >= 0; j--)
                        {
                            Hediff hd = hediffs[j];
                            
                            // 检查是否有通用的隐形组件
                            HediffComp_Invisibility invisComp = hd.TryGetComp<HediffComp_Invisibility>();
                            if (invisComp != null)
                            {
                                if (!invisComp.PsychologicallyVisible)
                                {
                                    invisComp.BecomeVisible(true);
                                    Messages.Message($"全自动扫描仪已破除隐形状态: {p.LabelShortCap} ({hd.Label})", new TargetInfo(p.Position, map),
                                        MessageTypeDefOf.PositiveEvent);
                                }
                            }
                            else if (hd.def.defName.Contains("Invisibility") || hd.def.label.Contains("隐形") || hd.def.label.Contains("Invisibility"))
                            {
                                // 如果没有组件但名字包含隐形，直接移除（兼容一些简单实现的MOD）
                                p.health.RemoveHediff(hd);
                                Messages.Message($"全自动扫描仪已移除疑似隐形属性: {p.LabelShortCap} ({hd.Label})", new TargetInfo(p.Position, map),
                                    MessageTypeDefOf.PositiveEvent);
                            }
                        }
                    }
                }
            }

            // 3. 自动销毁受污染食物
            if (autoPurgeFood && ModsConfig.AnomalyActive)
            {
                // 金属怪形会通过受污染食物传播，检查带有 MetalhorrorInfectionPathway 组件的物品
                List<Thing> contaminatedItems = new List<Thing>();
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing.def.IsIngestible && thing is ThingWithComps twc)
                    {
                        // 动态检查是否存在金属怪形感染组件，避免在未安装异象时直接引用类型导致解析失败
                        for (int i = 0; i < twc.AllComps.Count; i++)
                        {
                            if (twc.AllComps[i].GetType().Name == "CompMetalhorrorInfectible")
                            {
                                contaminatedItems.Add(thing);
                                break;
                            }
                        }
                    }
                }

                foreach (Thing item in contaminatedItems)
                {
                    string label = item.Label;
                    item.Destroy(DestroyMode.Vanish);
                    Messages.Message($"全自动扫描仪已销毁受污染物品: {label}", MessageTypeDefOf.PositiveEvent);
                }
            }
        }
    }
}
