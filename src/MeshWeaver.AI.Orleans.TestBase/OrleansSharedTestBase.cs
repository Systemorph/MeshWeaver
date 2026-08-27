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
    private readonly ConcurrentBag<IMessageHub> _clientHubs = new();

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
        // Per-class fixture — each test class boots its own Orleans cluster so grain
        // state is isolated. The cost is ~300-500 ms silo boot; the win is no
        // cross-test pollution and no 120 s disposal-wait pile-ups during the run.
        if (Fixture is null)
        {
            Fixture = new SharedOrleansFixture();
            await Fixture.InitializeAsync();
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
            await Fixture.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}
