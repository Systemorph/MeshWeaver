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
        // scope and throws an unobserved ObjectDisposedException.
        //
        // 🚨 The ordering does NOT come from registering here. Hosted services stop in reverse
        // registration order, and on an ASP.NET Core host GenericWebHostService is registered by
        // WebApplication.CreateBuilder — strictly before this ctor can run — so Kestrel and the
        // Blazor circuits stop AFTER anything registered here. The drain therefore runs in
        // IHostedLifecycleService.StoppedAsync, which the host invokes only once EVERY StopAsync
        // has returned. See MeshTeardownHostedService for the full story (#1548 and family).
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
        // The drain runs in IHostedLifecycleService.StoppedAsync — after the silo and EVERY
        // other hosted service has stopped feeding the mesh, regardless of registration
        // position. Pinned by MeshHostBuilderTeardownOrderingTest.
        Host.ConfigureServices((_, services) => services.AddHostedService<MeshTeardownHostedService>());
    }


    /// <summary>The host builder this mesh is bound to.</summary>
    public IHostBuilder Host { get; }
}
