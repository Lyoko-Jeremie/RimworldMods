using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力（§v3）：普通 Hediff，授权 = 携带此 Hediff。随 pawn hediffSet 存档，
    /// 天然跨地图 / 随远行队。携带者拥有随身视图（SubspaceAccessPawn 上下文 + OuterrealmVaultViewThingOwner），
    /// 使制作选料可跨地图从全局库取料（副本不进 lister，仅制作选料注入可见）。
    /// </summary>
    public class Hediff_SubspaceAccess : Hediff
    {
        /// <summary>随身视图（非序列化：副本是全局库投影，读档后由选料注入惰性重建）。</summary>
        [Unsaved]
        public OuterrealmVaultViewThingOwner view;

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
    }
}
