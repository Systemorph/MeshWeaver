using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Stands up a real monolith mesh on the SQLite backend (the local-first client configuration) and
/// drives node create / read / query end-to-end through the mesh — proving the SQLite adapter plugs
/// into the full PersistenceService + per-node-hub + query stack, not just the adapter in isolation.
/// Mirrors <c>MonolithMeshTestBase.ConfigureMeshBase</c> but swaps InMemory persistence for SQLite.
/// </summary>
public class SqliteMonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _assemblyStore = Path.Combine(
        Path.GetTempPath(), $"mw-sqlite-mesh-asm-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            // The one change vs the in-memory base config: persist nodes in SQLite.
            .AddPartitionedSqlitePersistence("Data Source=:memory:")
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(_assemblyStore))
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)))
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    [Fact]
    public async Task Create_read_and_query_a_node_through_a_monolith_mesh_on_sqlite()
    {
        // CREATE — routes through CreateNodeRequest → the per-node hub → the SQLite adapter's Write.
        var node = new MeshNode("Profile1", TestPartition) { NodeType = "Markdown", Name = "Hello SQLite" };
        await NodeFactory.CreateNode(node).FirstAsync().ToTask();

        // READ — routes through GetMeshNode → the SQLite adapter's Read.
        var read = await ReadNode($"{TestPartition}/Profile1").FirstAsync().ToTask();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Hello SQLite");
        read.NodeType.Should().Be("Markdown");

        // QUERY — routes through the generic StorageAdapterMeshQueryProvider over the SQLite adapter.
        var result = await MeshQuery.Query<MeshNode>(
                new MeshQueryRequest { Query = $"path:{TestPartition} scope:children" })
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .ToTask();

        result.Items.Select(n => n.Path).Should().Contain($"{TestPartition}/Profile1");
    }
}
