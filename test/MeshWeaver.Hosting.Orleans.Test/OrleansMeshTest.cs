using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Articles;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using Xunit;
using Orleans.Hosting;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans.Test;

public class OrleansMeshTests : IAsyncLifetime
{
    private TestCluster _cluster;
    private IHost _clientHost;

    public async Task InitializeAsync()
    {
        // Set up the test cluster (Server)
        var builder = new TestClusterBuilder();



        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        // Set up the client
        var clientBuilder = Host.CreateApplicationBuilder();
        var address = new OrleansAddress();

        clientBuilder.UseMeshWeaver(address, conf =>
            conf.UseOrleansMeshClient(client =>
            {
                client.UseStaticClustering(options =>
                {
                    var primarySiloEndpoint = _cluster.Primary.SiloAddress.Endpoint;
                    options.Gateways = new[] { primarySiloEndpoint.ToGatewayUri() }.ToList();
                });
                return client;
            }));

        _clientHost = clientBuilder.Build();
        await _clientHost.StartAsync();
    }
    public async Task DisposeAsync()
    {
        if (_clientHost != null)
            await _clientHost.StopAsync();

        if (_cluster != null)
            await _cluster.DisposeAsync();
    }
    [Fact]
    public async Task TestMeshConnection()
    {
        // Your test code here
    }
}

public class TestSiloConfigurator : ISiloConfigurator
{
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .ConfigurePortalMesh()
    ;


    public void Configure(ISiloBuilder siloBuilder)
    {

        siloBuilder.UseOrleansMeshServer(ConfigureMesh);

    }



}

public static class TestMeshConfiguration
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        return builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(TestMeshConfiguration).Assembly.Location)
            )
            .AddKernel()
            .AddArticles(articles
                => articles.FromAppSettings()
            );

    }
}
