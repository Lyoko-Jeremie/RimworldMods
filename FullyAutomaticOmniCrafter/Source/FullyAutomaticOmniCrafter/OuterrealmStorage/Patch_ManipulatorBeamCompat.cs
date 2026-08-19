using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 牵引光束搬运器（ManipulatorBeam）可选兼容 —— 不编译期引用其 dll。
    /// 所有目标方法经 AccessTools 字符串解析（TypeByName / Method(string)），
    /// 未安装或版本签名变化时返回 null → Harmony 自动跳过本组 patch，零副作用。
    ///
    /// 兼容策略（借出物化 + 取出记账，两条路径）：
    ///  · 普通取货（TryBuildBatchFromCell / TryBuildBatchFromCellAuto）：牵引光束的源扫描
    ///    最终落在 cell.GetThingList（thingGrid），而 vault 伪 Spawned 副本刻意不进 thingGrid
    ///    （见 OuterrealmVaultViewThingOwner.RegisterInLister）。prefix 把该 vault 格上的锚点
    ///    副本经 TryLendCopy 借出物化（真 Spawn 到存储格 → 进 thingGrid → 借出即取出扣全局全量），
    ///    原方法随即按普通地面物品生成 BeamTransfer；未被搬走的借出副本由 vault Tick 的
    ///    ReturnUnreservedBorrowed 自动回收（剩余量存回全局、重建锚点），天然自愈。
    ///  · 施工配送/运输舱加载（TryFindConstructionTransfer 等）：源扫描用 listerThings
    ///    （伪 Spawned 副本已注册其中），会直接把锚点副本放进 BeamTransfer。整堆取出若走原版
    ///    Thing.DeSpawn 会命中 Patch_Thing_DeSpawn_PseudoSpawned 的 Suppress 分支（不扣全局）
    ///    导致复制 —— LiftThingForTransfer prefix 兜底：锚点副本改走 view.Remove
    ///    （不 Suppress → Notify_ItemRemoved → 全局扣全量）或 SplitOff（视图记账）。
    /// </summary>
    internal static class BeamManipulatorCompat
    {
        // ── 反射缓存（只读、惰性；方法缺失时 null，调用方短路）——避免高频反射 ──
        private static readonly Type BeamManipulatorUtilityType =
            AccessTools.TypeByName("ManipulatorBeam.BeamManipulatorUtility");
        private static readonly MethodInfo TryFindBestStorageCellMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "TryFindBestStorageCellIgnoringReachability");
        private static readonly Type BeamTransferType =
            AccessTools.TypeByName("ManipulatorBeam.BeamTransfer");
        internal static readonly FieldInfo BeamTransferThingField =
            BeamTransferType == null ? null : AccessTools.Field(BeamTransferType, "thing");
        internal static readonly FieldInfo BeamTransferCountField =
            BeamTransferType == null ? null : AccessTools.Field(BeamTransferType, "count");

        /// <summary>该格是否为某 vault 的存储格；是则返回 vault，否则 null（O(1) slotGroup 查询）。</summary>
        public static Building_OuterrealmVault VaultAtCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid)
            {
                return null;
            }
            return cell.GetSlotGroup(map)?.parent as Building_OuterrealmVault;
        }

        /// <summary>借出该 vault 格上的全部锚点副本（真 Spawn 到存储格 → 进入 thingGrid 可见）。</summary>
        public static void LendAnchorCopiesAtCell(Building_OuterrealmVault vault, IntVec3 cell)
        {
            if (vault == null || vault.view == null || !vault.Spawned)
            {
                return;
            }
            List<Thing> copies = vault.view.InnerListForReading;
            for (int i = 0; i < copies.Count; i++)
            {
                Thing copy = copies[i];
                // 伪 Spawned 锚点：Spawned 且仍由视图持有、投影在当前格（借出副本/未 Spawned 均排除）
                if (copy == null || copy.Destroyed || !copy.Spawned ||
                    copy.holdingOwner != vault.view || copy.Position != cell)
                {
                    continue;
                }
                vault.view.TryLendCopy(copy); // 借出即取出：扣全局全量 + 真 Spawn 到存储格
            }
        }

        /// <summary>反射调用牵引光束私有静态 TryFindBestStorageCellIgnoringReachability：
        /// 复刻其"候选判定 + 存储目标搜索"（CanReserve / claim / cooldown / filter / 优先级），
        /// 保证与牵引光束自身语义一致。签名全为原版类型。</summary>
        public static bool TryFindStorageCellFor(
            Pawn pawn, Thing copy, HashSet<IntVec3> excludedDestinations, int ownerKey, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            if (TryFindBestStorageCellMethod == null || pawn == null || copy == null)
            {
                return false;
            }
            object[] args = { pawn, copy, excludedDestinations, ownerKey, IntVec3.Invalid };
            try
            {
                bool ok = (bool)TryFindBestStorageCellMethod.Invoke(null, args);
                destination = (IntVec3)args[4];
                return ok;
            }
            catch (Exception)
            {
                return false; // 版本差异等：静默降级
            }
        }
    }

    /// <summary>手动型普通取货：取货前借出该 vault 格上的锚点副本（prefix，纯原版类型参数）。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_TryBuildBatchFromCell
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method("ManipulatorBeam.BeamManipulatorUtility:TryBuildBatchFromCell");

        static bool Prefix(Pawn pawn, IntVec3 cell)
        {
            Map map = pawn?.Map;
            if (map == null)
            {
                return true;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault != null)
            {
                BeamManipulatorCompat.LendAnchorCopiesAtCell(vault, cell);
            }
            return true; // 继续原方法：借出副本已进 thingGrid，会被 cell.GetThingList 发现
        }
    }

    /// <summary>自动型普通取货：同上（building 参数用 object 接收，避免编译期引用其类型）。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_TryBuildBatchFromCellAuto
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method("ManipulatorBeam.BeamManipulatorUtility:TryBuildBatchFromCellAuto");

        static bool Prefix(object building, IntVec3 cell)
        {
            Thing bt = building as Thing; // Building_BeamManipulatorAuto 是 Thing 子类，Object→Thing 转换无需其类型
            Map map = bt?.Map;
            if (map == null)
            {
                return true;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault != null)
            {
                BeamManipulatorCompat.LendAnchorCopiesAtCell(vault, cell);
            }
            return true;
        }
    }

    /// <summary>手动型 WorkGiver 派活判定放行（HasAnyHaulWork → HasAnyStorageTransferFromCell）。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_HasAnyStorageTransferFromCell
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method("ManipulatorBeam.BeamManipulatorUtility:HasAnyStorageTransferFromCell");

        static void Postfix(Pawn pawn, IntVec3 cell, int ownerKey, ref bool __result)
        {
            if (__result || pawn?.Map == null)
            {
                return;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, pawn.Map);
            if (vault == null || vault.view == null)
            {
                return;
            }
            List<Thing> copies = vault.view.InnerListForReading;
            for (int i = 0; i < copies.Count; i++)
            {
                Thing copy = copies[i];
                if (copy == null || copy.Destroyed || !copy.Spawned ||
                    copy.holdingOwner != vault.view || copy.Position != cell)
                {
                    continue;
                }
                if (BeamManipulatorCompat.TryFindStorageCellFor(pawn, copy, null, ownerKey, out IntVec3 _))
                {
                    __result = true;
                    return;
                }
            }
        }
    }

    /// <summary>取出记账兜底（防复制）：transfer.thing 为 vault 锚点副本时改走视图记账。
    /// 借出副本（holdingOwner=null）与普通物品不受影响，直接走原逻辑。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_LiftThingForTransfer
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method("ManipulatorBeam.BeamManipulatorUtility:LiftThingForTransfer");

        static bool Prefix(object transfer, ref Thing __result)
        {
            if (transfer == null || BeamManipulatorCompat.BeamTransferThingField == null)
            {
                return true;
            }
            Thing thing = BeamManipulatorCompat.BeamTransferThingField.GetValue(transfer) as Thing;
            if (thing == null || thing.Destroyed || !thing.Spawned)
            {
                return true; // 原逻辑（返回 null）
            }
            if (!(thing.holdingOwner is OuterrealmVaultViewThingOwner view))
            {
                return true; // 非 vault 锚点：走原逻辑（含借出副本与普通物品）
            }
            int count = BeamManipulatorCompat.BeamTransferCountField != null
                ? (int)BeamManipulatorCompat.BeamTransferCountField.GetValue(transfer) : 0;
            if (count <= 0)
            {
                count = thing.stackCount;
            }
            count = Mathf.Min(count, thing.stackCount);
            if (count >= thing.stackCount)
            {
                // 整堆取出：走视图 Remove（不 Suppress → Notify_ItemRemoved → 全局扣全量；
                // Remove 内部已摘 lister 并恢复未 Spawned），实例交给光束，FinishTransfer 直接放置
                view.Remove(thing);
                __result = thing;
                return false;
            }
            // 部分取出：SplitOff 走视图记账（PreSplitOff/PostSplitOff）
            Thing result = thing.SplitOff(count);
            if (result.Spawned)
            {
                result.DeSpawn(DestroyMode.Vanish);
            }
            __result = result;
            return false;
        }
    }
}
