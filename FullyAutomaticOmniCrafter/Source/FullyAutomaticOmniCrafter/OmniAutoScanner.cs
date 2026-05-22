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
    public class CompProperties_OmniAutoDetector : CompProperties
    {
        public CompProperties_OmniAutoDetector()
        {
            this.compClass = typeof(CompOmniAutoDetector);
        }
    }

    /// <summary>
    /// 全自动万能扫描器 (全自动探测尖塔) (全自动监测终端) OmniAutoDetector
    /// 当前支持全自动发现全图的异常，并根据设置清除异常状态或对隐形敌人反隐（不杀死隐形敌人）
    /// </summary>
    public class CompOmniAutoDetector : ThingComp
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
                    defaultLabel = "OmniAutoDetector_AutoCureMetalhorrorLabel".Translate(),
                    defaultDesc = "OmniAutoDetector_AutoCureMetalhorrorDesc".Translate(),
                    icon = TexCommand.Draft, // 这里可以换成你自己的图标
                    isActive = () => autoCureMetalhorror,
                    toggleAction = () => { autoCureMetalhorror = !autoCureMetalhorror; }
                };
            }

            // 破除隐形开关
            yield return new Command_Toggle
            {
                defaultLabel = "OmniAutoDetector_AutoVisitableEntitiesLabel".Translate(),
                defaultDesc = "OmniAutoDetector_AutoVisitableEntitiesDesc".Translate(),
                icon = TexCommand.ForbidOff,
                isActive = () => autoVisitableEntities,
                toggleAction = () => { autoVisitableEntities = !autoVisitableEntities; }
            };

            if (ModsConfig.AnomalyActive)
            {
                // 自动清除受污染食物开关
                yield return new Command_Toggle
                {
                    defaultLabel = "OmniAutoDetector_AutoPurgeFoodLabel".Translate(),
                    defaultDesc = "OmniAutoDetector_AutoPurgeFoodDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Deconstruct") ?? BaseContent.WhiteTex,
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
                // 遍历所有殖民者、囚牢和奴隶
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.Faction != Faction.OfPlayer && !pawn.IsPrisonerOfColony && !pawn.IsSlaveOfColony) continue;

                    Hediff parasite = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.MetalhorrorImplant);
                    if (parasite != null)
                    {
                        pawn.health.RemoveHediff(parasite);
                        Messages.Message("OmniAutoDetector_MetalhorrorCured".Translate(pawn.Name.ToStringShort), pawn,
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
                                    Messages.Message("OmniAutoDetector_InvisibilityBroken".Translate(p.LabelShortCap, hd.Label), new TargetInfo(p.Position, map),
                                        MessageTypeDefOf.PositiveEvent);
                                }
                            }
                            else if (hd.def.defName.Contains("Invisibility") || hd.def.label.Contains("隐形") || hd.def.label.Contains("Invisibility"))
                            {
                                // 如果没有组件但名字包含隐形，直接移除（兼容一些简单实现的MOD）
                                p.health.RemoveHediff(hd);
                                Messages.Message("OmniAutoDetector_InvisibilityRemoved".Translate(p.LabelShortCap, hd.Label, hd.def.defName),
                                    new TargetInfo(p.Position, map),
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
                            var comp = twc.AllComps[i];
                            if (comp.GetType().Name == "CompMetalhorrorInfectible")
                            {
                                // 必须检查是否有感染记录。原版中该组件会挂在所有食物上，但 Infections > 0 才代表已污染。
                                // 使用反射获取 Infections 属性
                                var infectionsProp = comp.GetType().GetProperty("Infections");
                                if (infectionsProp != null)
                                {
                                    int count = (int)infectionsProp.GetValue(comp);
                                    if (count > 0)
                                    {
                                        contaminatedItems.Add(thing);
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                foreach (Thing item in contaminatedItems)
                {
                    string label = item.Label;
                    item.Destroy(DestroyMode.Vanish);
                    Messages.Message("OmniAutoDetector_ContaminatedFoodDestroyed".Translate(label), MessageTypeDefOf.PositiveEvent);
                }
            }
        }
    }
}
