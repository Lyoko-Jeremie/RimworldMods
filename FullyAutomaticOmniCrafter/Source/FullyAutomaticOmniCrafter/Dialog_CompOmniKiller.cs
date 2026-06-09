using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse.AI.Group;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    /// 提供一个控制界面，可以按照筛选条件列出地图上的所有pawn，并添加到处死列表中，来杀死指定的pawn。
    /// 这个控制界面由左中左中右右四个栏组成，左栏是筛选条件，中左栏是筛选出的pawn列表，中右栏是处死列表，右栏是操作栏。
    /// 双击可以将选中的pawn添加到处死列表中，或从处死列表中移除。
    /// 筛选功能参见 OmniPhantomWall2_PassabilitySettings、OmniAutoSurgeonSurgery 的筛选条件， ~需要附加拼音搜索~
    /// 操作栏包括如下的几个功能按钮：
    /// * 施加 +Infinity 点 damage
    /// * 摧毁所有身体部位
    /// * 剥夺身上的所有可剥夺的任何物品，包括穿戴、武器、防具、药剂、食物、资源等。放置在对象周边的地上。
    /// * 摘取所有可以摘取的器官和身体部件，放置在对象周边的地上。
    /// * 直接对对象使用 kill 指令
    /// * 将所有负面hediff堆叠到对象身上，并且将所有正面hediff移除
    public class Dialog_CompOmniKiller : Window
    {
        private readonly CompOmniKiller comp;
        private Vector2 scrollPosCandidates;
        private Vector2 scrollPosExecution;
        
        private List<Pawn> candidates = new List<Pawn>();
        private List<Pawn> executionList = new List<Pawn>();
        
        // 筛选条件使用 OmniPhantomWall2_PassabilitySettings 的逻辑
        private OmniPhantomWall2_PassabilitySettings settings;

        public override Vector2 InitialSize => new Vector2(1000f, 750f);

        public Dialog_CompOmniKiller(CompOmniKiller comp)
        {
            this.comp = comp;
            this.settings = comp.filterSettings;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            this.UpdateCandidates();
        }

        private void UpdateCandidates()
        {
            if (comp?.parent?.Map == null) return;

            candidates = comp.parent.Map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && !executionList.Contains(p))
                .Where(p => CanPawnPass(p, settings))
                .OrderBy(p => p.LabelCap)
                .ToList();
        }

        private bool CanPawnPass(Pawn pawn, OmniPhantomWall2_PassabilitySettings s)
        {
            if (pawn == null) return false;
            
            // 白名单逻辑：满足任何一个启用的条件即返回 true
            
            // 敌对单位
            if (s.allowHostiles && pawn.HostileTo(Faction.OfPlayer))
                return true;
            
            // 玩家的囚犯
            if (s.allowColonyPrisoners && pawn.IsPrisonerOfColony)
                return true;
            
            // 任意囚犯 （包括其他派系的囚犯）
            if (s.allowPrisoners && pawn.IsPrisoner)
                return true;
            
            // 玩家单位
            if (pawn.Faction == Faction.OfPlayer)
            {
                if (s.allowColonists && pawn.RaceProps.Humanlike)
                    return true;
                
                if (s.allowEntities && pawn.RaceProps.IsAnomalyEntity)
                    return true;

                if (s.allowMechanoids && pawn.RaceProps.IsMechanoid)
                    return true;
                
                if (s.allowDryad && pawn.RaceProps.Dryad)
                    return true;

                if (s.allowInsectoids && pawn.RaceProps.Insect)
                    return true;
                
                if (pawn.RaceProps.Animal)
                {
                    if (s.allowRoamers && pawn.Roamer)
                        return true;
                    
                    if (s.allowTrainableAnimals && pawn.RaceProps.trainability != null && pawn.RaceProps.trainability != TrainabilityDefOf.None)
                        return true;

                    if (s.allowPets)
                        return true;
                }
            }
            
            // 商人 (Trader)
            if (s.allowTraders &&
                !pawn.HostileTo(Faction.OfPlayer) &&
                pawn.Faction != null && pawn.Faction != Faction.OfPlayer &&
                pawn.GetLord() != null)
                return true;
            
            if (s.allowEntities && pawn.RaceProps.IsAnomalyEntity)
                return true;

            if (s.allowMechanoids && pawn.RaceProps.IsMechanoid)
                return true;
            
            if (s.allowDryad && pawn.RaceProps.Dryad)
                return true;

            if (s.allowInsectoids && pawn.RaceProps.Insect)
                return true;
            
            if (s.allowWildAnimals && pawn.RaceProps.Animal && pawn.Faction == null)
                return true;

            if (s.allowHumanlikes && pawn.RaceProps.Humanlike)
                return true;

            if (s.allowToolUsers && pawn.RaceProps.ToolUser)
                return true;

            if (s.allowFactioned && pawn.Faction != null)
                return true;

            if (s.allowLords && pawn.GetLord() != null)
                return true;

            if (s.allowUnfactions && pawn.Faction == null && pawn.GetLord() == null)
                return true;

            return false;
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

            void DrawCheckbox(string labelKey, ref bool value)
            {
                bool val = value;
                listing.CheckboxLabeled(labelKey.Translate(), ref val);
                if (val != value)
                {
                    value = val;
                    UpdateCandidates();
                }
            }

            DrawCheckbox("OPW_AllowColonists", ref settings.allowColonists);
            DrawCheckbox("OPW_AllowPets", ref settings.allowPets);
            DrawCheckbox("OPW_AllowRoamers", ref settings.allowRoamers);
            DrawCheckbox("OPW_AllowTrainableAnimals", ref settings.allowTrainableAnimals);
            DrawCheckbox("OPW_AllowDryad", ref settings.allowDryad);
            DrawCheckbox("OPW_AllowTraders", ref settings.allowTraders);
            DrawCheckbox("OPW_AllowPrisoners", ref settings.allowPrisoners);
            DrawCheckbox("OPW_AllowColonyPrisoners", ref settings.allowColonyPrisoners);
            DrawCheckbox("OPW_AllowWildAnimals", ref settings.allowWildAnimals);
            DrawCheckbox("OPW_AllowEntities", ref settings.allowEntities);
            DrawCheckbox("OPW_AllowHostiles", ref settings.allowHostiles);
            DrawCheckbox("OPW_AllowMechanoids", ref settings.allowMechanoids);
            DrawCheckbox("OPW_AllowInsectoids", ref settings.allowInsectoids);
            DrawCheckbox("OPW_AllowFactioned", ref settings.allowFactioned);
            DrawCheckbox("OPW_AllowLords", ref settings.allowLords);
            DrawCheckbox("OPW_AllowHumanlikes", ref settings.allowHumanlikes);
            DrawCheckbox("OPW_AllowToolUsers", ref settings.allowToolUsers);
            DrawCheckbox("OPW_AllowUnfactions", ref settings.allowUnfactions);

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
                Rect rowRect = new Rect(0, curY, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                
                Rect iconRect = new Rect(2, curY + 2, 26, 26);
                Widgets.ThingIcon(iconRect, p);
                
                Widgets.Label(new Rect(32, curY, viewRect.width - 32, 30f), p.LabelCap);
                
                if (Widgets.ButtonInvisible(rowRect))
                {
                    executionList.Add(p);
                    UpdateCandidates();
                    SoundDefOf.Click.PlayOneShotOnCamera();
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
                Rect rowRect = new Rect(0, curY, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Rect iconRect = new Rect(2, curY + 2, 26, 26);
                Widgets.ThingIcon(iconRect, p);

                Widgets.Label(new Rect(32, curY, viewRect.width - 32, 30f), p.LabelCap);

                if (Widgets.ButtonInvisible(rowRect))
                {
                    executionList.Remove(p);
                    UpdateCandidates();
                    SoundDefOf.Click.PlayOneShotOnCamera();
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

            if (listing.ButtonText("OmniKiller_DestroyAllParts".Translate()))
            {
                ApplyToAll(p =>
                {
                    var parts = p.health.hediffSet.GetNotMissingParts().ToList();
                    foreach (var part in parts)
                    {
                        p.health.AddHediff(HediffDefOf.MissingBodyPart, part);
                    }
                });
            }
            
            if (listing.ButtonText("OmniKiller_StripEverything".Translate()))
            {
                ApplyToAll(p => {
                    Map map = p.Map;
                    IntVec3 pos = p.Position;
                    if (map == null) return;
                    p.inventory.DropAllNearPawn(pos);
                    p.equipment.DropAllEquipment(pos);
                    p.apparel.DropAll(pos);
                });
            }

            if (listing.ButtonText("OmniKiller_HarvestEverything".Translate()))
            {
                ApplyToAll(p => {
                    Map map = p.Map;
                    IntVec3 pos = p.Position;
                    if (map == null) return;
                    var parts = p.RaceProps.body.AllParts.ToList();
                    foreach (var part in parts)
                    {
                        if (p.health.hediffSet.PartIsMissing(part)) continue;
                        if (part.def.spawnThingOnRemoved != null)
                        {
                            p.health.RestorePart(part); // 确保它是完整的
                            GenSpawn.Spawn(part.def.spawnThingOnRemoved, pos, map);
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
                    
                    // 遍历数据库中所有的负面效果并全部添加
                    foreach (var hediffDef in DefDatabase<HediffDef>.AllDefs)
                    {
                        if (hediffDef.isBad)
                        {
                            try
                            {
                                p.health.AddHediff(hediffDef, null, null);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Warning adding negative hediff {hediffDef.defName} to {p.LabelShort}: {ex.Message}");
                            }
                        }
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
