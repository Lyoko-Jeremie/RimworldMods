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

        // 类型筛选（仅左栏；右栏恒显示全部已授权）
        private bool filterHuman = true;
        private bool filterAnimal = true;
        private bool filterMechanoid = true;

        // 是否显示 pawn 图像（默认关闭以加速界面打开；开启后才加载并显示）
        private bool showPawnIcons;

        private readonly List<Pawn> candidates = new List<Pawn>();
        private readonly List<Pawn> authorized = new List<Pawn>();

        public override Vector2 InitialSize => new Vector2(920f, 720f);

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
                if (p == null || p.Discarded || p.Faction != Faction.OfPlayer || !MatchesSearch(p) || !MatchesFilters(p))
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

        private bool MatchesFilters(Pawn p)
        {
            return (filterHuman && p.RaceProps.Humanlike)
                || (filterAnimal && p.RaceProps.Animal)
                || (filterMechanoid && p.RaceProps.IsMechanoid);
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

            // 类型筛选（仅左栏）
            float filterW = (inRect.width - 20f) / 3f;
            bool filterChanged = false;
            Checkbox(new Rect(0f, y, filterW, 30f), "SubspaceAccess_FilterHuman", ref filterHuman, ref filterChanged);
            Checkbox(new Rect(filterW + 10f, y, filterW, 30f), "SubspaceAccess_FilterAnimal", ref filterAnimal, ref filterChanged);
            Checkbox(new Rect((filterW + 10f) * 2f, y, filterW, 30f), "SubspaceAccess_FilterMechanoid", ref filterMechanoid, ref filterChanged);
            y += 35f;
            // 显示图像开关（默认关闭以加速界面打开）
            Checkbox(new Rect(0f, y, inRect.width / 2f, 30f), "SubspaceAccess_ShowIcons", ref showPawnIcons, ref filterChanged);
            y += 35f;
            if (filterChanged)
            {
                dirty = true;
            }

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
            if (showPawnIcons)
            {
                Widgets.ThingIcon(iconRect, p);
            }
            else
            {
                Widgets.DrawRectFast(iconRect, new Color(0.16f, 0.16f, 0.16f, 0.55f));
                Widgets.DrawBox(iconRect, 1);
            }

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

        /// <summary>复选框行（点击整行切换），风格与 Dialog_OmniResurrector 一致。</summary>
        private void Checkbox(Rect rect, string labelKey, ref bool flag, ref bool changed)
        {
            bool old = flag;
            Widgets.CheckboxDraw(rect.x, rect.y + (rect.height - 24f) / 2f, flag, false);
            Rect labelRect = new Rect(rect.x + 28f, rect.y, rect.width - 28f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, labelKey.Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(rect))
            {
                flag = !flag;
            }
            if (flag != old)
            {
                changed = true;
            }
        }
    }
}
