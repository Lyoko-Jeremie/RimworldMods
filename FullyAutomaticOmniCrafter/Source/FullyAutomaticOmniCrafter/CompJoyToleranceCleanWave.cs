using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    
    // 自定义的全局地图状态类
    public class GameCondition_JoySoothe : GameCondition
    {
        private static HediffDef cachedHediffDef;

        private static readonly AccessTools.FieldRef<JoyToleranceSet, DefMap<JoyKindDef, float>> tolerancesRef =
            AccessTools.FieldRefAccess<JoyToleranceSet, DefMap<JoyKindDef, float>>("tolerances");

        private static readonly AccessTools.FieldRef<JoyToleranceSet, DefMap<JoyKindDef, bool>> boredRef =
            AccessTools.FieldRefAccess<JoyToleranceSet, DefMap<JoyKindDef, bool>>("bored");

        private static HediffDef HediffDefSoothe
        {
            get
            {
                if (cachedHediffDef == null)
                {
                    cachedHediffDef = DefDatabase<HediffDef>.GetNamed("Hediff_Omni_JoySootheVisual");
                }
                return cachedHediffDef;
            }
        }

        // 游戏引擎会每一刻（Tick）调用这个方法
        public override void GameConditionTick()
        {
            base.GameConditionTick();

            // 为了不卡顿，我们每 250 刻（游戏里大约十几分钟）执行一次实际逻辑
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                // 遍历受到该状态影响的所有地图
                foreach (Map map in this.AffectedMaps)
                {
                    ApplySootheAndHediff(map);
                }
            }
        }

        private void ApplySootheAndHediff(Map map)
        {
            // 获取我们在 XML 里定义的无副作用 Hediff
            // ../../Defs/HediffDef/Hediff_JoyResetInterference.xml
            HediffDef hediffDef = HediffDefSoothe;

            // 遍历地图上活动的殖民者
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs != null && p.needs.joy != null)
                {
                    // 1. 清空厌倦度 (使用 cached FieldRef 优化性能)
                    var tolerances = p.needs.joy.tolerances;
                    if (tolerances != null)
                    {
                        var tolerancesMap = tolerancesRef(tolerances);
                        if (tolerancesMap != null)
                        {
                            tolerancesMap.SetAll(0f);
                        }

                        var boredMap = boredRef(tolerances);
                        if (boredMap != null)
                        {
                            boredMap.SetAll(false);
                        }
                    }

                    // 2. 添加或刷新状态图标 (Hediff)
                    Hediff hediff = p.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                    if (hediff == null)
                    {
                        // 如果没有状态，则加上
                        hediff = p.health.AddHediff(hediffDef);
                    }
                    
                    // 3. 刷新状态的剩余时间 (防止状态消失)
                    // 我们给状态设置 600 刻的倒计时。由于我们每 250 刻刷新一次，所以只要机器开着，状态永远不会断。
                    // 一旦机器关机，右下角的地图状态结束，Hediff 就会在 600 刻（几秒钟）后自然消失。
                    HediffComp_Disappears disappearsComp = hediff.TryGetComp<HediffComp_Disappears>();
                    if (disappearsComp != null)
                    {
                        disappearsComp.ticksToDisappear = 600;
                    }
                }
            }
        }
    }
}