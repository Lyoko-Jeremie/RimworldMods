using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 视图容器上下文抽象（§v3 随身访问重构）：把 OuterrealmVaultViewThingOwner 对建筑的依赖
    /// 抽象为接口，使建筑 vault（Building_OuterrealmVault）与授权 pawn 随身视图（SubspaceAccessPawn）
    /// 共用同一套副本物化/退休/SplitOff 同步/预留记账机制。
    /// </summary>
    public interface IOuterrealmVaultContext : IThingHolder
    {
        /// <summary>当前上下文所在地图：建筑 = 建筑地图；随身 = pawn.Map（世界 pawn 为 null）。</summary>
        Map MapHeld { get; }

        /// <summary>是否进行 lister 投影（半 Spawned）。建筑 true；随身 false——副本不进 listerThings/listerHaulables。</summary>
        bool Spawned { get; }

        /// <summary>交互格：建筑 = InteractionCell；随身 = pawn.Position。用于设置副本 positionInt。</summary>
        IntVec3 InteractionCell { get; }

        /// <summary>该条目当前是否可见：建筑 = filter（CanShow）；随身 = 恒 true（预留 job 过滤扩展点）。</summary>
        bool CanShow(Thing t);
    }
}
