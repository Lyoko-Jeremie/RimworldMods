using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// "取 X 到背包"（§6.1）：以**建筑**为行走目标（原版 TakeCountToInventory 的 GotoThing
    /// 对未 Spawned 物品无效，无法走到），走到建筑交互格后从视图副本取物入背包。
    /// TargetA = 建筑（行走目标）；TargetB = 视图副本（取物目标，未 Spawned）。
    /// 取物 toil：先 BoostCopy（#9 提升）使 SplitOff 一次可取足 job.count（不受 stackLimit 封顶），
    /// SplitOff 经 §5.2 #5 patch 即时同步全局；背包放不下时剩余经 TryAbsorbStack 回滚补偿回全局。
    /// </summary>
    public class JobDriver_VaultTakeToInventory : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 预留视图副本（数量按 job.count；#8 预留检查会按全局可用量 G−R 校验）
            return pawn.Reserve(TargetB, job, 1, job.count);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull<JobDriver_VaultTakeToInventory>(TargetIndex.B);
            // 走到建筑（TargetA）交互格；副本不在地图上，不能作为行走目标
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A);
            // 取物：提升副本 → SplitOff → 入背包（放不下则回滚补偿）
            yield return new Toil
            {
                initAction = () =>
                {
                    Thing copy = job.GetTarget(TargetIndex.B).Thing;
                    if (copy == null || copy.stackCount <= 0)
                    {
                        return;
                    }
                    OuterrealmVaultViewThingOwner view = copy.holdingOwner as OuterrealmVaultViewThingOwner;
                    if (view != null)
                    {
                        view.BoostCopy(copy); // 使 SplitOff 一次取足 job.count（§5.2 #9）
                    }
                    int num = Mathf.Min(job.count, copy.stackCount);
                    if (num <= 0)
                    {
                        return;
                    }
                    Thing piece = copy.SplitOff(num); // postfix 扣全局 + 即时补回副本
                    int added = pawn.inventory.GetDirectlyHeldThings().TryAdd(piece, num, true);
                    if (added < piece.stackCount && piece.stackCount > 0 && !piece.Destroyed)
                    {
                        // 背包放不下：剩余退回副本（TryAbsorbStack 回滚补偿 → 全局补回，§3.3）
                        copy.TryAbsorbStack(piece, false);
                    }
                }
            };
        }
    }
}
