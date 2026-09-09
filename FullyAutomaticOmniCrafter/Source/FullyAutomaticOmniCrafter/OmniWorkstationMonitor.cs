using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站代理状态的常驻悬浮监视窗口。
    /// 交互结构沿用 Dubs Mint Minimap：GameUI 窗口、标题栏限定拖动、释放后持久化位置。
    /// 窗口右下角使用 RimWorld 原生 WindowResizer，窗口高度直接决定可同时显示的代理数量。
    /// </summary>
    public static class OmniWorkstationMonitor
    {
        private const float DefaultWidth = 384f;
        private const float DefaultHeight = 336f;

        private static Window_OmniWorkstationMonitor window;

        public static void Draw(MapComponent_OmniWorkstation manager)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null) return;

            if (!settings.omniWorkstationMonitorVisible)
            {
                if (window != null && window.IsOpen)
                    window.Close(false);
                window = null;
                return;
            }

            if (window != null && window.IsOpen)
            {
                window.SetManager(manager);
                return;
            }

            window = new Window_OmniWorkstationMonitor(manager);
            Find.WindowStack.Add(window);
        }

        public static bool Visible
        {
            get
            {
                OmniCrafterSettings settings = OmniCrafterMod.Settings;
                return settings != null && settings.omniWorkstationMonitorVisible;
            }
        }

        public static void SetVisible(bool visible)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null || settings.omniWorkstationMonitorVisible == visible) return;

            settings.omniWorkstationMonitorVisible = visible;
            if (!visible && window != null && window.IsOpen)
                window.Close(false);
            WriteSettings();
        }

        internal static Vector2 SavedPosition(OmniCrafterSettings settings)
        {
            if (settings.omniWorkstationMonitorX >= 0f && settings.omniWorkstationMonitorY >= 0f)
                return new Vector2(settings.omniWorkstationMonitorX, settings.omniWorkstationMonitorY);
            return new Vector2(UI.screenWidth - DefaultWidth - 24f, UI.screenHeight * 0.28f);
        }

        internal static Vector2 SavedSize(OmniCrafterSettings settings)
        {
            float width = settings.omniWorkstationMonitorWidth > 0f
                ? settings.omniWorkstationMonitorWidth
                : DefaultWidth;
            float height = settings.omniWorkstationMonitorHeight > 0f
                ? settings.omniWorkstationMonitorHeight
                : DefaultHeight;
            return new Vector2(width, height);
        }

        internal static Rect ClampToScreen(Rect rect)
        {
            rect.width = Mathf.Clamp(rect.width, Window_OmniWorkstationMonitor.MinWidth,
                Mathf.Max(Window_OmniWorkstationMonitor.MinWidth, UI.screenWidth - 4f));
            rect.height = Mathf.Clamp(rect.height, Window_OmniWorkstationMonitor.MinHeight,
                Mathf.Max(Window_OmniWorkstationMonitor.MinHeight, UI.screenHeight - 4f));
            rect.x = Mathf.Clamp(rect.x, 2f, Mathf.Max(2f, UI.screenWidth - rect.width - 2f));
            rect.y = Mathf.Clamp(rect.y, 2f, Mathf.Max(2f, UI.screenHeight - rect.height - 2f));
            return rect;
        }

        internal static void WriteSettings()
        {
            if (OmniCrafterMod.Instance != null)
                OmniCrafterMod.Instance.WriteSettings();
        }

        internal static void ClearWindow(Window_OmniWorkstationMonitor closingWindow)
        {
            if (window == closingWindow)
                window = null;
        }

        internal static void OpenDetailWindow()
        {
            MapComponent_OmniWorkstation manager =
                Find.CurrentMap?.GetComponent<MapComponent_OmniWorkstation>();
            if (manager != null)
                Find.WindowStack.Add(new Dialog_OmniWorkstationStatus(manager));
        }
    }

    /// <summary>不暂停也不遮挡窗口外游戏操作的代理状态监视窗口。</summary>
    public sealed class Window_OmniWorkstationMonitor : Window
    {
        internal const float MinWidth = 320f;
        internal const float MinHeight = 150f;

        private const float TitleHeight = 30f;
        private const float RowHeight = 26f;
        private const float NameColumnWidth = 110f;
        private const float DetailButtonWidth = 68f;
        private const float CollapseButtonWidth = 54f;
        private const float ButtonHeight = 22f;
        private const float SearchButtonSize = 25f;
        private const float SearchRowHeight = 28f;
        private const float ResizeHandleSize = 24f;
        private const int RefreshFrameInterval = 30;

        private readonly List<OmniWorkProxyStatus> activeRows = new List<OmniWorkProxyStatus>();
        private MapComponent_OmniWorkstation manager;
        private readonly QuickSearchWidget searchWidget = new QuickSearchWidget();
        private int lastRefreshFrame = -RefreshFrameInterval;
        private Rect lastSavedRect;
        private bool rectDirty;
        private bool searchVisible;
        private bool focusSearch;

        public override Vector2 InitialSize =>
            OmniWorkstationMonitor.SavedSize(OmniCrafterMod.Settings);

        protected override float Margin => 0f;

        public Window_OmniWorkstationMonitor(MapComponent_OmniWorkstation manager)
        {
            this.manager = manager;
            layer = WindowLayer.GameUI;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnAccept = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            preventCameraMotion = false;
            doCloseButton = false;
            doCloseX = false;
            draggable = false;
            resizeable = true;
            drawShadow = false;
            focusWhenOpened = false;
            onlyOneOfTypeAllowed = true;
        }

        public void SetManager(MapComponent_OmniWorkstation newManager)
        {
            if (manager == newManager) return;
            manager = newManager;
            lastRefreshFrame = -RefreshFrameInterval;
        }

        protected override void SetInitialSizeAndPosition()
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            Vector2 size = OmniWorkstationMonitor.SavedSize(settings);
            Vector2 position = OmniWorkstationMonitor.SavedPosition(settings);
            windowRect = OmniWorkstationMonitor.ClampToScreen(
                new Rect(position.x, position.y, size.x, size.y)).Rounded();
            lastSavedRect = windowRect;
        }

        public override void WindowOnGUI()
        {
            base.WindowOnGUI();

            Rect clamped = OmniWorkstationMonitor.ClampToScreen(windowRect).Rounded();
            if (clamped != windowRect)
                windowRect = clamped;

            if (windowRect != lastSavedRect)
                rectDirty = true;

            // 与 Dubs Mint Minimap 一致，在鼠标释放后才落盘，避免拖动期间频繁写配置。
            if (rectDirty && !Input.GetMouseButton(0))
                PersistRect();
        }

        public override void DoWindowContents(Rect inRect)
        {
            RefreshRows();

            Text.Font = GameFont.Small;
            Rect detailButton = new Rect(
                inRect.xMax - 8f - DetailButtonWidth, 4f, DetailButtonWidth, ButtonHeight);
            Rect collapseButton = new Rect(
                detailButton.x - 6f - CollapseButtonWidth, 4f, CollapseButtonWidth, ButtonHeight);
            Rect searchButton = new Rect(4f, 3f, SearchButtonSize, SearchButtonSize);
            Rect titleDragRect = new Rect(searchButton.xMax + 2f, 0f,
                Mathf.Max(0f, collapseButton.x - searchButton.xMax - 8f), TitleHeight);
            Rect titleTextRect = new Rect(titleDragRect.x + 6f, 5f,
                Mathf.Max(0f, titleDragRect.width - 12f), 20f);

            Widgets.Label(titleTextRect, "OmniWorkstation_MonitorTitle".Translate());
            if (Widgets.ButtonImage(searchButton, TexButton.Search,
                searchVisible ? Color.white : Color.gray))
            {
                searchVisible = !searchVisible;
                if (searchVisible)
                {
                    focusSearch = true;
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }
                else
                {
                    searchWidget.Reset();
                    searchWidget.Unfocus();
                }
            }
            TooltipHandler.TipRegion(searchButton, "OmniWorkstation_MonitorSearchTip".Translate());
            GUI.DragWindow(titleDragRect);
            TooltipHandler.TipRegion(titleDragRect, "OmniWorkstation_MonitorDragTip".Translate());
            TooltipHandler.TipRegion(collapseButton, "OmniWorkstation_MonitorCollapseTip".Translate());
            TooltipHandler.TipRegion(detailButton, "OmniWorkstation_MonitorDetailTip".Translate());

            if (Widgets.ButtonText(collapseButton, "OmniWorkstation_MonitorCollapse".Translate()))
                CloseMonitor();
            if (Widgets.ButtonText(detailButton, "OmniWorkstation_MonitorDetail".Translate()))
                OmniWorkstationMonitor.OpenDetailWindow();

            float listTop = TitleHeight + 4f;
            if (searchVisible)
            {
                Rect searchRect = new Rect(8f, TitleHeight + 2f, inRect.width - 16f, 24f);
                searchWidget.OnGUI(searchRect);
                TooltipHandler.TipRegion(searchRect, "OmniWorkstation_MonitorSearchTip".Translate());
                if (focusSearch)
                {
                    focusSearch = false;
                    searchWidget.Focus();
                }
                listTop += SearchRowHeight;
            }

            DrawRows(inRect, listTop);

            Rect resizeHandle = new Rect(inRect.xMax - ResizeHandleSize,
                inRect.yMax - ResizeHandleSize, ResizeHandleSize, ResizeHandleSize);
            TooltipHandler.TipRegion(resizeHandle, "OmniWorkstation_MonitorResizeTip".Translate());
        }

        public override void PostClose()
        {
            if (rectDirty)
                PersistRect();
            OmniWorkstationMonitor.ClearWindow(this);
            base.PostClose();
        }

        private void RefreshRows()
        {
            int frame = Time.frameCount;
            if (frame - lastRefreshFrame < RefreshFrameInterval || manager == null) return;
            lastRefreshFrame = frame;
            manager.FillActiveProxyStatuses(activeRows);
        }

        private void DrawRows(Rect inRect, float listTop)
        {
            float usableHeight = Mathf.Max(RowHeight,
                inRect.height - listTop - ResizeHandleSize - 2f);
            int rowSlots = Mathf.Max(1, Mathf.FloorToInt(usableHeight / RowHeight));
            int filteredCount = CountFilteredRows();
            searchWidget.noResultsMatched = searchWidget.filter.Active && filteredCount == 0;
            int shown = filteredCount;
            bool hasMore = shown > rowSlots;
            if (hasMore)
                shown = Mathf.Max(0, rowSlots - 1);

            float workX = 12f + NameColumnWidth;
            float workWidth = Mathf.Max(0f, inRect.width - workX - 12f);
            if (filteredCount == 0)
            {
                Rect idleRect = new Rect(0f, listTop, inRect.width, RowHeight);
                Widgets.Label(new Rect(12f, listTop + 3f, inRect.width - 24f, 20f),
                    (searchWidget.filter.Active
                        ? "OmniWorkstation_MonitorSearchEmpty"
                        : "OmniWorkstation_MonitorIdle").Translate());
                TooltipHandler.TipRegion(idleRect, searchWidget.filter.Active
                    ? "OmniWorkstation_MonitorSearchEmpty".Translate()
                    : "OmniWorkstation_MonitorIdle".Translate());
                return;
            }

            float y = listTop;
            int drawn = 0;
            for (int i = 0; i < activeRows.Count && drawn < shown; i++)
            {
                if (!MatchesSearch(activeRows[i])) continue;
                Rect rowRect = new Rect(0f, y, inRect.width, RowHeight);
                if ((drawn & 1) == 1)
                    Widgets.DrawLightHighlight(rowRect);
                Widgets.Label(new Rect(12f, y + 3f, NameColumnWidth, 20f), activeRows[i].name);
                Widgets.Label(new Rect(workX, y + 3f, workWidth, 20f), activeRows[i].work);
                y += RowHeight;
                drawn++;
            }

            if (hasMore)
            {
                int extra = filteredCount - shown;
                Rect moreButton = new Rect(8f, y + 2f,
                    Mathf.Max(0f, inRect.width - ResizeHandleSize - 20f), ButtonHeight);
                if (Widgets.ButtonText(moreButton,
                    "OmniWorkstation_MonitorMoreTip".Translate(extra)))
                    OmniWorkstationMonitor.OpenDetailWindow();
            }
        }

        private int CountFilteredRows()
        {
            if (!searchWidget.filter.Active)
                return activeRows.Count;

            int count = 0;
            for (int i = 0; i < activeRows.Count; i++)
            {
                if (MatchesSearch(activeRows[i]))
                    count++;
            }
            return count;
        }

        private bool MatchesSearch(OmniWorkProxyStatus row)
        {
            return searchWidget.filter.Matches(row.name) || searchWidget.filter.Matches(row.work);
        }

        private void CloseMonitor()
        {
            PersistRect();
            OmniWorkstationMonitor.SetVisible(false);
        }

        private void PersistRect()
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null) return;

            Rect clamped = OmniWorkstationMonitor.ClampToScreen(windowRect).Rounded();
            windowRect = clamped;
            settings.omniWorkstationMonitorX = clamped.x;
            settings.omniWorkstationMonitorY = clamped.y;
            settings.omniWorkstationMonitorWidth = clamped.width;
            settings.omniWorkstationMonitorHeight = clamped.height;
            lastSavedRect = clamped;
            rectDirty = false;
            OmniWorkstationMonitor.WriteSettings();
        }
    }
}
