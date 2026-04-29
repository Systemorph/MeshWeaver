using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Documentation;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Generic base for Orleans tests that own their own <see cref="TestCluster"/>.
/// Inherit <see cref="TestSiloConfigurator"/> for the canonical silo wiring and
/// override <c>RegisterChatClientFactory</c> / <c>ConfigureMesh</c> only for
/// per-test specifics.
///
/// <para>Provides:</para>
/// <list type="bullet">
///   <item>TestCluster lifecycle (Initialize/Dispose).</item>
///   <item><see cref="GetClientAsync"/> — creates a participating client mesh hub
///   with the standard mesh-node handler chain (<see cref="GraphConfigurationExtensions.AddMeshDataSource"/>
///   + <see cref="LayoutExtensions.AddLayoutClient"/> + <see cref="AIExtensions.AddAITypes"/>).</item>
///   <item>Per-call <see cref="AccessContext"/> seeding so the client posts under
///   the test user's identity (default <c>TestUser</c>).</item>
///   <item><see cref="IRoutingService"/> registration so the silo can route
///   responses back to the client address.</item>
/// </list>
/// </summary>
public abstract class OrleansTestBase<TSiloConfigurator>(ITestOutputHelper output) : TestBase(output)
    where TSiloConfigurator : ISiloConfigurator, IHostConfigurator, new()
{
    protected TestCluster Cluster { get; private set; } = null!;

    protected IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    protected static Address CreateClientAddress(string? id = null) => new("client", id ?? "1");

    /// <summary>
    /// Initial silo count for the <see cref="TestCluster"/>. Default 1 — tests that
    /// need cross-silo behaviour (assembly-store sharing, cache propagation across
    /// grain placements) override to <c>2</c>.
    /// </summary>
    protected virtual short InitialSilosCount => 1;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = InitialSilosCount;
        builder.AddSiloBuilderConfigurator<TSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Canonical client config — registers the AI message types the test posts,
    /// adds the standard mesh-node data plumbing
    /// (<see cref="GraphConfigurationExtensions.AddMeshDataSource"/> gives the
    /// client a <see cref="MeshNodeReference"/> reducer + <see cref="Data.GetDataRequest"/>
    /// handler + workspace stream protocol), and the layout client. Override to
    /// add more — chain through the base call.
    /// </summary>
    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return configuration
            .AddMeshDataSource(source => source)
            .AddLayoutClient();
    }

    /// <summary>
    /// Creates a participating client mesh hub at <c>client/{clientId}</c>, seeds the
    /// per-circuit <see cref="AccessContext"/> with <paramref name="userId"/>, and
    /// registers the client address with the silo's <see cref="IRoutingService"/>.
    /// </summary>
    protected async Task<IMessageHub> GetClientAsync(string? clientId = null, string userId = "TestUser")
    {
        var address = CreateClientAddress(clientId);
        var client = ClientMesh.ServiceProvider.CreateMessageHub(address, ConfigureClient);
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Email = $"{userId}@meshweaver.io"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }
}

/// <summary>
/// Default <see cref="OrleansTestBase{TSiloConfigurator}"/> wired to
/// <see cref="TestSiloConfigurator"/> — used by tests that don't need silo-side
/// customisation (e.g., <c>OrleansAssemblyStoreTest</c>).
/// </summary>
public abstract class OrleansTestBase(ITestOutputHelper output) : OrleansTestBase<TestSiloConfigurator>(output);

/// <summary>
/// Mirrors the silo's mesh-builder chain (<see cref="OrleansTestMeshExtensions.ConfigurePortalMesh"/>)
/// on the Orleans client so the client-side mesh catalog has the same NodeType
/// registrations (Graph, AI, Kernel). Without this, <c>CreateNodeRequest</c>
/// posted to the client mesh address fails with "NodeType '<X>' is not
/// registered" because the local catalog is empty.
/// </summary>
public class TestClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshClient()
            .ConfigurePortalMesh();
    }
}

