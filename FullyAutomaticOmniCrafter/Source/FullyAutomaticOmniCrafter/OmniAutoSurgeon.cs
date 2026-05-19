using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public static class OmniAutoSurgeonSurgeryContext
    {
        [ThreadStatic]
        private static int activeDepth;

        public static bool IsActive => activeDepth > 0;

        public static IDisposable Enter()
        {
            activeDepth++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (activeDepth > 0) activeDepth--;
            }
        }
    }

    public enum OmniSurgeonOperationType
    {
        Recipe,
        InstallImplant,
        RemoveImplant
    }

    public class OmniSurgeonOperation : IExposable
    {
        public OmniSurgeonOperationType operationType = OmniSurgeonOperationType.InstallImplant;
        public string recipeDefName;
        public string hediffDefName;
        public string partPath;
        public string partDefName;
        public string partLabel;

        public static OmniSurgeonOperation CreateRecipe(RecipeDef recipe, BodyPartRecord part)
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.Recipe,
                recipeDefName = recipe != null ? recipe.defName : null,
                partPath = Building_FullyAutoOmniSurgeon.GetPartPath(part),
                partDefName = part != null && part.def != null ? part.def.defName : null,
                partLabel = part != null ? part.Label : null
            };
        }

        public static OmniSurgeonOperation CreateInstall(HediffDef hediff, BodyPartRecord part)
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.InstallImplant,
                hediffDefName = hediff != null ? hediff.defName : null,
                partPath = Building_FullyAutoOmniSurgeon.GetPartPath(part),
                partDefName = part != null && part.def != null ? part.def.defName : null,
                partLabel = part != null ? part.Label : null
            };
        }

        public static OmniSurgeonOperation CreateRemove(HediffDef hediff, BodyPartRecord part)
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.RemoveImplant,
                hediffDefName = hediff != null ? hediff.defName : null,
                partPath = Building_FullyAutoOmniSurgeon.GetPartPath(part),
                partDefName = part != null && part.def != null ? part.def.defName : null,
                partLabel = part != null ? part.Label : null
            };
        }

        public OmniSurgeonOperation Clone()
        {
            return new OmniSurgeonOperation
            {
                operationType = operationType,
                recipeDefName = recipeDefName,
                hediffDefName = hediffDefName,
                partPath = partPath,
                partDefName = partDefName,
                partLabel = partLabel
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref operationType, "operationType", OmniSurgeonOperationType.InstallImplant);
            Scribe_Values.Look(ref recipeDefName, "recipeDefName");
            Scribe_Values.Look(ref hediffDefName, "hediffDefName");
            Scribe_Values.Look(ref partPath, "partPath");
            Scribe_Values.Look(ref partDefName, "partDefName");
            Scribe_Values.Look(ref partLabel, "partLabel");
        }
    }

    public class SurgeryTemplate : IExposable
    {
        public string templateName;

        // 记录部位路径 (unique path or defName + index) 和对应的 义体 HediffDef
        // 这里简单点，记录 BodyPartDef 的 defName 可能会有重复部位问题，
        // 但对于大多数义体（眼、臂、腿）通常是通用的。
        // 更好的做法是记录 BodyPartRecord 的某种标识。
        public Dictionary<string, string> partToBionicMap = new Dictionary<string, string>();
        public List<OmniSurgeonOperation> operations = new List<OmniSurgeonOperation>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref templateName, "templateName");
            Scribe_Collections.Look(ref partToBionicMap, "partToBionicMap", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref operations, "operations", LookMode.Deep);
            if (operations == null) operations = new List<OmniSurgeonOperation>();
            if (partToBionicMap == null) partToBionicMap = new Dictionary<string, string>();

            // 兼容旧存档: 旧模板只有 partToBionicMap 时，自动转换为新操作列表。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && operations.Count == 0 && partToBionicMap.Count > 0)
            {
                foreach (var pair in partToBionicMap)
                {
                    operations.Add(new OmniSurgeonOperation
                    {
                        operationType = OmniSurgeonOperationType.InstallImplant,
                        hediffDefName = pair.Value,
                        partDefName = pair.Key,
                        partLabel = pair.Key,
                        partPath = string.Empty
                    });
                }
            }
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

        public static readonly Texture2D IconSelectOccupant =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_SelectOccupant", true) ??
            BaseContent.WhiteTex;
    }

    /// <summary>
    /// 全自动医疗改造舱 FullyAutoOmniSurgeon
    /// 一个类似医疗床或休眠舱的建筑，可以快速为特定对象快速批量添加删除身体部位和义肢等增强部件、以及修复损伤和医疗受伤的建筑。
    /// 支持按模板安装、拆解。
    /// 支持手动按部位编辑和安装。
    /// 忽略材料限制。 
    /// </summary>
    public class Building_FullyAutoOmniSurgeon : Building_Enterable, IThingHolderWithDrawnPawn
    {
        public List<SurgeryTemplate> templates = new List<SurgeryTemplate>();

        public Pawn Occupant => innerContainer.FirstOrDefault() as Pawn;

        public float HeldPawnDrawPos_Y => this.DrawPos.y + 0.03658537f;

        public float HeldPawnBodyAngle => this.Rotation.AsAngle;

        public PawnPosture HeldPawnPosture => PawnPosture.LayingOnGroundFaceUp;

        public override Vector3 PawnDrawOffset
        {
            get
            {
                // 手术台是 3x2 建筑。中心在 1.5, 1.0 (相对于左下角)。
                // 我们希望 Pawn 在中间位置。
                // 如果 Rotation 是 North/South, 3x2 实际上是宽3高2。中心是相对于(0,0)的。
                // 但是 RimWorld 的 DrawPos 已经是建筑的中心。
                // 所以 Vector3.zero 应该就是建筑的中心。
                return Vector3.zero;
            }
        }

        public override bool IsContentsSuspended => false;

        public override void ExposeData()
        {
            base.ExposeData();
            // innerContainer 已经在 base.ExposeData() 中处理了（如果它是 Building_Enterable）
            // 但 Building_Enterable 使用的是 Scribe_Deep.Look<ThingOwner>(ref this.innerContainer, "innerContainer", (object) this);
            // 我们的类目前没有重写 innerContainer 字段，所以直接用父类的即可。
            Scribe_Collections.Look(ref templates, "templates", LookMode.Deep);
            if (templates == null) templates = new List<SurgeryTemplate>();
            // 注意：selectedPawn 已经在 base.ExposeData() 中处理了。
            // 为了兼容旧存档，我们可以保留对 selectedPawn 的显式加载逻辑，但通常 base 已经做了。
            // 如果 base.ExposeData 没有处理，我们需要手动处理。
            // 检查 Building_Enterable.ExposeData 确实有：Scribe_References.Look<Pawn>(ref this.selectedPawn, "selectedPawn");
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            if (this.Occupant != null)
            {
                // yield return new Command_Action
                // {
                //     defaultLabel = "CommandSelectContainedPawn".Translate(),
                //     defaultDesc = "CommandSelectContainedPawnDesc".Translate(),
                //     icon = FullyAutoOmniSurgeonTex.IconSelectOccupant,
                //     action = () =>
                //     {
                //         Find.Selector.ClearSelection();
                //         Find.Selector.Select(this.Occupant);
                //     }
                // };

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
            else if (this.selectedPawn != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandCancelLoad".Translate(),
                    defaultDesc = "CommandCancelLoadDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                    action = () => { this.selectedPawn = null; }
                };
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_SelectOccupant".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_SelectOccupantDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconSelectOccupant,
                    action = SelectOccupant
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

        public void EjectContents()
        {
            this.selectedPawn = null;
            foreach (Thing thing in (IEnumerable<Thing>)this.innerContainer)
            {
                if (thing is Pawn pawn)
                {
                    PawnComponentsUtility.AddComponentsForSpawn(pawn);
                    // 清理工作队列，防止出来后执行过时的 Job
                    if (pawn.jobs != null)
                    {
                        pawn.jobs.StopAll();
                    }
                }
            }

            this.innerContainer.TryDropAll(this.def.hasInteractionCell ? this.InteractionCell : this.Position, this.Map,
                ThingPlaceMode.Near);
        }

        public override void TryAcceptPawn(Pawn pawn)
        {
            if (this.innerContainer.Count > 0) return;
            this.selectedPawn = pawn;
            bool wasSpawned = pawn.Spawned;
            bool deselected = pawn.DeSpawnOrDeselect();
            if (this.innerContainer.TryAddOrTransfer(pawn))
            {
                // 可以记录进入时间等
            }

            if (wasSpawned && deselected)
            {
                Find.Selector.Select(pawn, false, false);
            }
        }

        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            // Draw the building first, then draw occupant so the pawn is not hidden under building realtime graphics.
            base.DynamicDrawPhaseAt(phase, drawLoc, flip);
            if (this.Occupant != null)
            {
                this.Occupant.Drawer.renderer.DynamicDrawPhaseAt(phase, drawLoc + this.PawnDrawOffset, neverAimWeapon: true);
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (this.Occupant != null)
            {
                if (!text.NullOrEmpty())
                {
                    text += "\n";
                }

                text += "Occupant".Translate() + ": " + this.Occupant.LabelCap;
            }

            return text;
        }

        public override AcceptanceReport CanAcceptPawn(Pawn pawn)
        {
            if (this.innerContainer.Count > 0) return "Occupied".Translate();
            if (this.selectedPawn != null && this.selectedPawn != pawn) return false;
            if (!pawn.RaceProps.IsFlesh && !pawn.RaceProps.IsMechanoid) return false;
            return true;
        }

        private void SelectOccupant()
        {
            Find.WindowStack.Add(new OmniAutoSurgeon_Dialog_SelectPawn(this, (pawn) =>
            {
                if (this.innerContainer.Count > 0)
                {
                    Messages.Message("FullyAutoOmniSurgeon_Occupied".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                if (pawn.Dead)
                {
                    Messages.Message("FullyAutoOmniSurgeon_DeadPawn".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                this.TryAcceptPawn(pawn);
            }));
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

                if (spawnThingDef != null && this.Map != null)
                {
                    Thing thing = ThingMaker.MakeThing(spawnThingDef);
                    ForceLegendaryQuality(thing);
                    IntVec3 dropCell = this.def != null && this.def.hasInteractionCell ? this.InteractionCell : this.Position;
                    GenPlace.TryPlaceThing(thing, dropCell, this.Map, ThingPlaceMode.Near);
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
                if (!template.operations.NullOrEmpty())
                {
                    for (int i = 0; i < template.operations.Count; i++)
                    {
                        string reason;
                        ExecuteOperation(pawn, template.operations[i], out reason);
                    }

                    Messages.Message(
                        "FullyAutoOmniSurgeon_TemplateApplied".Translate(pawn.LabelShort, template.templateName),
                        MessageTypeDefOf.TaskCompletion);
                    return;
                }

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

        public void SaveOperationTemplate(string name, List<OmniSurgeonOperation> operations)
        {
            if (name.NullOrEmpty() || operations == null || operations.Count == 0) return;

            SurgeryTemplate existing = templates.FirstOrDefault(t => t.templateName == name);
            if (existing == null)
            {
                existing = new SurgeryTemplate { templateName = name };
                templates.Add(existing);
            }

            existing.operations = operations.Select(o => o.Clone()).ToList();
            existing.partToBionicMap.Clear();
        }

        public BodyPartRecord ResolvePart(Pawn pawn, OmniSurgeonOperation operation)
        {
            if (pawn == null || operation == null) return null;

            if (!operation.partPath.NullOrEmpty())
            {
                BodyPartRecord byPath = ResolvePartFromPath(pawn, operation.partPath);
                if (byPath != null) return byPath;
            }

            BodyPartRecord byDef = null;
            if (!operation.partDefName.NullOrEmpty())
            {
                byDef = pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.def != null && p.def.defName == operation.partDefName);
            }
            if (byDef != null) return byDef;

            if (!operation.partLabel.NullOrEmpty())
            {
                return pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.Label == operation.partLabel || p.LabelCap == operation.partLabel);
            }

            return null;
        }

        public static string GetPartPath(BodyPartRecord part)
        {
            if (part == null) return string.Empty;

            List<int> indices = new List<int>();
            BodyPartRecord current = part;
            while (current != null && current.parent != null)
            {
                int idx = current.parent.parts.IndexOf(current);
                if (idx < 0) break;
                indices.Add(idx);
                current = current.parent;
            }

            indices.Reverse();
            return string.Join("/", indices.Select(i => i.ToString()).ToArray());
        }

        private static BodyPartRecord ResolvePartFromPath(Pawn pawn, string path)
        {
            if (pawn == null || pawn.RaceProps == null || pawn.RaceProps.body == null) return null;
            if (path.NullOrEmpty()) return pawn.RaceProps.body.corePart;

            BodyPartRecord current = pawn.RaceProps.body.corePart;
            string[] tokens = path.Split('/');
            for (int i = 0; i < tokens.Length; i++)
            {
                int index;
                if (!int.TryParse(tokens[i], out index)) return null;
                if (current.parts == null || index < 0 || index >= current.parts.Count) return null;
                current = current.parts[index];
            }
            return current;
        }

        public bool ExecuteOperation(Pawn pawn, OmniSurgeonOperation operation, out string failReason)
        {
            failReason = null;
            if (pawn == null || operation == null)
            {
                failReason = "Invalid operation";
                return false;
            }

            try
            {
                BodyPartRecord part = ResolvePart(pawn, operation);
                switch (operation.operationType)
                {
                    case OmniSurgeonOperationType.Recipe:
                    {
                        RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(operation.recipeDefName);
                        if (recipe == null || recipe.Worker == null)
                        {
                            failReason = "Recipe not found";
                            return false;
                        }

                        if (recipe.targetsBodyPart && part == null)
                        {
                            failReason = "Body part missing";
                            return false;
                        }

                        Pawn billDoer = recipe.Worker is Recipe_Surgery ? SelectOperationSurgeon(pawn) : null;
                        if (billDoer == null) billDoer = pawn;

                        HashSet<int> beforeThingIds = CaptureMapThingIds(this.Map);

                        using (OmniAutoSurgeonSurgeryContext.Enter())
                        {
                            recipe.Worker.ApplyOnPawn(pawn, part, billDoer, new List<Thing>(), null);
                        }

                        PromoteNewMapThingsToLegendary(this.Map, beforeThingIds);
                        return true;
                    }
                    case OmniSurgeonOperationType.InstallImplant:
                    {
                        HediffDef hediff = DefDatabase<HediffDef>.GetNamedSilentFail(operation.hediffDefName);
                        if (hediff == null || part == null)
                        {
                            failReason = "Implant or part missing";
                            return false;
                        }

                        InstallBionic(pawn, part, hediff);
                        return true;
                    }
                    case OmniSurgeonOperationType.RemoveImplant:
                    {
                        HediffDef hediff = DefDatabase<HediffDef>.GetNamedSilentFail(operation.hediffDefName);
                        if (part == null)
                        {
                            failReason = "Body part missing";
                            return false;
                        }

                        Hediff target = pawn.health.hediffSet.hediffs.FirstOrDefault(h =>
                            h.Part == part && (hediff == null || h.def == hediff));
                        if (target == null)
                        {
                            failReason = "Target hediff missing";
                            return false;
                        }

                        RemoveBionic(pawn, part, target);
                        return true;
                    }
                    default:
                        failReason = "Unknown operation type";
                        return false;
                }
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                Log.Error($"[OmniAutoSurgeon] 执行操作时发生异常: {ex}");
                return false;
            }
        }

        private Pawn SelectOperationSurgeon(Pawn patient)
        {
            if (Map == null)
            {
                return patient != null && !patient.Dead ? patient : null;
            }

            Pawn best = Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != null && !p.Dead && !p.Downed && p.health != null && !p.health.InPainShock)
                .OrderByDescending(p => p.skills != null ? p.skills.GetSkill(SkillDefOf.Medicine).Level : 0)
                .FirstOrDefault();

            if (best != null) return best;

            if (patient != null && !patient.Dead) return patient;
            return null;
        }

        private static HashSet<int> CaptureMapThingIds(Map map)
        {
            if (map == null || map.listerThings == null) return null;

            List<Thing> allThings = map.listerThings.AllThings;
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < allThings.Count; i++)
            {
                Thing thing = allThings[i];
                if (thing != null)
                {
                    ids.Add(thing.thingIDNumber);
                }
            }

            return ids;
        }

        private static void PromoteNewMapThingsToLegendary(Map map, HashSet<int> beforeThingIds)
        {
            if (map == null || beforeThingIds == null || map.listerThings == null) return;

            List<Thing> allThings = map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                Thing thing = allThings[i];
                if (thing == null || beforeThingIds.Contains(thing.thingIDNumber)) continue;
                ForceLegendaryQuality(thing);
            }
        }

        private static void ForceLegendaryQuality(Thing thing)
        {
            if (thing == null) return;

            CompQuality qualityComp = thing.TryGetComp<CompQuality>();
            if (qualityComp != null)
            {
                qualityComp.SetQuality(QualityCategory.Legendary, ArtGenerationContext.Colony);
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
        private readonly Pawn pawn;
        private readonly Building_FullyAutoOmniSurgeon surgeon;
        private readonly List<OmniSurgeonOperation> workingOperations = new List<OmniSurgeonOperation>();
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;

        public override Vector2 InitialSize => new Vector2(1180f, 760f);

        public Window_OmniAutoSurgeonUI(Pawn pawn, Building_FullyAutoOmniSurgeon surgeon)
        {
            this.pawn = pawn;
            this.surgeon = surgeon;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "FullyAutoOmniSurgeon_PanelTitle".Translate(pawn.LabelCap));
            Text.Font = GameFont.Small;

            float toolbarY = 4f;
            float x = inRect.width - 140f;
            if (Widgets.ButtonText(new Rect(x, toolbarY, 136f, 28f), "FullyAutoOmniSurgeon_SaveAsTemplate".Translate()))
            {
                Find.WindowStack.Add(new Dialog_NameTemplate(name => surgeon.SaveOperationTemplate(name, workingOperations)));
            }

            if (surgeon.templates.Any() && Widgets.ButtonText(new Rect(x - 146f, toolbarY, 136f, 28f), "FullyAutoOmniSurgeon_ApplyTemplate".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (SurgeryTemplate t in surgeon.templates)
                {
                    SurgeryTemplate localTemplate = t;
                    options.Add(new FloatMenuOption(localTemplate.templateName, delegate
                    {
                        workingOperations.Clear();
                        if (!localTemplate.operations.NullOrEmpty())
                        {
                            for (int i = 0; i < localTemplate.operations.Count; i++)
                            {
                                workingOperations.Add(localTemplate.operations[i].Clone());
                            }
                        }
                        else
                        {
                            surgeon.ApplyTemplate(pawn, localTemplate);
                        }
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Leave room for RimWorld's built-in bottom close button area.
            const float bottomReservedForCloseButton = 42f;
            Rect contentRect = new Rect(0f, 42f, inRect.width, inRect.height - 42f - bottomReservedForCloseButton);
            float gap = 10f;
            float leftWidth = Mathf.Floor(contentRect.width * 0.56f);
            Rect leftRect = new Rect(contentRect.x, contentRect.y, leftWidth - gap * 0.5f, contentRect.height);
            Rect rightRect = new Rect(leftRect.xMax + gap, contentRect.y, contentRect.width - leftRect.width - gap, contentRect.height);

            Widgets.DrawMenuSection(leftRect);
            Widgets.DrawMenuSection(rightRect);

            DrawLeftColumn(leftRect.ContractedBy(8f));
            DrawRightColumn(rightRect.ContractedBy(8f));
        }

        private void DrawLeftColumn(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "身体部位状态（快速加入操作）");
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(64f, parts.Count * 34f));

            Widgets.BeginScrollView(outRect, ref leftScrollPos, viewRect);
            float curY = 0f;
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                Rect rowRect = new Rect(0f, curY, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                int depth = GetPartDepth(part);
                float indent = depth * 12f;
                Widgets.Label(new Rect(4f + indent, curY + 5f, 210f - indent, 24f), part.LabelCap);

                string status = GetPartStatus(part);
                Widgets.Label(new Rect(214f, curY + 5f, 260f, 24f), status);

                if (Widgets.ButtonText(new Rect(viewRect.width - 160f, curY + 2f, 74f, 26f), "+植入"))
                {
                    OpenInstallOperationMenuForPart(part);
                }

                bool canRemove = pawn.health.hediffSet.hediffs.Any(h => h.Part == part && (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null));
                if (canRemove && Widgets.ButtonText(new Rect(viewRect.width - 82f, curY + 2f, 78f, 26f), "+移除"))
                {
                    OpenRemoveOperationMenuForPart(part);
                }

                curY += 34f;
            }
            Widgets.EndScrollView();
        }

        private void DrawRightColumn(Rect rect)
        {
            float y = rect.y;
            float topButtonWidth = (rect.width - 8f) * 0.5f;

            if (Widgets.ButtonText(new Rect(rect.x, y, topButtonWidth, 30f), "搜索并添加手术"))
            {
                Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_AddRecipeOperation(pawn, delegate(OmniSurgeonOperation op)
                {
                    if (op != null) workingOperations.Add(op);
                }));
            }

            if (Widgets.ButtonText(new Rect(rect.x + topButtonWidth + 8f, y, topButtonWidth, 30f), "搜索并添加植入"))
            {
                Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_AddImplantOperation(pawn, delegate(OmniSurgeonOperation op)
                {
                    if (op != null) workingOperations.Add(op);
                }));
            }

            y += 36f;

            float bottomAreaHeight = 42f;
            Rect listOutRect = new Rect(rect.x, y, rect.width, rect.height - y - bottomAreaHeight);
            Rect listViewRect = new Rect(0f, 0f, listOutRect.width - 16f, Mathf.Max(60f, workingOperations.Count * 34f));
            Widgets.BeginScrollView(listOutRect, ref rightScrollPos, listViewRect);

            float curY = 0f;
            for (int i = 0; i < workingOperations.Count; i++)
            {
                Rect rowRect = new Rect(0f, curY, listViewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Widgets.Label(new Rect(4f, curY + 5f, 28f, 24f), (i + 1).ToString());
                Widgets.Label(new Rect(34f, curY + 5f, listViewRect.width - 130f, 24f), GetOperationLabel(workingOperations[i]));

                if (Widgets.ButtonText(new Rect(listViewRect.width - 92f, curY + 2f, 28f, 26f), "↑") && i > 0)
                {
                    OmniSurgeonOperation tmp = workingOperations[i - 1];
                    workingOperations[i - 1] = workingOperations[i];
                    workingOperations[i] = tmp;
                }

                if (Widgets.ButtonText(new Rect(listViewRect.width - 62f, curY + 2f, 28f, 26f), "↓") && i < workingOperations.Count - 1)
                {
                    OmniSurgeonOperation tmp = workingOperations[i + 1];
                    workingOperations[i + 1] = workingOperations[i];
                    workingOperations[i] = tmp;
                }

                if (Widgets.ButtonText(new Rect(listViewRect.width - 32f, curY + 2f, 28f, 26f), "X"))
                {
                    workingOperations.RemoveAt(i);
                    i--;
                }

                curY += 34f;
            }
            Widgets.EndScrollView();

            Rect bottomRect = new Rect(rect.x, rect.yMax - 36f, rect.width, 32f);
            float executeWidth = rect.width * 0.62f;
            if (Widgets.ButtonText(new Rect(bottomRect.x, bottomRect.y, executeWidth, bottomRect.height), "执行操作模板"))
            {
                ExecuteWorkingOperations();
            }

            if (Widgets.ButtonText(new Rect(bottomRect.x + executeWidth + 8f, bottomRect.y, rect.width - executeWidth - 8f, bottomRect.height), "清空"))
            {
                workingOperations.Clear();
            }
        }

        private void ExecuteWorkingOperations()
        {
            if (workingOperations.Count == 0)
            {
                Messages.Message("操作列表为空。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            int success = 0;
            int failed = 0;
            string lastError = null;

            for (int i = 0; i < workingOperations.Count; i++)
            {
                string reason;
                if (surgeon.ExecuteOperation(pawn, workingOperations[i], out reason))
                {
                    success++;
                }
                else
                {
                    failed++;
                    lastError = reason;
                }
            }

            if (failed == 0)
            {
                Messages.Message($"已执行 {success} 项操作。", MessageTypeDefOf.TaskCompletion, false);
            }
            else
            {
                Messages.Message($"执行完成: 成功 {success}，失败 {failed}。{(lastError.NullOrEmpty() ? string.Empty : "最后错误: " + lastError)}", MessageTypeDefOf.CautionInput, false);
            }
        }

        private void OpenInstallOperationMenuForPart(BodyPartRecord part)
        {
            IEnumerable<HediffDef> candidates = DefDatabase<HediffDef>.AllDefs
                .Where(h => h != null && (h.countsAsAddedPartOrImplant || h.addedPartProps != null))
                .OrderBy(h => h.label);

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (HediffDef def in candidates)
            {
                string label = def.LabelCap;
                bool restricted = Building_FullyAutoOmniSurgeon.IsRestrictedFor(pawn, def, part);
                if (restricted)
                {
                    label = "<color=red>" + label + "（受种族限制）</color>";
                }

                HediffDef localDef = def;
                options.Add(new FloatMenuOption(label, delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateInstall(localDef, part));
                }));
            }

            if (options.Count == 0)
            {
                Messages.Message("没有可添加的植入物。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenRemoveOperationMenuForPart(BodyPartRecord part)
        {
            List<Hediff> removable = pawn.health.hediffSet.hediffs
                .Where(h => h.Part == part && (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null))
                .ToList();
            if (removable.Count == 0) return;

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < removable.Count; i++)
            {
                Hediff local = removable[i];
                options.Add(new FloatMenuOption("移除: " + local.LabelCap, delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateRemove(local.def, part));
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private string GetPartStatus(BodyPartRecord part)
        {
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs.Where(h => h.Part == part && h.Visible).ToList();
            if (hediffs.Count == 0) return "正常";
            return string.Join(", ", hediffs.Select(h => h.LabelCap).ToArray());
        }

        private static int GetPartDepth(BodyPartRecord part)
        {
            int depth = 0;
            BodyPartRecord current = part;
            while (current != null && current.parent != null)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }

        private string GetOperationLabel(OmniSurgeonOperation operation)
        {
            if (operation == null) return "<null>";

            BodyPartRecord part = surgeon.ResolvePart(pawn, operation);
            string partName = part != null ? part.LabelCap : (operation.partLabel ?? operation.partDefName ?? "未指定部位");

            if (operation.operationType == OmniSurgeonOperationType.Recipe)
            {
                RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(operation.recipeDefName);
                if (recipe == null) return "手术缺失: " + operation.recipeDefName;
                string label = recipe.Worker != null ? recipe.Worker.GetLabelWhenUsedOn(pawn, part).ToString() : recipe.LabelCap.ToString();
                if (recipe.targetsBodyPart) label += " (" + partName + ")";
                return label;
            }

            HediffDef h = DefDatabase<HediffDef>.GetNamedSilentFail(operation.hediffDefName);
            string hLabel = h != null ? h.LabelCap.ToString() : operation.hediffDefName;
            if (operation.operationType == OmniSurgeonOperationType.InstallImplant)
            {
                return "安装 " + hLabel + " -> " + partName;
            }

            return "移除 " + hLabel + " <- " + partName;
        }
    }

    public class Dialog_OmniAutoSurgeon_AddRecipeOperation : Window
    {
        private readonly Pawn pawn;
        private readonly Action<OmniSurgeonOperation> onSelected;
        private readonly List<RecipeCandidate> cached = new List<RecipeCandidate>();
        private string searchText = string.Empty;
        private Vector2 scrollPos;
        private bool pinyinIndexPrepared;
        private bool pinyinSearchEnabled;

        private struct RecipeCandidate
        {
            public RecipeDef recipe;
            public BodyPartRecord part;
            public string label;
        }

        public override Vector2 InitialSize => new Vector2(760f, 700f);

        public Dialog_OmniAutoSurgeon_AddRecipeOperation(Pawn pawn, Action<OmniSurgeonOperation> onSelected)
        {
            this.pawn = pawn;
            this.onSelected = onSelected;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            RebuildCache();
        }

        private void RebuildCache()
        {
            cached.Clear();
            string lower = searchText.NullOrEmpty() ? string.Empty : searchText.ToLower();

            List<RecipeDef> defs = DefDatabase<RecipeDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                RecipeDef recipe = defs[i];
                if (recipe == null || recipe.Worker == null || !(recipe.Worker is Recipe_Surgery)) continue;

                if (recipe.targetsBodyPart)
                {
                    IEnumerable<BodyPartRecord> parts = pawn?.RaceProps?.body?.AllParts ?? Enumerable.Empty<BodyPartRecord>();
                    if (!recipe.appliedOnFixedBodyParts.NullOrEmpty())
                    {
                        parts = parts.Where(p => p != null && p.def != null && recipe.appliedOnFixedBodyParts.Contains(p.def));
                    }

                    bool anyPart = false;
                    foreach (BodyPartRecord part in parts)
                    {
                        anyPart = true;
                        string opLabel;
                        try
                        {
                            opLabel = recipe.Worker.GetLabelWhenUsedOn(pawn, part).CapitalizeFirst();
                        }
                        catch
                        {
                            opLabel = !recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "Unknown surgery");
                        }

                        string label = opLabel + " (" + part.LabelCap + ")";
                        if (!MatchesSearch(recipe, label, lower)) continue;
                        cached.Add(new RecipeCandidate { recipe = recipe, part = part, label = label });
                    }

                    if (!anyPart)
                    {
                        string fallbackLabel = (!recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "Unknown surgery")) + " (未匹配部位)";
                        if (MatchesSearch(recipe, fallbackLabel, lower))
                        {
                            cached.Add(new RecipeCandidate { recipe = recipe, part = null, label = fallbackLabel });
                        }
                    }
                }
                else
                {
                    string label;
                    try
                    {
                        label = recipe.Worker.GetLabelWhenUsedOn(pawn, null).CapitalizeFirst();
                    }
                    catch
                    {
                        label = !recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "Unknown surgery");
                    }

                    if (!MatchesSearch(recipe, label, lower)) continue;
                    cached.Add(new RecipeCandidate { recipe = recipe, part = null, label = label });
                }
            }

            cached.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
        }

        private bool MatchesSearch(RecipeDef recipe, string label, string lower)
        {
            if (lower.NullOrEmpty()) return true;
            if ((label ?? string.Empty).ToLower().Contains(lower)) return true;
            if ((recipe.defName ?? string.Empty).ToLower().Contains(lower)) return true;
            if ((recipe.label ?? string.Empty).ToLower().Contains(lower)) return true;
            if (pinyinSearchEnabled && PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(recipe, lower)) return true;
            return false;
        }

        private void TryEnablePinyinSearch()
        {
            if (!pinyinIndexPrepared)
            {
                PinyinSearchEngine.EnsureIndexed(DefDatabase<RecipeDef>.AllDefsListForReading);
                pinyinIndexPrepared = true;
            }

            pinyinSearchEnabled = true;
            RebuildCache();
            Messages.Message("手术搜索已启用拼音匹配。", MessageTypeDefOf.TaskCompletion, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "添加手术操作");
            Text.Font = GameFont.Small;

            string newSearch = Widgets.TextField(new Rect(0f, 38f, inRect.width, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                RebuildCache();
            }

            string pinyinButtonLabel = pinyinSearchEnabled ? "拼音搜索: 已启用" : "启用拼音搜索";
            if (Widgets.ButtonText(new Rect(0f, 74f, 180f, 28f), pinyinButtonLabel))
            {
                if (!pinyinSearchEnabled)
                {
                    TryEnablePinyinSearch();
                }
            }

            Rect outRect = new Rect(0f, 108f, inRect.width, inRect.height - 108f - 42f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(40f, cached.Count * 34f));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);

            float y = 0f;
            for (int i = 0; i < cached.Count; i++)
            {
                RecipeCandidate c = cached[i];
                Rect rowRect = new Rect(0f, y, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                Widgets.Label(new Rect(6f, y + 5f, viewRect.width - 12f, 22f), c.label);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    onSelected?.Invoke(OmniSurgeonOperation.CreateRecipe(c.recipe, c.part));
                    Close();
                }
                y += 34f;
            }
            Widgets.EndScrollView();
        }
    }

    public class Dialog_OmniAutoSurgeon_AddImplantOperation : Window
    {
        private readonly Pawn pawn;
        private readonly Action<OmniSurgeonOperation> onSelected;
        private readonly List<HediffDef> cached = new List<HediffDef>();
        private string searchText = string.Empty;
        private Vector2 scrollPos;
        private bool pinyinIndexPrepared;
        private bool pinyinSearchEnabled;

        public override Vector2 InitialSize => new Vector2(720f, 680f);

        public Dialog_OmniAutoSurgeon_AddImplantOperation(Pawn pawn, Action<OmniSurgeonOperation> onSelected)
        {
            this.pawn = pawn;
            this.onSelected = onSelected;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            RebuildCache();
        }

        private void RebuildCache()
        {
            cached.Clear();
            string lower = searchText.NullOrEmpty() ? string.Empty : searchText.ToLower();

            List<HediffDef> defs = DefDatabase<HediffDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                HediffDef def = defs[i];
                if (def == null || !(def.countsAsAddedPartOrImplant || def.addedPartProps != null)) continue;

                if (!lower.NullOrEmpty())
                {
                    bool matched = def.LabelCap.ToString().ToLower().Contains(lower) ||
                                   (def.defName ?? string.Empty).ToLower().Contains(lower) ||
                                   (def.label ?? string.Empty).ToLower().Contains(lower) ||
                                   (pinyinSearchEnabled && PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(def, lower));
                    if (!matched) continue;
                }

                cached.Add(def);
            }

            cached.Sort((a, b) => string.Compare(a.LabelCap.ToString(), b.LabelCap.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private void TryEnablePinyinSearch()
        {

            if (!pinyinIndexPrepared)
            {
                PinyinSearchEngine.EnsureIndexed(DefDatabase<HediffDef>.AllDefsListForReading);
                pinyinIndexPrepared = true;
            }

            pinyinSearchEnabled = true;
            RebuildCache();
            Messages.Message("植入搜索已启用拼音匹配。", MessageTypeDefOf.TaskCompletion, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "添加植入操作（先选植入物，再选部位）");
            Text.Font = GameFont.Small;

            string newSearch = Widgets.TextField(new Rect(0f, 38f, inRect.width, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                RebuildCache();
            }

            string pinyinButtonLabel = pinyinSearchEnabled ? "拼音搜索: 已启用" : "启用拼音搜索";
            if (Widgets.ButtonText(new Rect(0f, 74f, 180f, 28f), pinyinButtonLabel))
            {
                if (!pinyinSearchEnabled)
                {
                    TryEnablePinyinSearch();
                }
            }

            Rect outRect = new Rect(0f, 108f, inRect.width, inRect.height - 108f - 42f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(40f, cached.Count * 34f));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);

            float y = 0f;
            for (int i = 0; i < cached.Count; i++)
            {
                HediffDef def = cached[i];
                Rect rowRect = new Rect(0f, y, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                Widgets.Label(new Rect(6f, y + 5f, viewRect.width - 12f, 22f), def.LabelCap);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    OpenPartMenu(def);
                }
                y += 34f;
            }

            Widgets.EndScrollView();
        }

        private void OpenPartMenu(HediffDef hediff)
        {
            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts().ToList();
            if (parts.Count == 0)
            {
                Messages.Message("没有可用部位。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                string label = part.LabelCap;
                bool restricted = Building_FullyAutoOmniSurgeon.IsRestrictedFor(pawn, hediff, part);
                if (restricted)
                {
                    label = "<color=red>" + label + "（受种族限制）</color>";
                }

                BodyPartRecord localPart = part;
                options.Add(new FloatMenuOption(label, delegate
                {
                    onSelected?.Invoke(OmniSurgeonOperation.CreateInstall(hediff, localPart));
                    Close();
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
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