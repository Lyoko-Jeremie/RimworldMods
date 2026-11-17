using System;
using System.Collections.Generic;
using System.Reflection;
using FullyAutomaticOmniCrafter;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 条目 2：护盾覆盖范围内的建筑/物品/屋顶不受风害。
    ///
    /// AO 的风害分四条独立通道，每条各有自己的成员数组与判定循环，
    /// 没有统一的「按格豁免」入口，所以分别在四个点按护盾覆盖格做 O(1) 剔除：
    ///
    /// 1. WindStructuralLoad：ComputeChainLoads 之后清零护盾内成员的 demand/pressureLoad/pendingDamage，
    ///    后续 load 计算自然为 0，也不会累积伤害；
    /// 2. WindPassableLoad：Evaluate 之后清掉护盾内成员的 PendingAction，Apply 阶段不会再执行；
    /// 3. WindRoofLoad：ApplyRemovals 之前把护盾内屋顶从 pending 列表里剔除，风抬升与压力压塌都不会发生；
    /// 4. WindTransport：TryLaunch 直接拒绝护盾内物品，ThrowCollectedPawns 前把护盾内 pawn 移出待抛列表；
    ///    另 patch Pawn_StanceTracker.StaggerFor，护盾内 pawn 不再踉跄。
    ///
    /// 这些 tick 全部在 AtmosphereMapComponent.MapComponentTick 的主线程里执行，
    /// 因此可以安全地访问 MapComponent、Thing 与地图格坐标。
    /// </summary>
    internal static class Patch_OmniWindExemption
    {
        private static Type structuralType;
        private static Type roofType;
        private static Type transportType;
        private static Type memberSetType;

        private static FieldInfo structCount;
        private static FieldInfo structDemand;
        private static FieldInfo structPressure;
        private static FieldInfo structPendingDamage;
        private static FieldInfo structBuilding;

        private static FieldInfo memberCount;
        private static FieldInfo memberThings;
        private static FieldInfo memberPendingAction;
        private static FieldInfo memberLastLoad;
        private static FieldInfo memberLastRisk;

        private static FieldInfo roofPendingCount;
        private static FieldInfo roofPending;
        private static FieldInfo roofCell;

        private static FieldInfo transportTmpThrow;

        internal static bool Install(Harmony harmony)
        {
            structuralType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindStructuralLoad");
            roofType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindRoofLoad");
            transportType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindTransport");
            memberSetType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindPassableLoad+MemberSet");

            if (structuralType == null || roofType == null || transportType == null || memberSetType == null)
            {
                Log.Warning("[AOPatch] 护盾风豁免：AO 风害类型缺失，补丁未挂载。");
                return false;
            }

            structCount = AccessTools.Field(structuralType, "count");
            structDemand = AccessTools.Field(structuralType, "demand");
            structPressure = AccessTools.Field(structuralType, "pressureLoad");
            structPendingDamage = AccessTools.Field(structuralType, "pendingDamage");
            structBuilding = AccessTools.Field(structuralType, "building");

            memberCount = AccessTools.Field(memberSetType, "Count");
            memberThings = AccessTools.Field(memberSetType, "Things");
            memberPendingAction = AccessTools.Field(memberSetType, "PendingAction");
            memberLastLoad = AccessTools.Field(memberSetType, "LastLoad");
            memberLastRisk = AccessTools.Field(memberSetType, "LastRiskPerHour");

            roofPendingCount = AccessTools.Field(roofType, "pendingCount");
            roofPending = AccessTools.Field(roofType, "pending");
            roofCell = AccessTools.Field(roofType, "cellIndex");

            transportTmpThrow = AccessTools.Field(transportType, "TmpThrow");

            if (structCount == null || structDemand == null || structPressure == null
                || structPendingDamage == null || structBuilding == null
                || memberCount == null || memberThings == null || memberPendingAction == null
                || memberLastLoad == null || memberLastRisk == null
                || roofPendingCount == null || roofPending == null || roofCell == null
                || transportTmpThrow == null)
            {
                Log.Warning("[AOPatch] 护盾风豁免：AO 内部字段缺失，补丁未挂载。");
                return false;
            }

            bool ok = true;

            MethodInfo computeChainLoads = AccessTools.Method(structuralType, "ComputeChainLoads");
            if (computeChainLoads != null)
            {
                harmony.Patch(computeChainLoads, postfix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(StructuralPostfix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo evaluate = AccessTools.Method(AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindPassableLoad"), "Evaluate");
            if (evaluate != null)
            {
                harmony.Patch(evaluate, postfix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(PassablePostfix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo applyRemovals = AccessTools.Method(roofType, "ApplyRemovals");
            if (applyRemovals != null)
            {
                harmony.Patch(applyRemovals, prefix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(RoofPrefix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo tryLaunch = AccessTools.Method(transportType, "TryLaunch");
            if (tryLaunch != null)
            {
                harmony.Patch(tryLaunch, prefix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(TryLaunchPrefix)));
            }
            else
            {
                ok = false;
            }

            MethodInfo throwCollected = AccessTools.Method(transportType, "ThrowCollectedPawns");
            if (throwCollected != null)
            {
                harmony.Patch(throwCollected, prefix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(ThrowCollectedPrefix)));
            }
            else
            {
                ok = false;
            }

            // Pawn_StanceTracker.StaggerFor 被 AO 直接调用来施加踉跄。
            MethodInfo staggerFor = AccessTools.Method(typeof(Pawn_StanceTracker), "StaggerFor", new[] { typeof(int) });
            if (staggerFor != null)
            {
                harmony.Patch(staggerFor, prefix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(StaggerForPrefix)));
            }
            else
            {
                ok = false;
            }

            // 护盾开关/尺寸变化走 OmniInterceptorTracker.DirtyCache，掩膜需要立刻置脏。
            MethodInfo trackerDirty = AccessTools.Method(typeof(OmniInterceptorTracker), "DirtyCache", Type.EmptyTypes);
            if (trackerDirty != null)
            {
                harmony.Patch(trackerDirty, postfix: new HarmonyMethod(
                    typeof(Patch_OmniWindExemption), nameof(TrackerDirtyPostfix)));
            }

            return ok;
        }

        /// <summary>结构载荷：清零护盾内成员的风压需求与待结算伤害。</summary>
        private static void StructuralPostfix(object __instance)
        {
            int count = (int)structCount.GetValue(__instance);
            if (count <= 0)
            {
                return;
            }

            Building[] buildings = (Building[])structBuilding.GetValue(__instance);
            float[] demand = (float[])structDemand.GetValue(__instance);
            float[] pressure = (float[])structPressure.GetValue(__instance);
            float[] pending = (float[])structPendingDamage.GetValue(__instance);
            if (buildings == null || demand == null || pressure == null || pending == null)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Building building = buildings[i];
                if (building == null || building.Destroyed)
                {
                    continue;
                }

                if (OmniProtection.IsShieldProtected(building.PositionHeld, building.MapHeld))
                {
                    demand[i] = 0f;
                    pressure[i] = 0f;
                    pending[i] = 0f;
                }
            }
        }

        /// <summary>可穿透物/植物：清掉护盾内成员的待执行动作，Apply 阶段就不会再吹走或打伤。</summary>
        private static void PassablePostfix(object __0)
        {
            if (__0 == null)
            {
                return;
            }

            int count = (int)memberCount.GetValue(__0);
            if (count <= 0)
            {
                return;
            }

            Thing[] things = (Thing[])memberThings.GetValue(__0);
            byte[] action = (byte[])memberPendingAction.GetValue(__0);
            float[] load = (float[])memberLastLoad.GetValue(__0);
            float[] risk = (float[])memberLastRisk.GetValue(__0);
            if (things == null || action == null)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Thing thing = things[i];
                if (thing == null || !thing.Spawned)
                {
                    continue;
                }

                if (OmniProtection.IsShieldProtected(thing.PositionHeld, thing.MapHeld))
                {
                    action[i] = 0;
                    if (load != null)
                    {
                        load[i] = 0f;
                    }

                    if (risk != null)
                    {
                        risk[i] = 0f;
                    }
                }
            }
        }

        /// <summary>屋顶：从待移除列表里剔除护盾内的格；全被剔除时直接跳过原方法。</summary>
        /// <remarks>
        /// ApplyRemovals 是实例方法，签名为 (Map map, LocalAtmosphereSettings s, int tick)，
        /// 因此 map 是 0 号参数（__0），不是 __1——写成 __1 会被 Harmony 当成
        /// 「LocalAtmosphereSettings 传给 Map」而在安装期抛参数绑定异常。
        /// </remarks>
        private static bool RoofPrefix(object __instance, Map __0)
        {
            int count = (int)roofPendingCount.GetValue(__instance);
            if (count <= 0)
            {
                return true;
            }

            int[] pending = (int[])roofPending.GetValue(__instance);
            int[] cells = (int[])roofCell.GetValue(__instance);
            if (pending == null || cells == null)
            {
                return true;
            }

            Map map = __0;
            if (map == null)
            {
                return true;
            }

            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                int member = pending[i];
                if (member < 0 || member >= cells.Length)
                {
                    pending[kept++] = member;
                    continue;
                }

                IntVec3 cell = map.cellIndices.IndexToCell(cells[member]);
                if (!OmniProtection.IsShieldProtected(cell, map))
                {
                    pending[kept++] = member;
                }
            }

            roofPendingCount.SetValue(__instance, kept);
            return kept > 0;
        }

        /// <summary>抛飞：护盾内的物品不允许被风带走。</summary>
        private static bool TryLaunchPrefix(Thing __3, ref bool __result)
        {
            Thing thing = __3;
            if (thing == null)
            {
                return true;
            }

            Map map = thing.MapHeld;
            if (map != null && OmniProtection.IsShieldProtected(thing.PositionHeld, map))
            {
                __result = false;
                return false;
            }

            return true;
        }

        /// <summary>抛飞：护盾内或随身温控启用的 pawn 从待抛列表移除。</summary>
        private static void ThrowCollectedPrefix()
        {
            List<Pawn> list = transportTmpThrow == null ? null : (List<Pawn>)transportTmpThrow.GetValue(null);
            if (list == null || list.Count == 0)
            {
                return;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (OmniProtection.IsPawnWindProtected(list[i]))
                {
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>踉跄：护盾内或随身温控启用的 pawn 不再被风吹得站不稳。</summary>
        private static bool StaggerForPrefix(Pawn_StanceTracker __instance)
        {
            return __instance == null || !OmniProtection.IsPawnWindProtected(__instance.pawn);
        }

        /// <summary>护盾状态变化后立即让护盾掩膜失效，不等下一个检查间隔。</summary>
        private static void TrackerDirtyPostfix()
        {
            OmniProtection.MarkAllDirty();
        }
    }
}
