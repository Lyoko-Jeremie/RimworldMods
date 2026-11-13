using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// ProxyHeat 替代机制的 Harmony 补丁集合。
    ///
    /// 四个补丁，全部是 Prefix/Postfix，不使用 Transpiler（兼容性优先）：
    /// 1. AO 的 <c>PrepareTemperatureSources</c> → 每模拟周期注入一次温度增量。
    /// 2. ProxyHeat 的 <c>RecalculateAffectedCells</c> → 同步环形基线采样格。
    /// 3. ProxyHeat 的 <c>WriteSettings</c> / <c>ApplySettings</c> → 强制关闭 ProxyHeat 自身的读值层，
    ///    并让改动过的半径/温度参数重建照射区。
    /// 4. <c>ThingWithComps.GetGizmos</c> → 给每个 ProxyHeat 源加一个「室内也生效」开关。
    /// </summary>
    public static class ProxyHeatRelayPatches
    {
        /// <summary>依次挂载全部补丁。返回是否全部成功（缺一两项不算致命）。</summary>
        public static bool Install(Harmony harmony)
        {
            bool ok = true;
            ok &= PatchInjection(harmony);
            ok &= PatchRecalculate(harmony);
            ok &= PatchPostSpawn(harmony);
            ok &= PatchSettingsWrite(harmony);
            ok &= PatchGizmos(harmony);
            return ok;
        }

        // ------------------------------------------------------------ 1. 注入

        private static bool PatchInjection(Harmony harmony)
        {
            Type componentType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
            if (componentType == null)
            {
                Log.Warning("[AOPatch] 找不到 AO 的 AtmosphereMapComponent，范围效果注入停用。");
                return false;
            }
            MethodInfo target = AccessTools.Method(componentType, "PrepareTemperatureSources", Type.EmptyTypes);
            if (target == null)
            {
                Log.Warning("[AOPatch] 找不到 AtmosphereMapComponent.PrepareTemperatureSources，范围效果注入停用。");
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(PrepareTemperatureSources_Postfix)));
            return true;
        }

        /// <summary>诊断用：确认补丁确实命中，且 relay 组件确实存在。</summary>
        private static int _postfixCalls;

        /// <summary>
        /// AO 的 PrepareTemperatureSources 末尾：pending 数组刚被清空并灌入本周期来源，
        /// 模拟 Task 还没启动，正好写增量。
        /// </summary>
        private static void PrepareTemperatureSources_Postfix(MapComponent __instance)
        {
            if (__instance == null)
            {
                return;
            }
            Map map = __instance.map;
            if (map == null)
            {
                return;
            }
            ProxyHeatRelayMapComponent relay = map.GetComponent<ProxyHeatRelayMapComponent>();

            _postfixCalls++;
            AOPatchSettings settings = AOPatchMod.Settings;
            if (_postfixCalls <= 3 && settings != null && settings.ProxyHeatDebugLog)
            {
                Log.Message("[AOPatch/ProxyHeat] PrepareTemperatureSources 命中 #" + _postfixCalls
                            + " relay=" + (relay != null ? "ok" : "null"));
            }

            if (relay != null)
            {
                relay.Inject();
            }
        }

        // ------------------------------------------------------------ 2. 照射区

        private static bool PatchRecalculate(Harmony harmony)
        {
            Type compType = ProxyHeatBridge.CompType;
            if (compType == null)
            {
                return false;
            }
            MethodInfo target = AccessTools.Method(compType, "RecalculateAffectedCells", Type.EmptyTypes);
            if (target == null)
            {
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(RecalculateAffectedCells_Postfix)));
            return true;
        }

        private static void RecalculateAffectedCells_Postfix(object __instance)
        {
            ThingComp comp = __instance as ThingComp;
            if (comp == null || comp.parent == null)
            {
                return;
            }
            Map map = comp.parent.Map;
            if (map == null)
            {
                return;
            }
            ProxyHeatRelayMapComponent relay = map.GetComponent<ProxyHeatRelayMapComponent>();
            if (relay != null)
            {
                relay.RefreshRing(__instance);
            }
        }

        // ------------------------------------------------------------ 2b. 源登记

        /// <summary>
        /// 新建筑（含读档重建）生成时登记源。
        ///
        /// 必须有自己的名单：ProxyHeat 的 compTemperatures 只收 active 的源，
        /// 而 active 由 TempTick 维护；我们关掉 enableProxyHeat 后 TempTick 不跑，
        /// 断电时生成的建筑会永久 active=false，永远进不了 compTemperatures。
        /// </summary>
        private static bool PatchPostSpawn(Harmony harmony)
        {
            Type compType = ProxyHeatBridge.CompType;
            if (compType == null)
            {
                return false;
            }
            MethodInfo target = AccessTools.Method(compType, "PostSpawnSetup", new[] { typeof(bool) });
            if (target == null)
            {
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(PostSpawnSetup_Postfix)));
            return true;
        }

        private static void PostSpawnSetup_Postfix(object __instance)
        {
            ThingComp comp = __instance as ThingComp;
            if (comp == null || comp.parent == null)
            {
                return;
            }
            Map map = comp.parent.Map;
            if (map == null)
            {
                return;
            }
            ProxyHeatRelayMapComponent relay = map.GetComponent<ProxyHeatRelayMapComponent>();
            if (relay != null)
            {
                relay.RegisterSource(__instance);
            }
        }

        // ------------------------------------------------------------ 3. 设置写回

        private static bool PatchSettingsWrite(Harmony harmony)
        {
            Type modType = AccessTools.TypeByName("ProxyHeat.ProxyHeatMod");
            if (modType == null)
            {
                return false;
            }
            MethodInfo write = AccessTools.Method(modType, "WriteSettings", Type.EmptyTypes);
            if (write != null)
            {
                harmony.Patch(write, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(ProxyHeatWriteSettings_Postfix)));
            }
            MethodInfo apply = AccessTools.Method(modType, "ApplySettings", Type.EmptyTypes);
            if (apply != null && apply.IsStatic)
            {
                harmony.Patch(apply, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(ProxyHeatApplySettings_Postfix)));
            }
            return write != null;
        }

        private static void ProxyHeatWriteSettings_Postfix()
        {
            EnforceProxyHeatDisabled();
            MarkAllSourcesDirty();
        }

        private static void ProxyHeatApplySettings_Postfix()
        {
            EnforceProxyHeatDisabled();
            MarkAllSourcesDirty();
        }

        /// <summary>
        /// 玩家在 ProxyHeat 面板里把开关改回 true 时，下一帧就再关掉。
        /// 我们只接管「范围效果」，ProxyHeat 的读值层必须保持关闭，
        /// 否则 18 处读值补丁全部复活。
        /// </summary>
        private static void EnforceProxyHeatDisabled()
        {
            if (!ProxyHeatBridge.Available)
            {
                return;
            }
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.ProxyHeatRelayEnabled)
            {
                // 模块关闭时不再干预：把 ProxyHeat 的开关还给玩家。
                return;
            }
            if (ProxyHeatBridge.GetEnableProxyHeat())
            {
                ProxyHeatBridge.SetEnableProxyHeat(false);
            }
            ProxyHeatBridge.SetEnableIndoors(true);
        }

        /// <summary>
        /// 设置面板改的是 Def 上的 CompProperties，<c>ApplySettings</c> 不会重建照射区。
        /// MarkDirty 后，下一次 <c>MapComponentTick</c> 的 dirty 分支会重算。
        /// </summary>
        private static void MarkAllSourcesDirty()
        {
            if (!ProxyHeatBridge.Available || Find.Maps == null)
            {
                return;
            }
            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                object manager = ProxyHeatBridge.FindManager(maps[m]);
                if (manager == null)
                {
                    continue;
                }
                IList comps = ProxyHeatBridge.GetActiveComps(manager);
                if (comps == null)
                {
                    continue;
                }
                for (int i = 0; i < comps.Count; i++)
                {
                    ProxyHeatBridge.MarkDirty(comps[i]);
                }
            }
        }

        // ------------------------------------------------------------ 4. 逐建筑开关

        private static bool PatchGizmos(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(ThingWithComps), "GetGizmos", Type.EmptyTypes);
            if (target == null)
            {
                Log.Warning("[AOPatch] 找不到 ThingWithComps.GetGizmos，逐建筑室内开关停用。");
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProxyHeatRelayPatches), nameof(GetGizmos_Postfix)));
            return true;
        }

        private static int _gizmoCalls;

        private static void GetGizmos_Postfix(ThingWithComps __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__instance == null || !ProxyHeatBridge.Available)
            {
                return;
            }
            object ignored;
            if (!ProxyHeatBridge.TryGetComp(__instance, out ignored))
            {
                return;
            }

            _gizmoCalls++;
            AOPatchSettings settings = AOPatchMod.Settings;
            if (_gizmoCalls <= 3 && settings != null && settings.ProxyHeatDebugLog)
            {
                Log.Message("[AOPatch/ProxyHeat] GetGizmos 命中 #" + _gizmoCalls
                            + " " + (__instance.def != null ? __instance.def.defName : "?"));
            }

            __result = AppendProxyHeatGizmos(__result, __instance);
        }

        private static IEnumerable<Gizmo> AppendProxyHeatGizmos(IEnumerable<Gizmo> inner, ThingWithComps thing)
        {
            if (inner != null)
            {
                foreach (Gizmo gizmo in inner)
                {
                    yield return gizmo;
                }
            }

            Map map = thing.Map;
            if (map == null)
            {
                yield break;
            }
            ProxyHeatRelayMapComponent relay = map.GetComponent<ProxyHeatRelayMapComponent>();
            if (relay == null)
            {
                yield break;
            }

            yield return new ProxyHeatToggleCommand
            {
                defaultLabel = "AOPatch_ProxyHeatAir".Translate(),
                defaultDesc = "AOPatch_ProxyHeatAirDesc".Translate(),
                icon = TexCommand.GatherSpotActive,
                isActive = () => relay.HasMode(thing, ProxyHeatMode.Air),
                toggleAction = () => relay.ToggleMode(thing, ProxyHeatMode.Air),
            };

            yield return new ProxyHeatToggleCommand
            {
                defaultLabel = "AOPatch_ProxyHeatSoil".Translate(),
                defaultDesc = "AOPatch_ProxyHeatSoilDesc".Translate(),
                icon = TexCommand.DesirePower,
                isActive = () => relay.HasMode(thing, ProxyHeatMode.Soil),
                toggleAction = () => relay.ToggleMode(thing, ProxyHeatMode.Soil),
            };

            yield return new ProxyHeatToggleCommand
            {
                defaultLabel = "AOPatch_ProxyHeatJet".Translate(),
                defaultDesc = "AOPatch_ProxyHeatJetDesc".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => relay.HasMode(thing, ProxyHeatMode.Jet),
                toggleAction = () => relay.ToggleMode(thing, ProxyHeatMode.Jet),
            };

            yield return new ProxyHeatToggleCommand
            {
                defaultLabel = "AOPatch_ProxyHeatIndoors".Translate(),
                defaultDesc = "AOPatch_ProxyHeatIndoorsDesc".Translate(),
                icon = TexCommand.HoldOpen,
                isActive = () => relay.IncludesIndoors(thing),
                toggleAction = () => relay.ToggleIndoors(thing),
            };
        }
    }
}
