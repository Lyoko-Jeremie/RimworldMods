using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆备份云总览窗口。
    /// </summary>
    public class Dialog_ArtificialMaidBackupCloud : Window
    {
        private readonly Map targetMap;
        private readonly IntVec3 targetPosition;
        private Vector2 scrollPosition;
        private string selectedSerialNumber;
        private readonly List<ArtificialMaidBackupCloud.MaidRegistryRecord> sortedRecords =
            new List<ArtificialMaidBackupCloud.MaidRegistryRecord>();

        private const float RowHeight = 54f;
        private const float DetailWidth = 310f;

        public override Vector2 InitialSize => new Vector2(980f, 680f);

        public Dialog_ArtificialMaidBackupCloud(Map targetMap, IntVec3 targetPosition)
        {
            this.targetMap = targetMap;
            this.targetPosition = targetPosition;
            doCloseX = true;
            doCloseButton = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            forcePause = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            ArtificialMaidBackupCloud.RequestAudit();
            RefreshRecords();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 35f),
                "ArtificialMaidBackupCloudWindowTitle".Translate());
            Text.Font = GameFont.Small;

            Rect summaryRect = new Rect(inRect.x, inRect.y + 42f, inRect.width, 46f);
            DrawSummary(summaryRect);

            float bodyY = summaryRect.yMax + 10f;
            Rect listRect = new Rect(inRect.x, bodyY, inRect.width - DetailWidth - 12f,
                inRect.yMax - bodyY - 45f);
            Rect detailRect = new Rect(listRect.xMax + 12f, bodyY, DetailWidth,
                listRect.height);

            DrawList(listRect);
            DrawDetails(detailRect);

            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 35f, 150f, 35f),
                    "ArtificialMaidBackupCloudRescan".Translate()))
            {
                ArtificialMaidBackupCloud.RequestAudit();
                RefreshRecords();
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - 150f, inRect.yMax - 35f, 150f, 35f),
                    "CloseButton".Translate()))
            {
                Close();
            }
        }

        private void RefreshRecords()
        {
            sortedRecords.Clear();
            IReadOnlyList<ArtificialMaidBackupCloud.MaidRegistryRecord> records =
                ArtificialMaidBackupCloud.RegistryForReading;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i] != null)
                {
                    sortedRecords.Add(records[i]);
                }
            }

            sortedRecords.Sort(CompareRecords);
            if (string.IsNullOrEmpty(selectedSerialNumber) && sortedRecords.Count > 0)
            {
                selectedSerialNumber = sortedRecords[0].SerialNumber;
            }
        }

        private static int CompareRecords(ArtificialMaidBackupCloud.MaidRegistryRecord left,
            ArtificialMaidBackupCloud.MaidRegistryRecord right)
        {
            int stateComparison = GetPriority(left).CompareTo(GetPriority(right));
            if (stateComparison != 0)
            {
                return stateComparison;
            }

            return string.Compare(left.Label, right.Label, StringComparison.CurrentCulture);
        }

        private static int GetPriority(ArtificialMaidBackupCloud.MaidRegistryRecord record)
        {
            switch (record.ObjectState)
            {
                case ArtificialMaidBackupCloud.MaidObjectState.Unrooted:
                case ArtificialMaidBackupCloud.MaidObjectState.Missing:
                case ArtificialMaidBackupCloud.MaidObjectState.Destroyed:
                case ArtificialMaidBackupCloud.MaidObjectState.Discarded:
                    return 0;
                case ArtificialMaidBackupCloud.MaidObjectState.Dead:
                    return 1;
            }

            switch (record.BackupState)
            {
                case ArtificialMaidBackupCloud.MaidBackupState.Failed:
                case ArtificialMaidBackupCloud.MaidBackupState.Stale:
                case ArtificialMaidBackupCloud.MaidBackupState.Corrupted:
                case ArtificialMaidBackupCloud.MaidBackupState.NeverAttempted:
                    return 2;
                default:
                    return 3;
            }
        }

        private void DrawSummary(Rect rect)
        {
            int valid = 0;
            int attention = 0;
            for (int i = 0; i < sortedRecords.Count; i++)
            {
                ArtificialMaidBackupCloud.MaidRegistryRecord record = sortedRecords[i];
                if (record.BackupState == ArtificialMaidBackupCloud.MaidBackupState.Valid ||
                    record.BackupState == ArtificialMaidBackupCloud.MaidBackupState.ValidPartial ||
                    record.BackupState == ArtificialMaidBackupCloud.MaidBackupState.Stale)
                {
                    valid++;
                }

                if (GetPriority(record) < 3)
                {
                    attention++;
                }
            }

            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(10f);
            Widgets.Label(inner,
                "ArtificialMaidBackupCloudSummary".Translate(sortedRecords.Count, valid, attention));
        }

        private void DrawList(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(6f);
            Rect header = new Rect(inner.x, inner.y, inner.width - 16f, 28f);
            DrawColumns(header, "ArtificialMaidBackupCloudColumnMaid".Translate(),
                "ArtificialMaidBackupCloudColumnLocation".Translate(),
                "ArtificialMaidBackupCloudColumnObjectState".Translate(),
                "ArtificialMaidBackupCloudColumnBackup".Translate(), true);

            Rect outRect = new Rect(inner.x, header.yMax, inner.width, inner.height - header.height);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f,
                Math.Max(outRect.height, sortedRecords.Count * RowHeight));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int i = 0; i < sortedRecords.Count; i++)
            {
                ArtificialMaidBackupCloud.MaidRegistryRecord record = sortedRecords[i];
                Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                if (selectedSerialNumber == record.SerialNumber)
                {
                    Widgets.DrawHighlightSelected(row);
                }
                else if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                if (Widgets.ButtonInvisible(row))
                {
                    selectedSerialNumber = record.SerialNumber;
                }

                string maidLabel = record.Label + "\n" + record.SerialNumber;
                DrawColumns(row.ContractedBy(4f), maidLabel,
                    TranslateLocation(record),
                    TranslateObjectState(record.ObjectState),
                    TranslateBackupState(record.BackupState), false);
                Widgets.DrawLineHorizontal(row.x, row.yMax - 1f, row.width);
            }

            Widgets.EndScrollView();
        }

        private static void DrawColumns(Rect rect, string maid, string location, string objectState,
            string backupState, bool header)
        {
            float maidWidth = rect.width * 0.31f;
            float locationWidth = rect.width * 0.25f;
            float stateWidth = rect.width * 0.22f;
            Rect maidRect = new Rect(rect.x, rect.y, maidWidth, rect.height);
            Rect locationRect = new Rect(maidRect.xMax, rect.y, locationWidth, rect.height);
            Rect stateRect = new Rect(locationRect.xMax, rect.y, stateWidth, rect.height);
            Rect backupRect = new Rect(stateRect.xMax, rect.y, rect.xMax - stateRect.xMax, rect.height);

            Text.Anchor = header ? TextAnchor.MiddleLeft : TextAnchor.MiddleLeft;
            if (!header)
            {
                Text.Font = GameFont.Tiny;
            }

            Widgets.Label(maidRect, maid);
            Widgets.Label(locationRect, location);
            Widgets.Label(stateRect, objectState);
            Widgets.Label(backupRect, backupState);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawDetails(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(12f);
            ArtificialMaidBackupCloud.MaidRegistryRecord record = GetSelectedRecord();
            if (record == null)
            {
                Widgets.Label(inner, "ArtificialMaidBackupCloudNoRegistry".Translate());
                return;
            }

            float y = inner.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, y, inner.width, 32f), record.Label);
            Text.Font = GameFont.Small;
            y += 38f;

            DrawDetailLine(ref y, inner, "ArtificialMaidSerialNumber".Translate(), record.SerialNumber);
            DrawDetailLine(ref y, inner, "ArtificialMaidBackupCloudObjectStateLabel".Translate(),
                TranslateObjectState(record.ObjectState));
            DrawDetailLine(ref y, inner, "ArtificialMaidBackupCloudLocationLabel".Translate(),
                TranslateLocation(record));
            DrawDetailLine(ref y, inner, "ArtificialMaidBackupCloudLastSeenLabel".Translate(),
                FormatTick(record.LastSeenTick));
            DrawDetailLine(ref y, inner, "ArtificialMaidBackupCloudBackupStateLabel".Translate(),
                TranslateBackupState(record.BackupState));
            DrawDetailLine(ref y, inner, "ArtificialMaidBackupCloudLastBackupLabel".Translate(),
                FormatTick(record.LastSuccessfulBackupTick));

            if (!string.IsNullOrEmpty(record.LastBackupError))
            {
                Rect errorRect = new Rect(inner.x, y + 4f, inner.width, 72f);
                Widgets.Label(errorRect,
                    "ArtificialMaidBackupCloudErrorLabel".Translate(record.LastBackupStage,
                        record.LastBackupError.Truncate(180)));
                y = errorRect.yMax + 6f;
            }
            else
            {
                y += 10f;
            }

            string reason;
            ArtificialMaidBackupCloud.RecoveryAction action =
                ArtificialMaidBackupCloud.GetRecoveryAction(record.SerialNumber, out reason);
            string actionLabel = GetActionLabel(action);
            Rect actionRect = new Rect(inner.x, y, inner.width, 36f);
            if (action == ArtificialMaidBackupCloud.RecoveryAction.None)
            {
                Widgets.DrawHighlight(actionRect);
                Widgets.Label(actionRect.ContractedBy(6f), reason ?? "ArtificialMaidBackupCloudNoBackup".Translate());
            }
            else if (Widgets.ButtonText(actionRect, actionLabel))
            {
                ConfirmRecovery(record, action);
            }

            y = actionRect.yMax + 8f;
            Pawn pawn = record.LastKnownPawn;
            bool canBackup = pawn != null && !pawn.Dead && !pawn.Destroyed && !pawn.Discarded;
            Rect backupRect = new Rect(inner.x, y, inner.width, 34f);
            if (!canBackup)
            {
                GUI.color = Color.gray;
            }

            if (Widgets.ButtonText(backupRect, "ArtificialMaidBackupCloudBackupNow".Translate(), active: canBackup) &&
                canBackup)
            {
                if (ArtificialMaidBackupCloud.RequestBackup(pawn, true, out string backupReason))
                {
                    Messages.Message("ArtificialMaidBackupCloudBackupSucceeded".Translate(record.Label),
                        MessageTypeDefOf.PositiveEvent);
                }
                else
                {
                    Messages.Message(backupReason ?? "ArtificialMaidBackupCloudRestoreFailed".Translate(),
                        MessageTypeDefOf.RejectInput);
                }

                RefreshRecords();
            }

            GUI.color = Color.white;
            y = backupRect.yMax + 8f;

            if (pawn != null && pawn.Spawned &&
                Widgets.ButtonText(new Rect(inner.x, y, inner.width, 34f),
                    "ArtificialMaidBackupCloudLocate".Translate()))
            {
                CameraJumper.TryJumpAndSelect(pawn);
            }
        }

        private static void DrawDetailLine(ref float y, Rect inner, string label, string value)
        {
            Rect labelRect = new Rect(inner.x, y, 112f, 25f);
            Rect valueRect = new Rect(labelRect.xMax, y, inner.xMax - labelRect.xMax, 25f);
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;
            Widgets.Label(valueRect, value ?? "-");
            y += 27f;
        }

        private void ConfirmRecovery(ArtificialMaidBackupCloud.MaidRegistryRecord record,
            ArtificialMaidBackupCloud.RecoveryAction action)
        {
            string text = "ArtificialMaidBackupCloudConfirmRecovery".Translate(
                record.Label, GetActionLabel(action), TranslateBackupState(record.BackupState));
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, delegate
            {
                if (ArtificialMaidBackupCloud.TryRecoverOrRestore(record.SerialNumber,
                        targetMap, targetPosition, out Pawn restoredPawn, out string reason))
                {
                    Messages.Message("ArtificialMaidBackupCloudRecoveredMessage".Translate(restoredPawn.LabelShort),
                        restoredPawn, MessageTypeDefOf.PositiveEvent);
                    ArtificialMaidBackupCloud.RequestAudit();
                    RefreshRecords();
                }
                else
                {
                    Messages.Message(reason ?? "ArtificialMaidBackupCloudRestoreFailed".Translate(),
                        MessageTypeDefOf.RejectInput);
                }
            }, destructive: false));
        }

        private ArtificialMaidBackupCloud.MaidRegistryRecord GetSelectedRecord()
        {
            for (int i = 0; i < sortedRecords.Count; i++)
            {
                if (sortedRecords[i].SerialNumber == selectedSerialNumber)
                {
                    return sortedRecords[i];
                }
            }

            return null;
        }

        private static string GetActionLabel(ArtificialMaidBackupCloud.RecoveryAction action)
        {
            switch (action)
            {
                case ArtificialMaidBackupCloud.RecoveryAction.RecallOriginal:
                    return "ArtificialMaidBackupCloudRecallOriginal".Translate();
                case ArtificialMaidBackupCloud.RecoveryAction.ResurrectOriginal:
                    return "ArtificialMaidBackupCloudResurrectOriginal".Translate();
                case ArtificialMaidBackupCloud.RecoveryAction.RebuildFromBackup:
                    return "ArtificialMaidBackupCloudRebuild".Translate();
                default:
                    return "ArtificialMaidBackupCloudUnavailable".Translate();
            }
        }

        private static string TranslateObjectState(ArtificialMaidBackupCloud.MaidObjectState state)
        {
            return ("ArtificialMaidBackupCloudObjectState_" + state).Translate();
        }

        private static string TranslateBackupState(ArtificialMaidBackupCloud.MaidBackupState state)
        {
            return ("ArtificialMaidBackupCloudBackupState_" + state).Translate();
        }

        private static string TranslateLocation(ArtificialMaidBackupCloud.MaidRegistryRecord record)
        {
            if (string.IsNullOrEmpty(record.LastKnownLocation))
            {
                return "-";
            }

            if (record.LastKnownLocation == "WorldPawns" ||
                record.LastKnownLocation == "Unrooted" ||
                record.LastKnownLocation == "Missing" ||
                record.LastKnownLocation == "Destroyed" ||
                record.LastKnownLocation == "Discarded" ||
                record.LastKnownLocation == "SerialConflict")
            {
                return ("ArtificialMaidBackupCloudLocation_" + record.LastKnownLocation).Translate();
            }

            return record.LastKnownLocation;
        }

        private static string FormatTick(int tick)
        {
            if (tick < 0)
            {
                return "ArtificialMaidBackupCloudNever".Translate();
            }

            long absTicks = GenDate.TickGameToAbs(tick);
            return GenDate.DateReadoutStringAt(absTicks, Vector2.zero);
        }
    }
}
