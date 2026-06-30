using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RoadsOfTheRim;
using UnityEngine;
using Verse;
using Verse.Sound;
using RotRConstructionMenu = RoadsOfTheRim.ConstructionMenu;
using RotRMod = RoadsOfTheRim.RoadsOfTheRim;

namespace OuterrealmTechRoadProject.Patches
{
    /// <summary>
    /// 让 Roads of the Rim 的道路选择窗口按道路数量自适应宽度，并在道路过多时横向滚动。
    /// </summary>
    [HarmonyPatch(typeof(RotRConstructionMenu))]
    public static class Patch_RotR_ConstructionMenu
    {
        private const float OriginalWindowWidth = 804f;
        private const float OriginalWindowHeight = 672f;
        private const float ScreenMargin = 40f;
        private const float LeftGutterWidth = 64f;
        private const float RoadCardWidth = 144f;
        private const float RoadCardTop = 32f;
        private const float RoadCardHeight = 560f;
        private const float RoadIconSize = 128f;

        private static readonly FieldInfo SiteField = AccessTools.Field(typeof(RotRConstructionMenu), "<site>P");
        private static readonly FieldInfo CaravanField = AccessTools.Field(typeof(RotRConstructionMenu), "<caravan>P");
        private static readonly FieldInfo BuildableRoadsField = AccessTools.Field(typeof(RotRConstructionMenu), "buildableRoads");
        private static readonly ConditionalWeakTable<RotRConstructionMenu, ScrollState> ScrollStates =
            new ConditionalWeakTable<RotRConstructionMenu, ScrollState>();

        private sealed class ScrollState
        {
            public Vector2 Position;
        }

        /// <summary>
        /// 根据实际可显示道路数量扩展窗口，但不超过屏幕宽度。
        /// </summary>
        [HarmonyPatch("get_InitialSize")]
        [HarmonyPostfix]
        public static void InitialSizePostfix(RotRConstructionMenu __instance, ref Vector2 __result)
        {
            List<RoadDef> roads = GetBuildableRoads(__instance);
            int visibleRoads = CountVisibleRoads(roads);
            if (visibleRoads <= 0)
            {
                __result = new Vector2(OriginalWindowWidth, OriginalWindowHeight);
                return;
            }

            float desiredContentWidth = LeftGutterWidth + RoadCardWidth * visibleRoads;
            float desiredWindowWidth = desiredContentWidth + Window.StandardMargin * 2f;
            float maxWindowWidth = Mathf.Max(320f, Verse.UI.screenWidth - ScreenMargin);
            float width = Mathf.Min(Mathf.Max(OriginalWindowWidth, desiredWindowWidth), maxWindowWidth);
            __result = new Vector2(width, OriginalWindowHeight);
        }

        /// <summary>
        /// 替换原窗口绘制逻辑，把道路列表放进横向滚动区域。
        /// </summary>
        [HarmonyPatch(nameof(Window.DoWindowContents))]
        [HarmonyPrefix]
        public static bool DoWindowContentsPrefix(RotRConstructionMenu __instance, Rect inRect)
        {
            List<RoadDef> roads = GetBuildableRoads(__instance);
            if (roads == null)
            {
                return true;
            }

            RoadConstructionSite site = GetFieldValue<RoadConstructionSite>(SiteField, __instance);
            Caravan caravan = GetFieldValue<Caravan>(CaravanField, __instance);

            if (Event.current.isKey && site != null)
            {
                RotRMod.DeleteConstructionSite((int)site.Tile);
                __instance.Close();
                return false;
            }

            DrawResourceIcons();
            DrawRoadCards(__instance, inRect, roads, site, caravan);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return false;
        }

        private static void DrawResourceIcons()
        {
            for (int index = 0; index < 9; index++)
            {
                Rect rect = new Rect(0f, 202f + index * 40f, 32f, 32f);
                if (index == 0)
                {
                    Widgets.ButtonImage(rect, ContentFinder<Texture2D>.Get("UI/Commands/AddConstructionSite", false) ?? BaseContent.WhiteTex);
                    continue;
                }

                Widgets.ThingIcon(rect, ThingDefForResourceIndex(index));
            }
        }

