using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
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

        // ── 查询投影标记 ──────────────────────────────────────────────────────
        // 投影虽然必须使用真实 Thing 类型才能进入原版 lister / reservation / 配方筛选 API，
        // 但它没有任何库存所有权，stackCount 也只是“可见数量”。使用弱表按实例标记后，
        // 即使第三方 Mod 绕过 ThingOwner、先 DeSpawn/Remove 再尝试把该对象存入仓库，Deposit
        // 仍能识别并拒绝它，避免把显示副本第二次变成权威物品。弱键不会延长投影生命周期。
        private static readonly ConditionalWeakTable<Thing, object> ProjectionCopies =
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

        /// <summary>登记只读查询投影。重复登记先 Remove，兼容视图自愈重建和实例复用边界。</summary>
        public static void MarkProjection(Thing t)
        {
            if (t == null)
            {
                return;
            }
            ProjectionCopies.Remove(t);
            ProjectionCopies.Add(t, null);
        }

        /// <summary>判断对象是否为无库存所有权的查询投影；该判断不依赖 holdingOwner，防第三方先移除再存入。</summary>
        public static bool IsProjection(Thing t)
        {
            object _;
            return t != null && ProjectionCopies.TryGetValue(t, out _);
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

        // ── 打包建筑安装蓝图兼容（§安装蓝图修复） ───────────────────────────────
        // 超维存储是"权威实例 + 投影副本"双实例架构：打包建筑（MinifiedThing）被吸收后，
        // Blueprint_Install 引用的权威实例被藏入全局层（未 Spawned、无 Spawned parent），
        // 原版安装工作 InstallJob 的 CanReach 检查失败 → 蓝图永远"没有路径"卡死。
        // 三个配套措施（A/B/C）：
        //   A. 存入前拦截（Patch_HaulAIUtility_HaulToCellStorageJob）：自动搬运不把
        //      等待安装的打包建筑搬进 vault，物品留地面，安装照常从地面取用；
        //   B. 吸收时取消蓝图（CancelBlueprintIfPendingInstall）：兜底强制搬运/竞态
        //      绕过 A 后，蓝图随权威实例藏入全局层而取消（对齐原版
        //      MinifiedThing.Destroy 的 CancelBlueprintsFor 语义）；
        //   C. 借出时重定向蓝图引用（RedirectBlueprintToActual）：玩家对 vault 副本
        //      直接下达安装时，pawn 借出的是真物，把蓝图引用从副本改为真物，
        //      保证 pawn 手里、蓝图引用、安装产物三者实例一致，安装产出真建筑。

        /// <summary>该打包建筑是否正被安装蓝图引用（等待安装）。InnerThing 为 null（非法状态）时不查，避免
        /// InstallBlueprintUtility.ExistingBlueprintFor 对 reinstallationMap 用 null key 抛异常。</summary>
        public static bool IsPendingInstall(Thing t)
        {
            if (!(t is MinifiedThing minified) || minified.InnerThing == null)
            {
                return false;
            }
            return InstallBlueprintUtility.ExistingBlueprintFor(t) != null;
        }

        /// <summary>吸收打包建筑前取消引用它的安装蓝图（方案 B；非打包建筑/内物缺失无副作用）。</summary>
        public static void CancelBlueprintIfPendingInstall(Thing item)
        {
            if (item is MinifiedThing minified && minified.InnerThing != null)
            {
                InstallBlueprintUtility.CancelBlueprintsFor(item);
            }
        }

        /// <summary>借出打包建筑时，把引用副本的安装蓝图重定向到借出的真物（方案 C）。</summary>
        public static void RedirectBlueprintToActual(Thing copy, Thing actual)
        {
            if (copy == null || actual == null || copy == actual
                || !(copy is MinifiedThing copyMin) || copyMin.InnerThing == null)
            {
                return;
            }
            Blueprint_Install bp = InstallBlueprintUtility.ExistingBlueprintFor(copy);
            if (bp == null || bp.Destroyed)
            {
                return;
            }
            if (!(actual is MinifiedThing actualMin))
            {
                return;
            }
            if (SetMiniToInstallMethod != null)
            {
                SetMiniToInstallMethod.Invoke(bp, new object[] { actualMin });
            }
            else
            {
                MiniToInstallField?.SetValue(bp, actualMin);
            }
            // listerBuildings 的 reinstall 注册 key 随引用变化：Deregister 在引用不一致时
            // 内部走遍历兜底移除旧 key，再以新 key（真物的 InnerThing）重新注册。
            if (bp.Map != null)
            {
                bp.Map.listerBuildings.DeregisterInstallBlueprint(bp);
                bp.Map.listerBuildings.RegisterInstallBlueprint(bp);
            }
        }

        /// <summary>原版 internal 设置器（置 miniToInstall 并清空 buildingToReinstall）；反射失败时回退字段写入。</summary>
        private static readonly MethodInfo SetMiniToInstallMethod =
            AccessTools.Method(typeof(Blueprint_Install), "SetThingToInstallFromMinified");

        /// <summary>兜底反射字段（SetThingToInstallFromMinified 不可用时直接写 private 字段）。</summary>
        private static readonly FieldInfo MiniToInstallField =
            AccessTools.Field(typeof(Blueprint_Install), "miniToInstall");
    }
}
