using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

public record MeshHostApplicationBuilder : MeshBuilder
{
    public MeshHostApplicationBuilder(IHostApplicationBuilder Host, Address address) : base(x => x.Invoke(Host.Services), address)
    {
        this.Host = Host;
        Host.ConfigureContainer(new MessageHubServiceProviderFactory(BuildHub));
        this.RegisterMeshQueryCoreOnMeshHub();
        // Drain the mesh root hub (action blocks + IoPool + AsyncDisposeQueue) during host shutdown,
        // BEFORE the host disposes the scope — otherwise a late continuation hits the disposed Autofac
        // scope and throws an unobserved ObjectDisposedException. Registered here (first) so it stops
        // LAST, after dependent hosted services. See MeshTeardownHostedService.
        Host.Services.AddHostedService<MeshTeardownHostedService>();
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
