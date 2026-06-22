using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Step 3 of the federated client: instances live as <c>MemexInstance</c> mesh nodes (not device
/// preferences), and the bootstrap reads them with <c>GetQuery</c> — the "GetQuery the instances"
/// step that precedes connecting each one with <c>ConnectToMesh</c>.
/// </summary>
public class MemexInstanceBootstrapTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _assemblyStore = Path.Combine(Path.GetTempPath(), $"mw-meminst-asm-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddPartitionedSqlitePersistence("Data Source=:memory:")
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMemexInstanceType()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(_assemblyStore))
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)))
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    [Fact]
    public async Task Instances_are_nodes_and_read_back_via_GetQuery()
    {
        // Instances are mesh nodes (the config lives in the mesh, not in Preferences).
        await NodeFactory.CreateNode(new MeshNode("memex", TestPartition)
        {
            NodeType = MemexInstanceNodeType.NodeType,
            Name = "memex",
            Content = new MemexInstanceContent
            {
                DisplayName = "memex", Url = "https://memex.meshweaver.cloud", Token = "mw_demo", MeshId = "memex",
            },
        }).FirstAsync().ToTask();

        await NodeFactory.CreateNode(new MeshNode("atioz", TestPartition)
        {
            NodeType = MemexInstanceNodeType.NodeType,
            Name = "atioz",
            Content = new MemexInstanceContent { DisplayName = "atioz", Url = "https://atioz.example", MeshId = "atioz" }, // no token
        }).FirstAsync().ToTask();

        // The bootstrap read: GetQuery the instance nodes (live set), wait for both to land.
        var nodes = await Mesh.GetWorkspace()
            .GetQuery("memex-instances", $"nodeType:{MemexInstanceNodeType.NodeType}")
            .Select(ns => ns.Where(n =>
                string.Equals(n.NodeType, MemexInstanceNodeType.NodeType, StringComparison.OrdinalIgnoreCase)).ToList())
            .Where(ns => ns.Count >= 2)
            .FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(30));

        var instances = nodes
            .Select(n => n.ContentAs<MemexInstanceContent>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null).Select(c => c!)
            .ToList();

        instances.Select(i => i.DisplayName).Should().Contain(["memex", "atioz"]);

        var memex = instances.First(i => i.DisplayName == "memex");
        memex.Url.Should().Be("https://memex.meshweaver.cloud");
        memex.IsAuthenticated.Should().BeTrue();   // has a token → the bootstrap would ConnectToMesh

        instances.First(i => i.DisplayName == "atioz").IsAuthenticated.Should().BeFalse(); // no token → skipped
    }
}
