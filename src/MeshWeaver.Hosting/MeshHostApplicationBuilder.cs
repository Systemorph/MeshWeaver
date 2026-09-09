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
        // 🚨 The deployment's configuration reaches the builder HERE — at construction, before any
        // caller can install a module — because a module attribute's contribution runs inside
        // MeshBuilder.InstallAssemblies and reads MeshBuilder.Configuration to decide what to
        // BUILD (see MeshBuilder.Configuration). A host that binds a mesh to an
        // IHostApplicationBuilder always HAS the configuration; making each composition root
        // remember to hand it over is what failed.
        //
        // MeshBuilderModuleActivation.InstallConfiguredModules also hands it over, and that was
        // believed to be enough. It is not: it covers only the hosts that install modules through
        // THAT helper. The portal composes its own module union (baseline + activation sidecar +
        // platform floor) and calls InstallAssemblies directly, so it never reached the hand-off —
        // and from 16497893b, when the AI engine became an attribute-carried module reading this
        // very property, every deployed portal answered "no configuration" and therefore
        // "Features:StaticRepoSync:Partitions is absent". The AI partitions (Agent, Skill, Harness,
        // Model, Provider) then fell back to in-memory serving, so each in-memory type-definition
        // node was served as the RUNTIME node at its own top-level path and SHADOWED the durable
        // package root there: /Agent and /Skill served the built-in NodeType instead of the
        // authored Store/Plugin node (#2517), and the static-repo import never registered at all,
        // so the Provider partition never materialized (#2507).
        //
        // Configuration is a live ConfigurationManager on this interface, so sources added after
        // this ctor are still visible to whatever reads it later.
        this.WithConfiguration(Host.Configuration);
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
