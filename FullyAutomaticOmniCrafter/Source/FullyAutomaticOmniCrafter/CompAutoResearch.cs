using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_AutoResearch : CompProperties
    {
        public CompProperties_AutoResearch()
        {
            this.compClass = typeof(CompAutoResearch);
        }
    }
    
    /// <summary>
    /// 自动研究器，通过消耗电网中存储的电量，自动推进当前被选定的研究项目。
    /// 对于电量的使用顺序，检查电网中所有的电池，
    ///     优先使用 CompMatterEnergyConverterBattery 中的电量，
    ///     其次使用 CompOmniCrafterSmartInfiniteBattery 中的电量，
    ///     最后使用 CompPowerBattery 中的电量。
    /// </summary>
    public class CompAutoResearch : ThingComp
    {
        public CompProperties_AutoResearch Props => (CompProperties_AutoResearch)props;
        private CompPowerTrader powerComp;

        // 在建筑生成时获取其电力组件
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = this.parent.GetComp<CompPowerTrader>();
        }
        
        public override void CompTick()
        {
            base.CompTick();

            // 检查：如果建筑有电力组件且当前没电，则停止工作
            if (powerComp != null && !powerComp.PowerOn)
            {
                return;
            }

            // 获取游戏当前选定的研究项目
            ResearchProjectDef currentProj = Find.ResearchManager.currentProj;
            if (currentProj != null)
            {
                // TODO 先计算还需要多少研究点数，再计算需要扣除的电量，再扣除电量，最后增加研究点数

            }
        }
    }
}