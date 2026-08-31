using System.Collections.Concurrent;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Base class for Orleans tests. Each test class now spins up its OWN
/// <see cref="SharedOrleansFixture"/> (one cluster per class, not per assembly)
/// — the prior shared-cluster shape (via <c>[Collection(nameof(OrleansClusterCollection))]</c>)
/// suffered from 120 s disposal-wait pile-ups and grain-state leakage across
/// tests in the OrleansClusterCollection. Per-class silos cost ~300-500 ms to
/// boot and give perfect state isolation; the overall suite is faster than
/// the shared-cluster version because the 20-second inter-class transition
/// gaps are gone.
///
/// <para>The compatibility goal: existing test code that reads
/// <c>Fixture.Cluster</c>, <c>Fixture.ClientMesh</c>, etc. still works — the
/// <see cref="Fixture"/> property is per-class now but exposes the same API.</para>
/// </summary>
public abstract class OrleansSharedTestBase : TestBase
{
    protected SharedOrleansFixture Fixture { get; private set; } = null!;

    /// <summary>
    /// Subclass hook: the cluster fixture to build. A repo that ships a module returns its own
    /// derived fixture here (which in turn names its own silo configurator), so the AI rig can
    /// live outside this repo while these ~51 agent-free tests keep the default (#2276).
    /// </summary>
    protected virtual SharedOrleansFixture CreateFixture() => new();
    private readonly ConcurrentBag<IMessageHub> _clientHubs = new();
    private bool _leasedFromPool;

    /// <summary>
    /// Opt-out for classes that mutate CLUSTER-WIDE state destructively (killing silos,
    /// asserting global counters): they keep the dedicated per-class cluster. Everyone else
    /// leases a RUNNING mesh from <see cref="OrleansMeshPool"/> — "we can have a pool of
    /// running meshes and then parallelize over this pool" (maintainer, 2026-09-01) — which
    /// deletes the ~90 per-class silo boots the runner paid while executing one class at a
    /// time. A lease is exclusive; client hubs are cleaned per class either way.
    /// </summary>
    protected virtual bool UsePooledMesh => true;
    /// <summary>
    /// Legacy two-arg ctor retained for tests that still inject the fixture from a
    /// collection. New tests should use the parameterless ctor with the per-class shape.
    /// </summary>
    protected OrleansSharedTestBase(SharedOrleansFixture fixture, ITestOutputHelper output)
        : base(output)
    {
        Fixture = fixture;
    }

    protected OrleansSharedTestBase(ITestOutputHelper output) : base(output)
    {
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        // Pool first (exclusive lease of a RUNNING cluster, per fixture type), dedicated
        // per-class cluster as the fallback — for opted-out classes, for the legacy
        // fixture-injecting ctor, and for assemblies that do not register the pool fixture.
        if (Fixture is null)
        {
            if (UsePooledMesh && OrleansMeshPool.Current is { } pool)
            {
                Fixture = await pool.LeaseAsync(CreateFixture);
                _leasedFromPool = true;
            }
            else
            {
                Fixture = CreateFixture();
                await Fixture.InitializeAsync();
            }
        }
    }

    /// <summary>
    /// Creates a tracked client hub. The returned hub will be disposed in
    /// <see cref="DisposeAsync"/>; do not dispose it manually.
    /// </summary>
    protected IMessageHub GetClient(string clientId, string userId = "TestUser")
    {
        var client = Fixture.GetClient(clientId, userId);
        _clientHubs.Add(client);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var hub in _clientHubs)
        {
            await Fixture.CleanupClientAsync(hub);
        }
        if (Fixture is not null)
        {
            if (_leasedFromPool && OrleansMeshPool.Current is { } pool)
                pool.Return(Fixture);
            else
                await Fixture.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}
