using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FullyAutomaticOmniCrafter;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 统一的「受保护」判定：护盾覆盖格、随身温控 pawn。
    ///
    /// 护盾覆盖用 bool[] 掩膜缓存：按 AO 自己的 WindDamageCheckIntervalTicks 刷新，
    /// 风害批量判定（结构成员、可穿透物、屋顶、物品）走掩膜 O(1)，
    /// 只有 pawn 这类单点判定才实时查超维的 cellCache。
    /// 掩膜重建只遍历护盾包围盒，不扫全图；护盾开关/尺寸变化由超维 DirtyCache 触发全量置脏。
    ///
    /// AO 的风害 tick 全在 AtmosphereMapComponent.MapComponentTick（主线程），
    /// 因此这里可以安全访问 MapComponent、Thing 与地图格坐标。
    /// </summary>
    internal static class OmniProtection
    {
        private static readonly ConditionalWeakTable<Map, TrackerHolder> Trackers =
            new ConditionalWeakTable<Map, TrackerHolder>();

        private static readonly Dictionary<Map, ShieldMask> Masks = new Dictionary<Map, ShieldMask>();

        private static FieldInfo allInterceptorsField;
        private static FieldInfo windCheckIntervalField;
        private static bool resolved;

        private sealed class TrackerHolder
        {
            public OmniInterceptorTracker Tracker;
        }

        private sealed class ShieldMask
        {
            public bool[] Cells;
            public int BuiltTick = -1;
            public int Interval = 30;
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;
            allInterceptorsField = AccessTools.Field(typeof(OmniInterceptorTracker), "allInterceptors");
            windCheckIntervalField = AccessTools.Field(
                AccessTools.TypeByName("AtmosphereOverhaul.Simulation.LocalAtmosphereSettings"),
                "WindDamageCheckIntervalTicks");
        }

        internal static OmniInterceptorTracker GetTracker(Map map)
        {
            if (map == null)
            {
                return null;
            }

            TrackerHolder holder;
            if (!Trackers.TryGetValue(map, out holder))
            {
                holder = new TrackerHolder { Tracker = map.GetComponent<OmniInterceptorTracker>() };
                try
                {
                    Trackers.Add(map, holder);
                }
                catch (ArgumentException)
                {
                    // 弱表不支持并发 Add；本入口目前只在主线程调用，这里仍防御第三方补丁
                    // 从后台线程触发风害判定的情况。
                }
            }

            return holder.Tracker;
        }

        /// <summary>护盾开关/尺寸变化时由超维 DirtyCache 触发，下一次风害判定立即重建掩膜。</summary>
        internal static void MarkAllDirty()
        {
            foreach (KeyValuePair<Map, ShieldMask> pair in Masks)
            {
                pair.Value.BuiltTick = -1;
            }
        }

        /// <summary>地图卸载时释放掩膜与 tracker 缓存。</summary>
        internal static void Forget(Map map)
        {
            if (map == null)
            {
                return;
            }

            Masks.Remove(map);
            Trackers.Remove(map);
        }

        /// <summary>批量判定入口：读护盾掩膜（O(1)，按 AO 检查间隔刷新）。</summary>
        internal static bool IsShieldProtected(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }

            ShieldMask mask = GetMask(map);
            if (mask.Cells == null)
            {
                return false;
            }

            return mask.Cells[map.cellIndices.CellToIndex(cell)];
        }

        /// <summary>单点实时判定：直接查超维每格 cellCache，用于 pawn 这类低频调用。</summary>
        internal static bool IsShieldProtectedLive(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }

            OmniInterceptorTracker tracker = GetTracker(map);
            if (tracker == null)
            {
                return false;
            }

            CompOmniProjectileInterceptor protector;
            return tracker.IsCellWithinShield(cell, out protector);
        }

        /// <summary>该 pawn 是否启用了随身温控（超维的「指定 pawn」集合）。</summary>
        internal static bool IsPersonalProtected(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            Map map = pawn.MapHeld;
            if (map == null)
            {
                return false;
            }

            float temperature;
            return PersonalTemperatureUtility.TryGetOverride(pawn, pawn.PositionHeld, map, out temperature);
        }

        /// <summary>pawn 是否应完全不受风影响：随身温控启用，或站在护盾内（实时判定）。</summary>
        internal static bool IsPawnWindProtected(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            if (IsPersonalProtected(pawn))
            {
                return true;
            }

            return IsShieldProtectedLive(pawn.PositionHeld, pawn.MapHeld);
        }

        private static ShieldMask GetMask(Map map)
        {
            ShieldMask mask;
            if (!Masks.TryGetValue(map, out mask))
            {
                mask = new ShieldMask();
                Masks[map] = mask;
            }

            int tick = GenTicks.TicksGame;
            if (mask.Cells == null || mask.BuiltTick < 0 || tick - mask.BuiltTick >= mask.Interval)
            {
                RebuildMask(map, mask);
            }

            return mask;
        }

        private static void RebuildMask(Map map, ShieldMask mask)
        {
            Resolve();

            int size = map.cellIndices.NumGridCells;
            if (mask.Cells == null || mask.Cells.Length != size)
            {
                mask.Cells = new bool[size];
            }
            else
            {
                Array.Clear(mask.Cells, 0, size);
            }

            mask.BuiltTick = GenTicks.TicksGame;
            mask.Interval = GetInterval(map);

            OmniInterceptorTracker tracker = GetTracker(map);
            if (tracker == null || allInterceptorsField == null)
            {
                return;
            }

            List<CompOmniProjectileInterceptor> list =
                allInterceptorsField.GetValue(tracker) as List<CompOmniProjectileInterceptor>;
            if (list == null)
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                CompOmniProjectileInterceptor interceptor = list[i];
                if (interceptor == null || !interceptor.Active || interceptor is CompOmniForceFieldDome)
                {
                    continue;
                }

                MarkInterceptor(map, mask.Cells, interceptor);
            }
        }

        private static void MarkInterceptor(Map map, bool[] cells, CompOmniProjectileInterceptor interceptor)
        {
            CompOmniRectangleProjectileInterceptor rectangle = interceptor as CompOmniRectangleProjectileInterceptor;
            if (rectangle != null)
            {
                CellRect rect = rectangle.OccupiedRect;
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 cell = new IntVec3(x, 0, z);
                        if (cell.InBounds(map))
                        {
                            cells[map.cellIndices.CellToIndex(cell)] = true;
                        }
                    }
                }

                return;
            }

            IntVec3 center = interceptor.parent.Position;
            float radius = interceptor.Radius;
            int minX = Mathf.FloorToInt(center.x - radius);
            int maxX = Mathf.FloorToInt(center.x + radius);
            int minZ = Mathf.FloorToInt(center.z - radius);
            int maxZ = Mathf.FloorToInt(center.z + radius);
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    IntVec3 cell = new IntVec3(x, 0, z);
                    if (cell.InBounds(map) && interceptor.IsCellInside(cell))
                    {
                        cells[map.cellIndices.CellToIndex(cell)] = true;
                    }
                }
            }
        }

        private static int GetInterval(Map map)
        {
            if (windCheckIntervalField == null)
            {
                return 30;
            }

            object component = AtmosphereFieldBridge.FindComponent(map);
            object settings = component == null ? null : AtmosphereFieldBridge.GetSettings(component);
            if (settings == null)
            {
                return 30;
            }

            int interval = Convert.ToInt32(windCheckIntervalField.GetValue(settings));
            return interval < 1 ? 1 : interval;
        }
    }
}
