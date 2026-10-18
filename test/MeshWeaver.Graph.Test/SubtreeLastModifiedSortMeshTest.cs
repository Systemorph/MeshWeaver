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

        // Created now, so its own stamp is newer than every fixture row: it enters at the top.
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("Epsilon", Org) { Name = "Epsilon", NodeType = "Group" })
            .FirstAsync()
            .Await(ct);

        // Every snapshot until Epsilon lands, and the one it lands in: none may exceed the window.
        var snapshots = await cache
            .Select(nodes => nodes.Select(n => n.Id).Order(StringComparer.Ordinal).ToArray())
            .TakeUntil(ids => ids.Contains("Epsilon"))
            .ToList()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        snapshots.Should().AllSatisfy(ids => ids.Length.Should().BeLessThanOrEqualTo(2));
        snapshots[^1].Should().Equal(["Epsilon", "Gamma"], "Beta left the window and the cache dropped it");
    }
}
