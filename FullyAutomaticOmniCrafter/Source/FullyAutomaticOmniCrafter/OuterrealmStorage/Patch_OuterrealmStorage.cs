using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

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

    // ── §5.2 #6：ListerHaulables 锁定短路（必需，默认启用） ──
    // 视图内条目 Accepts==true → 恒不 haulable（锁定，O(1) 短路，跳过 IsInValidBestStorage 全图搜索）；
    // 放行条目（Accepts==false）绝不短路（否则 §6.3 移出机制失效）。同时消除 M10 无限搬运循环。
    [HarmonyPatch(typeof(ListerHaulables), "ShouldBeHaulable")]
    internal static class Patch_ListerHaulables_ShouldBeHaulable
    {
        private static bool Prefix(Thing t, ref bool __result)
        {
            if (t.ParentHolder is Building_OuterrealmVault vault && vault.Accepts(t))
            {
                __result = false;
                return false;
            }
            return true;
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
                && t.ParentHolder is Building_OuterrealmVault sourceVault
                && !sourceVault.Accepts(t))
            {
                haulDestination = null;
                __result = false;
            }
        }
    }

    // ── §5.2 #9：数量替换（"无限容量对外可见"，默认启用） ──
    // 取料：Pawn_CarryTracker.TryStartCarry 对视图副本临时提升 stackCount，使单趟取物量不受 stackLimit 封顶。
    [HarmonyPatch(typeof(Pawn_CarryTracker), "TryStartCarry", new Type[] { typeof(Thing), typeof(int), typeof(bool) })]
    internal static class Patch_Pawn_CarryTracker_TryStartCarry
    {
        private static void Prefix(Thing item)
        {
            if (item.holdingOwner is OuterrealmVaultViewThingOwner view)
            {
                view.BoostCopy(item);
            }
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
}
