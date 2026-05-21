using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_AutomatedCapturerSettings : Window
    {
        private readonly CompAutomatedCapturer comp;
        private Vector2 scrollPosLeft;
        private Vector2 scrollPosMid;
        private Vector2 scrollPosRight;
        private List<Pawn> cachedMatchingPawns;
        private int lastUpdateTick;

        public override Vector2 InitialSize => new Vector2(900f, 700f);

        public Dialog_AutomatedCapturerSettings(CompAutomatedCapturer comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        private void UpdatePawnCache()
        {
            if (comp.parent.Map == null) return;
            cachedMatchingPawns = comp.parent.Map.mapPawns.AllPawnsSpawned
                .Where(p => comp.IsValidTarget(p))
                .OrderBy(p => p.Position.DistanceToSquared(comp.parent.Position))
                .ToList();
            lastUpdateTick = Find.TickManager.TicksGame;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (cachedMatchingPawns == null || Find.TickManager.TicksGame > lastUpdateTick + 60)
            {
                UpdatePawnCache();
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "AutomatedCapturer_Settings".Translate());
            Text.Font = GameFont.Small;

            float y = 45f;
            float panelHeight = inRect.height - y - 55f;
            float columnWidth = (inRect.width - 20f) / 3f;

            // --- 左栏：条件选择 ---
            Rect leftRect = new Rect(0f, y, columnWidth, panelHeight);
            Widgets.DrawMenuSection(leftRect);
            DrawLeftPanel(leftRect.ContractedBy(10f));

            // --- 中栏：预览 ---
            Rect midRect = new Rect(columnWidth + 10f, y, columnWidth, panelHeight);
            Widgets.DrawMenuSection(midRect);
            DrawMiddlePanel(midRect.ContractedBy(10f));

            // --- 右栏：附加效果 ---
            Rect rightRect = new Rect((columnWidth + 10f) * 2f, y, columnWidth, panelHeight);
            Widgets.DrawMenuSection(rightRect);
            DrawRightPanel(rightRect.ContractedBy(10f));
        }

        private void DrawLeftPanel(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            
            Text.Font = GameFont.Tiny;
            listing.Label("AutomatedCapturer_FilterConditions".Translate());
            Text.Font = GameFont.Small;
            listing.GapLine();

            float viewHeight = 16 * 28f + 50f;
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, viewHeight);
            Rect outRect = new Rect(0f, listing.CurHeight, rect.width, rect.height - listing.CurHeight);
            
            Widgets.BeginScrollView(outRect, ref scrollPosLeft, viewRect);
            Listing_Standard scrollListing = new Listing_Standard();
            scrollListing.Begin(viewRect);

            var settings = comp.settings;
            scrollListing.CheckboxLabeled("OPW_AllowColonists".Translate(), ref settings.allowColonists);
            scrollListing.CheckboxLabeled("OPW_AllowPets".Translate(), ref settings.allowPets);
            scrollListing.CheckboxLabeled("OPW_AllowDryad".Translate(), ref settings.allowDryad);
            scrollListing.CheckboxLabeled("OPW_AllowTraders".Translate(), ref settings.allowTraders);
            scrollListing.CheckboxLabeled("OPW_AllowPrisoners".Translate(), ref settings.allowPrisoners);
            scrollListing.CheckboxLabeled("OPW_AllowColonyPrisoners".Translate(), ref settings.allowColonyPrisoners);
            scrollListing.CheckboxLabeled("OPW_AllowWildAnimals".Translate(), ref settings.allowWildAnimals);
            scrollListing.CheckboxLabeled("OPW_AllowEntities".Translate(), ref settings.allowEntities);
            scrollListing.CheckboxLabeled("OPW_AllowHostiles".Translate(), ref settings.allowHostiles);
            scrollListing.CheckboxLabeled("OPW_AllowMechanoids".Translate(), ref settings.allowMechanoids);
            scrollListing.CheckboxLabeled("OPW_AllowInsectoids".Translate(), ref settings.allowInsectoids);
            scrollListing.CheckboxLabeled("OPW_AllowFactioned".Translate(), ref settings.allowFactioned);
            scrollListing.CheckboxLabeled("OPW_AllowLords".Translate(), ref settings.allowLords);
            scrollListing.CheckboxLabeled("OPW_AllowHumanlikes".Translate(), ref settings.allowHumanlikes);
            scrollListing.CheckboxLabeled("OPW_AllowToolUsers".Translate(), ref settings.allowToolUsers);
            scrollListing.CheckboxLabeled("OPW_AllowUnfactions".Translate(), ref settings.allowUnfactions);

            scrollListing.End();
            Widgets.EndScrollView();

            listing.End();
        }

        private void DrawMiddlePanel(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 25f), "AutomatedCapturer_MatchingPawns".Translate(cachedMatchingPawns.Count));
            Text.Font = GameFont.Small;
            
            Rect listRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, cachedMatchingPawns.Count * 35f);

            Widgets.BeginScrollView(listRect, ref scrollPosMid, viewRect);
            float curY = 0f;
            foreach (var pawn in cachedMatchingPawns)
            {
                Rect rowRect = new Rect(0f, curY, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Rect iconRect = new Rect(0f, curY, 30f, 30f);
                Widgets.ThingIcon(iconRect, pawn);

                Rect labelRect = new Rect(35f, curY, viewRect.width - 35f, 30f);
                Text.Anchor = TextAnchor.MiddleLeft;
                string label = pawn.LabelCap;
                if (pawn.Faction != null) label += $" ({pawn.Faction.Name})";
                Widgets.Label(labelRect, label.Truncate(labelRect.width));
                Text.Anchor = TextAnchor.UpperLeft;

                curY += 35f;
            }
            Widgets.EndScrollView();
        }

        private void DrawRightPanel(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            
            Text.Font = GameFont.Tiny;
            listing.Label("AutomatedCapturer_CaptureEffects".Translate());
            Text.Font = GameFont.Small;
            listing.GapLine();

            if (listing.RadioButton("AutomatedCapturer_EffectTeleportOnly".Translate(), comp.captureEffect == CaptureEffect.TeleportOnly))
                comp.captureEffect = CaptureEffect.TeleportOnly;
            
            if (listing.RadioButton("AutomatedCapturer_EffectStun".Translate(), comp.captureEffect == CaptureEffect.Stun))
                comp.captureEffect = CaptureEffect.Stun;

            if (listing.RadioButton("AutomatedCapturer_EffectTameOrRecruit".Translate(), comp.captureEffect == CaptureEffect.TameOrRecruit))
                comp.captureEffect = CaptureEffect.TameOrRecruit;

            if (listing.RadioButton("AutomatedCapturer_EffectHostileToPrisoner".Translate(), comp.captureEffect == CaptureEffect.HostileToPrisoner))
                comp.captureEffect = CaptureEffect.HostileToPrisoner;

            listing.Gap(20f);
            listing.CheckboxLabeled("AutomatedCapturer_VisualEffects".Translate(), ref comp.showVisualEffects);
            
            listing.End();
        }
    }
}
