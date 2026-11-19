using System;
using System.Collections.Generic;
using System.Reflection;
using FullyAutomaticOmniCrafter;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 超维科技穹顶 / 生物圈的 UI 接入：
    /// 1. 两者都追加「大气目标」按钮，打开地图级目标值面板；
    /// 2. 生物圈原有的「控制温度」开关与温度设置按钮在 AO 模式下灰显——
    ///    温度已由 AO 场硬固定，改 props.targetTemperature 不会再影响结果，
    ///    留着可点只会误导玩家。
    /// 3. 穹顶网络缓存变化时通知 OmniDomeZoneCache 重建。
    /// </summary>
    internal static class Patch_OmniAtmosphereGizmos
    {
        public static bool Install(Harmony harmony)
        {
            bool ok = true;

            MethodInfo dirty = AccessTools.Method(typeof(OmniForceFieldDomeNetworkManager), "DirtyCache", Type.EmptyTypes);
            if (dirty != null)
            {
                harmony.Patch(dirty, postfix: new HarmonyMethod(
                    typeof(Patch_OmniAtmosphereGizmos), nameof(DirtyCachePostfix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo domeGizmos = AccessTools.Method(typeof(CompOmniForceFieldDome), "CompGetGizmosExtra");
            if (domeGizmos != null)
            {
                harmony.Patch(domeGizmos, postfix: new HarmonyMethod(
                    typeof(Patch_OmniAtmosphereGizmos), nameof(DomeGizmosPostfix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo biosphereGizmos = AccessTools.Method(typeof(CompBiosphere), "CompGetGizmosExtra");
            if (biosphereGizmos != null)
            {
                harmony.Patch(biosphereGizmos, postfix: new HarmonyMethod(
                    typeof(Patch_OmniAtmosphereGizmos), nameof(BiosphereGizmosPostfix)));
            }
            else
            {
                ok = false;
            }

            return ok;
        }

        private static void DirtyCachePostfix(OmniForceFieldDomeNetworkManager __instance)
        {
            if (__instance != null && __instance.map != null)
            {
                OmniDomeZoneCache.MarkDirty(__instance.map);
            }

            // 护盾开关、尺寸变化会调 DirtyCache；掩膜与实时判定都要立刻反映。
            OmniProtection.MarkAllDirty();
        }

        private static void DomeGizmosPostfix(CompOmniForceFieldDome __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = AppendDome(__result, __instance);
        }

        private static IEnumerable<Gizmo> AppendDome(IEnumerable<Gizmo> source, CompOmniForceFieldDome dome)
        {
            if (source != null)
            {
                foreach (Gizmo gizmo in source)
                {
                    yield return gizmo;
                }
            }

            if (dome == null || dome.parent == null || dome.parent.Map == null)
            {
                yield break;
            }

            Map map = dome.parent.Map;
            if (!HasAtmosphere(map))
            {
                yield break;
            }
            yield return MakeTargetsGizmo(map);
            yield return MakeIsolationGizmo(map);
        }

        private static void BiosphereGizmosPostfix(CompBiosphere __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = AppendBiosphere(__result, __instance);
        }

        private static IEnumerable<Gizmo> AppendBiosphere(IEnumerable<Gizmo> source, CompBiosphere comp)
        {
            string controlDesc = "Biosphere_ControlTemperatureDesc".Translate();
            string setDesc = "Biosphere_SetTemperatureDesc".Translate();
            string lockReason = "AOPatch_Omni_BiosphereTemperatureLocked".Translate();

            if (source != null)
            {
                foreach (Gizmo gizmo in source)
                {
                    Command command = gizmo as Command;
                    if (command != null
                        && (command.defaultDesc == controlDesc || command.defaultDesc == setDesc))
                    {
                        command.Disable(lockReason);
                    }
                    yield return gizmo;
                }
            }

            if (comp == null || comp.parent == null || comp.parent.Map == null)
            {
                yield break;
            }

            Map map = comp.parent.Map;
            if (!HasAtmosphere(map))
            {
                yield break;
            }
            yield return MakeTargetsGizmo(map);
            yield return MakeIsolationGizmo(map);
        }

        /// <summary>AO 未加载或该地图还没有模拟组件时不显示目标面板，避免无意义的 UI。</summary>
        private static bool HasAtmosphere(Map map)
        {
            return map != null && AtmosphereFieldBridge.FindComponent(map) != null;
        }

        private static Gizmo MakeTargetsGizmo(Map map)
        {
            return new Command_Action
            {
                defaultLabel = "AOPatch_Omni_Targets".Translate(),
                defaultDesc = "AOPatch_Omni_TargetsDesc".Translate(),
                icon = BaseContent.WhiteTex,
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_OmniAtmosphereTargets(map));
                }
            };
        }

        /// <summary>
        /// 几何隔离开关。开启时把覆盖格在 AO 掩膜里设为不可通行，切断对外交换；
        /// 关闭后穹顶内仍会被硬固定成目标值，但会与外部平流/扩散交换。
        /// </summary>
        private static Gizmo MakeIsolationGizmo(Map map)
        {
            MapComponent_OmniAtmosphereTargets targets = MapComponent_OmniAtmosphereTargets.Get(map);
            return new Command_Toggle
            {
                defaultLabel = "AOPatch_Omni_Isolation".Translate(),
                defaultDesc = "AOPatch_Omni_IsolationDesc".Translate(),
                icon = BaseContent.WhiteTex,
                isActive = () => targets != null && targets.UseGeometricIsolation,
                toggleAction = delegate
                {
                    if (targets == null)
                    {
                        return;
                    }

                    targets.UseGeometricIsolation = !targets.UseGeometricIsolation;
                    // 掩码重建是脏标记驱动的；切换后立刻标脏，下一次主线程刷新就按新状态重算。
                    AtmosphereFieldBridge.MarkCachedMapDataDirty(map);
                }
            };
        }
    }
}
