using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OuterrealmTechRoadProject.Startup;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace OuterrealmTechRoadProject.Patches
{
    /// <summary>
    /// 让 Roads of the Rim 的道路选择窗口按道路数量自适应宽度，并在道路过多时横向滚动。
    /// 这里不能静态引用 RoadsOfTheRim 类型，否则 Rails-only 加载时本程序集会因缺少依赖而失败。
    /// </summary>
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

        private static readonly ConditionalWeakTable<object, ScrollState> ScrollStates =
            new ConditionalWeakTable<object, ScrollState>();

        private static Type menuType;
        private static Type siteType;
        private static Type extensionType;
        private static FieldInfo siteField;
        private static FieldInfo caravanField;
        private static FieldInfo buildableRoadsField;
        private static FieldInfo siteRoadDefField;
        private static FieldInfo techNeededToBuildField;
        private static FieldInfo allResourcesAndWorkField;
        private static FieldInfo settingsField;
        private static FieldInfo baseEffortField;
        private static PropertyInfo roadBuildingStateProperty;
        private static PropertyInfo currentlyTargetingProperty;
        private static PropertyInfo caravanProperty;
        private static MethodInfo deleteConstructionSiteMethod;
        private static MethodInfo targetLegMethod;
        private static MethodInfo getCostMethod;

        private sealed class ScrollState
        {
            public Vector2 Position;
        }

        public static void Apply(Harmony harmony)
        {
            if (!RoadConstructionBackend.RoadsActive)
            {
                return;
            }

            if (!TryInitialize())
            {
                Log.Warning("[OuterrealmTechRoadProject] RoadsOfTheRim is active, but its ConstructionMenu compatibility patch could not be initialized.");
                return;
            }

            MethodInfo initialSizeGetter = AccessTools.PropertyGetter(menuType, "InitialSize");
            MethodInfo doWindowContents = AccessTools.Method(menuType, nameof(Window.DoWindowContents), new[] { typeof(Rect) });
            if (initialSizeGetter == null || doWindowContents == null)
            {
                Log.Warning("[OuterrealmTechRoadProject] RoadsOfTheRim ConstructionMenu methods were not found; skipping menu size patch.");
                return;
            }

            harmony.Patch(initialSizeGetter, postfix: new HarmonyMethod(typeof(Patch_RotR_ConstructionMenu), nameof(InitialSizePostfix)));
            harmony.Patch(doWindowContents, prefix: new HarmonyMethod(typeof(Patch_RotR_ConstructionMenu), nameof(DoWindowContentsPrefix)));
        }

        /// <summary>
        /// 根据实际可显示道路数量扩展窗口，但不超过屏幕宽度。
        /// </summary>
        public static void InitialSizePostfix(object __instance, ref Vector2 __result)
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
        public static bool DoWindowContentsPrefix(object __instance, Rect inRect)
        {
            List<RoadDef> roads = GetBuildableRoads(__instance);
            if (roads == null)
            {
                return true;
            }

            WorldObject site = GetFieldValue<WorldObject>(siteField, __instance);
            Caravan caravan = GetFieldValue<Caravan>(caravanField, __instance);

            if (Event.current.isKey && site != null)
            {
                DeleteConstructionSite(site.Tile);
                ((Window)__instance).Close();
                return false;
            }

            DrawResourceIcons();
            DrawRoadCards(__instance, inRect, roads, site, caravan);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return false;
        }

        private static bool TryInitialize()
        {
            menuType = AccessTools.TypeByName("RoadsOfTheRim.ConstructionMenu");
            siteType = AccessTools.TypeByName("RoadsOfTheRim.RoadConstructionSite");
            Type legType = AccessTools.TypeByName("RoadsOfTheRim.RoadConstructionLeg");
            Type modType = AccessTools.TypeByName("RoadsOfTheRim.RoadsOfTheRim");
            extensionType = AccessTools.TypeByName("RoadsOfTheRim.DefModExtension_RotR_RoadDef");

            if (menuType == null || siteType == null || legType == null || modType == null || extensionType == null)
            {
                return false;
            }

            siteField = AccessTools.Field(menuType, "<site>P");
            caravanField = AccessTools.Field(menuType, "<caravan>P");
            buildableRoadsField = AccessTools.Field(menuType, "buildableRoads");
            siteRoadDefField = AccessTools.Field(siteType, "roadDef");
            techNeededToBuildField = AccessTools.Field(extensionType, "techNeededToBuild");
            allResourcesAndWorkField = AccessTools.Field(extensionType, "allResourcesAndWork");
            settingsField = AccessTools.Field(modType, "settings");
            roadBuildingStateProperty = AccessTools.Property(modType, "RoadBuildingState");
            if (roadBuildingStateProperty == null)
            {
                return false;
            }

            currentlyTargetingProperty = AccessTools.Property(roadBuildingStateProperty.PropertyType, "CurrentlyTargeting");
            caravanProperty = AccessTools.Property(roadBuildingStateProperty.PropertyType, "Caravan");
            deleteConstructionSiteMethod = AccessTools.Method(modType, "DeleteConstructionSite", new[] { typeof(int) });
            targetLegMethod = AccessTools.Method(legType, "Target", new[] { siteType });
            getCostMethod = AccessTools.Method(extensionType, "GetCost", new[] { typeof(string) });

            Type settingsType = settingsField != null ? settingsField.FieldType : null;
            baseEffortField = settingsType != null ? AccessTools.Field(settingsType, "BaseEffort") : null;

            return siteField != null &&
                   caravanField != null &&
                   buildableRoadsField != null &&
                   siteRoadDefField != null &&
                   techNeededToBuildField != null &&
                   allResourcesAndWorkField != null &&
                   settingsField != null &&
                   baseEffortField != null &&
                   roadBuildingStateProperty != null &&
                   currentlyTargetingProperty != null &&
                   caravanProperty != null &&
                   deleteConstructionSiteMethod != null &&
                   targetLegMethod != null &&
                   getCostMethod != null;
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
            object instance,
            Rect inRect,
            List<RoadDef> roads,
            WorldObject site,
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
                    object extension = GetRotRExtension(road);
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
            object extension,
            WorldObject site,
            Caravan caravan,
            object instance)
        {
            string iconPath = "UI/Commands/Build_" + road.defName;
            Texture2D icon = ContentFinder<Texture2D>.Get(iconPath, false) ?? BaseContent.WhiteTex;
            if (Widgets.ButtonImage(new Rect(8f, 8f, RoadIconSize, RoadIconSize), icon) && Event.current.button == 0)
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                if (site != null)
                {
                    siteRoadDefField.SetValue(site, road);
                    ((Window)instance).Close();
                    SetRoadBuildingState(site, caravan);
                    targetLegMethod.Invoke(null, new[] { site });
                }
            }

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 144f, RoadCardWidth, 32f), road.label);
            Text.Font = GameFont.Small;

            int resourceIndex = 0;
            foreach (string resourceName in AllResourcesAndWork())
            {
                int cost = GetCost(extension, resourceName);
                string label = cost > 0 ? (cost * GetBaseEffortFactor()).ToString() : "-";
                Widgets.Label(new Rect(0f, 176f + resourceIndex * 40f, RoadCardWidth, 32f), label);
                resourceIndex++;
            }
        }

        private static float GetBaseEffortFactor()
        {
            object settings = settingsField.GetValue(null);
            if (settings == null)
            {
                return 1f;
            }

            object baseEffort = baseEffortField.GetValue(settings);
            return baseEffort is int value ? value / 10f : 1f;
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
                if (ShouldShowRoad(GetRotRExtension(road)))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool ShouldShowRoad(object extension)
        {
            if (extension == null)
            {
                return false;
            }

            ResearchProjectDef techNeeded = techNeededToBuildField.GetValue(extension) as ResearchProjectDef;
            return techNeeded == null || techNeeded.IsFinished;
        }

        private static object GetRotRExtension(RoadDef road)
        {
            if (road == null || road.modExtensions == null)
            {
                return null;
            }

            for (int i = 0; i < road.modExtensions.Count; i++)
            {
                DefModExtension extension = road.modExtensions[i];
                if (extension != null && extensionType.IsInstanceOfType(extension))
                {
                    return extension;
                }
            }

            return null;
        }

        private static string[] AllResourcesAndWork()
        {
            return allResourcesAndWorkField.GetValue(null) as string[] ?? Array.Empty<string>();
        }

        private static int GetCost(object extension, string resourceName)
        {
            object result = getCostMethod.Invoke(extension, new object[] { resourceName });
            return result is int cost ? cost : 0;
        }

        private static List<RoadDef> GetBuildableRoads(object instance)
        {
            return buildableRoadsField.GetValue(instance) as List<RoadDef>;
        }

        private static T GetFieldValue<T>(FieldInfo field, object instance) where T : class
        {
            return field == null ? null : field.GetValue(instance) as T;
        }

        private static void DeleteConstructionSite(PlanetTile tile)
        {
            deleteConstructionSiteMethod.Invoke(null, new object[] { (int)tile });
        }

        private static void SetRoadBuildingState(WorldObject site, Caravan caravan)
        {
            object roadBuildingState = roadBuildingStateProperty.GetValue(null, null);
            if (roadBuildingState == null)
            {
                return;
            }

            currentlyTargetingProperty.SetValue(roadBuildingState, site, null);
            caravanProperty.SetValue(roadBuildingState, caravan, null);
        }

        private static ScrollState CreateScrollState(object _)
        {
            return new ScrollState();
        }
    }
}
