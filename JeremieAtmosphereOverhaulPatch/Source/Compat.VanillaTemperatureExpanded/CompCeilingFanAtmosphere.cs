using System.Collections.Generic;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using VanillaTemperatureExpanded.Comps;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded
{
    public class CompProperties_CeilingFanAtmosphere : CompProperties
    {
        /// <summary>送风半径（格）。小型吊扇约 6，大型约 9。</summary>
        public float radius = 6f;

        /// <summary>制冷系数。1 表示完全按 def 的 heatPerSecond 取值。</summary>
        public float coolingFactor = 1f;

        /// <summary>切向风速。RimWorld 的风速没有硬单位，3 约等于"能推动尘埃"。</summary>
        public float windSpeed = 3f;

        /// <summary>每 tick 向目标风靠拢的比例。</summary>
        public float windBlendPerTick = 0.1f;

        /// <summary>重扫格子的间隔。房间结构很少变，不必每 tick 扫。</summary>
        public int scanIntervalTicks = 60;

        /// <summary>重写连续源的间隔。温度变化慢，每 15 tick 一次足够，且省反射开销。</summary>
        public int applyIntervalTicks = 15;

        public CompProperties_CeilingFanAtmosphere()
        {
            compClass = typeof(CompCeilingFanAtmosphere);
        }
    }

    /// <summary>
    /// 吊扇的 AO 物理化。
    ///
    /// 原版 CompCeilingFan 每 60 tick 调一次 GenTemperature.PushHeat(-11)。
    /// 在 AO 地图上这条路径被转到 QueueLocalizedTemperatureDelta，
    /// 只影响中心 + 十字共 5 格、每格 -(11/5) = -2.2 ℃，
    /// 而 25 格房间原版是 -0.44 ℃ —— 局部过冷 5 倍，且完全不动空气。
    ///
    /// 这里的做法：把整间房（同 Room、非实心、圆内）作为连续源，
    /// 每格分摊 heatPerSecond / 60 / n，并给一个切向风场。
    /// 切向而非径向：RimWorld 的 AO 是二维单层，没有垂直方向；
    /// 径向会把空气持续推向墙边（净散度），切向则形成环流，净散度接近零。
    /// </summary>
    public class CompCeilingFanAtmosphere : ThingComp
    {
        private readonly List<IntVec3> _cells = new List<IntVec3>(128);

        private object _slots;
        private int _lastScanTick = -99999;
        private int _lastApplyTick = -99999;

        /// <summary>槽位是否已经全部清零。断电/停转时只在「从有到无」清一次。</summary>
        private bool _slotsCleared = true;

        /// <summary>上一次 Apply 写入的槽位数，用于判断是否需要清尾部残留。</summary>
        private int _writtenSlots;

        public CompProperties_CeilingFanAtmosphere Props
        {
            get { return (CompProperties_CeilingFanAtmosphere)props; }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            AoBridge.Resolve();
            if (!AoBridge.Available)
            {
                return;
            }
            _slots = AoBridge.NewSlots("JAP.Fan." + parent.thingIDNumber + ".");
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            if (_slots != null)
            {
                AoBridge.RemoveAll(_slots, parent, map);
                _slots = null;
            }
            base.PostDeSpawn(map, mode);
        }

        /// <summary>
        /// 接管一次 CompTick。返回 true 表示已处理（调用方应跳过原方法）；
        /// 返回 false 表示条件不具备，交回原版。
        /// </summary>
        public bool Tick(CompCeilingFan fan)
        {
            if (_slots == null || !AoBridge.Available)
            {
                return false;
            }

            CompPowerTrader power = parent.GetComp<CompPowerTrader>();
            if (power == null)
            {
                return false;
            }
            if (!power.PowerOn)
            {
                // 与原版一致：断电时完全不动作，扇叶停在当前角度。
                // 槽位只需清一次，不必每 tick 重复调 AO。
                ClearSlotsOnce();
                return true;
            }

            bool on = fan.ShouldPushHeatNow;
            int now = Find.TickManager.TicksGame;

            if (on && now - _lastScanTick >= Props.scanIntervalTicks)
            {
                _lastScanTick = now;
                RebuildCells();
            }

            if (!on || _cells.Count == 0)
            {
                ClearSlotsOnce();
                return true;
            }

            if (now - _lastApplyTick >= Props.applyIntervalTicks)
            {
                _lastApplyTick = now;
                Apply(fan);
            }
            return true;
        }

        private void Apply(CompCeilingFan fan)
        {
            int count = _cells.Count;
            if (count == 0)
            {
                ClearSlotsOnce();
                return;
            }

            // 单位是 ℃ / 游戏 tick：60 tick 累计正好等于 heatPerSecond。
            float perTick = fan.Props.heatPerSecond * Props.coolingFactor / 60f / count;
            IntVec3 center = parent.Position;

            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = _cells[i];
                Vector2 tangent = Tangent(cell, center);

                SlotRates rates = new SlotRates();
                rates.TemperaturePerTick = perTick;
                rates.MaintainTargetWind = true;
                rates.TargetWind = tangent * Props.windSpeed;
                rates.TargetWindBlendPerTick = Props.windBlendPerTick;
                AoBridge.SetSlot(_slots, parent, i, cell, rates);
            }

            // 只有上一次写过的槽位比本次多时，才需要清尾部残留；
            // 槽位数不变时 AO 那边本来就没有多余 handle。
            if (_writtenSlots > count)
            {
                AoBridge.ZeroFrom(_slots, parent, count);
            }
            _writtenSlots = count;
            _slotsCleared = false;
        }

        /// <summary>
        /// 只在「从有到无」时清一次槽位。原来每 tick 都调 ZeroFrom，
        /// AO 每次都要遍历 handles 并逐个 UpdateContinuousSource，
        /// 断电或停转的吊扇会白白占满 tick 预算。
        /// </summary>
        private void ClearSlotsOnce()
        {
            if (_slotsCleared)
            {
                return;
            }
            AoBridge.ZeroFrom(_slots, parent, 0);
            _slotsCleared = true;
            _writtenSlots = 0;
        }

        private void RebuildCells()
        {
            _cells.Clear();

            Map map = parent.Map;
            if (map == null)
            {
                return;
            }

            IntVec3 center = parent.Position;
            Room room = center.GetRoom(map);

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, Props.radius, true))
            {
                if (cell == center)
                {
                    // 中心格没有切向方向，给风会把目标风速拉成零。
                    continue;
                }
                if (!cell.InBounds(map))
                {
                    continue;
                }
                if (cell.Impassable(map))
                {
                    continue;
                }
                if (room != null && cell.GetRoom(map) != room)
                {
                    continue;
                }
                _cells.Add(cell);
            }
        }

        /// <summary>
        /// 扇叶周围的气流切向单位向量。
        ///
        /// 与扇叶视觉转向保持一致：RimWorld 的 (x, z) 投影到屏幕上 x 向右、z 向上，
        /// 顺时针切向为 (dz, -dx) / |r|。
        /// </summary>
        private static Vector2 Tangent(IntVec3 cell, IntVec3 center)
        {
            float dx = cell.x - center.x;
            float dz = cell.z - center.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 0.001f)
            {
                return Vector2.zero;
            }
            // 顺时针切向：(dz, -dx) / |r|
            return new Vector2(dz / length, -dx / length);
        }
    }
}
