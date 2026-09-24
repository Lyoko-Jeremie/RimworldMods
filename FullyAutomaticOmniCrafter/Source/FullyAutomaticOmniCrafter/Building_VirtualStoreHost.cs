using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 「虚拟存储」宿主基类：只把原版存储 / 搬运接口转发给 CompOmniVirtualStore，自身不含业务逻辑。
    ///
    /// 为什么必须有这一层：Verse/Thing.cs 的 SpawnSetup 用 `this is IHaulSource` /
    /// `this is IHaulDestination` 决定是否注册到 map.haulDestinationManager，原版
    /// WorkGiver_DoBill 也要求候选 haul source 满足 `holder is Thing`，因此这些接口
    /// **必须由 Thing 子类实现**，Comp 无法代劳。接口实现集中在本基类后，任何建筑只要
    /// 继承本类并挂上 CompOmniVirtualStore，即可获得「不可存入、可被取用」的虚拟存储。
    ///
    /// 语义要点：
    ///  · Accepts 恒为 false、HaulDestinationEnabled 恒为 false → 永远不作为搬运目的地（只出不进）。
    ///  · HaulSourceEnabled 跟随组件开关 → 停用后原版搜索立刻看不到它。
    ///  · StorageTabVisible 用显式接口实现返回 false → 隐藏原版存储页签与存储组 gizmo。
    /// </summary>
    public abstract class Building_VirtualStoreHost : Building,
        IHaulSource, IHaulDestination, IStoreSettingsParent, IThingHolder
    {
        private CompOmniVirtualStore store;
        private StorageSettings disallowAllSettings;

        /// <summary>虚拟存储组件；未挂该组件时所有接口退化为「空且禁用」。</summary>
        public CompOmniVirtualStore Store => store ?? (store = GetComp<CompOmniVirtualStore>());

        // ── IThingHolder ────────────────────────────────────────────────────────
        // ParentHolder 复用 Thing 的既有实现（holdingOwner?.Owner），此处无需重复声明。
        public ThingOwner GetDirectlyHeldThings()
        {
            return Store?.Contents;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwner held = GetDirectlyHeldThings();
            if (held != null) ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, held);
        }

        // ── IHaulSource ─────────────────────────────────────────────────────────
        public bool HaulSourceEnabled => Store?.Enabled == true;

        // ── IHaulDestination ────────────────────────────────────────────────────
        public bool HaulDestinationEnabled => false;

        /// <summary>虚拟存储只出不进：任何物品都不被接受。</summary>
        public bool Accepts(Thing t)
        {
            return false;
        }

        // ── IStoreSettingsParent ────────────────────────────────────────────────
        /// <summary>隐藏原版存储页签（ITab_Storage 与存储组 gizmo 都读这个接口成员）。</summary>
        bool IStoreSettingsParent.StorageTabVisible => false;

        /// <summary>全禁设置属于常量语义，不参与存档，读档后惰性重建。</summary>
        public StorageSettings GetStoreSettings()
        {
            if (disallowAllSettings == null)
            {
                disallowAllSettings = new StorageSettings(this);
                disallowAllSettings.filter.SetDisallowAll();
            }
            return disallowAllSettings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            return null;
        }

        public void Notify_SettingsChanged()
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            disallowAllSettings = null;
        }
    }
}
