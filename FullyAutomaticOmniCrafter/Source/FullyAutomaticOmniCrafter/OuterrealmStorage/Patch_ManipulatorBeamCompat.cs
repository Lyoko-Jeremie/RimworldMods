using System;
using System.Collections;
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
    /// 所有目标方法经 AccessTools 字符串解析（TypeByName / Method(string)）。
    /// 未安装或版本签名变化时方法为 null：各 patch 类通过 Prepare() 返回 false
    /// 让 Harmony 跳过整组 patch（注意 TargetMethod() 返回 null 会抛异常，不能依赖它跳过）。
    ///
    /// 兼容策略（种子 + 注入 + 取出记账，三条路径）：
    ///  · 普通取货（TryBuildBatchFromCell / TryBuildBatchFromCellAuto）：牵引光束的源扫描
    ///    最终落在 cell.GetThingList（thingGrid），而 vault 伪 Spawned 副本刻意不进 thingGrid
    ///    （见 OuterrealmVaultViewThingOwner.RegisterInLister）。prefix 只借出 1 个"判定通过"的
    ///    种子副本（TryLendCopy 按需物化到存储格 → 进 thingGrid → 借出即取出：SplitOff 按物化量扣账），
    ///    保证原方法 batch 非 null；postfix 再把其余判定通过的锚点副本反射注入 batch.transfers
    ///    （不借出、保持伪 Spawned）——"先确定要搬（transfer 生成）、后取出"，物品在确定之前
    ///    不离开 vault、不显示，彻底避免"全部物品显示在格子上"的瞬间卡顿。
    ///  · 施工配送/运输舱加载（TryFindConstructionTransfer 等）：源扫描用 listerThings
    ///    （伪 Spawned 副本已注册其中），会直接把锚点副本放进 BeamTransfer。整堆取出若走原版
    ///    Thing.DeSpawn 会命中 Patch_Thing_DeSpawn_PseudoSpawned 的 Suppress 分支（不扣全局）
    ///    导致复制 —— LiftThingForTransfer prefix 兜底：锚点副本改走 view.Remove
    ///    （不 Suppress → Notify_ItemRemoved → 全局扣全量）或 SplitOff（视图记账）。
    ///    注入的副本 transfer 与施工配送路径同经 LiftThingForTransfer，共用此记账。
    ///  · 目的地门控（vault 作为目的地时的 noDeposit 语义）：牵引光束 TryFindBestStorageCellCore
    ///    只查 filter 与阵营、不查 IHaulDestination.HaulDestinationEnabled（原版 StoreUtility 有查），
    ///    否则关闭"允许存入"后 vault 格仍会被当作搬入目的地、物品落格后被吸收进全局层。
    ///    Patch_Beam_IsBeamStorageGroupAllowed 在组级/格级判定汇合点排除 noDeposit 的 vault 组，
    ///    使 Core 继续遍历下一优先级存储组（与原版跳过禁用存储一致）。
    /// </summary>
    internal static class BeamManipulatorCompat
    {
        // ── 反射缓存（只读、惰性；方法缺失时 null，调用方短路）——避免高频反射 ──
        private static readonly Type BeamManipulatorUtilityType =
            AccessTools.TypeByName("ManipulatorBeam.BeamManipulatorUtility");
        private static readonly MethodInfo TryFindBestStorageCellMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "TryFindBestStorageCellIgnoringReachability");
        private static readonly MethodInfo TryFindBestStorageCellAutoMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "TryFindBestStorageCellIgnoringReachabilityAuto");
        // ── 各 patch 的目标方法（缓存 + 供 Prepare() 判空；未安装/签名变化时为 null）──
        internal static readonly MethodInfo TryBuildBatchFromCellMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "TryBuildBatchFromCell");
        internal static readonly MethodInfo TryBuildBatchFromCellAutoMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "TryBuildBatchFromCellAuto");
        internal static readonly MethodInfo HasAnyStorageTransferFromCellMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "HasAnyStorageTransferFromCell");
        internal static readonly MethodInfo LiftThingForTransferMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "LiftThingForTransfer");
        internal static readonly MethodInfo IsBeamStorageGroupAllowedMethod =
            BeamManipulatorUtilityType == null ? null :
            AccessTools.Method(BeamManipulatorUtilityType, "IsBeamStorageGroupAllowed");
        private static readonly Type BeamTransferType =
            AccessTools.TypeByName("ManipulatorBeam.BeamTransfer");
        internal static readonly FieldInfo BeamTransferThingField =
            BeamTransferType == null ? null : AccessTools.Field(BeamTransferType, "thing");
        internal static readonly FieldInfo BeamTransferCountField =
            BeamTransferType == null ? null : AccessTools.Field(BeamTransferType, "count");
        private static readonly Type BeamHaulBatchType =
            AccessTools.TypeByName("ManipulatorBeam.BeamHaulBatch");
        private static readonly FieldInfo BeamHaulBatchTransfersField =
            BeamHaulBatchType == null ? null : AccessTools.Field(BeamHaulBatchType, "transfers");
        private static readonly ConstructorInfo BeamTransferCtor =
            BeamTransferType == null ? null :
            AccessTools.Constructor(BeamTransferType, new[] { typeof(Thing), typeof(IntVec3), typeof(IntVec3) });

        /// <summary>该格是否为某 vault 的存储格；是则返回 vault，否则 null（O(1) slotGroup 查询）。</summary>
        public static Building_OuterrealmVault VaultAtCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid)
            {
                return null;
            }
            return cell.GetSlotGroup(map)?.parent as Building_OuterrealmVault;
        }

        /// <summary>借出该 vault 格上第一个"需要搬运"的锚点副本（种子）：真 Spawn 到存储格 →
        /// 进入 thingGrid → 保证原方法 TryBuildBatchFromCell 返回 true（batch 非 null）。
        /// 种子在借出前用牵引光束自身的存储目标搜索判定可搬（存在更高优先级存储可去），
        /// 因此种子必然会被原方法生成 transfer；其余副本不借出（见 InjectTransferableCopies），
        /// 避免"全部物品显示在格子上"的瞬间卡顿。pawn 与 autoBuilding 二选一（手动/自动判定器）。</summary>
        public static void LendFirstTransferableCopy(
            Building_OuterrealmVault vault, IntVec3 cell,
            Pawn pawn, object autoBuilding, HashSet<IntVec3> excludedDestinations, int ownerKey)
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
                IntVec3 _;
                bool transferable = pawn != null
                    ? TryFindStorageCellFor(pawn, copy, excludedDestinations, ownerKey, out _)
                    : TryFindStorageCellForAuto(autoBuilding, copy, excludedDestinations, ownerKey, out _);
                if (!transferable)
                {
                    continue;
                }
                vault.view.TryLendCopy(copy, copy.stackCount); // 借出整堆（beam 搬整个堆）：按需物化 + SplitOff 记账
                return;
            }
        }

        /// <summary>把该 vault 格上其余"需要搬运"的锚点副本反射注入 batch.transfers（不借出、不真 Spawn）：
        /// 副本保持伪 Spawned 留在视图，仅在取货时（LiftThingForTransfer → view.Remove）才离开 vault——
        /// 即"先确定要搬（transfer 生成）→ 后取出"，物品在确定之前完全不显示、不离开全局账目。
        /// 注入上限 maxInject = beam 单轮通道数 4（含原方法已生成的 transfer 数）。</summary>
        public static void InjectTransferableCopies(
            object batch, Building_OuterrealmVault vault, IntVec3 cell, int maxInject,
            Pawn pawn, object autoBuilding, HashSet<IntVec3> excludedDestinations, int ownerKey)
        {
            if (batch == null || vault == null || vault.view == null || maxInject <= 0 ||
                BeamHaulBatchTransfersField == null || BeamTransferCtor == null)
            {
                return;
            }
            IList transfers = BeamHaulBatchTransfersField.GetValue(batch) as IList; // List<BeamTransfer>
            if (transfers == null || transfers.Count >= maxInject)
            {
                return;
            }
            List<Thing> copies = vault.view.InnerListForReading;
            for (int i = 0; i < copies.Count && transfers.Count < maxInject; i++)
            {
                Thing copy = copies[i];
                // 伪 Spawned 锚点：Spawned 且仍由视图持有、投影在当前格（借出副本/未 Spawned 均排除）
                if (copy == null || copy.Destroyed || !copy.Spawned ||
                    copy.holdingOwner != vault.view || copy.Position != cell)
                {
                    continue;
                }
                // 判定"该副本确实能被搬到某更高优先级存储"（与牵引光束自身语义一致）；
                // 判定不过的不注入（否则 LiftThingForTransfer 取出时无目的地可放）
                IntVec3 dest;
                bool transferable = pawn != null
                    ? TryFindStorageCellFor(pawn, copy, excludedDestinations, ownerKey, out dest)
                    : TryFindStorageCellForAuto(autoBuilding, copy, excludedDestinations, ownerKey, out dest);
                if (!transferable)
                {
                    continue;
                }
                // 反射构造 BeamTransfer(copy, cell, dest)（count = copy.stackCount，整堆）
                transfers.Add(BeamTransferCtor.Invoke(new object[] { copy, cell, dest }));
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

        /// <summary>反射调用牵引光束私有静态 TryFindBestStorageCellIgnoringReachabilityAuto
        /// （自动型版本：内部用 CanAutoTransferThingForOwner + faction 判定）。
        /// Invoke 的参数为 object[]，运行时按实际类型绑定即可，无需编译期引用其
        /// Building_BeamManipulatorAuto 类型。</summary>
        public static bool TryFindStorageCellForAuto(
            object building, Thing copy, HashSet<IntVec3> excludedDestinations, int ownerKey, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            if (TryFindBestStorageCellAutoMethod == null || building == null || copy == null)
            {
                return false;
            }
            object[] args = { building, copy, excludedDestinations, ownerKey, IntVec3.Invalid };
            try
            {
                bool ok = (bool)TryFindBestStorageCellAutoMethod.Invoke(null, args);
                destination = (IntVec3)args[4];
                return ok;
            }
            catch (Exception)
            {
                return false; // 版本差异等：静默降级
            }
        }
    }

    /// <summary>手动型普通取货：借 1 个种子保证原方法成功，postfix 把其余判定通过的锚点副本
    /// 反射注入 batch（不借出）——"先确定要搬、后取出"，物品确定前不离开 vault、不显示。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_TryBuildBatchFromCell
    {
        // 未安装牵引光束或方法签名变化时为 null → Prepare 返回 false，整组 patch 跳过
        static bool Prepare() => BeamManipulatorCompat.TryBuildBatchFromCellMethod != null;

        static MethodBase TargetMethod() => BeamManipulatorCompat.TryBuildBatchFromCellMethod;

        static bool Prefix(Pawn pawn, IntVec3 cell, HashSet<IntVec3> excludedDestinations, int ownerKey)
        {
            Map map = pawn?.Map;
            if (map == null)
            {
                return true;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault != null)
            {
                // 只借 1 个"判定通过"的种子：让原方法 batch 非 null；其余副本由 postfix 注入（不借出）
                BeamManipulatorCompat.LendFirstTransferableCopy(
                    vault, cell, pawn, null, excludedDestinations, ownerKey);
            }
            return true; // 继续原方法：种子副本已进 thingGrid，会被 cell.GetThingList 发现
        }

        static void Postfix(Pawn pawn, IntVec3 cell, HashSet<IntVec3> excludedDestinations, int ownerKey, object batch)
        {
            Map map = pawn?.Map;
            if (map == null)
            {
                return;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault == null)
            {
                return;
            }
            // 注入上限 4 = beam 单轮通道数（含原方法已生成的 transfer，如种子）
            BeamManipulatorCompat.InjectTransferableCopies(
                batch, vault, cell, 4, pawn, null, excludedDestinations, ownerKey);
        }
    }

    /// <summary>自动型普通取货：同上（building 参数用 object 接收，避免编译期引用其类型）。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_TryBuildBatchFromCellAuto
    {
        // 未安装牵引光束或方法签名变化时为 null → Prepare 返回 false，整组 patch 跳过
        static bool Prepare() => BeamManipulatorCompat.TryBuildBatchFromCellAutoMethod != null;

        static MethodBase TargetMethod() => BeamManipulatorCompat.TryBuildBatchFromCellAutoMethod;

        static bool Prefix(object building, IntVec3 cell, HashSet<IntVec3> excludedDestinations, int ownerKey)
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
                // 只借 1 个"判定通过"的种子：让原方法 batch 非 null；其余副本由 postfix 注入（不借出）
                BeamManipulatorCompat.LendFirstTransferableCopy(
                    vault, cell, null, building, excludedDestinations, ownerKey);
            }
            return true;
        }

        static void Postfix(object building, IntVec3 cell, HashSet<IntVec3> excludedDestinations, int ownerKey, object batch)
        {
            Thing bt = building as Thing;
            Map map = bt?.Map;
            if (map == null)
            {
                return;
            }
            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault == null)
            {
                return;
            }
            // 注入上限 4 = beam 单轮通道数（含原方法已生成的 transfer，如种子）
            BeamManipulatorCompat.InjectTransferableCopies(
                batch, vault, cell, 4, null, building, excludedDestinations, ownerKey);
        }
    }

    /// <summary>手动型 WorkGiver 派活判定放行（HasAnyHaulWork → HasAnyStorageTransferFromCell）。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_HasAnyStorageTransferFromCell
    {
        // 未安装牵引光束或方法签名变化时为 null → Prepare 返回 false，整组 patch 跳过
        static bool Prepare() => BeamManipulatorCompat.HasAnyStorageTransferFromCellMethod != null;

        static MethodBase TargetMethod() => BeamManipulatorCompat.HasAnyStorageTransferFromCellMethod;

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
        // 未安装牵引光束或方法签名变化时为 null → Prepare 返回 false，整组 patch 跳过
        static bool Prepare() => BeamManipulatorCompat.LiftThingForTransferMethod != null;

        static MethodBase TargetMethod() => BeamManipulatorCompat.LiftThingForTransferMethod;

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
            // 无论整堆还是部分均走 SplitOff；投影 SplitOff 已统一重定向为从权威库存
            // 转移真实实例，保证第三方 Comp 状态与物品身份不丢失。
            Thing result = thing.SplitOff(count);
            if (result != null && result.Spawned)
            {
                result.DeSpawn(DestroyMode.Vanish);
            }
            __result = result;
            return false;
        }
    }

    /// <summary>目的地格门控（vault 作为目的地时的 noDeposit 语义）：牵引光束的存储搜索
    /// TryFindBestStorageCellCore 只检查 filter（AllowedToAccept）与阵营，不检查
    /// IHaulDestination.HaulDestinationEnabled（原版 StoreUtility 有查、牵引光束没有），
    /// 导致关闭"允许存入"后仍把 vault 格当作合法目的地（物品落格后被吸收进全局层）。
    /// IsBeamStorageGroupAllowed 是组级判定、且被格级 IsBeamStorageCellAllowed 内部调用，
    /// 是该问题的唯一汇合点：noDeposit 关闭的 vault 组整体排除 → FindBestCellInSlotGroup
    /// 返回 Invalid → TryFindBestStorageCellCore 继续遍历下一优先级存储组（与原版跳过
    /// 禁用存储的行为一致，而非直接放弃搬运）。覆盖手动/自动取货、飞行中换目的地、
    /// 施工配送等全部经 Core 的目的地搜索。</summary>
    [HarmonyPatch]
    internal static class Patch_Beam_IsBeamStorageGroupAllowed
    {
        // 未安装牵引光束或方法签名变化时为 null → Prepare 返回 false，整组 patch 跳过
        static bool Prepare() => BeamManipulatorCompat.IsBeamStorageGroupAllowedMethod != null;

        static MethodBase TargetMethod() => BeamManipulatorCompat.IsBeamStorageGroupAllowedMethod;

        static void Postfix(SlotGroup group, ref bool __result)
        {
            if (!__result || group == null)
            {
                return;
            }
            // vault 关闭"允许存入"（noDeposit）：该存储组对牵引光束不再可作目的地
            if (group.parent is Building_OuterrealmVault vault && vault.NoDeposit)
            {
                __result = false;
            }
        }
    }
}
