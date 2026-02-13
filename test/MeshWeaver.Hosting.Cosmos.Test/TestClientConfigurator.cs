using MeshWeaver.Connection.Orleans;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Cosmos.Test;

public class TestClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshClient();
    }
}
