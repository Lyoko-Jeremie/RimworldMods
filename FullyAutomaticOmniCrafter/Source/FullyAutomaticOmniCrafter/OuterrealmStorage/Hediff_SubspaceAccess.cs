using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力（§v3）：普通 Hediff，授权 = 携带此 Hediff。随 pawn hediffSet 存档，
    /// 天然跨地图 / 随远行队。制作选料直接查询全局权威索引，不再为每个 Pawn 建立完整物品视图；
    /// 只有 Job 正式预约成功后才从全局库结账并在 Pawn 所在处生成真实物品。
    /// 携带者的按钮菜单（选中 pawn 后底部 Gizmo）提供两个开关：自动取用（制作选料时自动从随身空间取料）
    /// 与自动存入（制作完成后自动把产物存入超维空间）。两个开关只限制"自动"路径，
    /// 右键手动存入 / 手动取出不受影响。
    /// </summary>
    public class Hediff_SubspaceAccess : Hediff
    {
        /// <summary>自动取用：制作选料时查询全局索引（默认开）。关闭后制作不再自动从身上取料，手动取用不受影响。</summary>
        public bool autoTake = true;

        /// <summary>自动存入：制作完成后自动把产物 Deposit 进全局库（默认开）。关闭后产物留在原地 / 由玩家处置，手动存入不受影响。</summary>
        public bool autoStore = true;

        /// <summary>自动存入的类别限制（默认开）：开启后逐个核对当前地图上的所有超维存储仓，
        /// 仅当产物能被至少一个仓接受（filter 允许且未冻结、未禁止存入）时才自动存入；其余产物按原版流程放置
        /// （可落到指定存储区）。仅 autoStore 开启时有意义。</summary>
        public bool autoStoreFiltered = true;

        public override void ExposeData()
        {
            base.ExposeData();
            // 随 pawn hediffSet 存档；旧档无节点自动取默认值 true，保持既有行为。
            Scribe_Values.Look(ref autoTake, "autoTake", true);
            Scribe_Values.Look(ref autoStore, "autoStore", true);
            // 旧档无此节点自动取默认值 true（= 按类别限制存入）；已显式改过该开关的旧档保留玩家设置。
            Scribe_Values.Look(ref autoStoreFiltered, "autoStoreFiltered", true);
        }

        /// <summary>
        /// 被授权 pawn 的按钮菜单（经原版 Pawn.GetGizmos → health.GetGizmos → Hediff.GetGizmos 链路挂载）：
        /// 自动取用 / 自动存入两个开关 + 超维存储管理器。groupKey 相同 → 多选 pawn 时点击一个同步全部。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            yield return new Command_Toggle
            {
                defaultLabel = "SubspaceAccess_AutoTake".Translate(),
                defaultDesc = "SubspaceAccess_AutoTakeDesc".Translate(),
                icon = OuterrealmStorageTex.VaultAllowTakeForUseIcon,
                groupKey = SubspaceAccessGizmoKeys.AutoTake,
                isActive = () => autoTake,
                toggleAction = () => autoTake = !autoTake,
            };
            yield return new Command_Toggle
            {
                defaultLabel = "SubspaceAccess_AutoStore".Translate(),
                defaultDesc = "SubspaceAccess_AutoStoreDesc".Translate(),
                icon = OuterrealmStorageTex.VaultAllowDepositIcon,
                groupKey = SubspaceAccessGizmoKeys.AutoStore,
                isActive = () => autoStore,
                toggleAction = () => autoStore = !autoStore,
            };
            yield return new Command_Toggle
            {
                defaultLabel = "SubspaceAccess_AutoStoreFiltered".Translate(),
                defaultDesc = "SubspaceAccess_AutoStoreFilteredDesc".Translate(),
                icon = OuterrealmStorageTex.VaultAllowDepositIcon,
                groupKey = SubspaceAccessGizmoKeys.AutoStoreFiltered,
                isActive = () => autoStoreFiltered,
                toggleAction = () => autoStoreFiltered = !autoStoreFiltered,
                // 条件开关：自动存入关闭时无意义，置灰（与 vault 的 allowTakeForUse 联动模式一致）。
                Disabled = !autoStore,
                disabledReason = "SubspaceAccess_AutoStoreFilteredDisabledReason".Translate(),
            };
            yield return new Command_Action
            {
                defaultLabel = "SubspaceAccess_OpenManager".Translate(),
                defaultDesc = "SubspaceAccess_OpenManagerDesc".Translate(),
                icon = OuterrealmStorageTex.SubspaceAccessOpenManagerSelfIcon,
                action = () => Find.WindowStack.Add(new Dialog_OuterrealmStorageManager(pawn)),
            };
        }
    }

    /// <summary>随身访问开关的多选合并 groupKey 常量（与原版多选同步机制一致）。</summary>
    internal static class SubspaceAccessGizmoKeys
    {
        public const int AutoTake = 714206;
        public const int AutoStore = 714207;
        public const int AutoStoreFiltered = 714208;
    }
}
