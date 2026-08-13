using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 全局存储管理器（§6.4，必须实现）：无视任何建筑 filter 查看超维空间全部内容，
    /// 是"全禁用死锁"（§6.2）的唯一逃生口。功能：内容列表（图标/名称/long 数量/可见建筑数）、
    /// 搜索（QuickSearchWidget）、"仅显示不可见条目"快捷筛选（死锁定位）、按地图强制弹出（限速队列）。
    /// </summary>
    public class Dialog_OuterrealmStorageManager : Window
    {
        private readonly Vector2 initialSize = new Vector2(1000f, 700f);
        public override Vector2 InitialSize => initialSize;

        private Vector2 scrollPosition;
        private readonly QuickSearchWidget searchWidget = new QuickSearchWidget();
        private bool showOnlyUnseen;
        private bool dirtyUnseenFlag;
        private bool dirty = true;
        private int selectedMapIndex;
        private readonly List<OuterrealmEntry> visibleEntries = new List<OuterrealmEntry>();

        public Dialog_OuterrealmStorageManager()
        {
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            selectedMapIndex = Find.CurrentMap != null ? Find.Maps.IndexOf(Find.CurrentMap) : 0;
            if (selectedMapIndex < 0)
            {
                selectedMapIndex = 0;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                Widgets.Label(inRect, "OuterrealmStorageManager_NoEntries".Translate());
                return;
            }
            if (dirty)
            {
                RebuildVisible(gs);
                dirty = false;
            }

            Text.Font = GameFont.Small;
            float y = inRect.y;
            const float rowHeight = 28f;

            // 标题 + 统计
            gs.GetSummary(out int entryCount, out long totalCount);
            long ejecting = 0;
            for (int i = 0; i < gs.EjectQueueForReading.Count; i++)
            {
                ejecting += gs.EjectQueueForReading[i].Remaining;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "OuterrealmStorageManagerTitle".Translate());
            y += 30f;
            Text.Font = GameFont.Small;
            string stat = "OuterrealmStorageManager_Total".Translate(entryCount, totalCount.ToString("N0"));
            if (ejecting > 0)
            {
                stat += "   " + "OuterrealmStorageManager_EjectProgress".Translate(ejecting.ToString("N0"));
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 250f, 24f), stat);
            // 全部取出所有物品（追加到弹出队列，逐 tick 限速执行）
            if (Widgets.ButtonText(new Rect(inRect.x + inRect.width - 250f, y, 250f, 26f), "OuterrealmStorageManager_EjectAllEntries".Translate(), true, false, true))
            {
                List<OuterrealmEntry> all = gs.EntriesForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    OuterrealmEntry e = all[i];
                    if (e.Count > 0)
                    {
                        gs.EnqueueEject(e.Key, TargetMap(), e.Count);
                    }
                }
            }
            y += 30f;

            // 搜索 + 不可见筛选 + 地图选择
            searchWidget.OnGUI(new Rect(inRect.x, y, 260f, 28f), () => dirty = true, () => dirty = true);
            Widgets.CheckboxLabeled(new Rect(inRect.x + 270f, y, 240f, 28f), "OuterrealmStorageManager_ShowOnlyUnseen".Translate(), ref showOnlyUnseen, true);
            if (showOnlyUnseen != dirtyUnseenFlag)
            {
                dirtyUnseenFlag = showOnlyUnseen;
                dirty = true;
            }
            if (Find.Maps.Count > 1)
            {
                string mapLabel = selectedMapIndex >= 0 && selectedMapIndex < Find.Maps.Count
                    ? Find.Maps[selectedMapIndex].info.parent.Label
                    : "-";
                Rect mapRect = new Rect(inRect.x + inRect.width - 240f, y, 240f, 28f);
                if (Widgets.ButtonText(mapRect, "OuterrealmStorageManager_SelectMap".Translate() + ": " + mapLabel, true, false, true))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    for (int i = 0; i < Find.Maps.Count; i++)
                    {
                        Map m = Find.Maps[i];
                        int index = i;
                        options.Add(new FloatMenuOption(m.info.parent.Label, () => selectedMapIndex = index));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            y += 34f;

            // 条目列表
            Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.height - y - 40f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, visibleEntries.Count * rowHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0f;
            for (int i = 0; i < visibleEntries.Count; i++)
            {
                DoEntryRow(gs, visibleEntries[i], viewRect.width, ref curY);
            }
            if (visibleEntries.Count == 0)
            {
                Widgets.NoneLabel(ref curY, viewRect.width, "OuterrealmStorageManager_NoEntries".Translate());
            }
            Widgets.EndScrollView();
        }

        /// <summary>按搜索/筛选重建可见条目列表（行数 = L1 组合级，几十~几百）。</summary>
        private void RebuildVisible(GameComponent_OuterrealmStorage gs)
        {
            visibleEntries.Clear();
            List<OuterrealmEntry> all = gs.EntriesForReading;
            for (int i = 0; i < all.Count; i++)
            {
                OuterrealmEntry e = all[i];
                if (e.Count <= 0)
                {
                    continue;
                }
                if (showOnlyUnseen && CountVisibleBuildings(gs, e) > 0)
                {
                    continue;
                }
                bool matches = searchWidget.filter.Matches(e.Proto.def.label)
                    || searchWidget.filter.Matches(e.Proto.def.defName);
                if (matches)
                {
                    visibleEntries.Add(e);
                }
            }
        }

        /// <summary>该条目当前被几座终端可见（帮助定位死锁条目，§6.4）。</summary>
        private static int CountVisibleBuildings(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry)
        {
            int count = 0;
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.view != null && v.view.FindCopy(entry.Key) != null)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>单行渲染：图标 + 名称 + long 数量 + 可见建筑数/不可见标记 + 弹出/全部弹出按钮。</summary>
        private void DoEntryRow(GameComponent_OuterrealmStorage gs, OuterrealmEntry entry, float width, ref float curY)
        {
            Rect rect = new Rect(0f, curY, width, 28f);
            int visibleBuildings = CountVisibleBuildings(gs, entry);

            if (Widgets.ButtonText(new Rect(rect.x + rect.width - 90f, curY + 2f, 86f, 24f), "OuterrealmStorageManager_EjectAll".Translate(), true, false, true))
            {
                gs.EnqueueEject(entry.Key, TargetMap(), entry.Count);
            }
            if (Widgets.ButtonText(new Rect(rect.x + rect.width - 180f, curY + 2f, 86f, 24f), "OuterrealmStorageManager_Eject".Translate(), true, false, true))
            {
                int max = (int)Mathf.Min(entry.Count, int.MaxValue);
                if (max > 0)
                {
                    string label = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto);
                    Find.WindowStack.Add(new Dialog_Slider(
                        (int v) => label + " x" + v.ToString("N0"),
                        1,
                        max,
                        (int v) => gs.EnqueueEject(entry.Key, TargetMap(), v)));
                }
            }
            rect.width -= 190f;

            Widgets.InfoCardButton(rect.width - 24f, curY, entry.Proto);
            rect.width -= 24f;
            Widgets.ThingIcon(new Rect(4f, curY, 28f, 28f), entry.Proto);

            string text = OuterrealmVaultUtil.SafeLabelCapNoCount(entry.Proto) + " x" + entry.Count.ToString("N0");
            if (visibleBuildings == 0)
            {
                text += "  (" + "OuterrealmStorageManager_Unseen".Translate() + ")";
            }
            else
            {
                text += "  " + "OuterrealmStorageManager_VisibleBuildings".Translate(visibleBuildings);
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(36f, curY, rect.width - 36f, rect.height), text.StripTags().Truncate(rect.width - 36f));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, text);
            curY += 28f;
        }

        private Map TargetMap()
        {
            if (selectedMapIndex >= 0 && selectedMapIndex < Find.Maps.Count)
            {
                return Find.Maps[selectedMapIndex];
            }
            return Find.CurrentMap;
        }
    }
}
