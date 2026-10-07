using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 工作代理的"死亡审计 + 死亡阻断"中枢（档 1 诊断）。
    ///
    /// 代理是建筑的工具而不是"人"，它的生命周期必须完全由代理池掌握。此前唯一的防线只覆盖
    /// <c>Pawn_HealthTracker.PreApplyDamage</c>（伤害管线），而原版与第三方还有大量**不经过伤害管线**
    /// 的致死路径：显式 <c>Pawn.Kill</c>、显式 <c>Pawn_HealthTracker.SetDead</c>、
    /// 以及 <c>CheckForStateChange</c> 里的状态判死（hediff 达到 lethalSeverity、lethal capacity 归零、
    /// 核心部位效率归零）。代理一旦死掉，后续链路上会依次发生：生成 Corpse（<c>Corpse.InnerPawn.IsColonist</c>
    /// 为 true，于是尸体会被 ColonistBar 当成殖民者列进头像栏）→ <c>Destroy(KillFinalize)</c>
    /// → <c>WorldPawns.PassToWorld</c>，而池在下一个 60 tick 维护周期发现 Destroyed 就补建新代理
    /// —— 结果就是玩家看到的"不断死亡、不断重生、头像栏堆满尸体"。
    ///
    /// 本文件只负责**统计与留痕**，具体的拦截补丁在同文件下方的几个 Harmony 类里。
    /// 拦截动作本身只做"记录 + 登记"，绝不就地改动 Job 或地图集合：<c>Pawn.Kill</c> 与 <c>MakeDowned</c>
    /// 常常在 Toil / JobDriver / HediffComp.Tick 的调用栈里被触发，就地 DeSpawn 会让后续 Toil
    /// 读到 null 的 <c>pawn.MapHeld</c>（项目此前已踩过这个坑）。真正的状态清理统一交回
    /// <c>MapComponent_OmniWorkstation</c> 的 tick 栈执行（RequestDeathGuardRecovery）。
    /// </summary>
    internal static class OmniWorkProxyDeathGuard
    {
        /// <summary>被拦截的 Pawn.Kill 次数（含同一代理在致死条件下每 60 tick 的重复触发）。</summary>
        internal static int BlockedKillCount;
        /// <summary>被拦截的 Pawn_HealthTracker.SetDead 次数。</summary>
        internal static int BlockedSetDeadCount;
        /// <summary>被拦截的倒地次数。</summary>
        internal static int BlockedDownedCount;
        /// <summary>被第三方直接销毁（不是死亡）的次数。</summary>
        internal static int ExternalDestroyCount;
        /// <summary>被拦下的真空暴露 hediff 添加次数（Odyssey）。</summary>
        internal static int BlockedVacuumExposureCount;
        /// <summary>最近一次异常事件的成因摘要，供状态窗口与日志显示。</summary>
        internal static string LastReason = "-";

        // 日志限流：代理在致死条件下（例如真空暴露）每 60 tick 就会被判死一次，没有限流会把
        // Player.log 刷爆。统计仍然实时累加，只是日志按时间窗节流。
        private const int LogThrottleTicks = 300;
        private const int MaxReportedFrames = 12;
        private static int lastLoggedTick = -1;

        internal static void ResetStats()
        {
            BlockedKillCount = 0;
            BlockedSetDeadCount = 0;
            BlockedDownedCount = 0;
            ExternalDestroyCount = 0;
            BlockedVacuumExposureCount = 0;
            LastReason = "-";
            lastLoggedTick = -1;
        }

        internal static void NotifyBlockedKill(Pawn pawn, DamageInfo? dinfo, Hediff culprit)
        {
            if (pawn == null) return;
            BlockedKillCount++;
            LastReason = BuildReason("Pawn.Kill", pawn, dinfo, culprit);
            Report("blocked a death attempt on work proxy", pawn, dinfo, culprit);
            // 必须清掉致死来源（例如真空暴露 hediff），否则每个 tick 都会再次触发判死。
            RegisterRecovery(pawn);
        }

        internal static void NotifyBlockedSetDead(Pawn pawn)
        {
            if (pawn == null) return;
            BlockedSetDeadCount++;
            LastReason = BuildReason("Pawn_HealthTracker.SetDead", pawn, null, null);
            Report("blocked a SetDead on work proxy", pawn, null, null);
            RegisterRecovery(pawn);
        }

        internal static void NotifyBlockedDowned(Pawn pawn, DamageInfo? dinfo, Hediff hediff)
        {
            if (pawn == null) return;
            BlockedDownedCount++;
            LastReason = BuildReason("Pawn_HealthTracker.MakeDowned", pawn, dinfo, hediff);
            // 倒地不触发软回收：倒地可能只是瞬时状态，持续倒地由维护周期的
            // ReclaimStuckDownedProxies 在超时后统一收进休眠舱。
            Report("blocked a downed state on work proxy", pawn, dinfo, hediff);
        }

        internal static void NotifyExternalDestroy(Pawn pawn, DestroyMode mode)
        {
            if (pawn == null) return;
            ExternalDestroyCount++;
            LastReason = "Pawn.Destroy(" + mode + ")";
            Report("work proxy was destroyed externally", pawn, null, null);
        }

        /// <summary>记录一次"代理被拒绝获得真空暴露 hediff"（Odyssey）。</summary>
        internal static void NotifyBlockedVacuumExposure(Pawn pawn)
        {
            if (pawn == null) return;
            BlockedVacuumExposureCount++;
            LastReason = "VacuumExposure blocked (Pawn_HealthTracker.AddHediff)";
            Report("blocked a VacuumExposure hediff on work proxy", pawn, null, null);
        }

        /// <summary>登记一次"延迟软回收"：由归属图的池在下一 tick 清理状态并收回休眠舱。</summary>
        private static void RegisterRecovery(Pawn pawn)
        {
            MapComponent_OmniWorkstation manager = GameComponent_OmniWorkProxyRegistry.ManagerOf(pawn);
            manager?.RequestDeathGuardRecovery(pawn);
        }

        // ── 诊断输出 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 记录一次异常事件：日志按时间窗节流，统计不受影响。
        /// 只在拦截路径上调用，因此这里可以承受一次栈回溯的开销。
        /// </summary>
        private static void Report(string headline, Pawn pawn, DamageInfo? dinfo, Hediff culprit)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (lastLoggedTick >= 0 && tick - lastLoggedTick < LogThrottleTicks) return;
            lastLoggedTick = tick;

            StringBuilder sb = new StringBuilder(256);
            sb.Append("[OmniWorkstation] ").Append(headline).Append(' ').Append(Describe(pawn));
            sb.Append(" (tick=").Append(tick).Append(", kill=").Append(BlockedKillCount)
                .Append(", setDead=").Append(BlockedSetDeadCount)
                .Append(", downed=").Append(BlockedDownedCount)
                .Append(", destroyed=").Append(ExternalDestroyCount).Append(')');
            sb.Append('\n').Append("  reason: ").Append(BuildReason(headline, pawn, dinfo, culprit));
            sb.Append('\n').Append("  lethalHediffs: ").Append(CollectLethalHediffs(pawn));
            sb.Append('\n').Append("  stack (first non-mod frames):");
            AppendStack(sb);
            Log.Warning(sb.ToString());
        }

        private static string BuildReason(string source, Pawn pawn, DamageInfo? dinfo, Hediff culprit)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append("source=").Append(source);
            if (dinfo.HasValue)
            {
                DamageInfo info = dinfo.Value;
                sb.Append(", damage=").Append(info.Def == null ? "?" : info.Def.defName)
                    .Append(", amount=").Append(info.Amount.ToString("F1", CultureInfo.InvariantCulture));
                if (info.Instigator != null)
                    sb.Append(", instigator=").Append(SafeLabel(info.Instigator));
            }
            if (culprit != null)
                sb.Append(", culprit=").Append(culprit.def == null ? "?" : culprit.def.defName);
            if (pawn != null && pawn.health != null && pawn.health.State != PawnHealthState.Mobile)
                sb.Append(", healthState=").Append(pawn.health.State);
            return sb.ToString();
        }

        /// <summary>列出代理身上所有"现在就能致死"的 hediff（lethalSeverity 或 lifeThreatening 阶段）。</summary>
        private static string CollectLethalHediffs(Pawn pawn)
        {
            List<Hediff> hediffs = pawn?.health?.hediffSet?.hediffs;
            if (hediffs == null || hediffs.Count == 0) return "-";
            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (hediff == null) continue;
                if (!hediff.IsLethal && !hediff.IsAnyStageLifeThreatening()) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(hediff.def == null ? "?" : hediff.def.defName)
                    .Append('=').Append(hediff.Severity.ToString("F2", CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        /// <summary>
        /// 追加调用栈。跳过本 mod 自己的帧（补丁桩与审计函数），这样第一条外部帧就是
        /// "谁在杀代理"的直接证据：原版内部判死会指向 Pawn_HealthTracker 系列，
        /// 第三方则会直接指向它自己的程序集。
        /// </summary>
        private static void AppendStack(StringBuilder sb)
        {
            StackTrace trace;
            try { trace = new StackTrace(1, false); }
            catch (Exception) { sb.Append("\n    <unavailable>"); return; }

            int reported = 0;
            for (int i = 0; i < trace.FrameCount && reported < MaxReportedFrames; i++)
            {
                System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();
                if (method == null) continue;
                Type declaring = method.DeclaringType;
                string ns = declaring == null ? null : declaring.Namespace;
                if (ns != null && ns.StartsWith("FullyAutomaticOmniCrafter", StringComparison.Ordinal))
                    continue;
                sb.Append("\n    at ");
                sb.Append(declaring == null ? "?" : declaring.FullName);
                sb.Append('.').Append(method.Name);
                reported++;
            }
            if (reported == 0) sb.Append("\n    <no external frame>");
        }

        private static string Describe(Pawn pawn)
        {
            try { return pawn.LabelShortCap + "/#" + pawn.thingIDNumber; }
            catch (Exception) { return "#" + (pawn == null ? -1 : pawn.thingIDNumber); }
        }

        private static string SafeLabel(Thing thing)
        {
            try { return thing.LabelShortCap; }
            catch (Exception) { return thing?.def?.defName ?? "?"; }
        }
    }

    /// <summary>
    /// 档 3：在死亡动作的汇聚点上拦截。
    ///
    /// <c>Pawn.Kill</c> 是原版与第三方"处死一个 Pawn"的唯一公开汇聚点（<c>Verse/Pawn.cs</c>）；
    /// 它内部会做大量不可撤销的副作用（DoKillSideEffects、health.SetDead、MakeCorpse、
    /// DoKillSideEffects 里的死亡通知、Faction.Notify_MemberDied、最后 base.Kill → Destroy(KillFinalize)
    /// → WorldPawns.PassToWorld）。前缀直接返回 false 则这些副作用**一次都不会发生**，
    /// 比"死后再复活"干净得多（复活无法撤销已经发出的死亡信件、殖民者心情惩罚与 Tale）。
    ///
    /// 豁免通道：<c>OmniWorkProxyUtility.BeginAllowDeath</c>。本 mod 自己的"主动处死"入口
    /// （万能杀手、物质能量转换器）必须包一层豁免，否则玩家显式操作会被这里挡住。
    /// 代理池的强删/重建走的是 <c>Destroy(Vanish)</c>，不经过 Kill，因此不受影响。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill), new Type[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class Patch_OmniWorkProxy_BlockDeath
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            if (OmniWorkProxyUtility.AllowsDeath(__instance)) return true;
            OmniWorkProxyDeathGuard.NotifyBlockedKill(__instance, dinfo, exactCulprit);
            return false;
        }
    }

    /// <summary>
    /// 档 3：拦住直接置死。
    ///
    /// <c>Pawn_HealthTracker.SetDead</c> 是 public 的，第三方可以通过它绕过 Pawn.Kill 一步
    /// 把 pawn 置成 Dead（此时既没有尸体、也不必然被销毁）。这条路若放行，池里会留下
    /// "已死但未销毁"的僵尸记录长期占位（EnsureProxyCount 现在也把 Dead 视为失效，
    /// 属于第二道兜底）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.SetDead))]
    internal static class Patch_OmniWorkProxy_BlockSetDead
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            if (OmniWorkProxyUtility.AllowsDeath(___pawn)) return true;
            OmniWorkProxyDeathGuard.NotifyBlockedSetDead(___pawn);
            return false;
        }
    }

    /// <summary>
    /// 阻止代理进入倒地（Downed）状态。
    ///
    /// <c>Pawn_HealthTracker.MakeDowned</c> 是 <c>healthState = Down</c> 的唯一写入点。
    /// 代理一旦倒地会连带触发 <c>DropAndForbidEverything</c>、<c>ClearMind</c>、
    /// <c>Lord.Notify_PawnLost(Incapped)</c> 等一串"人"的语义，而且它不会被原版的救援/医疗
    /// 流程照顾（代理不在 colony pawn 列表里），结果是槽位被永久占住。真空暴露的 extreme 阶段
    /// （Consciousness setMax 0.10）、麻醉、昏迷类 hediff 都会走到这里，因此直接在入口拦下。
    ///
    /// 读档带回的 Down 状态与第三方直写 healthState 由维护周期的
    /// <c>ReclaimStuckDownedProxies</c> + <c>OmniWorkProxyUtility.ClearDownedState</c> 兜底。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    internal static class Patch_OmniWorkProxy_BlockDowned
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn ___pawn, DamageInfo? __0, Hediff __1)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            if (___pawn.Destroyed || ___pawn.health == null) return true;
            OmniWorkProxyDeathGuard.NotifyBlockedDowned(___pawn, __0, __1);
            return false;
        }
    }

    /// <summary>
    /// 修法 2：让代理在真空判定上等同于"真空免疫"。
    ///
    /// Odyssey 的真空伤害有两条路径，都先看 <c>Pawn.HarmedByVacuum</c>：真空灼伤
    /// （<c>HediffGiver_VacuumBurn</c> → TakeDamage，已被伤害吸收挡住）与真空暴露累积
    /// （<c>VacuumUtility.PawnVacuumTickInterval</c> → HealthUtility.AdjustSeverity，**完全绕过伤害管线**）。
    /// 这里直接对代理返回 false，等于把两条路径一起关掉。
    ///
    /// 注意：本补丁不会让**已积累**的 VacuumExposure 消退（消退判定看 StatDefOf.VacuumResistance，
    /// 不看这个属性），因此必须与 Defs 中 <c>FAOC_OmniWorkProxyBoost</c> 的
    /// <c>&lt;VacuumResistance MayRequire="Ludeon.RimWorld.Odyssey"&gt;1&lt;/VacuumResistance&gt;</c>
    /// （修法 1）配合使用；两者都做了，代理既不受新伤害，已有暴露也会正常消退。
    /// ConcernedByVacuum 一并关掉，避免代理因为"担心真空"而拒绝使用真空中的目标。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.HarmedByVacuum), MethodType.Getter)]
    internal static class Patch_OmniWorkProxy_VacuumImmunity
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ConcernedByVacuum), MethodType.Getter)]
    internal static class Patch_OmniWorkProxy_VacuumConcern
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 档 3 兜底 + 档 1 留痕：代理被第三方直接销毁（不是死亡）时记录下来。
    ///
    /// 这条路径不生成尸体、也不触发死亡通知，症状是"代理凭空消失、池随后补建"。
    /// 本 mod 自己的管理路径（ForceDestroy / EnsureProxyCount 回收）都会先
    /// <c>registry.Unregister</c>，因此这里用"仍在登记表中"区分外部销毁，避免自报噪音。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
    internal static class Patch_OmniWorkProxy_TrackExternalDestroy
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance, DestroyMode mode)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return;
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry == null || !registry.IsRegistered(__instance)) return;
            OmniWorkProxyDeathGuard.NotifyExternalDestroy(__instance, mode);
        }
    }

    /// <summary>
    /// 代理不产生任何面向玩家的通知。
    ///
    /// <c>PawnUtility.ShouldSendNotificationAbout</c> 是"要不要给玩家发消息/信件"的唯一开关，
    /// 代理是 <c>Faction.OfPlayer</c> 的 humanlike，因此本会被判为 true：死亡会发"XX 已死亡"信件、
    /// 解除倒地会发"不再倒地"、hediff 变化会发提示。这些内容对"工具"没有意义，而且会在
    /// 反复的事件里刷屏。该判定只影响消息/信件/提示，不影响任何功能逻辑。
    /// </summary>
    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.ShouldSendNotificationAbout))]
    internal static class Patch_OmniWorkProxy_NoPlayerNotification
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn p, ref bool __result)
        {
            if (__result && OmniWorkProxyUtility.IsProxy(p)) __result = false;
        }
    }

    /// <summary>
    /// 第三层 A：拒绝代理获得真空暴露 hediff —— 覆盖**任意来源**。
    ///
    /// 前两层（Pawn.HarmedByVacuum 返回 false、VacuumResistance = 1）都只在"原版真空系统"的
    /// 判定点上生效：第三方 mod 直接 AddHediff(HediffDefOf.VacuumExposure)、或原版以后换一种
    /// 累积方式时，那两层都不参与判定。
    ///
    /// 只拦"添加"就够的原因：原版 HealthUtility.AdjustSeverity（Verse/HealthUtility.cs:499）
    /// 先找已有实例，找不到才 HediffMaker.MakeHediff + AddHediff。代理永远拿不到第一个实例，
    /// 之后每次累积都会回到"不存在"分支再被这里拦下，净效果就是永不生效。
    ///
    /// 性能：Pawn_HealthTracker.AddHediff 是所有 Pawn 的所有 hediff 添加的共同路径，
    /// 因此判序先做一次 def 引用比较、命中后才判 IsProxy。**此处不得再加入任何重活。**
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
        new Type[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    internal static class Patch_OmniWorkProxy_BlockVacuumExposure
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn ___pawn, Hediff hediff)
        {
            HediffDef vacuumExposure = HediffDefOf.VacuumExposure;
            // 未装 Odyssey 时该字段为 null；hediff 或 def 为 null 时同样放行（比较天然不成立）。
            if (vacuumExposure == null || hediff?.def != vacuumExposure) return true;
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            OmniWorkProxyDeathGuard.NotifyBlockedVacuumExposure(___pawn);
            return false;
        }
    }

    /// <summary>
    /// 第三层 C：万一代理身上真的存在 VacuumExposure（A 生效前积累的、读档带回来的，或第三方
    /// 绕过 AddHediff 直写 hediffSet 的），让它以固定速率快速消退，不再依赖"是否处于真空"。
    ///
    /// 必须用 postfix：原版 CompPostTickInterval 在真空里会把调整量设为 0（即不消退），
    /// 前缀里的修改会被它覆盖。这里每 60 tick 恒定给一个强负值（-1/秒），
    /// 所以"边加边退"的净效果也永远是下降。
    ///
    /// 注意本补丁不会让 hediff 自己消失（VacuumExposure 没有 HediffComp_Disappears），
    /// severity 归零后的空实例由维护周期的 PurgeVacuumExposureFromProxies 回收清理。
    /// </summary>
    [HarmonyPatch(typeof(HediffComp_VacuumExposure), nameof(HediffComp_VacuumExposure.CompPostTickInterval))]
    internal static class Patch_OmniWorkProxy_VacuumExposureDecay
    {
        /// <summary>代理身上真空暴露的消退速率（每秒 severity）。原版未暴露时只有 -0.1。</summary>
        private const float ProxyDecayPerSecond = -1f;

        [HarmonyPostfix]
        private static void Postfix(HediffComp_VacuumExposure __instance, ref float severityAdjustment)
        {
            if (!ModsConfig.OdysseyActive) return;
            Pawn pawn = __instance.parent == null ? null : __instance.parent.pawn;
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return;
            severityAdjustment = ProxyDecayPerSecond;
        }
    }
}