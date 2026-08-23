using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力（§v3）：普通 Hediff，授权 = 携带此 Hediff。随 pawn hediffSet 存档，
    /// 天然跨地图 / 随远行队。携带者拥有随身视图（SubspaceAccessPawn 上下文 + OuterrealmVaultViewThingOwner），
    /// 使制作选料可跨地图从全局库取料（副本不进 lister，仅制作选料注入可见）。
    /// 携带者的按钮菜单（选中 pawn 后底部 Gizmo）提供两个开关：自动取用（制作选料时自动从随身空间取料）
    /// 与自动存入（制作完成后自动把产物存入超维空间）。两个开关只限制"自动"路径，
    /// 右键手动存入 / 手动取出不受影响。
    /// </summary>
    public class Hediff_SubspaceAccess : Hediff
    {
        /// <summary>随身视图（非序列化：副本是全局库投影，读档后由选料注入惰性重建）。</summary>
        [Unsaved]
        public OuterrealmVaultViewThingOwner view;

        /// <summary>自动取用：制作选料时把随身视图副本注入 relevantThings（默认开）。关闭后制作不再自动从身上取料，手动取用不受影响。</summary>
        public bool autoTake = true;

        /// <summary>自动存入：制作完成后自动把产物 Deposit 进全局库（默认开）。关闭后产物留在原地 / 由玩家处置，手动存入不受影响。</summary>
        public bool autoStore = true;

        public override void ExposeData()
        {
            base.ExposeData();
            // 随 pawn hediffSet 存档；旧档无节点自动取默认值 true，保持既有行为。
            Scribe_Values.Look(ref autoTake, "autoTake", true);
            Scribe_Values.Look(ref autoStore, "autoStore", true);
        }

        public override void PostAdd(DamageInfo? dinfo)
        {
            base.PostAdd(dinfo);
            EnsureView(); // 授权即建空视图；副本由选料注入的 RebuildView 惰性物化
        }

        public override void PostRemoved()
        {
            ClearView();
            base.PostRemoved();
        }

        /// <summary>确保随身视图存在（惰性创建空视图；副本物化由 InjectPawnCopies 的 RebuildView 完成）。</summary>
        public OuterrealmVaultViewThingOwner EnsureView()
        {
            if (view == null)
            {
                view = new OuterrealmVaultViewThingOwner(new SubspaceAccessPawn(pawn, this));
            }
            return view;
        }

        /// <summary>注销随身视图（取消授权 / 移除 Hediff 时）：销毁全部副本，内容保留在全局层。</summary>
        public void ClearView()
        {
            if (view != null)
            {
                view.ClearView();
                view = null;
            }
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
    }
}
