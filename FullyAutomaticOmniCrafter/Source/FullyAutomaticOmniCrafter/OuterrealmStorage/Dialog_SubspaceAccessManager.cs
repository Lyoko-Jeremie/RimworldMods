using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力授权界面（§v3）：双栏。
    /// 左栏：当前我方派系存活 pawn（殖民者/动物/机械体），可 [授权]（添加 FAOC_SubspaceAccess Hediff）。
    /// 右栏：当前所有已授权 pawn（不分派系、含死亡/曾我方后叛变者），可 [取消授权]（移除 Hediff）。
    /// 授权状态随 pawn hediffSet 存档，天然跨地图 / 随远行队。
    /// </summary>
    public class Dialog_SubspaceAccessManager : Window
    {
        private const float RowHeight = 40f;

        private Vector2 leftScroll;
        private Vector2 rightScroll;
        private string searchText = "";
        private bool dirty = true;

        private readonly List<Pawn> candidates = new List<Pawn>();
        private readonly List<Pawn> authorized = new List<Pawn>();

        public override Vector2 InitialSize => new Vector2(920f, 640f);

        public Dialog_SubspaceAccessManager()
        {
            doCloseButton = true;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            forcePause = true;
            Rebuild();
        }

        private void Rebuild()
        {
            candidates.Clear();
            authorized.Clear();
            List<Pawn> alive = PawnsFinder.AllMapsAndWorld_Alive;
            // 左栏：当前我方派系存活 pawn
            for (int i = 0; i < alive.Count; i++)
            {
                Pawn p = alive[i];
                if (p == null || p.Discarded || p.Faction != Faction.OfPlayer || !MatchesSearch(p))
                {
                    continue;
                }
                candidates.Add(p);
            }
            // 右栏：所有已授权 pawn（不分派系/死活）
            for (int i = 0; i < alive.Count; i++)
            {
                Pawn p = alive[i];
                if (p != null && !p.Discarded && SubspaceAccessUtility.IsAuthorized(p))
                {
                    authorized.Add(p);
                }
            }
            foreach (Pawn p in Find.WorldPawns.AllPawnsDead)
            {
                if (p != null && !p.Discarded && SubspaceAccessUtility.IsAuthorized(p))
                {
                    authorized.Add(p);
                }
            }
            dirty = false;
        }

        private bool MatchesSearch(Pawn p)
        {
            if (searchText.NullOrEmpty())
            {
                return true;
            }
            string lower = searchText.ToLower();
            return p.LabelCap.ToLower().Contains(lower) || p.def.defName.ToLower().Contains(lower);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (dirty)
            {
                Rebuild();
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "SubspaceAccessManagerTitle".Translate());
            Text.Font = GameFont.Small;

            float y = 40f;
            Widgets.Label(new Rect(0f, y, 60f, 30f), "SubspaceAccess_Search".Translate());
            string ns = Widgets.TextField(new Rect(65f, y, inRect.width - 70f, 30f), searchText);
            if (ns != searchText)
            {
                searchText = ns;
                dirty = true;
            }
            y += 40f;

            float gap = 12f;
            float leftW = (inRect.width - gap) * 0.55f;
            float rightW = inRect.width - gap - leftW;
            float listH = inRect.height - y - 8f;
            DrawColumn(new Rect(0f, y, leftW, listH), true, ref leftScroll);
            DrawColumn(new Rect(leftW + gap, y, rightW, listH), false, ref rightScroll);
        }

        private void DrawColumn(Rect rect, bool isLeft, ref Vector2 scroll)
        {
            List<Pawn> list = isLeft ? candidates : authorized;
            string title = (isLeft ? "SubspaceAccess_LeftTitle" : "SubspaceAccess_RightTitle").Translate();
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), title);
            Rect outRect = new Rect(rect.x, rect.y + 26f, rect.width, rect.height - 26f);
            if (list.Count == 0)
            {
                Widgets.Label(outRect, "SubspaceAccess_Empty".Translate());
                return;
            }
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, list.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float curY = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                DrawRow(new Rect(0f, curY, viewRect.width, RowHeight), list[i], isLeft);
                curY += RowHeight;
            }
            Widgets.EndScrollView();
        }

        private void DrawRow(Rect rowRect, Pawn p, bool isLeft)
        {
            bool authorizedNow = SubspaceAccessUtility.IsAuthorized(p);
            if (authorizedNow && isLeft)
            {
                Widgets.DrawRectFast(rowRect, new Color(0.25f, 0.55f, 0.25f, 0.35f));
            }
            else if (Mouse.IsOver(rowRect))
            {
                Widgets.DrawHighlight(rowRect);
            }

            Rect iconRect = new Rect(rowRect.x + 4f, rowRect.y + 5f, 30f, 30f);
            Widgets.DrawRectFast(iconRect, new Color(0.16f, 0.16f, 0.16f, 0.55f));
            Widgets.DrawBox(iconRect, 1);

            string nameLine = p.LabelCap;
            if (p.Faction != null)
            {
                nameLine += " (" + p.Faction.Name + ")";
            }
            Widgets.Label(new Rect(rowRect.x + 40f, rowRect.y + 4f, rowRect.width - 160f, 22f), nameLine);

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rowRect.x + 40f, rowRect.y + 23f, rowRect.width - 160f, 16f), p.def.label);
            Text.Font = GameFont.Small;

            Rect btnRect = new Rect(rowRect.xMax - 110f, rowRect.y + 5f, 100f, 30f);
            if (isLeft)
            {
                if (authorizedNow)
                {
                    Widgets.ButtonText(btnRect, "SubspaceAccess_Authorized".Translate(), active: false);
                }
                else if (Widgets.ButtonText(btnRect, "SubspaceAccess_Authorize".Translate()))
                {
                    Authorize(p);
                    dirty = true;
                }
            }
            else if (Widgets.ButtonText(btnRect, "SubspaceAccess_Deauthorize".Translate()))
            {
                Deauthorize(p);
                dirty = true;
            }
        }

        private static void Authorize(Pawn p)
        {
            HediffDef def = SubspaceAccessUtility.AccessHediffDef;
            if (def != null && p?.health != null)
            {
                p.health.AddHediff(def);
            }
        }

        private static void Deauthorize(Pawn p)
        {
            Hediff_SubspaceAccess hediff = SubspaceAccessUtility.GetAccessHediff(p);
            if (hediff != null && p?.health != null)
            {
                p.health.RemoveHediff(hediff);
            }
        }
    }
}
