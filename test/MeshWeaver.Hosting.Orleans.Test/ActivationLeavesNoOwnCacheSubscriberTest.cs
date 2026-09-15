using System;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins #3432's retainer: an activated node hub must not hold its OWN mesh-node cache entry open
/// for its whole life.
///
/// <para>On Orleans, <c>MessageHubGrain</c> resolves a grain's node through
/// <c>IMeshNodeStreamCache.GetStream(ownPath)</c> — the cache's <c>SharedView</c>, which registers a
/// live subscriber on the entry. That is fine for the ACTIVATION chain, which takes one node and
/// unsubscribes. It was not fine for the stream handed to the hub as its own-node source: the hub's
/// <c>MeshNodeTypeSource</c> keeps a <c>Replay(1).RefCount()</c> subscription on it for the hub's
/// lifetime, so the entry never reached zero subscribers, the idle sweep and the terminal
/// <c>ReleaseIfUnwatched</c> could never release it, its hydration sync stream kept sending the
/// owner a <c>HeartBeatEvent</c> every 45 s, and the heartbeat kept the grain from ever
/// deactivating — a loop with nothing outside it. Every node ever activated on a replica stayed
/// resident with its cache entry and the <c>sync/</c> hubs on both sides (≈6–7 per node), which is
/// the monotone growth the live census measured. The Monolith never had it: it hands the hub
/// <c>Observable.Return(enriched)</c>.</para>
///
/// <para>The assertion is the release the platform already owns: once the grain is active and
/// nobody outside the process holds the entry, <see cref="IMeshNodeStreamCache.ReleaseIfUnwatched"/>
/// on the silo's cache must be able to release it. On the unfixed grain it never can — the hub's
/// own subscription is permanent — so the wait times out. That is the falsification.</para>
/// </summary>
public class ActivationLeavesNoOwnCacheSubscriberTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"own-pin-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact(Timeout = 120_000)]
    public async Task AnActivatedNodeHub_LeavesItsOwnCacheEntryReleasable()
    {
        var client = GetClient();
        var id = $"own-pin-{Guid.NewGuid():N}";
        var created = await client.Observe(
                new CreateNodeRequest(new MeshNode(id, "TestUser") { Name = "Pinned?", NodeType = "Markdown" }),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        created.Message.Success.Should().BeTrue(created.Message.Error ?? "");
        var path = created.Message.Node!.Path!;

        // Activate the node's OWN grain the way a reader does — through the client's cache, a
        // SubscribeRequest routed to the grain — and let the reader's subscription go (FirstAsync).
        var node = await client.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        node!.Path.Should().Be(path);
        Output.WriteLine($"[active] {path} answered its first read — grain active, hub built");

        // The SILO's cache is the one the grain activated through. With the reader gone, the only
        // subscriber that could remain is the hub's own — and that is the retainer under test.
        var siloCache = SiloServices().GetRequiredService<IMeshNodeStreamCache>();
        var released = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => siloCache.ReleaseIfUnwatched(path))
            .Where(r => r)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        released.Should().BeTrue(
            "an activated hub's own cache entry must reach zero subscribers once activation has settled — " +
            "a hub that keeps its own entry subscribed pins the entry, its hydration stream, its heartbeat " +
            "and therefore its own grain for the life of the process (#3432)");
    }
}
