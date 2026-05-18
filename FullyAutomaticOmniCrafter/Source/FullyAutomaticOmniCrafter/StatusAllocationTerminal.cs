using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_StatusAllocationTerminal : CompProperties
    {
        public CompProperties_StatusAllocationTerminal()
        {
            this.compClass = typeof(StatusAllocationTerminal);
        }
    }

    public class HediffAssignment : IExposable
    {
        public HediffDef hediffDef;
        public BodyPartRecord bodyPart;

        public HediffAssignment()
        {
        }

        public HediffAssignment(HediffDef hediffDef, BodyPartRecord bodyPart)
        {
            this.hediffDef = hediffDef;
            this.bodyPart = bodyPart;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref hediffDef, "hediffDef");
            Scribe_BodyParts.Look(ref bodyPart, "bodyPart");
        }

        public override bool Equals(object obj)
        {
            if (obj is HediffAssignment other)
            {
                return other.hediffDef == hediffDef && other.bodyPart == bodyPart;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return (hediffDef?.GetHashCode() ?? 0) ^ (bodyPart?.GetHashCode() ?? 0);
        }

        public string Label => hediffDef.LabelCap + (bodyPart != null ? $" ({bodyPart.LabelCap})" : "");
    }

    public class HediffErrorRecord : IExposable
    {
        public string pawnName;
        public string bodyPartLabel;
        public string hediffLabel;
        public string timestamp;

        public HediffErrorRecord()
        {
        }

        public HediffErrorRecord(Pawn pawn, BodyPartRecord part, HediffDef hediff)
        {
            this.pawnName = pawn?.LabelCap ?? "Unknown";
            this.bodyPartLabel = part?.LabelCap ?? "Whole Body";
            this.hediffLabel = hediff?.LabelCap ?? "Unknown Hediff";
            this.timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnName, "pawnName");
            Scribe_Values.Look(ref bodyPartLabel, "bodyPartLabel");
            Scribe_Values.Look(ref hediffLabel, "hediffLabel");
            Scribe_Values.Look(ref timestamp, "timestamp");
        }
    }

    [StaticConstructorOnStartup]
    public static class StatusAllocationTerminalTex
    {
        public static readonly Texture2D IconAutoDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/StatusAllocationTerminal_AutoDialog", true) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconManualDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/StatusAllocationTerminal_ManualDialog", true) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconErrorLog =
            ContentFinder<Texture2D>.Get("UI/Buttons/DevRoot/ShowDebugLog", true) ??
            BaseContent.WhiteTex;
    }

    /// <summary>
    /// 一个可以手动为我方殖民者调整hediff的建筑
    /// 有两个功能模式：
    /// 1 自动光环模式
    /// 2 手动赋予移除模式
    ///
    /// 手动赋予移除模式：
    /// 建筑弹出一个菜单界面，并在菜单上为指定的我方pawn手动添加或移除状态，使得pawn即使离开地图也有这个这状态
    /// 从左到右ABCD四栏，A是pawn名字和头像，B是选中的pawn的当前状态列表（像原版一样以部位分组），C是状态模板，D是可用状态列表。
    /// 从D中选择状态，点击每个状态右侧的按钮弹出部位的下拉菜单，选择部位添加到C中，C中以部位为组分组显示，双击C中的条目移除状态。
    ///
    /// 自动光环模式：
    /// 目的：设置赋予模板（包括hediff和对于身体部位）和移除列表，自动对当前地图上的我方人员和动物及机器人和race进行自动赋予和自动移除。
    /// 建筑弹出一个菜单界面，在界面上设置自动光环模式的赋予参数，以及自动移除列表。
    /// 从左到右分ABC三栏，A是自动赋予模板列表（像原版一样以部位分组），B是自动移除列表，C是可选状态列表，C中每个条目尾部有两个按钮，分别用于添加和移除状态，添加按钮点击同样弹出部位的下拉菜单，选择部位添加到C中。
    /// 
    /// </summary>
    public class StatusAllocationTerminal : ThingComp
    {
        public CompProperties_StatusAllocationTerminal Props => (CompProperties_StatusAllocationTerminal)this.props;

        public List<HediffAssignment> autoTemplates = new List<HediffAssignment>();
        public List<HediffDef> autoRemovals = new List<HediffDef>();
        public List<HediffErrorRecord> failedHediffLogs = new List<HediffErrorRecord>();

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref autoTemplates, "autoTemplates", LookMode.Deep);
            Scribe_Collections.Look(ref autoRemovals, "autoRemovals", LookMode.Def);
            Scribe_Collections.Look(ref failedHediffLogs, "failedHediffLogs", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (autoTemplates == null) autoTemplates = new List<HediffAssignment>();
                if (autoRemovals == null) autoRemovals = new List<HediffDef>();
                if (failedHediffLogs == null) failedHediffLogs = new List<HediffErrorRecord>();
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            if (parent.Faction != Faction.OfPlayer || !parent.Spawned) return;

            Map map = parent.Map;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn.Faction != Faction.OfPlayer || pawn.Dead) continue;

                // 自动赋予
                foreach (var template in autoTemplates)
                {
                    if (template.hediffDef == null) continue;
                    if (!pawn.health.hediffSet.HasHediff(template.hediffDef, template.bodyPart))
                    {
                        try
                        {
                            pawn.health.AddHediff(template.hediffDef, template.bodyPart);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[StatusAllocationTerminal] Failed to add hediff {template.hediffDef.defName} to {pawn.LabelShort} at {template.bodyPart?.def.defName ?? "Whole Body"}: {ex.Message}");
                            failedHediffLogs.Add(new HediffErrorRecord(pawn, template.bodyPart, template.hediffDef));
                            if (failedHediffLogs.Count > 100) failedHediffLogs.RemoveAt(0); // 限制记录数量
                        }
                    }
                }

                // 自动移除
                if (autoRemovals.Count > 0)
                {
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int j = hediffs.Count - 1; j >= 0; j--)
                    {
                        if (autoRemovals.Contains(hediffs[j].def))
                        {
                            pawn.health.RemoveHediff(hediffs[j]);
                        }
                    }
                }
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            if (parent.Faction == Faction.OfPlayer)
            {
                yield return new Command_Action
                {
                    defaultLabel = "光环设置",
                    defaultDesc = "打开终端面板，设置光环模板。",
                    icon = StatusAllocationTerminalTex.IconAutoDialog,
                    action = delegate()
                    {
                        Find.WindowStack.Add(new Dialog_StatusAllocationTerminal(parent.Map, this, true));
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "手动设置",
                    defaultDesc = "打开终端面板，为我方人员分配或移除状态。",
                    icon = StatusAllocationTerminalTex.IconManualDialog,
                    action = delegate()
                    {
                        Find.WindowStack.Add(new Dialog_StatusAllocationTerminal(parent.Map, this, false));
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "错误日志",
                    defaultDesc = "查看在添加状态时遇到的错误记录。",
                    icon = StatusAllocationTerminalTex.IconErrorLog,
                    action = delegate()
                    {
                        Find.WindowStack.Add(new Dialog_StatusAllocationTerminalErrors(this));
                    }
                };
            }
        }
    }

    public class Dialog_StatusAllocationTerminalErrors : Window
    {
        private StatusAllocationTerminal comp;
        private Vector2 scrollPosition;

        public override Vector2 InitialSize => new Vector2(600f, 600f);

        public Dialog_StatusAllocationTerminalErrors(StatusAllocationTerminal comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "添加状态错误记录");
            Text.Font = GameFont.Small;

            Rect clearRect = new Rect(inRect.width - 100f, 0f, 100f, 30f);
            if (Widgets.ButtonText(clearRect, "清空日志"))
            {
                comp.failedHediffLogs.Clear();
            }

            Rect outRect = new Rect(0f, 40f, inRect.width, inRect.height - 100f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, comp.failedHediffLogs.Count * 25f);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float num = 0f;
            for (int i = comp.failedHediffLogs.Count - 1; i >= 0; i--)
            {
                var log = comp.failedHediffLogs[i];
                Rect rect = new Rect(0f, num, viewRect.width, 25f);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(rect);
                }
                Widgets.Label(rect, $"{log.timestamp}: [{log.pawnName}] 部位: {log.bodyPartLabel} 状态: {log.hediffLabel}");
                num += 25f;
            }
            Widgets.EndScrollView();
        }
    }

    // 3. 自定义 UI 窗口类 (绘制人员列表)
    public class Dialog_StatusAllocationTerminal : Window
    {
        private Map map;
        private StatusAllocationTerminal comp;
        private bool autoMode;

        private Vector2 scrollPosA = Vector2.zero;
        private Vector2 scrollPosB = Vector2.zero;
        private Vector2 scrollPosC = Vector2.zero;
        private Vector2 scrollPosD = Vector2.zero;

        private string searchFilter = "";
        private Pawn selectedPawn;
        private List<HediffAssignment> manualTemplates = new List<HediffAssignment>();

        private static List<HediffDef> cachedAllHediffs;

        public Dialog_StatusAllocationTerminal(Map map, StatusAllocationTerminal comp, bool autoMode)
        {
            this.map = map;
            this.comp = comp;
            this.autoMode = autoMode;
            this.doCloseX = true;
            this.doCloseButton = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;

            if (cachedAllHediffs == null)
            {
                cachedAllHediffs = DefDatabase<HediffDef>.AllDefs
                    .Where(d => !typeof(Hediff_Injury).IsAssignableFrom(d.hediffClass) &&
                                !typeof(Hediff_MissingPart).IsAssignableFrom(d.hediffClass))
                    .OrderBy(d => d.label)
                    .ToList();
            }
        }

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            string title = autoMode ? "状态光环管理" : "状态人员管理";
            Rect titleRect = new Rect(0f, 0f, 250f, 35f);
            Widgets.Label(titleRect, title);
            Text.Font = GameFont.Small;

            // ── 拼音搜索切换按钮 (参照 Dialog_OmniCrafter) ──
            float pinyinBtnX = titleRect.xMax + 10f;
            Rect pinyinBtnRect = new Rect(pinyinBtnX, 4f, 36f, 26f);
            bool pe = OmniCrafterMod.Settings.enablePinyinSearch;

            GUI.color = pe ? Color.cyan : new Color(0.55f, 0.55f, 0.55f);
            if (Widgets.ButtonText(pinyinBtnRect, "拼"))
            {
                pe = !pe;
                OmniCrafterMod.Settings.enablePinyinSearch = pe;
                OmniCrafterMod.Settings.Write();
                if (pe && !PinyinSearchEngine.IsReady)
                {
                    PinyinSearchEngine.BuildIndex(cachedAllHediffs);
                }
            }

            GUI.color = Color.white;
            TooltipHandler.TipRegion(pinyinBtnRect, pe ? "拼音搜索已开启" : "拼音搜索已关闭");

            // ── 重建拼音缓存按钮 ──
            float rebuildBtnX = pinyinBtnRect.xMax + 4f;
            Rect rebuildBtnRect = new Rect(rebuildBtnX, 4f, 26f, 26f);
            GUI.color = PinyinSearchEngine.IsReady ? Color.white : new Color(1f, 0.7f, 0.3f);
            if (Widgets.ButtonText(rebuildBtnRect, "R"))
            {
                PinyinSearchEngine.BuildIndex(cachedAllHediffs);
                Messages.Message("拼音索引已重建", MessageTypeDefOf.TaskCompletion, false);
            }

            GUI.color = Color.white;
            TooltipHandler.TipRegion(rebuildBtnRect, "重建拼音索引");

            Rect mainRect = new Rect(0f, 40f, inRect.width, inRect.height - 100f);
            if (autoMode)
            {
                DoAutoMode(mainRect);
            }
            else
            {
                DoManualMode(mainRect);
            }
        }

        private void DoManualMode(Rect rect)
        {
            float colWidth = rect.width / 4f;
            Rect rectA = new Rect(rect.x, rect.y, colWidth - 5f, rect.height);
            Rect rectB = new Rect(rectA.xMax + 5f, rect.y, colWidth - 5f, rect.height);
            Rect rectC = new Rect(rectB.xMax + 5f, rect.y, colWidth - 5f, rect.height);
            Rect rectD = new Rect(rectC.xMax + 5f, rect.y, colWidth - 5f, rect.height);

            // A: Pawn 列表
            DrawPawnList(rectA);
            // B: 选中 Pawn 的状态
            DrawPawnHediffs(rectB);
            // C: 状态模板
            DrawManualTemplates(rectC);
            // D: 可用状态
            DrawAvailableHediffs(rectD, (def) => AddToManualTemplate(def));
        }

        private void DoAutoMode(Rect rect)
        {
            float colWidth = rect.width / 3f;
            Rect rectA = new Rect(rect.x, rect.y, colWidth - 5f, rect.height);
            Rect rectB = new Rect(rectA.xMax + 5f, rect.y, colWidth - 5f, rect.height);
            Rect rectC = new Rect(rectB.xMax + 5f, rect.y, colWidth - 5f, rect.height);

            // A: 自动赋予模板
            DrawAutoTemplates(rectA);
            // B: 自动移除列表
            DrawAutoRemovals(rectB);
            // C: 可选状态
            DrawAvailableHediffs(rectC, (def) => AddToAutoTemplate(def), (def) => AddToAutoRemoval(def));
        }

        private void DrawPawnList(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            var pawns = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer && !p.Dead).ToList();
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, pawns.Count * 40f);
            Widgets.BeginScrollView(rect, ref scrollPosA, viewRect);
            float y = 0f;
            foreach (var pawn in pawns)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 35f);
                if (selectedPawn == pawn) Widgets.DrawHighlightSelected(rowRect);
                else if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                if (Widgets.ButtonInvisible(rowRect)) selectedPawn = pawn;

                GUI.DrawTexture(new Rect(0f, y, 35f, 35f), PortraitsCache.Get(pawn, new Vector2(35f, 35f), Rot4.South));
                Widgets.Label(new Rect(40f, y + 5f, viewRect.width - 40f, 30f), pawn.LabelShort);
                y += 40f;
            }

            Widgets.EndScrollView();
        }

        private void DrawPawnHediffs(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "当前状态 (部位分组)");
            Widgets.DrawMenuSection(rect);
            if (selectedPawn == null) return;

            var hediffs = selectedPawn.health.hediffSet.hediffs.OrderBy(h => h.Part?.Index ?? -1).ToList();
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, hediffs.Count * 30f);
            Widgets.BeginScrollView(rect, ref scrollPosB, viewRect);
            float y = 0f;
            foreach (var h in hediffs)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 25f);
                string label = (h.Part != null ? "[" + h.Part.LabelCap + "] " : "") + h.LabelCap;
                Widgets.Label(rowRect, label);
                TooltipHandler.TipRegion(rowRect, new TipSignal(() => h.GetTooltip(selectedPawn, false), h.GetHashCode()));
                if (Widgets.ButtonText(new Rect(viewRect.width - 25f, y, 25f, 25f), "X"))
                {
                    selectedPawn.health.RemoveHediff(h);
                }

                y += 30f;
            }

            Widgets.EndScrollView();
        }

        private void DrawManualTemplates(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "手动模板 (双击移除)");
            Widgets.DrawMenuSection(rect);
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, manualTemplates.Count * 30f + 40f);
            Widgets.BeginScrollView(rect, ref scrollPosC, viewRect);
            float y = 0f;
            for (int i = 0; i < manualTemplates.Count; i++)
            {
                var template = manualTemplates[i];
                Rect rowRect = new Rect(0f, y, viewRect.width, 25f);
                Widgets.Label(rowRect, template.Label);
                TooltipHandler.TipRegion(rowRect, template.hediffDef.description);
                if (Widgets.ButtonInvisible(rowRect, true) && Event.current.clickCount == 2)
                {
                    manualTemplates.RemoveAt(i);
                    i--;
                }

                y += 30f;
            }

            if (selectedPawn != null && manualTemplates.Count > 0)
            {
                if (Widgets.ButtonText(new Rect(0f, y, viewRect.width, 30f), "应用于选中人员"))
                {
                    foreach (var t in manualTemplates)
                    {
                        if (!selectedPawn.health.hediffSet.HasHediff(t.hediffDef, t.bodyPart))
                            selectedPawn.health.AddHediff(t.hediffDef, t.bodyPart);
                    }
                }
            }

            Widgets.EndScrollView();
        }

        private void DrawAvailableHediffs(Rect rect, Action<HediffDef> onAdd, Action<HediffDef> onRemove = null)
        {
            searchFilter = Widgets.TextField(new Rect(rect.x, rect.y - 30f, rect.width, 25f), searchFilter);
            Widgets.DrawMenuSection(rect);

            string query = searchFilter.ToLower();
            bool usePinyin = !string.IsNullOrEmpty(query) && OmniCrafterMod.Settings.enablePinyinSearch && PinyinSearchEngine.IsReady;

            var filtered = cachedAllHediffs.Where(d =>
            {
                if (string.IsNullOrEmpty(query)) return true;
                if (d.label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (usePinyin && PinyinSearchEngine.MatchesPinyin(d, query)) return true;
                return false;
            }).ToList();

            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, filtered.Count * 35f);
            Widgets.BeginScrollView(rect, ref scrollPosD, viewRect);
            float y = 0f;
            foreach (var def in filtered)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 30f);
                Widgets.Label(new Rect(0f, y + 5f, viewRect.width - 65f, 25f), def.LabelCap);
                TooltipHandler.TipRegion(rowRect, def.description);

                float btnX = viewRect.width - 30f;
                if (onRemove != null)
                {
                    if (Widgets.ButtonText(new Rect(btnX - 30f, y, 25f, 25f), "+")) onAdd(def);
                    if (Widgets.ButtonText(new Rect(btnX, y, 25f, 25f), "-")) onRemove(def);
                }
                else
                {
                    if (Widgets.ButtonText(new Rect(btnX, y, 25f, 25f), "+")) onAdd(def);
                }

                y += 35f;
            }

            Widgets.EndScrollView();
        }

        private void DrawAutoTemplates(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "自动赋予列表");
            Widgets.DrawMenuSection(rect);
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, comp.autoTemplates.Count * 30f);
            Widgets.BeginScrollView(rect, ref scrollPosA, viewRect);
            float y = 0f;
            for (int i = 0; i < comp.autoTemplates.Count; i++)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 25f);
                Widgets.Label(rowRect, comp.autoTemplates[i].Label);
                TooltipHandler.TipRegion(rowRect, comp.autoTemplates[i].hediffDef.description);
                if (Widgets.ButtonText(new Rect(viewRect.width - 25f, y, 25f, 25f), "X"))
                {
                    comp.autoTemplates.RemoveAt(i);
                    i--;
                }

                y += 30f;
            }

            Widgets.EndScrollView();
        }

        private void DrawAutoRemovals(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "自动移除列表");
            Widgets.DrawMenuSection(rect);
            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, comp.autoRemovals.Count * 30f);
            Widgets.BeginScrollView(rect, ref scrollPosB, viewRect);
            float y = 0f;
            for (int i = 0; i < comp.autoRemovals.Count; i++)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 25f);
                Widgets.Label(rowRect, comp.autoRemovals[i].LabelCap);
                TooltipHandler.TipRegion(rowRect, comp.autoRemovals[i].description);
                if (Widgets.ButtonText(new Rect(viewRect.width - 25f, y, 25f, 25f), "X"))
                {
                    comp.autoRemovals.RemoveAt(i);
                    i--;
                }

                y += 30f;
            }

            Widgets.EndScrollView();
        }

        private void AddToManualTemplate(HediffDef def)
        {
            ShowPartPicker(def, (part) => { manualTemplates.Add(new HediffAssignment(def, part)); });
        }

        private void AddToAutoTemplate(HediffDef def)
        {
            ShowPartPicker(def, (part) =>
            {
                if (!comp.autoTemplates.Any(a => a.hediffDef == def && a.bodyPart == part))
                    comp.autoTemplates.Add(new HediffAssignment(def, part));
            });
        }

        private void AddToAutoRemoval(HediffDef def)
        {
            if (!comp.autoRemovals.Contains(def))
                comp.autoRemovals.Add(def);
        }

        private void ShowPartPicker(HediffDef def, Action<BodyPartRecord> onSelected)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            list.Add(new FloatMenuOption("全身 (null)", () => onSelected(null)));

            if (selectedPawn != null)
            {
                foreach (var part in selectedPawn.RaceProps.body.AllParts)
                {
                    list.Add(new FloatMenuOption(part.LabelCap, () => onSelected(part)));
                }
            }
            else
            {
                // 如果没选 Pawn，提供基础人类的部位作为参考（RimWorld 默认逻辑通常如此）
                BodyDef humanBody = BodyDefOf.Human;
                foreach (var part in humanBody.AllParts)
                {
                    list.Add(new FloatMenuOption(part.LabelCap, () => onSelected(part)));
                }
            }

            Find.WindowStack.Add(new FloatMenu(list));
        }
    }
}