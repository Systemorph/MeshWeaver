using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Regression guard for issue #2346 — the shard-0 Orleans "rotating cast" of flakes.
///
/// <para><b>The defect.</b> <see cref="OrleansClusterDisposal"/> backgrounds each test class's
/// <c>TestCluster</c> teardown and registers it so the assembly fixture can await every one of them
/// at the end of the run. It used to register the DRAIN itself, as a connected
/// <c>Replay(1)</c>, in a bag nothing ever removed from. A connected <c>Replay</c> holds its source;
/// the source is a <c>SelectMany</c> chain whose lambdas close over the <c>TestCluster</c> and the
/// Orleans client <c>IHost</c>. So every silo this assembly ever built — ~90 of them, each with its
/// own Autofac container, grain catalog, serializer codecs, mesh hubs, workspaces, seeded MeshNodes
/// and collectible NodeType load contexts — stayed reachable until the process exited, even though
/// its disposal had long since completed.</para>
///
/// <para><b>Why it fails TESTS rather than merely wasting memory.</b> The xUnit test host runs
/// workstation GC (Orleans logs <c>ServerGC=False</c> at every silo start), so a multi-GB gen-2 heap
/// buys multi-second blocking pauses. CI caught Orleans' own health monitor naming them —
/// <c>".NET Thread Pool is exhibiting delays of 1.9080052s"</c> and <c>".NET Runtime Platform
/// stalled for 00:00:03.55 … We are now using a total of 3775MB memory"</c> — and a whole-process
/// stall of that size expires whichever in-test budget is open at that moment. That is the rotating
/// victim: one condition, a different named test every run. Measured on this repo before the fix,
/// RSS across one 124 s run of this assembly climbed strictly monotonically from 86 MB to 4.6 GB
/// with no sawtooth at all — retention, not the working set of the concurrently-running clusters.</para>
///
/// <para><b>What this test pins.</b> Once a backgrounded teardown has SETTLED, nothing the drain
/// captured may still be reachable. It is deterministic: the drain is driven by a
/// <see cref="Subject{T}"/> the test completes itself, so there is no timing, no cluster and no
/// sleep — only "is the captured object collectable once its teardown is done?".</para>
/// </summary>
public class ClusterDisposalRetentionTest
{
    [Fact]
    public void ASettledTeardown_RetainsNothingItCapturedSoItsClusterCanBeCollected()
    {
        // Other test classes are tearing their clusters down concurrently, so the registry is not
        // empty — the invariant is that it RETURNS to its prior depth, not that it is ever zero.
        var before = OrleansClusterDisposal.PendingCount;

        var gate = new Subject<Unit>();
        var captured = Enqueue(gate);

        // Settle the teardown, exactly as the real ordered stop→dispose chain settles when the last
        // leg completes on the I/O pool.
        gate.OnNext(Unit.Default);
        gate.OnCompleted();

        // Deterministic half of the guard: the entry is gone. (The old bag never removed one, so the
        // registry — and everything each entry reached — grew for the whole run.)
        OrleansClusterDisposal.PendingCount.Should().BeLessThanOrEqualTo(
            before,
            "a settled teardown must leave the registry, or it accumulates for the life of the process");

        Collect();

        captured.IsAlive.Should().BeFalse(
            "a teardown that has completed must not keep its TestCluster (here: the object its drain "
            + "captured) reachable — registering the drain itself rooted every silo the assembly ever "
            + "built, and the multi-GB heap that produced is what stalled the process for seconds at a "
            + "time and expired whichever test budget was open (issue #2346)");
    }

    /// <summary>
    /// Registers a drain that captures a fresh object, and hands back only a weak reference to it.
    /// <see cref="MethodImplOptions.NoInlining"/> so the closure's display class is unreachable from
    /// the caller's frame once this returns — otherwise a live stack slot, not the registry, would
    /// decide the outcome.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Enqueue(IObservable<Unit> gate)
    {
        var captured = new object();
        OrleansClusterDisposal.Enqueue(gate.Select(_ =>
        {
            GC.KeepAlive(captured);
            return Unit.Default;
        }));
        return new WeakReference(captured);
    }

    /// <summary>Two forced, blocking, compacting full collections — enough for a plain object with
    /// no finalizer, and no polling.</summary>
    private static void Collect()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
