using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 「立即解锁可用研究」管理界面。
    /// 打开时暂停游戏（forcePause），展示各来源 Mod 的研究条目与详细信息，
    /// 支持按来源分组解锁、忽略前置条件开关，并通过分帧解锁避免一次性大量解锁导致界面冻结。
    /// </summary>
    public class Dialog_InstantResearchUnlock : Window
    {
        public override Vector2 InitialSize => new Vector2(1150f, 750f);

        // 每帧用于解锁工作的耗时预算（毫秒），保证界面不冻结
        private const float FrameTimeBudgetMs = 6f;
        private const float RowHeight = 28f;
        private const float GroupHeaderHeight = 30f;

        private readonly List<IResearchUnlockProvider> providers;

        // 展示数据（每次收集重建，仅在界面打开期间有效）
        private List<IResearchUnlockProvider> activeProviders;
        private List<List<ResearchUnlockEntry>> groupedEntries;
        private List<ResearchUnlockEntry> entries;
        private Vector2 scrollPos;
        private bool showOnlyAvailable = true;
        private bool ignorePrerequisites;
        private ResearchUnlockEntry expandedEntry;

        // 分帧执行状态
        private bool isRunning;
        private Queue<ResearchUnlockEntry> pendingQueue;
        private HashSet<string> processedKeys;
        private Dictionary<IResearchUnlockProvider, int> queuedByProvider;
        private Dictionary<IResearchUnlockProvider, int> completedByProvider;
        private int totalQueued;
        private int completedCount;
        private bool rescanPending;
        private int rescanIndex;
        private bool rescanEnqueuedAny;
        private readonly Stopwatch frameWatch = new Stopwatch();

        public Dialog_InstantResearchUnlock()
        {
            // 打开界面时暂停游戏，分帧解锁期间进度可预期且界面流畅
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnCancel = true;
            this.draggable = false;

            providers = new List<IResearchUnlockProvider>
            {
                new VanillaResearchProvider(),
                new RavenResearchProvider(),
                new RimatomicsResearchProvider()
            };

            CollectAll();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 解锁执行期间禁止 ESC 与右上角 X 关闭，避免意外中断
            this.closeOnCancel = !isRunning;
            this.doCloseX = !isRunning;

            if (isRunning)
            {
                TickUnlock();
                DrawProgress(inRect);
                return;
            }

            DrawMain(inRect);
        }

        // ---------- 数据收集 ----------

        private void CollectAll()
        {
            activeProviders = new List<IResearchUnlockProvider>();
            groupedEntries = new List<List<ResearchUnlockEntry>>();
            entries = new List<ResearchUnlockEntry>();

            for (int i = 0; i < providers.Count; i++)
            {
                IResearchUnlockProvider provider = providers[i];
                if (!provider.IsActive)
                {
                    continue;
                }

                List<ResearchUnlockEntry> group = provider.CollectEntries(ignorePrerequisites);
                activeProviders.Add(provider);
                groupedEntries.Add(group);
                entries.AddRange(group);
            }

            // 默认勾选全部当前可解锁项
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].State == ResearchEntryState.Available)
                {
                    entries[i].IsSelected = true;
                }
            }
        }

        private int CountAvailable()
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].State == ResearchEntryState.Available)
                {
                    count++;
                }
            }
            return count;
        }

        private int CountSelected()
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsSelected)
                {
                    count++;
                }
            }
            return count;
        }

        // ---------- 分帧解锁执行器 ----------

        private void StartUnlock()
        {
            pendingQueue = new Queue<ResearchUnlockEntry>();
            processedKeys = new HashSet<string>();
            queuedByProvider = new Dictionary<IResearchUnlockProvider, int>();
            completedByProvider = new Dictionary<IResearchUnlockProvider, int>();
            totalQueued = 0;
            completedCount = 0;
            rescanPending = false;
            rescanIndex = 0;
            rescanEnqueuedAny = false;

            for (int i = 0; i < entries.Count; i++)
            {
                ResearchUnlockEntry entry = entries[i];
                if (entry.IsSelected && entry.State == ResearchEntryState.Available)
                {
                    EnqueueEntry(entry);
                }
            }

            if (totalQueued == 0)
            {
                Messages.Message("OmniCrafter_Research_NoSelection".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            isRunning = true;
        }

        private void EnqueueEntry(ResearchUnlockEntry entry)
        {
            pendingQueue.Enqueue(entry);
            totalQueued++;

            int queued;
            queuedByProvider.TryGetValue(entry.Provider, out queued);
            queuedByProvider[entry.Provider] = queued + 1;
        }

        /// <summary>
        /// 每 UI 帧执行一部分解锁工作：先处理队列，队列清空后启动连锁重扫，
        /// 直到一轮重扫后仍无新可解锁项为止。
        /// </summary>
        private void TickUnlock()
        {
            frameWatch.Restart();

            while (frameWatch.ElapsedMilliseconds < FrameTimeBudgetMs)
            {
                if (pendingQueue.Count > 0)
                {
                    ResearchUnlockEntry entry = pendingQueue.Dequeue();
                    string key = EntryKey(entry);
                    if (!processedKeys.Add(key))
                    {
                        continue;
                    }

                    bool ok = false;
                    try
                    {
                        ok = entry.Provider.TryComplete(entry);
                    }
                    catch (Exception exception)
                    {
                        Log.Error("[FullyAutomaticOmniCrafter] 解锁研究时发生异常：\n" + exception);
                    }

                    if (ok)
                    {
                        completedCount++;

                        int done;
                        completedByProvider.TryGetValue(entry.Provider, out done);
                        completedByProvider[entry.Provider] = done + 1;
                    }
                }
                else if (rescanPending)
                {
                    // 连锁重扫：每帧最多扫描一个 Provider，避免扫描本身造成卡顿
                    IResearchUnlockProvider provider = providers[rescanIndex];
                    rescanIndex++;

                    if (provider.IsActive)
                    {
                        List<ResearchUnlockEntry> fresh = provider.CollectEntries(ignorePrerequisites);
                        bool enqueuedInGroup = false;
                        for (int i = 0; i < fresh.Count; i++)
                        {
                            ResearchUnlockEntry freshEntry = fresh[i];
                            if (freshEntry.State == ResearchEntryState.Available && !processedKeys.Contains(EntryKey(freshEntry)))
                            {
                                EnqueueEntry(freshEntry);
                                enqueuedInGroup = true;
                            }
                        }

                        if (enqueuedInGroup)
                        {
                            rescanEnqueuedAny = true;
                        }
                    }

                    if (rescanIndex >= providers.Count)
                    {
                        rescanPending = false;

                        // 仅当队列为空且本轮重扫没有任何新入队时才判定解锁完成；
                        // 否则继续处理队列，处理完后开启下一轮重扫（保证连锁解锁不漏项）。
                        if (pendingQueue.Count == 0 && !rescanEnqueuedAny)
                        {
                            // 一轮重扫后仍无新可解锁项，解锁全部完成
                            FinishUnlock();
                            return;
                        }

                        rescanEnqueuedAny = false;
                    }
                }
                else
                {
                    // 队列清空，启动一轮连锁重扫。
                    // 注意：连锁解锁会继续解锁「勾选起点」之后新变为可解锁的项目（与旧版 do-while 行为一致），
                    // 因此进度总数 totalQueued 会随重扫动态增长。
                    rescanPending = true;
                    rescanIndex = 0;
                }
            }
        }

        private static string EntryKey(ResearchUnlockEntry entry)
        {
            return entry.Provider.GetType().Name + "|" + entry.Def.defName;
        }

        private void FinishUnlock()
        {
            isRunning = false;
            CollectAll();

            if (completedCount > 0)
            {
                List<string> parts = new List<string>();
                for (int i = 0; i < activeProviders.Count; i++)
                {
                    IResearchUnlockProvider provider = activeProviders[i];
                    int done;
                    completedByProvider.TryGetValue(provider, out done);
                    if (done > 0)
                    {
                        parts.Add(provider.GroupNameKey.Translate() + " " + done);
                    }
                }

                string message = "OmniCrafter_Research_Done".Translate(completedCount);
                if (parts.Count > 0)
                {
                    message += "\n" + string.Join(" · ", parts.ToArray());
                }

                Messages.Message(message, MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message("OmniCrafter_Research_NoAvailable".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        // ---------- 界面绘制 ----------

        private void DrawMain(Rect inRect)
        {
            // 标题
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "OmniCrafter_Research_Title".Translate());

            // 统计栏
            Text.Font = GameFont.Small;
            string stat = "OmniCrafter_Research_StatSummary".Translate(entries.Count, CountAvailable(), CountSelected());
            Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 24f), stat);

            // 工具栏
            float toolbarY = inRect.y + 70f;
            DrawToolbar(new Rect(inRect.x, toolbarY, inRect.width, 30f));

            // 列表
            Rect listRect = new Rect(inRect.x, toolbarY + 36f, inRect.width, inRect.height - (toolbarY + 36f) - 52f);
            DrawEntryList(listRect);

            // 底部按钮
            Rect bottomRect = new Rect(inRect.x, listRect.yMax + 10f, inRect.width, 40f);
            int selectedCount = CountSelected();
            Rect unlockButtonRect = new Rect(bottomRect.xMax - 260f, bottomRect.y, 200f, 36f);
            if (Widgets.ButtonText(unlockButtonRect, "OmniCrafter_Research_UnlockSelected".Translate(selectedCount), true, true, selectedCount > 0))
            {
                StartUnlock();
            }

            Rect closeButtonRect = new Rect(bottomRect.xMax - 90f, bottomRect.y, 80f, 36f);
            if (Widgets.ButtonText(closeButtonRect, "OmniCrafter_Research_Close".Translate()))
            {
                Close();
            }
        }

        private void DrawToolbar(Rect rect)
        {
            float x = rect.x;

            // 仅显示可解锁
            Widgets.Checkbox(new Vector2(x, rect.y + 3f), ref showOnlyAvailable, 24f, false);
            Widgets.Label(new Rect(x + 30f, rect.y, 130f, rect.height), "OmniCrafter_Research_ShowAvailableOnly".Translate());
            x += 170f;

            // 忽略前置条件（切换后重新收集）
            bool newIgnore = ignorePrerequisites;
            Widgets.Checkbox(new Vector2(x, rect.y + 3f), ref newIgnore, 24f, false);
            Widgets.Label(new Rect(x + 30f, rect.y, 150f, rect.height), "OmniCrafter_Research_IgnorePrerequisites".Translate());
            x += 190f;

            if (newIgnore != ignorePrerequisites)
            {
                ignorePrerequisites = newIgnore;
                CollectAll();
            }

            // 解锁全部 / 全不选
            Rect unlockAllRect = new Rect(rect.xMax - 250f, rect.y, 110f, 30f);
            if (Widgets.ButtonText(unlockAllRect, "OmniCrafter_Research_UnlockAll".Translate()))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].State == ResearchEntryState.Available)
                    {
                        entries[i].IsSelected = true;
                    }
                }
            }

            Rect deselectRect = new Rect(rect.xMax - 130f, rect.y, 120f, 30f);
            if (Widgets.ButtonText(deselectRect, "OmniCrafter_Research_DeselectAll".Translate()))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].IsSelected = false;
                }
            }
        }

        private void DrawEntryList(Rect outRect)
        {
            float contentWidth = outRect.width - 20f;
            float totalHeight = ComputeListHeight(contentWidth);

            Widgets.BeginScrollView(outRect, ref scrollPos, new Rect(0f, 0f, contentWidth, totalHeight));

            float y = 0f;
            for (int g = 0; g < activeProviders.Count; g++)
            {
                IResearchUnlockProvider provider = activeProviders[g];
                List<ResearchUnlockEntry> group = groupedEntries[g];

                int availableInGroup = 0;
                for (int i = 0; i < group.Count; i++)
                {
                    if (group[i].State == ResearchEntryState.Available)
                    {
                        availableInGroup++;
                    }
                }

                DrawGroupHeader(new Rect(0f, y, contentWidth, GroupHeaderHeight), provider, group, availableInGroup);
                y += GroupHeaderHeight;

                for (int i = 0; i < group.Count; i++)
                {
                    ResearchUnlockEntry entry = group[i];
                    if (showOnlyAvailable && entry.State != ResearchEntryState.Available)
                    {
                        continue;
                    }

                    DrawEntryRow(new Rect(0f, y, contentWidth, RowHeight), entry);
                    y += RowHeight;

                    if (expandedEntry == entry)
                    {
                        float detailHeight = DrawEntryDetail(new Rect(12f, y, contentWidth - 12f, 400f), entry);
                        y += detailHeight;
                    }
                }
            }

            Widgets.EndScrollView();
        }

        private float ComputeListHeight(float contentWidth)
        {
            float height = 0f;
            for (int g = 0; g < activeProviders.Count; g++)
            {
                height += GroupHeaderHeight;
                List<ResearchUnlockEntry> group = groupedEntries[g];
                for (int i = 0; i < group.Count; i++)
                {
                    ResearchUnlockEntry entry = group[i];
                    if (showOnlyAvailable && entry.State != ResearchEntryState.Available)
                    {
                        continue;
                    }

                    height += RowHeight;
                    if (expandedEntry == entry)
                    {
                        height += ComputeDetailHeight(entry, contentWidth - 12f);
                    }
                }
            }
            return height;
        }

        private static float ComputeDetailHeight(ResearchUnlockEntry entry, float width)
        {
            float height = 0f;
            if (!entry.Def.description.NullOrEmpty())
            {
                height += Text.CalcHeight(entry.Def.description, width) + 4f;
            }
            if (entry.PrerequisiteLabels.Count > 0)
            {
                height += entry.PrerequisiteLabels.Count * 20f + 4f;
            }
            return height;
        }

        private void DrawGroupHeader(Rect rect, IResearchUnlockProvider provider, List<ResearchUnlockEntry> group, int availableInGroup)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.15f, 0.15f, 0.15f, 0.5f));
            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            string headerText = "OmniCrafter_Research_GroupHeader".Translate(provider.GroupNameKey.Translate(), availableInGroup);
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 5f, rect.width - 140f, rect.height), headerText);
            Text.WordWrap = true;

            Rect buttonRect = new Rect(rect.xMax - 120f, rect.y + 3f, 110f, 24f);
            if (Widgets.ButtonText(buttonRect, "OmniCrafter_Research_UnlockGroup".Translate(), true, true, availableInGroup > 0))
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (group[i].State == ResearchEntryState.Available)
                    {
                        group[i].IsSelected = true;
                    }
                }
            }
        }

        private void DrawEntryRow(Rect rect, ResearchUnlockEntry entry)
        {
            if (expandedEntry == entry)
            {
                Widgets.DrawHighlight(rect);
            }

            // 勾选框（仅可解锁项可勾选）
            bool canSelect = entry.State == ResearchEntryState.Available;
            bool selected = entry.IsSelected;
            Widgets.Checkbox(new Vector2(rect.x, rect.y + (rect.height - 24f) / 2f), ref selected, 24f, !canSelect);
            entry.IsSelected = selected;

            // 名称
            Text.Font = GameFont.Tiny;
            Text.WordWrap = false;
            Widgets.Label(new Rect(rect.x + 30f, rect.y, 300f, rect.height), entry.Def.LabelCap.ToString());

            // 状态徽标
            string stateText;
            Color stateColor;
            switch (entry.State)
            {
                case ResearchEntryState.Unlocked:
                    stateText = "OmniCrafter_Research_StateUnlocked".Translate();
                    stateColor = ColoredText.FactionColor_Ally;
                    break;
                case ResearchEntryState.Available:
                    stateText = ignorePrerequisites
                        ? "OmniCrafter_Research_StateAvailableIgnored".Translate()
                        : "OmniCrafter_Research_StateAvailable".Translate();
                    stateColor = Color.yellow;
                    break;
                case ResearchEntryState.PrerequisiteMissing:
                    stateText = "OmniCrafter_Research_StateMissingPrereq".Translate();
                    stateColor = ColoredText.SubtleGrayColor;
                    break;
                default:
                    stateText = "OmniCrafter_Research_StateHidden".Translate();
                    stateColor = ColoredText.SubtleGrayColor;
                    break;
            }
            Widgets.Label(new Rect(rect.x + 340f, rect.y, 170f, rect.height), stateText.Colorize(stateColor));

            // 成本
            if (!entry.CostText.NullOrEmpty())
            {
                Widgets.Label(new Rect(rect.x + 520f, rect.y, 240f, rect.height), "OmniCrafter_Research_CostLabel".Translate(entry.CostText));
            }

            Text.WordWrap = true;

            // 整行点击展开 / 收起详情（排除勾选框区域，避免点勾选时误触发展开）
            if (Widgets.ButtonInvisible(new Rect(rect.x + 30f, rect.y, rect.width - 30f, rect.height)))
            {
                expandedEntry = expandedEntry == entry ? null : entry;
            }
        }

        /// <summary>
        /// 绘制展开的详细信息，返回实际占用高度。
        /// </summary>
        private static float DrawEntryDetail(Rect rect, ResearchUnlockEntry entry)
        {
            float y = rect.y;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;

            string desc = entry.Def.description;
            if (!desc.NullOrEmpty())
            {
                float descHeight = Text.CalcHeight(desc, rect.width);
                Widgets.Label(new Rect(rect.x, y, rect.width, descHeight), desc);
                y += descHeight + 4f;
            }

            if (entry.PrerequisiteLabels.Count > 0)
            {
                for (int i = 0; i < entry.PrerequisiteLabels.Count; i++)
                {
                    bool met = entry.PrerequisiteMet[i];
                    string preText = "OmniCrafter_Research_Prerequisites".Translate(entry.PrerequisiteLabels[i]);
                    Widgets.Label(
                        new Rect(rect.x, y, rect.width, 20f),
                        preText.Colorize(met ? ColoredText.FactionColor_Ally : Color.red));
                    y += 20f;
                }
                y += 4f;
            }

            return y - rect.y;
        }

        private void DrawProgress(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 34f), "OmniCrafter_Research_Title".Translate());

            Text.Font = GameFont.Small;
            float progress = totalQueued > 0 ? completedCount / (float)totalQueued : 0f;

            Rect barRect = new Rect(inRect.x + 100f, inRect.y + 140f, inRect.width - 200f, 28f);
            Widgets.FillableBar(barRect, progress, BaseContent.WhiteTex, BaseContent.GreyTex, true);

            Rect textRect = new Rect(inRect.x + 100f, inRect.y + 100f, inRect.width - 200f, 26f);
            Widgets.Label(textRect, "OmniCrafter_Research_Running".Translate(completedCount, totalQueued));

            Rect detailRect = new Rect(inRect.x + 100f, inRect.y + 180f, inRect.width - 200f, 26f);
            Widgets.Label(detailRect, BuildRunningDetail());

            Rect stopRect = new Rect(inRect.x + inRect.width - 220f, inRect.yMax - 60f, 140f, 36f);
            if (Widgets.ButtonText(stopRect, "OmniCrafter_Research_Stop".Translate()))
            {
                isRunning = false;
                CollectAll();
            }
        }

        private string BuildRunningDetail()
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < activeProviders.Count; i++)
            {
                IResearchUnlockProvider provider = activeProviders[i];
                int queued;
                int done;
                queuedByProvider.TryGetValue(provider, out queued);
                completedByProvider.TryGetValue(provider, out done);
                parts.Add(provider.GroupNameKey.Translate() + " " + done + "/" + queued);
            }
            return string.Join("   ", parts.ToArray());
        }
    }
}
