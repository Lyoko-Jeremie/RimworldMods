using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_SelectPawn : Window
    {
        private readonly Building_FullyAutoOmniSurgeon surgeon;
        private readonly Action<Pawn> onSelected;
        private string searchText = "";
        private Vector2 scrollPosition;
        private List<Pawn> cachedPawns;
        private bool filterColonists = true;
        private bool filterPrisoners = true;
        private bool filterAllies = true;
        private bool filterEnemies = false;
        private bool filterAnimals = false;

        public override Vector2 InitialSize => new Vector2(600f, 700f);

        public Dialog_SelectPawn(Building_FullyAutoOmniSurgeon surgeon, Action<Pawn> onSelected)
        {
            this.surgeon = surgeon;
            this.onSelected = onSelected;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        private void UpdateCache()
        {
            if (surgeon?.Map == null) return;

            // 确保拼音引擎已为 PawnKindDef 构建索引
            if (OmniCrafterMod.Settings.enablePinyinSearch && !PinyinSearchEngine.IsReady)
            {
                PinyinSearchEngine.BuildIndex(DefDatabase<PawnKindDef>.AllDefsListForReading);
            }

            cachedPawns = surgeon.Map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && (p.RaceProps.IsFlesh || p.RaceProps.IsMechanoid))
                .Where(p =>
                {
                    if (filterAnimals && p.RaceProps.Animal) return true;
                    if (filterColonists && p.IsColonist) return true;
                    if (filterPrisoners && p.IsPrisonerOfColony) return true;
                    if (filterEnemies && p.HostileTo(Faction.OfPlayer)) return true;
                    if (filterAllies && !p.IsColonist && !p.IsPrisonerOfColony && !p.RaceProps.Animal && !p.HostileTo(Faction.OfPlayer)) return true;
                    return false;
                })
                .Where(p =>
                {
                    if (searchText.NullOrEmpty()) return true;
                    string lowerSearch = searchText.ToLower();
                    if (p.LabelCap.ToLower().Contains(lowerSearch)) return true;
                    if (p.def.defName.ToLower().Contains(lowerSearch)) return true;
                    if (p.kindDef != null && p.kindDef.defName.ToLower().Contains(lowerSearch)) return true;
                    if (PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(p.def, lowerSearch)) return true;
                    if (PinyinSearchEngine.IsReady && p.kindDef != null && PinyinSearchEngine.MatchesPinyin(p.kindDef, lowerSearch)) return true;
                    return false;
                })
                .OrderBy(p => p.Position.DistanceToSquared(surgeon.Position))
                .ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "FullyAutoOmniSurgeon_SelectOccupant".Translate());
            Text.Font = GameFont.Small;

            float y = 40f;

            // 搜索框
            Widgets.Label(new Rect(0f, y, 60f, 30f), "FullyAutoOmniSurgeon_Search".Translate());
            string newSearch = Widgets.TextField(new Rect(65f, y, inRect.width - 70f, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                UpdateCache();
            }
            y += 35f;

            // 过滤器
            float filterW = (inRect.width - 10f) / 5f;
            bool changed = false;

            void Checkbox(Rect rect, string label, ref bool flag)
            {
                bool old = flag;
                Widgets.CheckboxLabeled(rect, label.Translate(), ref flag);
                if (flag != old) changed = true;
            }

            Checkbox(new Rect(0f, y, filterW, 30f), "FullyAutoOmniSurgeon_FilterColonists", ref filterColonists);
            Checkbox(new Rect(filterW, y, filterW, 30f), "FullyAutoOmniSurgeon_FilterPrisoners", ref filterPrisoners);
            Checkbox(new Rect(filterW * 2, y, filterW, 30f), "FullyAutoOmniSurgeon_FilterAllies", ref filterAllies);
            Checkbox(new Rect(filterW * 3, y, filterW, 30f), "FullyAutoOmniSurgeon_FilterEnemies", ref filterEnemies);
            Checkbox(new Rect(filterW * 4, y, filterW, 30f), "FullyAutoOmniSurgeon_FilterAnimals", ref filterAnimals);

            y += 35f;
            if (cachedPawns == null || changed) UpdateCache();

            // 列表
            Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y - 50f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, cachedPawns.Count * 40f);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0f;
            foreach (var pawn in cachedPawns)
            {
                Rect rowRect = new Rect(0f, curY, viewRect.width, 35f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                // 绘制头像
                Rect iconRect = new Rect(5f, curY, 30f, 30f);
                Widgets.ThingIcon(iconRect, pawn);

                // 绘制名称和派系
                string label = pawn.LabelCap;
                if (pawn.Faction != null) label += $" ({pawn.Faction.Name})";
                Widgets.Label(new Rect(40f, curY + 5f, viewRect.width - 50f, 30f), label);

                if (Widgets.ButtonInvisible(rowRect))
                {
                    onSelected?.Invoke(pawn);
                    Close();
                }
                curY += 40f;
            }
            Widgets.EndScrollView();
        }
    }
}
