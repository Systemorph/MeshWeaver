using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

public record MeshHostApplicationBuilder : MeshBuilder
{
    public MeshHostApplicationBuilder(IHostApplicationBuilder Host, Address address) : base(x => x.Invoke(Host.Services), address)
    {
        this.Host = Host;
        Host.ConfigureContainer(new MessageHubServiceProviderFactory(BuildHub));
        this.RegisterMeshQueryCoreOnMeshHub();
    }

    public IHostApplicationBuilder Host { get; }
}
public record MeshHostBuilder : MeshBuilder
{
    public MeshHostBuilder(IHostBuilder Host, Address address) : base(c => Host.ConfigureServices((_,services) => c(services)), address)
    {
        this.Host = Host;
        Host.UseServiceProviderFactory(new MessageHubServiceProviderFactory(BuildHub));
        this.RegisterMeshQueryCoreOnMeshHub();
    }


    public IHostBuilder Host { get; }
}
