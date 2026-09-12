using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using ManipulatorBeam;
using OuterrealmStorageManipulatorBeamSupport;
using Verse;

internal static class Program
{
    private static int passed;

    private static void Check(bool condition, string context)
    {
        if (!condition) throw new Exception("FAIL: " + context);
        passed++;
    }

    private static void Test(string name, Action body)
    {
        Console.WriteLine("· " + name);
        body();
    }

    private static void Main(string[] args)
    {
        // 可选：审核用户提供的真实 ManipulatorBeam.dll 元数据签名。
        if (args.Length > 0) SignatureAudit.Run(args[0]);

        OuterrealmBeamAdapter.Install();

        Test("候选放行：纯查询投影可被光束取用", () =>
        {
            var f = new Fixture();
            f.Vault.HaulSourceEnabledSet(true);
            Check(BeamManipulatorUtility.CanBeamTransferThing(f.Op, f.Query, f.Op.OwnerKey), "投影候选放行");
        });

        Test("入队申请条目级预留并反映到全局可用量", () =>
        {
            var f = new Fixture();
            var transfer = new BeamTransfer(f.Query, f.VaultCell, f.NonVaultCell);
            var queue = new List<BeamTransfer>();
            Check(BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, queue, f.Excluded), "入队成功");
            Check(queue.Count == 1, "队列已接收");
            Check(f.Storage.ReservedCountOf(f.Entry) >= transfer.count, "全局预留已反映");
        });

        Test("目的地格 → 容器改写：vault 格自动改写为容器目的地", () =>
        {
            var f = new Fixture();
            var transfer = new BeamTransfer(f.Query, f.VaultCell, f.VaultCell);
            var queue = new List<BeamTransfer>();
            BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, queue, f.Excluded);
            Check(ReferenceEquals(transfer.destinationContainer, f.Vault), "目的地改写为 vault 容器");
        });

        Test("非 vault 目的地不改写", () =>
        {
            var f = new Fixture();
            var transfer = new BeamTransfer(f.Query, f.VaultCell, f.NonVaultCell);
            BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, new List<BeamTransfer>(), f.Excluded);
            Check(transfer.destinationContainer == null, "普通目的地保持原样");
        });

        Test("实际抓取只 Checkout 一次并释放预留", () =>
        {
            var f = new Fixture();
            var transfer = new BeamTransfer(f.Query, f.VaultCell, f.VaultCell);
            BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, new List<BeamTransfer>(), f.Excluded);
            long before = f.Entry.Count;
            Thing actual = new Building_BeamManipulator().Lift(f.Op, transfer);
            Check(actual != null, "取得实物");
            Check(f.Storage.Checkouts == 1, "只 Checkout 一次");
            Check(f.Entry.Count == before - actual.stackCount, "权威数量已扣减");
            Check(f.Storage.ReservedCountOf(f.Entry) == 0, "预留已释放");
        });

        Test("禁止取出时无候选", () =>
        {
            var f = new Fixture();
            f.Vault.NoWithdraw = true;
            Check(!BeamManipulatorUtility.CanBeamTransferThing(f.Op, f.Query, f.Op.OwnerKey), "禁止取出不放行");
        });

        Test("冻结仓库不能作为目的地", () =>
        {
            var f = new Fixture();
            f.Vault.Frozen = true;
            var group = new RimWorld.SlotGroup { parent = f.Vault };
            Check(!BeamManipulatorUtility.GroupAllowed(group), "冻结目的地被拒");
        });

        Test("禁止存入仓库不能作为目的地", () =>
        {
            var f = new Fixture();
            f.Vault.NoDeposit = true;
            var group = new RimWorld.SlotGroup { parent = f.Vault };
            Check(!BeamManipulatorUtility.GroupAllowed(group), "禁止存入目的地被拒");
        });

        Test("入队失败时预留与 claim 原子回滚", () =>
        {
            var f = new Fixture();
            BeamManipulatorUtility.RejectDestination = true;
            try
            {
                var transfer = new BeamTransfer(f.Query, f.VaultCell, f.VaultCell);
                var queue = new List<BeamTransfer>();
                Check(!BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, queue, f.Excluded), "入队被拒");
                Check(queue.Count == 0 && f.Storage.ReservedCountOf(f.Entry) == 0, "队列与预留已回滚");
                Check(!BeamClaimUtility.Claimed.Contains(transfer), "claim 已释放");
            }
            finally { BeamManipulatorUtility.RejectDestination = false; }
        });

        Test("目的地缩量同步释放额度，且不能扩大", () =>
        {
            var f = new Fixture();
            BeamManipulatorUtility.ShrinkTo = 10;
            try
            {
                var transfer = new BeamTransfer(f.Query, f.VaultCell, f.VaultCell) { count = 25 };
                BeamManipulatorUtility.Enqueue(transfer, f.Op.OwnerKey, new List<BeamTransfer>(), f.Excluded);
                Check(f.Storage.ReservedCountOf(f.Entry) == 10, "缩量后预留同步收缩");
            }
            finally { BeamManipulatorUtility.ShrinkTo = 0; }
        });

        Console.WriteLine("Passed " + passed + " beam compatibility assertions (production adapter with game doubles).");
    }

    /// <summary>测试夹具：一座启用取出的 vault + 一个 100 数量的查询投影条目。</summary>
    private sealed class Fixture
    {
        internal readonly Map Map = new Map();
        internal readonly Building_OuterrealmVault Vault = new Building_OuterrealmVault();
        internal readonly OuterrealmEntry Entry = new OuterrealmEntry { Count = 100 };
        internal readonly Thing Query = new Thing();
        internal readonly Operator Op = new Operator();
        internal readonly HashSet<Thing> Excluded = new HashSet<Thing>();
        internal readonly IntVec3 VaultCell = new IntVec3(0);
        internal readonly IntVec3 NonVaultCell = new IntVec3(5);

        internal GameComponent_OuterrealmStorage Storage => GameComponent_OuterrealmStorage.Instance;

        internal Fixture()
        {
            OuterrealmBeamSupportComponent.Reset();
            Storage.Sources.Clear();
            Storage.VaultsForReading.Clear();
            Storage.Checkouts = 0;
            Storage.Deposits = 0;
            Storage.PawnReserved = 0;
            Map.Vaults.Clear();
            Map.SlotGroups.Clear();
            BeamClaimUtility.Claimed.Clear();
            BeamClaimUtility.ExclusiveContainers.Clear();

            Vault.Spawned = true;
            Vault.Map = Map;
            Vault.Position = VaultCell;
            Map.Vaults[VaultCell] = Vault;
            Map.SlotGroups[VaultCell] = new RimWorld.SlotGroup { parent = Vault };

            Query.Stored = true;
            Query.Spawned = true;
            Query.Map = Map;
            Query.Position = VaultCell;
            Query.stackCount = 100;
            Query.thingIDNumber = 42;

            Storage.Sources[Query] = new OuterrealmSource { QueryThing = Query, Entry = Entry, Vault = Vault };
            Storage.VaultsForReading.Add(Vault);

            Op.Map = Map;
            Op.Pawn = new Pawn { Map = Map, Spawned = true, Position = VaultCell };
            Op.OwnerKey = 1;
        }
    }
}

internal static class VaultTestExtensions
{
    internal static void HaulSourceEnabledSet(this Verse.Thing vault, bool enabled)
    {
        // 替身里 HaulSourceEnabled 由 Spawned/Frozen/NoWithdraw 推导，无需额外设置。
        _ = vault;
        _ = enabled;
    }
}
