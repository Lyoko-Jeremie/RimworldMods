using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储终端「投影诊断」窗口（只读）。
    ///
    /// 用途：列出本建筑当前视图实际投影了哪些物品及各自数量，并补上"存储清单允许但尚未物化"的条目；
    /// 异常行（筛选已禁止、空条目残留、投影堆数不符预期、待物化、借出中的实物）置顶标色，便于诊断。
    /// 顶部搜索框可按物品名 / defName / def label 过滤列表。
    ///
    /// 约束（严禁破坏）：
    ///  1. 本窗口只读：不修改库存、不物化/回收副本、不改预留、不动 Job，任何情况都不会污染存档。
    ///  2. 行集按全局版本号 + 1 秒节流重建；真实时间只用于纯 UI 刷新（readme §9.1），不推进任何游戏状态。
    ///  3. 行对象池化复用，绘制只画滚动视口内的行，避免每帧分配与全量绘制。
    /// </summary>
    public class Dialog_VaultProjectionDiagnostics : Window
    {
        private const float RowHeight = 28f;
        private const float RefreshIntervalSeconds = 1f;
        private const int MaxRows = 4096; // 行数安全上限，超出即截断并提示

        // 列宽（表头与行绘制必须共用同一组常量，避免错列）
        private const float IconW = 34f;
        private const float CopyColW = 74f;
        private const float GlobalColW = 92f;
        private const float ReservedColW = 74f;
        private const float AvailColW = 92f;
        private const float StateColW = 150f;

        private const byte SevNormal = 0;   // 正常
        private const byte SevNote = 1;     // 需要注意（未必是错误）
        private const byte SevAnomaly = 2;  // 异常

        private static readonly Color AnomalyColor = new Color(1f, 0.45f, 0.45f);
        private static readonly Color NoteColor = new Color(1f, 0.85f, 0.4f);
        private static readonly Color HeaderColor = new Color(0.75f, 0.75f, 0.75f);

        private readonly Building_OuterrealmVault vault;

        // ── 行池 ────────────────────────────────────────────────────────────────
        private DiagRow[] rows = new DiagRow[64];
        private int rowCount;
        private int noteRowCount;   // Severity >= SevNote 的行数（排序后位于 [0, noteRowCount)）
        private int seq;

        // ── 列表筛选（搜索词 + 仅看注意项） ─────────────────────────────────────
        private string searchText = "";
        private string lastSearchText;              // 缓冲，用于零分配地检测搜索词变化
        private bool lastOnlyNoteworthy;
        private bool filterInitialized;
        private int[] visibleIdx = new int[64];     // 通过筛选的行在 rows 中的下标（排序后的展示顺序）
        private int visibleCount;

        // ── 概览统计 ────────────────────────────────────────────────────────────
        private int copyRowCount;
        private int borrowedRowCount;
        private int allowedEntryCount;
        private int pendingCount;
        private int anomalyCount;
        private bool materializeWork;
        private bool truncated;
        private int builtVersion = -1;
        private int builtReservationVersion = -1;
        private float lastBuiltRealtime = -1f;

        // ── 绘制状态 ────────────────────────────────────────────────────────────
        private Vector2 scrollPosition;
        private float visibleMinY;
        private float visibleMaxY;
        private bool onlyNoteworthy;

        private readonly HashSet<OuterrealmEntry> seenEntries = new HashSet<OuterrealmEntry>();
        private readonly StringBuilder tipBuilder = new StringBuilder(256);

        /// <summary>高度 = 560 × 1.2（在窗口底部关闭按钮移除后加高五分之一）。</summary>
        public override Vector2 InitialSize => new Vector2(820f, 672f);

        public Dialog_VaultProjectionDiagnostics(Building_OuterrealmVault vault)
        {
            this.vault = vault;
            doCloseX = true;        // 右上角关闭按钮保留
            doCloseButton = false;  // 不再绘制窗口底部关闭按钮
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
        }

        // ── 窗口主体 ────────────────────────────────────────────────────────────

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            RebuildIfNeeded(gs);

            Text.Font = GameFont.Medium;
            string title = "VaultProjDiag_Title".Translate(vault != null ? vault.LabelShortCap.ToString() : "");
            if (truncated)
            {
                title = title + "    " + "VaultProjDiag_Truncated".Translate(MaxRows);
            }
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), title);
            Text.Font = GameFont.Small;

            float curY = DrawOverview(inRect, 34f, gs);
            curY = DrawSearchBar(inRect, curY);

            // 过滤开关（切换不重建行：只需重算展示索引）
            bool prevOnly = onlyNoteworthy;
            Widgets.CheckboxLabeled(new Rect(0f, curY, inRect.width, 24f),
                "VaultProjDiag_OnlyNoteworthy".Translate(), ref onlyNoteworthy);
            if (onlyNoteworthy != prevOnly)
            {
                scrollPosition = Vector2.zero;
            }
            curY += 28f;

            // 搜索词 / 复选变化后立即重算展示索引（仅一遍行遍历，不重建行数据）
            if (!filterInitialized || searchText != lastSearchText || onlyNoteworthy != lastOnlyNoteworthy)
            {
                lastSearchText = searchText;
                lastOnlyNoteworthy = onlyNoteworthy;
                filterInitialized = true;
                scrollPosition = Vector2.zero;
                RebuildVisibleIndex();
            }

            DrawHeader(new Rect(0f, curY, inRect.width, 22f));
            curY += 22f;

            Rect outRect = new Rect(0f, curY, inRect.width, Mathf.Max(0f, inRect.height - curY));
            if (visibleCount <= 0)
            {
                string emptyKey = rowCount == 0
                    ? "VaultProjDiag_Empty"
                    : (onlyNoteworthy || !searchText.NullOrEmpty() ? "VaultProjDiag_NoMatch" : "VaultProjDiag_EmptyNoteworthy");
                Widgets.NoneLabel(ref curY, outRect.width, emptyKey.Translate());
                return;
            }

            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, visibleCount * RowHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            visibleMinY = scrollPosition.y - RowHeight;
            visibleMaxY = scrollPosition.y + outRect.height + RowHeight;
            for (int i = 0; i < visibleCount; i++)
            {
                float rowY = i * RowHeight;
                if (rowY + RowHeight < visibleMinY || rowY > visibleMaxY)
                {
                    continue; // 视口外的行不解析图标与文本
                }
                DrawRow(rows[visibleIdx[i]], rowY, viewRect.width);
            }
            Widgets.EndScrollView();
        }

        /// <summary>概览区：终端位置、权限、投影统计、版本与待物化队列状态。</summary>
        private float DrawOverview(Rect inRect, float curY, GameComponent_OuterrealmStorage gs)
        {
            Map map = vault != null ? vault.Map : null;
            string mapName = map != null ? map.Parent.LabelCap.ToString() : (string)"VaultProjDiag_MapUnknown".Translate();
            Widgets.Label(new Rect(0f, curY, inRect.width, 22f),
                "VaultProjDiag_VaultInfo".Translate(mapName, vault != null ? vault.Position.ToString() : "-"));
            curY += 22f;

            if (vault != null)
            {
                Widgets.Label(new Rect(0f, curY, inRect.width, 22f), "VaultProjDiag_Permissions".Translate(
                    YesNo(!vault.NoDeposit), YesNo(!vault.NoWithdraw),
                    YesNo(vault.AllowTakeForUse), YesNo(vault.Frozen)));
                curY += 22f;
            }

            Widgets.Label(new Rect(0f, curY, inRect.width, 22f), "VaultProjDiag_Stats".Translate(
                copyRowCount, borrowedRowCount, allowedEntryCount, pendingCount, anomalyCount));
            curY += 22f;

            Widgets.Label(new Rect(0f, curY, inRect.width, 22f), "VaultProjDiag_Versions".Translate(
                gs != null ? gs.Version : -1,
                gs != null ? gs.ReservationVersion : -1,
                (materializeWork ? "VaultProjDiag_QueuePending" : "VaultProjDiag_QueueIdle").Translate()));
            curY += 26f;
            return curY;
        }

        /// <summary>搜索栏：按物品名 / defName / def label 过滤列表，附清除按钮与匹配计数。</summary>
        private float DrawSearchBar(Rect inRect, float curY)
        {
            const float LabelW = 52f;
            const float FieldW = 260f;
            const float ClearW = 60f;
            Widgets.Label(new Rect(0f, curY, LabelW, 26f), "VaultProjDiag_Search".Translate());
            string newSearch = Widgets.TextField(new Rect(LabelW, curY + 3f, FieldW, 24f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
            }
            float clearX = LabelW + FieldW + 6f;
            if (searchText.NullOrEmpty())
            {
                Widgets.Label(new Rect(clearX, curY, ClearW, 26f), ""); // 占位保持排版稳定
            }
            else if (Widgets.ButtonText(new Rect(clearX, curY + 3f, ClearW, 24f), "VaultProjDiag_Clear".Translate()))
            {
                searchText = "";
            }
            float countX = clearX + ClearW + 10f;
            Widgets.Label(new Rect(countX, curY, Mathf.Max(0f, inRect.width - countX), 26f),
                "VaultProjDiag_ShowCount".Translate(visibleCount, rowCount));
            return curY + 30f;
        }

        private static string YesNo(bool value)
        {
            return (value ? "VaultProjDiag_Yes" : "VaultProjDiag_No").Translate();
        }

        /// <summary>列布局：名称列占据图标右侧到投影列之间的全部宽度。</summary>
        private static void LayoutColumns(float width, out float nameX, out float copyX, out float globalX,
            out float reservedX, out float availX, out float stateX, out float nameW)
        {
            nameX = IconW;
            stateX = width - StateColW;
            availX = stateX - AvailColW;
            reservedX = availX - ReservedColW;
            globalX = reservedX - GlobalColW;
            copyX = globalX - CopyColW;
            nameW = Mathf.Max(60f, copyX - nameX - 4f);
        }

        /// <summary>表头：列宽与 DrawRow 完全一致。</summary>
        private void DrawHeader(Rect rect)
        {
            float nameX, copyX, globalX, reservedX, availX, stateX, nameW;
            LayoutColumns(rect.width, out nameX, out copyX, out globalX, out reservedX, out availX, out stateX, out nameW);
            Color prev = GUI.color;
            GUI.color = HeaderColor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(nameX, rect.y, nameW, rect.height), "VaultProjDiag_ColName".Translate());
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(copyX, rect.y, CopyColW, rect.height), "VaultProjDiag_ColCopy".Translate());
            Widgets.Label(new Rect(globalX, rect.y, GlobalColW, rect.height), "VaultProjDiag_ColGlobal".Translate());
            Widgets.Label(new Rect(reservedX, rect.y, ReservedColW, rect.height), "VaultProjDiag_ColReserved".Translate());
            Widgets.Label(new Rect(availX, rect.y, AvailColW, rect.height), "VaultProjDiag_ColAvailable".Translate());
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(stateX, rect.y, StateColW, rect.height), "VaultProjDiag_ColState".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = prev;
        }

        /// <summary>单行绘制：图标 + 名称 + 四个数量列 + 状态文字（异常/提示着色）。</summary>
        private void DrawRow(DiagRow row, float rowY, float width)
        {
            Rect rowRect = new Rect(0f, rowY, width, RowHeight);
            Widgets.DrawHighlightIfMouseover(rowRect);

            float nameX, copyX, globalX, reservedX, availX, stateX, nameW;
            LayoutColumns(width, out nameX, out copyX, out globalX, out reservedX, out availX, out stateX, out nameW);

            OuterrealmVaultUtil.ThingIconSafe(new Rect(4f, rowY + 2f, IconW - 8f, RowHeight - 4f), row.Render);

            Color prev = GUI.color;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(nameX, rowY, nameW, RowHeight), row.Label.Truncate(nameW));
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(copyX, rowY, CopyColW, RowHeight), row.CopyText);
            Widgets.Label(new Rect(globalX, rowY, GlobalColW, RowHeight), row.GlobalText);
            Widgets.Label(new Rect(reservedX, rowY, ReservedColW, RowHeight), row.ReservedText);
            Widgets.Label(new Rect(availX, rowY, AvailColW, RowHeight), row.AvailableText);
            Text.Anchor = TextAnchor.MiddleLeft;
            if (row.Severity == SevAnomaly)
            {
                GUI.color = AnomalyColor;
            }
            else if (row.Severity == SevNote)
            {
                GUI.color = NoteColor;
            }
            Widgets.Label(new Rect(stateX, rowY, StateColW, RowHeight), row.StateText.Truncate(StateColW));
            GUI.color = prev;
            Text.Anchor = TextAnchor.UpperLeft;

            TooltipHandler.TipRegion(rowRect, row.Tooltip);
        }

        // ── 列表筛选 ────────────────────────────────────────────────────────────

        /// <summary>按当前搜索词与「仅显示需要注意的行」重算展示索引（只遍历行集，不重建行数据、不分配）。</summary>
        private void RebuildVisibleIndex()
        {
            visibleCount = 0;
            if (visibleIdx.Length < rowCount)
            {
                Array.Resize(ref visibleIdx, Math.Max(rowCount, visibleIdx.Length * 2));
            }
            string query = searchText.NullOrEmpty() ? null : searchText.Trim().ToLowerInvariant();
            int queryLength = query != null ? query.Length : 0;
            for (int i = 0; i < rowCount; i++)
            {
                DiagRow row = rows[i];
                if (onlyNoteworthy && row.Severity == SevNormal)
                {
                    continue;
                }
                if (queryLength > 0 && !row.SearchKey.Contains(query))
                {
                    continue;
                }
                visibleIdx[visibleCount++] = i;
            }
        }

        // ── 行集重建 ────────────────────────────────────────────────────────────

        /// <summary>按全局版本号 + 1 秒节流重建；真实时间只用于观察临时变化（纯 UI 行为）。</summary>
        private void RebuildIfNeeded(GameComponent_OuterrealmStorage gs)
        {
            int version = gs != null ? gs.Version : -1;
            int reservationVersion = gs != null ? gs.ReservationVersion : -1;
            float now = Time.realtimeSinceStartup;
            if (version == builtVersion && reservationVersion == builtReservationVersion
                && now - lastBuiltRealtime < RefreshIntervalSeconds)
            {
                return;
            }
            builtVersion = version;
            builtReservationVersion = reservationVersion;
            lastBuiltRealtime = now;
            Rebuild(gs);
        }

        private void Rebuild(GameComponent_OuterrealmStorage gs)
        {
            rowCount = 0;
            noteRowCount = 0;
            seq = 0;
            copyRowCount = 0;
            borrowedRowCount = 0;
            allowedEntryCount = 0;
            pendingCount = 0;
            anomalyCount = 0;
            truncated = false;
            materializeWork = false;

            if (vault == null || vault.view == null)
            {
                visibleCount = 0;
                filterInitialized = false;
                return;
            }
            materializeWork = vault.view.HasMaterializeWork;

            // 1. 视图当前实际存在的投影副本（诊断主体）
            seenEntries.Clear();
            List<Thing> copies = vault.view.InnerListForReading;
            for (int i = 0; i < copies.Count; i++)
            {
                Thing copy = copies[i];
                if (copy == null || copy.Destroyed)
                {
                    continue;
                }
                OuterrealmEntry entry = vault.view.GetEntryOf(copy);
                if (entry != null)
                {
                    seenEntries.Add(entry);
                }
                if (rowCount >= MaxRows)
                {
                    truncated = true;
                    break;
                }
                BuildCopyRow(RentRow(), copy, entry, gs);
                copyRowCount++;
            }

            // 2. 借出中的真实实物（已离开视图列表，但仍是本终端的租约）
            if (!truncated)
            {
                foreach (Thing lend in vault.view.BorrowedCopiesForReading)
                {
                    if (lend == null || lend.Destroyed)
                    {
                        continue;
                    }
                    if (rowCount >= MaxRows)
                    {
                        truncated = true;
                        break;
                    }
                    BuildBorrowedRow(RentRow(), lend, vault.view.GetEntryOf(lend), gs);
                    borrowedRowCount++;
                }
            }

            // 3. 存储清单允许、有库存但尚未物化的条目（诊断"物化是否卡住"）
            if (!truncated && gs != null)
            {
                List<OuterrealmEntry> entries = gs.EntriesForReading;
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry entry = entries[i];
                    if (entry == null || entry.Proto == null || entry.Count <= 0)
                    {
                        continue;
                    }
                    if (!vault.CanShow(entry.Proto))
                    {
                        continue;
                    }
                    allowedEntryCount++;
                    if (seenEntries.Contains(entry))
                    {
                        continue;
                    }
                    pendingCount++;
                    if (rowCount >= MaxRows)
                    {
                        truncated = true;
                        break;
                    }
                    BuildPendingRow(RentRow(), entry, gs);
                }
            }

            // 异常 / 需要注意的行置顶（严重级降序，构建序升序保证稳定）
            if (rowCount > 1)
            {
                Array.Sort(rows, 0, rowCount, DiagRowComparer.Instance);
            }
            for (int i = 0; i < rowCount; i++)
            {
                byte severity = rows[i].Severity;
                if (severity == SevNormal)
                {
                    break; // 已按严重级降序排列
                }
                noteRowCount++;
                if (severity == SevAnomaly)
                {
                    anomalyCount++;
                }
            }

            // 行集变了，展示索引必须同步重算（搜索词保持不变）
            filterInitialized = true;
            lastSearchText = searchText;
            lastOnlyNoteworthy = onlyNoteworthy;
            RebuildVisibleIndex();
        }

        private DiagRow RentRow()
        {
            if (rowCount >= rows.Length)
            {
                Array.Resize(ref rows, rows.Length * 2);
            }
            DiagRow row = rows[rowCount];
            if (row == null)
            {
                row = new DiagRow();
                rows[rowCount] = row;
            }
            else
            {
                row.Reset();
            }
            row.Seq = seq++;
            rowCount++;
            return row;
        }

        /// <summary>视图副本行：投影堆数、全局总量、预留与可用量，并对异常组合着色。</summary>
        private void BuildCopyRow(DiagRow row, Thing copy, OuterrealmEntry entry, GameComponent_OuterrealmStorage gs)
        {
            row.Copy = copy;
            row.Render = copy;
            row.Label = OuterrealmVaultUtil.SafeLabelCapNoCount(copy);
            SetSearchKey(row, copy);
            row.CopyStack = copy.stackCount;
            row.CopyText = copy.stackCount.ToString("N0");
            bool borrowed = vault.view.IsBorrowed(copy);
            row.Borrowed = borrowed;

            if (entry == null)
            {
                // 视图里存在但找不到对应全局条目：孤儿投影
                row.Severity = SevAnomaly;
                row.StateText = BorrowedSuffix("VaultProjDiag_StateOrphanCopy".Translate(), borrowed);
                row.Tooltip = "VaultProjDiag_TipOrphanCopy".Translate(copy.thingIDNumber);
                return;
            }

            ApplyEntryNumbers(row, entry, gs);
            row.ExpectedStack = ExpectedCopyStack(entry);
            row.Severity = ResolveCopySeverity(row, entry, borrowed, out string state);
            row.StateText = BorrowedSuffix(state, borrowed);
            BuildEntryTooltip(row, entry, copy);
        }

        /// <summary>借出行：真实实物已从全局扣减，仍以租约驻留在本终端存储格。</summary>
        private void BuildBorrowedRow(DiagRow row, Thing lend, OuterrealmEntry entry, GameComponent_OuterrealmStorage gs)
        {
            row.Render = lend;
            row.Label = OuterrealmVaultUtil.SafeLabelCapNoCount(lend);
            SetSearchKey(row, lend);
            row.CopyStack = lend.stackCount;
            row.CopyText = lend.stackCount.ToString("N0");
            row.Borrowed = true;
            row.Severity = SevNote;
            row.StateText = "VaultProjDiag_StateBorrowed".Translate();
            if (entry != null)
            {
                ApplyEntryNumbers(row, entry, gs);
                row.ExpectedStack = ExpectedCopyStack(entry);
                BuildEntryTooltip(row, entry, lend);
            }
            else
            {
                row.Tooltip = "VaultProjDiag_TipOrphanCopy".Translate(lend.thingIDNumber);
            }
        }

        /// <summary>待物化行：存储清单允许且有库存，但本建筑视图还没有对应投影。</summary>
        private void BuildPendingRow(DiagRow row, OuterrealmEntry entry, GameComponent_OuterrealmStorage gs)
        {
            row.Render = entry.Proto;
            row.Label = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto);
            SetSearchKey(row, entry.Proto);
            row.ExpectedStack = ExpectedCopyStack(entry);
            ApplyEntryNumbers(row, entry, gs);
            row.Severity = SevNote;
            row.StateText = vault.Frozen
                ? "VaultProjDiag_StatePendingFrozen".Translate()
                : "VaultProjDiag_StatePending".Translate();
            BuildEntryTooltip(row, entry, null);
        }

        /// <summary>构建搜索键（小写）：显示名 + defName + def label，随行建立一次，过滤时零分配匹配。</summary>
        private static void SetSearchKey(DiagRow row, Thing thing)
        {
            string key = row.Label;
            ThingDef def = thing != null ? thing.def : null;
            if (def != null)
            {
                key = key + " " + def.defName + " " + def.label;
            }
            row.SearchKey = key.ToLowerInvariant();
        }

        private void ApplyEntryNumbers(DiagRow row, OuterrealmEntry entry, GameComponent_OuterrealmStorage gs)
        {
            row.Entry = entry;
            row.GlobalCount = entry.Count;
            row.GlobalText = entry.Count.ToString("N0");
            long reserved = gs != null ? gs.ReservedCountOf(entry) : 0L;
            row.Reserved = reserved;
            row.ReservedText = reserved.ToString("N0");
            row.Available = Math.Max(0L, entry.Count - reserved);
            row.AvailableText = row.Available.ToString("N0");
        }

        /// <summary>副本行的严重级判定：筛选已禁止 / 空条目残留 / 投影堆数不符预期 / 正常。</summary>
        private byte ResolveCopySeverity(DiagRow row, OuterrealmEntry entry, bool borrowed, out string state)
        {
            byte severity;
            if (!vault.CanShow(entry.Proto))
            {
                state = "VaultProjDiag_StateDenied".Translate();
                severity = SevAnomaly;
            }
            else if (entry.Count <= 0)
            {
                state = "VaultProjDiag_StateEmptyEntry".Translate();
                severity = SevAnomaly;
            }
            else if (row.CopyStack != row.ExpectedStack)
            {
                state = "VaultProjDiag_StateMismatch".Translate();
                severity = SevNote;
            }
            else
            {
                state = "VaultProjDiag_StateNormal".Translate();
                severity = SevNormal;
            }
            if (vault.Frozen)
            {
                severity = SevAnomaly;
                state = state + "/" + "VaultProjDiag_StateFrozen".Translate();
            }
            else if (vault.NoWithdraw && !vault.AllowTakeForUse)
            {
                if (severity < SevNote)
                {
                    severity = SevNote;
                }
                state = state + "/" + "VaultProjDiag_StateNoWithdraw".Translate();
            }
            if (borrowed && severity < SevNote)
            {
                severity = SevNote;
            }
            return severity;
        }

        private static string BorrowedSuffix(string state, bool borrowed)
        {
            return borrowed ? state + "/" + (string)"VaultProjDiag_StateBorrowed".Translate() : state;
        }

        /// <summary>投影副本的预期堆数：min(全局数量, def.stackLimit)（readme §3）。</summary>
        private static int ExpectedCopyStack(OuterrealmEntry entry)
        {
            Thing proto = entry != null ? entry.Proto : null;
            int limit = proto != null && proto.def != null ? Math.Max(1, proto.def.stackLimit) : 1;
            long count = entry != null ? entry.Count : 0L;
            return (int)Math.Min(count, limit);
        }

        private void BuildEntryTooltip(DiagRow row, OuterrealmEntry entry, Thing thing)
        {
            StringBuilder sb = tipBuilder;
            sb.Length = 0;
            Thing proto = entry.Proto;
            if (proto != null && proto.def != null)
            {
                sb.Append("VaultProjDiag_TipDef".Translate(proto.def.defName)).Append('\n');
            }
            sb.Append("VaultProjDiag_TipKey".Translate(entry.Key.ToString())).Append('\n');
            sb.Append("VaultProjDiag_TipCopyExpected".Translate(row.ExpectedStack,
                proto != null && proto.def != null ? proto.def.stackLimit : 0)).Append('\n');
            sb.Append("VaultProjDiag_TipAmounts".Translate(
                entry.Count.ToString("N0"), row.Reserved.ToString("N0"), row.Available.ToString("N0")));
            if (thing != null)
            {
                sb.Append('\n').Append((row.Copy == null
                    ? "VaultProjDiag_TipRealId"
                    : "VaultProjDiag_TipCopyId").Translate(thing.thingIDNumber));
            }
            row.Tooltip = sb.ToString();
        }

        // ── 行数据结构 ──────────────────────────────────────────────────────────

        private sealed class DiagRow
        {
            public int Seq;
            public byte Severity;
            public OuterrealmEntry Entry;
            public Thing Copy;
            public Thing Render;
            public string Label = "";
            public string SearchKey = "";
            public string Tooltip = "";
            public string StateText = "";
            public string CopyText = "-";
            public string GlobalText = "-";
            public string ReservedText = "-";
            public string AvailableText = "-";
            public long GlobalCount;
            public long Reserved;
            public long Available;
            public int CopyStack;
            public int ExpectedStack;
            public bool Borrowed;

            public void Reset()
            {
                Seq = 0;
                Severity = SevNormal;
                Entry = null;
                Copy = null;
                Render = null;
                Label = "";
                SearchKey = "";
                Tooltip = "";
                StateText = "";
                CopyText = "-";
                GlobalText = "-";
                ReservedText = "-";
                AvailableText = "-";
                GlobalCount = 0L;
                Reserved = 0L;
                Available = 0L;
                CopyStack = 0;
                ExpectedStack = 0;
                Borrowed = false;
            }
        }

        private sealed class DiagRowComparer : IComparer<DiagRow>
        {
            public static readonly DiagRowComparer Instance = new DiagRowComparer();

            public int Compare(DiagRow a, DiagRow b)
            {
                int bySeverity = b.Severity.CompareTo(a.Severity); // 严重级降序
                return bySeverity != 0 ? bySeverity : a.Seq.CompareTo(b.Seq);
            }
        }
    }
}
