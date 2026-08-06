using System.Collections.Generic;
using System.Linq;
using FullyAutomaticOmniCrafter.UtilApi;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能重生平台的复活控制界面。
    /// 单个列表同时展示"已登记"（受 GC 保护）与"未登记"的已死亡 Pawn，
    /// 已登记的 Pawn 排序在前并带高亮徽章。
    /// 支持按类型（人类/动物/机械体）、派系（玩家/敌对/中立/无派系）筛选，
    /// 并提供名称搜索框。
    /// 每一行可执行 [登记]/[取消登记]/[复活] 操作，复活为即时完成。
    /// </summary>
    public class Dialog_OmniResurrector : Window
    {
        private const float RowHeight = 46f;

        private readonly CompOmniResurrector comp;
        private string searchText = "";
        private Vector2 scrollPosition;
        private List<Pawn> cachedPawns;

        // 类型筛选。
        private bool filterHuman = true;
        private bool filterAnimal = true;
        private bool filterMechanoid = true;
        // 派系筛选。
        private bool filterFactionPlayer = true;
        private bool filterFactionHostile = true;
        private bool filterFactionNeutral = true;
        private bool filterFactionNone = true;

        public override Vector2 InitialSize => new Vector2(720f, 720f);

        public Dialog_OmniResurrector(CompOmniResurrector comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            GameComponent_OmniResurrector.Instance?.CleanupInvalid();
            UpdateCache();
        }

        /// <summary>
        /// 构建缓存列表：可复活死亡 Pawn 集合中，先按筛选、再按搜索词过滤，
        /// 已登记的排在上面，同组内按名称排序。
        /// 仅在筛选/搜索/操作变化时重建，不做高频全量扫描。
        /// </summary>
        private void UpdateCache()
        {
            GameComponent_OmniResurrector mgr = GameComponent_OmniResurrector.Instance;
            if (mgr == null)
            {
                cachedPawns = new List<Pawn>();
                return;
            }
            List<Pawn> registered = mgr.Registered;
            cachedPawns = Find.WorldPawns.AllPawnsDead
                .Where(p => p != null && p.Dead && !p.Discarded)
                .Where(MatchesFilters)
                .Where(MatchesSearch)
                .OrderBy(p => registered.Contains(p) ? 0 : 1)
                .ThenBy(p => p.LabelCap)
                .ToList();
        }

        private bool MatchesFilters(Pawn p)
        {
            bool typeOk = (filterHuman && p.RaceProps.Humanlike)
                || (filterAnimal && p.RaceProps.Animal)
                || (filterMechanoid && p.RaceProps.IsMechanoid);
            if (!typeOk)
            {
                return false;
            }
            Faction player = Faction.OfPlayer;
            if (p.Faction == null)
            {
                return filterFactionNone;
            }
            if (p.Faction == player)
            {
                return filterFactionPlayer;
            }
            if (p.HostileTo(player))
            {
                return filterFactionHostile;
            }
            return filterFactionNeutral;
        }

        private bool MatchesSearch(Pawn p)
        {
            if (searchText.NullOrEmpty())
            {
                return true;
            }
            string lower = searchText.ToLower();
            if (p.LabelCap.ToLower().Contains(lower))
            {
                return true;
            }
            if (p.def.defName.ToLower().Contains(lower))
            {
                return true;
            }
            return p.kindDef != null && p.kindDef.defName.ToLower().Contains(lower);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "OmniResurrector_Title".Translate());
            Text.Font = GameFont.Small;

            float y = 40f;

            // 搜索框。
            Widgets.Label(new Rect(0f, y, 70f, 30f), "OmniResurrector_Search".Translate());
            string newSearch = Widgets.TextField(new Rect(75f, y, inRect.width - 80f, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                UpdateCache();
            }
            y += 35f;

            // 类型筛选。
            float typeW = (inRect.width - 10f) / 3f;
            bool changed = false;
            Checkbox(new Rect(0f, y, typeW, 30f), "OmniResurrector_FilterHuman", ref filterHuman, ref changed);
            Checkbox(new Rect(typeW, y, typeW, 30f), "OmniResurrector_FilterAnimal", ref filterAnimal, ref changed);
            Checkbox(new Rect(typeW * 2f, y, typeW, 30f), "OmniResurrector_FilterMechanoid", ref filterMechanoid, ref changed);
            y += 35f;

            // 派系筛选。
            float factionW = (inRect.width - 10f) / 4f;
            Checkbox(new Rect(0f, y, factionW, 30f), "OmniResurrector_FilterFactionPlayer", ref filterFactionPlayer, ref changed);
            Checkbox(new Rect(factionW, y, factionW, 30f), "OmniResurrector_FilterFactionHostile", ref filterFactionHostile, ref changed);
            Checkbox(new Rect(factionW * 2f, y, factionW, 30f), "OmniResurrector_FilterFactionNeutral", ref filterFactionNeutral, ref changed);
            Checkbox(new Rect(factionW * 3f, y, factionW, 30f), "OmniResurrector_FilterFactionNone", ref filterFactionNone, ref changed);
            y += 35f;

            if (changed)
            {
                UpdateCache();
            }

            // 列表。
            Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y - 50f);
            if (cachedPawns.Count == 0)
            {
                Widgets.Label(outRect, "OmniResurrector_EmptyList".Translate());
                return;
            }
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, cachedPawns.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0f;
            foreach (Pawn pawn in cachedPawns)
            {
                DrawRow(new Rect(0f, curY, viewRect.width, RowHeight), pawn);
                curY += RowHeight;
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 绘制复选框行（点击整行切换状态），风格与项目其他 Dialog 保持一致。
        /// </summary>
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

        /// <summary>
        /// 绘制一行 Pawn：头像、名称（含派系与已登记徽章）、类型与尸体状态、操作按钮。
        /// </summary>
        private void DrawRow(Rect rowRect, Pawn pawn)
        {
            GameComponent_OmniResurrector mgr = GameComponent_OmniResurrector.Instance;
            bool registered = mgr != null && mgr.Registered.Contains(pawn);

            // 已登记行高亮。
            if (registered)
            {
                Widgets.DrawRectFast(rowRect, new Color(0.25f, 0.55f, 0.25f, 0.35f));
            }
            else if (Mouse.IsOver(rowRect))
            {
                Widgets.DrawHighlight(rowRect);
            }

            // 头像。
            Widgets.ThingIcon(new Rect(rowRect.x + 5f, rowRect.y + 8f, 30f, 30f), pawn);

            // 第一行：名称 + 派系 + 已登记徽章。
            string nameLine = pawn.LabelCap;
            if (pawn.Faction != null)
            {
                nameLine += " (" + pawn.Faction.Name + ")";
            }
            else
            {
                nameLine += " (" + "OmniResurrector_NoFaction".Translate() + ")";
            }
            if (registered)
            {
                nameLine = "OmniResurrector_Registered".Translate() + " " + nameLine;
            }
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rowRect.x + 42f, rowRect.y + 4f, 320f, 22f), nameLine);

            // 第二行：类型 + 尸体状态。
            string infoLine = GetKindLabel(pawn) + "  " + GetCorpseInfo(pawn);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rowRect.x + 42f, rowRect.y + 24f, 320f, 18f), infoLine);
            Text.Font = GameFont.Small;

            // 操作按钮。
            Rect registerBtn = new Rect(rowRect.xMax - 190f, rowRect.y + 7f, 88f, 32f);
            Rect resurrectBtn = new Rect(rowRect.xMax - 96f, rowRect.y + 7f, 88f, 32f);

            if (registered)
            {
                if (Widgets.ButtonText(registerBtn, "OmniResurrector_ButtonUnregister".Translate()))
                {
                    mgr?.Unregister(pawn);
                    UpdateCache();
                }
            }
            else if (Widgets.ButtonText(registerBtn, "OmniResurrector_ButtonRegister".Translate()))
            {
                mgr?.Register(pawn);
                UpdateCache();
            }

            if (Widgets.ButtonText(resurrectBtn, "OmniResurrector_ButtonResurrect".Translate()))
            {
                TryResurrect(pawn);
            }
        }

        /// <summary>
        /// 即时复活：先预检电量并给出具体数值提示，成功后通过 Comp 执行复活。
        /// </summary>
        private void TryResurrect(Pawn pawn)
        {
            PowerNet net = comp.parent.GetComp<CompPowerTrader>()?.PowerNet;
            float need = comp.Props.energyCostWd;
            if (!OmniPowerNetUtility.CanDeductFromPowerNet(net, need, out OmniPowerNetStorageState state, comp.Props.allowInfiniteGenerator))
            {
                Messages.Message(
                    "OmniResurrector_NoEnergy".Translate(need.ToString("F0"), state.AvailableStoredEnergyWd.ToString("F0")),
                    MessageTypeDefOf.RejectInput);
                return;
            }
            if (comp.TryResurrectNow(pawn))
            {
                Messages.Message("OmniResurrector_Resurrected".Translate(pawn.LabelCap), (LookTargets)pawn, MessageTypeDefOf.PositiveEvent);
            }
            else
            {
                Messages.Message("OmniResurrector_FailInvalid".Translate(pawn.LabelCap), MessageTypeDefOf.RejectInput);
            }
            UpdateCache();
        }

        /// <summary>类型标签：人类 / 动物 / 机械体 / 其他。</summary>
        private static string GetKindLabel(Pawn p)
        {
            if (p.RaceProps.Humanlike)
            {
                return "OmniResurrector_KindHuman".Translate();
            }
            if (p.RaceProps.Animal)
            {
                return "OmniResurrector_KindAnimal".Translate();
            }
            if (p.RaceProps.IsMechanoid)
            {
                return "OmniResurrector_KindMechanoid".Translate();
            }
            return p.def.label;
        }

        /// <summary>尸体状态：无尸体 / 新鲜 / 腐烂 / 干尸。</summary>
        private static string GetCorpseInfo(Pawn p)
        {
            Corpse corpse = p.Corpse;
            if (corpse == null || corpse.Destroyed)
            {
                return "OmniResurrector_CorpseNone".Translate();
            }
            CompRottable rottable = corpse.GetComp<CompRottable>();
            if (rottable == null)
            {
                return "OmniResurrector_CorpseYes".Translate();
            }
            switch (rottable.Stage)
            {
                case RotStage.Rotting:
                    return "OmniResurrector_CorpseRotting".Translate();
                case RotStage.Dessicated:
                    return "OmniResurrector_CorpseDessicated".Translate();
                default:
                    return "OmniResurrector_CorpseFresh".Translate();
            }
        }
    }
}
