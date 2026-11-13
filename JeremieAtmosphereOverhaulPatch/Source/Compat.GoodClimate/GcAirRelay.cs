using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.GoodClimate
{
    /// <summary>
    /// GC 主动通风口 → AO AirMover 的桥接。
    ///
    /// 只做三件事：
    /// 1. 反射解析 GC 的类型与字段（本程序集不静态引用 GC）；
    /// 2. 在 GC 的 60 tick 节拍上扫描 GC 自己的 vents 注册表，按门控注册/注销一条单对端口 AirMover；
    /// 3. 真空时关断通风口并复刻 GC 的提示信。
    ///
    /// 不写 Room.Temperature，不改 CompAirVent.speedLimit，也不新增 MapComponent。
    /// </summary>
    internal static class GcAirRelay
    {
        private const string GcThingCompTypeName = "GOS.GoodClimate.GC_ThingComp";
        private const string GcMapComponentTypeName = "GOS.GoodClimate.GC_MapComponent";
        private const string GcPropsTypeName = "GOS.GoodClimate.GC_CompProperties";
        private const string AirVentTypeName = "AtmosphereOverhaul.Buildings.CompAirVent";

        private const string AirMoverKeyPrefix = "JGC_IE_";

        // 参数按 AO 允许的最小档位（3 tick）取值，其余档位由 LimitedVolumes 截断到 0.5 格/步。
        // 这样玩家在 AO 设置里改模拟间隔时无需重新注册，也不会出现低档位半速。
        private const float AirColumnHeight = 5f;
        private const float CubicMetresPerTick = 0.5f * AirColumnHeight / 3f;
        private const float MaxPressureRisePascals = 1000f;

        internal static readonly Type GcThingCompType;
        internal static readonly Type GcMapComponentType;
        internal static readonly Type GcPropsType;
        internal static readonly Type AirVentType;
        internal static readonly FieldInfo IeActiveField;
        internal static readonly FieldInfo LabelLetterField;
        internal static readonly FieldInfo TextLetterField;
        internal static readonly FieldInfo GcVentsField;
        internal static readonly PropertyInfo AirVentIsOpenProperty;
        internal static readonly bool Ready;

        private static readonly Dictionary<int, Building> Registered = new Dictionary<int, Building>();
        private static readonly Dictionary<int, Rot4> Directions = new Dictionary<int, Rot4>();
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly List<int> Stale = new List<int>();
        private static readonly Dictionary<string, int> Failures = new Dictionary<string, int>();
        private static readonly StringBuilder Report = new StringBuilder();
        private static int scanCount;
        private static int reportsLeft = 30;

        static GcAirRelay()
        {
            try
            {
                GcThingCompType = AccessTools.TypeByName(GcThingCompTypeName);
                GcMapComponentType = AccessTools.TypeByName(GcMapComponentTypeName);
                GcPropsType = AccessTools.TypeByName(GcPropsTypeName);
                AirVentType = AccessTools.TypeByName(AirVentTypeName);

                if (GcThingCompType == null || GcMapComponentType == null || AirVentType == null)
                {
                    return;
                }

                IeActiveField = AccessTools.Field(GcThingCompType, "GoodClimate_IE_Active");
                GcVentsField = AccessTools.Field(GcMapComponentType, "vents");
                if (IeActiveField == null || GcVentsField == null)
                {
                    return;
                }

                if (GcPropsType != null)
                {
                    LabelLetterField = AccessTools.Field(GcPropsType, "GC_IE_labelLetter");
                    TextLetterField = AccessTools.Field(GcPropsType, "GC_IE_textLetter");
                }

                AirVentIsOpenProperty = AccessTools.Property(AirVentType, "IsOpen");
                if (AirVentIsOpenProperty == null)
                {
                    return;
                }

                Ready = true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[AO Patch][GC] 反射初始化失败：" + e, 0x4743);
            }
        }

        /// <summary>
        /// 由 GC 的 60 tick 节拍（GcVentExchangePatch.Postfix）调用。
        /// </summary>
        internal static void Scan(Map map)
        {
            if (!Ready || map == null || !AtmosphereFieldBridge.Available)
            {
                return;
            }

            object component = AtmosphereFieldBridge.FindComponent(map);
            object engine = component == null ? null : AtmosphereFieldBridge.GetEngine(component);
            float[] mask = engine == null ? null : AtmosphereFieldBridge.GetObstacleMask(engine);
            if (mask == null)
            {
                Log.WarningOnce("[AO Patch][GC] 该地图没有 AO 引擎/障碍掩码，跳过主动通风口接管。", 0x4745);
                return;
            }

            object gcMapComp = map.GetComponent(GcMapComponentType);
            IList vents = gcMapComp == null ? null : GcVentsField.GetValue(gcMapComp) as IList;
            if (vents == null)
            {
                Log.WarningOnce("[AO Patch][GC] 取不到 GC_MapComponent.vents，跳过主动通风口接管。", 0x4746);
                return;
            }

            Seen.Clear();
            for (int i = 0; i < vents.Count; i++)
            {
                ThingComp vent = vents[i] as ThingComp;
                if (vent == null)
                {
                    continue;
                }

                Building building = vent.parent as Building;
                if (building == null || building.Destroyed || !building.Spawned || building.Map != map)
                {
                    continue;
                }

                Seen.Add(building.thingIDNumber);
                Consider(map, vent, building, mask);
            }

            // 已拆除、或已从 GC 注册表移除的项
            Stale.Clear();
            foreach (KeyValuePair<int, Building> pair in Registered)
            {
                if (!Seen.Contains(pair.Key))
                {
                    Stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < Stale.Count; i++)
            {
                Building building;
                if (Registered.TryGetValue(Stale[i], out building))
                {
                    Unregister(building);
                }
            }

            ReportScan(vents.Count, map);
        }

        private static MethodInfo _isPortOpenMethod;
        private static MethodInfo _hasReturnPathMethod;
        private static bool _aoDiagResolved;

        private static void ResolveAoDiag(object engine)
        {
            if (_aoDiagResolved)
            {
                return;
            }

            _aoDiagResolved = true;
            Type engineType = engine.GetType();
            _isPortOpenMethod = AccessTools.Method(engineType, "IsAirMoverPortOpen", new[] { typeof(int) });
            _hasReturnPathMethod = AccessTools.Method(engineType, "AirMoverHasReturnPath", new[] { typeof(int), typeof(int) });
        }

        /// <summary>临时诊断：每 10 次扫描（600 tick）输出一次门控统计，最多 30 条。</summary>
        private static void ReportScan(int ventCount, Map map)
        {
            scanCount++;
            if (reportsLeft <= 0 || scanCount % 10 != 0)
            {
                return;
            }

            reportsLeft--;
            Report.Length = 0;
            Report.Append("[AO Patch][GC] scan: vents=").Append(ventCount)
                  .Append(" registered=").Append(Registered.Count);
            foreach (KeyValuePair<string, int> pair in Failures)
            {
                Report.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            }

            AppendAoState(map);
            Log.Message(Report.ToString());
            Failures.Clear();
        }

        /// <summary>诊断 AO 运行时是否真的接受这条 AirMover（端口合法性 + 回流路径）。</summary>
        private static void AppendAoState(Map map)
        {
            if (map == null || Registered.Count == 0)
            {
                return;
            }

            object component = AtmosphereFieldBridge.FindComponent(map);
            object engine = component == null ? null : AtmosphereFieldBridge.GetEngine(component);
            if (engine == null)
            {
                return;
            }

            ResolveAoDiag(engine);
            foreach (KeyValuePair<int, Building> pair in Registered)
            {
                Building building = pair.Value;
                if (building == null || !building.Spawned)
                {
                    continue;
                }

                IntVec3 facing = building.Rotation.FacingCell;
                int inletIndex = map.cellIndices.CellToIndex(building.Position + facing);
                int outletIndex = map.cellIndices.CellToIndex(building.Position - facing);
                bool portOpen = false;
                bool hasPath = false;
                try
                {
                    if (_isPortOpenMethod != null)
                    {
                        portOpen = (bool)_isPortOpenMethod.Invoke(engine, new object[] { inletIndex })
                                   && (bool)_isPortOpenMethod.Invoke(engine, new object[] { outletIndex });
                    }

                    if (_hasReturnPathMethod != null)
                    {
                        hasPath = (bool)_hasReturnPathMethod.Invoke(engine, new object[] { inletIndex, outletIndex });
                    }
                }
                catch (Exception e)
                {
                    Log.WarningOnce("[AO Patch][GC] AO 状态诊断失败：" + e, 0x4747);
                }

                Report.Append(" [").Append(pair.Key).Append(" port=").Append(portOpen)
                      .Append(" path=").Append(hasPath).Append(']');
            }
        }

        private static void Fail(string reason)
        {
            int count;
            Failures.TryGetValue(reason, out count);
            Failures[reason] = count + 1;
        }

        private static void Consider(Map map, ThingComp gcComp, Building building, float[] mask)
        {
            bool active = (bool)IeActiveField.GetValue(gcComp);
            if (!active)
            {
                Fail("inactive");
                Unregister(building);
                return;
            }

            // 电源与总开关：GC 原版语义，任何一项不满足都不允许强力交换。
            CompPowerTrader power = building.GetComp<CompPowerTrader>();
            CompFlickable flick = building.GetComp<CompFlickable>();
            if (power != null && !power.PowerOn)
            {
                Fail("power");
                Unregister(building);
                return;
            }

            if (flick != null && !flick.SwitchIsOn)
            {
                Fail("flick");
                Unregister(building);
                return;
            }

            // 关键门控：AO 可压模式下 AirMoverHasReturnPath 不要求连通，
            // 只有 CompAirVent.IsOpen 能阻止「vent 关着时仍然穿墙搬运」。
            ThingComp airVent = FindCompByType(building, AirVentType);
            if (airVent == null)
            {
                Fail("noairvent");
                Unregister(building);
                return;
            }

            if (!(bool)AirVentIsOpenProperty.GetValue(airVent, null))
            {
                Fail("closed");
                Unregister(building);
                return;
            }

            IntVec3 facing = building.Rotation.FacingCell;
            IntVec3 inlet = building.Position + facing;
            IntVec3 outlet = building.Position - facing;

            Room roomIn = GridsUtility.GetRoom(inlet, map);
            Room roomOut = GridsUtility.GetRoom(outlet, map);

            // 真空：关断并复刻 GC 的负事件信。DoFlick 后 SwitchIsOn=false，
            // 下一轮会在上面的总开关检查处直接返回，因此不会重复发信。
            if ((roomIn != null && roomIn.Vacuum > 0f) || (roomOut != null && roomOut.Vacuum > 0f))
            {
                Fail("vacuum");
                if (flick != null)
                {
                    flick.DoFlick();
                }

                SendVacuumLetter(building, gcComp);
                Unregister(building);
                return;
            }

            if (!PortLooksOpen(map, inlet, mask) || !PortLooksOpen(map, outlet, mask))
            {
                Fail("port");
                Unregister(building);
                return;
            }

            int id = building.thingIDNumber;
            Rot4 rotation = building.Rotation;
            if (Registered.ContainsKey(id))
            {
                if (Directions[id] == rotation)
                {
                    return;
                }

                // 方向变了：先结算旧区间，再用新端点注册。
                Unregister(building);
            }

            if (AtmosphereFieldBridge.RegisterAirMover(map, building, AirMoverKeyPrefix + id, inlet, outlet,
                                                      BuildParams(facing), -1f))
            {
                Registered[id] = building;
                Directions[id] = rotation;
            }
            else
            {
                Fail("register-failed");
            }
        }

        private static AtmosphereFieldBridge.AirMoverParams BuildParams(IntVec3 facing)
        {
            return new AtmosphereFieldBridge.AirMoverParams
            {
                CubicMetresPerTick = CubicMetresPerTick,
                AirColumnHeightMetres = AirColumnHeight,
                InletDirectionX = facing.x,
                InletDirectionY = facing.z,
                OutletDirectionX = -facing.x,
                OutletDirectionY = -facing.z,
                MaxPressureRisePascals = MaxPressureRisePascals,
                ConditionExhaust = false,
            };
        }

        /// <summary>
        /// 按精确类型取组件。ThingWithComps 只有泛型 GetComp，没有 Type 重载。
        /// </summary>
        private static ThingComp FindCompByType(Building building, Type compType)
        {
            List<ThingComp> comps = building.AllComps;
            if (comps == null)
            {
                return null;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp != null && comp.GetType() == compType)
                {
                    return comp;
                }
            }

            return null;
        }

        private static void Unregister(Building building)
        {
            if (building == null)
            {
                return;
            }

            AtmosphereFieldBridge.UnregisterAirMovers(building.Map, building);
            int id = building.thingIDNumber;
            Registered.Remove(id);
            Directions.Remove(id);
        }

        /// <summary>
        /// 端口格必须可通行、非地图边缘。
        /// 与 AO 的 IsAirMoverPortOpen 同义（四邻面开放一项由 AO 内部再查），
        /// 这里用公开的 ObstacleMask 自查，避免反射 internal。
        /// </summary>
        private static bool PortLooksOpen(Map map, IntVec3 cell, float[] mask)
        {
            if (!cell.InBounds(map))
            {
                return false;
            }

            if (cell.x <= 0 || cell.z <= 0 || cell.x >= map.Size.x - 1 || cell.z >= map.Size.z - 1)
            {
                return false;
            }

            int index = map.cellIndices.CellToIndex(cell);
            if (index < 0 || index >= mask.Length)
            {
                return false;
            }

            return mask[index] > 0.001f;
        }

        private static void SendVacuumLetter(Building building, ThingComp comp)
        {
            if (building == null || comp == null || comp.props == null
                || LabelLetterField == null || TextLetterField == null)
            {
                return;
            }

            try
            {
                string label = LabelLetterField.GetValue(comp.props) as string;
                string text = TextLetterField.GetValue(comp.props) as string;
                if (label.NullOrEmpty() || text.NullOrEmpty())
                {
                    return;
                }

                Find.LetterStack.ReceiveLetter(Translator.Translate(label), Translator.Translate(text),
                                               LetterDefOf.NegativeEvent, new LookTargets(building));
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[AO Patch][GC] 真空信件发送失败：" + e, 0x4744);
            }
        }
    }
}
