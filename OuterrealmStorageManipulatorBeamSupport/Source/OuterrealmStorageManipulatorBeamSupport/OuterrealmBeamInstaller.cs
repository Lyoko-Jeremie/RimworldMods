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
    /// 全部安装成功后才置 `enabled = true`；任一 `Require` 抛出即 `UnpatchAll` 回滚并写日志，
    /// 结果是「本适配器失效」而不是「游戏或主 mod 损坏」。
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
                MethodInfo destination = Require(utility, "TryFindStorageDestinationFor", true, typeof(bool),
                    new[] { "op", "thing", "excludedDestinations", "ownerKey", "destination" },
                    op, typeof(Thing), set, typeof(int), typeof(IntVec3).MakeByRefType());
                MethodInfo candidate = Require(utility, "CanBeamTransferThing", true, typeof(bool),
                    new[] { "op", "thing", "ownerKey" }, op, typeof(Thing), typeof(int));
                MethodInfo enqueue = Require(utility, "TryClaimAndEnqueue", true, typeof(bool),
                    new[] { "transfer", "destinationQueue", "excludedThings", "ownerKey" },
                    transfer, typeof(List<>).MakeGenericType(transfer), typeof(HashSet<Thing>), typeof(int));
                MethodInfo take = Require(building, "TryLiftForTransfer", false, typeof(bool),
                    new[] { "op", "transfer", "carriedThing" }, op, transfer, typeof(Thing).MakeByRefType());
                MethodInfo extract = Require(building, "ExtractThingForTransfer", true, typeof(Thing), new[] { "transfer" }, transfer);
                MethodInfo release = Require(claims, "ReleaseClaim", true, typeof(void), new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo clear = Require(claims, "ReleaseAllClaimsForOwner", true, typeof(void), new[] { "map", "ownerKey" }, typeof(Map), typeof(int));
                MethodInfo claimContainer = Require(claims, "TryClaimDestinationContainer", true, typeof(bool),
                    new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo finish = Require(utility, "FinishTransfer", true, typeof(bool),
                    new[] { "op", "carriedThing", "transfer", "fallbackCell" },
                    op, typeof(Thing), transfer, typeof(IntVec3));
                MethodInfo releaseTransit = Require(building, "ReleaseInTransitThing", false, typeof(void),
                    new[] { "thing" }, typeof(Thing));
                // 通道推进：源投影被主 mod 的空条目清理移除后，光束会中止本次搬运并把在途
                // 物品丢回 vault 格。在它的源检查之前把源引用换成仍在途的实体，搬运才能走完
                // FinishTransfer 正常入库（详见 OuterrealmBeamAdapter.AdvancePrefix）。
                MethodInfo advance = Require(building, "AdvanceChannel", false, typeof(bool),
                    new[] { "op", "index", "channel" }, op, typeof(int), channel);
                // 目的地保护也是协议的一部分，不能只安装源端。
                MethodInfo group = Require(utility, "IsBeamStorageGroupAllowed", true, typeof(bool), new[] { "group" }, typeof(SlotGroup));
                OuterrealmBeamAdapter.mapOf = Getter<Map>(op, "Map", false);
                OuterrealmBeamAdapter.pawnOf = Getter<Pawn>(op, "Pawn", false);
                OuterrealmBeamAdapter.ownerOf = Getter<int>(op, "OwnerKey", false);
                OuterrealmBeamAdapter.manipulatorOf = Getter<object>(op, "Manipulator", false);
                OuterrealmBeamAdapter.thingOf = Getter<Thing>(transfer, "thing", true);
                OuterrealmBeamAdapter.containerOf = Getter<Thing>(transfer, "destinationContainer", true);
                OuterrealmBeamAdapter.countOf = Getter<int>(transfer, "count", true);
                OuterrealmBeamAdapter.stripOf = Getter<bool>(transfer, "isStripJob", true);
                OuterrealmBeamAdapter.destinationOf = Getter<IntVec3>(transfer, "destination", true);
                OuterrealmBeamAdapter.setCount = Setter<int>(transfer, "count");
                // 目的地「格 → 容器」改写用字段赋值：transfer 对象身份必须保持不变，
                // 因为新版 FillTransferQueue 在构造该 transfer 时已对它做过 claim 登记。
                OuterrealmBeamAdapter.setDestinationContainer = Setter<Thing>(transfer, "destinationContainer");
                // 续搬：transfer.thing 用字段赋值，通道取 transfer 与在途实体用字段读取。
                OuterrealmBeamAdapter.setThing = Setter<Thing>(transfer, "thing");
                OuterrealmBeamAdapter.transferOf = Getter<object>(channel, "activeTransfer", true);
                OuterrealmBeamAdapter.carriedInTransitOf = Getter<Thing>(channel, "carriedThingInTransit", true);
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
