using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 全自动手术台的手术执行上下文。
    /// 使用 ThreadStatic 确保多线程安全，并通过 IDisposable 模式管理手术状态的生命周期。
    /// 进入上下文时会自动挂载高频补丁，退出时自动卸载。
    /// </summary>
    public static class OmniAutoSurgeonSurgeryContext
    {
        [ThreadStatic]
        private static int activeDepth;

        [ThreadStatic]
        public static Building_FullyAutoOmniSurgeon CurrentSurgeon;

        public static bool IsActive => activeDepth > 0;

        public static IDisposable Enter(Building_FullyAutoOmniSurgeon surgeon)
        {
            Patch_HighFrequency_Manual.PatchHighFrequencyMethods(OmniCrafterMod.HarmonyInstance);
            activeDepth++;
            CurrentSurgeon = surgeon;
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
                if (activeDepth == 0)
                {
                    CurrentSurgeon = null;
                }
                Patch_HighFrequency_Manual.UnpatchHighFrequencyMethods(OmniCrafterMod.HarmonyInstance);
            }
        }
    }

    public enum OmniSurgeonOperationType
    {
        Recipe,
        InstallImplant,
        RemoveImplant,
        RepairAndHeal,
        RemoveAllImplantsAndRepair,
        TendAllWounds,
        RemoveAnesthesia
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

        public static OmniSurgeonOperation CreateRepairAndHeal()
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.RepairAndHeal
            };
        }

        public static OmniSurgeonOperation CreateRemoveAllImplantsAndRepair()
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.RemoveAllImplantsAndRepair
            };
        }

        public static OmniSurgeonOperation CreateTendAllWounds()
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.TendAllWounds
            };
        }

        public static OmniSurgeonOperation CreateRemoveAnesthesia()
        {
            return new OmniSurgeonOperation
            {
                operationType = OmniSurgeonOperationType.RemoveAnesthesia
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

        public bool IsValid()
        {
            switch (operationType)
            {
                case OmniSurgeonOperationType.Recipe:
                    return !recipeDefName.NullOrEmpty() && DefDatabase<RecipeDef>.GetNamed(recipeDefName, false) != null;
                case OmniSurgeonOperationType.InstallImplant:
                case OmniSurgeonOperationType.RemoveImplant:
                    return !hediffDefName.NullOrEmpty() && DefDatabase<HediffDef>.GetNamed(hediffDefName, false) != null;
                case OmniSurgeonOperationType.RepairAndHeal:
                case OmniSurgeonOperationType.RemoveAllImplantsAndRepair:
                case OmniSurgeonOperationType.TendAllWounds:
                case OmniSurgeonOperationType.RemoveAnesthesia:
                    return true;
                default:
                    return false;
            }
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

        public static readonly Texture2D IconThought =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_Thought", true) ??
            IconModifyDialog;

        public static readonly Texture2D IconHediff =
            ContentFinder<Texture2D>.Get("UI/Commands/FullyAutoOmniSurgeon_Hediff", true) ??
            IconModifyDialog;

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
        public List<SurgeryTemplate> templates => OmniCrafterMod.Settings.globalSurgeryTemplates;
        public List<OmniSurgeonOperation> lastOperations = new List<OmniSurgeonOperation>();

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
            
            // 为了向前兼容，读取旧存档中的数据并合并到全局设置中
            List<SurgeryTemplate> localTemplates = null;

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Collections.Look(ref localTemplates, "templates", LookMode.Deep);
                Scribe_Collections.Look(ref lastOperations, "lastOperations", LookMode.Deep);
            }
            if (lastOperations == null) lastOperations = new List<OmniSurgeonOperation>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (localTemplates != null)
                {
                    foreach (var t in localTemplates)
                    {
                        if (!templates.Any(gt => gt.templateName == t.templateName))
                        {
                            templates.Add(t);
                        }
                    }
                }
            }
            
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 读档后检查并移除不存在的手术（可能由于Mod列表变更）
                lastOperations.RemoveAll(op => !op.IsValid());
                if (templates != null)
                {
                    foreach (var template in templates)
                    {
                        if (template.operations != null)
                        {
                            template.operations.RemoveAll(op => !op.IsValid());
                        }
                    }
                }
            }

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
                    defaultLabel = "FullyAutoOmniSurgeon_RepairAndHeal".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_RepairAndHealDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconRepair,
                    action = () => { RepairAndHeal(this.Occupant); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_FullRepair".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_FullRepairDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconRepair,
                    action = () => { FullRepair(this.Occupant); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_OpenPanel".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_OpenPanelDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconModifyDialog,
                    action = () => { Find.WindowStack.Add(new Window_OmniAutoSurgeonUI(this.Occupant, this)); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_OpenThoughtEditor".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_OpenThoughtEditorDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconThought,
                    action = () => { Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_ThoughtEditor(this.Occupant)); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "FullyAutoOmniSurgeon_OpenHediffEditor".Translate(),
                    defaultDesc = "FullyAutoOmniSurgeon_OpenHediffEditorDesc".Translate(),
                    icon = FullyAutoOmniSurgeonTex.IconHediff,
                    action = () => { Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_HediffEditor(this.Occupant)); }
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
                    // 在全自动手术中，我们放宽“干净”的限制
                    if (part.def.spawnThingOnRemoved != null)
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

        public void RepairAndHeal(Pawn pawn)
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

                // 2. 移除所有负面状态（保留义体）
                var toRemove = pawn.health.hediffSet.hediffs
                    .Where(h => h is Hediff_Injury || h is Hediff_Addiction || h.def.isBad)
                    .ToList();

                foreach (var h in toRemove)
                {
                    // 如果是义体相关的 bad hediff（比如排斥），我们决定保留它，因为用户要求保持植入物不变
                    // 但通常 isBad 指的是疾病、受伤等。
                    pawn.health.RemoveHediff(h);
                }

                Messages.Message("FullyAutoOmniSurgeon_RepairAndHealComplete".Translate(pawn.LabelShort),
                    MessageTypeDefOf.TaskCompletion);
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 进行修复和医疗时发生异常: {ex}");
            }
        }

        public void RemoveAllImplantsAndRepair(Pawn pawn)
        {
            if (pawn == null) return;
            try
            {
                // 1. 移除并掉落所有植入物/义体
                var implants = pawn.health.hediffSet.hediffs
                    .Where(h => h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null)
                    .ToList();

                foreach (var h in implants)
                {
                    RemoveBionic(pawn, h.Part, h);
                }

                // 2. 恢复所有肢体和损伤
                RepairAndHeal(pawn);
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 移除所有植入物并修复时发生异常: {ex}");
            }
        }

        public void TendAllWounds(Pawn pawn)
        {
            if (pawn == null) return;
            try
            {
                int count = 0;
                // 遍历所有 hediff，查找可以包扎的
                foreach (var hediff in pawn.health.hediffSet.hediffs)
                {
                    if (hediff.TendableNow())
                    {
                        // 1.0f 代表 100% 的包扎质量。有些伤口可能支持更高的上限，但 1.0f 是最高标准质量。
                        hediff.Tended(1.0f, 1.0f);
                        count++;
                    }
                }
                if (count > 0)
                {
                    Log.Message($"[OmniAutoSurgeon] 已为 {pawn.LabelShort} 包扎了 {count} 处伤口 (最高质量)");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 包扎伤口时发生异常: {ex}");
            }
        }

        public void RemoveAnesthesia(Pawn pawn)
        {
            if (pawn == null) return;
            try
            {
                var anesthesia = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Anesthetic);
                if (anesthesia != null)
                {
                    pawn.health.RemoveHediff(anesthesia);
                    Log.Message($"[OmniAutoSurgeon] 已为 {pawn.LabelShort} 移除麻醉状态");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[OmniAutoSurgeon] 为 {pawn.LabelShort} 移除麻醉状态时发生异常: {ex}");
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
            OmniCrafterMod.Instance.WriteSettings();
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
                failReason = "FullyAutoOmniSurgeon_InvalidOperation".Translate();
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
                            failReason = "FullyAutoOmniSurgeon_RecipeNotFound".Translate();
                            return false;
                        }

                        if (recipe.targetsBodyPart && part == null)
                        {
                            failReason = "FullyAutoOmniSurgeon_BodyPartMissing".Translate();
                            return false;
                        }

                        Pawn billDoer = recipe.Worker is Recipe_Surgery ? SelectOperationSurgeon(pawn) : null;
                        if (billDoer == null) billDoer = pawn;

                        List<Thing> ingredients = new List<Thing>();

                        HashSet<int> beforeThingIds = CaptureMapThingIds(this.Map);

                        using (OmniAutoSurgeonSurgeryContext.Enter(this))
                        {
                            recipe.Worker.ApplyOnPawn(pawn, part, billDoer, ingredients, null);
                        }

                        // 对于某些移除部位的操作，即使 ApplyOnPawn 没能成功生成物品（例如因为 IsCleanAndDroppable 限制或其他原因）
                        // 且该部位确实被移除了（变成了缺失部位），我们尝试手动补救生成。
                        // 注意：如果 Harmony 补丁生效了，这里通常不需要补救，但为了双重保险：
                        if (recipe.targetsBodyPart && part != null)
                        {
                            // 检查该部位是否现在确实缺失了
                            if (!pawn.health.hediffSet.GetNotMissingParts().Contains(part))
                            {
                                // 检查是否有新物品生成在地图上
                                HashSet<int> afterThingIds = CaptureMapThingIds(this.Map);
                                bool anyNewThing = afterThingIds != null && beforeThingIds != null && afterThingIds.Any(id => !beforeThingIds.Contains(id));
                                if (!anyNewThing)
                                {
                                    ThingDef spawnDef = part.def.spawnThingOnRemoved;
                                    // 有些 Recipe 可能会在 recipe 级定义产出，虽然 RemoveBodyPart 主要是 BodyPartDef
                                    
                                    if (spawnDef != null)
                                    {
                                        // 手动生成缺失的器官
                                        Thing thing = ThingMaker.MakeThing(spawnDef);
                                        ForceLegendaryQuality(thing);
                                        IntVec3 dropCell = this.def != null && this.def.hasInteractionCell ? this.InteractionCell : this.Position;
                                        GenPlace.TryPlaceThing(thing, dropCell, this.Map, ThingPlaceMode.Near);
                                    }
                                }
                            }
                        }

                        PromoteNewMapThingsToLegendary(this.Map, beforeThingIds);
                        return true;
                    }
                    case OmniSurgeonOperationType.InstallImplant:
                    {
                        HediffDef hediff = DefDatabase<HediffDef>.GetNamedSilentFail(operation.hediffDefName);
                        if (hediff == null || part == null)
                        {
                            failReason = "FullyAutoOmniSurgeon_ImplantOrPartMissing".Translate();
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
                            failReason = "FullyAutoOmniSurgeon_BodyPartMissing".Translate();
                            return false;
                        }

                        Hediff target = pawn.health.hediffSet.hediffs.FirstOrDefault(h =>
                            h.Part == part && (hediff == null || h.def == hediff));
                        if (target == null)
                        {
                            failReason = "FullyAutoOmniSurgeon_TargetHediffMissing".Translate();
                            return false;
                        }

                        RemoveBionic(pawn, part, target);
                        return true;
                    }
                    case OmniSurgeonOperationType.RepairAndHeal:
                    {
                        RepairAndHeal(pawn);
                        return true;
                    }
                    case OmniSurgeonOperationType.RemoveAllImplantsAndRepair:
                    {
                        RemoveAllImplantsAndRepair(pawn);
                        return true;
                    }
                    case OmniSurgeonOperationType.TendAllWounds:
                    {
                        TendAllWounds(pawn);
                        return true;
                    }
                    case OmniSurgeonOperationType.RemoveAnesthesia:
                    {
                        RemoveAnesthesia(pawn);
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
            OmniCrafterMod.Instance.WriteSettings();
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

            if (!surgeon.lastOperations.NullOrEmpty())
            {
                foreach (var op in surgeon.lastOperations)
                {
                    workingOperations.Add(op.Clone());
                }
            }
        }

        private void SyncOperations()
        {
            surgeon.lastOperations.Clear();
            foreach (var op in workingOperations)
            {
                surgeon.lastOperations.Add(op.Clone());
            }
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
                        SyncOperations();
                    }, extraPartWidth: 30f, extraPartOnGUI: delegate(Rect rect)
                    {
                        if (Widgets.ButtonImage(new Rect(rect.xMax - 25f, rect.y + (rect.height - 20f) / 2f, 20f, 20f), Verse.TexButton.Delete))
                        {
                            surgeon.templates.Remove(localTemplate);
                            OmniCrafterMod.Instance.WriteSettings();
                            return true;
                        }
                        return false;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (Widgets.ButtonText(new Rect(x - 292f, toolbarY, 136f, 28f), "FullyAutoOmniSurgeon_SpecialOps".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("FullyAutoOmniSurgeon_RepairAndHeal_Simple".Translate(), delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateRepairAndHeal());
                    SyncOperations();
                }));
                options.Add(new FloatMenuOption("FullyAutoOmniSurgeon_RemoveAllImplants".Translate(), delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateRemoveAllImplantsAndRepair());
                    SyncOperations();
                }));
                options.Add(new FloatMenuOption("FullyAutoOmniSurgeon_TendAllWounds".Translate(), delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateTendAllWounds());
                    SyncOperations();
                }));
                options.Add(new FloatMenuOption("FullyAutoOmniSurgeon_RemoveAnesthesia".Translate(), delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateRemoveAnesthesia());
                    SyncOperations();
                }));
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
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "FullyAutoOmniSurgeon_PartStatusTitle".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(64f, parts.Count * 34f));

            Widgets.BeginScrollView(outRect, ref leftScrollPos, viewRect);
            
            float rowHeight = 34f;
            int firstIndex = Mathf.Max(0, Mathf.FloorToInt(leftScrollPos.y / rowHeight));
            int lastIndex = Mathf.Min(parts.Count, Mathf.CeilToInt((leftScrollPos.y + outRect.height) / rowHeight));

            for (int i = firstIndex; i < lastIndex; i++)
            {
                BodyPartRecord part = parts[i];
                float curY = i * rowHeight;
                Rect rowRect = new Rect(0f, curY, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                int depth = GetPartDepth(part);
                float indent = depth * 12f;
                Widgets.Label(new Rect(4f + indent, curY + 5f, 210f - indent, 24f), part.LabelCap);

                string status = GetPartStatus(part);
                Widgets.Label(new Rect(214f, curY + 5f, 260f, 24f), status);

                if (Widgets.ButtonText(new Rect(viewRect.width - 160f, curY + 2f, 74f, 26f), "FullyAutoOmniSurgeon_AddImplantShort".Translate()))
                {
                    OpenInstallOperationMenuForPart(part);
                }

                bool canRemove = pawn.health.hediffSet.hediffs.Any(h => h.Part == part && (h.def.countsAsAddedPartOrImplant || h.def.addedPartProps != null));
                if (canRemove && Widgets.ButtonText(new Rect(viewRect.width - 82f, curY + 2f, 78f, 26f), "FullyAutoOmniSurgeon_RemoveImplantShort".Translate()))
                {
                    OpenRemoveOperationMenuForPart(part);
                }
            }
            Widgets.EndScrollView();
        }

        private void DrawRightColumn(Rect rect)
        {
            float y = rect.y;
            float topButtonWidth = (rect.width - 8f) * 0.5f;

            if (Widgets.ButtonText(new Rect(rect.x, y, topButtonWidth, 30f), "FullyAutoOmniSurgeon_SearchAddSurgery".Translate()))
            {
                Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_AddRecipeOperation(pawn, delegate(OmniSurgeonOperation op)
                {
                    if (op != null)
                    {
                        workingOperations.Add(op);
                        SyncOperations();
                    }
                }));
            }

            if (Widgets.ButtonText(new Rect(rect.x + topButtonWidth + 8f, y, topButtonWidth, 30f), "FullyAutoOmniSurgeon_SearchAddImplant".Translate()))
            {
                Find.WindowStack.Add(new Dialog_OmniAutoSurgeon_AddImplantOperation(pawn, delegate(OmniSurgeonOperation op)
                {
                    if (op != null)
                    {
                        workingOperations.Add(op);
                        SyncOperations();
                    }
                }));
            }

            y += 36f;

            float bottomAreaHeight = 42f;
            Rect listOutRect = new Rect(rect.x, y, rect.width, rect.height - y - bottomAreaHeight);
            Rect listViewRect = new Rect(0f, 0f, listOutRect.width - 16f, Mathf.Max(60f, workingOperations.Count * 34f));
            Widgets.BeginScrollView(listOutRect, ref rightScrollPos, listViewRect);

            float rowHeight = 34f;
            int firstIndex = Mathf.Max(0, Mathf.FloorToInt(rightScrollPos.y / rowHeight));
            int lastIndex = Mathf.Min(workingOperations.Count, Mathf.CeilToInt((rightScrollPos.y + listOutRect.height) / rowHeight));

            for (int i = firstIndex; i < lastIndex; i++)
            {
                float curY = i * rowHeight;
                Rect rowRect = new Rect(0f, curY, listViewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Widgets.Label(new Rect(4f, curY + 5f, 28f, 24f), (i + 1).ToString());
                Widgets.Label(new Rect(34f, curY + 5f, listViewRect.width - 130f, 24f), GetOperationLabel(workingOperations[i]));

                if (Widgets.ButtonText(new Rect(listViewRect.width - 92f, curY + 2f, 28f, 26f), "↑") && i > 0)
                {
                    OmniSurgeonOperation tmp = workingOperations[i - 1];
                    workingOperations[i - 1] = workingOperations[i];
                    workingOperations[i] = tmp;
                    SyncOperations();
                }

                if (Widgets.ButtonText(new Rect(listViewRect.width - 62f, curY + 2f, 28f, 26f), "↓") && i < workingOperations.Count - 1)
                {
                    OmniSurgeonOperation tmp = workingOperations[i + 1];
                    workingOperations[i + 1] = workingOperations[i];
                    workingOperations[i] = tmp;
                    SyncOperations();
                }

                if (Widgets.ButtonText(new Rect(listViewRect.width - 32f, curY + 2f, 28f, 26f), "X"))
                {
                    workingOperations.RemoveAt(i);
                    i--;
                    SyncOperations();
                    // Since we removed an element, we should break and re-render next frame or adjust indices.
                    // But in RimWorld UI, it's safer to break if the list changes during iteration.
                    break;
                }
            }
            Widgets.EndScrollView();

            Rect bottomRect = new Rect(rect.x, rect.yMax - 36f, rect.width, 32f);
            float executeWidth = rect.width * 0.62f;
            if (Widgets.ButtonText(new Rect(bottomRect.x, bottomRect.y, executeWidth, bottomRect.height), "FullyAutoOmniSurgeon_ExecuteTemplate".Translate()))
            {
                ExecuteWorkingOperations();
            }

            if (Widgets.ButtonText(new Rect(bottomRect.x + executeWidth + 8f, bottomRect.y, rect.width - executeWidth - 8f, bottomRect.height), "FullyAutoOmniSurgeon_ClearList".Translate()))
            {
                workingOperations.Clear();
                SyncOperations();
            }
        }

        private void ExecuteWorkingOperations()
        {
            if (workingOperations.Count == 0)
            {
                Messages.Message("FullyAutoOmniSurgeon_EmptyListMessage".Translate(), MessageTypeDefOf.RejectInput, false);
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
                Messages.Message("FullyAutoOmniSurgeon_OperationsExecuted".Translate(success), MessageTypeDefOf.TaskCompletion, false);
            }
            else
            {
                string errorPart = lastError.NullOrEmpty() ? string.Empty : "FullyAutoOmniSurgeon_LastError".Translate().ToString() + lastError;
                Messages.Message("FullyAutoOmniSurgeon_ExecutionResult".Translate(success, failed, errorPart), MessageTypeDefOf.CautionInput, false);
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
                    label = "<color=red>" + label + "FullyAutoOmniSurgeon_RaceRestricted_Simple".Translate() + "</color>";
                }

                HediffDef localDef = def;
                options.Add(new FloatMenuOption(label, delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateInstall(localDef, part));
                    SyncOperations();
                }));
            }

            if (options.Count == 0)
            {
                Messages.Message("FullyAutoOmniSurgeon_NoImplantsToAdd".Translate(), MessageTypeDefOf.RejectInput, false);
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
                HediffDef hediff = removable[i].def;
                options.Add(new FloatMenuOption("FullyAutoOmniSurgeon_RemoveLabel".Translate(removable[i].LabelCap), delegate
                {
                    workingOperations.Add(OmniSurgeonOperation.CreateRemove(hediff, part));
                    SyncOperations();
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private string GetPartStatus(BodyPartRecord part)
        {
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs.Where(h => h.Part == part && h.Visible).ToList();
            if (hediffs.Count == 0) return "FullyAutoOmniSurgeon_StatusNormal".Translate();
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
            string partName = part != null ? part.LabelCap : (operation.partLabel ?? operation.partDefName ?? "FullyAutoOmniSurgeon_NotSpecifiedPart".Translate().ToString());

            if (operation.operationType == OmniSurgeonOperationType.Recipe)
            {
                RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(operation.recipeDefName);
                if (recipe == null) return "FullyAutoOmniSurgeon_MissingSurgery".Translate(operation.recipeDefName);
                string label = recipe.Worker != null ? recipe.Worker.GetLabelWhenUsedOn(pawn, part).ToString() : recipe.LabelCap.ToString();
                if (recipe.targetsBodyPart) label = "FullyAutoOmniSurgeon_LabelWithPart".Translate(label, partName);
                return label;
            }

            if (operation.operationType == OmniSurgeonOperationType.RepairAndHeal)
            {
                return "FullyAutoOmniSurgeon_RepairAndHeal_Simple".Translate();
            }
            if (operation.operationType == OmniSurgeonOperationType.RemoveAllImplantsAndRepair)
            {
                return "FullyAutoOmniSurgeon_RemoveAllImplants".Translate();
            }
            if (operation.operationType == OmniSurgeonOperationType.TendAllWounds)
            {
                return "FullyAutoOmniSurgeon_TendAllWounds".Translate();
            }
            if (operation.operationType == OmniSurgeonOperationType.RemoveAnesthesia)
            {
                return "FullyAutoOmniSurgeon_RemoveAnesthesia".Translate();
            }

            HediffDef h = !operation.hediffDefName.NullOrEmpty() ? DefDatabase<HediffDef>.GetNamedSilentFail(operation.hediffDefName) : null;
            string hLabel = h != null ? h.LabelCap.ToString() : (operation.hediffDefName ?? "FullyAutoOmniSurgeon_Unknown".Translate().ToString());
            if (operation.operationType == OmniSurgeonOperationType.InstallImplant)
            {
                return "FullyAutoOmniSurgeon_InstallArrow".Translate(hLabel, partName);
            }
            if (operation.operationType == OmniSurgeonOperationType.RemoveImplant)
            {
                return "FullyAutoOmniSurgeon_RemoveArrow".Translate(hLabel, partName);
            }

            return "FullyAutoOmniSurgeon_UnknownOperation".Translate();
        }
    }

    public class Dialog_OmniAutoSurgeon_AddRecipeOperation : Window
    {
        private readonly Pawn pawn;
        private readonly Action<OmniSurgeonOperation> onSelected;
        private readonly List<RecipeCandidate> cached = new List<RecipeCandidate>();
        private readonly HashSet<RecipeCandidate> selectedCandidates = new HashSet<RecipeCandidate>();
        private string searchText = string.Empty;
        private Vector2 scrollPos;
        private static bool pinyinSearchEnabled;

        private struct RecipeCandidate
        {
            public RecipeDef recipe;
            public BodyPartRecord part;
            public string label;

            public override bool Equals(object obj)
            {
                if (!(obj is RecipeCandidate other)) return false;
                return recipe == other.recipe && part == other.part && label == other.label;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (recipe != null ? recipe.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (part != null ? part.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (label != null ? label.GetHashCode() : 0);
                    return hashCode;
                }
            }
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
                            opLabel = !recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "FullyAutoOmniSurgeon_UnknownSurgery".Translate());
                        }

                        string label = "FullyAutoOmniSurgeon_LabelWithPart".Translate(opLabel, part.LabelCap);
                        if (!MatchesSearch(recipe, label, lower)) continue;
                        cached.Add(new RecipeCandidate { recipe = recipe, part = part, label = label });
                    }

                    if (!anyPart)
                    {
                        string fallbackLabel = (!recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "FullyAutoOmniSurgeon_UnknownSurgery".Translate().ToString())) + "FullyAutoOmniSurgeon_NotMatchedPart".Translate();
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
                        label = !recipe.label.NullOrEmpty() ? recipe.label.CapitalizeFirst() : (recipe.defName ?? "FullyAutoOmniSurgeon_UnknownSurgery".Translate().ToString());
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
            if (pinyinSearchEnabled && PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(recipe, lower, PinyinSource.SurgeryRecipe)) return true;
            return false;
        }

        private void TryEnablePinyinSearch()
        {
            PinyinSearchEngine.EnsureIndexed(DefDatabase<RecipeDef>.AllDefsListForReading, PinyinSource.SurgeryRecipe);
            pinyinSearchEnabled = true;
            RebuildCache();
            Messages.Message("FullyAutoOmniSurgeon_PinyinEnabledMessage".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FullyAutoOmniSurgeon_AddSurgeryTitle".Translate());
            Text.Font = GameFont.Small;

            string newSearch = Widgets.TextField(new Rect(0f, 38f, inRect.width, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                RebuildCache();
            }

            float yOffset = 74f;
            string pinyinButtonLabel = pinyinSearchEnabled ? "FullyAutoOmniSurgeon_PinyinSearchEnabled".Translate().ToString() : "FullyAutoOmniSurgeon_EnablePinyinSearch".Translate().ToString();
            if (Widgets.ButtonText(new Rect(0f, yOffset, 180f, 28f), pinyinButtonLabel))
            {
                if (!pinyinSearchEnabled)
                {
                    TryEnablePinyinSearch();
                }
            }

            if (Widgets.ButtonText(new Rect(190f, yOffset, 120f, 28f), "FullyAutoOmniSurgeon_SelectAll".Translate()))
            {
                foreach (var c in cached)
                {
                    selectedCandidates.Add(c);
                }
            }
            if (Widgets.ButtonText(new Rect(320f, yOffset, 120f, 28f), "FullyAutoOmniSurgeon_DeselectAll".Translate()))
            {
                selectedCandidates.Clear();
            }

            if (selectedCandidates.Count > 0)
            {
                string confirmLabel = "FullyAutoOmniSurgeon_ConfirmBatchAdd".Translate(selectedCandidates.Count);
                if (Widgets.ButtonText(new Rect(inRect.width - 200f, yOffset, 200f, 28f), confirmLabel))
                {
                    foreach (var c in selectedCandidates)
                    {
                        onSelected?.Invoke(OmniSurgeonOperation.CreateRecipe(c.recipe, c.part));
                    }
                    Close();
                }
            }

            Rect outRect = new Rect(0f, 108f, inRect.width, inRect.height - 108f - 42f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(40f, cached.Count * 34f));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);

            float rowHeight = 34f;
            int firstIndex = Mathf.Max(0, Mathf.FloorToInt(scrollPos.y / rowHeight));
            int lastIndex = Mathf.Min(cached.Count, Mathf.CeilToInt((scrollPos.y + outRect.height) / rowHeight));

            for (int i = firstIndex; i < lastIndex; i++)
            {
                RecipeCandidate c = cached[i];
                float y = i * rowHeight;
                Rect rowRect = new Rect(0f, y, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                bool selected = selectedCandidates.Contains(c);
                bool newSelected = selected;
                Widgets.Checkbox(new Vector2(6f, y + 4f), ref newSelected);
                if (newSelected != selected)
                {
                    if (newSelected) selectedCandidates.Add(c);
                    else selectedCandidates.Remove(c);
                }

                Widgets.Label(new Rect(36f, y + 5f, viewRect.width - 42f, 22f), c.label);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    if (selectedCandidates.Contains(c))
                    {
                        selectedCandidates.Remove(c);
                    }
                    else
                    {
                        selectedCandidates.Add(c);
                    }
                }
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
        private static bool pinyinSearchEnabled;

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
                                   (pinyinSearchEnabled && PinyinSearchEngine.IsReady && PinyinSearchEngine.MatchesPinyin(def, lower, PinyinSource.SurgeryImplant));
                    if (!matched) continue;
                }

                cached.Add(def);
            }

            cached.Sort((a, b) => string.Compare(a.LabelCap.ToString(), b.LabelCap.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private void TryEnablePinyinSearch()
        {
            PinyinSearchEngine.EnsureIndexed(DefDatabase<HediffDef>.AllDefsListForReading, PinyinSource.SurgeryImplant);
            pinyinSearchEnabled = true;
            RebuildCache();
            Messages.Message("FullyAutoOmniSurgeon_PinyinEnabledMessageImplant".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FullyAutoOmniSurgeon_AddImplantTitle".Translate());
            Text.Font = GameFont.Small;

            string newSearch = Widgets.TextField(new Rect(0f, 38f, inRect.width, 30f), searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                RebuildCache();
            }

            string pinyinButtonLabel = pinyinSearchEnabled ? "FullyAutoOmniSurgeon_PinyinSearchEnabled".Translate().ToString() : "FullyAutoOmniSurgeon_EnablePinyinSearch".Translate().ToString();
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

            float rowHeight = 34f;
            int firstIndex = Mathf.Max(0, Mathf.FloorToInt(scrollPos.y / rowHeight));
            int lastIndex = Mathf.Min(cached.Count, Mathf.CeilToInt((scrollPos.y + outRect.height) / rowHeight));

            for (int i = firstIndex; i < lastIndex; i++)
            {
                HediffDef def = cached[i];
                float y = i * rowHeight;
                Rect rowRect = new Rect(0f, y, viewRect.width, 30f);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                Widgets.Label(new Rect(6f, y + 5f, viewRect.width - 12f, 22f), def.LabelCap);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    OpenPartMenu(def);
                }
            }

            Widgets.EndScrollView();
        }

        private void OpenPartMenu(HediffDef hediff)
        {
            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts().ToList();
            if (parts.Count == 0)
            {
                Messages.Message("FullyAutoOmniSurgeon_NoAvailableParts".Translate(), MessageTypeDefOf.RejectInput, false);
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
                    label = "<color=red>" + label + "FullyAutoOmniSurgeon_RaceRestricted_Simple".Translate() + "</color>";
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

    public class Dialog_OmniAutoSurgeon_ThoughtEditor : Window
    {
        private readonly Pawn pawn;
        private readonly List<Thought> thoughtGroups = new List<Thought>();
        private readonly List<Thought> thoughtGroup = new List<Thought>();
        private readonly List<Thought> selectedGroupThoughts = new List<Thought>();
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private Thought selectedGroup;

        private static readonly Color MoodColor = new Color(0.1f, 1f, 0.1f);
        private static readonly Color MoodColorNegative = new Color(0.8f, 0.4f, 0.4f);
        private static readonly Color NoEffectColor = new Color(0.5f, 0.5f, 0.5f, 0.75f);

        public override Vector2 InitialSize => new Vector2(900f, 620f);

        public Dialog_OmniAutoSurgeon_ThoughtEditor(Pawn pawn)
        {
            this.pawn = pawn;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (pawn == null || pawn.Destroyed || pawn.needs == null || pawn.needs.mood == null)
            {
                Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FullyAutoOmniSurgeon_ThoughtNoMood".Translate());
                return;
            }

            RefreshThoughtGroups();

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FullyAutoOmniSurgeon_ThoughtEditorTitle".Translate(pawn.LabelShortCap));
            Text.Font = GameFont.Small;

            const float bottomReservedForCloseButton = 42f;
            Rect contentRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f - bottomReservedForCloseButton);
            const float gap = 10f;
            float leftWidth = Mathf.Floor(contentRect.width * 0.46f);
            Rect leftRect = new Rect(contentRect.x, contentRect.y, leftWidth - gap * 0.5f, contentRect.height);
            Rect rightRect = new Rect(leftRect.xMax + gap, contentRect.y, contentRect.width - leftRect.width - gap, contentRect.height);

            Widgets.DrawMenuSection(leftRect);
            Widgets.DrawMenuSection(rightRect);

            DrawThoughtList(leftRect.ContractedBy(8f));
            DrawThoughtDetails(rightRect.ContractedBy(8f));
        }

        private void RefreshThoughtGroups()
        {
            thoughtGroups.Clear();
            PawnNeedsUIUtility.GetThoughtGroupsInDisplayOrder(pawn.needs.mood, thoughtGroups);

            if (selectedGroup == null)
            {
                return;
            }

            Thought current = null;
            for (int i = 0; i < thoughtGroups.Count; i++)
            {
                if (thoughtGroups[i].GroupsWith(selectedGroup))
                {
                    current = thoughtGroups[i];
                    break;
                }
            }

            selectedGroup = current;
        }

        private void DrawThoughtList(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "FullyAutoOmniSurgeon_ThoughtListTitle".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            const float rowHeight = 24f;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(60f, thoughtGroups.Count * rowHeight));

            Widgets.BeginScrollView(outRect, ref leftScrollPos, viewRect);

            int firstIndex = Mathf.Max(0, Mathf.FloorToInt(leftScrollPos.y / rowHeight));
            int lastIndex = Mathf.Min(thoughtGroups.Count, Mathf.CeilToInt((leftScrollPos.y + outRect.height) / rowHeight));

            for (int i = firstIndex; i < lastIndex; i++)
            {
                Thought group = thoughtGroups[i];
                Rect rowRect = new Rect(0f, i * rowHeight, viewRect.width, 20f);
                if (selectedGroup != null && group.GroupsWith(selectedGroup))
                {
                    Widgets.DrawHighlightSelected(rowRect);
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                if (DrawThoughtGroupRow(rowRect, group))
                {
                    selectedGroup = group;
                    rightScrollPos = Vector2.zero;
                }
            }

            Widgets.EndScrollView();
        }

        private bool DrawThoughtGroupRow(Rect rect, Thought group)
        {
            pawn.needs.mood.thoughts.GetMoodThoughts(group, thoughtGroup);
            if (thoughtGroup.Count == 0)
            {
                return false;
            }

            Thought leadingThought = PawnNeedsUIUtility.GetLeadingThoughtInGroup(thoughtGroup);
            if (leadingThought == null || !leadingThought.VisibleInNeedsTab)
            {
                thoughtGroup.Clear();
                return false;
            }

            Verse.Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = leadingThought.LabelCap;
            if (thoughtGroup.Count > 1)
            {
                label = label + " x" + thoughtGroup.Count;
            }

            Rect labelRect = new Rect(rect.x + 8f, rect.y, rect.width - 58f, rect.height);
            Widgets.Label(labelRect, label.Truncate(labelRect.width));

            float moodOffset = pawn.needs.mood.thoughts.MoodOffsetOfGroup(group);
            GUI.color = GetMoodColor(moodOffset);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rect.xMax - 44f, rect.y, 38f, rect.height), moodOffset.ToString("##0"));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Verse.Text.WordWrap = true;

            if (Mouse.IsOver(rect))
            {
                TooltipHandler.TipRegion(rect, new TipSignal(BuildThoughtTooltip(leadingThought, group), group.GetHashCode()));
            }

            bool clicked = Widgets.ButtonInvisible(rect);
            thoughtGroup.Clear();
            return clicked;
        }

        private void DrawThoughtDetails(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "FullyAutoOmniSurgeon_ThoughtDetailsTitle".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, 900f);

            Widgets.BeginScrollView(outRect, ref rightScrollPos, viewRect);

            if (selectedGroup == null)
            {
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 30f), "FullyAutoOmniSurgeon_ThoughtSelectPrompt".Translate());
                Widgets.EndScrollView();
                return;
            }

            FillSelectedGroupThoughts();
            Thought leadingThought = PawnNeedsUIUtility.GetLeadingThoughtInGroup(selectedGroupThoughts);
            if (leadingThought == null)
            {
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 30f), "FullyAutoOmniSurgeon_ThoughtSelectPrompt".Translate());
                Widgets.EndScrollView();
                return;
            }

            float y = 0f;
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtName".Translate(), leadingThought.LabelCap);
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtDefName".Translate(), leadingThought.def.defName);
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtType".Translate(), GetThoughtTypeLabel(leadingThought));
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtSource".Translate(), GetThoughtSourceLabel(leadingThought.def));
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtMoodOffset".Translate(), pawn.needs.mood.thoughts.MoodOffsetOfGroup(selectedGroup).ToString("##0"));

            if (leadingThought.sourcePrecept != null)
            {
                string precept = leadingThought.sourcePrecept.def != null ? leadingThought.sourcePrecept.def.LabelCap.ToString() : leadingThought.sourcePrecept.ToStringSafe();
                DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtPrecept".Translate(), precept);
            }

            if (leadingThought is Thought_Memory memory)
            {
                DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_ThoughtDuration".Translate(), GetMemoryDurationLabel(memory));
            }

            y += 8f;
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), "FullyAutoOmniSurgeon_ThoughtDescription".Translate());
            y += 24f;
            string description = leadingThought.Description;
            float descHeight = Text.CalcHeight(description, viewRect.width);
            Widgets.Label(new Rect(0f, y, viewRect.width, descHeight), description);
            y += descHeight + 12f;

            if (selectedGroupThoughts.Count > 1)
            {
                Widgets.Label(new Rect(0f, y, viewRect.width, 24f), "FullyAutoOmniSurgeon_ThoughtGroupItems".Translate(selectedGroupThoughts.Count));
                y += 24f;
                for (int i = 0; i < selectedGroupThoughts.Count; i++)
                {
                    Thought item = selectedGroupThoughts[i];
                    Widgets.Label(new Rect(8f, y, viewRect.width - 8f, 22f), "- " + item.LabelCap + " (" + GetThoughtTypeLabel(item) + ")");
                    y += 22f;
                }
                y += 8f;
            }

            DrawDeleteControls(new Rect(0f, y, viewRect.width, 84f));

            Widgets.EndScrollView();
        }

        private void FillSelectedGroupThoughts()
        {
            selectedGroupThoughts.Clear();
            if (selectedGroup != null)
            {
                pawn.needs.mood.thoughts.GetMoodThoughts(selectedGroup, selectedGroupThoughts);
            }
        }

        private void DrawDeleteControls(Rect rect)
        {
            int memoryCount = 0;
            for (int i = 0; i < selectedGroupThoughts.Count; i++)
            {
                if (selectedGroupThoughts[i] is Thought_Memory)
                {
                    memoryCount++;
                }
            }

            if (memoryCount == 0)
            {
                Widgets.Label(rect, "FullyAutoOmniSurgeon_ThoughtSituationalCannotDelete".Translate());
                GUI.enabled = false;
                Widgets.ButtonText(new Rect(rect.x, rect.y + 46f, 180f, 30f), "FullyAutoOmniSurgeon_ThoughtDelete".Translate());
                GUI.enabled = true;
                return;
            }

            if (Widgets.ButtonText(new Rect(rect.x, rect.y, 180f, 30f), "FullyAutoOmniSurgeon_ThoughtDelete".Translate()))
            {
                int count = memoryCount;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "FullyAutoOmniSurgeon_ThoughtDeleteConfirm".Translate(count),
                    delegate { DeleteSelectedMemories(); },
                    destructive: true));
            }
        }

        private void DeleteSelectedMemories()
        {
            if (selectedGroup == null || pawn.needs?.mood?.thoughts?.memories == null)
            {
                return;
            }

            selectedGroupThoughts.Clear();
            pawn.needs.mood.thoughts.GetMoodThoughts(selectedGroup, selectedGroupThoughts);

            int removed = 0;
            List<Thought_Memory> memories = pawn.needs.mood.thoughts.memories.Memories;
            for (int i = memories.Count - 1; i >= 0; i--)
            {
                Thought_Memory memory = memories[i];
                if (memory != null && memory.GroupsWith(selectedGroup))
                {
                    pawn.needs.mood.thoughts.memories.RemoveMemory(memory);
                    removed++;
                }
            }

            selectedGroup = null;
            selectedGroupThoughts.Clear();
            Messages.Message("FullyAutoOmniSurgeon_ThoughtDeleted".Translate(removed), MessageTypeDefOf.TaskCompletion, false);
        }

        private void DrawInfoLine(Rect viewRect, ref float y, string label, string value)
        {
            Rect labelRect = new Rect(0f, y, 120f, 22f);
            Rect valueRect = new Rect(124f, y, viewRect.width - 124f, 22f);
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;
            Widgets.Label(valueRect, value);
            y += 24f;
        }

        private static Color GetMoodColor(float moodOffset)
        {
            if (moodOffset > 0f) return MoodColor;
            if (moodOffset < 0f) return MoodColorNegative;
            return NoEffectColor;
        }

        private string BuildThoughtTooltip(Thought leadingThought, Thought group)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(leadingThought.LabelCap.AsTipTitle()).AppendLine().AppendLine();
            sb.Append(leadingThought.Description);

            int durationTicks = group.DurationTicks;
            if (durationTicks > 5 && leadingThought is Thought_Memory memory)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("ThoughtExpiresIn".Translate((durationTicks - memory.age).ToStringTicksToPeriod()));
            }

            return sb.ToString();
        }

        private static string GetThoughtTypeLabel(Thought thought)
        {
            if (thought is Thought_Memory)
            {
                return "FullyAutoOmniSurgeon_ThoughtTypeMemory".Translate();
            }
            if (thought.def != null && thought.def.IsSituational)
            {
                return "FullyAutoOmniSurgeon_ThoughtTypeSituational".Translate();
            }
            return "FullyAutoOmniSurgeon_ThoughtTypeOther".Translate();
        }

        private static string GetThoughtSourceLabel(ThoughtDef def)
        {
            ModContentPack mod = def?.modContentPack;
            if (mod == null)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceUnknown".Translate();
            }

            if (mod.IsCoreMod)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceCore".Translate(mod.Name, mod.PackageIdPlayerFacing);
            }

            if (mod.IsOfficialMod)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceDlc".Translate(mod.Name, mod.PackageIdPlayerFacing);
            }

            return "FullyAutoOmniSurgeon_ThoughtSourceMod".Translate(mod.Name, mod.PackageIdPlayerFacing);
        }

        private static string GetMemoryDurationLabel(Thought_Memory memory)
        {
            if (memory.permanent)
            {
                return "FullyAutoOmniSurgeon_ThoughtDurationPermanent".Translate();
            }

            int remaining = Mathf.Max(0, memory.DurationTicks - memory.age);
            return "FullyAutoOmniSurgeon_ThoughtDurationRemaining".Translate(remaining.ToStringTicksToPeriod());
        }
    }

    public class Dialog_OmniAutoSurgeon_HediffEditor : Window
    {
        private readonly Pawn pawn;
        private readonly List<Hediff> hediffs = new List<Hediff>();
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private Hediff selectedHediff;

        public override Vector2 InitialSize => new Vector2(900f, 620f);

        public Dialog_OmniAutoSurgeon_HediffEditor(Pawn pawn)
        {
            this.pawn = pawn;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (pawn == null || pawn.Destroyed)
            {
                Close();
                return;
            }

            RefreshHediffs();

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FullyAutoOmniSurgeon_HediffEditorTitle".Translate(pawn.LabelShortCap));
            Text.Font = GameFont.Small;

            const float bottomReservedForCloseButton = 42f;
            Rect contentRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f - bottomReservedForCloseButton);
            const float gap = 10f;
            float leftWidth = Mathf.Floor(contentRect.width * 0.46f);
            Rect leftRect = new Rect(contentRect.x, contentRect.y, leftWidth - gap * 0.5f, contentRect.height);
            Rect rightRect = new Rect(leftRect.xMax + gap, contentRect.y, contentRect.width - leftRect.width - gap, contentRect.height);

            Widgets.DrawMenuSection(leftRect);
            Widgets.DrawMenuSection(rightRect);

            DrawHediffList(leftRect.ContractedBy(8f));
            DrawHediffDetails(rightRect.ContractedBy(8f));
        }

        private void RefreshHediffs()
        {
            hediffs.Clear();
            if (pawn.health?.hediffSet?.hediffs != null)
            {
                hediffs.AddRange(pawn.health.hediffSet.hediffs);
            }

            if (selectedHediff != null && !hediffs.Contains(selectedHediff))
            {
                selectedHediff = null;
            }
        }

        private void DrawHediffList(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "FullyAutoOmniSurgeon_HediffListTitle".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);
            const float rowHeight = 28f;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(60f, hediffs.Count * rowHeight));

            Widgets.BeginScrollView(outRect, ref leftScrollPos, viewRect);

            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                Rect rowRect = new Rect(0f, i * rowHeight, viewRect.width, rowHeight - 2f);

                if (selectedHediff == hediff)
                {
                    Widgets.DrawHighlightSelected(rowRect);
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                string label = hediff.LabelCap;
                if (hediff.Part != null)
                {
                    label += " (" + hediff.Part.LabelCap + ")";
                }

                Rect labelRect = rowRect;
                labelRect.xMin += 4f;
                GUI.color = hediff.LabelColor;
                Widgets.Label(labelRect, label.Truncate(labelRect.width));
                GUI.color = Color.white;

                if (Widgets.ButtonInvisible(rowRect))
                {
                    selectedHediff = hediff;
                    rightScrollPos = Vector2.zero;
                }
            }

            Widgets.EndScrollView();
        }

        private void DrawHediffDetails(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), "FullyAutoOmniSurgeon_HediffDetailsTitle".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect outRect = new Rect(rect.x, rect.y + 30f, rect.width, rect.height - 30f);

            if (selectedHediff == null)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = ColoredText.SubtleGrayColor;
                Widgets.Label(outRect, "FullyAutoOmniSurgeon_HediffSelectPrompt".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, 1000f);
            Widgets.BeginScrollView(outRect, ref rightScrollPos, viewRect);

            float y = 0f;
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_HediffName".Translate(), selectedHediff.LabelCap);
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_HediffDefName".Translate(), selectedHediff.def.defName);
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_HediffPart".Translate(), selectedHediff.Part?.LabelCap ?? "FullyAutoOmniSurgeon_NotSpecifiedPart".Translate());
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_HediffSeverity".Translate(), selectedHediff.SeverityLabel ?? "FullyAutoOmniSurgeon_StatusNormal".Translate());
            DrawInfoLine(viewRect, ref y, "FullyAutoOmniSurgeon_HediffSource".Translate(), GetHediffSourceLabel(selectedHediff.def));

            y += 10f;
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), "FullyAutoOmniSurgeon_HediffDescription".Translate() + ":");
            y += 24f;

            string desc = selectedHediff.Description;
            if (desc.NullOrEmpty()) desc = selectedHediff.def.description;
            if (desc.NullOrEmpty()) desc = "No description.";

            float descHeight = Text.CalcHeight(desc, viewRect.width);
            Widgets.Label(new Rect(0f, y, viewRect.width, descHeight), desc);
            y += descHeight + 20f;

            if (Widgets.ButtonText(new Rect(0f, y, 120f, 32f), "FullyAutoOmniSurgeon_HediffDelete".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("FullyAutoOmniSurgeon_HediffDeleteConfirm".Translate(selectedHediff.LabelCap), () =>
                {
                    string labelStr = selectedHediff.LabelCap;
                    pawn.health.RemoveHediff(selectedHediff);
                    selectedHediff = null;
                    Messages.Message("FullyAutoOmniSurgeon_HediffDeleted".Translate(labelStr), MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            y += 40f;

            if (Event.current.type == EventType.Layout)
            {
                viewRect.height = y;
            }

            Widgets.EndScrollView();
        }

        private void DrawInfoLine(Rect viewRect, ref float y, string label, string value)
        {
            Rect labelRect = new Rect(0f, y, 120f, 22f);
            Rect valueRect = new Rect(124f, y, viewRect.width - 124f, 22f);
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;
            Widgets.Label(valueRect, value);
            y += 24f;
        }

        private string GetHediffSourceLabel(HediffDef def)
        {
            ModContentPack mod = def?.modContentPack;
            if (mod == null)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceUnknown".Translate();
            }

            if (mod.IsCoreMod)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceCore".Translate(mod.Name, mod.PackageIdPlayerFacing);
            }

            if (mod.IsOfficialMod)
            {
                return "FullyAutoOmniSurgeon_ThoughtSourceDlc".Translate(mod.Name, mod.PackageIdPlayerFacing);
            }

            return "FullyAutoOmniSurgeon_ThoughtSourceMod".Translate(mod.Name, mod.PackageIdPlayerFacing);
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
