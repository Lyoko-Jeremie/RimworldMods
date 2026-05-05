using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;


namespace FullyAutomaticOmniCrafter
{
    // 定义组件的属性，方便在 XML 中配置扫描间隔
    public class CompProperties_GlobalFireExtinguisher : CompProperties
    {
        // 默认 60 Ticks = 1秒游戏时间
        public int checkIntervalTicks = 60; 

        public CompProperties_GlobalFireExtinguisher()
        {
            this.compClass = typeof(CompGlobalFireExtinguisher);
        }
    }

    // 核心逻辑组件
    public class CompGlobalFireExtinguisher : ThingComp
    {
        public CompProperties_GlobalFireExtinguisher Props => (CompProperties_GlobalFireExtinguisher)props;

        public override void CompTick()
        {
            base.CompTick();

            // 【性能优化1】降频且错峰执行。
            // IsHashIntervalTick 会根据建筑的 ThingID 进行哈希计算，
            // 确保玩家即使建了 10 个灭火器，它们也不会在同一帧同时扫描，避免瞬间掉帧。
            if (parent.IsHashIntervalTick(Props.checkIntervalTicks))
            {
                ExtinguishAllFires();
            }
        }

        private void ExtinguishAllFires()
        {
            if (parent.Map == null) return;

            // 可选检查：如果建筑需要电力，且没电，则不工作
            CompPowerTrader powerComp = parent.GetComp<CompPowerTrader>();
            if (powerComp != null && !powerComp.PowerOn) return;

            // 【性能优化2】直接读取游戏引擎缓存的火焰列表，O(1) 的查找复杂度！
            // 绝对不要用 Map.AllCells 遍历全图！
            List<Thing> fires = parent.Map.listerThings.ThingsInGroup(ThingRequestGroup.Fire);

            if (fires.Count == 0) return;

            // 【性能优化3】逆向 for 循环。
            // 为什么？因为 fire.Destroy() 会将火从 fires 列表中移除。
            // 正向循环或 foreach 会导致索引越界或集合修改异常 (CollectionModifiedException)。
            for (int i = fires.Count - 1; i >= 0; i--)
            {
                Thing fire = fires[i];
                
                // 可选：在灭火的位置生成一点水雾或烟雾特效，让视觉不那么突兀
                FleckMaker.ThrowSmoke(fire.DrawPos, parent.Map, 1.5f);
                FleckMaker.ThrowMicroSparks(fire.DrawPos, parent.Map);

                // 销毁火焰对象 (Vanish 代表直接消失，不留残渣)
                fire.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
