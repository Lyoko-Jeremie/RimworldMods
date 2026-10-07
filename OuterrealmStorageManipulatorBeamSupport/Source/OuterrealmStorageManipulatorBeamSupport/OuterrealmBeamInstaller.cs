using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using static OuterrealmStorageManipulatorBeamSupport.OuterrealmBeamBinding;

namespace OuterrealmStorageManipulatorBeamSupport
{
    /// <summary>
    /// 把适配器的 11 个边界安装到牵引光束上。
    ///
    /// 流程改为「先全量核对、再绑定、最后打补丁」：签名与字段/属性两项都只记录问题不中断，
    /// 全部核对完再一次性列出（第三方光束一次改动常拖断多处，只报第一个失败点会误导排查）。
    /// 有任何不兼容即 `UnpatchAll` 整组回滚并写日志，结果是「本适配器失效」而不是
    /// 「游戏或主 mod 损坏」。
    /// </summary>
    internal static class OuterrealmBeamInstaller
    {
        internal static void Install()
        {
            Type op = AccessTools.TypeByName("ManipulatorBeam.IBeamOperator");
            if (op == null) return;
            Harmony harmony = new Harmony(OuterrealmBeamAdapter.PatchId);
            try
            {
                Type utility = RequiredType("BeamManipulatorUtility");
                Type building = RequiredType("Building_BeamManipulator");
                Type transfer = RequiredType("BeamTransfer");
                Type claims = RequiredType("BeamClaimUtility");
                Type channel = RequiredType("BeamChannelRuntime");
                Type set = typeof(HashSet<IntVec3>);
                List<string> problems = new List<string>();
                MethodInfo candidate = Require(problems, utility, "CanBeamTransferThing", true, typeof(bool),
                    new[] { "op", "thing", "ownerKey" }, op, typeof(Thing), typeof(int));
                // 普通储区目的地搜索。原公开入口 TryFindStorageDestinationFor 已被光束 1.1.0 的
                // 性能重构收进私有 TryFindBestStorageCellCore——普通搬运、探针补给、在途改道
                // 全部经由这一处，因此改绑到这里。thing 是本次搜索的源物品（探针路径下是探针），
                // 「源是 vault 物品」的判定语义与原入口一致。
                MethodInfo destination = Require(problems, utility, "TryFindBestStorageCellCore", true, typeof(bool),
                    new[] { "map", "thing", "referenceCell", "excludedDestinations", "destination" },
                    typeof(Map), typeof(Thing), typeof(IntVec3), set, typeof(IntVec3).MakeByRefType());
                MethodInfo enqueue = Require(problems, utility, "TryClaimAndEnqueue", true, typeof(bool),
                    new[] { "transfer", "destinationQueue", "excludedThings", "ownerKey" },
                    transfer, typeof(List<>).MakeGenericType(transfer), typeof(HashSet<Thing>), typeof(int));
                MethodInfo take = Require(problems, building, "TryLiftForTransfer", false, typeof(bool),
                    new[] { "op", "transfer", "carriedThing" }, op, transfer, typeof(Thing).MakeByRefType());
                MethodInfo extract = Require(problems, building, "ExtractThingForTransfer", true, typeof(Thing), new[] { "transfer" }, transfer);
                MethodInfo release = Require(problems, claims, "ReleaseClaim", true, typeof(void), new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo clear = Require(problems, claims, "ReleaseAllClaimsForOwner", true, typeof(void), new[] { "map", "ownerKey" }, typeof(Map), typeof(int));
                MethodInfo claimContainer = Require(problems, claims, "TryClaimDestinationContainer", true, typeof(bool),
                    new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo finish = Require(problems, utility, "FinishTransfer", true, typeof(bool),
                    new[] { "op", "carriedThing", "transfer", "fallbackCell" },
                    op, typeof(Thing), transfer, typeof(IntVec3));
                MethodInfo releaseTransit = Require(problems, building, "ReleaseInTransitThing", false, typeof(void),
                    new[] { "thing" }, typeof(Thing));
                // 通道推进：源投影被主 mod 的空条目清理移除后，光束会中止本次搬运并把在途
                // 物品丢回 vault 格。在它的源检查之前把源引用换成仍在途的实体，搬运才能走完
                // FinishTransfer 正常入库（详见 OuterrealmBeamAdapter.AdvancePrefix）。
                MethodInfo advance = Require(problems, building, "AdvanceChannel", false, typeof(bool),
                    new[] { "op", "index", "channel" }, op, typeof(int), channel);
                // 目的地保护也是协议的一部分，不能只安装源端。
                MethodInfo group = Require(problems, utility, "IsBeamStorageGroupAllowed", true, typeof(bool), new[] { "group" }, typeof(SlotGroup));
                // 字段与属性：改名同样会让适配器失效，和签名一起列出（失败时值保持 null）。
                OuterrealmBeamAdapter.mapOf = Capture(problems, "IBeamOperator.Map", () => Getter<Map>(op, "Map", false));
                OuterrealmBeamAdapter.pawnOf = Capture(problems, "IBeamOperator.Pawn", () => Getter<Pawn>(op, "Pawn", false));
                OuterrealmBeamAdapter.ownerOf = Capture(problems, "IBeamOperator.OwnerKey", () => Getter<int>(op, "OwnerKey", false));
                OuterrealmBeamAdapter.manipulatorOf = Capture(problems, "IBeamOperator.Manipulator", () => Getter<object>(op, "Manipulator", false));
                OuterrealmBeamAdapter.thingOf = Capture(problems, "BeamTransfer.thing", () => Getter<Thing>(transfer, "thing", true));
                OuterrealmBeamAdapter.containerOf = Capture(problems, "BeamTransfer.destinationContainer", () => Getter<Thing>(transfer, "destinationContainer", true));
                OuterrealmBeamAdapter.countOf = Capture(problems, "BeamTransfer.count", () => Getter<int>(transfer, "count", true));
                OuterrealmBeamAdapter.stripOf = Capture(problems, "BeamTransfer.isStripJob", () => Getter<bool>(transfer, "isStripJob", true));
                OuterrealmBeamAdapter.destinationOf = Capture(problems, "BeamTransfer.destination", () => Getter<IntVec3>(transfer, "destination", true));
                OuterrealmBeamAdapter.setCount = Capture(problems, "BeamTransfer.count(set)", () => Setter<int>(transfer, "count"));
                // 目的地「格 → 容器」改写用字段赋值：transfer 对象身份必须保持不变，
                // 因为新版 FillTransferQueue 在构造该 transfer 时已对它做过 claim 登记。
                OuterrealmBeamAdapter.setDestinationContainer = Capture(problems, "BeamTransfer.destinationContainer(set)", () => Setter<Thing>(transfer, "destinationContainer"));
                // 续搬：transfer.thing 用字段赋值，通道取 transfer 与在途实体用字段读取。
                OuterrealmBeamAdapter.setThing = Capture(problems, "BeamTransfer.thing(set)", () => Setter<Thing>(transfer, "thing"));
                OuterrealmBeamAdapter.transferOf = Capture(problems, "BeamChannelRuntime.activeTransfer", () => Getter<object>(channel, "activeTransfer", true));
                OuterrealmBeamAdapter.carriedInTransitOf = Capture(problems, "BeamChannelRuntime.carriedThingInTransit", () => Getter<Thing>(channel, "carriedThingInTransit", true));

                if (problems.Count > 0)
                {
                    throw new InvalidOperationException("ManipulatorBeam protocol mismatch (" + problems.Count + "): "
                        + string.Join("; ", problems.ToArray()));
                }

                // 核对全部通过后才执行委托编译与补丁：任一步失败仍由外层 catch 整组回滚。
                OuterrealmBeamAdapter.releaseClaim = Bind<Action<object, int>>(release);
                ParameterExpression machine = Expression.Parameter(typeof(object), "machine");
                ParameterExpression carried = Expression.Parameter(typeof(Thing), "carried");
                OuterrealmBeamAdapter.releaseInTransit = Expression.Lambda<Action<object, Thing>>(Expression.Call(
                    Expression.Convert(machine, building), releaseTransit, carried), machine, carried).Compile();

                Patch(harmony, candidate, "CandidatePrefix", null, "CandidateFinalizer");
                Patch(harmony, destination, "DestinationPrefix", null, "DestinationFinalizer");
                Patch(harmony, enqueue, "EnqueuePrefix", null, "EnqueueFinalizer");
                Patch(harmony, take, "LiftPrefix", null, "LiftFinalizer");
                Patch(harmony, extract, "ExtractPrefix");
                Patch(harmony, release, null, null, "ReleaseFinalizer");
                Patch(harmony, clear, null, null, "ClearFinalizer");
                Patch(harmony, claimContainer, "ContainerClaimPrefix");
                Patch(harmony, finish, "FinishPrefix");
                Patch(harmony, group, null, "GroupPostfix");
                Patch(harmony, advance, "AdvancePrefix");
                OuterrealmBeamAdapter.enabled = true;
                Log.Message("[OuterrealmStorageManipulatorBeamSupport] IBeamOperator compatibility installed (11 boundaries).");
            }
            catch (Exception error)
            {
                OuterrealmBeamAdapter.enabled = false;
                harmony.UnpatchAll(OuterrealmBeamAdapter.PatchId);
                Log.Error("[OuterrealmStorageManipulatorBeamSupport] IBeamOperator compatibility disabled; installation rolled back. " + error);
            }
        }
    }
}
