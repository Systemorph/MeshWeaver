using System.Collections.Concurrent;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Base class for tests that share the <see cref="SharedOrleansFixture"/>
/// Orleans cluster via <c>[Collection(nameof(OrleansClusterCollection))]</c>.
///
/// Responsibility: keep the shared cluster clean across tests. Every client
/// hub created via <see cref="GetClientAsync"/> is tracked here and disposed
/// in <see cref="DisposeAsync"/>. Without this, the client + silo
/// <c>OrleansRoutingService.streams</c> dictionaries (and the hosted-hubs
/// collection on the client mesh) accumulate dead entries for the whole
/// assembly run, which has caused address-collision and "stale registration"
/// flakes in the past.
///
/// Tests that need the per-test prefix to also be wiped on the silo side
/// (per-node grain hubs activated by <c>CreateNodeRequest</c>) can call
/// <see cref="SharedOrleansFixture.CleanupSiloHubsWithPrefix"/> explicitly;
/// the default cleanup here handles the much more common client-hub case.
/// </summary>
public abstract class OrleansSharedTestBase : TestBase
{
    protected readonly SharedOrleansFixture Fixture;
    private readonly ConcurrentBag<IMessageHub> _clientHubs = new();

    protected OrleansSharedTestBase(SharedOrleansFixture fixture, ITestOutputHelper output)
        : base(output)
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Creates a tracked client hub. The returned hub will be disposed in
    /// <see cref="DisposeAsync"/>; do not dispose it manually.
    /// </summary>
    protected async Task<IMessageHub> GetClientAsync(string clientId, string userId = "TestUser")
    {
        var client = await Fixture.GetClientAsync(clientId, userId);
        _clientHubs.Add(client);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var hub in _clientHubs)
        {
            await Fixture.CleanupClientAsync(hub);
        }
        await base.DisposeAsync();
    }
}
