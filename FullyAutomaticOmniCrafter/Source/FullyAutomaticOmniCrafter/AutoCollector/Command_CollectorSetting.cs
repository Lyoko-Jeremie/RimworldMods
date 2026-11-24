using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 收集器设置命令，用于多选同步。
    /// 仿照原版 <c>Command_SetTargetFuelLevel</c>：多个同名命令（groupKeyIgnoreContent 相同）
    /// 会被 GizmoGridDrawer 归为一组，点击时通过 InheritInteractionsFrom 收集组内全部收集器，
    /// 由同一个菜单一次性改写所有目标，避免“只改第一个选中的建筑”。
    /// </summary>
    public class Command_CollectorSetting : Command_Action
    {
        /// <summary>本次命令作用到的收集器列表（初始只有生成本命令的建筑，级联后补齐）。</summary>
        public List<Building_AutoItemCollector> collectors = new List<Building_AutoItemCollector>();

        public override bool InheritInteractionsFrom(Gizmo other)
        {
            Command_CollectorSetting command = other as Command_CollectorSetting;
            if (command != null && command.collectors != null)
            {
                for (int i = 0; i < command.collectors.Count; i++)
                {
                    Building_AutoItemCollector collector = command.collectors[i];
                    if (collector != null && !collectors.Contains(collector))
                    {
                        collectors.Add(collector);
                    }
                }
            }

            // 返回 false：阻止 other.ProcessInput 被再次调用，否则会弹出多个菜单叠在一起。
            return false;
        }
    }
}
