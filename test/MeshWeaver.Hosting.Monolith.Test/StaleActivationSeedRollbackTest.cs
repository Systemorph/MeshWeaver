#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// DETERMINISTIC repro of the owner-recycle write-rollback: a reactivating per-node hub must seed
/// its own MeshNode from DURABLE STORAGE, never from a routing cache — and it must never adopt an
/// own-node emission whose <see cref="MeshNode.Version"/> regresses below what it already holds.
///
/// <para><b>The defect.</b> Both legs of the routing-supplied own-node stream are caches.
/// <c>PathResolutionService</c> memoizes the resolved <c>AddressResolution</c> — INCLUDING its
/// <see cref="MeshNode"/> snapshot — and invalidates only from the per-silo change feed;
/// <c>MeshNodeStreamCache</c> replays its last seen value. <c>MessageHubGrain</c> /
/// <c>MonolithRoutingService</c> hand that stream to the hub as its authoritative own-node data
/// source (<c>WithOwnNodeStream</c>), and <c>MeshNodeTypeSource.Initialize</c> preferred it over
/// <c>IStorageAdapter.Read</c>. So a reactivation could adopt an arbitrarily stale snapshot as LIVE
/// state — which the persistence sampler then wrote back over newer durable data. Captured in
/// production as <c>Version=12 / ApiKey=sk-v6</c> → <c>Version=2 / ApiKey=sk-v0</c>: six
/// acknowledged writes destroyed while the write reported success in 93 ms.</para>
///
/// <para><b>Why this test is deterministic where the two-silo repro is not.</b>
/// <c>TwoSiloRecycleConvergenceTest</c> has to RACE a recycle against the debounced persistence
/// sampler to produce a stale cache — it reproduces roughly 1 run in 11 under CPU contention. Here
/// the same state is produced by construction: activate the hub (which warms the path-resolution
/// cache with the node as it is NOW), recycle the hub, then advance the DURABLE row out of band —
/// straight through <see cref="IStorageAdapter"/>, which does not publish to
/// <see cref="IMeshChangeFeed"/> and therefore does not invalidate the resolution cache. The next
/// message reactivates the hub against a resolution cache that is provably behind the store. That
/// is precisely the post-recycle state, with the timing removed.</para>
///
/// <para><b>Before the fix</b> the hub adopts the cached snapshot: the authoritative read returns
/// the OLD content at the OLD version, and the durable row is then rolled back to it.
/// <b>After the fix</b> the hub seeds from the durable row, the cached snapshot is dropped by the
/// version floor, and the store never moves backward.</para>
/// </summary>
public class StaleActivationSeedRollbackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>Reads the node straight out of durable storage — bypassing every hub, stream and
    /// cache — so an assertion about DURABILITY cannot be satisfied by an in-RAM snapshot.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    /// <summary>Disposes the per-node hub owning <paramref name="path"/> — the idle-recycle.</summary>
    private async Task RecycleOwner(string path)
    {
        var resolution = await PathResolver.ResolvePath(path).Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull($"'{path}' must resolve to an owning hub");
        var owner = Mesh.GetHostedHub(new Address(resolution!.Prefix.ToString()!), HostedHubCreation.Never);
        owner.Should().NotBeNull("the owner hub must be live after the warm read");
        owner!.Dispose();
        Output.WriteLine($"[recycle] disposed owner hub for {path}");
    }

    [Fact(Timeout = 55_000)]
    public async Task ReactivatedOwner_SeedsFromDurableStorage_NotFromTheRoutingCache()
    {
        var id = $"stale-seed-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        // 1. Create + warm-read: activates the owner hub AND caches the create-time node in the
        //    path-resolution cache (the snapshot the reactivation would otherwise adopt).
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        Output.WriteLine($"[created] version={created!.Version}");

        // 2. Recycle the owner. The routing caches survive — that is the whole point.
        await RecycleOwner(path);

        // 3. Durable state advances OUT OF BAND (the acked writes the recycled owner would not
        //    know about). Straight through IStorageAdapter: no IMeshChangeFeed publish, so the
        //    path-resolution cache keeps serving the pre-recycle node.
        const long durableVersion = 5000L;
        await Storage.Write(created with
        {
            Name = "durable-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        (await ReadDurable(path).Should().Within(30.Seconds()).Emit())!
            .Version.Should().Be(durableVersion, "the out-of-band advance must be durable before we reactivate");

        // 4. Reactivate by subscribing through a client — the same surface the GUI binds to, and
        //    bounded on OUR timeout (ReadNode's own 60 s budget would outrun the test-class
        //    watchdog on the failing path). THE ASSERTION: the hub must come up on the DURABLE
        //    row, not on the stale cached snapshot — so the stream must serve "durable-advance".
        var client = GetClient(c => c.AddData());
        var reactivated = await client.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Should().Within(20.Seconds())
            .Match(n => n.Name == "durable-advance",
                "a reactivating hub must seed its own node from durable storage — the routing-supplied "
                + "own-node stream is cache-backed (path-resolution memo / mesh-node stream replay) and "
                + "was still serving the pre-recycle snapshot");
        Output.WriteLine($"[reactivated] name={reactivated.Name} version={reactivated.Version}");

        reactivated.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "Doc/Architecture/MeshNodeVersioning.md: the node loads its persisted Version verbatim on activation");

        // 5. And the durable row must never have been rolled back. The persistence sampler is
        //    Sample(200 ms) + a debounce flush; give it several windows to do damage if it can,
        //    then assert the store is still at (or above) the durable version.
        var afterSettle = await Observable.Timer(TimeSpan.FromSeconds(2))
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(20.Seconds()).Emit();
        Output.WriteLine($"[durable-after-settle] name={afterSettle?.Name} version={afterSettle?.Version}");

        afterSettle.Should().NotBeNull();
        afterSettle!.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "a reactivated hub must never write a pre-recycle snapshot back over newer durable data "
            + "— that is the acked-write-loss defect (Version=12/sk-v6 → Version=2/sk-v0)");
        afterSettle.Name.Should().Be("durable-advance");
    }

    [Fact(Timeout = 55_000)]
    public async Task PostRecycleWrite_LandsAboveTheDurableVersion_AndDoesNotRollTheStoreBack()
    {
        var id = $"stale-write-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        await RecycleOwner(path);

        const long durableVersion = 7000L;
        await Storage.Write(created! with
        {
            Name = "durable-advance", Version = durableVersion
        }, JsonOptions).Should().Within(30.Seconds()).Emit();

        // The post-recycle write — the same shape as the two-silo repro's UpdateApiKeyResilient.
        var client = GetClient(c => c.AddData());
        client.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "post-recycle" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[write error] {ex.Message}"));

        // The write must land, and it must land ABOVE the durable version — a hub seeded from a
        // stale snapshot stamps MeshNode.NextVersion off that snapshot and lands BELOW it.
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(60.Seconds()).Match(n => n is { Name: "post-recycle" });

        Output.WriteLine($"[persisted] name={persisted!.Name} version={persisted.Version}");
        persisted.Version.Should().BeGreaterThan(durableVersion,
            "the node's revision counter is monotonic across activations (MeshNode.NextVersion is "
            + "current + 1) — a post-recycle write below the durable version means the hub reactivated "
            + "on a stale snapshot");
    }
}
