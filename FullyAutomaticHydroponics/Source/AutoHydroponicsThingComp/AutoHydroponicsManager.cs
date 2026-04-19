using System.Collections.Generic;
using Verse;

namespace FullyAutoHydroponicsThingComp
{
    // 继承 MapComponent，每个地图实例会自动生成一个此对象
    public class AutoHydroponicsManager : MapComponent
    {
        // 使用 HashSet 保证查询和增删的速度极快，且不会重复添加
        private readonly HashSet<ThingComp_FullyAutoHydroponics> _activeComps = new HashSet<ThingComp_FullyAutoHydroponics>();

        private int _tickCounter = 0;

        public AutoHydroponicsManager(Map map) : base(map)
        {
        }

        // 提供给组件的注册接口（开启开关时调用）
        public void Register(ThingComp_FullyAutoHydroponics comp)
        {
            _activeComps.Add(comp);
        }

        // 提供给组件的注销接口（关闭开关时调用）
        public void Deregister(ThingComp_FullyAutoHydroponics comp)
        {
            _activeComps.Remove(comp);
        }

        // MapComponent 自带每 tick 执行一次的心跳
        public override void MapComponentTick()
        {
            base.MapComponentTick();

            _tickCounter++;
            // 模拟 Rare Tick 的频率（每 250 tick 执行一次）
            if (_tickCounter >= 250)
            {
                _tickCounter = 0;

                // 仅遍历当前开启了开关的组件并执行逻辑
                // 使用 RemoveWhere 处理那些可能被意外摧毁但没来得及注销的建筑
                _activeComps.RemoveWhere(comp => comp.parent == null || !comp.parent.Spawned || comp.parent.Destroyed);

                foreach (var comp in _activeComps)
                {
                    comp.DoAutoWork(); // 调用你抽取出来的核心逻辑
                }
            }
        }
    }
}