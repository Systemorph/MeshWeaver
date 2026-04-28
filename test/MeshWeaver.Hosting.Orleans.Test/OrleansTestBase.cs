using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Generic base for Orleans tests that own their own <see cref="TestCluster"/> and
/// need a per-test silo configurator (custom <see cref="IChatClientFactory"/>,
/// custom <c>AddMeshNodes</c>, etc.). The non-generic <see cref="OrleansTestBase"/>
/// uses the default <see cref="TestSiloConfigurator"/> for tests that don't need
/// silo-side customisation.
///
/// <para>What this base provides:</para>
/// <list type="bullet">
///   <item>TestCluster lifecycle (Initialize/Dispose).</item>
///   <item><see cref="GetClientAsync"/> — creates a participating client mesh hub
///   with the canonical mesh-node handler chain (<see cref="GraphConfigurationExtensions.AddMeshDataSource"/>
///   + <see cref="LayoutExtensions.AddLayoutClient"/> + <see cref="AIExtensions.AddAITypes"/>) so the
///   client can post / receive every standard mesh request type.</item>
///   <item>Per-call <see cref="AccessContext"/> seeding so the client posts under
///   the test user's identity (default <c>TestUser</c>).</item>
///   <item><see cref="IRoutingService"/> registration so the silo can route
///   responses back to the client address.</item>
/// </list>
///
/// <para>Tests can override <see cref="ConfigureClient"/> to add extra type
/// registrations or pipeline steps; the override SHOULD chain through the
/// base implementation, not replace it.</para>
/// </summary>
public abstract class OrleansTestBase<TSiloConfigurator>(ITestOutputHelper output) : TestBase(output)
    where TSiloConfigurator : ISiloConfigurator, IHostConfigurator, new()
{
    protected TestCluster Cluster { get; private set; } = null!;

    protected IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    protected static Address CreateClientAddress(string? id = null) => new("client", id ?? "1");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
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
    /// adds the standard mesh-node data plumbing (<see cref="GraphConfigurationExtensions.AddMeshDataSource"/>
    /// gives the client a <see cref="MeshNodeReference"/> reducer + <see cref="Data.GetDataRequest"/>
    /// handler + workspace stream protocol), and the layout client. Without
    /// <c>AddMeshDataSource</c> the client can post <c>GetDataRequest(new MeshNodeReference())</c>
    /// but the response can't deserialise the Reference field — polling loops then
    /// time out at 30 s with <c>responseMsg=null</c>.
    /// Override to add more — chain through the base call.
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

public class TestClientConfigurator : IHostConfigurator
{

    public void Configure(IHostBuilder hostBuilder)
    {

        // Mirror the silo's mesh-builder chain (ConfigurePortalMesh) on the client so
        // the client-side mesh catalog has the same NodeType registrations (Graph, AI,
        // Kernel). Without this, CreateNodeRequest posted to the client mesh address
        // fails with "NodeType '<X>' is not registered" because the local catalog is
        // empty — every CreateThread/CreateApiToken test depends on this.
        hostBuilder.UseOrleansMeshClient()
            .ConfigurePortalMesh();


    }
}

public class TestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    /// <summary>
    /// Shared root directory for the <see cref="IAssemblyStore"/> across every silo in
    /// the test cluster. Fixed (not <c>Guid.NewGuid()</c>) so that multi-silo tests can
    /// observe one silo's Put reflected in another silo's TryGet — exactly what the
    /// content-addressed store promises in production across ACA replicas.
    /// </summary>
    public static readonly string AssemblyStoreRoot =
        Path.Combine(Path.GetTempPath(), "mw-orleans-asmstore");

    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .ConfigurePortalMesh()
    ;


    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        ConfigureMesh(hostBuilder.UseOrleansMeshServer());
    }
}
