using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站代理状态的常驻悬浮监视条。
    ///
    /// 设计要点：
    /// - 挂在 MapComponent_OmniWorkstation.MapComponentOnGUI 上绘制，不经过 WindowStack：
    ///   因此不会暂停游戏、不会取消当前选择、不会拦截窗口外的输入，天然"不干扰其他操作"；
    /// - 面板矩形内（按钮区除外）的鼠标事件会被主动消费，避免点击穿透到地图或下层 UI；
    /// - 按住标题栏/面板空白可拖动，位置与开关状态保存在 OmniCrafterSettings 中跨会话记住；
    /// - 只实时显示活动代理明细（名字 + 当前工作），可一键打开完整详情窗口。
    /// </summary>
    public static class OmniWorkstationMonitor
    {
        private const float PanelWidth = 384f;
        private const float TitleHeight = 30f;
        private const float RowHeight = 26f;
        private const float MaxVisibleRows = 9f;
        private const float NameColumnWidth = 110f;
        private const float ShowButtonWidth = 168f;
        private const float ShowButtonHeight = 30f;
        private const float DetailButtonWidth = 68f;
        private const float CollapseButtonWidth = 54f;
        private const float ButtonHeight = 22f;
        private const int RefreshFrameInterval = 30;

        /// <summary>未自定义位置时使用的默认锚点标记（左上角坐标）。</summary>
        private static readonly Vector2 DefaultAnchor =
            new Vector2(UI.screenWidth - PanelWidth - 24f, UI.screenHeight * 0.28f);

        /// <summary>缓存的活动代理明细；仅在低频刷新时重建，绘制阶段零分配。</summary>
        private static readonly System.Collections.Generic.List<OmniWorkProxyStatus> activeRows =
            new System.Collections.Generic.List<OmniWorkProxyStatus>();
        private static int lastRefreshFrame = -RefreshFrameInterval;

        private static bool dragging;
        private static Vector2 dragOffset;

        /// <summary>当前帧底部"更多"按钮区域（无更多提示时为零矩形）；事件处理需放行该按钮。</summary>
        private static Rect moreButtonRect;

        public static void Draw(MapComponent_OmniWorkstation manager)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null) return;
            if (!settings.omniWorkstationMonitorVisible)
            {
                DrawShowButton();
                return;
            }
            RefreshRows(manager);
            DrawPanel();
        }

        // ─── 刷新 ─────────────────────────────────────────────────────────────
        private static void RefreshRows(MapComponent_OmniWorkstation manager)
        {
            int frame = Time.frameCount;
            if (frame - lastRefreshFrame < RefreshFrameInterval) return;
            lastRefreshFrame = frame;
            Text.Font = GameFont.Small;
            manager.FillActiveProxyStatuses(activeRows);
            // 裁切后的文本直接缓存回行数据，避免每帧绘制重复分配字符串。
            float workWidth = PanelWidth - 24f - NameColumnWidth;
            for (int i = 0; i < activeRows.Count; i++)
            {
                OmniWorkProxyStatus row = activeRows[i];
                row.name = ClipText(row.name, NameColumnWidth);
                row.work = ClipText(row.work, workWidth);
                activeRows[i] = row;
            }
        }

        // ─── 折叠状态：入口按钮 ────────────────────────────────────────────────
        // 收起时仅提供单击打开；位置调整在展开面板上通过拖动标题栏完成（位置会被记住）。
        private static void DrawShowButton()
        {
            Vector2 anchor = AnchorPosition();
            Rect button = new Rect(anchor.x, anchor.y, ShowButtonWidth, ShowButtonHeight);
            button = ClampToScreen(button);

            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(button, "OmniWorkstation_MonitorShow".Translate()))
                SetVisible(true);
            TooltipHandler.TipRegion(button, "OmniWorkstation_MonitorShowTip".Translate());
        }

        // ─── 展开状态：悬浮面板 ────────────────────────────────────────────────
        private static void DrawPanel()
        {
            int visibleCount = activeRows.Count;
            float listHeight = visibleCount == 0
                ? RowHeight
                : Mathf.Min(visibleCount, (int)MaxVisibleRows) * RowHeight;
            float panelHeight = TitleHeight + 8f + listHeight + 8f;
            if (visibleCount > MaxVisibleRows) panelHeight += ButtonHeight + 4f;

            Vector2 anchor = AnchorPosition();
            Rect panel = new Rect(anchor.x, anchor.y, PanelWidth, panelHeight);
            panel = ClampToScreen(panel);

            Rect detailButton = new Rect(
                panel.xMax - 8f - DetailButtonWidth, panel.y + 4f, DetailButtonWidth, ButtonHeight);
            Rect collapseButton = new Rect(
                detailButton.x - 6f - CollapseButtonWidth, panel.y + 4f, CollapseButtonWidth, ButtonHeight);
            // 与底部绘制保持一致；无"更多"提示时为零矩形，Contains 恒为 false。
            moreButtonRect = visibleCount > MaxVisibleRows
                ? new Rect(panel.x + 8f, panel.y + TitleHeight + 4f + listHeight + 2f,
                    panel.width - 16f, ButtonHeight)
                : Rect.zero;

            HandlePanelEvents(panel, detailButton, collapseButton);

            Widgets.DrawWindowBackground(panel);
            Text.Font = GameFont.Small;

            // 标题栏（按钮区以外的整条都是拖动手柄）。
            Rect titleText = new Rect(panel.x + 12f, panel.y + 5f,
                collapseButton.x - panel.x - 18f, 20f);
            Widgets.Label(titleText, "OmniWorkstation_MonitorTitle".Translate());
            Rect dragHint = new Rect(panel.x, panel.y, panel.width, TitleHeight);
            TooltipHandler.TipRegion(dragHint, "OmniWorkstation_MonitorDragTip".Translate());
            TooltipHandler.TipRegion(collapseButton, "OmniWorkstation_MonitorCollapseTip".Translate());
            TooltipHandler.TipRegion(detailButton, "OmniWorkstation_MonitorDetailTip".Translate());

            if (Widgets.ButtonText(collapseButton, "OmniWorkstation_MonitorCollapse".Translate()))
                SetVisible(false);
            if (Widgets.ButtonText(detailButton, "OmniWorkstation_MonitorDetail".Translate()))
                OpenDetailWindow();

            // 列表区。
            float y = panel.y + TitleHeight + 4f;
            if (visibleCount == 0)
            {
                Rect rowRect = new Rect(panel.x, y, panel.width, RowHeight);
                Widgets.Label(new Rect(panel.x + 12f, y + 3f, panel.width - 24f, 20f),
                    "OmniWorkstation_MonitorIdle".Translate());
                TooltipHandler.TipRegion(rowRect, "OmniWorkstation_MonitorIdle".Translate());
                return;
            }

            int shown = Mathf.Min(visibleCount, (int)MaxVisibleRows);
            for (int i = 0; i < shown; i++)
            {
                Rect rowRect = new Rect(panel.x, y, panel.width, RowHeight);
                if ((i & 1) == 1) Widgets.DrawLightHighlight(rowRect);
                Widgets.Label(new Rect(panel.x + 12f, y + 3f, NameColumnWidth, 20f), activeRows[i].name);
                Widgets.Label(new Rect(panel.x + 12f + NameColumnWidth, y + 3f,
                    panel.width - 24f - NameColumnWidth, 20f), activeRows[i].work);
                y += RowHeight;
            }

            if (visibleCount > MaxVisibleRows)
            {
                int extra = visibleCount - shown;
                if (Widgets.ButtonText(moreButtonRect, "OmniWorkstation_MonitorMoreTip".Translate(extra)))
                    OpenDetailWindow();
            }
        }

        // ─── 事件（面板内按钮区放行给控件，其余消费防穿透）──────────────────
        private static void HandlePanelEvents(Rect panel, Rect detailButton, Rect collapseButton)
        {
            Event e = Event.current;
            if (e == null) return;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && panel.Contains(e.mousePosition))
                    {
                        // 按钮区交给 ButtonText 处理，不得吞掉点击。
                        if (detailButton.Contains(e.mousePosition) || collapseButton.Contains(e.mousePosition) ||
                            moreButtonRect.Contains(e.mousePosition))
                            return;
                        dragging = true;
                        dragOffset = e.mousePosition - new Vector2(panel.x, panel.y);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (!dragging) return;
                    MoveAnchor(e.mousePosition - dragOffset, panel.width, panel.height);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (dragging && e.button == 0)
                    {
                        dragging = false;
                        PersistAnchor();
                        e.Use();
                    }
                    break;
            }
        }

        // ─── 设置读写 ─────────────────────────────────────────────────────────
        private static Vector2 AnchorPosition()
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null) return DefaultAnchor;
            if (settings.omniWorkstationMonitorX < 0f || settings.omniWorkstationMonitorY < 0f)
                return DefaultAnchor;
            return new Vector2(settings.omniWorkstationMonitorX, settings.omniWorkstationMonitorY);
        }

        private static void MoveAnchor(Vector2 desired, float width, float height)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null) return;
            // 拖动过程中即时写入内存字段；仅释放时落盘一次。
            settings.omniWorkstationMonitorX = Mathf.Clamp(desired.x, 2f, UI.screenWidth - width - 2f);
            settings.omniWorkstationMonitorY = Mathf.Clamp(desired.y, 2f, UI.screenHeight - height - 2f);
        }

        private static void PersistAnchor()
        {
            if (OmniCrafterMod.Instance != null)
                OmniCrafterMod.Instance.WriteSettings();
        }

        private static void SetVisible(bool visible)
        {
            OmniCrafterSettings settings = OmniCrafterMod.Settings;
            if (settings == null || settings.omniWorkstationMonitorVisible == visible) return;
            settings.omniWorkstationMonitorVisible = visible;
            PersistAnchor();
        }

        private static void OpenDetailWindow()
        {
            // 详情窗口需要 manager；由当前活动地图的组件提供。
            MapComponent_OmniWorkstation manager = Find.CurrentMap?.GetComponent<MapComponent_OmniWorkstation>();
            if (manager != null)
                Find.WindowStack.Add(new Dialog_OmniWorkstationStatus(manager));
        }

        private static Rect ClampToScreen(Rect rect)
        {
            rect.x = Mathf.Clamp(rect.x, 2f, Mathf.Max(2f, UI.screenWidth - rect.width - 2f));
            rect.y = Mathf.Clamp(rect.y, 2f, Mathf.Max(2f, UI.screenHeight - rect.height - 2f));
            return rect;
        }

        /// <summary>超出给定像素宽度时在字符边界截断并追加省略号（仅超宽行触发，低频）。</summary>
        private static string ClipText(string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text;
            if (Text.CalcSize(text).x <= maxWidth) return text;
            if (text.Length <= 1) return text;

            float perChar = Mathf.Max(1f, Text.CalcSize(text).x / text.Length);
            int guess = Mathf.Max(1, Mathf.FloorToInt(maxWidth / perChar));
            string clipped = text.Substring(0, Mathf.Min(guess, text.Length - 1)) + "…";
            while (Text.CalcSize(clipped).x > maxWidth && clipped.Length > 2)
                clipped = text.Substring(0, clipped.Length - 2) + "…";
            return clipped;
        }
    }
}
