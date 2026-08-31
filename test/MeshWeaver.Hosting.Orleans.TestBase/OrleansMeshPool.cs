using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Xunit;

[assembly: AssemblyFixture(typeof(MeshWeaver.Hosting.Orleans.Test.OrleansMeshPool))]

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A pool of RUNNING mesh clusters, leased per test class — "we can have a pool of running
/// meshes and then parallelize over this pool" (maintainer, 2026-09-01).
///
/// <para><b>Why.</b> The per-class model boots ~90 silos per run (~300–500 ms each) and needed a
/// whole background-disposal drain to keep 90 teardowns from wedging the runner — while
/// <c>test/xunit.runner.json</c> caps execution at ONE class at a time, so all that isolation
/// paid for parallelism that never happens. A leased cluster is reused by the next class
/// instead of being torn down; the pool grows only as wide as the runner actually runs
/// classes concurrently (take-or-create, never wait — a waiting gate is the hand-woven
/// primitive test/ forbids).</para>
///
/// <para><b>Isolation.</b> By construction where it matters: a lease is EXCLUSIVE (no two
/// classes share an instance concurrently), addresses derive from node paths, and a class that
/// needs pristine cluster-wide state (kills silos, asserts global counters, custom silo
/// config) simply keeps the dedicated-fixture path — <see cref="OrleansSharedTestBase"/> falls
/// back to it automatically for custom fixtures. Client hubs are already cleaned per class
/// (<see cref="SharedOrleansFixture.CleanupClientAsync"/>).</para>
///
/// <para><b>Lifetime.</b> xUnit v3 constructs this assembly fixture once and disposes it after
/// the last class — every pooled cluster is disposed there, through the same
/// <see cref="SharedOrleansFixture.DisposeAsync"/> path a dedicated fixture uses. The static
/// <see cref="Current"/> is a single reference bounded by that lifecycle (the same shape as
/// the disposal drain's registry), never a grow-only collection.</para>
/// </summary>
public sealed class OrleansMeshPool : IAsyncDisposable
{
    /// <summary>The live pool for this test assembly; null outside the fixture's lifetime.</summary>
    public static OrleansMeshPool? Current { get; private set; }

    // Pools are per FIXTURE TYPE: a derived fixture configures its silo differently, and a
    // lease must never hand a class a cluster built by someone else's configurator.
    private readonly ConcurrentDictionary<Type, ConcurrentBag<SharedOrleansFixture>> idleByType = new();
    private int created;

    public OrleansMeshPool() => Current = this;

    /// <summary>Take an idle cluster of this exact fixture type, or create one. Never waits.</summary>
    public async ValueTask<SharedOrleansFixture> LeaseAsync(Func<SharedOrleansFixture> factory)
    {
        var probe = factory();
        var type = probe.GetType();
        if (idleByType.TryGetValue(type, out var bag) && bag.TryTake(out var pooled))
            return pooled;
        await probe.InitializeAsync();
        System.Threading.Interlocked.Increment(ref created);
        return probe;
    }

    /// <summary>Hand a cluster back for the next class. The lease's clients are already cleaned.</summary>
    public void Return(SharedOrleansFixture fixture) =>
        idleByType.GetOrAdd(fixture.GetType(), _ => new ConcurrentBag<SharedOrleansFixture>())
            .Add(fixture);


    public async ValueTask DisposeAsync()
    {
        // The one-line receipt: how many clusters the whole run actually booted. ~90 was the
        // per-class number; the pool's worth IS this line staying small.
        Console.WriteLine($"OrleansMeshPool: {created} cluster(s) booted for the whole assembly");
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orleans-mesh-pool-receipt.txt"),
                $"created={created}");
        }
        catch { /* the console line above is the CI receipt; this file is for local verification */ }
        Current = null;
        foreach (var (_, bag) in idleByType)
            while (bag.TryTake(out var fixture))
                await fixture.DisposeAsync();
    }
}
