using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutoHydroponicsThingComp
{
    // 继承 MapComponent，每个地图实例会自动生成一个此对象
    // 采用分帧处理，以避免一次性处理过多组件导致的“瞬时卡顿”（Spike Lag） “惊群效应”（Thundering Herd）
    public class AutoHydroponicsManager : MapComponent
    {
        // 改用 List 以支持按索引遍历
        private List<ThingComp_FullyAutoHydroponics> activeComps = new List<ThingComp_FullyAutoHydroponics>();

        // 【核心新增】：按物品类型缓存目标存储格
        // 键：物品的 Def (如水稻、土豆)
        // 值：上次成功找到的有效存储格坐标
        private Dictionary<ThingDef, IntVec3> _storeCellCache = new Dictionary<ThingDef, IntVec3>();

        public AutoHydroponicsManager(Map map) : base(map)
        {
        }

        // 提供给组件的注册接口（开启开关时调用）
        public void Register(ThingComp_FullyAutoHydroponics comp)
        {
            if (!activeComps.Contains(comp))
            {
                activeComps.Add(comp);
            }
        }

        // 提供给组件的注销接口（关闭开关时调用）
        public void Deregister(ThingComp_FullyAutoHydroponics comp)
        {
            activeComps.Remove(comp);
        }

        // MapComponent 自带每 tick 执行一次的心跳
        public override void MapComponentTick()
        {
            base.MapComponentTick();

            if (activeComps.Count == 0) return;

            // 获取当前处于 250 ticks 循环中的哪一帧 (0 到 249)
            int tickInCycle = Find.TickManager.TicksGame % 250;

            // 计算这一帧应该处理多少个组件
            // 假设有 5000 个对象，5000 / 250 = 20。每帧恰好处理 20 个。
            // 使用 CeilToInt 向上取整，确保即使数量少于 250 也能被处理。
            int batchSize = Mathf.CeilToInt((float)activeComps.Count / 250f);

            // 计算当前帧的起始和结束索引
            int startIndex = tickInCycle * batchSize;
            int endIndex = Mathf.Min(startIndex + batchSize, activeComps.Count);

            // 仅处理属于当前帧的切片
            // 倒序遍历（或从后往前清）是处理可能在循环中被销毁对象的安全做法
            for (int i = endIndex - 1; i >= startIndex; i--)
            {
                var comp = activeComps[i];

                // 二次校验，防止对象在等待分片期间被摧毁但未正确注销
                if (comp != null && comp.parent != null && comp.parent.Spawned && !comp.parent.Destroyed)
                {
                    comp.DoAutoWork();
                }
                else
                {
                    // 自动清理脏数据
                    activeComps.RemoveAt(i);
                }
            }
        }

        // 【核心新增】：提供给水培盆调用的智能寻址方法
        public bool TryGetSmartStoreCell(Thing item, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            // 1. 尝试命中缓存
            if (_storeCellCache.TryGetValue(item.def, out IntVec3 cachedCell))
            {
                // 极速单格验证：检查缓存的格子当前是否还能塞下这个物品
                if (StoreUtility.IsGoodStoreCell(cachedCell, this.map, item, null, Faction.OfPlayer))
                {
                    result = cachedCell;
                    return true;
                }
                else
                {
                    // 缓存已失效（格子满了，或者存储区设置被玩家改了），移除旧缓存
                    _storeCellCache.Remove(item.def);
                }
            }

            // 2. 缓存未命中或已失效，执行一次（且仅执行一次）昂贵的全局搜索
            if (StoreUtility.TryFindBestBetterStoreCellFor(
                    item, null, this.map,
                    StoragePriority.Unstored, Faction.OfPlayer,
                    out IntVec3 newCell))
            {
                // 搜索成功，更新缓存
                _storeCellCache[item.def] = newCell;
                result = newCell;
                return true;
            }

            // 3. 全局搜索也失败了（地图上真没地方放了）
            return false;
        }
    }
}