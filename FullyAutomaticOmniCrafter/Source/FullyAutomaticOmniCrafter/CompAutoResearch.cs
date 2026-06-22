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
        private const int FastResearchTickInterval = 50;
        private const int SlowResearchTickInterval = 500;
        private const float MaxEnergyPerTickWd = 10f;

        public CompProperties_AutoResearch Props => (CompProperties_AutoResearch)props;
        private CompPowerTrader powerComp;
        private bool active = false;
        private int researchTickInterval = SlowResearchTickInterval;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = this.parent.GetComp<CompPowerTrader>();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref active, "active", false);
            Scribe_Values.Look(ref researchTickInterval, "researchTickInterval", SlowResearchTickInterval);
            if (researchTickInterval != FastResearchTickInterval && researchTickInterval != SlowResearchTickInterval)
            {
                researchTickInterval = SlowResearchTickInterval;
            }
        }
        
        public override void CompTick()
        {
            base.CompTick();

            if (!active)
            {
                return;
            }

            if (!this.parent.IsHashIntervalTick(researchTickInterval))
            {
                return;
            }

            bool advancedResearch = TryAdvanceResearch();
            researchTickInterval = advancedResearch ? FastResearchTickInterval : SlowResearchTickInterval;
        }

        private bool TryAdvanceResearch()
        {
            if (powerComp != null && !powerComp.PowerOn)
            {
                return false;
            }

            ResearchProjectDef currentProj = Find.ResearchManager.GetProject();
            if (currentProj == null || currentProj.IsFinished)
            {
                return false;
            }

            float pointsNeeded = currentProj.baseCost - Find.ResearchManager.GetProgress(currentProj);
            if (pointsNeeded <= 0) return false;

            float ratio = Props.researchPoints / Mathf.Max(Props.energyCost, 0.0001f);
            float energyNeededWd = pointsNeeded / ratio;
            
            // 根据当前自适应间隔批量结算，但保持原本每 tick 10 Wd 的强度上限。
            float maxEnergyPerBatchWd = MaxEnergyPerTickWd * researchTickInterval;
            float energyToConsumeWd = Mathf.Min(energyNeededWd, maxEnergyPerBatchWd);

            float consumedWd = ConsumeEnergyFromNet(energyToConsumeWd);
            if (consumedWd > 0)
            {
                float pointsToGained = consumedWd * ratio;
                // 使用 AddProgress 绕过 ResearchPerformed 中的 0.00825 倍率，
                // 使得 config 中的 researchPoints 直接对应 UI 上的研究点数。
                Find.ResearchManager.AddProgress(currentProj, pointsToGained, null);
                return true;
            }

            return false;
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

        private float GetAvailableStoredEnergyFromNet(PowerNet net)
        {
            if (net == null) return 0f;

            float available = 0f;
            foreach (CompPowerBattery battery in net.batteryComps)
            {
                available += GetStoredEnergy(battery);
            }
            return available;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            yield return new Command_Toggle
            {
                defaultLabel = "OmniCrafter_AutoResearch_Active".Translate(),
                defaultDesc = "OmniCrafter_AutoResearch_ActiveDesc".Translate(),
                icon = AutoResearchTex.IconModifyDialog,
                isActive = () => active,
                toggleAction = () => active = !active
            };
        }

        public override string CompInspectStringExtra()
        {
            if (!active)
            {
                return "OmniCrafter_AutoResearch_StatusPaused".Translate();
            }

            if (powerComp != null && !powerComp.PowerOn)
            {
                return "OmniCrafter_AutoResearch_StatusNoPower".Translate();
            }

            ResearchProjectDef currentProj = Find.ResearchManager.GetProject();
            if (currentProj == null)
            {
                return "OmniCrafter_AutoResearch_StatusNoProject".Translate();
            }

            if (currentProj.IsFinished)
            {
                return "OmniCrafter_AutoResearch_StatusProjectFinished".Translate(currentProj.LabelCap);
            }

            float pointsNeeded = currentProj.baseCost - Find.ResearchManager.GetProgress(currentProj);
            if (pointsNeeded <= 0)
            {
                return "OmniCrafter_AutoResearch_StatusProjectFinished".Translate(currentProj.LabelCap);
            }

            PowerNet net = this.parent.GetComp<CompPower>()?.PowerNet;
            if (net == null)
            {
                return "OmniCrafter_AutoResearch_StatusNoPowerNet".Translate();
            }

            if (GetAvailableStoredEnergyFromNet(net) <= 0f)
            {
                return "OmniCrafter_AutoResearch_StatusNoStoredEnergy".Translate();
            }

            return "OmniCrafter_AutoResearch_StatusRunning".Translate(currentProj.LabelCap);
        }
    }
    
    [StaticConstructorOnStartup]
    public static class AutoResearchTex
    {
        public static readonly Texture2D IconModifyDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/AutoResearch_Active", true) ??
            BaseContent.WhiteTex;
    }
}
