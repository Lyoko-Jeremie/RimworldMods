using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_CompOmniKiller : Window
    {
        private readonly CompOmniKiller comp;
        private string searchText = "";
        private Vector2 scrollPosCandidates;
        private Vector2 scrollPosExecution;
        
        private List<Pawn> candidates = new List<Pawn>();
        private List<Pawn> executionList = new List<Pawn>();
        
        // 筛选条件
        private bool filterColonists = true;
        private bool filterPrisoners = true;
        private bool filterAllies = true;
        private bool filterEnemies = true;
        private bool filterNeutral = true;
        private bool filterAnimals = false;
        private bool filterMechanoids = true;

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public Dialog_CompOmniKiller(CompOmniKiller comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            this.UpdateCandidates();
        }

        private void UpdateCandidates()
        {
            if (comp?.parent?.Map == null) return;

            if (OmniCrafterMod.Settings.enablePinyinSearch)
            {
                PinyinSearchEngine.EnsureIndexed(comp.parent.Map.mapPawns.AllPawnsSpawned.Cast<Def>().ToList(), PinyinSource.SurgeryPawnKind);
                PinyinSearchEngine.EnsureIndexed(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null).Cast<Def>().ToList(), PinyinSource.SurgeryPawnRace);
            }

            candidates = comp.parent.Map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && !executionList.Contains(p))
                .Where(p =>
                {
                    if (filterColonists && p.IsColonist) return true;
                    if (filterPrisoners && p.IsPrisonerOfColony) return true;
                    if (filterAllies && p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer) && !p.IsColonist && !p.IsPrisonerOfColony) return true;
                    if (filterEnemies && p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)) return true;
                    if (filterNeutral && p.Faction == null && !p.RaceProps.Animal) return true;
                    if (filterAnimals && p.RaceProps.Animal) return true;
                    if (filterMechanoids && p.RaceProps.IsMechanoid) return true;
                    return false;
                })
                .Where(p =>
                {
                    if (searchText.NullOrEmpty()) return true;
                    string lower = searchText.ToLower();
                    if (p.LabelCap.ToLower().Contains(lower)) return true;
                    if (PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(p.def, lower, PinyinSource.SurgeryPawnRace)) return true;
                    if (p.kindDef != null && PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(p.kindDef, lower, PinyinSource.SurgeryPawnKind)) return true;
                    return false;
                })
                .OrderBy(p => p.LabelCap)
                .ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            float margin = 10f;
            float columnWidth = (inRect.width - margin * 3) / 4f;

            // 1. 左栏：筛选条件
            Rect rectLeft = new Rect(0, 0, columnWidth, inRect.height - 50f);
            DrawFilterColumn(rectLeft);

            // 2. 中左栏：候选列表
            Rect rectMidLeft = new Rect(columnWidth + margin, 0, columnWidth, inRect.height - 50f);
            DrawCandidateColumn(rectMidLeft);

            // 3. 中右栏：处死列表
            Rect rectMidRight = new Rect((columnWidth + margin) * 2, 0, columnWidth, inRect.height - 50f);
            DrawExecutionColumn(rectMidRight);

            // 4. 右栏：操作栏
            Rect rectRight = new Rect((columnWidth + margin) * 3, 0, columnWidth, inRect.height - 50f);
            DrawActionColumn(rectRight);
        }

        private void DrawFilterColumn(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            Text.Font = GameFont.Medium;
            listing.Label("OmniKiller_Filters".Translate());
            Text.Font = GameFont.Small;
            listing.Gap();

            listing.Label("OmniKiller_Search".Translate());
            string newSearch = Widgets.TextField(listing.GetRect(30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                UpdateCandidates();
            }
            listing.Gap();

            bool changed = false;
            listing.CheckboxLabeled("OmniKiller_FilterColonists".Translate(), ref filterColonists);
            listing.CheckboxLabeled("OmniKiller_FilterPrisoners".Translate(), ref filterPrisoners);
            listing.CheckboxLabeled("OmniKiller_FilterAllies".Translate(), ref filterAllies);
            listing.CheckboxLabeled("OmniKiller_FilterEnemies".Translate(), ref filterEnemies);
            listing.CheckboxLabeled("OmniKiller_FilterNeutral".Translate(), ref filterNeutral);
            listing.CheckboxLabeled("OmniKiller_FilterAnimals".Translate(), ref filterAnimals);
            listing.CheckboxLabeled("OmniKiller_FilterMechanoids".Translate(), ref filterMechanoids);

            if (listing.ButtonText("OmniKiller_Refresh".Translate())) changed = true;
            
            if (changed) UpdateCandidates();

            listing.End();
        }

        private void DrawCandidateColumn(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 30f), "OmniKiller_Candidates".Translate());
            Text.Font = GameFont.Small;
            
            Rect listRect = new Rect(rect.x, rect.y + 35f, rect.width, rect.height - 35f);
            Widgets.DrawMenuSection(listRect);
            
            Rect viewRect = new Rect(0, 0, listRect.width - 16f, candidates.Count * 30f);
            Widgets.BeginScrollView(listRect, ref scrollPosCandidates, viewRect);
            
            float curY = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                Pawn p = candidates[i];
                Rect rowRect = new Rect(0, curY, viewRect.width, 28f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                
                Widgets.Label(new Rect(5, curY, viewRect.width - 10, 28f), p.LabelCap);
                
                if (Widgets.ButtonInvisible(rowRect))
                {
                    if (Event.current.clickCount == 2)
                    {
                        executionList.Add(p);
                        UpdateCandidates();
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                }
                curY += 30f;
            }
            Widgets.EndScrollView();
        }

        private void DrawExecutionColumn(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 30f), "OmniKiller_ExecutionList".Translate());
            Text.Font = GameFont.Small;

            Rect listRect = new Rect(rect.x, rect.y + 35f, rect.width, rect.height - 35f);
            Widgets.DrawMenuSection(listRect);

            Rect viewRect = new Rect(0, 0, listRect.width - 16f, executionList.Count * 30f);
            Widgets.BeginScrollView(listRect, ref scrollPosExecution, viewRect);

            float curY = 0;
            for (int i = 0; i < executionList.Count; i++)
            {
                Pawn p = executionList[i];
                Rect rowRect = new Rect(0, curY, viewRect.width, 28f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Widgets.Label(new Rect(5, curY, viewRect.width - 10, 28f), p.LabelCap);

                if (Widgets.ButtonInvisible(rowRect))
                {
                    if (Event.current.clickCount == 2)
                    {
                        executionList.Remove(p);
                        UpdateCandidates();
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                }
                curY += 30f;
            }
            Widgets.EndScrollView();
        }

        private void DrawActionColumn(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            Text.Font = GameFont.Medium;
            listing.Label("OmniKiller_Actions".Translate());
            Text.Font = GameFont.Small;
            listing.Gap();

            if (listing.ButtonText("OmniKiller_ApplyInfiniteDamage".Translate()))
            {
                ApplyToAll(p => p.TakeDamage(new DamageInfo(DamageDefOf.Bomb, 999999f)));
            }
            
            if (listing.ButtonText("OmniKiller_StripEverything".Translate()))
            {
                ApplyToAll(p => {
                    p.inventory.DropAllNearPawn(p.Position);
                    p.equipment.DropAllEquipment(p.Position);
                    p.apparel.DropAll(p.Position);
                });
            }

            if (listing.ButtonText("OmniKiller_HarvestEverything".Translate()))
            {
                ApplyToAll(p => {
                    var parts = p.RaceProps.body.AllParts.ToList();
                    foreach (var part in parts)
                    {
                        if (p.health.hediffSet.PartIsMissing(part)) continue;
                        if (part.def.spawnThingOnRemoved != null)
                        {
                            p.health.RestorePart(part); // 确保它是完整的
                            GenSpawn.Spawn(part.def.spawnThingOnRemoved, p.Position, p.Map);
                            p.health.AddHediff(HediffDefOf.MissingBodyPart, part);
                        }
                    }
                });
            }

            if (listing.ButtonText("OmniKiller_KillCommand".Translate()))
            {
                ApplyToAll(p => p.Kill(null));
            }

            if (listing.ButtonText("OmniKiller_StackNegativeHediffs".Translate()))
            {
                ApplyToAll(p => {
                    // 移除正面效果
                    var toRemove = p.health.hediffSet.hediffs.Where(h => h.def.isBad == false).ToList();
                    foreach (var h in toRemove) p.health.RemoveHediff(h);
                    
                    // 添加负面效果
                    p.health.AddHediff(HediffDefOf.BloodLoss, null, null);
                    p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss).Severity = 0.9f;

                    if (HediffDefOf.FoodPoisoning != null)
                    {
                        p.health.AddHediff(HediffDefOf.FoodPoisoning, null, null);
                        p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning).Severity = 1f;
                    }
                    if (HediffDefOf.Hypothermia != null)
                    {
                        p.health.AddHediff(HediffDefOf.Hypothermia, null, null);
                        p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia).Severity = 0.8f;
                    }
                    // 尝试添加毒性
                    HediffDef toxic = DefDatabase<HediffDef>.GetNamedSilentFail("ToxicBuildup");
                    if (toxic != null)
                    {
                        p.health.AddHediff(toxic, null, null);
                        p.health.hediffSet.GetFirstHediffOfDef(toxic).Severity = 0.8f;
                    }
                });
            }

            listing.Gap(20f);
            if (listing.ButtonText("OmniKiller_ClearExecutionList".Translate()))
            {
                executionList.Clear();
                UpdateCandidates();
            }

            listing.End();
        }

        private void ApplyToAll(Action<Pawn> action)
        {
            foreach (var p in executionList.ToList())
            {
                if (p.Spawned && !p.Dead)
                {
                    action(p);
                }
            }
            executionList.RemoveAll(p => p.Dead || !p.Spawned);
            UpdateCandidates();
        }
    }
}
