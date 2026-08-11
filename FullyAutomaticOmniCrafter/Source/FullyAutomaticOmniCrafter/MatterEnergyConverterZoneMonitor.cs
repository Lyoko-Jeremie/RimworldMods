using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    // ─── Harmony：消除 ZoneMonitor 建筑与存储区之间的 SlotGroup 格子冲突 ─────
    /// <summary>
    /// 背景：ZoneMonitor 继承 Building_Storage，其占用格会被注册进
    /// HaulDestinationManager（格子归属表是「一格一主」的独占模型）。而本 mod 又
    /// 通过 Patch_ZoneMonitorMec_CanOverlapZones 允许存储区与 ZoneMonitor 共存，
    /// 于是两种操作顺序都会触发原版冲突报错：
    ///   - 先建建筑再画存储区：Zone_Stockpile.AddCell → SlotGroup.Notify_AddedCell
    ///     → HaulDestinationManager.SetCellFor 发现格子已属于建筑的 SlotGroup，
    ///     打出 "overwriting slot group square" 错误（即本次用户报告的报错）；
    ///   - 先画存储区再建建筑：建筑 SpawnSetup → AddHaulDestination → SetCellFor
    ///     覆盖存储区格子 → 同样报错并破坏存储区归属；
    ///   - 用收缩工具把建筑格从存储区划掉：ClearCellFor 同样报错；
    ///   - 删除存储区/拆除建筑：RemoveHaulDestination 无条件清空格子归属，
    ///     会误清对方已经登记的格子。
    /// 本组 patch 采用「先到先得」语义：仅当冲突双方恰好是 ZoneMonitor 建筑与
    /// 存储区时静默跳过（不 Log、不覆盖、不清除），其余冲突保持原版行为。
    /// </summary>
    internal static class ZoneMonitorStorageConflictUtility
    {
        // 反射缓存：HaulDestinationManager.map 为私有字段，只解析一次
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(HaulDestinationManager), "map");

        /// <summary>冲突双方是否恰好是 ZoneMonitor 建筑与存储区。</summary>
        public static bool IsZoneMonitorVsStockpile(SlotGroup a, SlotGroup b)
        {
            if (a == null || b == null) return false;
            bool zmInvolved = a.parent is Building_MatterEnergyConverterZoneMonitor
                           || b.parent is Building_MatterEnergyConverterZoneMonitor;
            if (!zmInvolved) return false;
            return a.parent is Zone_Stockpile || b.parent is Zone_Stockpile;
        }

        /// <summary>
        /// 获取 HaulDestinationManager 所属地图（其 map 字段为私有，需反射）。
        /// 字段不存在时记录一次警告并返回 null，恢复逻辑静默降级（核心报错消除不受影响）。
        /// </summary>
        public static Map GetMap(HaulDestinationManager manager)
        {
            if (MapField == null)
            {
                Log.WarningOnce(
                    "FullyAutomaticOmniCrafter: 无法反射 HaulDestinationManager.map 字段，" +
                    "ZoneMonitor 与存储区共存的格子归属恢复将不可用。",
                    "MEC_ZoneMonitor_HdManagerMapField".GetHashCode());
                return null;
            }
            return MapField.GetValue(manager) as Map;
        }
    }

    [HarmonyPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.SetCellFor))]
    public static class Patch_ZoneMonitorMec_StorageConflict_SetCellFor
    {
        [HarmonyPrefix]
        public static bool Prefix(HaulDestinationManager __instance, IntVec3 c, SlotGroup group)
        {
            SlotGroup old = __instance.SlotGroupAt(c);
            if (old == null || old == group) return true;
            // ZoneMonitor 建筑与存储区之间的冲突：静默跳过（先到先得，保留先注册者）
            return !ZoneMonitorStorageConflictUtility.IsZoneMonitorVsStockpile(old, group);
        }
    }

    [HarmonyPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.ClearCellFor))]
    public static class Patch_ZoneMonitorMec_StorageConflict_ClearCellFor
    {
        [HarmonyPrefix]
        public static bool Prefix(HaulDestinationManager __instance, IntVec3 c, SlotGroup group)
        {
            SlotGroup cur = __instance.SlotGroupAt(c);
            if (cur == null || cur == group) return true;
            // 收缩/删除存储区把 ZoneMonitor 建筑格划掉等场景：该格归属仍是建筑的
            // SlotGroup（先到先得），原版会 Log.Error 并误清建筑归属；此处静默不清。
            return !ZoneMonitorStorageConflictUtility.IsZoneMonitorVsStockpile(cur, group);
        }
    }

    [HarmonyPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.RemoveHaulDestination))]
    public static class Patch_ZoneMonitorMec_StorageConflict_RemoveHaulDestination
    {
        [HarmonyPostfix]
        public static void Postfix(HaulDestinationManager __instance, IHaulDestination haulDestination)
        {
            Map map = ZoneMonitorStorageConflictUtility.GetMap(__instance);
            if (map == null) return;

            // 场景 1：删除存储区。原版会无条件清空存储区全部格子的归属，
            // 其中可能包含 ZoneMonitor 建筑已登记的格子，此处恢复建筑归属。
            Zone_Stockpile zone = haulDestination as Zone_Stockpile;
            if (zone != null)
            {
                foreach (IntVec3 c in zone.AllSlotCellsList())
                {
                    if (__instance.SlotGroupAt(c) != null) continue; // 归属仍在，无需恢复
                    foreach (Thing t in map.thingGrid.ThingsListAt(c))
                    {
                        Building_MatterEnergyConverterZoneMonitor zm = t as Building_MatterEnergyConverterZoneMonitor;
                        if (zm != null && zm.Spawned)
                        {
                            SlotGroup sg = zm.GetSlotGroup();
                            if (sg != null) RestoreCell(__instance, map, c, sg);
                            break;
                        }
                    }
                }
                return;
            }

            // 场景 2：拆除 ZoneMonitor 建筑。原版会清空建筑占用格的归属，
            // 若该格实际由脚下存储区登记（先画区后建建筑），此处恢复存储区归属。
            Building_MatterEnergyConverterZoneMonitor zmBuilding = haulDestination as Building_MatterEnergyConverterZoneMonitor;
            if (zmBuilding != null)
            {
                foreach (IntVec3 c in zmBuilding.AllSlotCellsList())
                {
                    if (__instance.SlotGroupAt(c) != null) continue;
                    Zone_Stockpile z = map.zoneManager.ZoneAt(c) as Zone_Stockpile;
                    if (z != null && z.GetSlotGroup() != null)
                        RestoreCell(__instance, map, c, z.GetSlotGroup());
                }
            }
        }

        /// <summary>
        /// 把格子归属直接写回归属表并刷新 haulable/mergeable 缓存。
        /// 不使用 SlotGroup.Notify_AddedCell：那是「新增格子」语义，内部实现可能
        /// 因版本而异（个别版本带 cells.Contains 短路），直接写表保证恢复必生效，
        /// 且此时该格归属必为 null，不会触发本组 SetCellFor 前缀的拦截。
        /// </summary>
        private static void RestoreCell(HaulDestinationManager manager, Map map, IntVec3 c, SlotGroup group)
        {
            manager.SetCellFor(c, group);
            map.listerHaulables.RecalcAllInCell(c);
            map.listerMergeables.RecalcAllInCell(c);
        }
    }
}
