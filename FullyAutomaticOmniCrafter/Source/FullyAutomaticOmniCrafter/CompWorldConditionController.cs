using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_WorldConditionController : CompProperties
    {
        public CompProperties_WorldConditionController()
        {
            compClass = typeof(CompWorldConditionController);
        }
    }

    [StaticConstructorOnStartup]
    public static class WorldConditionControllerTex
    {
        public static readonly Texture2D OpenConditionMenu =
            ContentFinder<Texture2D>.Get("UI/Commands/ManualEventTrigger_OpenEventMenu", false)
            ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 允许玩家手动终止当前地图右下角显示的地图条件与世界条件。
    /// </summary>
    public class CompWorldConditionController : ThingComp
    {
        // Gizmo 会被频繁查询，复用列表以避免产生无意义的临时分配。
        private readonly List<GameCondition> visibleConditions = new List<GameCondition>();

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            CollectVisibleConditions();

            Command_Action command = new Command_Action
            {
                defaultLabel = "WorldConditionController_OpenMenu".Translate(),
                defaultDesc = "WorldConditionController_OpenMenuDesc".Translate(),
                icon = WorldConditionControllerTex.OpenConditionMenu,
                action = OpenConditionMenu
            };

            if (visibleConditions.Count == 0)
            {
                command.Disable("WorldConditionController_NoConditions".Translate());
            }

            yield return command;
        }

        private void OpenConditionMenu()
        {
            CollectVisibleConditions();
            if (visibleConditions.Count == 0)
            {
                Messages.Message(
                    "WorldConditionController_NoConditions".Translate(),
                    MessageTypeDefOf.RejectInput);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>(visibleConditions.Count);
            for (int i = 0; i < visibleConditions.Count; i++)
            {
                GameCondition condition = visibleConditions[i];
                bool isWorldCondition = condition.gameConditionManager.ownerMap == null;
                string scope = isWorldCondition
                    ? "WorldConditionController_WorldScope".Translate()
                    : "WorldConditionController_MapScope".Translate();

                string warning = string.Empty;
                if (condition.conditionCauser != null)
                {
                    warning = "WorldConditionController_SourceWarning".Translate();
                }
                else if (condition.quest != null)
                {
                    warning = "WorldConditionController_QuestWarning".Translate();
                }

                string label = "WorldConditionController_ConditionOption".Translate(
                    condition.LabelCap,
                    condition.def.defName,
                    scope,
                    warning);

                options.Add(new FloatMenuOption(label, delegate
                {
                    OpenTerminationMethodMenu(condition);
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void OpenTerminationMethodMenu(GameCondition condition)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>(2)
            {
                new FloatMenuOption(
                    "WorldConditionController_MethodEnd".Translate(),
                    delegate { EndConditionImmediately(condition); }),
                new FloatMenuOption(
                    "WorldConditionController_MethodDuration".Translate(),
                    delegate { SetConditionDurationToZero(condition); })
            };

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void CollectVisibleConditions()
        {
            visibleConditions.Clear();

            Map map = parent.Map;
            if (map == null)
            {
                return;
            }

            List<GameCondition> mapConditions = map.GameConditionManager.ActiveConditions;
            for (int i = 0; i < mapConditions.Count; i++)
            {
                GameCondition condition = mapConditions[i];
                if (condition.def.displayOnUI
                    && condition.CanApplyOnMap(map)
                    && !condition.HiddenByOtherCondition(map))
                {
                    visibleConditions.Add(condition);
                }
            }

            GameConditionManager worldManager = Find.World?.GameConditionManager;
            if (worldManager == null)
            {
                return;
            }

            List<GameCondition> worldConditions = worldManager.ActiveConditions;
            for (int i = 0; i < worldConditions.Count; i++)
            {
                GameCondition condition = worldConditions[i];
                // 原版世界条件 UI 只使用 displayOnUI 作为显示条件。
                if (condition.def.displayOnUI)
                {
                    visibleConditions.Add(condition);
                }
            }
        }

        private static bool IsConditionActive(GameCondition condition)
        {
            GameConditionManager manager = condition?.gameConditionManager;
            if (manager == null || !manager.ActiveConditions.Contains(condition))
            {
                Messages.Message(
                    "WorldConditionController_AlreadyEnded".Translate(),
                    MessageTypeDefOf.RejectInput);
                return false;
            }

            return true;
        }

        private static void EndConditionImmediately(GameCondition condition)
        {
            if (!IsConditionActive(condition))
            {
                return;
            }

            string conditionLabel = condition.LabelCap;
            condition.End();

            Messages.Message(
                "WorldConditionController_EndImmediateSuccess".Translate(conditionLabel),
                MessageTypeDefOf.PositiveEvent);
        }

        private static void SetConditionDurationToZero(GameCondition condition)
        {
            if (!IsConditionActive(condition))
            {
                return;
            }

            string conditionLabel = condition.LabelCap;
            condition.Duration = 0;

            Messages.Message(
                "WorldConditionController_DurationSuccess".Translate(conditionLabel),
                MessageTypeDefOf.PositiveEvent);
        }
    }
}
