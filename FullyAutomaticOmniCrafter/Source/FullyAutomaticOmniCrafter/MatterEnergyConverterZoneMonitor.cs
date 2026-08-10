using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ─── 存储区监控型物资能量转化装置（1x1） ─────────────────────────────────
    /// <summary>
    /// 特殊形态的物资能量转化装置：
    ///   - 1x1 大小，继承 Building_MatterEnergyConverter，保留全部原有能力
    ///     （专属无限电池 CompMatterEnergyConverterBattery、方式 A/B/C 三种手动转化、
    ///      1 格存储位、Flickable 开关）。
    ///   - 允许建造在存储区（Zone_Stockpile）范围内
    ///     （需配合 Patch_ZoneMonitorMec_CanOverlapZones，见本文件下方）。
    ///   - 自动绑定建筑脚下所在的存储区：自动转化开关（Gizmo）开启且装置处于
    ///     存储区内时，持续自动将存储区中的物品转化为电能（复用 RecycleThing 逻辑）。
    ///   - 不在存储区内时自动转化暂停，但手动转化模式仍然可用。
    ///   - 注意：自动转化由独立 Gizmo 开关控制，不受 Flickable 物理开关影响；
    ///     Flickable 仍保留原功能（控制专属电池对外放电）。
    /// </summary>
    public class Building_MatterEnergyConverterZoneMonitor : Building_MatterEnergyConverter
    {
        private const int AutoScanIntervalTicks = 60; // 每 60 tick（约 1 秒）扫描一次
        private const int AutoConvertPerScan = 40;    // 单次扫描最多转化件数，避免单帧销毁过多物品导致卡顿

        private int nextAutoScanTick;
        private int totalConvertedCount;
        private float totalConvertedEnergy;

        /// <summary>自动转化开关（由 Gizmo 按钮控制，独立于 Flickable 物理开关）。</summary>
        private bool autoConvertEnabled = true;

        /// <summary>
        /// 当前监控的存储区：建筑所在格所属的 Zone_Stockpile。
        /// 若建筑不在任何存储区内，返回 null（自动转化暂停）。
        /// </summary>
        public Zone_Stockpile MonitoredZone()
        {
            if (!Spawned || Map == null) return null;
            return Map.zoneManager.ZoneAt(Position) as Zone_Stockpile;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autoConvertEnabled, "autoConvertEnabled", true, true);
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned) return;

            // 节流：仅在到达扫描间隔时执行，避免每 tick 遍历存储区
            if (GenTicks.TicksGame < nextAutoScanTick) return;
            nextAutoScanTick = GenTicks.TicksGame + AutoScanIntervalTicks;

            // 开关关闭（Gizmo 控制）或装置不在存储区内时暂停自动转化
            if (!autoConvertEnabled) return;
            Zone_Stockpile zone = MonitoredZone();
            if (zone == null) return;
            SlotGroup slotGroup = zone.GetSlotGroup();
            if (slotGroup == null) return;

            int converted = 0;
            // 先快照再遍历：转化（销毁）过程中会修改 thingGrid，避免在惰性枚举期间变更集合
            foreach (Thing t in slotGroup.HeldThings.ToList())
            {
                if (converted >= AutoConvertPerScan) break;
                if (t == null || t.Destroyed || !t.Spawned) continue;
                // 防御：绝不自动转化活体（HeldThings 已过滤 EverStorable，此处双保险）
                if (t is Pawn) continue;
                // 尊重玩家标记：被禁止（Forbidden）的物品不自动转化
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                // 正在被殖民者取用/搬运（被 Reserve）的物品跳过，避免中断任务
                if (t.Map.reservationManager.IsReservedByAnyoneOf(t, Faction.OfPlayer)) continue;

                float energy = RecycleThing(t);
                totalConvertedCount++;
                totalConvertedEnergy += energy;
                converted++;
            }
        }

        // ── Gizmo：自动转化开关 ────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Toggle
            {
                defaultLabel = "MEC_ZM_AutoConvert_Toggle".Translate(),
                defaultDesc = "MEC_ZM_AutoConvert_Toggle_Desc".Translate(),
                icon = MatterEnergyConverterTex.IconStorage,
                isActive = () => autoConvertEnabled,
                toggleAction = () => autoConvertEnabled = !autoConvertEnabled
            };
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();

            Zone_Stockpile zone = MonitoredZone();
            if (zone != null && autoConvertEnabled)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "MEC_ZM_StatusMonitoring".Translate(zone.label);
            }
            else if (zone != null)
            {
                // 在存储区内但自动转化开关关闭
                if (!s.NullOrEmpty()) s += "\n";
                s += "MEC_ZM_StatusDisabled".Translate();
            }
            else
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "MEC_ZM_StatusNotInZone".Translate();
            }

            if (totalConvertedCount > 0)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "MEC_ZM_Converted".Translate(totalConvertedCount, totalConvertedEnergy.ToString("N1"));
            }

            return s;
        }
    }

    // ─── Harmony：允许目标装置建造在存储区范围内 ─────────────────────────────
    /// <summary>
    /// 原版 ThingDef.CanOverlapZones 对满足以下任一条件的建筑恒返回 false：
    ///   - surfaceType >= SurfaceType.Item
    ///   - thingClass 实现 ISlotGroupParent（Building_Storage 派生类）
    /// 物资能量转化仪两者都命中，若不处理，建筑生成时会触发
    /// ZoneManager.Notify_NoZoneOverlapThingSpawned，把脚下的存储区格子顶掉，
    /// 「建造在存储区内」无法实现。
    /// 此处对目标装置 def 前缀短路返回 true；同时匹配其蓝图/框架 def
    /// （entityDefToBuild 指向目标 def），保证建造全流程（蓝图→框架→完成）
    /// 都不会移除脚下的存储区格子。
    /// </summary>
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.CanOverlapZones), MethodType.Getter)]
    public static class Patch_ZoneMonitorMec_CanOverlapZones
    {
        public const string ZoneMonitorDefName = "MatterEnergyConverter_ZoneMonitor";

        [HarmonyPrefix]
        public static bool Prefix(ThingDef __instance, ref bool __result)
        {
            if (string.Equals(__instance.defName, ZoneMonitorDefName, StringComparison.Ordinal) ||
                __instance.entityDefToBuild is ThingDef buildDef &&
                string.Equals(buildDef.defName, ZoneMonitorDefName, StringComparison.Ordinal))
            {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
