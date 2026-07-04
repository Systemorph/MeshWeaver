using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

/// <summary>
/// A <see cref="MeshBuilder"/> bound to an <see cref="IHostApplicationBuilder"/>, wiring the mesh
/// hub as the host's service-provider factory and registering ordered shutdown teardown.
/// </summary>
public record MeshHostApplicationBuilder : MeshBuilder
{
    /// <summary>
    /// Binds the mesh to the host application builder and registers the hub container factory and teardown service.
    /// </summary>
    /// <param name="Host">The host application builder to bind the mesh to.</param>
    /// <param name="address">The mesh address for the root hub.</param>
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

    /// <summary>The host application builder this mesh is bound to.</summary>
    public IHostApplicationBuilder Host { get; }
}
/// <summary>
/// A <see cref="MeshBuilder"/> bound to the legacy <see cref="IHostBuilder"/>, wiring the mesh hub
/// as the host's service-provider factory.
/// </summary>
public record MeshHostBuilder : MeshBuilder
{
    /// <summary>
    /// Binds the mesh to the host builder and registers the hub container factory.
    /// </summary>
    /// <param name="Host">The host builder to bind the mesh to.</param>
    /// <param name="address">The mesh address for the root hub.</param>
    public MeshHostBuilder(IHostBuilder Host, Address address) : base(c => Host.ConfigureServices((_,services) => c(services)), address)
    {
        this.Host = Host;
        Host.UseServiceProviderFactory(new MessageHubServiceProviderFactory(BuildHub));
        this.RegisterMeshQueryCoreOnMeshHub();
        // Same ordered drain as MeshHostApplicationBuilder above: quiesce the mesh root hub
        // (action blocks + IoPool + AsyncDisposeQueue) during host StopAsync, BEFORE the host
        // disposes the root scope — which IS the hub's Autofac container. Without it, a late
        // continuation resolves from (or begins a nested hub scope on) the already-disposed
        // container and throws ObjectDisposedException("LifetimeScope … has already been
        // disposed") on a pooled task nobody observes — the pre-existing "Catastrophic failure"
        // in Orleans TestCluster teardown (this legacy IHostBuilder path builds every
        // TestCluster silo and client via UseOrleansMeshServer/UseOrleansMeshClient).
        // Registered FIRST so it stops LAST — after the silo and the other hosted services
        // have stopped feeding the mesh. Pinned by MeshHostBuilderTeardownOrderingTest.
        Host.ConfigureServices((_, services) => services.AddHostedService<MeshTeardownHostedService>());
    }


    /// <summary>The host builder this mesh is bound to.</summary>
    public IHostBuilder Host { get; }
}
