using System;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
        // Set up the test cluster options
        var options = new TestClusterOptions
        {
            InitialSilosCount = 1,
            ClusterId = "test-cluster",
            ServiceId = "test-service"
        };
        // Configure the silo
        // Set up the test cluster (Server)
        var builder = new TestClusterBuilder();



        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();


    }




    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration> config = null) =>
        Cluster.Client.ServiceProvider.CreateMessageHub(new ClientAddress(), config ?? ConfigureClient);

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

    public TestSiloConfigurator()
    {
        
    }
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .ConfigurePortalMesh()
    ;


    public void Configure(ISiloBuilder siloBuilder)
    {

        siloBuilder.ConfigureMeshWeaverServer();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        var meshBuilder = hostBuilder.UseOrleansMeshServer();
    }
}

