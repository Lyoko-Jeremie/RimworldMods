using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储通用工具。
    /// </summary>
    public static class OuterrealmVaultUtil
    {
        // ── 借出副本全局索引（§v4 借出温度修复） ──
        // 借出副本 = TryLendCopy 真 Spawn 到 vault 存储格的锚点副本（真 Spawned、holdingOwner=null），
        // 其 AmbientTemperature 走地图温度分支（Spawned → GetTemperatureForCell），绕过
        // ThingOwnerUtility.TryGetFixedTemperature 的自适应链——导致 UI 显示"未冷藏"且借出期间
        // 按地图温度真实腐烂（CompRottable.TickInterval 被 tick 驱动）。
        // 用 CWT 登记借出中的副本：TryLendCopy 登记 / ReturnCopy 注销；副本被 Destroy 后
        // 由弱引用自动清理（无需手动注销、无泄漏）。Thing 键仅弱引用，不阻碍 GC。
        private static readonly ConditionalWeakTable<Thing, object> BorrowedCopies =
            new ConditionalWeakTable<Thing, object>();

        /// <summary>登记借出副本（TryLendCopy 成功 Spawn 后调用）。</summary>
        public static void MarkOuterrealmBorrowed(Thing t)
        {
            if (t == null)
            {
                return;
            }
            BorrowedCopies.Remove(t);
            BorrowedCopies.Add(t, null);
        }

        /// <summary>注销借出副本（ReturnCopy 回收 / 被 job 取走离开 vault 时调用）。</summary>
        public static void UnmarkOuterrealmBorrowed(Thing t)
        {
            if (t != null)
            {
                BorrowedCopies.Remove(t);
            }
        }

        /// <summary>该 Thing 是否为"借出中"的 vault 副本（温度读数应走自适应保存温度）。</summary>
        public static bool IsOuterrealmBorrowed(Thing t)
        {
            object _;
            return t != null && BorrowedCopies.TryGetValue(t, out _);
        }

        /// <summary>
        /// 安全的物品显示名：Corpse 在 Bugged 状态（Corpse.LabelNoCount 会 Log.Error
        /// "LabelNoCount on Corpse while Bugged" 并返回空串）——用 def 标签兜底；其余走原版 LabelCapNoCount。
        /// </summary>
        public static string SafeLabelCapNoCount(Thing t)
        {
            if (t == null)
            {
                return "";
            }
            if (t is Corpse corpse && corpse.Bugged)
            {
                return corpse.def.label.CapitalizeFirst();
            }
            if (t is MinifiedThing minified && minified.InnerThing == null)
            {
                // MinifiedThing 内物丢失（InnerThing == null，原版非法状态，如打包箱曾
                // 被 Destroy / 内物被转移）：原版 LabelNoCount => InnerThing.LabelNoCount
                // 会直接 NRE——用 def 标签兜底（与 Corpse.Bugged 同模式）。
                return t.def.label.CapitalizeFirst();
            }
            return t.LabelCapNoCount;
        }

        /// <summary>
        /// 安全的物品图标：Corpse 在 Bugged 状态（InnerPawn==null）时，原版 Widgets.GetIconFor
        /// 会执行 thing = corpse.InnerPawn 然后访问 thing.StyleDef 而 NRE——用 def 图标兜底；其余走原版 ThingIcon。
        /// </summary>
        public static void ThingIconSafe(Rect rect, Thing thing)
        {
            if (thing == null)
            {
                return;
            }
            if (thing is Corpse corpse && corpse.Bugged)
            {
                Widgets.ThingIcon(rect, thing.def);
                return;
            }
            Widgets.ThingIcon(rect, thing);
        }

        /// <summary>
        /// 计算"存储中"物品的理想保存温度（§5.1 自适应温度读数）。
        /// 超维空间内物品时间被冻结（副本 dontTickContents、Proto 不 tick），实际不会腐烂/孵化/
        /// 受温度损坏；本读数只用于让 UI 状态与腐烂/损坏判定呈现"物品被妥善保存"的语义，
        /// 并按物品自身的温度约束自适应：
        ///   - 会腐烂的物品（CompRottable.Active）：尽量低温——无低温约束则 -30°C（显示"已冷冻"、
        ///     腐烂完全停止）；有最低安全温度（怕冷）则取该下界（显示"冷藏"，腐烂减速且不损坏）。
        ///   - 不腐烂或腐烂不可见的物品（受精卵等 disableIfHatcher）：返回安全区间内的室温 21°C，
        ///     不触发 CompTemperatureRuinable 的 Freezing/Overheating 显示与损坏。
        /// 温度约束取交集：CompTemperatureRuinable（min/maxSafeTemperature）、
        /// CompTemperatureDamaged（safeTemperatureRange）。
        /// 仅影响温度读数，不修改物品任何状态；取出物化（Materialize 不复制 comp 状态）不受影响。
        /// </summary>
        public static float IdealStorageTemperature(Thing t)
        {
            if (t == null)
            {
                return 21f;
            }
            float minSafe = float.MinValue;
            float maxSafe = float.MaxValue;
            CompTemperatureRuinable ruin = t.TryGetComp<CompTemperatureRuinable>();
            if (ruin != null)
            {
                if (ruin.Props.minSafeTemperature > minSafe)
                {
                    minSafe = ruin.Props.minSafeTemperature;
                }
                if (ruin.Props.maxSafeTemperature < maxSafe)
                {
                    maxSafe = ruin.Props.maxSafeTemperature;
                }
            }
            CompTemperatureDamaged dmg = t.TryGetComp<CompTemperatureDamaged>();
            if (dmg != null)
            {
                if (dmg.Props.safeTemperatureRange.min > minSafe)
                {
                    minSafe = dmg.Props.safeTemperatureRange.min;
                }
                if (dmg.Props.safeTemperatureRange.max < maxSafe)
                {
                    maxSafe = dmg.Props.safeTemperatureRange.max;
                }
            }
            CompRottable rot = t.TryGetComp<CompRottable>();
            float target;
            if (rot != null && rot.Active)
            {
                // 会腐烂：优先冷冻（<0°C 腐烂完全停止）；怕冷则取最低安全温度（冷藏减速）
                target = minSafe <= float.MinValue ? -30f : Mathf.Min(0f, minSafe);
            }
            else
            {
                // 不腐烂（或受精卵等腐烂不可见）：安全室温
                target = 21f;
            }
            return Mathf.Clamp(target, minSafe, maxSafe);
        }
    }
}
