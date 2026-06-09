using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_CompBiosphere : Window
    {
        private CompBiosphere comp;
        private Vector2 scrollPosition = Vector2.zero;

        public Dialog_CompBiosphere(CompBiosphere comp)
        {
            this.comp = comp;
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(500f, 600f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("Biosphere Control".Translate());
            Text.Font = GameFont.Small;
            listing.Gap();

            // 1. 活动区选择
            Rect areaRect = listing.GetRect(30f);
            Widgets.Label(areaRect.LeftPart(0.4f), "Selected Area:".Translate());
            if (Widgets.ButtonText(areaRect.RightPart(0.6f), comp.areaName ?? "None".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("None".Translate(), () => {
                    comp.areaName = null;
                    comp.RefreshArea();
                }));
                foreach (Area area in comp.parent.Map.areaManager.AllAreas)
                {
                    options.Add(new FloatMenuOption(area.Label, () => {
                        comp.areaName = area.Label;
                        comp.RefreshArea();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap();

            // 2. 植物生长控制
            listing.Label("Plant Growth Mode:".Translate());
            if (listing.RadioButton("Growth_Normal".Translate(), comp.growthMode == PlantGrowthMode.Normal))
                comp.growthMode = PlantGrowthMode.Normal;
            if (listing.RadioButton("Growth_Forced".Translate(), comp.growthMode == PlantGrowthMode.Forced))
                comp.growthMode = PlantGrowthMode.Forced;
            if (listing.RadioButton("Growth_Stopped".Translate(), comp.growthMode == PlantGrowthMode.Stopped))
                comp.growthMode = PlantGrowthMode.Stopped;
            if (listing.RadioButton("Growth_Cleared".Translate(), comp.growthMode == PlantGrowthMode.Cleared))
                comp.growthMode = PlantGrowthMode.Cleared;
            listing.Gap();

            // 3. 温度控制
            listing.CheckboxLabeled("Control Temperature".Translate(), ref comp.controlTemperature);
            if (comp.controlTemperature)
            {
                listing.Label("Target Temperature: ".Translate() + comp.targetTemperature.ToStringTemperature());
                comp.targetTemperature = listing.Slider(comp.targetTemperature, -50f, 50f);
            }
            listing.Gap();

            // 4. 环境强制控制
            listing.CheckboxLabeled("Ensure No Vacuum".Translate(), ref comp.ensureNoVacuum);
            listing.CheckboxLabeled("Ensure Light".Translate(), ref comp.ensureLight);
            listing.CheckboxLabeled("Ensure Sunlight".Translate(), ref comp.ensureSunlight);

            listing.End();
        }
    }
}
