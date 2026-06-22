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
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Proves the RAW <see cref="MeshBuilder"/> bootstrap — the exact pattern the MAUI client uses in
/// MauiProgram (build a plain ServiceCollection, new MeshBuilder, register BuildHub, resolve the hub)
/// — stands up a monolith mesh on SQLite and does create + read, with NO test-base machinery. This
/// is what de-risks wiring the mesh into the client: it exercises the manual SP build + the device-
/// user AccessContext + the hub lifecycle that the client owns itself.
/// </summary>
public class SqliteRawBootstrapTest
{
    [Fact]
    public async Task Raw_monolith_mesh_bootstrap_on_sqlite_creates_and_reads_a_node()
    {
        var assemblyStore = Path.Combine(Path.GetTempPath(), $"mw-raw-asm-{Guid.NewGuid():N}");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddOptions();

        // The exact bootstrap the client will run (no MonolithMeshTestBase).
        var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())
            .UseMonolithMesh()
            .AddPartitionedSqlitePersistence("Data Source=:memory:")
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(new MeshNode("TestData") { Name = "Test Data", NodeType = "Markdown" })
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(assemblyStore))
            // Single-user local mesh: the device user is admin (seed an all-admin access grant).
            .AddMeshNodes(TestUsers.PublicAdminAccess());
        services.AddSingleton(builder.BuildHub);

        // The MeshWeaver SP runs the module setup — a plain BuildServiceProvider does NOT,
        // and the hub throws "Mesh Weaver has not been properly configured". The MAUI client
        // must build its local-mesh SP the same way (separate from the default MAUI DI).
        var sp = services.CreateMeshWeaverServiceProvider();
        var hub = sp.GetRequiredService<IMessageHub>();

        // The client sets one device-user identity for every operation.
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(new AccessContext { ObjectId = "device-user", Name = "Device User" });

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService
            .CreateNode(new MeshNode("Profile1", "TestData") { NodeType = "Markdown", Name = "Hello" })
            .FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(30));

        var read = await hub.GetMeshNodeStream("TestData/Profile1")
            .Where(n => n is not null)
            .Select(n => n!)
            .FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(30));

        read.Name.Should().Be("Hello");
        read.NodeType.Should().Be("Markdown");

        (sp as IDisposable)?.Dispose();
    }
}
