using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using Verse;
using Verse.AI;

internal static partial class Program
{
    private static int passed;
    private static GameComponent_OuterrealmStorage Store => GameComponent_OuterrealmStorage.Instance;
    private static OuterrealmBillResourceLedger Ledger => Store.Runtime.Bills;
    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(context + ": expected " + expected + ", got " + actual);
    }
    private static void Check(bool value, string context) { if (!value) throw new Exception(context); }
    private static void Test(string name, Action action)
    {
        GameComponent_OuterrealmStorage.Instance = new GameComponent_OuterrealmStorage();
        OuterrealmSourceResolver.Sources.Clear();
        OuterrealmSourceResolver.ProjectionMarkers.Clear();
        OuterrealmSourceResolver.BeforeCheckout = null;
        SubspaceAccessUtility.Pending.Clear();
        Find.TickManager.TicksGame++;
        OuterrealmBillIngredientSelector.CurrentPawn = null;
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    private static Pawn Pawn()
    {
        var map = new Map(); Store.Maps.Add(map); return new Pawn(map);
    }
    private static Thing Source(Pawn pawn, long stock, out OuterrealmEntry entry, bool canonical = false)
    {
        entry = new OuterrealmEntry { Count = stock, Def = new ThingDef { defName = "resource" } };
        return Alias(pawn, entry, canonical);
    }
    private static Thing Alias(Pawn pawn, OuterrealmEntry entry, bool canonical = false)
    {
        var t = new Thing { def = entry.Def, stackCount = canonical ? (int)entry.Count : (int)Math.Min(entry.Count, entry.Def.stackLimit), Spawned = !canonical, Map = canonical ? null : pawn.Map };
        var source = new OuterrealmSource { QueryThing = t, Entry = entry, Kind = canonical ? OuterrealmSourceKind.SubspaceCanonical : OuterrealmSourceKind.Projection,
            Vault = canonical ? null : new Building_OuterrealmVault { Map = pawn.Map, Spawned = true } };
        OuterrealmSourceResolver.Sources.Add(t, source);
        if (!canonical) OuterrealmSourceResolver.ProjectionMarkers.Add(t);
        return t;
    }
    private static Job Job(Pawn pawn, params (Thing thing, int count)[] ingredients)
    {
        var job = new Job { targetA = new Thing { def = new ThingDef(), Spawned = true, Map = pawn.Map } };
        foreach (var ingredient in ingredients) { job.targetQueueB.Add(ingredient.thing); job.countQueue.Add(ingredient.count); }
        pawn.jobs.curJob = job;
        return job;
    }
    private static void Prepare(Pawn pawn, Job job)
    {
        Check(OuterrealmBillJobUtility.Prepare(new JobDriver_DoBill { pawn = pawn, job = job }, false, out bool result) && result, "prepare");
    }
    private static Bill Recipe(ThingDef def, float count, bool mixing = false)
    {
        var bill = new Bill(); bill.recipe.allowMixingIngredients = mixing;
        var ingredient = new IngredientCount { Count = count }; ingredient.filter.Allowed.Add(def); bill.recipe.ingredients.Add(ingredient); return bill;
    }
    private static bool Choose(Pawn pawn, Bill bill, List<Thing> candidates, out List<ThingCount> chosen)
    {
        chosen = new List<ThingCount>();
        return OuterrealmBillIngredientSelector.Choose(candidates, bill, chosen, default, false, null, pawn);
    }
    private static void Main()
    {
        TradeTests();
        Test("multiple outlets cannot turn 100 into 150", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var alias = Alias(pawn, entry); var canonical = Alias(pawn, entry, true);
            Check(!Choose(pawn, Recipe(t.def, 150), new List<Thing> { t, alias, canonical }, out var chosen), "overcommitted selection");
            Equal(0, chosen.Count, "failed selection cleared"); Equal(75, t.stackCount, "projection unchanged"); Equal(100, canonical.stackCount, "canonical unchanged");
        });
        Test("ground fallback supplies the real deficit", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var ground = new Thing { def = t.def, stackCount = 50 };
            Check(Choose(pawn, Recipe(t.def, 150), new List<Thing> { t, Alias(pawn, entry), ground }, out var chosen), "fallback");
            int total = 0; foreach (var selected in chosen) total += selected.Count; Equal(150, total, "total chosen");
        });
        Test("mixing slots share remaining budget", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var bill = Recipe(t.def, 60, true);
            bill.recipe.ingredients.Add(bill.recipe.ingredients[0]);
            Check(!Choose(pawn, bill, new List<Thing> { t, Alias(pawn, entry) }, out _), "mixing reused stock");
        });
        Test("nutrition units and alternative definitions", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 10, out _); var otherDef = new ThingDef { defName = "other" };
            var other = new Thing { def = otherDef, stackCount = 20 }; var bill = Recipe(t.def, 10, true);
            bill.recipe.ingredients[0].filter.Allowed.Add(otherDef); bill.recipe.IngredientValueGetter.Values[t.def] = .5f;
            bill.recipe.IngredientValueGetter.Values[otherDef] = .25f;
            Check(Choose(pawn, bill, new List<Thing> { t, other }, out var chosen), "nutritional mix");
            float value = 0; foreach (var c in chosen) value += c.Count * bill.recipe.IngredientValueGetter.ValuePerUnitOf(c.Thing.def);
            Equal(10f, value, "nutrition");
        });
        Test("two maps cannot reserve 120 out of 100", () =>
        {
            var p1 = Pawn(); var t = Source(p1, 100, out var entry); var j1 = Job(p1, (t, 60)); Prepare(p1, j1);
            var p2 = Pawn(); var j2 = Job(p2, (Alias(p2, entry), 60));
            Check(OuterrealmBillJobUtility.Prepare(new JobDriver_DoBill { pawn = p2, job = j2 }, false, out bool result) && !result, "second reservation succeeded");
            Equal(60L, Store.ReservedCountOf(entry), "surviving reservation");
        });
        Test("repeated reserve is idempotent and display independent", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 1000, out var entry); var job = Job(pawn, (t, 100)); Prepare(pawn, job);
            t.stackCount = 1000; Prepare(pawn, job); Equal(100L, Store.ReservedCountOf(entry), "boosted reserve");
            t.stackCount = 75; Equal(100L, Store.ReservedCountOf(entry), "unboosted reserve");
        });
        Test("100 requirement survives 60 and 40 carry trips", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 1000, out var entry); var job = Job(pawn, (t, 100)); Prepare(pawn, job); var toil = new Toil { actor = pawn };
            Check(OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true), "extract"); Equal(100, job.count, "uncapped need");
            Check(OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false), "carry");
            Equal(940L, entry.Count, "stock after first trip"); Equal(40L, Ledger.Own(job, entry), "remaining claim");
            Equal(40, job.countQueue[0], "queued remainder"); Equal(75, t.stackCount, "no boost");
            pawn.carryTracker.CarriedThing = null;
            OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true); OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false);
            Equal(900L, entry.Count, "stock after second trip"); Equal(0L, Ledger.Own(job, entry), "claim consumed"); Equal(0, job.targetQueueB.Count, "queue drained");
        });
        Test("duplicate queue slots remain independent", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 40), (t, 60)); Prepare(pawn, job);
            var toil = new Toil { actor = pawn }; OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true);
            OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false);
            Check(job.targetQueueB[0].Thing == t, "future reference replaced"); Check(job.targetB.Thing != t, "current reference not migrated"); Equal(60L, Ledger.Own(job, entry), "remaining duplicate claim");
        });
        Test("same-type middle-queue pickup bypasses extract safely", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 40), (t, 60)); Prepare(pawn, job);
            var toil = new Toil { actor = pawn }; OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true);
            OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false);
            // 模拟原版 JumpToCollectNextIntoHandsForBill 已经从队列预扣本次追加量。
            job.targetB = t; job.count = 20; job.countQueue[0] -= 20;
            OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false);
            Equal(60, pawn.carryTracker.CarriedThing.stackCount, "combined carry");
            Equal(40, job.countQueue[0], "middle-queue remainder"); Equal(40L, Ledger.Own(job, entry), "middle-queue claim");
        });
        Test("fifteen ingredient slots each carry exact requirements", () =>
        {
            var pawn = Pawn(); var inputs = new (Thing, int)[15]; var entries = new OuterrealmEntry[15];
            for (int i = 0; i < 15; i++) inputs[i] = (Source(pawn, 200, out entries[i]), 76 + i);
            var job = Job(pawn, inputs); Prepare(pawn, job); var toil = new Toil { actor = pawn };
            int trips = 0;
            while (job.targetQueueB.Count > 0 && trips++ < 100)
            {
                OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true);
                OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, false);
                Check(pawn.CurJob == job, "job restarted"); pawn.carryTracker.CarriedThing = null;
            }
            Equal(30, trips, "number of trips");
            for (int i = 0; i < 15; i++) { Equal(124L - i, entries[i].Count, "ingredient quantity"); Equal(0L, Ledger.Own(job, entries[i]), "ingredient claim drained"); }
        });
        foreach (Acceptance mode in new[] { Acceptance.None, Acceptance.Partial, Acceptance.ThrowBefore, Acceptance.ThrowAfter })
        {
            Test("ownership rollback: " + mode, () =>
            {
                var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 60)); Prepare(pawn, job);
                OuterrealmBillJobUtility.Extract(new Toil { actor = pawn }, TargetIndex.B, true);
                pawn.carryTracker.innerContainer.Mode = mode;
                try { pawn.carryTracker.TryStartCarry(t, 60, false); } catch (InvalidOperationException) { }
                int held = pawn.carryTracker.CarriedThing?.stackCount ?? 0;
                Equal(100L, entry.Count + held, "stock conservation"); Equal(60L - held, Ledger.Own(job, entry), "claim settlement");
                Check(!Ledger.IsTransferring(entry), "transaction leaked");
            });
        }
        Test("checkout callback cannot allocate released intermediate quantity", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 60)); Prepare(pawn, job);
            OuterrealmBillJobUtility.Extract(new Toil { actor = pawn }, TargetIndex.B, true);
            OuterrealmSourceResolver.BeforeCheckout = () => Equal(entry.Count, Store.ReservedCountOf(entry), "in-flight availability");
            pawn.carryTracker.TryStartCarry(t, 60, false); Equal(40L, entry.Count, "post checkout");
        });
        Test("subspace duplicate candidates become distinct real stacks", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry, true); var job = Job(pawn, (t, 40), (t, 60)); Prepare(pawn, job);
            Check(job.targetQueueB[0].Thing != job.targetQueueB[1].Thing, "duplicated physical target");
            Equal(40, job.targetQueueB[0].Thing.stackCount, "first checkout"); Equal(60, job.targetQueueB[1].Thing.stackCount, "second checkout");
            Equal(0L, entry.Count, "canonical fully checked out");
            pawn.Map.reservationManager.ReleaseClaimedBy(pawn, job); Equal(100L, entry.Count, "pending rollback");
        });
        Test("active job reconstructs its outstanding claim", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 40)); job.targetB = t; job.count = 60;
            Check(OuterrealmBillJobUtility.EnsurePlan(pawn, job), "load recovery"); Equal(100L, Ledger.Own(job, entry), "current plus queue");
        });
        Test("frozen or expired projection fails without ordinary fallback", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out _); var job = Job(pawn, (t, 60)); Prepare(pawn, job);
            OuterrealmSourceResolver.Sources[t].Vault.Frozen = true;
            Check(OuterrealmBillJobUtility.Extract(new Toil { actor = pawn }, TargetIndex.B, true), "not handled");
            Check(pawn.CurJob == null, "frozen job survived"); Check(Ledger.IsBlocked(pawn, job.bill), "retry guard");
            Find.TickManager.TicksGame++; Check(!Ledger.IsBlocked(pawn, job.bill), "permanent retry block");
        });
        Test("map removal releases claims", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 60)); Prepare(pawn, job);
            Ledger.ReleaseMap(pawn.Map); Equal(0L, Ledger.Own(job, entry), "map claim released");
        });
        Test("take-for-use permission is revalidated", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out _); var source = OuterrealmSourceResolver.Sources[t];
            source.Vault.NoWithdraw = true;
            Check(!OuterrealmBillJobUtility.CanUse(source, pawn), "withdraw prohibition ignored");
            source.Vault.AllowTakeForUse = true;
            Check(OuterrealmBillJobUtility.CanUse(source, pawn), "take-for-use not allowed");
        });
        Test("quantity arithmetic does not overflow int", () =>
        {
            var budget = new OuterrealmQuantityBudget<string>(); budget.Add("ore", (long)int.MaxValue + 100);
            Check(budget.TrySpend("ore", int.MaxValue), "large spend"); Equal(100L, budget.Get("ore"), "long remainder");
            Check(!budget.TrySpend("ore", 101), "overspend allowed");
        });
        Test("biosculpter ingredients retain per-slot multi-trip demand", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 180, out var entry); var job = Job(pawn, (t, 100), (t, 80));
            job.def = RimWorld.JobDefOf.EnterBiosculpterPod; job.bill = null;
            Check(OuterrealmBillJobUtility.Prepare(new JobDriver_EnterBiosculpterPod { pawn = pawn, job = job }, false, out bool ready) && ready, "pod reserve");
            int collected = 0;
            while (job.targetQueueB.Count > 0)
            {
                var toil = new Toil { actor = pawn };
                Check(OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true), "pod extract");
                Check(OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, true, false, true, true), "pod carry");
                collected += pawn.carryTracker.CarriedThing.stackCount; pawn.carryTracker.CarriedThing = null;
            }
            Equal(180, collected, "pod delivered"); Equal(0L, entry.Count, "pod stock"); Equal(0L, Ledger.Own(job, entry), "pod claim");
        });
        Test("total-count jobs are not treated as ingredient queues", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out _); var job = Job(pawn, (t, 80)); job.def = new JobDef(); job.countQueue = null;
            Check(!OuterrealmBillJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, false, out _), "unknown driver intercepted");
            Check(!OuterrealmBillJobUtility.Extract(new Toil { actor = pawn }, TargetIndex.B, true), "unknown queue intercepted");
        });
        foreach (var mode in new[] { Acceptance.All, Acceptance.None, Acceptance.Partial, Acceptance.ThrowBefore, Acceptance.ThrowAfter })
        {
            Test("general carry ownership " + mode, () =>
            {
                var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var source = OuterrealmSourceResolver.Sources[t];
                pawn.carryTracker.innerContainer.Mode = mode;
                int result = -1;
                try { result = OuterrealmCarryTransaction.Transfer(pawn.carryTracker, source, 60); } catch (InvalidOperationException) { }
                int held = pawn.carryTracker.CarriedThing?.stackCount ?? 0;
                Equal(100L, entry.Count + held, "generic conservation");
                if (result >= 0) Equal(held, result, "actual delivery count");
                Equal(75, t.stackCount, "no direct carry boost"); Check(!Ledger.IsTransferring(entry), "generic transfer lock leaked");
            });
        }
        Test("general pickup cannot consume another job budget", () =>
        {
            var maker = Pawn(); var t = Source(maker, 100, out var entry); Prepare(maker, Job(maker, (t, 90)));
            var hauler = Pawn(); var alias = Alias(hauler, entry);
            Equal(10, OuterrealmCarryTransaction.Transfer(hauler.carryTracker, OuterrealmSourceResolver.Sources[alias], 60), "protected claim");
            Equal(90L, entry.Count, "reserved stock retained");
        });
        Test("general pickup includes its own raw reservation and rejects reentry", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn); job.def = new JobDef();
            pawn.Reserve(t, job, stackCount: 60);
            var source = OuterrealmSourceResolver.Sources[t];
            OuterrealmSourceResolver.BeforeCheckout = () => Equal(0, OuterrealmCarryTransaction.Transfer(pawn.carryTracker, source, 60), "reentrant carry");
            Equal(60, OuterrealmCarryTransaction.Transfer(pawn.carryTracker, source, 60), "own reservation counted twice");
        });
        Test("transport maximum and preview deduplicate entries without boost", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 500, out var entry); var ground = new Thing { def = t.def, stackCount = 50 };
            var list = new List<Thing> { t, Alias(pawn, entry), ground, t, ground };
            Equal(550, OuterrealmTransferQuantities.Maximum(list), "transport maximum");
            int selected = 0;
            Check(!Patch_OuterrealmTransferPreview.Prefix(list, 530, (thing, count) => selected += count, false, false), "preview hook");
            Equal(530, selected, "mass/rot preview quantities"); Equal(75, t.stackCount, "preview mutated count");
            Equal(500L, entry.Count, "preview took stock");
            Check(Patch_OuterrealmTransferPreview.Prefix(list, 530, (thing, count) => { throw new Exception("not a preview"); }, true, true), "ownership callback intercepted");
        });
        Test("retired projections and enormous transport counts", () =>
        {
            var pawn = Pawn(); var retired = Source(pawn, 100, out _); OuterrealmSourceResolver.Sources.Remove(retired);
            Equal(0, OuterrealmTransferQuantities.Maximum(new List<Thing> { retired }), "retired quantity");
            var a = Source(pawn, long.MaxValue, out _); var b = Source(pawn, long.MaxValue, out _);
            Equal(int.MaxValue, OuterrealmTransferQuantities.Maximum(new List<Thing> { a, b }), "saturating maximum");
        });
        Test("trade preview recognizes separately registered projections", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 500, out var entry); OuterrealmSourceResolver.Sources.Remove(t);
            OuterrealmTradeSourceRegistry.Entries[t] = entry;
            int count = 0;
            Check(!Patch_OuterrealmTransferPreview.Prefix(new List<Thing> { t, t }, 400, (thing, amount) => count += amount, false, false), "trade preview hook");
            Equal(400, count, "trade projection mistaken for orphan");
        });
        foreach (var mode in new[] { Acceptance.All, Acceptance.None, Acceptance.Partial, Acceptance.ThrowBefore, Acceptance.ThrowAfter })
        {
            Test("instant transport ownership " + mode, () =>
            {
                var pawn = Pawn(); var t = Source(pawn, 500, out var entry); var list = new List<Thing> { t, Alias(pawn, entry) };
                pawn.carryTracker.innerContainer.Mode = mode; pawn.carryTracker.Capacity = 500;
                try { Check(!Patch_OuterrealmImmediateTransfer.Prefix(list, 400, (actual, holder) => pawn.carryTracker.innerContainer.TryAdd(actual, true)), "instant hook"); }
                catch (InvalidOperationException) { }
                int held = pawn.carryTracker.CarriedThing?.stackCount ?? 0;
                Equal(500L, entry.Count + held, "instant conservation");
                if (mode == Acceptance.All) Equal(400, held, "instant not capped to stackLimit");
                Check(!Ledger.IsTransferring(entry), "instant transfer lock leaked");
            });
        }
        foreach (var def in new[] { RimWorld.JobDefOf.Reload, RimWorld.JobDefOf.RefuelAtomic })
        {
            Test("total-count multi-source multi-trip " + (def == RimWorld.JobDefOf.Reload ? "reload" : "fuel"), () =>
            {
                var pawn = Pawn(); var a = Source(pawn, 100, out var first); var b = Source(pawn, 100, out var second);
                var job = Job(pawn, (a, 0), (Alias(pawn, first), 0), (b, 0)); job.def = def; job.bill = null; job.countQueue = null;
                Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 180, false, out bool result) && result, "total prepare");
                Equal(2, job.targetQueueB.Count, "alias removed"); Equal(180, job.count, "initial total");
                Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 180, false, out result) && result, "repeat prepare");
                int collected = 0;
                while (job.targetQueueB.Count > 0)
                {
                    int before = job.count; var toil = new Toil { actor = pawn };
                    Check(OuterrealmBillJobUtility.Extract(toil, TargetIndex.B, true), "total extract");
                    Equal(before, job.count, "extract reset global demand");
                    Check(OuterrealmBillJobUtility.StartCarry(toil, TargetIndex.B, false, true, false, false), "total carry");
                    int carried = pawn.carryTracker.CarriedThing.stackCount; collected += carried;
                    Equal(before - carried, job.count, "remaining demand"); pawn.carryTracker.CarriedThing = null;
                }
                Equal(180, collected, "total collection"); Equal(20L, first.Count + second.Count, "total stock");
                Equal(0L, Ledger.Own(job, first) + Ledger.Own(job, second), "claims spent"); Check(job.countQueue == null, "synthesized countQueue");
            });
        }
        Test("total budget rejects aliased shortage and blocks only this target for one tick", () =>
        {
            var pawn = Pawn(); var a = Source(pawn, 100, out var entry); var job = Job(pawn, (a, 0), (Alias(pawn, entry), 0));
            job.def = RimWorld.JobDefOf.Reload; job.bill = null; job.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 150, false, out bool result) && !result, "false supply accepted");
            Equal(0L, Ledger.Own(job, entry), "failed claim leaked"); Check(Ledger.IsTotalBlocked(pawn, job.targetA.Thing), "target retry not blocked");
            Check(!Ledger.IsTotalBlocked(pawn, new Thing()), "other target blocked"); Find.TickManager.TicksGame++;
            Check(!Ledger.IsTotalBlocked(pawn, job.targetA.Thing), "retry blocked forever");
        });
        Test("total budget protects stock across maps", () =>
        {
            var a = Pawn(); var t = Source(a, 200, out var entry); var first = Job(a, (t, 0)); first.def = RimWorld.JobDefOf.Reload; first.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = a, job = first }, 150, false, out bool ok) && ok, "first total reserve");
            var b = Pawn(); var second = Job(b, (Alias(b, entry), 0)); second.def = RimWorld.JobDefOf.RefuelAtomic; second.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = b, job = second }, 100, false, out ok) && !ok, "cross-map oversell");
            Equal(150L, Ledger.Own(first, entry), "first claim changed");
        });
        Test("total recovery excludes delivered current target at next extraction", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 180, out var entry); var job = Job(pawn, (t, 0)); job.def = RimWorld.JobDefOf.RefuelAtomic; job.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 180, false, out bool ok) && ok, "prepare recovery");
            var toil = new Toil { actor = pawn }; OuterrealmTotalJobUtility.Extract(toil, TargetIndex.B); OuterrealmTotalJobUtility.StartCarry(toil, TargetIndex.B, false);
            Equal(120, job.count, "first trip"); Ledger.Release(job);
            Check(OuterrealmTotalJobUtility.Extract(toil, TargetIndex.B), "recover extract");
            Equal(120L, Ledger.Own(job, entry), "carried item reduced future budget"); Equal(120, job.count, "recover total");
        });
        Test("total recovery at carry includes current projection", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 180, out var entry); var job = Job(pawn); job.def = RimWorld.JobDefOf.Reload;
            job.countQueue = null; job.count = 180; job.targetB = t;
            Check(OuterrealmTotalJobUtility.Ensure(pawn, job, true), "current target restore");
            Equal(180L, Ledger.Own(job, entry), "current demand");
        });
        Test("total plan combines ordinary ground and canonical checkout", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry, true); var ground = new Thing { def = t.def, stackCount = 40, Spawned = true, Map = pawn.Map };
            var job = Job(pawn, (ground, 0), (t, 0)); job.def = RimWorld.JobDefOf.RefuelAtomic; job.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 140, false, out bool ok) && ok, "canonical total prepare");
            int collected = 0;
            while (job.targetQueueB.Count > 0)
            {
                var toil = new Toil { actor = pawn }; Check(OuterrealmTotalJobUtility.Extract(toil, TargetIndex.B), "real queue extract");
                Check(OuterrealmTotalJobUtility.StartCarry(toil, TargetIndex.B, false), "real queue carry");
                collected += pawn.carryTracker.CarriedThing.stackCount; pawn.carryTracker.CarriedThing = null;
            }
            Equal(140, collected, "real/canonical multi trip"); Equal(0L, entry.Count, "canonical spent");
        });
        Test("total unique anchor reserves without early physical checkout", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 1, out var entry); var source = OuterrealmSourceResolver.Sources[t];
            source.Kind = OuterrealmSourceKind.IdentityAnchor; OuterrealmSourceResolver.Sources[t] = source;
            var job = Job(pawn, (t, 0)); job.def = RimWorld.JobDefOf.Reload; job.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 1, false, out bool ok) && ok, "anchor prepare");
            Equal(1L, entry.Count, "anchor materialized at reserve"); Equal(1L, Ledger.Own(job, entry), "anchor claim");
            var toil = new Toil { actor = pawn }; OuterrealmTotalJobUtility.Extract(toil, TargetIndex.B); OuterrealmTotalJobUtility.StartCarry(toil, TargetIndex.B, false);
            Equal(0L, entry.Count, "anchor checkout"); Equal(0L, Ledger.Own(job, entry), "anchor claim spent");
        });
        Test("partial total delivery requeues precisely the undelivered demand", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 100, out var entry); var job = Job(pawn, (t, 0)); job.def = RimWorld.JobDefOf.Reload; job.countQueue = null;
            Check(OuterrealmTotalJobUtility.Prepare(new JobDriver { pawn = pawn, job = job }, 100, false, out bool ok) && ok, "partial total prepare");
            pawn.carryTracker.innerContainer.Mode = Acceptance.Partial; var toil = new Toil { actor = pawn };
            OuterrealmTotalJobUtility.Extract(toil, TargetIndex.B); OuterrealmTotalJobUtility.StartCarry(toil, TargetIndex.B, false);
            Equal(70, job.count, "partial job remainder"); Equal(70L, Ledger.Own(job, entry), "partial claim remainder");
            Equal(1, job.targetQueueB.Count, "partial source lost"); Equal(70L, entry.Count, "partial stock");
        });
        Test("product counter counts one entry across lister and haul sources", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 500, out var entry); var alias = Alias(pawn, entry); var holder = new FakeHolder();
            pawn.Map.listerThings.Items.Add(t); holder.Things.Add(t); holder.Things.Add(alias); pawn.Map.haulDestinationManager.AllHaulSourcesListForReading.Add(holder);
            var counter = new RecipeWorkerCounter(); counter.recipe.products.Add(new ThingDefCountClass { thingDef = t.def });
            var bill = new Bill_Production { Map = pawn.Map };
            Check(OuterrealmProductCounter.TryCount(counter, bill, out int count), "slow count intercepted"); Equal(500, count, "entry duplication");
            Equal(75, t.stackCount, "counter boost");
            counter.Filter = thing => thing != t && thing != alias;
            OuterrealmProductCounter.TryCount(counter, bill, out count); Equal(0, count, "vanilla validation ignored");
        });
        Test("product counter respects slot filter equipped option and fast resource path", () =>
        {
            var pawn = Pawn(); var t = Source(pawn, 500, out _); pawn.Map.listerThings.Items.Add(t); pawn.Map.mapPawns.FreeColonistsSpawned.Add(pawn);
            var counter = new RecipeWorkerCounter(); counter.recipe.products.Add(new ThingDefCountClass { thingDef = t.def });
            var holder = new FakeHolder(); holder.Things.Add(new Thing { def = t.def, stackCount = 7 });
            var bill = new Bill_Production { Map = pawn.Map, Slot = holder };
            OuterrealmProductCounter.TryCount(counter, bill, out int count); Equal(7, count, "other storage leaked into slot");
            pawn.inventory.Things.Add(new Thing { def = t.def, stackCount = 3 }); bill.includeEquipped = true;
            OuterrealmProductCounter.TryCount(counter, bill, out count); Equal(10, count, "equipped inventory");
            bill.Slot = null; bill.includeEquipped = false; t.def.CountAsResource = true;
            Check(!OuterrealmProductCounter.TryCount(counter, bill, out count), "resource counter counted twice"); Equal(75, t.stackCount, "fast path boost");
        });
        Console.WriteLine("Passed " + passed + " resource tests (production code with game doubles).");
    }
}
