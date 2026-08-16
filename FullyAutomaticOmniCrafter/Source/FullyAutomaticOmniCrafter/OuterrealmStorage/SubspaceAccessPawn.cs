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
    /// </summary>
    public class SubspaceAccessPawn : IOuterrealmVaultContext
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
    }
}
