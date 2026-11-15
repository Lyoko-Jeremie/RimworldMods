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
    /// GC 主动通风口 → AO 定向喷流的桥接。
    ///
    /// 语义：
    /// 1. 强力模式开启时，从入口格抽气、从出口格定向喷出（喷流走 ApplyJetTransportUnsafe）；
    /// 2. 同时强制关闭该通风口的自然通风（CompAirVent.speedLimit = 0），避免 vent 自然通道反向短路；
    /// 3. 「自然通风」状态就是 CompAirVent.speedLimit：0 = 关，>0 = 开。
    ///    玩家可以用 AO 的速度滑条调任意速度，也可以用按钮一步切到 0 / 最大值；
    ///    两者共用同一个值，按钮的灯由滑条的值决定；
    /// 4. 强力模式运行中强制 speedLimit = 0（滑条锁死），失败时按钮也会禁用；
    /// 5. 通风口被拆除时把该格及轴向两侧邻格的残留流速抵消为 0。
    ///
    /// 本程序集不静态引用 GC / AO，全部通过反射。
    /// </summary>
    internal static class GcAirRelay
    {
        private const string GcThingCompTypeName = "GOS.GoodClimate.GC_ThingComp";
        private const string GcMapComponentTypeName = "GOS.GoodClimate.GC_MapComponent";
        private const string GcPropsTypeName = "GOS.GoodClimate.GC_CompProperties";
        private const string AirVentTypeName = "AtmosphereOverhaul.Buildings.CompAirVent";
        private const string AirVentPropsTypeName = "AtmosphereOverhaul.Buildings.CompProperties_AirVent";

        private const string AirMoverKeyPrefix = "JGC_IE_";
        private const string NaturalVentGizmoLabelKey = "JGC_NaturalVent_Label";
        private const string NaturalVentGizmoDescKey = "JGC_NaturalVent_Desc";
        private const string NaturalVentGizmoDisabledKey = "JGC_NaturalVent_Disabled";

        /// <summary>
        /// 自然通风开启时的速度上限。AO 给原版 Vent 配的 CompProperties_AirVent
        /// 是 maxSpeedLimit = 20（见 AO 1.7/Patches/Vent_AirFlow.xml），
        /// 运行时优先从 props 读，读不到时退回这个值。
        /// </summary>
        internal const float NaturalVentOpenSpeedLimit = 20f;

        // 喷流参数：Volumes = min(1000, C * elapsed / 5)；默认 6 tick 一步 → 12 格体积/步。
        // ExhaustSpeed 同时决定 substeps：30 时约 72 个子步，风速体感 = 30 * (1-exp(-V))，
        // V≈0.5 时约 12 m/s；体积混合仍由 CubicMetresPerTick 单独控制，不会因提速而加剧温度振荡。
        private const float JetCubicMetresPerTick = 10f;
        private const float JetColumnHeight = 5f;
        private const float JetExhaustSpeed = 30f;
        private const float JetExhaustPressureRatio = 0.02f;
        private const float JetInletPressureRatio = 0f;
        private const float JetMaxPressureRisePascals = 1000f;

        // 端口源 / 温度闭环参数。
        // 每模拟周期（默认 6 tick）把出口温度朝入口拉 15% 温差，单周期上限 2℃，
        // 避免与喷流自身的整格混合叠加后过冲。
        private const float TemperatureRelayRate = 0.15f;
        private const float MaxTemperatureDeltaPerCycle = 2f;
        private const float OxygenRelayRate = 0.1f;

        internal static readonly Type GcThingCompType;
        internal static readonly Type GcMapComponentType;
        internal static readonly Type GcPropsType;
        internal static readonly Type AirVentType;
        internal static readonly FieldInfo IeActiveField;
        internal static readonly FieldInfo LabelLetterField;
        internal static readonly FieldInfo TextLetterField;
        internal static readonly FieldInfo GcVentsField;
        internal static readonly FieldInfo SpeedLimitField;
        internal static readonly PropertyInfo SpeedLimitProperty;
        internal static readonly FieldInfo MaxSpeedLimitField;
        internal static readonly bool Ready;

        private static readonly Dictionary<int, Building> Registered = new Dictionary<int, Building>();
        private static readonly Dictionary<int, Rot4> Directions = new Dictionary<int, Rot4>();
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly List<int> Stale = new List<int>();
        private static readonly Dictionary<string, int> Failures = new Dictionary<string, int>();

        /// <summary>每个通风口两条连续源的句柄：[0]=inlet、[1]=outlet（装箱的 AtmosphereSourceHandle）。</summary>
        private static readonly Dictionary<int, object[]> SourceHandles = new Dictionary<int, object[]>();

        /// <summary>上一步闭环期望的出口温度，用于计算「实际变化」。</summary>
        private static readonly Dictionary<int, float> LastOutletTemperature = new Dictionary<int, float>();
        private static readonly StringBuilder Report = new StringBuilder();
        private static int scanCount;
        private static int reportsLeft = 30;
        private static int diagLeft = 8;
        private static int gizmoDiagLeft = 4;
        private static int toggleDiagLeft = 20;
        private static int speedDiagLeft = 20;

        // 本帧由「自然通风」按钮接管了速度写入：AO 的速度滑条在同一个 Unity 帧的
        // Repaint 阶段会把 targetValuePct（点击前读到的旧值）写回 speedLimit，
        // 必须丢弃那一次写入，否则点击立刻被撤销。
        // 只在主线程（GUI + SetSpeedLimit）读写，不需要锁。
        private static int buttonWriteFrame = -1;
        private static int buttonWriteThingId = -1;

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

                SpeedLimitField = AccessTools.Field(AirVentType, "speedLimit");
                SpeedLimitProperty = AccessTools.Property(AirVentType, "SpeedLimit");
                MaxSpeedLimitField = AccessTools.Field(AccessTools.TypeByName(AirVentPropsTypeName), "maxSpeedLimit");
                if (SpeedLimitField == null || SpeedLimitProperty == null)
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
        /// 由 GC 的 60 tick 节拍（GcVentExchangePatch.Prefix）调用。
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

            float[] temperatures = AtmosphereFieldBridge.GetTemperatureRead(engine);

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

                // 默认关闭自然通风（新放置、旧档未初始化）。
                // 扫描由 GC 的 60 tick 节拍驱动，因此最迟 3 秒内生效。
                EnsureDefaultClosed(building);

                Consider(map, vent, building, mask, temperatures);
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

        /// <summary>诊断 AO 运行时是否真的接受这条喷流（端口合法性 + 回流路径）。</summary>
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

        private static void Consider(Map map, ThingComp gcComp, Building building, float[] mask, float[] temperatures)
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

            ThingComp airVent = FindCompByType(building, AirVentType);
            if (airVent == null)
            {
                Fail("noairvent");
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

            // 强力模式期间强制关闭自然通风：既消除 vent 自然通道的逆向短路，
            // 也让「喷流」成为该格唯一的空气通道。
            EnsureNaturalVentOff(airVent);

            if (!PortLooksOpen(map, inlet, mask) || !PortLooksOpen(map, outlet, mask))
            {
                Fail("port");
                Unregister(building);
                return;
            }

            float inletTemperature = ReadCellTemperature(map, inlet, temperatures);
            float outletTemperature = ReadCellTemperature(map, outlet, temperatures);
            float inletOxygen = ReadCellOxygen(map, inlet);
            float outletOxygen = ReadCellOxygen(map, outlet);
            float cubicMetresPerTick = ComputeCubicMetresPerTick(inletTemperature, outletTemperature);

            int id = building.thingIDNumber;
            Rot4 rotation = building.Rotation;
            if (Registered.ContainsKey(id) && Directions[id] == rotation)
            {
                // 方向未变：AO 内部按 owner+key 去重，参数不变时是空操作。
                if (AtmosphereFieldBridge.RegisterAirMover(map, building, AirMoverKeyPrefix + id, inlet, outlet,
                                                          BuildParams(facing, outletTemperature, outletOxygen, cubicMetresPerTick), -1f))
                {
                    EnsureSources(map, building, inlet, outlet, inletOxygen, outletOxygen);
                    return;
                }

                Fail("refresh-failed");
                Registered.Remove(id);
                Directions.Remove(id);
                return;
            }

            if (Registered.ContainsKey(id))
            {
                // 方向变了：先结算旧区间，再用新端点注册。
                Unregister(building);
            }

            if (AtmosphereFieldBridge.RegisterAirMover(map, building, AirMoverKeyPrefix + id, inlet, outlet,
                                                      BuildParams(facing, outletTemperature, outletOxygen, cubicMetresPerTick), -1f))
            {
                Registered[id] = building;
                Directions[id] = rotation;
                EnsureSources(map, building, inlet, outlet, inletOxygen, outletOxygen);

                if (diagLeft > 0)
                {
                    diagLeft--;
                    Log.Message("[AO Patch][GC] 端口 id=" + id
                        + " pos=(" + building.Position.x + "," + building.Position.z + ")"
                        + " facing=(" + facing.x + "," + facing.z + ")"
                        + " inlet=(" + inlet.x + "," + inlet.z + ")"
                        + " outlet=(" + outlet.x + "," + outlet.z + ")");
                }
            }
            else
            {
                Fail("register-failed");
            }
        }

        /// <summary>
        /// 强力模式期间强制关闭自然通风。直接写 speedLimit 字段并通知 AO 重建缓存；
        /// 不用 AO 的 SetSpeedLimit，因为它内部的 `num == SpeedLimit` 守卫会把
        /// 「字段 -1（属性回退 defaultSpeedLimit）」误判成「已经是目标值」而跳过写入。
        /// </summary>
        private static void EnsureNaturalVentOff(ThingComp airVent)
        {
            if (!IsNaturalVentOpen(airVent))
            {
                return;
            }

            ApplySpeedLimit(airVent, 0f);
        }

        /// <summary>新放置/旧档未初始化的通风口默认关闭自然通风。</summary>
        internal static void EnsureDefaultClosed(Building building)
        {
            if (building == null || AirVentType == null || SpeedLimitField == null)
            {
                return;
            }

            ThingComp airVent = FindCompByType(building, AirVentType);
            if (airVent == null)
            {
                return;
            }

            float raw = (float)SpeedLimitField.GetValue(airVent);
            if (raw < 0f)
            {
                ApplySpeedLimit(airVent, 0f);
            }
        }

        internal static bool IsNaturalVentOpen(ThingComp airVent)
        {
            if (airVent == null || SpeedLimitProperty == null)
            {
                return false;
            }

            try
            {
                return (float)SpeedLimitProperty.GetValue(airVent, null) > 0f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 强弱力模式是否激活。参数是父建筑——GC 的状态字段在 GC_ThingComp 上，
        /// 不能把 CompAirVent 传进来（会抛 Object does not match target type）。
        /// </summary>
        internal static bool IsIntensiveActive(Thing parent)
        {
            if (parent == null || IeActiveField == null)
            {
                return false;
            }

            ThingComp gcComp = FindCompByType(parent as Building, GcThingCompType);
            if (gcComp == null)
            {
                return false;
            }

            try
            {
                object value = IeActiveField.GetValue(gcComp);
                return value is bool && (bool)value;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 自然通风开关：关闭 → speedLimit = 0；开启 → speedLimit = maxSpeedLimit（Vent 为 20）。
        /// 它和 AO 的速度滑条写同一个字段，滑动滑条也会改变按钮的亮灭（按钮只读该字段）。
        /// </summary>
        internal static void ToggleNaturalVent(ThingComp airVent)
        {
            if (airVent == null || airVent.parent == null || IsIntensiveActive(airVent.parent))
            {
                return;
            }

            bool open = IsNaturalVentOpen(airVent);
            float target = open ? 0f : NaturalVentOpenSpeed(airVent);

            // 先占住本帧：AO 滑条在同一个 Unity 帧的 Repaint 里会把旧值写回来。
            MarkButtonWrite(airVent.parent);
            ApplySpeedLimit(airVent, target);

            if (toggleDiagLeft > 0)
            {
                toggleDiagLeft--;
                Log.Message("[AO Patch][GC] toggle id=" + airVent.parent.thingIDNumber
                    + " open=" + open + " target=" + target
                    + " raw=" + RawSpeed(airVent) + " attr=" + CurrentSpeed(airVent));
            }
        }

        private static float CurrentSpeed(ThingComp airVent)
        {
            if (airVent == null || SpeedLimitProperty == null)
            {
                return 0f;
            }

            try
            {
                return (float)SpeedLimitProperty.GetValue(airVent, null);
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>
        /// 直接写 CompAirVent.speedLimit 字段，再让 AO 重建缓存。
        ///
        /// 不走 AO 的 SetSpeedLimit：它有两处会吞掉写入——
        /// 1. `num == SpeedLimit` 用「含默认回退」的属性做比较，字段 -1 时属性返回
        ///    defaultSpeedLimit(10)，此时写 10 会直接 return，字段仍是 -1；
        /// 2. 写完后不通知时，obstacleMask/faceOpen/VentCells 仍是旧值。
        /// 这里写字段 + 显式 MarkCachedMapDataDirty，行为确定。
        /// </summary>
        private static void ApplySpeedLimit(ThingComp airVent, float value)
        {
            if (airVent == null || SpeedLimitField == null)
            {
                return;
            }

            float target = Mathf.Clamp(value, 0f, NaturalVentOpenSpeed(airVent));
            try
            {
                // 已经是目标值就不写，避免每 tick 重建 AO 缓存。
                if (Mathf.Abs((float)SpeedLimitField.GetValue(airVent) - target) < 0.001f)
                {
                    return;
                }

                SpeedLimitField.SetValue(airVent, target);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[AO Patch][GC] 写 speedLimit 失败：" + e, 0x4748);
                return;
            }

            Thing parent = airVent.parent;
            Map map = parent == null ? null : parent.Map;
            if (map != null)
            {
                // AO 的 speedLimit 变化不会自己触发重建（只有 SetSpeedLimit/MarkDirty 会），
                // 直接写字段必须补上这一步，否则 vent 的通断要等到下一次建拆事件才生效。
                AtmosphereFieldBridge.MarkCachedMapDataDirty(map);
            }

            if (speedDiagLeft > 0)
            {
                speedDiagLeft--;
                int diagId = parent != null ? parent.thingIDNumber : -1;
                Log.Message("[AO Patch][GC] speedLimit id=" + diagId + " -> " + target
                    + " (attr " + CurrentSpeed(airVent) + ")");
            }
        }

        /// <summary>该建筑是否挂了 GC 的换热组件（只有这类通风口才归本补丁管）。</summary>
        internal static bool HasGcComp(Building building)
        {
            return building != null && GcThingCompType != null && FindCompByType(building, GcThingCompType) != null;
        }

        /// <summary>记录「本帧的速度值由按钮决定」，让滑条的同帧写回失效。</summary>
        internal static void MarkButtonWrite(Thing thing)
        {
            if (thing == null)
            {
                return;
            }

            buttonWriteFrame = Time.frameCount;
            buttonWriteThingId = thing.thingIDNumber;
        }

        /// <summary>这一帧是否要丢弃该通风口滑条的写回。</summary>
        internal static bool IsSliderWriteSuppressed(Thing thing)
        {
            return thing != null
                && thing.thingIDNumber == buttonWriteThingId
                && Time.frameCount == buttonWriteFrame;
        }

        /// <summary>原始字段值（-1 表示尚未初始化）。</summary>
        private static float RawSpeed(ThingComp airVent)
        {
            if (airVent == null || SpeedLimitField == null)
            {
                return -1f;
            }

            try
            {
                return (float)SpeedLimitField.GetValue(airVent);
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        /// <summary>该通风口的自然通风最大值：优先读 CompProperties_AirVent.maxSpeedLimit。</summary>
        private static float NaturalVentOpenSpeed(ThingComp airVent)
        {
            if (MaxSpeedLimitField != null && airVent != null && airVent.props != null)
            {
                try
                {
                    float max = (float)MaxSpeedLimitField.GetValue(airVent.props);
                    if (max > 0f)
                    {
                        return max;
                    }
                }
                catch (Exception)
                {
                    // 读不到就走下面的常量。
                }
            }

            return NaturalVentOpenSpeedLimit;
        }

        /// <summary>
        /// 拆除通风口时清掉该格残留的流速。
        ///
        /// AO 的 ClampVentVelocities 会把 vent 格 + 轴向两侧邻格的速度钳到 ±SpeedLimit，
        /// 但通风口一旦被拆除，vents 表重建后这三格不再被钳制，残留速度会一直留在速度场里。
        /// 这里读三格当前风，再用 AddWindImpulse 排入相反的增量，在下一模拟步抵消为 0。
        /// </summary>
        internal static void ClearVentVelocity(Building building)
        {
            if (building == null || AirVentType == null || !AtmosphereFieldBridge.WindAvailable)
            {
                return;
            }

            Map map = building.Map;
            if (map == null || !building.Spawned)
            {
                return;
            }

            // 只有挂了 AO 通风组件的建筑才需要处理。
            if (building.def == null || !building.def.HasComp(AirVentType)
                || FindCompByType(building, AirVentType) == null)
            {
                return;
            }

            IntVec3 pos = building.Position;
            IntVec3 facing = building.Rotation.FacingCell;
            ClearWindAt(map, pos);
            if (facing.x != 0)
            {
                // 通风口沿 X 轴通风：ClampVentVelocities 影响左右两格。
                ClearWindAt(map, pos + IntVec3.West);
                ClearWindAt(map, pos + IntVec3.East);
            }
            else
            {
                ClearWindAt(map, pos + IntVec3.South);
                ClearWindAt(map, pos + IntVec3.North);
            }

            if (toggleDiagLeft > 0)
            {
                toggleDiagLeft--;
                Log.Message("[AO Patch][GC] despawn-clear id=" + building.thingIDNumber
                    + " pos=(" + pos.x + "," + pos.z + ")");
            }
        }

        private static void ClearWindAt(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map))
            {
                return;
            }

            Vector2 wind;
            if (!AtmosphereFieldBridge.TryGetWind(map, cell, out wind))
            {
                return;
            }

            if (Mathf.Abs(wind.x) < 0.0001f && Mathf.Abs(wind.y) < 0.0001f)
            {
                return;
            }

            AtmosphereFieldBridge.AddWindImpulse(map, cell, new Vector2(-wind.x, -wind.y));
        }

        /// <summary>自然通风开关按钮，由 CompGetGizmosExtra 的 Postfix 追加。</summary>
        internal static Gizmo BuildNaturalVentGizmo(ThingComp airVent)
        {
            if (gizmoDiagLeft > 0)
            {
                gizmoDiagLeft--;
                int diagId = airVent != null && airVent.parent != null ? airVent.parent.thingIDNumber : -1;
                Log.Message("[AO Patch][GC] gizmo 追加 id=" + diagId
                    + " intensive=" + IsIntensiveActive(airVent != null ? airVent.parent : null)
                    + " open=" + IsNaturalVentOpen(airVent));
            }

            Command_Toggle command = new Command_Toggle();
            command.defaultLabel = NaturalVentGizmoLabelKey.Translate();
            command.defaultDesc = NaturalVentGizmoDescKey.Translate();
            command.icon = BaseContent.WhiteTex;
            command.isActive = () => IsNaturalVentOpen(airVent);
            command.toggleAction = () => ToggleNaturalVent(airVent);

            if (IsIntensiveActive(airVent.parent))
            {
                command.Disabled = true;
                command.disabledReason = NaturalVentGizmoDisabledKey.Translate();
            }

            return command;
        }

        private static float ReadCellTemperature(Map map, IntVec3 cell, float[] temperatures)
        {
            if (temperatures != null)
            {
                int index = map.cellIndices.CellToIndex(cell);
                if (index >= 0 && index < temperatures.Length)
                {
                    return temperatures[index];
                }
            }

            return map.mapTemperature == null ? 15f : map.mapTemperature.OutdoorTemp;
        }

        private static float ReadCellOxygen(Map map, IntVec3 cell)
        {
            float oxygen = 21f;
            AoBridge.TryGetOxygenPercent(map, cell, out oxygen);
            return oxygen;
        }

        /// <summary>
        /// 按温差限流：每模拟步让出口格向入口温度混合的累计比例不超过 α_max。
        ///
        /// 喷流在 JetTransport.Advance 里每子步做 1-exp(-V) 混合，累计为 1-exp(-V_total)。
        /// 若两个相邻房间各装一台反向喷流且 α≈1，会把对方出口格整格替换成自己入口的温度，
        /// 形成 2 周期交替振荡。这里取 α_max = min(0.5, 2/ΔT)，不依赖房间格数
        /// （通风口一侧可能就是室外大气，没有「房间体积」可言）。
        /// </summary>
        private static float ComputeCubicMetresPerTick(float inletTemperature, float outletTemperature)
        {
            float delta = Mathf.Abs(inletTemperature - outletTemperature);
            float alphaMax = Mathf.Min(0.6f, 2f / Mathf.Max(1f, delta));
            float volumes = -Mathf.Log(1f - alphaMax);
            return Mathf.Clamp(volumes * JetColumnHeight / 6f, 0.5f, JetCubicMetresPerTick);
        }

        private static AtmosphereFieldBridge.AirMoverParams BuildParams(IntVec3 facing, float outletTemperature,
                                                                       float outletOxygen, float cubicMetresPerTick)
        {
            return new AtmosphereFieldBridge.AirMoverParams
            {
                CubicMetresPerTick = cubicMetresPerTick,
                AirColumnHeightMetres = JetColumnHeight,
                // 方向约定与 AO 的 CompPairedJet 一致：InletDirection 是「空气流入端口」的方向，
                // OutletDirection 是「空气离开端口」的方向。inlet 在 Position+facing，
                // 所以吸入方向是 -facing；outlet 在 Position-facing，喷出方向也是 -facing。
                InletDirectionX = -facing.x,
                InletDirectionY = -facing.z,
                OutletDirectionX = -facing.x,
                OutletDirectionY = -facing.z,
                MaxPressureRisePascals = JetMaxPressureRisePascals,
                ConditionExhaust = true,
                ExhaustSpeed = JetExhaustSpeed,
                // 温度下限设成「出口当前温度」，让喷流尽量等温搬体积；
                // 真正的温度搬运交给端口连续源的闭环（RelayTemperature），
                // 这样既避开 AO 喷流 max(室外, 入口邻格) 的硬下限，也不会与喷流整格混合叠加过冲。
                // 量化到 1℃、1% 是为了避免 Settings 每周期变化触发 UpdateAirMover 重置计时。
                ExhaustTemperatureFloor = Quantize(outletTemperature, 1f),
                ExhaustPressureRatio = JetExhaustPressureRatio,
                InletPressureRatio = JetInletPressureRatio,
                // 氧同样交给源闭环，喷流用出口浓度保持中性。
                ExhaustOxygenPercent = Quantize(outletOxygen, 1f),
                WaterKgPerTick = 0f,
            };
        }

        /// <summary>
        /// 维护端口连续源：每侧一条，只负责氧（温度走 RelayTemperature 的成对注入）。
        /// AO 的 RegisterContinuousSource 按 owner+key 复用，但这里保存句柄并用
        /// UpdateContinuousSource 更新，避免重复注册改变 sourceId。
        /// </summary>
        private static void EnsureSources(Map map, Building building, IntVec3 inlet, IntVec3 outlet,
                                          float inletOxygen, float outletOxygen)
        {
            if (!AtmosphereFieldBridge.SourceAvailable)
            {
                return;
            }

            int id = building.thingIDNumber;
            object[] handles;
            if (!SourceHandles.TryGetValue(id, out handles) || handles == null || handles.Length != 2)
            {
                handles = new object[2];
                SourceHandles[id] = handles;
            }

            float oxygenPerTick = OxygenRelayRate * (inletOxygen - outletOxygen) / 6f;
            AtmosphereFieldBridge.SourceRates inletRates = default(AtmosphereFieldBridge.SourceRates);
            inletRates.OxygenPercentPerTick = -oxygenPerTick;
            AtmosphereFieldBridge.SourceRates outletRates = default(AtmosphereFieldBridge.SourceRates);
            outletRates.OxygenPercentPerTick = oxygenPerTick;

            if (handles[0] == null)
            {
                handles[0] = AtmosphereFieldBridge.RegisterContinuousSource(map, building,
                    AirMoverKeyPrefix + "SIN_" + id, inlet, inletRates);
            }
            else
            {
                AtmosphereFieldBridge.UpdateContinuousSource(map, building, handles[0], inlet, inletRates);
            }

            if (handles[1] == null)
            {
                handles[1] = AtmosphereFieldBridge.RegisterContinuousSource(map, building,
                    AirMoverKeyPrefix + "SOUT_" + id, outlet, outletRates);
            }
            else
            {
                AtmosphereFieldBridge.UpdateContinuousSource(map, building, handles[1], outlet, outletRates);
            }
        }

        /// <summary>
        /// 每模拟周期执行一次（GcTemperatureRelayPatch.Postfix）。
        /// 成对写温度：outlet +delta、inlet -delta，守恒且没有室外下限。
        /// 用「实际变化 vs 期望变化」的差值做闭环，抵消喷流自身的整格混合。
        /// </summary>
        internal static void RelayTemperature(Map map)
        {
            if (map == null || Registered.Count == 0 || !AtmosphereFieldBridge.SourceAvailable)
            {
                return;
            }

            foreach (KeyValuePair<int, Building> pair in Registered)
            {
                int id = pair.Key;
                Building building = pair.Value;
                if (building == null || !building.Spawned || building.Destroyed || building.Map != map)
                {
                    continue;
                }

                IntVec3 facing = building.Rotation.FacingCell;
                IntVec3 inlet = building.Position + facing;
                IntVec3 outlet = building.Position - facing;

                float inletTemperature;
                float outletTemperature;
                if (!AoBridge.TryGetTemperature(map, inlet, out inletTemperature)
                    || !AoBridge.TryGetTemperature(map, outlet, out outletTemperature))
                {
                    continue;
                }

                float last;
                if (!LastOutletTemperature.TryGetValue(id, out last))
                {
                    last = outletTemperature;
                }

                float target = TemperatureRelayRate * (inletTemperature - outletTemperature);
                float applied = outletTemperature - last;
                float delta = Mathf.Clamp(target - applied, -MaxTemperatureDeltaPerCycle, MaxTemperatureDeltaPerCycle);

                LastOutletTemperature[id] = outletTemperature + delta;
                if (Mathf.Abs(delta) < 0.01f)
                {
                    continue;
                }

                AtmosphereFieldBridge.AddTemperatureDelta(map, outlet, delta);
                AtmosphereFieldBridge.AddTemperatureDelta(map, inlet, -delta);
            }
        }

        private static float Quantize(float value, float step)
        {
            return Mathf.Round(value / step) * step;
        }

        /// <summary>
        /// 按精确类型取组件。ThingWithComps 只有泛型 GetComp，没有 Type 重载。
        /// </summary>
        private static ThingComp FindCompByType(Building building, Type compType)
        {
            if (building == null || compType == null)
            {
                return null;
            }

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

        /// <summary>取建筑上的 AO 通风组件（用于 Gizmo 按钮）。</summary>
        internal static ThingComp FindAirVent(Thing thing)
        {
            Building building = thing as Building;
            return building == null ? null : FindCompByType(building, AirVentType);
        }

        private static void Unregister(Building building)
        {
            if (building == null)
            {
                return;
            }

            AtmosphereFieldBridge.UnregisterAirMovers(building.Map, building);
            AtmosphereFieldBridge.UnregisterContinuousSources(building.Map, building);
            int id = building.thingIDNumber;
            Registered.Remove(id);
            Directions.Remove(id);
            SourceHandles.Remove(id);
            LastOutletTemperature.Remove(id);
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
