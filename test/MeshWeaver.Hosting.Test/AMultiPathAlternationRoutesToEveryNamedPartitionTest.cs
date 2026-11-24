using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A <c>path:A|B|C</c> alternation was answered for the FIRST path only (Plugins #5898): the
/// routing rule pinned the whole query to <c>Path</c> (= the first alternative) and the provider
/// matchers read only <c>Path</c>. The Spaces catalog queries the viewer's profile manifest this
/// way and showed one space. The access clause is per partition and unchanged: a viewer who cannot
/// read B gets A only (negative control).
/// </summary>
public class AMultiPathAlternationRoutesToEveryNamedPartitionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string A = "MpAlpha";
    private const string B = "MpBeta";

    private static readonly AccessContext OnlyA = new()
    {
        ObjectId = "only-a", Name = "Only A", Email = "only-a@example.com", Roles = [],
    };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(A) { Name = "Alpha", NodeType = "Markdown" })
            .AddMeshNodes(new MeshNode(B) { Name = "Beta", NodeType = "Markdown" })
            .AddMeshNodes(AssignmentNodeFactory.UserRole(OnlyA.ObjectId, roleId: "Viewer", scope: A));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private Task<string[]> Paths(string query) =>
        MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.Items.Count > 0)
            .Select(c => c.Items.Select(n => n.Path).OrderBy(p => p).ToArray())
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the alternation answers",
                cancellationToken: TestContext.Current.CancellationToken);

    [Theory]
    [InlineData($"path:{A}|{B}")]
    [InlineData($"path:{B}|{A}")]
    public async Task BothNamedPartitionsAnswerInEitherOrder(string query)
    {
        var paths = await Paths(query);
        paths.Should().Equal(A, B);
    }

    [Fact]
    public async Task ASinglePathStillPins()
    {
        (await Paths($"path:{A}")).Should().Equal(A);
        var config = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var parse = new QueryParser();
        config.ResolveRoutingHints(parse.Parse($"path:{A}")).Partition.Should().Be(A);
        config.ResolveRoutingHints(parse.Parse($"path:{A}/x|{A}/y")).Partition.Should().Be(A,
            "one partition named twice is still a single pin");
    }

    [Theory]
    [InlineData($"path:{A}|{B}")]
    [InlineData($"path:{B}|{A}")]
    public void ARoutingRuleDoesNotPinAMultiPartitionAlternation(string query)
    {
        var config = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        config.ResolveRoutingHints(new QueryParser().Parse(query)).Partition.Should().BeNull(
            "pinning to the first alternative drops the others");
    }

    [Theory]
    [InlineData($"path:{A}|{B}")]
    [InlineData($"path:{B}|{A}")]
    public async Task AViewerWhoCannotReadBGetsOnlyA(string query)
    {
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetCircuitContext(OnlyA);
        var paths = await Paths(query);
        paths.Should().Equal(A);
    }

    [Fact]
    public void ParsedQueryNamesEveryPartitionOfTheAlternation()
    {
        var parsed = new QueryParser().Parse($"path:{B}|{A}/x");
        parsed.PathPartitions().Should().Equal(B, A);
    }
}
