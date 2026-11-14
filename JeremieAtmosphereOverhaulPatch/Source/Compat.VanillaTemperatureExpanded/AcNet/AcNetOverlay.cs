using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PipeSystem;
using RimWorld;
using VanillaTemperatureExpanded.Buildings;
using VanillaTemperatureExpanded.Comps;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet
{
    /// <summary>
    /// 中控器的错误图标刷新。
    ///
    /// 为什么必须由账本来写：
    /// VTE 的 <c>CompResourceSingleton.UpdateOverlayHandle</c> 只有两个调用时机——
    /// <c>AcPipeNet.PipeSystemTick</c>（被本 mod 短路掉）和
    /// <c>AcPipeNet.RegisterComp</c> / <c>UnregisterComp</c>（管网合并、拆分时）。
    /// 后者会按「当时」的 <c>AcPipeNet.Efficiency</c> 直接 <c>TogglePulsing</c>：
    /// 新建空调自成一网时账本把这个空网写成 0，随后玩家用管道把它接到主网上，
    /// 一旦是「新网吞并主网」，中控器就被注册进这个 Efficiency 还是 0 的网，
    /// 图标当场被点亮。
    ///
    /// 注意这里没有 <c>ResourceOn</c> 早退（有早退的是 <c>CompResourceTrader_AC</c> 和
    /// <c>CompResourceTrader_Compressor</c>），所以「控制器常开」并不能挡住它。
    ///
    /// 光靠本类每步写还不够：下面的 <c>_lastState</c> 只在「我们上次写过的值」变化时才写。
    /// VTE 绕过缓存点亮图标后，账本下一步算出的 <c>false</c> 会被当成「没变化」丢弃，
    /// 图标就永久脉动。因此 <c>AcNetPatches</c> 给 RegisterComp/UnregisterComp 打了 Postfix：
    /// 只要有控制器被搬动就 <see cref="Forget"/> 掉它的缓存，逼账本下一步无条件重写一次。
    ///
    /// 材质是 UnityEngine.Material，本程序集不直接引用 CoreModule，所以整条调用链都走反射。
    /// </summary>
    internal static class AcNetOverlay
    {
        private static readonly FieldInfo _drawerField =
            AccessTools.Field(typeof(CompResourceSingleton), "pipeNetOverlayDrawer");

        private static readonly FieldInfo _missingMatField =
            AccessTools.Field(typeof(CompResourceSingleton), "missingCompressorsMat");

        private static readonly FieldInfo _tooManyMatField =
            AccessTools.Field(typeof(CompResourceSingleton), "tooManyMat");

        private static readonly MethodInfo _togglePulsing =
            AccessTools.Method(typeof(PipeNetOverlayDrawer), "TogglePulsing");

        /// <summary>thingIDNumber → 上次显示的 missing 状态，只在变化时写 overlay。</summary>
        private static readonly Dictionary<int, bool> _lastState = new Dictionary<int, bool>();

        public static void Refresh(Building_AcControlUnit controller, bool missingCompressors)
        {
            if (controller == null || !controller.Spawned || _togglePulsing == null)
            {
                return;
            }

            CompResourceSingleton comp = controller.resourceComp;
            if (comp == null)
            {
                return;
            }

            int id = controller.thingIDNumber;
            bool last;
            if (_lastState.TryGetValue(id, out last) && last == missingCompressors)
            {
                return;
            }
            _lastState[id] = missingCompressors;

            if (_drawerField == null)
            {
                return;
            }

            object drawer = _drawerField.GetValue(comp);
            if (drawer == null)
            {
                return;
            }

            object missingMat = _missingMatField != null ? _missingMatField.GetValue(null) : null;
            if (missingMat != null)
            {
                Toggle(drawer, controller, missingMat, missingCompressors);
            }

            // 本 mod 允许多个控制器（用户已确认不区分），所以「控制器过多」图标始终关掉。
            object tooManyMat = _tooManyMatField != null ? _tooManyMatField.GetValue(null) : null;
            if (tooManyMat != null)
            {
                Toggle(drawer, controller, tooManyMat, false);
            }
        }

        private static void Toggle(object drawer, Thing thing, object material, bool on)
        {
            _togglePulsing.Invoke(drawer, new object[] { thing, material, on });
        }

        /// <summary>
        /// 丢掉某个中控器的图标缓存，让它下次进 Refresh 时无条件重写一次 overlay。
        ///
        /// 调用点（都在 AcNetPatches）：
        /// 1. <c>AcPipeNet.RegisterComp</c> 的 Postfix（<c>ControllerRegisteredPostfix</c>）：
        ///    VTE 在这个钩子里绕开本类直接写 overlay，缓存必须作废，否则纠正写会被 early-return 吞掉；
        /// 2. <c>AcPipeNet.UnregisterComp</c> 的 Postfix（<c>ControllerUnregisteredPostfix</c>）：
        ///    拆网/合并时同样作废，只清缓存、不同步温度（那时是半拆状态）；
        /// 3. <c>CompResourceSingleton.PostDeSpawn</c> 的 Postfix：控制器被拆除后清账，
        ///    避免字典随拆建无限增长。
        /// </summary>
        public static void Forget(int thingIdNumber)
        {
            _lastState.Remove(thingIdNumber);
        }

        /// <summary>安装时把反射解析结果打进日志，避免运行期静默失效。</summary>
        public static string Describe()
        {
            if (_togglePulsing != null && _drawerField != null
                && _missingMatField != null && _tooManyMatField != null)
            {
                return "图标刷新就绪";
            }

            return "图标刷新反射缺失（drawer=" + (_drawerField != null)
                   + " missingMat=" + (_missingMatField != null)
                   + " tooManyMat=" + (_tooManyMatField != null)
                   + " toggle=" + (_togglePulsing != null) + "）";
        }
    }
}
