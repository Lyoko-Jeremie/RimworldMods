using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_CompBiosphere_Temperature : Window
    {
        private CompBiosphere comp;
        private string tempBuffer;

        public Dialog_CompBiosphere_Temperature(CompBiosphere comp)
        {
            this.comp = comp;
            this.tempBuffer = comp.targetTemperature.ToString("F1");
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            
            listing.Label("Biosphere_TargetTemperature".Translate() + ": " + comp.targetTemperature.ToStringTemperature());
            
            float temp = comp.targetTemperature;
            listing.TextFieldNumeric(ref temp, ref tempBuffer, -100f, 100f);
            comp.targetTemperature = temp;
            
            if (listing.ButtonText("OK".Translate()))
            {
                this.Close();
            }
            
            listing.End();
        }
    }
}
