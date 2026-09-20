using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 全局代理与工作站总览（R-7）：列出所有地图池、工作站与代理，显示每个代理的
    /// **归属地图**与**当前所在地图 + 坐标**，并提供批量与逐代理管理动作。
    ///
    /// 所有会改动地图集合的动作（重建 / 回收 / 修复）都**只登记请求**，由
    /// `GameComponent_OmniWorkProxyRegistry.GameComponentTick` 或
    /// `MapComponent_OmniWorkstation.ProcessPendingManagementRequests` 在 tick 中执行 ——
    /// OnGUI 阶段销毁或生成 Pawn 会破坏原版迭代。
    ///
    /// 性能：快照 30 帧重建一次；只绘制视口内的池与代理行（AGENTS.md 要求）。
    /// </summary>
    public sealed class Window_OmniWorkstationManager : Window
    {
        private const float RowHeight = 26f;
        private const float PoolHeaderHeight = 30f;
        private const float PoolGap = 6f;

        private Vector2 scrollPosition;
        private int lastRefreshFrame = -1000;
        private float snapshotHeight;

        private readonly List<PoolRow> poolRows = new List<PoolRow>();
        private readonly List<ProxyRow> proxyRows = new List<ProxyRow>();

        // 复用列表：避免每次快照重建时反复分配。
        private readonly List<Pawn> poolScratch = new List<Pawn>();
        private readonly List<Map> reachableScratch = new List<Map>();
        private readonly List<string> nameScratch = new List<string>();

        private struct PoolRow
        {
            public Map map;
            public string label;
            public int stationCount;
            public int configuredLimit;
            public int totalCount;
            public int abroadCount;
            public int uncoveredPortals;
            public string reachableMaps;
            public int firstProxyIndex;
            public int proxyCount;
        }

        private struct ProxyRow
        {
            public Pawn pawn;
            public string name;
            public string homeMapLabel;
            public string location;
            public string work;
        }

        public override Vector2 InitialSize => new Vector2(920f, 680f);

        public Window_OmniWorkstationManager()
        {
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 快照低频重建：遍历地图与代理只在打开窗口时发生。
            if (Time.frameCount - lastRefreshFrame >= 30)
            {
                RebuildSnapshot();
                lastRefreshFrame = Time.frameCount;
            }

            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 40f, 32f),
                "OmniWorkstation_ManagerTitle".Translate());
            Text.Font = GameFont.Small;

            float top = 36f;
            if (registry != null)
            {
                string toggleKey = registry.GlobalWorkEnabled
                    ? "OmniWorkstation_GlobalWorkOn"
                    : "OmniWorkstation_GlobalWorkOff";
                if (Widgets.ButtonText(new Rect(0f, top, 170f, 26f), toggleKey.Translate()))
                    registry.SetGlobalWorkEnabled(!registry.GlobalWorkEnabled);
                if (Widgets.ButtonText(new Rect(176f, top, 152f, 26f),
                        "OmniWorkstation_ManagerRecreateAll".Translate()))
                    registry.RequestForceRecreateAll();
                if (Widgets.ButtonText(new Rect(334f, top, 152f, 26f),
                        "OmniWorkstation_ManagerRepairAll".Translate()))
                    registry.RequestRepairAll();
                if (Widgets.ButtonText(new Rect(492f, top, 152f, 26f),
                        "OmniWorkstation_VerifyMirrors".Translate()))
                    registry.VerifyAllMirrors();

                Widgets.Label(new Rect(652f, top + 4f, inRect.width - 656f, 24f),
                    "OmniWorkstation_ManagerSummary".Translate(poolRows.Count, proxyRows.Count));
            }
            top += 34f;

            Rect outRect = new Rect(0f, top, inRect.width, inRect.height - top - 40f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(outRect.height, snapshotHeight));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float y = 0f;
            float viewTop = scrollPosition.y - 40f;
            float viewBottom = scrollPosition.y + outRect.height + 40f;
            for (int i = 0; i < poolRows.Count; i++)
            {
                PoolRow pool = poolRows[i];
                float blockHeight = PoolHeaderHeight + pool.proxyCount * RowHeight + PoolGap;
                if (y + blockHeight < viewTop)
                {
                    y += blockHeight;
                    continue;
                }
                if (y > viewBottom) break;

                DrawPoolHeader(new Rect(0f, y, viewRect.width, PoolHeaderHeight), pool);
                y += PoolHeaderHeight;

                for (int j = 0; j < pool.proxyCount; j++)
                {
                    DrawProxyRow(new Rect(0f, y, viewRect.width, RowHeight),
                        proxyRows[pool.firstProxyIndex + j], registry, pool.map);
                    y += RowHeight;
                }
                y += PoolGap;
            }
            Widgets.EndScrollView();
        }

        // ── 快照 ───────────────────────────────────────────────────────────────

        private void RebuildSnapshot()
        {
            poolRows.Clear();
            proxyRows.Clear();

            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                MapComponent_OmniWorkstation manager = map.GetComponent<MapComponent_OmniWorkstation>();
                if (manager == null) continue;

                int stationCount = CountStations(map);
                poolScratch.Clear();
                if (registry != null) registry.EnumeratePool(map, poolScratch);
                if (stationCount == 0 && poolScratch.Count == 0) continue;

                PoolRow row = new PoolRow
                {
                    map = map,
                    label = MapLabel(map),
                    stationCount = stationCount,
                    configuredLimit = manager.ConfiguredProxyCount,
                    totalCount = manager.TotalProxyCount,
                    firstProxyIndex = proxyRows.Count,
                    uncoveredPortals = OmniWorkProxyEntryAuthorization.UncoveredPortals(map),
                    reachableMaps = DescribeReachableMaps(map),
                };

                for (int j = 0; j < poolScratch.Count; j++)
                {
                    Pawn pawn = poolScratch[j];
                    if (pawn == null || pawn.Destroyed) continue;

                    registry.TryGetHome(pawn, out GameComponent_OmniWorkProxyRegistry.ProxyHomeRecord record);
                    if (record != null && record.abroadMap != null && record.abroadMap != map) row.abroadCount++;

                    proxyRows.Add(new ProxyRow
                    {
                        pawn = pawn,
                        name = pawn.Name?.ToStringShort ?? "-",
                        homeMapLabel = MapLabel(record?.homeMap ?? map),
                        location = DescribeLocation(pawn),
                        work = DescribeWork(pawn),
                    });
                }

                row.proxyCount = proxyRows.Count - row.firstProxyIndex;
                poolRows.Add(row);
            }

            snapshotHeight = 0f;
            for (int i = 0; i < poolRows.Count; i++)
                snapshotHeight += PoolHeaderHeight + poolRows[i].proxyCount * RowHeight + PoolGap;
        }

        private void DrawPoolHeader(Rect rect, PoolRow pool)
        {
            Widgets.DrawHighlight(rect);
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 4f, rect.width * 0.42f, 22f),
                "OmniWorkstation_ManagerPoolHeader".Translate(pool.label, pool.stationCount,
                    pool.totalCount, pool.configuredLimit));
            Widgets.Label(new Rect(rect.x + rect.width * 0.42f, rect.y + 4f,
                    rect.width * 0.58f - 8f, 22f),
                "OmniWorkstation_ManagerPoolDetail".Translate(pool.abroadCount,
                    pool.uncoveredPortals, pool.reachableMaps));
        }

        private void DrawProxyRow(Rect rect, ProxyRow proxy, GameComponent_OmniWorkProxyRegistry registry,
            Map poolMap)
        {
            if ((rect.y / RowHeight) % 2f >= 1f) Widgets.DrawLightHighlight(rect);
            Widgets.Label(new Rect(rect.x + 12f, rect.y + 3f, 130f, 22f), proxy.name);
            Widgets.Label(new Rect(rect.x + 146f, rect.y + 3f, 130f, 22f), proxy.homeMapLabel);
            Widgets.Label(new Rect(rect.x + 280f, rect.y + 3f, 190f, 22f), proxy.location);

            float actionsX = rect.width - 236f;
            Widgets.Label(new Rect(rect.x + 474f, rect.y + 3f, Mathf.Max(60f, actionsX - 480f), 22f),
                proxy.work);

            Pawn pawn = proxy.pawn;
            if (pawn == null || pawn.Destroyed || poolMap == null) return;
            MapComponent_OmniWorkstation manager = poolMap.GetComponent<MapComponent_OmniWorkstation>();
            if (manager == null) return;

            if (Widgets.ButtonText(new Rect(actionsX, rect.y + 1f, 108f, 24f),
                    "OmniWorkstation_ProxyReclaim".Translate()))
                manager.RequestProxyReclaim(pawn);
            if (Widgets.ButtonText(new Rect(actionsX + 113f, rect.y + 1f, 108f, 24f),
                    "OmniWorkstation_ProxyRecreate".Translate()))
                manager.RequestProxyRecreate(pawn);
        }

        // ── 文本 ───────────────────────────────────────────────────────────────

        private static int CountStations(Map map)
        {
            int count = 0;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i] is Building_OmniWorkstation) count++;
            return count;
        }

        private static string MapLabel(Map map)
        {
            if (map == null) return "-";
            string label = map.Parent?.LabelCap;
            return label.NullOrEmpty() ? "#" + map.uniqueID : label;
        }

        private static string DescribeLocation(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return "-";
            if (pawn.Spawned && pawn.Map != null)
                return MapLabel(pawn.Map) + " (" + pawn.Position.x + ", " + pawn.Position.z + ")";
            return pawn.ParentHolder != null ? pawn.ParentHolder.ToString() : "-";
        }

        private static string DescribeWork(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return "-";
            Job job = pawn.CurJob;
            if (job == null) return "OmniWorkstation_ManagerIdle".Translate().ToString();
            return job.def?.label ?? job.def?.defName ?? "-";
        }

        private string DescribeReachableMaps(Map map)
        {
            reachableScratch.Clear();
            OmniWorkProxyEntryAuthorization.GetReachableMaps(map, reachableScratch);
            if (reachableScratch.Count == 0) return "OmniWorkstation_ReachableMapsNone".Translate().ToString();

            nameScratch.Clear();
            for (int i = 0; i < reachableScratch.Count; i++)
                nameScratch.Add(MapLabel(reachableScratch[i]));
            return string.Join("、", nameScratch);
        }
    }
}
