using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <c>sort:subtreeLastModified</c> on the real mesh, through <see cref="IMeshService"/> — the
/// SECURED surface a <c>MeshSearchControl</c> runs, with the viewer resolved from the ambient
/// context rather than stamped on the request. The engine-level contract (access, the window,
/// nulls last, the candidate bound) is pinned in <c>MeshWeaver.Hosting.Test.SubtreeLastModifiedSortTest</c>;
/// this proves the same ordering survives the full stack, providers and identity resolution included.
/// </summary>
public class SubtreeLastModifiedSortMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Org = $"{TestPartition}/SubtreeSortOrg";

    private static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static MeshNode Node(string id, string ns, int day, string nodeType) => new(id, ns)
    {
        Name = id,
        NodeType = nodeType,
        LastModified = Epoch.AddDays(day),
    };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode("SubtreeSortOrg", TestPartition) { Name = "Org", NodeType = "Markdown" },
            // Alpha: re-stamped root (day 30), work below last touched day 1.
            Node("Alpha", Org, 30, "Group"),
            Node("Notes", $"{Org}/Alpha", 1, "Markdown"),
            // Beta: root day 1, a child day 20.
            Node("Beta", Org, 1, "Group"),
            Node("Deal", $"{Org}/Beta", 20, "Markdown"),
            // Gamma: root day 2, a grandchild day 25.
            Node("Gamma", Org, 2, "Group"),
            Node("Case", $"{Org}/Gamma", 4, "Markdown"),
            Node("Offer", $"{Org}/Gamma/Case", 25, "Markdown"));

    [Fact(Timeout = 120_000)]
    public async Task TheSecuredSurfaceOrdersContainersByTheirNewestDescendant()
    {
        var change = await MeshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{Org} nodeType:Group sort:subtreeLastModified-desc"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        change.Items.Select(n => n.Id).Should().Equal(["Gamma", "Beta", "Alpha"],
            "Gamma's grandchild (day 25) beats Beta's child (day 20); Alpha's own day-30 stamp is not activity");
    }

    /// <summary>
    /// The snapshot surface (<see cref="QueryResult"/> rows) answers the same window in the same
    /// order as the change surface (review #4306596226). It used to hand the selector to the
    /// providers and re-sort by score and path, which never sees a subtree.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSnapshotSurfaceAnswersTheRankedWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var (window, expected) in new (string, string[])[]
                 {
                     ("", ["Gamma", "Beta", "Alpha"]),
                     (" limit:2", ["Gamma", "Beta"]),
                 })
        {
            var request = MeshQueryRequest.FromQuery($"namespace:{Org} nodeType:Group sort:subtreeLastModified-desc{window}");
            var changes = await MeshQuery.Query<MeshNode>(request)
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            var snapshot = await MeshQuery.Query(request)
                .FirstAsync(rows => rows.Count > 0).Timeout(TestTimeouts.Convergence).Await(ct);
            changes.Items.Select(n => n.Id).Should().Equal(expected, "control: the change surface ranks");
            snapshot.Select(r => r.Id).Should().Equal(expected, $"the snapshot surface ranks the same window ('{window}')");
        }
    }

    /// <summary>
    /// The window holds through <c>hub.GetQuery</c>, the synced cache a hub-side reader binds to
    /// (review #4306054491). That cache folds a <see cref="QueryChangeType.Reset"/> additively, so a
    /// container ranked into a <c>limit:2</c> window must evict the row it pushed out — or the cache
    /// holds three.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSyncedCacheKeepsOnlyTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Mesh.GetQuery("subtree-sort-window",
            $"namespace:{Org} nodeType:Group sort:subtreeLastModified-desc limit:2");

        await cache
            .Select(nodes => string.Join(",", nodes.Select(n => n.Id).Order(StringComparer.Ordinal)))
            .FirstAsync(ids => ids == "Beta,Gamma")
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        // Record BEFORE the create: the cache replays only its latest snapshot, so a transient
        // over-limit snapshot emitted before a later subscription would escape the assertion.
        var snapshots = cache
            .Select(nodes => nodes.Select(n => n.Id).Order(StringComparer.Ordinal).ToArray())
            .TakeUntil(ids => ids.Contains("Epsilon"))
            .ToList()
            .Timeout(TestTimeouts.Convergence)
            .Replay();
        using var recording = snapshots.Connect();

        // Created now, so its own stamp is newer than every fixture row: it enters at the top.
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("Epsilon", Org) { Name = "Epsilon", NodeType = "Group" })
            .FirstAsync()
            .Await(ct);

        // Every snapshot until Epsilon lands, and the one it lands in: none may exceed the window.
        var recorded = await snapshots.Await(ct);
        recorded.Should().AllSatisfy(ids => ids.Length.Should().BeLessThanOrEqualTo(2));
        recorded[^1].Should().Equal(["Epsilon", "Gamma"], "Beta left the window and the cache dropped it");
    }
}
