using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>虚拟存储清单条目：某个 ThingDef 需要维持的目标数量。</summary>
    public class VirtualStoreItem : IExposable
    {
        public ThingDef thingDef;

        /// <summary>目标数量，不设上限。</summary>
        public int targetCount = 1;

        public VirtualStoreItem()
        {
        }

        public VirtualStoreItem(ThingDef def, int count)
        {
            thingDef = def;
            targetCount = count;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref thingDef, "thingDef");
            Scribe_Values.Look(ref targetCount, "targetCount", 1);
        }
    }

    public class CompProperties_OmniVirtualStore : CompProperties
    {
        /// <summary>补货对账间隔（tick）。1 表示每 tick 检查一次。</summary>
        public int ticksBetweenChecks = 1;

        /// <summary>
        /// 单次补货最多新增的堆数。目标数量可能极大（例如一百万钢铁 = 一万三千多个堆），
        /// 用这个上限把生成摊到多个 tick，避免单帧生成海量 Thing 造成卡顿。
        /// </summary>
        public int maxStacksPerTick = 200;

        public CompProperties_OmniVirtualStore()
        {
            compClass = typeof(CompOmniVirtualStore);
        }
    }

    /// <summary>
    /// 万能工作站的「虚拟存储」：内部持有一个真实物品容器，按玩家设定的目标数量自动补货。
    /// 物品未 Spawn、不占地图格、不渲染、不可被点击选中；只出不进（宿主 Accepts 恒为 false）。
    /// 取出走原版路径（制作选料遍历 haul source 的内容物、携带时 SplitOff 离开容器），
    /// 取走后的差额由本组件在后续 tick 补回。
    /// </summary>
    public class CompOmniVirtualStore : ThingComp, IThingHolder
    {
        private ThingOwner<Thing> contents;
        private List<VirtualStoreItem> wanted = new List<VirtualStoreItem>();
        private bool enabled = true;

        /// <summary>复用直方图，避免每 tick 分配临时集合（只在主线程 Tick 中读写）。</summary>
        private static readonly Dictionary<ThingDef, int> CountScratch = new Dictionary<ThingDef, int>();

        public bool Enabled => enabled;

        public IReadOnlyList<VirtualStoreItem> Wanted => wanted;

        public ThingOwner<Thing> Contents => contents ?? (contents = new ThingOwner<Thing>(this, true));

        private CompProperties_OmniVirtualStore VProps => props as CompProperties_OmniVirtualStore;

        // ── IThingHolder ────────────────────────────────────────────────────────
        // ParentHolder 由 ThingComp 基类提供（parent.ParentHolder），此处只需实现内容持有者相关成员。
        public ThingOwner GetDirectlyHeldThings()
        {
            return Contents;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, Contents);
        }

        // ── 开关 ────────────────────────────────────────────────────────────────
        /// <summary>停用：直接销毁容器内全部物品并暂停自动补货（清单配置保留）。</summary>
        public void SetEnabled(bool value)
        {
            if (enabled == value) return;
            enabled = value;
            if (!enabled) ClearContents();
        }

        // ── 清单维护 ────────────────────────────────────────────────────────────
        public int GetTargetCount(ThingDef def)
        {
            if (def == null) return 0;
            for (int i = 0; i < wanted.Count; i++)
                if (wanted[i].thingDef == def)
                    return wanted[i].targetCount;
            return 0;
        }

        /// <summary>设定目标数量；count 小于等于 0 表示从清单移除（不影响已存在的物品）。</summary>
        public void SetTargetCount(ThingDef def, int count)
        {
            if (def == null) return;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (wanted[i].thingDef != def) continue;
                if (count <= 0) wanted.RemoveAt(i);
                else wanted[i].targetCount = count;
                return;
            }
            if (count > 0) wanted.Add(new VirtualStoreItem(def, count));
        }

        public void RemoveEntry(ThingDef def)
        {
            if (def == null) return;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (wanted[i].thingDef != def) continue;
                wanted.RemoveAt(i);
                return;
            }
        }

        public void ClearEntries()
        {
            wanted.Clear();
        }

        // ── 容器操作 ────────────────────────────────────────────────────────────
        public void ClearContents()
        {
            contents?.ClearAndDestroyContents(DestroyMode.Vanish);
        }

        /// <summary>把容器内全部物品丢到宿主附近地面（不销毁、不清空清单）。</summary>
        public bool DropAllToGround()
        {
            if (contents == null || contents.Count == 0) return false;
            if (!parent.Spawned) return false;
            return contents.TryDropAll(parent.Position, parent.Map, ThingPlaceMode.Near);
        }

        public int CurrentTotalCount()
        {
            if (contents == null) return 0;
            int total = 0;
            for (int i = 0; i < contents.Count; i++) total += contents[i].stackCount;
            return total;
        }

        // ── Tick：节流补货 ──────────────────────────────────────────────────────
        public override void CompTick()
        {
            if (!enabled || wanted.Count == 0) return;
            if (parent == null || !parent.Spawned) return;

            int interval = VProps?.ticksBetweenChecks ?? 1;
            // 用 ThingID 打散相位，避免同 tick 内大量工作站集中补货（性能平滑）。
            if (interval > 1 && (Find.TickManager.TicksGame + parent.thingIDNumber) % interval != 0) return;

            MaintainStocks(VProps?.maxStacksPerTick ?? 200);
        }

        /// <summary>
        /// 对账并补货：先一次遍历容器建立直方图，再按清单逐条补足差额。
        /// 单次最多新增 maxStacksPerTick 个堆，剩余差额留到后续 tick。
        /// </summary>
        private void MaintainStocks(int maxStacksPerTick)
        {
            ThingOwner<Thing> owner = Contents;

            CountScratch.Clear();
            for (int i = 0; i < owner.Count; i++)
            {
                Thing existing = owner[i];
                if (existing == null) continue;
                int have;
                CountScratch.TryGetValue(existing.def, out have);
                CountScratch[existing.def] = have + existing.stackCount;
            }

            int createdStacks = 0;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (createdStacks >= maxStacksPerTick) break;
                VirtualStoreItem item = wanted[i];
                if (item == null) continue;
                ThingDef def = item.thingDef;
                if (def == null || item.targetCount <= 0) continue;

                int current;
                CountScratch.TryGetValue(def, out current);
                int missing = item.targetCount - current;
                if (missing <= 0) continue;

                int stackLimit = def.stackLimit > 0 ? def.stackLimit : 1;
                while (missing > 0 && createdStacks < maxStacksPerTick)
                {
                    int size = Mathf.Min(missing, stackLimit);
                    Thing made = MakeStock(def, size);
                    if (made == null) break; // 该 def 无法生成（例如不可打包的建筑），跳过
                    if (!owner.TryAdd(made, true))
                    {
                        made.Destroy(DestroyMode.Vanish);
                        break;
                    }
                    missing -= size;
                    createdStacks++;
                }
            }
        }

        /// <summary>
        /// 生成一件库存物品。建筑类 def 需要打包为 MinifiedThing，
        /// 因此复用万能制造机已有的生成实现（含打包、品质与艺术初始化）。
        /// </summary>
        private static Thing MakeStock(ThingDef def, int count)
        {
            if (def.category == ThingCategory.Building && !def.Minifiable) return null;
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            return Building_OmniCrafter.MakeThing(def, stuff, QualityCategory.Normal, count);
        }

        // ── 生命周期 ────────────────────────────────────────────────────────────
        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            // 打包 / 拆除 / 换图都不保留容器内容：物品由补货语义再生，避免随 MinifiedThing 泄漏。
            ClearContents();
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            ClearContents();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref contents, "contents", this);
            Scribe_Collections.Look(ref wanted, "wanted", LookMode.Deep);
            Scribe_Values.Look(ref enabled, "enabled", true);
            if (wanted == null) wanted = new List<VirtualStoreItem>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) SanitizeWanted();
        }

        /// <summary>读档后清理非法条目：空 def、非正数量，并把重复 def 合并为一条。</summary>
        private void SanitizeWanted()
        {
            for (int i = wanted.Count - 1; i >= 0; i--)
            {
                VirtualStoreItem item = wanted[i];
                if (item == null || item.thingDef == null || item.targetCount <= 0)
                {
                    wanted.RemoveAt(i);
                    continue;
                }
                for (int j = i - 1; j >= 0; j--)
                {
                    if (wanted[j]?.thingDef != item.thingDef) continue;
                    if (wanted[j].targetCount < item.targetCount) wanted[j].targetCount = item.targetCount;
                    wanted.RemoveAt(i);
                    break;
                }
            }
        }

        // ── UI ──────────────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent == null || parent.Faction != Faction.OfPlayer) yield break;

            yield return new Command_Toggle
            {
                defaultLabel = "OmniVirtualStore_GizmoToggle".Translate(),
                defaultDesc = "OmniVirtualStore_GizmoToggleDesc".Translate(),
                icon = TexCommand.DesirePower,
                isActive = () => enabled,
                toggleAction = () => SetEnabled(!enabled)
            };

            yield return new Command_Action
            {
                defaultLabel = "OmniVirtualStore_GizmoDropAll".Translate(),
                defaultDesc = "OmniVirtualStore_GizmoDropAllDesc".Translate(),
                icon = Verse.TexButton.Delete,
                action = () =>
                {
                    if (DropAllToGround())
                        Messages.Message("OmniVirtualStore_DroppedToGround".Translate(), parent,
                            MessageTypeDefOf.NeutralEvent, false);
                    else
                        Messages.Message("OmniVirtualStore_DropFailed".Translate(), parent,
                            MessageTypeDefOf.RejectInput, false);
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "OmniVirtualStore_GizmoConfig".Translate(),
                defaultDesc = "OmniVirtualStore_GizmoConfigDesc".Translate(),
                icon = TexButton.Info,
                action = OpenConfigWindow
            };
        }

        private void OpenConfigWindow()
        {
            if (Find.WindowStack.IsOpen<Window_OmniVirtualStoreConfig>()) return;
            Find.WindowStack.Add(new Window_OmniVirtualStoreConfig(this));
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            if (contents == null || contents.Count == 0)
            {
                yield return new FloatMenuOption("OmniVirtualStore_DropAllNoItems".Translate(), null);
                yield break;
            }
            yield return new FloatMenuOption("OmniVirtualStore_DropAll".Translate(), () => DropAllToGround());
        }

        public override string CompInspectStringExtra()
        {
            if (!enabled) return "OmniVirtualStore_InspectDisabled".Translate();
            if (wanted.Count == 0) return "OmniVirtualStore_InspectEmpty".Translate();
            return "OmniVirtualStore_InspectEnabled".Translate(wanted.Count, CurrentTotalCount());
        }
    }
}
