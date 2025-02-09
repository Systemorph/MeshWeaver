using System;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Orleans.Test;

public abstract class OrleansTestBase(ITestOutputHelper output) : TestBase(output)
{
    protected TestCluster Cluster { get; private set; }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();



        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();


    }




    protected async Task<IMessageHub> GetClientAsync(Func<MessageHubConfiguration, MessageHubConfiguration> config = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(new ClientAddress(), config ?? ConfigureClient);
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }


    protected IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration;

    public override async Task DisposeAsync()
    {
        if(Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
    protected record ClientAddress(string Id = null) : Address("client", Id ?? "1");


}
public class TestClientConfigurator : IHostConfigurator
{

    public void Configure(IHostBuilder hostBuilder)
    {

        hostBuilder.UseOrleansMeshClient();


    }
}

public class TestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .ConfigurePortalMesh()
    ;


    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorage(StorageProviders.MeshCatalog)
            .AddMemoryGrainStorage(StorageProviders.Activity)
            .AddMemoryGrainStorage(StorageProviders.AddressRegistry)
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        ConfigureMesh(hostBuilder.UseOrleansMeshServer());
    }
}