/// <summary>
/// Canonical silo configurator. Inherit and override <see cref="ConfigureMesh"/>
/// to add per-test seeds, or <see cref="RegisterChatClientFactory"/> to plug in a
/// fake <see cref="IChatClientFactory"/>. The base wires:
///
/// <list type="bullet">
///   <item><see cref="PersistenceExtensions.AddPartitionedInMemoryPersistence(MeshBuilder)"/>
///   so <see cref="IPartitionStorageProvider"/> rules (e.g.
///   <see cref="EmbeddedResourcePartitionStorageProvider"/> registered by
///   <see cref="DocumentationExtensions.AddDocumentation"/>) actually serve reads.
///   See <c>Doc/Architecture/PartitionedPersistence.md</c>.</item>
///   <item><see cref="OrleansTestMeshExtensions.ConfigurePortalMesh"/>: <c>AddGraph</c>,
///   <c>AddAI</c>, <c>AddKernel</c>, plus the test assembly's <c>HubFactory</c>
///   and <c>Kernel</c> NodeType registrations.</item>
///   <item><see cref="DocumentationExtensions.AddDocumentation"/>: registers the
///   <c>Doc</c> embedded-resource partition.</item>
///   <item><see cref="SecurityHostingExtensions.AddRowLevelSecurity"/>:
///   ScopeRolesService + SecurityService. Combined with the <c>TestUser</c>
///   admin seeds below, every test starts with a logged-in admin user.</item>
///   <item>TestUser admin seeds: <c>User/TestUser</c> + <c>User/_Access/TestUser_Access</c>
///   so the default identity has Admin role.</item>
///   <item><see cref="MeshHubBuilderExtensions.ConfigureDefaultNodeHub"/> with
///   <see cref="LayoutExtensions.AddDefaultLayoutAreas"/>.</item>
///   <item>Per-process Guid-suffixed <see cref="IAssemblyStore"/> root (Acme/FutuRe
///   isolation pattern).</item>
///   <item>Silo-side framework logging through <see cref="ITestOutputHelper"/> so
///   silo errors aren't lost on a crash.</item>
/// </list>
///
/// <para>With this baseline, the typical per-test configurator only needs to
/// override <see cref="RegisterChatClientFactory"/> to plug in a fake AI
/// factory.</para>
/// </summary>
public class TestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    /// <summary>
    /// Shared root directory for the <see cref="IAssemblyStore"/> across every silo in
    /// the test cluster. Per-process Guid suffix (mirrors the Acme/FutuRe test
    /// isolation pattern) so a stale DLL from a previous test process can't collide
    /// on Windows file locks. The Guid is computed once per AppDomain, so every silo
    /// in the same cluster sees the same root and the cross-silo Put-on-A /
    /// TryGet-on-B invariant holds.
    /// </summary>
    public static readonly string AssemblyStoreRoot =
        Path.Combine(Path.GetTempPath(), $"mw-orleans-asmstore-{Guid.NewGuid():N}");

    /// <summary>
    /// Subclass hook: register a custom <see cref="IChatClientFactory"/>. Default is
    /// no-op; tests that need agent behaviour register their fake here.
    /// </summary>
    protected virtual void RegisterChatClientFactory(IServiceCollection services) { }

    /// <summary>
    /// Subclass hook: add per-test mesh nodes / seeds / extensions. Called after
    /// the canonical chain, so seeds layer on top of the standard config.
    /// </summary>
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder) => builder;

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            // Surface silo-side framework logs through ITestOutputHelper. Without
            // this, silo errors vanish on a test crash leaving only the test's own
            // Output.WriteLine, which makes hangs / stack overflows diagnostically
            // opaque in CI.
            .ConfigureLogging(logging => logging.AddXUnitLogger());
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        var meshBuilder = hostBuilder.UseOrleansMeshServer()
            .AddPartitionedInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddDocumentation()
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(TestUserAdminAccess())
            .ConfigureServices(services =>
            {
                RegisterChatClientFactory(services);
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

        ConfigureMesh(meshBuilder);
    }

    /// <summary>
    /// TestUser-specific Admin seed (mirrors
    /// <c>samples/Graph/Data/User/_Access/TestUser_Access.json</c>). Namespace MUST
    /// end in <c>/_Access</c> — see <c>SecurityService.ComputeScopeRoles</c>; anything
    /// else is silently dropped, leaving the user with zero permissions.
    /// </summary>
    private static MeshNode[] TestUserAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test User",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return [new("TestUser_Access", "User/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            Content = assignment,
            MainNode = "User",
        }];
    }
}
