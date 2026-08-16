using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储所需的全部 Harmony patches（§5 清单）。
    /// 由 OmniCrafterMod 构造中的 HarmonyInstance.PatchAll() 自动应用。
    ///
    /// P1 已实现：#5 SplitOff 同步（防超卖）、TryAbsorbStack 回滚补偿、
    /// #6 ListerHaulables 锁定短路、#8 ReservationManager 预留数量检查、
    /// #9 数量替换（选料/取料/计数）、§5.1 冻结温度读数。
    /// P1 补充：#8 扩展——vault 建筑本体的 CanReserve/Reserve 豁免（无限容量容器
    /// 无需"防过度搬运"互斥），消除 PickUpAndHaul 多工同时搬入 vault 时的预留冲突报错。
    /// P4 再补：#7 使用路径放宽（allowTakeForUse 驱动）。
    /// </summary>
    internal static class OuterrealmPatchUtil
    {
        /// <summary>提升地图上所有视图副本的 stackCount 为全局剩余量（#9 数量感知路径）。</summary>
        public static void BoostMapVaults(Map map)
        {
            List<IHaulSource> sources = map.haulDestinationManager.AllHaulSourcesListForReading;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] is Building_OuterrealmVault vault && vault.view != null)
                {
                    vault.view.BoostAllCopies();
                }
            }
        }

        /// <summary>恢复地图上所有视图副本的 stackCount 为 min(全局剩余, stackLimit)。</summary>
        public static void UnboostMapVaults(Map map)
        {
            List<IHaulSource> sources = map.haulDestinationManager.AllHaulSourcesListForReading;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] is Building_OuterrealmVault vault && vault.view != null)
                {
                    vault.view.UnboostCopies();
                }
            }
        }
    }

    // ── §5.2 #5：Thing.SplitOff 取出即同步 + 防超卖（只 patch virtual 声明处一处） ──
    // ThingWithComps.SplitOff / MinifiedThing.SplitOff 均为 base.SplitOff 薄包装，
    // patch 此处即覆盖全部；三处全 patch 会导致 prefix/postfix 双触发。
    [HarmonyPatch(typeof(Thing), "SplitOff")]
    internal static class Patch_Thing_SplitOff
    {
        private static void Prefix(Thing __instance)
        {
            OuterrealmVaultViewThingOwner view = __instance.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view == null)
            {
                return;
            }
            view.PreSplitOff(__instance);
        }

        private static void Postfix(Thing __instance, Thing __result)
        {
            OuterrealmVaultViewThingOwner view = __instance.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view == null)
            {
                return;
            }
            view.PostSplitOff(__instance, __result);
        }
    }

    // ── §5.2 #5 配套：Thing.TryAbsorbStack 回滚补偿（§3.3） ──
    // SplitOff 调用方失败回滚（TakeToInventory / TryTransferToContainer）时 piece 合并回副本，
    // 须把吸收量补回全局。ThingWithComps.TryAbsorbStack 内部调 base（Thing）版本，patch 此处即覆盖。
    [HarmonyPatch(typeof(Thing), "TryAbsorbStack")]
    internal static class Patch_Thing_TryAbsorbStack
    {
        private static void Prefix(Thing __instance, Thing other)
        {
            OuterrealmVaultViewThingOwner view = __instance.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view == null || other == null)
            {
                return;
            }
            view.LastAbsorbAmount = other.stackCount;
        }

        private static void Postfix(Thing __instance, Thing other)
        {
            OuterrealmVaultViewThingOwner view = __instance.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view == null)
            {
                return;
            }
            int amountBefore = view.LastAbsorbAmount;
            view.LastAbsorbAmount = 0;
            if (amountBefore <= 0)
            {
                return;
            }
            // 实际吸收量 = 吸收前 other.stackCount − 吸收后 remaining（全量吸收时 other 被 Destroy → remaining=0；
            // 部分吸收（respectStackLimit=true 时 take 受限）按实际量补偿，避免多计全局）。
            int remaining = other != null && !other.Destroyed ? other.stackCount : 0;
            int absorbed = amountBefore - remaining;
            if (absorbed <= 0)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmEntryKey key = OuterrealmEntryKey.From(__instance);
            OuterrealmEntry e = gs.FindEntry(key);
            if (e != null)
            {
                e.Count += absorbed;
                gs.NotifyContentChanged(key); // version++ + 变更日志
            }
            else
            {
                // 条目已被完全取走并移除（回滚重建）：以副本属性物化新代表 Thing 重建条目。
                Thing newProto = GameComponent_OuterrealmStorage.Materialize(__instance);
                newProto.stackCount = 1;
                gs.RestoreEntry(new OuterrealmEntry { Key = key, Proto = newProto, Count = absorbed });
            }
        }
    }

    // ── §3.3 设置变更事件补链：StorageGroup.Notify_SettingsChanged 原版只通知 ISlotGroupParent 成员 ──
    // （Building_Storage / Zone_Stockpile 等有格子的存储）。无限容量容器（本 Mod vault 等非
    // ISlotGroupParent 成员）收不到组设置变更事件——此前靠 60 tick 签名轮询兜底（且组 filter
    // 内容变化因 ToString=Summary 本就漏检）。补链：额外通知非 ISlotGroupParent 的
    // IStoreSettingsParent 成员（语义与原版本意一致），使 vault 的设置变更检测完全事件驱动，
    // 从而移除 60 tick 指纹轮询（Building_OuterrealmVault 已同步精简）。
    // 前提：组 settings 的一切修改最终都触发本方法——filter 内容经
    // ThingFilter.settingsChangedCallback → StorageSettings.TryNotifyChanged；Priority 经
    // ITab_Storage 的 SettingsOwner.Notify_SettingsChanged（组场景返回 Group）。
    [HarmonyPatch(typeof(StorageGroup), "Notify_SettingsChanged")]
    internal static class Patch_StorageGroup_Notify_SettingsChanged
    {
        private static void Postfix(StorageGroup __instance)
        {
            List<IStorageGroupMember> members = __instance.members;
            for (int i = 0; i < members.Count; i++)
            {
                IStorageGroupMember m = members[i];
                if (m is IStoreSettingsParent settingsParent && !(m is ISlotGroupParent))
                {
                    settingsParent.Notify_SettingsChanged();
                }
            }
        }
    }

    // ── §5.2 #8：ReservationManager 预留记账（默认启用） ──
    // 对视图副本 target 用全局可用量 G−R 做数量检查（替代原版 num1 = target.Thing.stackCount），
    // 数量不足静默拒绝（不打 Log.Error）；在入口无条件执行（不因 ignoreOtherReservations 跳过，防 playerForced 强抢）。
    // 数量足够时直接短路放行：视图副本未 Spawned 且其 stackCount 受视图形态 stackLimit 约束
    // （如取 740 银而副本仅显示 500），原版 CanReserve 的 `stackCount > target.Thing.stackCount`
    // 检查会误拒（→ "Could not reserve ... No existing reserver." + job 无法启动）；
    // 视图副本的预留数量完全由 G−R 记账决定，跳过原版检查即可（Reserve 内部后续会正常添加 reservation）。
    [HarmonyPatch(typeof(ReservationManager), "CanReserve")]
    internal static class Patch_ReservationManager_CanReserve
    {
        private static bool Prefix(Pawn claimant, LocalTargetInfo target, int stackCount, ref bool __result)
        {
            Thing t = target.Thing;
            if (t is Building_OuterrealmVault)
            {
                // vault 建筑本体（§1.2 无限容量）：预留不构成任何容量约束，
                // 豁免"他人已占满 maxPawns"的互斥检查。否则 PickUpAndHaul 对目的地
                // 建筑打 maxPawns=1 整建筑预留后，其他搬运工同时搬入 vault 时
                // CanReserve 恒失败 → Reserve 失败 → "Could not reserve" 刷屏。
                __result = true;
                return false;
            }
            if (t == null || !(t.holdingOwner is OuterrealmVaultViewThingOwner view))
            {
                return true;
            }
            int req = stackCount == ReservationManager.StackCount_All ? t.stackCount : stackCount;
            if (req <= 0)
            {
                return true;
            }
            if (req > view.AvailableForReserve(t))
            {
                __result = false; // 数量不足：阻止预留
                return false;
            }
            __result = true; // 数量足够：短路放行（见上，避免原版按副本 stackCount 误拒）
            return false;
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "Reserve")]
    internal static class Patch_ReservationManager_Reserve
    {
        private static bool Prefix(Pawn claimant, LocalTargetInfo target, int maxPawns, int stackCount, ref bool __result)
        {
            Thing t = target.Thing;
            if (t is Building_OuterrealmVault)
            {
                // vault 建筑本体（§1.2 无限容量）：直接放行，不做预留记账。
                // 多 pawn 可同时向 vault 搬入（容量无限，无过度搬运问题）；
                // 消除 LogCouldNotReserveError 与 StartJob 的
                // "TryMakePreToilReservations() returned false" 警告刷屏。
                // 本 Mod 取货 job（JobDriver_VaultTakeToInventory）预留的是视图副本
                // （TargetB），与建筑本体无关，不受影响。
                __result = true;
                return false;
            }
            if (t == null || !(t.holdingOwner is OuterrealmVaultViewThingOwner view))
            {
                return true;
            }
            int req = stackCount == ReservationManager.StackCount_All ? t.stackCount : stackCount;
            if (req <= 0)
            {
                return true;
            }
            // 条目已空（e == null / Count <= 0）而副本仍残留在视图：孤儿副本自愈清理 + 静默拒绝。
            // 成因：条目被取空（穿戴 RemoveApparel → Subtract 等）后，残留副本在 60 tick 懒同步前
            // 被 JobGiver_OptimizeApparel（枚举 GetDirectlyHeldThings 且生成 job 不重置检查间隔）
            // 反复选中 → Wear job 启动 → 预留失败 → "Could not reserve ... No existing reserver"
            // + StartJob 警告每 30 tick 刷屏。此处命中即清理（不扣全局），本次预留静默失败
            // （不打 LogCouldNotReserveError），副本脱离候选后循环终止。必须放在穿戴排队分支之前：
            // 条目空时排队无物可取，同样应拒绝并清理。
            GameComponent_OuterrealmStorage gs0 = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry e0 = gs0 != null ? gs0.FindEntry(OuterrealmEntryKey.From(t)) : null;
            if (e0 == null || e0.Count <= 0)
            {
                view.DisposeOrphanCopy(t);
                __result = false;
                return false;
            }
            if (maxPawns > 1 && stackCount == 1)
            {
                // 穿戴排队预留（§穿戴 patch：maxPawns=8, stackCount=1）：允许多个 pawn 排队同一副本，
                // 先到者穿走（副本移除），后到者由穿戴 toil 的 FailOnDespawnedNullOrForbidden 在执行中失败
                // 一次即恢复——不做数量检查（防排队被 G−R 阻塞而每 tick 循环），实际取物由 SplitOff 校正防超卖。
                return true;
            }
            if (req > view.AvailableForReserve(t))
            {
                // 数量不足：静默拒绝（§3.3 静默语义，短路避免原版 LogCouldNotReserveError 刷屏）
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "CanReserveStack")]
    internal static class Patch_ReservationManager_CanReserveStack
    {
        private static bool Prefix(Pawn claimant, LocalTargetInfo target, ref int __result)
        {
            Thing t = target.Thing;
            if (t == null || !(t.holdingOwner is OuterrealmVaultViewThingOwner view))
            {
                return true;
            }
            long available = view.AvailableForReserve(t);
            __result = (int)Math.Min(available, int.MaxValue);
            return false;
        }
    }

    // §3.3 退休副本销毁检查：reservation 释放后，若对应条目已不存在 / filter 已禁止则销毁孤儿副本。
    // 覆盖能定位 target 的释放路径（Release / ReleaseAllForTarget）；ReleaseClaimedBy 系列无 target
    // 参数，孤儿副本留待下次同 key 变更或全量重建时清理（P4 可补）。
    [HarmonyPatch(typeof(ReservationManager), "Release")]
    internal static class Patch_ReservationManager_Release
    {
        private static void Postfix(LocalTargetInfo target)
        {
            Thing t = target.Thing;
            if (t != null && t.holdingOwner is OuterrealmVaultViewThingOwner view)
            {
                view.TryDisposeCopyIfObsolete(t);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "ReleaseAllForTarget")]
    internal static class Patch_ReservationManager_ReleaseAllForTarget
    {
        private static void Postfix(Thing t)
        {
            if (t != null && t.holdingOwner is OuterrealmVaultViewThingOwner view)
            {
                view.TryDisposeCopyIfObsolete(t);
            }
        }
    }

    // ── 穿戴 job 预留修复：衣物来自本系统建筑时预留"副本（1 件，排队）"而非"建筑" ──
    // ① 原版 JobDriver_Wear/ForceTargetWear 在 TargetIsOnApparelSource 时 Reserve(建筑)。
    //    本建筑作为无限容量存储目的地常被搬运工 HaulToContainer 预留 → Reserve(建筑) 失败
    //    → job 无法启动（点击穿戴无动作 / OptimizeApparel 自动穿戴失败后不设冷却而每 tick 重试）。
    // ② 预留副本用"排队语义"（maxPawns=8, stackCount=1）：多个 pawn 可同时预留同一副本，
    //    先到者 RemoveApparel 穿走（副本移除），后到者穿戴 delay toil 的 FailOnDespawnedNullOrForbidden(A)
    //    使 job 在执行中失败一次即恢复——不再出现"检查（建筑）通过但预留（副本）失败"的每 tick 循环。
    [HarmonyPatch(typeof(JobDriver_Wear), "TryMakePreToilReservations")]
    internal static class Patch_JobDriver_Wear_TryMakePreToilReservations
    {
        private static bool Prefix(JobDriver_Wear __instance, bool errorOnFailed, ref bool __result)
        {
            Thing apparelThing = __instance.job.GetTarget(TargetIndex.A).Thing;
            if (!(apparelThing is Apparel ap) || ap.Spawned || !(ap.ParentHolder is Building_OuterrealmVault))
            {
                return true; // 非本系统：完全走原版
            }
            __result = __instance.pawn.Reserve(ap, __instance.job, 8, 1, errorOnFailed: errorOnFailed);
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_ForceTargetWear), "TryMakePreToilReservations")]
    internal static class Patch_JobDriver_ForceTargetWear_TryMakePreToilReservations
    {
        private static bool Prefix(JobDriver_ForceTargetWear __instance, bool errorOnFailed, ref bool __result)
        {
            Thing apparelThing = __instance.job.GetTarget(TargetIndex.B).Thing;
            if (!(apparelThing is Apparel ap) || ap.Spawned || !(ap.ParentHolder is Building_OuterrealmVault))
            {
                return true; // 非本系统：完全走原版
            }
            // 本系统：复制原版逻辑，仅 ApparelSource 分支改为预留副本 1 件（排队语义，其余保持不变）
            Pawn targetPawn = __instance.job.GetTarget(TargetIndex.A).Thing as Pawn;
            __instance.job.count = 1;
            if (__instance.pawn == targetPawn)
            {
                Log.Error($"Pawn {__instance.pawn} tried to do ForceTargetWear with self as target; this should not happen.");
                __result = false;
                return false;
            }
            if (!__instance.pawn.Reserve(targetPawn, __instance.job, errorOnFailed: errorOnFailed))
            {
                __result = false;
                return false;
            }
            __result = __instance.pawn.Reserve(ap, __instance.job, 8, 1, errorOnFailed: errorOnFailed);
            return false;
        }
    }

    // ── 装备 job 修复：原版 JobDriver_Equip 的容器武器路径硬编码 Building_OutfitStand ──
    // （TargetIsOnOutfitStand）。本系统武器副本走普通路径时 GotoThing(副本) +
    // FailOnDespawnedNullOrForbidden(A) 对未 Spawned 副本（Despawned=true）在起始即失败——
    // pawn 停住后不走向建筑、不装备，立即转做其他任务（点击后只闪一下目标标记）。
    // 本系统场景替换为自定义 toils：走到建筑（targetB）→ 副本 SplitOff(1) → AddEquipment。
    [HarmonyPatch(typeof(JobDriver_Equip), "MakeNewToils")]
    internal static class Patch_JobDriver_Equip_MakeNewToils
    {
        private static bool Prefix(JobDriver_Equip __instance, ref IEnumerable<Toil> __result)
        {
            Thing weapon = __instance.job.GetTarget(TargetIndex.A).Thing;
            if (!(weapon is ThingWithComps) || weapon.Spawned || !(weapon.ParentHolder is Building_OuterrealmVault))
            {
                return true; // 非本系统：完全走原版
            }
            __result = VaultEquipToils(__instance);
            return false;
        }

        private static IEnumerable<Toil> VaultEquipToils(JobDriver_Equip driver)
        {
            driver.FailOnDestroyedOrNull<JobDriver_Equip>(TargetIndex.A);
            Thing weapon = driver.job.GetTarget(TargetIndex.A).Thing;
            // 行走目标 = 建筑（副本未 Spawned，不能作为 GotoThing 目标；原版 OutfitStand 路径同样以容器为行走目标）
            driver.job.targetB = (LocalTargetInfo)(Thing)weapon.ParentHolder;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.InteractionCell)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return new Toil
            {
                initAction = () =>
                {
                    Thing copy = driver.job.GetTarget(TargetIndex.A).Thing;
                    if (copy == null || copy.stackCount <= 0)
                    {
                        return;
                    }
                    // 从视图副本取 1 件（SplitOff 经 §3.3 同步全局：整堆分支走 Notify_ItemRemoved 扣减）
                    Thing piece = copy.SplitOff(1);
                    if (piece is ThingWithComps twc && driver.pawn.equipment != null)
                    {
                        // 先腾出主武器槽（旧武器入背包或落地，对齐原版 JobDriver_Equip 流程）；
                        // 否则 AddEquipment 在 pawn 已有主武器时会 Log.Error（"got primaryInt equipment ... while already having ..."）
                        driver.pawn.equipment.MakeRoomFor(twc);
                        driver.pawn.equipment.AddEquipment(twc);
                    }
                }
            };
        }
    }

    // ── 无限容量豁免：对 Vault 本体豁免"非 HaulToContainer 预留"检查（右键搬运被错误拒绝） ──
    // 原版 StoreUtility.TryFindBestBetterNonSlotGroupStorageFor 对 IHaulEnroute 容器要求
    // map.reservationManager.OnlyReservationsForJobDef(容器, JobDefOf.HaulToContainer) 才将其选为
    // 搬运目标。第三方 Mod（典型如 PickUpAndHaul 的 HaulToInventory job）会对"无限容量容器"本体
    // 打上 HaulToContainer 之外的 reservation（其源码注释明言 "reserve it so you don't over-haul"，
    // 属防过度搬运的软预留）→ Vault 在右键搬运的目标搜索中被跳过 → 全图无其他存储时右键菜单
    // 显示原版 "NoEmptyPlaceLower"（未配置空余的、可到达的储存点）。
    // Vault 容量无限（SpaceRemainingFor=int.MaxValue、GetCountCanAccept=int.MaxValue），任何预留
    // 都不构成容量约束，故对 Vault 豁免该检查：requireAtLeastOne=false（搬运目标搜索）时恒放行；
    // requireAtLeastOne=true 时仍要求存在 HaulToContainer 预留，维持原版"至少一条"语义。
    // 修复与本 Mod 是否加载 PickUpAndHaul 无关——覆盖一切向 Vault 本体打非 HaulToContainer 预留的路径。
    [HarmonyPatch(typeof(ReservationManager), "OnlyReservationsForJobDef")]
    internal static class Patch_ReservationManager_OnlyReservationsForJobDef
    {
        private static bool Prefix(ReservationManager __instance, LocalTargetInfo target, JobDef requiredJobDef, bool requireAtLeastOne, ref bool __result)
        {
            Thing t = target.Thing;
            if (t == null || !(t is Building_OuterrealmVault) || requiredJobDef != JobDefOf.HaulToContainer)
            {
                return true; // 非本系统：完全走原版
            }
            bool hasRequired = false;
            List<ReservationManager.Reservation> reservations = __instance.ReservationsReadOnly;
            for (int i = 0; i < reservations.Count; i++)
            {
                ReservationManager.Reservation r = reservations[i];
                if (r.Target == target && r.Job != null && r.Job.def == requiredJobDef)
                {
                    hasRequired = true;
                    break;
                }
            }
            __result = !requireAtLeastOne || hasRequired;
            return false;
        }
    }

    // ── §6.3 防回吸：放行条目的搬运目标排除本系统 Vault ──
    // 若目标选中另一座超维存储仓，条目会被其 TryAdd 吸收回全局层（数量不减）→ 放行永不完成、
    // 无限搬运循环。postfix 发现"放行条目 + 目标是本系统 Vault"时置失败（搬运工放弃该 job，
    // 物品滞留；存在普通存储区时正常搬走）。
    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterNonSlotGroupStorageFor")]
    internal static class Patch_StoreUtility_TryFindBestBetterNonSlotGroupStorageFor
    {
        private static void Postfix(Thing t, ref IHaulDestination haulDestination, ref bool __result)
        {
            if (!__result || haulDestination == null || t == null)
            {
                return;
            }
            if (haulDestination is Building_OuterrealmVault
                && t.ParentHolder is Building_OuterrealmVault)
            {
                // 阻止 vault→vault 搬运（无论放行/锁定）：目标是 vault 时失败，
                // 避免无限搬运循环；vault→普通存储区不受影响（目标不是 vault）。
                haulDestination = null;
                __result = false;
            }
        }
    }

    // ── §5.2 #9：数量替换（"无限容量对外可见"，默认启用） ──
    // 取料：Pawn_CarryTracker.TryStartCarry 对视图副本临时提升 stackCount，使单趟取物量不受 stackLimit 封顶。
    // 修复 1/2/3：
    //   1. 回滚兜底——TryAdd 失败时把已扣全局的 splitStack 退回（split 分支 TryAbsorbStack 补回全局；
    //      整堆分支重新吸收回全局层）。
    //   2. 预检——carry 满且不能堆叠时提前拒绝，避免 SplitOff 扣全局后 TryAdd 失败丢物品
    //      （原版 AvailableStackSpace 无条件减 CarriedThing.stackCount，会在"满且不同 def"时误报正数）。
    //   3. Boost 生效——配合 Patch_Toils_Haul_StartCarryThing 让取料量感知全局剩余量。
    [HarmonyPatch(typeof(Pawn_CarryTracker), "TryStartCarry", new Type[] { typeof(Thing), typeof(int), typeof(bool) })]
    internal static class Patch_Pawn_CarryTracker_TryStartCarry
    {
        // 缓存原版 private 方法 TryUpdateTransferables 的调用委托（避免每次反射）。
        private static readonly Action<Pawn_CarryTracker, Thing> TryUpdateTransferablesInvoker = BuildTryUpdateTransferablesInvoker();

        private static Action<Pawn_CarryTracker, Thing> BuildTryUpdateTransferablesInvoker()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(Pawn_CarryTracker), "TryUpdateTransferables");
                if (method == null)
                {
                    return null;
                }
                return (Action<Pawn_CarryTracker, Thing>)Delegate.CreateDelegate(typeof(Action<Pawn_CarryTracker, Thing>), method);
            }
            catch
            {
                // 反射失败（版本/签名变化）时跳过 TryUpdateTransferables，仅影响装运输舱场景的 transferable 列表，不影响物品正确性。
                return null;
            }
        }

        private static bool Prefix(Pawn_CarryTracker __instance, Thing item, int count, bool reserve, ref int __result)
        {
            if (!(item.holdingOwner is OuterrealmVaultViewThingOwner view))
            {
                return true; // 非视图副本：完全走原版
            }
            __result = TryStartCarryFromVault(__instance, item, count, reserve, view);
            return false;
        }

        private static int TryStartCarryFromVault(Pawn_CarryTracker carry, Thing item, int count, bool reserve, OuterrealmVaultViewThingOwner view)
        {
            Pawn pawn = carry.pawn;
            if (pawn.Dead || pawn.Downed)
            {
                Log.Error($"Dead/downed/deathresting pawn {pawn?.ToString()} tried to start carry {item.ToStringSafe<Thing>()}");
                return 0;
            }

            // 修复 2：预检——carry 已满且不能与 item 堆叠时直接拒绝（避免 SplitOff 扣全局后 TryAdd 失败丢物品）。
            if (carry.CarriedThing != null && !carry.CarriedThing.CanStackWith(item))
            {
                return 0;
            }

            // Boost：使 count 与 failIfStackCountLessThanJobCount 感知全局剩余量（配合 Patch_Toils_Haul_StartCarryThing）。
            view.BoostCopy(item);

            count = Mathf.Min(count, carry.AvailableStackSpace(item.def));
            count = Mathf.Min(count, item.stackCount);
            if (count <= 0)
            {
                // 防御：避免 count 为 0 时 SplitOff 抛异常（原版调用方通常已保证 >0，此处兜底）。
                // 恢复 Boost 前的 stackCount，避免 Boost 状态残留（非 StartCarryThing 路径无外层 finally 恢复）。
                view.UnboostCopy(item);
                return 0;
            }
            bool selected = Find.Selector.IsSelected(item);
            Thing splitStack = item.SplitOff(count);
            int num = carry.innerContainer.TryAdd(splitStack, count, true);
            if (num > 0 && splitStack != item && TryUpdateTransferablesInvoker != null)
            {
                TryUpdateTransferablesInvoker(carry, splitStack);
            }
            if (num <= 0)
            {
                // 修复 1：回滚兜底——TryAdd 失败（预检未能拦截的边界），把已扣全局的 splitStack 退回。
                RollbackVaultTake(item, splitStack, view);
                return num;
            }
            item.def.soundPickup.PlayOneShot((SoundInfo)new TargetInfo(item.Position, pawn.Map));
            if (reserve)
            {
                pawn.Reserve((LocalTargetInfo)carry.CarriedThing, pawn.CurJob);
            }
            if (selected)
            {
                if (!splitStack.Destroyed)
                {
                    Find.Selector.Select(splitStack);
                }
                Find.Selector.Select(carry.CarriedThing);
            }
            pawn.MapHeld.resourceCounter.UpdateResourceCounts();
            return num;
        }

        private static void RollbackVaultTake(Thing item, Thing splitStack, OuterrealmVaultViewThingOwner view)
        {
            if (splitStack == null || splitStack.Destroyed)
            {
                return;
            }
            if (splitStack == item)
            {
                // 整堆分支：item 已从视图移除（holdingOwner=null），全局已由 Notify_ItemRemoved 扣减。
                // 回滚 = 把 item 重新吸收回全局层（view.TryAdd 要求 holdingOwner==null，整堆后满足）。
                if (item.holdingOwner == null && item.stackCount > 0)
                {
                    view.TryAdd(item);
                }
            }
            else
            {
                // split 分支：item 仍在视图中，splitStack 是独立 piece。
                // 回滚 = item.TryAbsorbStack(splitStack) → Patch_Thing_TryAbsorbStack 补回全局。
                if (item.holdingOwner is OuterrealmVaultViewThingOwner v)
                {
                    item.TryAbsorbStack(splitStack, false);
                    v.UnboostCopy(item); // 纠正 TryAbsorbStack 后可能超过 stackLimit 的 stackCount
                }
            }
        }
    }

    // ── §5.2 #9 配套（修复 3）：Toils_Haul.StartCarryThing 执行期 Boost ──
    // StartCarryThing 的 initAction 在取料前读取 thing.stackCount（=min(全局剩余, stackLimit)）计算
    // count 并做 failIfStackCountLessThanJobCount 检查——执行期未 Boost 会导致"单份需求 > stackLimit"
    // 的账单反复 Incompletable，且单趟取料量被 stackLimit 封顶。此处包装 initAction：执行时对视图副本
    // BoostCopy（幂等），执行后仅在副本仍留在视图中时 UnboostCopy（整堆分支已移出视图，不可误改 carry 物品）。
    [HarmonyPatch(typeof(Toils_Haul), "StartCarryThing")]
    internal static class Patch_Toils_Haul_StartCarryThing
    {
        private static void Postfix(ref Toil __result, TargetIndex haulableInd)
        {
            if (__result == null || __result.initAction == null)
            {
                return;
            }
            Toil toil = __result;
            Action original = toil.initAction;
            toil.initAction = () =>
            {
                Pawn actor = toil.actor;
                Thing thing = actor != null && actor.jobs != null && actor.jobs.curJob != null
                    ? actor.jobs.curJob.GetTarget(haulableInd).Thing
                    : null;
                OuterrealmVaultViewThingOwner view = thing != null ? thing.holdingOwner as OuterrealmVaultViewThingOwner : null;
                if (view != null)
                {
                    view.BoostCopy(thing);
                    try
                    {
                        original();
                    }
                    finally
                    {
                        // 仅当副本仍留在视图中时恢复（整堆分支已移出视图，不能误改 carry 物品）。
                        if (thing.holdingOwner is OuterrealmVaultViewThingOwner v)
                        {
                            v.UnboostCopy(thing);
                        }
                    }
                }
                else
                {
                    original();
                }
            };
        }
    }

    // ── ResourceCounter 计数：全局存储计入资源计数（设计文档 §5.2 表 #3） ──
    // 原版 UpdateResourceCounts 只统计 SlotGroup（格子型存储），无限容量容器（本 Mod Vault）
    // 不参与 → 存储在其中的资源在 resourceCounter 中恒为 0，导致建造设计器
    // DrawPanelReadout / DrawPlaceMouseAttachments 显示"库存不足"、StuffDef 默认材料误判。
    // 本 postfix 在按地图判定"该地图存在已 Spawned 的 Vault"后，把全局层各条目数量
    // 并入 countedAmounts（仅 CountAsResource 的 def；Proto 经 GetInnerIfMinified 取真实 def）。
    [HarmonyPatch(typeof(ResourceCounter), "UpdateResourceCounts")]
    internal static class Patch_ResourceCounter_UpdateResourceCounts
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(ResourceCounter), "map");

        private static void Postfix(ResourceCounter __instance)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            Map map = MapField != null ? (Map)MapField.GetValue(__instance) : null;
            if (map == null || !gs.HasVaultOnMap(map))
            {
                return;
            }
            Dictionary<ThingDef, int> amounts = __instance.AllCountedAmounts;
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry e = entries[i];
                if (e == null || e.Proto == null || e.Count <= 0)
                {
                    continue;
                }
                Thing inner = e.Proto.GetInnerIfMinified();
                if (inner == null || inner.def == null || !inner.def.CountAsResource)
                {
                    continue;
                }
                int cur;
                if (amounts.TryGetValue(inner.def, out cur))
                {
                    amounts[inner.def] = (int)Math.Min((long)cur + e.Count, int.MaxValue);
                }
            }
        }
    }

    // ── 建造设计器材料选择：vault 材料纳入 stuff 候选与可用性判定 ──
    // 原版 Designator_Build.ProcessInput 对 MadeFromStuff 建筑用
    //   resourceCounter.AllCountedAmounts.Keys 作为候选来源，
    //   并以 map.listerThings.ThingsOfDef(def).Count > 0 判定"确实持有该材料"。
    // vault 的视图副本未 Spawned，既不在 resourceCounter 也不在 listerThings，
    // 因此 vault 里纵有足够钢材也会走到 options.Count == 0 → "NoStuffsToBuildWith"。
    // 本 prefix 仅在当前地图存在 vault 时接管该分支：候选来源并入 vault 内的 stuff def，
    // 可用性判定并入"全局层中有该 def 的条目"，其余（排序/浮菜单/材质选择动作）与原版逐行一致。
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    internal static class Patch_Designator_Build_ProcessInput
    {
        private static readonly MethodInfo CheckCanInteractMethod = AccessTools.Method(typeof(Designator), "CheckCanInteract");
        private static readonly FieldInfo WriteStuffField = AccessTools.Field(typeof(Designator_Build), "writeStuff");

        private static bool Prefix(Designator_Build __instance, Event ev)
        {
            if (!(__instance.PlacingDef is ThingDef entDef) || !entDef.MadeFromStuff)
            {
                return true; // 非 stuff 建筑：完全走原版
            }
            Map map = __instance.Map;
            if (map == null)
            {
                return true;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || !gs.HasVaultOnMap(map))
            {
                return true; // 当前地图无 vault：完全走原版
            }
            if (CheckCanInteractMethod != null && !(bool)CheckCanInteractMethod.Invoke(__instance, null))
            {
                return false; // 对齐原版方法首行：不可交互则直接返回
            }

            // 候选 stuff def：resourceCounter 已有键 + vault 全局层中的 stuff def（去重）
            List<ThingDef> candidates = new List<ThingDef>();
            HashSet<ThingDef> seen = new HashSet<ThingDef>();
            Dictionary<ThingDef, int> counted = map.resourceCounter.AllCountedAmounts;
            foreach (ThingDef d in counted.Keys)
            {
                if (d != null && seen.Add(d))
                {
                    candidates.Add(d);
                }
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry e = entries[i];
                if (e == null || e.Proto == null || e.Count <= 0)
                {
                    continue;
                }
                Thing inner = e.Proto.GetInnerIfMinified();
                ThingDef d = inner != null ? inner.def : null;
                if (d != null && d.IsStuff && seen.Add(d))
                {
                    candidates.Add(d);
                }
            }

            // 排序对齐原版：commonality 降序、BaseMarketValue 升序
            candidates.Sort(CompareStuffCandidates);

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < candidates.Count; i++)
            {
                ThingDef thingDef = candidates[i];
                if (!thingDef.IsStuff || thingDef.stuffProps == null || !thingDef.stuffProps.CanMake(entDef))
                {
                    continue;
                }
                bool available = DebugSettings.godMode
                    || map.listerThings.ThingsOfDef(thingDef).Count > 0
                    || gs.TotalCountOf(thingDef) > 0;
                if (!available)
                {
                    continue;
                }
                ThingDef localStuffDef = thingDef;
                options.Add(new FloatMenuOption(
                    (__instance.sourcePrecept == null
                        ? GenLabel.ThingLabel(entDef, localStuffDef)
                        : (string)"ThingMadeOfStuffLabel".Translate(localStuffDef.LabelAsStuff, __instance.sourcePrecept.Label)).CapitalizeFirst(),
                    () =>
                    {
                        // 对齐原版闭包：base.ProcessInput(ev)（= Designator.ProcessInput：
                        // 检查交互 + 播放激活音 + 选中）→ 选中 → 设置 stuffDef + writeStuff
                        if (CheckCanInteractMethod != null && (bool)CheckCanInteractMethod.Invoke(__instance, null))
                        {
                            __instance.CurActivateSound?.PlayOneShotOnCamera();
                        }
                        Find.DesignatorManager.Select(__instance);
                        __instance.SetStuffDef(localStuffDef);
                        if (WriteStuffField != null)
                        {
                            WriteStuffField.SetValue(__instance, true);
                        }
                    },
                    thingDef)
                {
                    tutorTag = $"SelectStuff-{entDef.defName}-{localStuffDef.defName}"
                });
            }

            if (options.Count == 0)
            {
                Messages.Message((string)"NoStuffsToBuildWith".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            else
            {
                Find.WindowStack.Add(new FloatMenu(options)
                {
                    onCloseCallback = () =>
                    {
                        if (WriteStuffField != null)
                        {
                            WriteStuffField.SetValue(__instance, true);
                        }
                    }
                });
                Find.DesignatorManager.Select(__instance);
            }
            return false;
        }

        private static int CompareStuffCandidates(ThingDef a, ThingDef b)
        {
            float ca = a.stuffProps != null ? a.stuffProps.commonality : float.PositiveInfinity;
            float cb = b.stuffProps != null ? b.stuffProps.commonality : float.PositiveInfinity;
            int c = cb.CompareTo(ca); // commonality 降序
            if (c != 0)
            {
                return c;
            }
            return a.BaseMarketValue.CompareTo(b.BaseMarketValue); // 市场价值升序
        }
    }

    // ── 施工配送兜底：地面/可自动搬运材料不足时，从超维存储取料送往蓝图/Frame ──
    // 原版 WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor 只通过
    // itemAvailability（listerThings）与 GenClosest.ClosestThingReachable 寻找已 Spawned 的
    // 地面材料；vault 的视图副本未 Spawned，故永远不被考虑。本 postfix 在原版返回 null
    // （无法生成地面配送 job）时，逐个材料成本检查"地面可用量不足但 vault 有"，命中则生成
    // FAOC_VaultDeliverResources job（走到 vault → 取料入 carry → 送到蓝图/Frame）。
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    internal static class Patch_WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor
    {
        private static JobDef vaultDeliverJobDef;

        private static void Postfix(Pawn pawn, IConstructible c, ref Job __result)
        {
            if (__result != null || pawn == null || c == null)
            {
                return;
            }
            // 安装既有建筑（Blueprint_Install）无需从 vault 配送材料
            if (c is Blueprint_Install)
            {
                return;
            }
            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || !gs.HasVaultOnMap(map))
            {
                return;
            }
            List<ThingDefCountClass> costs = c.TotalMaterialCost();
            for (int i = 0; i < costs.Count; i++)
            {
                ThingDefCountClass need = costs[i];
                if (need.thingDef == null || need.count <= 0)
                {
                    continue;
                }
                int num = !(c is IHaulEnroute enroute)
                    ? c.ThingCountNeeded(need.thingDef)
                    : enroute.GetSpaceRemainingWithEnroute(need.thingDef, pawn);
                if (num <= 0)
                {
                    continue;
                }
                // 地面已有足够的可自动搬运材料：原版会处理，这里不越权
                if (MapHasEnough(pawn, need.thingDef, num))
                {
                    continue;
                }
                if (gs.TotalCountOf(need.thingDef) <= 0)
                {
                    continue;
                }
                if (!TryFindVaultCopy(gs, map, need.thingDef, out Building_OuterrealmVault vault, out Thing copy))
                {
                    continue;
                }
                Job job = MakeVaultDeliverJob(c, vault, copy, num);
                if (job != null)
                {
                    __result = job;
                    return;
                }
            }
        }

        private static Job MakeVaultDeliverJob(IConstructible c, Building_OuterrealmVault vault, Thing copy, int count)
        {
            if (vaultDeliverJobDef == null)
            {
                vaultDeliverJobDef = DefDatabase<JobDef>.GetNamedSilentFail("FAOC_VaultDeliverResources");
            }
            if (vaultDeliverJobDef == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(vaultDeliverJobDef);
            job.targetA = vault;
            job.targetB = copy;
            job.targetC = (Thing)c;
            job.count = count;
            return job;
        }

        private static bool MapHasEnough(Pawn pawn, ThingDef def, int amount)
        {
            List<Thing> things = pawn.Map.listerThings.ThingsOfDef(def);
            int total = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.IsForbidden(pawn))
                {
                    continue;
                }
                if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false))
                {
                    continue;
                }
                total += t.stackCount;
                if (total >= amount)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindVaultCopy(GameComponent_OuterrealmStorage gs, Map map, ThingDef def, out Building_OuterrealmVault vault, out Thing copy)
        {
            vault = null;
            copy = null;
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v == null || !v.Spawned || v.Map != map || v.view == null)
                {
                    continue;
                }
                List<Thing> copies = v.view.InnerListForReading;
                for (int j = 0; j < copies.Count; j++)
                {
                    Thing c = copies[j];
                    if (c == null)
                    {
                        continue;
                    }
                    Thing inner = c.GetInnerIfMinified();
                    if (inner != null && inner.def == def)
                    {
                        OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(c));
                        if (e != null && e.Count > 0)
                        {
                            vault = v;
                            copy = c;
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    // ── 半 Spawned 投影配套：补齐 ClosestThing_Global_Reachable 对 vault 副本的可见性 ──
    // 原版 ClosestThing_Global_Reachable 的局部函数 Process 里硬性 if (t == null || !t.Spawned) return，
    // 会跳过“未 Spawned 但处于 HaulSource 容器”的 vault 视图副本（染料/血包等少数路径用此方法）。
    // 该检查位于编译器生成的 display class 方法内，Transpiler 无法稳定定位 Thing.get_Spawned 调用点，
    // 故用 Postfix 兜底：原方法返回 null 时，遍历 searchSet 中未 Spawned 但 IsInHaulableInventory 的副本，
    // 复刻其可达性 + validator + priority 判定并回填 __result。
    [HarmonyPatch(typeof(GenClosest), "ClosestThing_Global_Reachable")]
    internal static class Patch_GenClosest_ClosestThing_Global_Reachable
    {
        private static void Postfix(
            IntVec3 center, Map map, IEnumerable<Thing> searchSet,
            PathEndMode peMode, TraverseParms traverseParams, float maxDistance,
            Predicate<Thing> validator, Func<Thing, float> priorityGetter,
            ref Thing __result)
        {
            if (__result != null || map == null || searchSet == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || !gs.HasVaultOnMap(map))
            {
                return;
            }

            Thing result = null;
            float closestDistSquared = float.MaxValue;
            float bestPrio = float.MinValue;
            float maxDistanceSquared = maxDistance * maxDistance;

            if (searchSet is IList<Thing> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    ConsiderCandidate(list[i], center, map, peMode, traverseParams, maxDistanceSquared,
                        validator, priorityGetter, ref result, ref closestDistSquared, ref bestPrio);
                }
            }
            else
            {
                foreach (Thing t in searchSet)
                {
                    ConsiderCandidate(t, center, map, peMode, traverseParams, maxDistanceSquared,
                        validator, priorityGetter, ref result, ref closestDistSquared, ref bestPrio);
                }
            }
            __result = result;
        }

        private static void ConsiderCandidate(
            Thing t, IntVec3 center, Map map, PathEndMode peMode, TraverseParms traverseParams,
            float maxDistanceSquared, Predicate<Thing> validator, Func<Thing, float> priorityGetter,
            ref Thing result, ref float closestDistSquared, ref float bestPrio)
        {
            if (t == null || t.Spawned || !HaulAIUtility.IsInHaulableInventory(t))
            {
                return;
            }
            float horizontalSquared = (center - t.PositionHeld).LengthHorizontalSquared;
            if (horizontalSquared > maxDistanceSquared)
            {
                return;
            }
            if (priorityGetter == null && horizontalSquared >= closestDistSquared)
            {
                return;
            }
            if (!map.reachability.CanReach(center, (LocalTargetInfo)t.SpawnedParentOrMe, peMode, traverseParams)
                || validator != null && !validator(t))
            {
                return;
            }
            float a = 0f;
            if (priorityGetter != null)
            {
                a = priorityGetter(t);
                if (a < bestPrio || Mathf.Approximately(a, bestPrio) && horizontalSquared >= closestDistSquared)
                {
                    return;
                }
            }
            result = t;
            closestDistSquared = horizontalSquared;
            bestPrio = a;
        }
    }

    // ── 半 Spawned 投影配套：自动食用豁免 vault 副本的 Spawned 检查 ──
    // FoodUtility.SpawnedFoodSearchInnerScan 里硬性 `search.Spawned`，副本未 Spawned 被跳过，
    // 导致 pawn 不自动取食 vault 食物。此处把 Thing.get_Spawned 替换为 IsUsableForIngest
    // （Spawned || IsInHaulableInventory），对齐 ClosestThing_Global 的豁免语义。
    [HarmonyPatch(typeof(FoodUtility), "SpawnedFoodSearchInnerScan")]
    internal static class Patch_FoodUtility_SpawnedFoodSearchInnerScan
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo spawnGetter = AccessTools.PropertyGetter(typeof(Thing), "Spawned");
            MethodInfo helper = AccessTools.Method(typeof(Patch_FoodUtility_SpawnedFoodSearchInnerScan), nameof(IsUsableForIngest));
            foreach (CodeInstruction code in instructions)
            {
                if (spawnGetter != null && helper != null && code.Calls(spawnGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, helper);
                }
                else
                {
                    yield return code;
                }
            }
        }

        private static bool IsUsableForIngest(Thing t)
        {
            return t.Spawned || HaulAIUtility.IsInHaulableInventory(t);
        }
    }

    // ── 半 Spawned 投影配套：食用执行——从 vault 取食送到嘴边 ──
    // 自动食用 / 右键“食用”都会生成 JobDefOf.Ingest（targetA = 食物副本）。原版 PrepareToIngestToils_ToolUser
    // 用 GotoThing(A).FailOnDespawnedNullOrForbidden 走到食物，副本未 Spawned 会在起始失败。
    // 此处对 vault 副本重写“取食”阶段：走到 vault 建筑交互格 → PickupIngestible（内部 TryStartCarry 已 patch，
    // 对副本 Boost+SplitOff+入 carry）→ 后续 CarryIngestibleToChewSpot / FindAdjacentEatSurface 原样复用。
    [HarmonyPatch(typeof(JobDriver_Ingest), "PrepareToIngestToils_ToolUser")]
    internal static class Patch_JobDriver_Ingest_PrepareToIngestToils_ToolUser
    {
        private static bool Prefix(JobDriver_Ingest __instance, ref IEnumerable<Toil> __result)
        {
            Thing food = __instance.job.GetTarget(TargetIndex.A).Thing;
            if (food == null || food.Spawned || !(food.ParentHolder is Building_OuterrealmVault))
            {
                return true; // 非 vault 副本：完全走原版
            }
            __result = VaultPrepareToIngestToils(__instance);
            return false;
        }

        private static IEnumerable<Toil> VaultPrepareToIngestToils(JobDriver_Ingest driver)
        {
            // 走到 vault 建筑交互格（副本未 Spawned，不能作为行走目标）
            yield return new Toil
            {
                initAction = () =>
                {
                    Thing food = driver.job.GetTarget(TargetIndex.A).Thing;
                    Thing vault = food != null ? food.ParentHolder as Thing : null;
                    if (vault != null)
                    {
                        driver.pawn.pather.StartPath((LocalTargetInfo)vault, PathEndMode.InteractionCell);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.PatherArrival
            };
            // 拿起食物：PickupIngestible 内部 TryStartCarry 已由 Patch_Pawn_CarryTracker_TryStartCarry 处理副本
            yield return Toils_Ingest.PickupIngestible(TargetIndex.A, driver.pawn);
            // 后续走到桌边/找餐桌，与原版 ToolUser 路径一致
            if (!driver.pawn.Drafted)
            {
                yield return Toils_Ingest.CarryIngestibleToChewSpot(driver.pawn, TargetIndex.A).FailOnDestroyedOrNull(TargetIndex.A);
            }
            yield return Toils_Ingest.FindAdjacentEatSurface(TargetIndex.B, TargetIndex.A);
        }
    }

    // ── 通用右键 FloatMenu：让 vault 副本通过 provider 的 Spawned 检查 ──
    // containedItemsSelectable=true 让副本进入 ClickedThings，但 FloatMenuOptionProvider.TargetThingValid
    // 默认返回 (CanTargetDespawned || thing.Spawned)，副本未 Spawned 会被过滤 → 所有 provider 选项都不出现。
    // 此处对 vault 副本短路 TargetThingValid，视为有效目标，使 Ingest/Wear/Equip 及第三方 provider 自动生成选项。
    [HarmonyPatch(typeof(FloatMenuOptionProvider), "TargetThingValid")]
    internal static class Patch_FloatMenuOptionProvider_TargetThingValid
    {
        private static bool Prefix(Thing thing, ref bool __result)
        {
            if (thing != null && thing.holdingOwner is OuterrealmVaultViewThingOwner)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    // ── 半 Spawned 投影配套：存档时临时摘除 vault 副本，存档后加回 ──
    // 原版 Map.ExposeData 的 Saving 分支遍历 listerThings.AllThings 并 Scribe_Deep.Look 保存每个不可压缩 Thing。
    // 副本已进入 listsByGroup（含 AllThings），若不摘除会被保存进存档，读档后 view 未序列化副本成为孤儿，
    // 并被 Spawn 到 positionInt（InteractionCell）→ 物品出现在地上。
    // 因此：Saving 时 Prefix 把本地图所有 vault 副本从 listerThings 移除，Postfix 加回（Harmony 的 Postfix
    // 在 finally 语义下执行，原方法异常时也会恢复）。副本恢复后重新进入 listsByDef + 所有 group，
    // 从而保留“按 group 查询（食物/药/毒品/书等）”的可见性。
    [HarmonyPatch(typeof(Map), "ExposeData")]
    internal static class Patch_Map_ExposeData
    {
        private static void Prefix(Map __instance, ref List<Thing> __state)
        {
            __state = null;
            if (Scribe.mode != LoadSaveMode.Saving)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            __state = new List<Thing>();
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v == null || v.MapHeld != __instance || v.view == null)
                {
                    continue;
                }
                List<Thing> copies = v.view.InnerListForReading;
                for (int j = 0; j < copies.Count; j++)
                {
                    Thing copy = copies[j];
                    if (copy == null)
                    {
                        continue;
                    }
                    __state.Add(copy);
                    __instance.listerThings.Remove(copy);
                }
            }
        }

        private static void Postfix(Map __instance, List<Thing> __state)
        {
            if (Scribe.mode != LoadSaveMode.Saving || __state == null)
            {
                return;
            }
            for (int i = 0; i < __state.Count; i++)
            {
                Thing copy = __state[i];
                if (copy != null && !copy.Destroyed && copy.MapHeld == __instance)
                {
                    __instance.listerThings.Add(copy);
                }
            }
        }
    }

    // ── 半 Spawned 投影配套：屏蔽 vault 副本的堆叠数字 overlay ──
    // 副本进入 HasGUIOverlay group 后，ThingOverlays 会遍历到它并调用 DrawGUIOverlay，
    // 在副本 Position（InteractionCell）处绘制 x 数字，多个副本堆叠同一格形成数字堆叠。
    // 对 vault 视图副本短路 DrawGUIOverlay（覆盖 Thing 与 ThingWithComps 两类副本），避免绘制。
    [HarmonyPatch(typeof(Thing), "DrawGUIOverlay")]
    internal static class Patch_Thing_DrawGUIOverlay
    {
        private static bool Prefix(Thing __instance)
        {
            return !(__instance.holdingOwner is OuterrealmVaultViewThingOwner);
        }
    }

    [HarmonyPatch(typeof(ThingWithComps), "DrawGUIOverlay")]
    internal static class Patch_ThingWithComps_DrawGUIOverlay
    {
        private static bool Prefix(ThingWithComps __instance)
        {
            return !(__instance.holdingOwner is OuterrealmVaultViewThingOwner);
        }
    }

    // 计数：RecipeWorkerCounter.CountProducts（账单"已有数量"用全局量）。
    [HarmonyPatch(typeof(RecipeWorkerCounter), "CountProducts")]
    internal static class Patch_RecipeWorkerCounter_CountProducts
    {
        private static void Prefix(Bill_Production bill)
        {
            if (bill != null && bill.Map != null)
            {
                OuterrealmPatchUtil.BoostMapVaults(bill.Map);
            }
        }

        private static void Postfix(Bill_Production bill)
        {
            if (bill != null && bill.Map != null)
            {
                OuterrealmPatchUtil.UnboostMapVaults(bill.Map);
            }
        }
    }

    // 选料：WorkGiver_DoBill.TryFindBestIngredientsHelper（HaulSource 分支候选的 stackCount 用全局量，
    // 使"单份需求 > stackLimit"的账单可生成，连续制作无 8-10 秒空转）。
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
    internal static class Patch_WorkGiver_DoBill_TryFindBestIngredientsHelper
    {
        private static void Prefix(Pawn pawn)
        {
            if (pawn != null && pawn.Map != null)
            {
                OuterrealmPatchUtil.BoostMapVaults(pawn.Map);
            }
        }

        private static void Postfix(Pawn pawn)
        {
            if (pawn != null && pawn.Map != null)
            {
                OuterrealmPatchUtil.UnboostMapVaults(pawn.Map);
            }
        }
    }

    // ── §7.3 A3：标记 pawn 的产物原地存入超维空间 ──
    // GenRecipe.MakeRecipeProducts 的迭代器在 initAction 里被 ToList() 枚举——postfix 物化枚举后
    // 逐个 Deposit 吸收进全局层，再返回空列表（原版 thingList.Count==0 → EndCurrentJob(Succeeded)，
    // 无放置/自持）。Bill_Mech 分支走 FinalizeGestatedPawns，不受影响。
    [HarmonyPatch(typeof(GenRecipe), "MakeRecipeProducts")]
    internal static class Patch_GenRecipe_MakeRecipeProducts
    {
        private static void Postfix(Pawn worker, ref IEnumerable<Thing> __result)
        {
            if (!OuterrealmMarkUtility.IsMarked(worker))
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<Thing> products = new List<Thing>();
            foreach (Thing product in __result)
            {
                if (product == null)
                {
                    continue;
                }
                products.Add(product);
                gs.Deposit(product); // 吸收（冻结在超维空间）
            }
            __result = new List<Thing>();
            if (products.Count > 0)
            {
                MoteMaker.ThrowText(worker.DrawPos, worker.Map, "OuterrealmVault_ProductDeposited".Translate(products[0].LabelCapNoCount));
            }
        }
    }

    // ── §5.1 必需：ThingOwnerUtility.TryGetFixedTemperature（冻结温度读数） ──
    // 硬编码 switch 无法从 mod 扩展；对持有者是本系统建筑的条目返回"冷冻"读数。
    // 使用 prefix 短路并严格限定本 mod holder，其余放行。
    [HarmonyPatch(typeof(ThingOwnerUtility), "TryGetFixedTemperature")]
    internal static class Patch_ThingOwnerUtility_TryGetFixedTemperature
    {
        private static bool Prefix(IThingHolder holder, ref float temperature, ref bool __result)
        {
            if (holder is Building_OuterrealmVault)
            {
                temperature = -30f; // 显示为"冷冻"（§5.1）
                __result = true;
                return false;
            }
            return true;
        }
    }

    // ── 穿戴候选前置清理：JobGiver_OptimizeApparel 运行前移除孤儿副本（§3.3 兜底） ──
    // 原版 JobGiver_OptimizeApparel 枚举所有 HaulSource 的 GetDirectlyHeldThings()（含本系统
    // vault 视图副本）生成 Wear job，且不检查条目数量——空条目残留副本被选中 → StartJob 预留
    // 失败 → 原版 "TryMakePreToilReservations() returned false" 警告。正常路径已由
    // Subtract 取空 → NotifyEntriesEmptied → SyncKey 即时清理；本 prefix 兜底"枚举与取空之间"
    // 的竞态残留窗口：先清理本 pawn 地图上所有 vault 的孤儿副本，候选列表即不含空条目副本，
    // 从源头消除该警告（Reserve 的孤儿分支保留为执行期兜底）。
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    internal static class Patch_JobGiver_OptimizeApparel_TryGiveJob
    {
        private static void Prefix(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v != null && v.Spawned && v.Map == pawn.Map && v.view != null)
                {
                    v.view.CleanOrphanCopies();
                }
            }
        }
    }

    // ── ITab_ContentsBase.IsVisible null 防护（修复 vanilla 通病 NRE 刷屏） ──
    // 原版 IsVisible => this.SelThing.Faction == Faction.OfPlayer，其中 SelThing =
    // Selector.SingleSelectedThing：MainTabWindow_Inspect 打开期间 selection 被清空/切换的帧
    // （点击空白取消选择、多选非 Thing 等）会返回 null → null.Faction 直接 NRE，且
    // UpdateTabs/PaneWidthFor/DoTabs 三处每帧调用导致刷屏。本系统 vault 的
    // ITab_OuterrealmVaultContents 继承 ITab_ContentsBase，玩家管理 vault 时高频触发；
    // prefix 短路 null 场景（无选中物时 Tab 本应隐藏，语义不变）。
    [HarmonyPatch(typeof(ITab_ContentsBase), "IsVisible", MethodType.Getter)]
    internal static class Patch_ITab_ContentsBase_IsVisible
    {
        private static bool Prefix(ref bool __result)
        {
            if (Find.Selector.SingleSelectedThing == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
