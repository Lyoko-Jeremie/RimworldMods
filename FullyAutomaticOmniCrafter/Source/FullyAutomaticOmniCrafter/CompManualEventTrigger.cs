using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
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
                defaultLabel = "控制台：触发事件",
                defaultDesc = "查看并启动可用的事件链。",
                icon = CompManualEventTriggerTex.IconOpenEventMenu,
                action = delegate { OpenAllEventsMenu(); }
            };
        }

        private void OpenAllEventsMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            Map map = this.parent.Map;

            // 遍历游戏数据库中加载的所有事件 (IncidentDef)
            // 按名称拼音/字母排序，方便玩家查找
            var allIncidents = DefDatabase<IncidentDef>.AllDefs.OrderBy(d => d.label ?? d.defName);

            foreach (IncidentDef incidentDef in allIncidents)
            {
                // 过滤 1：目标必须是玩家地图
                if (!incidentDef.targetTags.Contains(IncidentTargetTagDefOf.Map_PlayerHome))
                {
                    continue;
                }

                // 过滤 2：排除事件链中间环节和隐藏事件（核心防线！）
                // 绝大多数正规、独立的事件，其 baseChance 必然大于 0。
                // 如果为 0，说明它只允许被其他代码（如任务脚本）在特定时刻显式调用。
                if (incidentDef.baseChance <= 0f)
                {
                    continue;
                }

                // 过滤 3：排除特定类别的系统内部事件
                // "Special" 类别通常用于游戏底层的机制过渡，不适合作为独立事件触发
                if (incidentDef.category == IncidentCategoryDefOf.Special)
                {
                    continue;
                }

                // 1. 为这个事件生成默认的上下文参数（比如当前地图、当前财富值算出的袭击点数等）
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(incidentDef.category, map);

                // 满足条件：选项为白色，点击即可触发
                string label = incidentDef.label != null ? incidentDef.label : incidentDef.defName;

                // 2. 调用原版的检测方法：这个事件现在能发生吗？
                // 这会自动检测事件自带的所有条件（比如温度、季节、财富、冷却时间等）
                if (incidentDef.Worker.CanFireNow(parms))
                {
                    options.Add(new FloatMenuOption(label, () =>
                    {
                        // 强制触发该事件
                        incidentDef.Worker.TryExecute(parms);
                        Messages.Message($"已强制触发事件: {label}", MessageTypeDefOf.NeutralEvent);
                    }));
                }
                else
                {
                    // 条件未满足，但允许强制触发！
                    string reason = GetDisableReason(incidentDef, map);

                    // 给标签加上红色/黄色警告，或者明确标注 [强制执行]
                    string forcedLabel = $"{label} [强制触发 - {reason}]";

                    options.Add(new FloatMenuOption(forcedLabel, () =>
                    {
                        // 核心：无视 CanFireNow，直接霸王硬上弓调用 TryExecute！
                        bool success = incidentDef.Worker.TryExecute(parms);

                        if (success)
                        {
                            Messages.Message($"已无视条件强制触发事件: {label}", MessageTypeDefOf.NeutralEvent);
                        }
                        else
                        {
                            // 如果强制触发仍然失败，给出提示
                            Messages.Message($"强制触发失败：该事件内部代码存在硬性物理阻断。", MessageTypeDefOf.RejectInput, false);
                        }
                    }));
                }
            }

            // 弹出浮动菜单。如果事件很多，RimWorld 的 FloatMenu 会自动生成滚动条。
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private string GetDisableReason(IncidentDef incidentDef, Map map)
        {
            // 1. 检查游戏天数 (earliestDay 标签)
            if (GenDate.DaysPassed < incidentDef.earliestDay)
            {
                return $"时间未到 (需存活至第 {incidentDef.earliestDay} 天)";
            }

            // 2. 检查人口数量 (minPopulation 标签)
            int colonistCount = map.mapPawns.FreeColonistsCount;
            if (colonistCount < incidentDef.minPopulation)
            {
                return $"人口不足 (需要 {incidentDef.minPopulation} 人，当前 {colonistCount} 人)";
            }

            // 3. 检查群落限制 (allowedBiomes 标签)
            if (incidentDef.allowedBiomes != null && !incidentDef.allowedBiomes.Contains(map.Biome))
            {
                return "当前群落地形不匹配";
            }

            // 4. 检查温度条件 (min/max 相关的隐藏逻辑，如果事件配置了特定气象要求)
            // 比如：某些冷冻事件要求温度极低
            // 虽然温度有时在 C# 中判断，但我们可以做一个大概的捕获

            // 5. 检查是否在冷却期 (minRefireDays 标签)
            // 游戏内部有一个事件历史记录板 (StorytellerWatcher)
            if (incidentDef.minRefireDays > 0)
            {
                // 查找这个事件上一次触发是什么时候
                int lastFireTick = Find.Storyteller.incidentQueue.DebugQueueReadout
                    .Where(q => q.def == incidentDef)
                    .Select(q => q.fireTick)
                    .FirstOrDefault(); // 注意：这里仅为简化示例，原版精确判断历史略复杂

                // 如果需要更精确的冷却时间诊断，需要查阅 Find.StoryWatcher
            }

            // 6. 如果上面的 XML 常见条件都满足，那说明是被 C# 的动态逻辑拦截了
            return "被事件内部 C# 逻辑拦截 (如：财富不足/缺少特定建筑等)";
        }
    }
}