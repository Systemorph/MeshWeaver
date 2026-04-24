using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

public abstract class OrleansTestBase(ITestOutputHelper output) : TestBase(output)
{
    protected TestCluster Cluster { get; private set; } = null!;

    protected static Address CreateClientAddress(string? id = null) => new Address("client", id ?? "1");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();



        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();


    }




    protected async Task<IMessageHub> GetClientAsync(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient);
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }


    protected IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration;

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }


}
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

