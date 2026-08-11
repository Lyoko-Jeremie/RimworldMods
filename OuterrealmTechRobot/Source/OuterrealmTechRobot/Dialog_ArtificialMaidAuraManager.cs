using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆光环管理对话框。
    /// 列出我方所有存活小人，逐项勾选/取消"女仆在身边"光环标记；
    /// 勾选立即生效（标记 + 施加光环），取消立即移除。
    /// 任何女仆（我方阵营）均可通过命令按钮打开本对话框管理全部我方小人。
    /// </summary>
    public class Dialog_ArtificialMaidAuraManager : Window
    {
        private const float RowHeight = 32f;
        private const float CheckboxSize = 24f;

        private readonly List<Pawn> candidatePawns = new List<Pawn>();
        private Vector2 scrollPosition;

        public override Vector2 InitialSize => new Vector2(620f, 640f);

        public Dialog_ArtificialMaidAuraManager()
        {
            doCloseX = true;
            doCloseButton = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            forcePause = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            RefreshCandidatePawns();
        }

        /// <summary>
        /// 刷新候选列表：我方所有存活小人（地图 + 远行队 + 运输舱），按名字排序。
        /// </summary>
        private void RefreshCandidatePawns()
        {
            candidatePawns.Clear();
            List<Pawn> all = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p.Dead || p.health == null || !p.RaceProps.Humanlike) continue;
                candidatePawns.Add(p);
            }
            candidatePawns.Sort((a, b) => a.LabelCap.CompareTo(b.LabelCap));
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "ArtificialMaidAuraDialogTitle".Translate());
            Text.Font = GameFont.Small;

            Rect listRect = new Rect(inRect.x, inRect.y + 44f, inRect.width, inRect.yMax - inRect.y - 44f - 76f);

            float contentHeight = candidatePawns.Count * RowHeight;
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, contentHeight);
            Widgets.BeginScrollView(listRect, ref scrollPosition, viewRect);

            GameComponent_ArtificialMaidAuraManager manager = GameComponent_ArtificialMaidAuraManager.Get();
            for (int i = 0; i < candidatePawns.Count; i++)
            {
                Pawn pawn = candidatePawns[i];
                Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                if (i % 2 == 1)
                {
                    Widgets.DrawHighlight(row);
                }

                // 勾选框（行右侧）：勾选 = 标记并施加光环；取消 = 移除标记与光环
                bool isMarked = manager.IsMarked(pawn);
                bool checkOn = isMarked;
                Vector2 checkPos = new Vector2(row.x + row.width - CheckboxSize, row.y + (RowHeight - CheckboxSize) / 2f);
                Widgets.Checkbox(checkPos, ref checkOn);
                if (checkOn != isMarked)
                {
                    if (checkOn)
                    {
                        manager.Mark(pawn);
                    }
                    else
                    {
                        manager.Unmark(pawn);
                    }
                }

                // 名字 + 当前位置
                Rect textRect = new Rect(row.x + 8f, row.y, row.width - CheckboxSize - 20f, RowHeight);
                Widgets.Label(textRect, pawn.LabelCap + "  " + "ArtificialMaidAuraLocationSuffix".Translate(LocationLabel(pawn)));
            }

            Widgets.EndScrollView();

            // 底部说明
            Rect hintRect = new Rect(inRect.x, inRect.yMax - 68f, inRect.width - 160f, 58f);
            Widgets.Label(hintRect, "ArtificialMaidAuraDialogHint".Translate());

            if (Widgets.ButtonText(new Rect(inRect.xMax - 150f, inRect.yMax - 35f, 150f, 35f), "CloseButton".Translate()))
            {
                Close();
            }
        }

        /// <summary>
        /// 小人当前位置描述：远行队 / 地图 / 其他。
        /// </summary>
        private static string LocationLabel(Pawn pawn)
        {
            Caravan caravan = pawn.GetCaravan();
            if (caravan != null)
            {
                return caravan.LabelCap;
            }
            if (pawn.Spawned && pawn.Map != null && pawn.Map.info.parent != null)
            {
                return pawn.Map.info.parent.LabelCap;
            }
            return "ArtificialMaidAuraLocationUnknown".Translate();
        }
    }
}
