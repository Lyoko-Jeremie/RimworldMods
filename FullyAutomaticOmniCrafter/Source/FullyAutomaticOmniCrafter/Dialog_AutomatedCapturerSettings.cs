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
        private List<Pawn> cachedMatchingPawns;
        private int lastUpdateTick;
        private bool? mouseDragState;

        public override Vector2 InitialSize => new Vector2(900f, 700f);

        public Dialog_AutomatedCapturerSettings(CompAutomatedCapturer comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        private void UpdatePawnCache()
        {
            if (comp.parent.Map == null) return;
            var query = comp.parent.Map.mapPawns.AllPawnsSpawned
                .Where(p => comp.IsValidTarget(p));

            IOrderedEnumerable<Pawn> ordered;
            switch (comp.sortMode)
            {
                case CapturerSortMode.Distance:
                    ordered = comp.sortDescending 
                        ? query.OrderByDescending(p => p.Position.DistanceToSquared(comp.parent.Position))
                        : query.OrderBy(p => p.Position.DistanceToSquared(comp.parent.Position));
                    break;
                case CapturerSortMode.Name:
                    ordered = comp.sortDescending 
                        ? query.OrderByDescending(p => p.LabelCap)
                        : query.OrderBy(p => p.LabelCap);
                    break;
                case CapturerSortMode.Faction:
                    ordered = comp.sortDescending 
                        ? query.OrderByDescending(p => p.Faction?.Name ?? "")
                        : query.OrderBy(p => p.Faction?.Name ?? "");
                    break;
                case CapturerSortMode.Health:
                    ordered = comp.sortDescending 
                        ? query.OrderByDescending(p => p.health.summaryHealth.SummaryHealthPercent)
                        : query.OrderBy(p => p.health.summaryHealth.SummaryHealthPercent);
                    break;
                case CapturerSortMode.MarketValue:
                    ordered = comp.sortDescending 
                        ? query.OrderByDescending(p => p.MarketValue)
                        : query.OrderBy(p => p.MarketValue);
                    break;
                default:
                    ordered = query.OrderBy(p => p.Position.DistanceToSquared(comp.parent.Position));
                    break;
            }

            cachedMatchingPawns = ordered.ToList();
            lastUpdateTick = Find.TickManager.TicksGame;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Event.current.type == EventType.MouseUp)
            {
                mouseDragState = null;
            }

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

            void CheckboxLabeled(string label, ref bool checkOn)
            {
                Rect rect2 = scrollListing.GetRect(Text.LineHeight);
                if (Mouse.IsOver(rect2))
                {
                    Widgets.DrawHighlight(rect2);
                }

                bool initial = checkOn;
                Widgets.CheckboxLabeled(rect2, label, ref checkOn);
                if (checkOn != initial)
                {
                    mouseDragState = checkOn;
                    UpdatePawnCache();
                }
                else if (mouseDragState.HasValue && Mouse.IsOver(rect2) && checkOn != mouseDragState.Value)
                {
                    checkOn = mouseDragState.Value;
                    UpdatePawnCache();
                }

                scrollListing.Gap(scrollListing.verticalSpacing);
            }

            CheckboxLabeled("OPW_AllowColonists".Translate(), ref settings.allowColonists);
            CheckboxLabeled("OPW_AllowPets".Translate(), ref settings.allowPets);
            CheckboxLabeled("OPW_AllowDryad".Translate(), ref settings.allowDryad);
            CheckboxLabeled("OPW_AllowTraders".Translate(), ref settings.allowTraders);
            CheckboxLabeled("OPW_AllowPrisoners".Translate(), ref settings.allowPrisoners);
            CheckboxLabeled("OPW_AllowColonyPrisoners".Translate(), ref settings.allowColonyPrisoners);
            CheckboxLabeled("OPW_AllowWildAnimals".Translate(), ref settings.allowWildAnimals);
            CheckboxLabeled("OPW_AllowEntities".Translate(), ref settings.allowEntities);
            CheckboxLabeled("OPW_AllowHostiles".Translate(), ref settings.allowHostiles);
            CheckboxLabeled("OPW_AllowMechanoids".Translate(), ref settings.allowMechanoids);
            CheckboxLabeled("OPW_AllowInsectoids".Translate(), ref settings.allowInsectoids);
            CheckboxLabeled("OPW_AllowFactioned".Translate(), ref settings.allowFactioned);
            CheckboxLabeled("OPW_AllowLords".Translate(), ref settings.allowLords);
            CheckboxLabeled("OPW_AllowHumanlikes".Translate(), ref settings.allowHumanlikes);
            CheckboxLabeled("OPW_AllowToolUsers".Translate(), ref settings.allowToolUsers);
            CheckboxLabeled("OPW_AllowUnfactions".Translate(), ref settings.allowUnfactions);

            scrollListing.End();
            Widgets.EndScrollView();

            listing.End();
        }

        private void DrawMiddlePanel(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            float headerHeight = 25f;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, headerHeight), "AutomatedCapturer_MatchingPawns".Translate(cachedMatchingPawns.Count));
            
            // 排序按钮区域
            float sortBtnY = rect.y + headerHeight + 2f;
            float sortBtnHeight = 25f;
            Rect sortRect = new Rect(rect.x, sortBtnY, rect.width, sortBtnHeight);
            
            float btnWidth = rect.width / 2f - 2f;
            if (Widgets.ButtonText(new Rect(sortRect.x, sortRect.y, btnWidth, sortBtnHeight), ("AutomatedCapturer_SortBy_" + comp.sortMode).Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (CapturerSortMode mode in Enum.GetValues(typeof(CapturerSortMode)))
                {
                    options.Add(new FloatMenuOption(("AutomatedCapturer_SortBy_" + mode).Translate(), () =>
                    {
                        comp.sortMode = mode;
                        UpdatePawnCache();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (Widgets.ButtonText(new Rect(sortRect.x + btnWidth + 4f, sortRect.y, btnWidth, sortBtnHeight), 
                comp.sortDescending ? "AutomatedCapturer_SortDescending".Translate() : "AutomatedCapturer_SortAscending".Translate()))
            {
                comp.sortDescending = !comp.sortDescending;
                UpdatePawnCache();
            }

            Text.Font = GameFont.Small;
            
            float listStartOffset = headerHeight + sortBtnHeight + 10f;
            Rect listRect = new Rect(rect.x, rect.y + listStartOffset, rect.width, rect.height - listStartOffset);
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
