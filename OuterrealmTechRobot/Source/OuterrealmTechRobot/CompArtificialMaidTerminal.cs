using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    public class CompProperties_ArtificialMaidTerminal : CompProperties
    {
        public CompProperties_ArtificialMaidTerminal()
        {
            this.compClass = typeof(CompArtificialMaidTerminal);
        }
    }

    public class CompArtificialMaidTerminal : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra()) yield return g;

            yield return new Command_Action
            {
                defaultLabel = "ModifyArtificialMaidLabel".Translate(),
                defaultDesc = "ModifyArtificialMaidDesc".Translate(),
                icon = ArtificialMaidTex.IconModifyMaid,
                action = delegate
                {
                    List<FloatMenuOption> list = new List<FloatMenuOption>();
                    foreach (Pawn pawn in this.parent.Map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                        {
                            Pawn localPawn = pawn;
                            list.Add(new FloatMenuOption(localPawn.LabelCap,
                                delegate { OpenModificationMenu(localPawn); }));
                        }
                    }

                    if (list.Count == 0)
                    {
                        list.Add(new FloatMenuOption("NoArtificialMaidFound".Translate(), null));
                    }

                    Find.WindowStack.Add(new FloatMenu(list));
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "ArtificialMaidBackupCloudLabel".Translate(),
                defaultDesc = "ArtificialMaidBackupCloudDesc".Translate(),
                icon = ArtificialMaidTex.IconBackupCloud,
                action = OpenBackupCloudMenu
            };
        }

        private void OpenBackupCloudMenu()
        {
            Find.WindowStack.Add(new Dialog_ArtificialMaidBackupCloud(parent.Map, parent.Position));
        }

        private void OpenModificationMenu(Pawn pawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();

            list.Add(new FloatMenuOption("ModifyChildhoodLabel".Translate(),
                delegate { OpenBackstoryMenu(pawn, BackstorySlot.Childhood); }));

            list.Add(new FloatMenuOption("ModifyAdulthoodLabel".Translate(),
                delegate { OpenBackstoryMenu(pawn, BackstorySlot.Adulthood); }));

            list.Add(new FloatMenuOption("ModifyTraitsLabel".Translate(), delegate { OpenTraitMenu(pawn); }));

            // 文化修改（仅 Ideology DLC 激活且非无文化模式时显示）
            if (ModsConfig.IdeologyActive && Find.IdeoManager != null && !Find.IdeoManager.classicMode)
            {
                list.Add(new FloatMenuOption("ModifyIdeoLabel".Translate(), delegate { OpenIdeoMenu(pawn); }));
            }

            list.Add(new FloatMenuOption("AutofixReplenishLabel".Translate(), delegate
            {
                var comp = CompArtificialMaid.GetCompCached(pawn);
                if (comp != null)
                {
                    comp.FullRepair();
                    Messages.Message("ArtificialMaidFixedMessage".Translate(pawn.LabelShort),
                        MessageTypeDefOf.PositiveEvent);
                }
            }));

            list.Add(new FloatMenuOption("TeleportArtificialMaidLabel".Translate(), delegate
            {
                pawn.Position = this.parent.Position;
                // 使用原版传送收尾，统一重置寻路与绘制状态，并中断传送前的旧任务。
                pawn.Notify_Teleported();

                Messages.Message("ArtificialMaidTeleportedMessage".Translate(pawn.LabelShort),
                    MessageTypeDefOf.PositiveEvent);
            }));

            Find.WindowStack.Add(new FloatMenu(list));
        }

        private void OpenIdeoMenu(Pawn pawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();

            // 列出所有可用文化，排除隐藏文化（如 Anomaly 的实体文化 Horaxian）
            foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
            {
                if (ideo.hidden || ideo == Find.IdeoManager.Horaxian) continue;

                Ideo localIdeo = ideo;
                bool isCurrent = pawn.Ideo == localIdeo;
                string label = isCurrent
                    ? (string)"ArtificialMaidIdeoCurrent".Translate(localIdeo.name)
                    : localIdeo.name;

                list.Add(new FloatMenuOption(label, delegate
                {
                    if (pawn.ideo == null) return; // 防御性检查
                    pawn.ideo.SetIdeo(localIdeo);
                    CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
                    if (comp != null) comp.ideologySet = true; // 标记为玩家指定，自动修复不再清除
                    Messages.Message("ArtificialMaidIdeoUpdated".Translate(pawn.LabelShort, localIdeo.name),
                        MessageTypeDefOf.PositiveEvent);
                }));
            }

            list.Add(new FloatMenuOption("ClearArtificialMaidIdeoLabel".Translate(), delegate
            {
                if (pawn.ideo != null) pawn.ideo.SetIdeo(null);
                CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
                if (comp != null) comp.ideologySet = false; // 恢复出厂无文化状态
                Messages.Message("ArtificialMaidIdeoCleared".Translate(pawn.LabelShort),
                    MessageTypeDefOf.PositiveEvent);
            }));

            Find.WindowStack.Add(new FloatMenu(list));
        }

        private void OpenBackstoryMenu(Pawn pawn, BackstorySlot slot)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            foreach (var bs in DefDatabase<BackstoryDef>.AllDefs)
            {
                if (bs.slot == slot && bs.spawnCategories != null)
                {
                    bool found = false;
                    for (int i = 0; i < bs.spawnCategories.Count; i++)
                    {
                        if (bs.spawnCategories[i] == "ArtificialMaidBackstory")
                        {
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        list.Add(new FloatMenuOption(bs.title, delegate
                        {
                            if (slot == BackstorySlot.Childhood) pawn.story.Childhood = bs;
                            else pawn.story.Adulthood = bs;
                            Messages.Message(
                                "ArtificialMaidBackstoryUpdated".Translate(pawn.LabelShort, slot.ToString(), bs.title),
                                MessageTypeDefOf.PositiveEvent);
                        }));
                    }
                }
            }

            Find.WindowStack.Add(new FloatMenu(list));
        }

        private void OpenTraitMenu(Pawn pawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            foreach (var trait in DefDatabase<TraitDef>.AllDefs)
            {
                if (trait.defName != null && trait.defName.StartsWith("ArtificialMaidTrait_"))
                {
                    list.Add(new FloatMenuOption(trait.degreeDatas[0].label, delegate
                    {
                        if (pawn.story.traits.HasTrait(trait))
                        {
                            Messages.Message("ArtificialMaidAlreadyHasTrait".Translate(pawn.LabelShort),
                                MessageTypeDefOf.RejectInput);
                            return;
                        }

                        pawn.story.traits.GainTrait(new Trait(trait));
                        Messages.Message(
                            "ArtificialMaidTraitAdded".Translate(trait.degreeDatas[0].label, pawn.LabelShort),
                            MessageTypeDefOf.PositiveEvent);
                    }));
                }
            }

            list.Add(new FloatMenuOption("ClearArtificialMaidTraitsLabel".Translate(), delegate
            {
                var allTraits = pawn.story.traits.allTraits;
                for (int i = allTraits.Count - 1; i >= 0; i--)
                {
                    var t = allTraits[i];
                    if (t.def != null && t.def.defName != null && t.def.defName.StartsWith("ArtificialMaidTrait_"))
                    {
                        pawn.story.traits.RemoveTrait(t);
                    }
                }

                Messages.Message("ArtificialMaidTraitsCleared".Translate(pawn.LabelShort),
                    MessageTypeDefOf.PositiveEvent);
            }));

            Find.WindowStack.Add(new FloatMenu(list));
        }
    }
}
