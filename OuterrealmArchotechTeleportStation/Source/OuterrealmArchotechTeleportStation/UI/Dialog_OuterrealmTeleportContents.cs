using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 非传送站地图使用的传送内容选择窗口。
    /// 复用原版远行队 Transferable 控件，确认后立即把选择内容传送走。
    /// </summary>
    public class Dialog_OuterrealmTeleportContents : Window
    {
        private const float TitleRectHeight = 35f;
        private const float BottomAreaHeight = 55f;

        private static readonly Vector2 BottomButtonSize = new Vector2(160f, 40f);
        private static readonly List<TabRecord> TabsList = new List<TabRecord>();

        private readonly Map map;
        private readonly OuterrealmTeleportDestination destination;
        private List<TransferableOneWay> transferables;
        private TransferableOneWayWidget pawnsTransfer;
        private TransferableOneWayWidget itemsTransfer;
        private TransferableOneWayWidget travelSuppliesTransfer;
        private Tab tab;

        public Dialog_OuterrealmTeleportContents(Map map, OuterrealmTeleportDestination destination)
        {
            this.map = map;
            this.destination = destination;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(1024f, UI.screenHeight);

        protected override float Margin => 0f;

        public override void PostOpen()
        {
            base.PostOpen();
            CalculateAndRecacheTransferables();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect titleRect = new Rect(0f, 0f, inRect.width, TitleRectHeight);
            using (new TextBlock(GameFont.Medium, TextAnchor.MiddleCenter))
            {
                Widgets.Label(titleRect, "OATS_SelectTeleportContents".Translate(destination.LabelCap));
            }

            TabsList.Clear();
            TabsList.Add(new TabRecord("PawnsTab".Translate(), () => tab = Tab.Pawns, tab == Tab.Pawns));
            TabsList.Add(new TabRecord("ItemsTab".Translate(), () => tab = Tab.Items, tab == Tab.Items));
            TabsList.Add(new TabRecord("TravelSupplies".Translate(), () => tab = Tab.TravelSupplies, tab == Tab.TravelSupplies));

            inRect.yMin += 67f;
            Widgets.DrawMenuSection(inRect);
            TabDrawer.DrawTabs(inRect, TabsList);
            TabsList.Clear();

            inRect = inRect.ContractedBy(17f);
            Widgets.BeginGroup(inRect);
            Rect contentsRect = inRect.AtZero();
            DoBottomButtons(contentsRect);
            contentsRect.yMax -= 76f;

            bool anythingChanged = false;
            switch (tab)
            {
                case Tab.Pawns:
                    pawnsTransfer.OnGUI(contentsRect, out anythingChanged);
                    break;
                case Tab.Items:
                    itemsTransfer.OnGUI(contentsRect, out anythingChanged);
                    break;
                case Tab.TravelSupplies:
                    travelSuppliesTransfer.OnGUI(contentsRect, out anythingChanged);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Widgets.EndGroup();
        }

        public override void OnAcceptKeyPressed()
        {
            if (!TryAccept())
            {
                return;
            }

            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Close(false);
        }

        private void DoBottomButtons(Rect rect)
        {
            float y = rect.height - BottomAreaHeight - 17f;
            if (Widgets.ButtonText(new Rect(rect.width - BottomButtonSize.x, y, BottomButtonSize.x, BottomButtonSize.y), "AcceptButton".Translate()) &&
                TryAccept())
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                Close(false);
            }

            if (Widgets.ButtonText(new Rect(0f, y, BottomButtonSize.x, BottomButtonSize.y), "CancelButton".Translate()))
            {
                Close();
            }

            Rect resetRect = new Rect(
                rect.width / 2f - BottomButtonSize.x / 2f,
                y,
                BottomButtonSize.x,
                BottomButtonSize.y);
            if (Widgets.ButtonText(resetRect, "ResetButton".Translate()))
            {
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                CalculateAndRecacheTransferables();
            }
        }

        private bool TryAccept()
        {
            return OuterrealmMapTeleportUtility.TryTeleportTransferables(
                map,
                destination,
                transferables,
                removeSourceMap: false);
        }

        private void CalculateAndRecacheTransferables()
        {
            transferables = OuterrealmMapTeleportUtility.CreateTransferables(map, selectAll: false);
            CaravanUIUtility.CreateCaravanTransferableWidgets(
                transferables,
                out pawnsTransfer,
                out itemsTransfer,
                out travelSuppliesTransfer,
                "FormCaravanColonyThingCountTip".Translate(),
                IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload,
                () => float.MaxValue,
                false,
                map.Tile);
        }

        private enum Tab
        {
            Pawns,
            Items,
            TravelSupplies
        }
    }
}
