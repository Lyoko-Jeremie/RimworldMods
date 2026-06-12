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
        public float energyCost = 1f;
        public float researchPoints = 100f;

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
        private bool active = false;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = this.parent.GetComp<CompPowerTrader>();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref active, "active", false);
        }
        
        public override void CompTick()
        {
            base.CompTick();

            if (active)
            {
                return;
            }

            if (powerComp != null && !powerComp.PowerOn)
            {
                return;
            }

            ResearchProjectDef currentProj = Find.ResearchManager.GetProject();
            if (currentProj == null || currentProj.IsFinished)
            {
                return;
            }

            float pointsNeeded = currentProj.baseCost - Find.ResearchManager.GetProgress(currentProj);
            if (pointsNeeded <= 0) return;

            float ratio = Props.researchPoints / Mathf.Max(Props.energyCost, 0.0001f);
            float energyNeededWd = pointsNeeded / ratio;
            
            // 每 tick 最多消耗多少电量？如果太快可能一瞬间吸干。
            // 假设每 tick 最多消耗 10 Wd (即 600,000 W 功率)
            float maxEnergyPerTickWd = 10f; 
            float energyToConsumeWd = Mathf.Min(energyNeededWd, maxEnergyPerTickWd);

            float consumedWd = ConsumeEnergyFromNet(energyToConsumeWd);
            if (consumedWd > 0)
            {
                float pointsToGained = consumedWd * ratio;
                // 使用 AddProgress 绕过 ResearchPerformed 中的 0.00825 倍率，
                // 使得 config 中的 researchPoints 直接对应 UI 上的研究点数。
                Find.ResearchManager.AddProgress(currentProj, pointsToGained, null);
            }
        }

        private float ConsumeEnergyFromNet(float amountWd)
        {
            PowerNet net = this.parent.GetComp<CompPower>()?.PowerNet;
            if (net == null) return 0f;

            float remaining = amountWd;

            // 1. CompMatterEnergyConverterBattery
            remaining -= DrawFromBatteries<CompMatterEnergyConverterBattery>(net, remaining);
            if (remaining <= 0.0001f) return amountWd;

            // 2. CompOmniCrafterSmartInfiniteBattery
            remaining -= DrawFromBatteries<CompOmniCrafterSmartInfiniteBattery>(net, remaining);
            if (remaining <= 0.0001f) return amountWd;

            // 3. CompPowerBattery (Normal)
            remaining -= DrawFromBatteries<CompPowerBattery>(net, remaining, (b) => 
                !(b is CompMatterEnergyConverterBattery) && !(b is CompOmniCrafterSmartInfiniteBattery));

            return amountWd - remaining;
        }

        private float DrawFromBatteries<T>(PowerNet net, float amountWd, System.Predicate<T> filter = null) where T : CompPowerBattery
        {
            float drawn = 0f;
            foreach (var comp in net.batteryComps)
            {
                if (comp is T battery && (filter == null || filter(battery)))
                {
                    float canDraw = GetStoredEnergy(battery);
                    float toDraw = Mathf.Min(amountWd - drawn, canDraw);
                    if (toDraw > 0)
                    {
                        battery.DrawPower(toDraw);
                        drawn += toDraw;
                    }
                }
                if (drawn >= amountWd - 0.0001f) break;
            }
            return drawn;
        }

        private float GetStoredEnergy(CompPowerBattery battery)
        {
            // 对于我们的特殊电池，由于有补丁拦截了 StoredEnergy getter（比如开关关闭时），
            // 我们需要确定是否应该直接读取字段。
            // 但如果是在电网中，batteryComps 通常是连接着的。
            // 不过 CompOmniCrafterSmartInfiniteBattery 的补丁在开关关闭时会返回 0。
            // 我们这里遵循补丁逻辑即可，如果它说没电（因为关了），那我们就不抽。
            return battery.StoredEnergy;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            yield return new Command_Toggle
            {
                defaultLabel = "OmniCrafter_AutoResearch_Pause".Translate(),
                defaultDesc = "OmniCrafter_AutoResearch_PauseDesc".Translate(),
                icon = AutoResearchTex.IconModifyDialog,
                isActive = () => !active,
                toggleAction = () => active = !active
            };
        }

        public override string CompInspectStringExtra()
        {
            if (!active)
            {
                return "OmniCrafter_AutoResearch_Paused".Translate();
            }
            ResearchProjectDef currentProj = Find.ResearchManager.GetProject();
            if (currentProj != null)
            {
                return "OmniCrafter_AutoResearching".Translate(currentProj.LabelCap);
            }
            return base.CompInspectStringExtra();
        }
    }
    
    [StaticConstructorOnStartup]
    public static class AutoResearchTex
    {
        public static readonly Texture2D IconModifyDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/AutoResearch_Pause", true) ??
            BaseContent.WhiteTex;
    }
}