        private static ThingDef ThingDefForResourceIndex(int index)
        {
            switch (index)
            {
                case 1:
                    return ThingDefOf.WoodLog;
                case 2:
                    return ThingDefOf.BlocksGranite;
                case 3:
                    return ThingDefOf.Steel;
                case 4:
                    return ThingDefOf.Chemfuel;
                case 5:
                    return ThingDefOf.Plasteel;
                case 6:
                    return ThingDefOf.Uranium;
                case 7:
                    return ThingDefOf.ComponentIndustrial;
                default:
                    return ThingDefOf.ComponentSpacer;
            }
        }

        private static void DrawRoadCards(
            RotRConstructionMenu instance,
            Rect inRect,
            List<RoadDef> roads,
            RoadConstructionSite site,
            Caravan caravan)
        {
            int visibleRoads = CountVisibleRoads(roads);
            if (visibleRoads <= 0)
            {
                return;
            }

            ScrollState scrollState = ScrollStates.GetValue(instance, CreateScrollState);
            Rect outRect = new Rect(LeftGutterWidth, 0f, Mathf.Max(1f, inRect.width - LeftGutterWidth), inRect.height);
            Rect viewRect = new Rect(0f, 0f, RoadCardWidth * visibleRoads, Mathf.Max(outRect.height - 16f, RoadCardTop + RoadCardHeight));
            Widgets.BeginScrollView(outRect, ref scrollState.Position, viewRect, viewRect.width > outRect.width);
            try
            {
                int roadIndex = 0;
                foreach (RoadDef road in roads)
                {
                    DefModExtension_RotR_RoadDef extension = road.GetModExtension<DefModExtension_RotR_RoadDef>();
                    if (!ShouldShowRoad(extension))
                    {
                        continue;
                    }

                    GUI.BeginGroup(new Rect(RoadCardWidth * roadIndex, RoadCardTop, RoadCardWidth, RoadCardHeight));
                    DrawRoadCard(road, extension, site, caravan, instance);
                    GUI.EndGroup();
                    roadIndex++;
                }
            }
            finally
            {
                Widgets.EndScrollView();
            }
        }

        private static void DrawRoadCard(
            RoadDef road,
            DefModExtension_RotR_RoadDef extension,
            RoadConstructionSite site,
            Caravan caravan,
            RotRConstructionMenu instance)
        {
            string iconPath = "UI/Commands/Build_" + road.defName;
            Texture2D icon = ContentFinder<Texture2D>.Get(iconPath, false) ?? BaseContent.WhiteTex;
            if (Widgets.ButtonImage(new Rect(8f, 8f, RoadIconSize, RoadIconSize), icon) && Event.current.button == 0)
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                if (site != null)
                {
                    site.roadDef = road;
                    instance.Close();
                    RotRMod.RoadBuildingState.CurrentlyTargeting = site;
                    RotRMod.RoadBuildingState.Caravan = caravan;
                    RoadConstructionLeg.Target(site);
                }
            }

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 144f, RoadCardWidth, 32f), road.label);
            Text.Font = GameFont.Small;

            int resourceIndex = 0;
            foreach (string resourceName in DefModExtension_RotR_RoadDef.allResourcesAndWork)
            {
                int cost = extension.GetCost(resourceName);
                string label = cost > 0 ? (cost * GetBaseEffortFactor()).ToString() : "-";
                Widgets.Label(new Rect(0f, 176f + resourceIndex * 40f, RoadCardWidth, 32f), label);
                resourceIndex++;
            }
        }

        private static float GetBaseEffortFactor()
        {
            return RotRMod.settings != null ? RotRMod.settings.BaseEffort / 10f : 1f;
        }

        private static int CountVisibleRoads(List<RoadDef> roads)
        {
            if (roads == null)
            {
                return 0;
            }

            int count = 0;
            foreach (RoadDef road in roads)
            {
                if (ShouldShowRoad(road.GetModExtension<DefModExtension_RotR_RoadDef>()))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool ShouldShowRoad(DefModExtension_RotR_RoadDef extension)
        {
            if (extension == null)
            {
                return false;
            }

            ResearchProjectDef techNeeded = extension.techNeededToBuild;
            return techNeeded == null || techNeeded.IsFinished;
        }

        private static List<RoadDef> GetBuildableRoads(RotRConstructionMenu instance)
        {
            return GetFieldValue<List<RoadDef>>(BuildableRoadsField, instance);
        }

        private static T GetFieldValue<T>(FieldInfo field, RotRConstructionMenu instance) where T : class
        {
            return field == null ? null : field.GetValue(instance) as T;
        }

        private static ScrollState CreateScrollState(RotRConstructionMenu _)
        {
            return new ScrollState();
        }
    }
}
