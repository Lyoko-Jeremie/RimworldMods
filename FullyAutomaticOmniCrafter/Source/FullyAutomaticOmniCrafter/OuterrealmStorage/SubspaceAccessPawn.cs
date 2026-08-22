using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 随身上下文（§v3）：授权 pawn 的视图容器上下文。与建筑 vault 共用 OuterrealmVaultViewThingOwner 的
    /// 副本物化/退休/SplitOff 同步/预留记账机制。
    /// Spawned 恒 false → 副本不进 listerThings / listerHaulables（随身副本不属于任何地图）。
    /// ParentHolder = pawn → PositionHeld / GetRootPosition 解析到 pawn.Position，天然原地取料。
    /// 实现 IThingHolderEvents&lt;Thing&gt;：随身副本的整堆移除（Thing.SplitOff 整堆分支经
    /// holdingOwner.Remove → NotifyRemoved）必须同步扣减全局——否则"bill 需求 ≥ 全局剩余量"时
    /// 整堆取走不扣账 → 物品复制（§v3 随身同步）。
    /// </summary>
    public class SubspaceAccessPawn : IOuterrealmVaultContext, IThingHolderEvents<Thing>
    {
        private readonly Pawn pawn;
        private readonly Hediff_SubspaceAccess hediff;

        public SubspaceAccessPawn(Pawn pawn, Hediff_SubspaceAccess hediff)
        {
            this.pawn = pawn;
            this.hediff = hediff;
        }

        public Map MapHeld => pawn.Map;

        /// <summary>随身视图：不做 lister 投影。</summary>
        public bool Spawned => false;

        public IntVec3 InteractionCell => pawn.Position;

        /// <summary>随身视图恒可见（job 过滤为后续扩展点）。</summary>
        public bool CanShow(Thing t) => true;

        // ── IThingHolder ──

        public IThingHolder ParentHolder => pawn;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            // 无子 holder：副本由 GetDirectlyHeldThings 直接暴露。
        }

        public ThingOwner GetDirectlyHeldThings() => hediff != null ? hediff.view : null;

        // ── IThingHolderEvents<Thing>：视图加入/移除钩子（§v3 随身同步） ──

        public void Notify_ItemAdded(Thing item)
        {
            // 随身视图 Spawned=false 不进 listerThings / listerHaulables，加入无需地图级通知。
        }

        /// <summary>随身副本整堆移除（Remove 语义）时同步扣减全局（与建筑视图共用同一记账逻辑）。</summary>
        public void Notify_ItemRemoved(Thing item)
        {
            hediff?.view?.SyncRemoveFromGlobal(item);
        }
    }
}
