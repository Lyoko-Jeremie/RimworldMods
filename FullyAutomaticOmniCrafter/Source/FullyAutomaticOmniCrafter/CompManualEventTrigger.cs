using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    
    public class CompProperties_ManualEventTrigger : CompProperties
    {
        public CompProperties_ManualEventTrigger()
        {
            this.compClass = typeof(CompManualEventTrigger);
        }
    }
    
    
    [StaticConstructorOnStartup]
    public static class CompManualEventTriggerTex
    {
        public static readonly Texture2D IconOpenEventMenu =
            ContentFinder<Texture2D>.Get("UI/Commands/ManualEventTrigger_OpenEventMenu", true) ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 一个可以选择并强制触发事件链开始的建筑组件
    /// </summary>
    public class CompManualEventTrigger : ThingComp
    {
        public CompProperties_ManualEventTrigger Props => (CompProperties_ManualEventTrigger)props;
        
        // 这个方法用于在选中建筑时生成底部的按钮
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 添加一个自定义按钮
            yield return new Command_Action
            {
                defaultLabel = "CompManualEventTrigger_OpenEventMenu_Label".Translate(),
                defaultDesc = "CompManualEventTrigger_OpenEventMenu_Desc".Translate(),
                icon = CompManualEventTriggerTex.IconOpenEventMenu,
                action = delegate { OpenAllEventsMenu(); }
            };
        }

        private void OpenAllEventsMenu()
        {
            Find.WindowStack.Add(new Dialog_ManualEventTrigger(this.parent.Map));
        }

        public static string GetDisableReason(IncidentDef incidentDef, Map map)
        {
            // 1. 检查游戏天数 (earliestDay 标签)
            if (GenDate.DaysPassed < incidentDef.earliestDay)
            {
                return "CompManualEventTrigger_DisableReason_WaitDays".Translate(incidentDef.earliestDay);
            }

            // 2. 检查人口数量 (minPopulation 标签)
            int colonistCount = map.mapPawns.FreeColonistsCount;
            if (colonistCount < incidentDef.minPopulation)
            {
                return "CompManualEventTrigger_DisableReason_MinPopulation".Translate(incidentDef.minPopulation, colonistCount);
            }

            // 3. 检查群落限制 (allowedBiomes 标签)
            if (incidentDef.allowedBiomes != null && !incidentDef.allowedBiomes.Contains(map.Biome))
            {
                return "CompManualEventTrigger_DisableReason_InvalidBiome".Translate();
            }

            // 4. 检查是否在冷却期 (minRefireDays 标签)
            if (incidentDef.minRefireDays > 0)
            {
                // 这里检查是否由于冷却导致的不可用（简化逻辑）
                // 实际上 Worker.CanFireNow 会检查 Storyteller.incidentQueue 和 StorytellerWatcher
            }

            // 5. 如果上面的 XML 常见条件都满足，那说明是被 C# 的动态逻辑拦截了
            return "CompManualEventTrigger_DisableReason_Unknown".Translate();
        }
    }

    public class Dialog_ManualEventTrigger : Window
    {
        private readonly Map map;
        private string searchText = "";
        private Vector2 scrollPosition;
        private List<IncidentDef> cachedIncidents;
        private List<IncidentDef> filteredIncidents;
        private Dictionary<IncidentDef, bool> canFireCache = new Dictionary<IncidentDef, bool>();

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1200f, (float)UI.screenWidth * 0.9f), 700f);

        public Dialog_ManualEventTrigger(Map map)
        {
            this.map = map;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.draggable = true;
            this.forcePause = true;

            // 初始化拼音索引
            if (OmniCrafterMod.Settings.enablePinyinSearch)
            {
                PinyinSearchEngine.EnsureIndexed(DefDatabase<IncidentDef>.AllDefs.ToList(), PinyinSource.ManualEvent);
            }

            BuildCache();
        }

        private void BuildCache()
        {
            cachedIncidents = DefDatabase<IncidentDef>.AllDefs
                .Where(incidentDef =>
                    incidentDef.targetTags.Contains(IncidentTargetTagDefOf.Map_PlayerHome) &&
                    incidentDef.baseChance > 0f &&
                    incidentDef.category != IncidentCategoryDefOf.Special)
                .OrderBy(d => d.label ?? d.defName)
                .ToList();
            
            RefreshCanFireCache();
            FilterIncidents();
        }

        private void RefreshCanFireCache()
        {
            canFireCache.Clear();
            foreach (var incidentDef in cachedIncidents)
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(incidentDef.category, map);
                if (incidentDef.category == IncidentCategoryDefOf.ThreatBig || incidentDef.category == IncidentCategoryDefOf.ThreatSmall)
                {
                    parms.points = StorytellerUtility.DefaultThreatPointsNow(map);
                }

                bool canFire;
                try
                {
                    canFire = incidentDef.Worker.CanFireNow(parms);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ManualEventTrigger] 缓存检查事件 {incidentDef.defName} 的 CanFireNow 时发生异常: {ex}");
                    canFire = false;
                }
                canFireCache[incidentDef] = canFire;
            }
        }

        private void FilterIncidents()
        {
            if (searchText.NullOrEmpty())
            {
                filteredIncidents = cachedIncidents;
                return;
            }

            string query = searchText.ToLower();
            filteredIncidents = cachedIncidents.Where(d =>
            {
                bool match = (d.label != null && d.label.ToLower().Contains(query)) ||
                             (d.defName != null && d.defName.ToLower().Contains(query));
                if (match) return true;

                if (OmniCrafterMod.Settings.enablePinyinSearch)
                {
                    return PinyinSearchEngine.MatchesPinyin(d, query, PinyinSource.ManualEvent);
                }

                return false;
            }).ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "Dialog_ManualEventTrigger_Title".Translate());
            Text.Font = GameFont.Small;

            float y = 45f;

            // 搜索框
            Widgets.Label(new Rect(0, y, 60f, 30f), "Dialog_ManualEventTrigger_Search".Translate());
            string newSearchText = Widgets.TextField(new Rect(65f, y, inRect.width - 150f, 30f), searchText);
            if (newSearchText != searchText)
            {
                searchText = newSearchText;
                FilterIncidents();
            }

            // 拼音切换按钮
            bool pinyin = OmniCrafterMod.Settings.enablePinyinSearch;
            if (Widgets.ButtonText(new Rect(inRect.width - 80f, y, 80f, 30f), pinyin ? "Dialog_ManualEventTrigger_PinyinOn".Translate() : "Dialog_ManualEventTrigger_PinyinOff".Translate()))
            {
                pinyin = !pinyin;
                OmniCrafterMod.Settings.enablePinyinSearch = pinyin;
                OmniCrafterMod.Settings.Write();
                if (pinyin)
                {
                    PinyinSearchEngine.EnsureIndexed(DefDatabase<IncidentDef>.AllDefs.ToList(), PinyinSource.ManualEvent);
                }
                FilterIncidents();
            }

            y += 40f;

            // 列表
            Rect outRect = new Rect(0, y, inRect.width, inRect.height - y - 55f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, filteredIncidents.Count * 35f);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float curY = 0;
            for (int i = 0; i < filteredIncidents.Count; i++)
            {
                IncidentDef incidentDef = filteredIncidents[i];
                Rect rowRect = new Rect(0, curY, viewRect.width, 30f);

                if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                if (!canFireCache.TryGetValue(incidentDef, out bool canFire))
                {
                    canFire = false;
                }

                string label = incidentDef.label ?? incidentDef.defName;
                string displayLabel = canFire ? label : (string)"Dialog_ManualEventTrigger_ForcedLabel".Translate(label, CompManualEventTrigger.GetDisableReason(incidentDef, map));

                GUI.color = canFire ? Color.white : Color.yellow;
                if (Widgets.ButtonInvisible(rowRect))
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(incidentDef.category, map);
                    if (incidentDef.category == IncidentCategoryDefOf.ThreatBig || incidentDef.category == IncidentCategoryDefOf.ThreatSmall)
                    {
                        parms.points = StorytellerUtility.DefaultThreatPointsNow(map);
                    }
                    ExecuteIncident(incidentDef, parms, canFire);
                }
                Widgets.Label(rowRect, displayLabel);
                GUI.color = Color.white;

                curY += 35f;
            }
            Widgets.EndScrollView();
        }

        private void ExecuteIncident(IncidentDef incidentDef, IncidentParms parms, bool canFire)
        {
            string label = incidentDef.label ?? incidentDef.defName;
            try
            {
                bool success = incidentDef.Worker.TryExecute(parms);
                if (success)
                {
                    Messages.Message(canFire ? "Dialog_ManualEventTrigger_Success".Translate(label) : "Dialog_ManualEventTrigger_ForcedSuccess".Translate(label), MessageTypeDefOf.NeutralEvent);
                    this.Close();
                }
                else
                {
                    Messages.Message("Dialog_ManualEventTrigger_Failed".Translate(label), MessageTypeDefOf.RejectInput);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[ManualEventTrigger] 触发事件 {incidentDef.defName} 时发生异常: {ex}");
                Messages.Message("Dialog_ManualEventTrigger_Exception".Translate(ex.Message), MessageTypeDefOf.RejectInput);
            }
        }
    }
}