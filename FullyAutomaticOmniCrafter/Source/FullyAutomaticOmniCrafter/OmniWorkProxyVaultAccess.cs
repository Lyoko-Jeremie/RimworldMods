using System;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站代理的"超维存储随身访问"判定层（由超维存储访问授权界面上的全局开关控制）。
    ///
    /// 开关开启时代理与超维存储之间的空间距离按 0 计算：取出与存入都不需要走到终端建筑旁，
    /// 也不受工作站工作范围（Area_OmniWorkstation）限制。代理不携带访问 Hediff，
    /// 其"视同已授权"状态由 SubspaceAccessUtility.TryGetAutoFlags 统一提供。
    ///
    /// 本层只改变"够不够得到"与"要走多远"，不改变任何所有权边界：
    ///   - 取出仍走 TryStartCarry → OuterrealmSourceResolver.Checkout（投影拆分/权威实例转移）；
    ///   - 存入仍走 Toils_Haul.DepositHauledThingInContainer → view.TryAdd → Deposit。
    /// 因此终端的禁止存入 / 禁止取出 / 冻结 / filter / 存储优先级等限制全部照旧生效。
    /// </summary>
    internal static class OmniWorkProxyVaultAccess
    {
        // 开关状态的弱引用缓存（配合 GameComponent 的 setter 通知）：
        // IntVec3.IsForbidden(Pawn) 是原版最热的判定之一，而 GameComponent_OuterrealmStorage.Instance
        // 每次都要做一次 game.components 线性查找。这里缓存"当前存档的开关值"，
        // 只有切换存档或玩家在授权界面上改动时才重新读取。
        // 缓存字段都是引用/值类型的原子读写：即便被寻路或渲染线程读到一次陈旧值，
        // 语义上也只差一帧，不会破坏库存守恒。
        private static WeakReference cachedGameRef;
        private static bool cachedEnabled;

        /// <summary>代理随身访问的全局开关（随存档的殖民地级规则）。</summary>
        internal static bool Enabled
        {
            get
            {
                Game game = Current.Game;
                if (game == null)
                {
                    return false;
                }
                if (ReferenceEquals(cachedGameRef?.Target, game))
                {
                    return cachedEnabled;
                }
                GameComponent_OuterrealmStorage storage = GameComponent_OuterrealmStorage.Instance;
                if (storage == null)
                {
                    // 组件尚未挂载：不建立缓存，等它出现后自然建立。
                    return false;
                }
                cachedGameRef = new WeakReference(game);
                cachedEnabled = storage.ProxySubspaceAccess;
                return cachedEnabled;
            }
        }

        /// <summary>开关写入后刷新缓存（由 GameComponent 的 setter 调用）。</summary>
        internal static void NotifyEnabledChanged(bool value)
        {
            Game game = Current.Game;
            cachedGameRef = game != null ? new WeakReference(game) : null;
            cachedEnabled = value;
        }

        /// <summary>该代理当前是否以"零距离"方式使用超维存储（开关开启且本图存在终端）。</summary>
        internal static bool Applies(Pawn pawn, Map map)
        {
            if (pawn == null || map == null || !OmniWorkProxyUtility.IsProxy(pawn))
            {
                return false;
            }
            if (!Enabled)
            {
                return false;
            }
            return GameComponent_OuterrealmStorage.Instance?.HasVaultOnMap(map) == true;
        }

        /// <summary>
        /// 目标是否为"超维存储访问点"：终端建筑本体、终端的视图副本，或终端占用的存储格。
        ///
        /// 唯一物品的临时权威锚点不在此列——它被临时注册在终端格上，按普通目标寻路同样会到达
        /// 同一位置，只是不享受零距离（多一个 tick 的移动）。这里不为它付出热路径判定成本。
        /// </summary>
        internal static bool IsVaultAccessPoint(Map map, LocalTargetInfo target)
        {
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                if (thing == null || thing.Destroyed)
                {
                    return false;
                }
                if (thing is Building_OuterrealmVault)
                {
                    return true;
                }
                // 视图副本：holdingOwner 是终端的视图容器，其 Owner 即终端建筑本身。
                return thing.holdingOwner?.Owner is Building_OuterrealmVault;
            }
            return IsVaultCell(map, target.Cell);
        }

        /// <summary>该格是否属于某个超维存储终端（占用格；终端存储格列表 = GenAdj.CellsOccupiedBy）。</summary>
        internal static bool IsVaultCell(Map map, IntVec3 cell)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }
            if (cell.GetEdifice(map) is Building_OuterrealmVault)
            {
                return true;
            }
            return cell.GetSlotGroup(map)?.parent is Building_OuterrealmVault;
        }
    }
}
