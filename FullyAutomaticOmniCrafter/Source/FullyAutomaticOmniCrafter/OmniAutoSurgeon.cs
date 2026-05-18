using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class SurgeryTemplate : IExposable
    {
        public string templateName;

        // 记录部位路径 (unique path or defName + index) 和对应的 义体 HediffDef
        // 这里简单点，记录 BodyPartDef 的 defName 可能会有重复部位问题，
        // 但对于大多数义体（眼、臂、腿）通常是通用的。
        // 更好的做法是记录 BodyPartRecord 的某种标识。
        public Dictionary<string, string> partToBionicMap = new Dictionary<string, string>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref templateName, "templateName");
            Scribe_Collections.Look(ref partToBionicMap, "partToBionicMap", LookMode.Value, LookMode.Value);
        }
    }

    [StaticConstructorOnStartup]
    public static class FullyAutoOmniSurgeonTex
    {
        public static readonly Texture2D IconModifyDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_Modify", true) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconRepair =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_Repair", true) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconPodEject =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_PodEject", true) ??
            BaseContent.WhiteTex;
    }

    /// <summary>
    /// 全自动医疗改造舱 FullyAutoOmniSurgeon
    /// 一个类似医疗床或休眠舱的建筑，可以快速为特定对象快速批量添加删除身体部位和义肢等增强部件、以及修复损伤和医疗受伤的建筑。
    /// 支持按模板安装、拆解。
    /// 支持手动按部位编辑和安装。
    /// 忽略材料限制。 
    /// </summary>
    public class Building_FullyAutoOmniSurgeon : Building_Casket
    {
        public List<SurgeryTemplate> templates = new List<SurgeryTemplate>();

        public Pawn Occupant => innerContainer.FirstOrDefault() as Pawn;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref templates, "templates", LookMode.Deep);
            if (templates == null) templates = new List<SurgeryTemplate>();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            if (this.Occupant != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_OpenPanel".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_OpenPanelDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconModifyDialog,
                    action = () => { Find.WindowStack.Add(new Window_OmniAutoSurgeonUI(this.Occupant, this)); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_FullRepair".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_FullRepairDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconRepair,
                    action = () => { FullRepair(this.Occupant); }
                };
            }

            if (this.Faction == Faction.OfPlayer && this.innerContainer.Count > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_Eject".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_EjectDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconPodEject,
                    action = EjectContents
                };
            }
        }

        public override void EjectContents()
        {
            foreach (Thing thing in (IEnumerable<Thing>)this.innerContainer)
            {
                if (thing is Pawn pawn)
                {
                    PawnComponentsUtility.AddComponentsForSpawn(pawn);
                }
            }

            base.EjectContents();
        }

        public void InstallBionic(Pawn pawn, BodyPartRecord part, HediffDef bionicDef)
        {
            if (pawn == null || part == null || bionicDef == null) return;

            try
            {
                // 1. 移除该部位已有的义体或冲突
                var existing = pawn.health.hediffSet.hediffs
                    .Where(h => h.Part == part && (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null))
                    .ToList();

                foreach (var h in existing)
                {
                    RemoveBionic(pawn, part, h);
                }

                // 2. 安装新义体
                pawn.health.AddHediff(bionicDef, part);
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 安装义体 {bionicDef.defName} 到 {pawn.LabelShort} 的 {part.Label} 时发生异常: {ex}");
            }
        }

        public void RemoveBionic(Pawn pawn, BodyPartRecord part, Hediff hediffToRemove)
        {
            if (pawn == null || hediffToRemove == null) return;

            try
            {
                // 1. 尝试生成物品
                ThingDef spawnThingDef = hediffToRemove.def.spawnThingOnRemoved;
                if (spawnThingDef == null && part != null)
                {
                    // 如果 Hediff 本身没定义掉落物，且是移除整个部位（天然器官），尝试从部位定义获取
                    // 只有在确定是“移除”操作且该部位是干净的时候才生成天然器官
                    if (MedicalRecipesUtility.IsCleanAndDroppable(pawn, part))
                    {
                        spawnThingDef = part.def.spawnThingOnRemoved;
                    }
                }

                if (spawnThingDef != null)
                {
                    GenSpawn.Spawn(spawnThingDef, Position, Map);
                }

                // 2. 移除 Hediff
                pawn.health.RemoveHediff(hediffToRemove);

                // 如果拆除的是替换型义体，恢复原部位
                if (part != null && !pawn.health.hediffSet.GetNotMissingParts().Contains(part))
                {
                    pawn.health.RestorePart(part);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 移除义体 {hediffToRemove.def.defName} 时发生异常: {ex}");
            }
        }

        public void FullRepair(Pawn pawn)
        {
            if (pawn == null) return;

            try
            {
                // 1. 恢复所有缺失部位
                var missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
                foreach (var part in missingParts)
                {
                    pawn.health.RestorePart(part.Part);
                }

                // 2. 移除所有负面状态
                var toRemove = pawn.health.hediffSet.hediffs
                    .Where(h => h is Hediff_Injury || h is Hediff_Addiction || h.def.isBad ||
                                h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null)
                    .ToList();

                foreach (var h in toRemove)
                {
                    if (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null)
                    {
                        RemoveBionic(pawn, h.Part, h);
                    }
                    else
                    {
                        pawn.health.RemoveHediff(h);
                    }
                }

                Messages.Message("FullyAutoOmniSurgeon_FullRepairComplete".Translate(pawn.LabelShort),
                    MessageTypeDefOf.TaskCompletion);
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 进行全自动修复时发生异常: {ex}");
            }
        }

        public void ApplyTemplate(Pawn pawn, SurgeryTemplate template)
        {
            if (pawn == null || template == null) return;

            try
            {
                foreach (var entry in template.partToBionicMap)
                {
                    var part = pawn.RaceProps.body.AllParts.FirstOrDefault(p =>
                        p.Label == entry.Key || p.def.defName == entry.Key);
                    var bionicDef = DefDatabase<HediffDef>.GetNamedSilentFail(entry.Value);

                    if (part != null && bionicDef != null)
                    {
                        // 检查 HAR 限制提示
                        if (HarmonyLib.AccessTools.TypeByName("AlienRace.RaceRestrictionSettings") != null)
                        {
                            if (IsRestrictedFor(pawn, bionicDef, part))
                            {
                                Messages.Message(
                                    "FullyAutoOmniSurgeon_RaceRestrictedWarning".Translate(bionicDef.label),
                                    MessageTypeDefOf.CautionInput, false);
                            }
                        }

                        InstallBionic(pawn, part, bionicDef);
                    }
                }

                Messages.Message(
                    "FullyAutoOmniSurgeon_TemplateApplied".Translate(pawn.LabelShort, template.templateName),
                    MessageTypeDefOf.TaskCompletion);
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 应用模板 {template.templateName} 时发生异常: {ex}");
            }
        }

        public static bool IsRestrictedFor(Pawn pawn, HediffDef hDef, BodyPartRecord part)
        {
            // 通过寻找是否有对应的 RecipeDef 被 HAR 限制来判断
            var recipes = DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.addsHediff == hDef && (r.appliedOnFixedBodyParts.NullOrEmpty() ||
                                                     r.appliedOnFixedBodyParts.Contains(part.def)));

            if (!recipes.Any()) return false;

            var harType = HarmonyLib.AccessTools.TypeByName("AlienRace.RaceRestrictionSettings");
            if (harType == null) return false;

            var canDoMethod = HarmonyLib.AccessTools.Method(harType, "CanDoRecipe");
            if (canDoMethod == null) return false;

            foreach (var r in recipes)
            {
                try
                {
                    // HAR 的 CanDoRecipe(RecipeDef recipe, ThingDef race)
                    bool canDo = (bool)canDoMethod.Invoke(null, new object[] { r, pawn.def });
                    if (canDo) return false; // 只要有一个配方是允许的，就不算完全屏蔽
                }
                catch
                {
                }
            }

            return true; // 所有相关配方都被限制了
        }

        public void SaveAsTemplate(Pawn pawn, string name)
        {
            if (pawn == null) return;
            var template = new SurgeryTemplate { templateName = name };
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h.Part != null && (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null))
                {
                    template.partToBionicMap[h.Part.Label] = h.def.defName;
                }
            }

            templates.Add(template);
        }
    }

    public class Window_OmniAutoSurgeonUI : Window
    {
        private Pawn pawn;
        private Building_FullyAutoOmniSurgeon surgeon;
        private Vector2 scrollPos;

        public override Vector2 InitialSize => new Vector2(600f, 700f);

        public Window_OmniAutoSurgeonUI(Pawn pawn, Building_FullyAutoOmniSurgeon surgeon)
        {
            this.pawn = pawn;
            this.surgeon = surgeon;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 40f),
                "FullyAutoOmniSurgeon_PanelTitle".Translate(pawn.LabelCap));
            Text.Font = GameFont.Small;

            float x = inRect.width - 150f;
            if (Widgets.ButtonText(new Rect(x, 0, 140f, 30f), "FullyAutoOmniSurgeon_SaveAsTemplate".Translate()))
            {
                Find.WindowStack.Add(new Dialog_NameTemplate(name => surgeon.SaveAsTemplate(pawn, name)));
            }

            if (surgeon.templates.Any() && Widgets.ButtonText(new Rect(x - 150f, 0, 140f, 30f),
                    "FullyAutoOmniSurgeon_ApplyTemplate".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (var t in surgeon.templates)
                {
                    options.Add(new FloatMenuOption(t.templateName, () => surgeon.ApplyTemplate(pawn, t)));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            float y = 50f;

            // 简单列出所有身体部位
            Rect outRect = new Rect(0, y, inRect.width, inRect.height - y - 60f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, pawn.RaceProps.body.AllParts.Count * 30f);

            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            float curY = 0;
            foreach (var part in pawn.RaceProps.body.AllParts)
            {
                Rect rowRect = new Rect(0, curY, viewRect.width, 25f);
                Widgets.Label(new Rect(0, curY, 200f, 25f), part.LabelCap);

                // 显示当前状态
                var hediffs = pawn.health.hediffSet.hediffs.Where(h => h.Part == part).ToList();
                string status = hediffs.Any()
                    ? string.Join(", ", hediffs.Select(h => h.LabelCap))
                    : "FullyAutoOmniSurgeon_StatusNormal".Translate().ToString();
                Widgets.Label(new Rect(210, curY, 200f, 25f), status);

                if (Widgets.ButtonText(new Rect(420, curY, 60f, 25f), "FullyAutoOmniSurgeon_Install".Translate()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    // 这里应该筛选出所有可能的义体 Def
                    var bionicDefs = DefDatabase<HediffDef>.AllDefs
                        .Where(d => d.spawnThingOnRemoved != null || d.hediffClass.Name.Contains("Bionic") ||
                                    d.label.Contains("仿生"))
                        .OrderBy(d => d.label);

                    foreach (var def in bionicDefs)
                    {
                        string label = def.LabelCap;
                        bool restricted = Building_FullyAutoOmniSurgeon.IsRestrictedFor(pawn, def, part);
                        if (restricted)
                        {
                            label = "<color=red>" + label + "FullyAutoOmniSurgeon_RaceRestricted".Translate() +
                                    "</color>";
                        }

                        options.Add(new FloatMenuOption(label, () => surgeon.InstallBionic(pawn, part, def)));
                    }

                    Find.WindowStack.Add(new FloatMenu(options));
                }

                if (hediffs.Any() && Widgets.ButtonText(new Rect(490, curY, 60f, 25f),
                        "FullyAutoOmniSurgeon_Remove".Translate()))
                {
                    foreach (var h in hediffs) surgeon.RemoveBionic(pawn, part, h);
                }

                curY += 30f;
            }

            Widgets.EndScrollView();
        }
    }

    public class Dialog_NameTemplate : Window
    {
        private string name = "FullyAutoOmniSurgeon_NewTemplateName".Translate();
        private Action<string> onConfirm;

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public Dialog_NameTemplate(Action<string> onConfirm)
        {
            this.onConfirm = onConfirm;
            this.doCloseButton = false;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), "FullyAutoOmniSurgeon_InputTemplateName".Translate());
            name = Widgets.TextField(new Rect(0, 40f, inRect.width, 30f), name);
            if (Widgets.ButtonText(new Rect(0, 80f, inRect.width, 30f), "FullyAutoOmniSurgeon_OK".Translate()))
            {
                onConfirm?.Invoke(name);
                this.Close();
            }
        }
    }
}
