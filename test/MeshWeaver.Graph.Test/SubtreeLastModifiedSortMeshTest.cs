using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
}
