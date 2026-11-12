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
    /// VTE 的 <c>CompResourceSingleton.UpdateOverlayHandle</c> 只在 spawn/despawn 时被调用，
    /// 而且开头就是 <c>if (ResourceOn || !CanBeOn()) return;</c>。本 mod 让控制器一直保持
    /// <c>ResourceOn = true</c>（它不参与账本开关机），于是这个方法永远不会再刷新图标：
    /// 只要历史上出现过一次 <c>Efficiency == 0</c>（重建室内机、网络瞬时断开的那个模拟步），
    /// 「缺少室外机」的脉动图标就会一直亮着，直到控制器被拆掉重建。
    ///
    /// 这里用反射调 <c>PipeNetOverlayDrawer.TogglePulsing</c>，绕开那个 early-return，
    /// 并且只在状态变化时调用，避免每 30 tick 反复 Enable/Disable 造成闪烁。
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

        /// <summary>控制器被拆掉后清掉缓存，避免字典无限增长。</summary>
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
