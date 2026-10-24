using System;
using System.Linq;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// Wires the injectors into a single-process (monolith) test mesh. The multi-silo form lives in
/// <c>MeshWeaver.Hosting.Orleans.TestBase</c> (<c>FaultInjectionCluster</c>), which wraps every silo's
/// store the same way and relays commits between them.
/// </summary>
public static class FaultInjectionMeshExtensions
{
    /// <summary>
    /// Makes the mesh's store of record a <see cref="FaultInjectingStorageAdapter"/> over a fresh
    /// in-memory adapter, resolvable as <c>GetRequiredService&lt;FaultInjectingStorageAdapter&gt;()</c>.
    ///
    /// <para>🚨 Call it BEFORE the base configuration adds in-memory persistence (in a
    /// <c>MonolithMeshTestBase</c>: before <c>base.ConfigureMesh(builder)</c>). Persistence
    /// <c>TryAdd</c>s its adapter and then stacks the platform's guards over whatever adapter is
    /// registered, so registering first is what puts the injector UNDER every guard, where a slow
    /// backend sits. Registered after, it is ignored.</para>
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    public static MeshBuilder AddFaultInjectingStorage(this MeshBuilder builder)
        => builder.ConfigureServices(services =>
        {
            services.AddSingleton(sp => new FaultInjectingStorageAdapter(
                new InMemoryStorageAdapter(sp.GetService<ILogger<InMemoryStorageAdapter>>())));
            services.AddSingleton<IStorageAdapter>(sp => sp.GetRequiredService<FaultInjectingStorageAdapter>());
            return services;
        });

    /// <summary>
    /// Wraps the OUTERMOST <see cref="IStorageAdapter"/> registration — the whole production decorator
    /// chain — in a <see cref="FaultInjectingStorageAdapter"/>, resolvable both as
    /// <c>GetRequiredService&lt;FaultInjectingStorageAdapter&gt;()</c> and as the mesh's
    /// <see cref="IStorageAdapter"/>. A fault injected here is met by the CALLER's own read or write
    /// (the create handler, the per-node hub's own save) before any guard sees it, which is where a
    /// test about how that caller classifies a store fault needs it.
    ///
    /// <para>🚨 The opposite placement to <see cref="AddFaultInjectingStorage"/>: call it AFTER the
    /// base configuration has registered persistence (in a <c>MonolithMeshTestBase</c>: on the
    /// builder <c>base.ConfigureMesh(builder)</c> returned). It throws when there is nothing to wrap,
    /// because an unwrapped store would make every fault the test arms silently inert. Use one of the
    /// two per mesh, never both.</para>
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="arm">Optional: arms faults on the adapter as it is constructed, for a fault that
    /// must be in force from the mesh's first operation.</param>
    public static MeshBuilder AddFaultInjectingStorageOverTheChain(
        this MeshBuilder builder, Action<FaultInjectingStorageAdapter>? arm = null)
        => builder.ConfigureServices(services => WrapOutermostStorageAdapter(services, arm));

    /// <summary>
    /// The private key the wrapped chain stays registered under, so the container still OWNS what it
    /// built: a factory- or type-registered adapter (the production chain's outermost decorator,
    /// <c>SubtreeDeletionGuardStorageAdapter</c>, releases its admitted-write connections on
    /// <c>Dispose</c>) is disposed with the provider, and an instance registration stays non-owned.
    /// </summary>
    private const string WrappedChainKey = "MeshWeaver.Testing.FaultInjection.WrappedStorageChain";

    /// <summary>
    /// Moves the outermost <see cref="IStorageAdapter"/> registration under <see cref="WrappedChainKey"/>
    /// — same lifetime, same form — and registers a <see cref="FaultInjectingStorageAdapter"/> over it.
    /// </summary>
    internal static IServiceCollection WrapOutermostStorageAdapter(
        IServiceCollection services, Action<FaultInjectingStorageAdapter>? arm)
    {
        var outermost = services.LastOrDefault(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService)
            ?? throw new InvalidOperationException(
                "No IStorageAdapter is registered yet, so there is no chain to wrap — call "
                + "AddFaultInjectingStorageOverTheChain after the base configuration adds persistence.");
        services.Remove(outermost);
        services.Add(Rekeyed(outermost));
        services.AddSingleton(sp =>
        {
            var adapter = new FaultInjectingStorageAdapter(
                sp.GetRequiredKeyedService<IStorageAdapter>(WrappedChainKey), "mesh (over the chain)");
            arm?.Invoke(adapter);
            return adapter;
        });
        services.AddSingleton<IStorageAdapter>(sp => sp.GetRequiredService<FaultInjectingStorageAdapter>());
        return services;
    }

    /// <summary>The removed registration, unchanged but for its key — whichever form it took.</summary>
    private static ServiceDescriptor Rekeyed(ServiceDescriptor descriptor)
        => descriptor.ImplementationInstance is { } instance
            ? new ServiceDescriptor(typeof(IStorageAdapter), WrappedChainKey, instance)
            : descriptor.ImplementationFactory is { } factory
                ? new ServiceDescriptor(typeof(IStorageAdapter), WrappedChainKey, (sp, _) => factory(sp), descriptor.Lifetime)
                : descriptor.ImplementationType is { } type
                    ? new ServiceDescriptor(typeof(IStorageAdapter), WrappedChainKey, type, descriptor.Lifetime)
                    : throw new InvalidOperationException(
                        "The IStorageAdapter registration is neither an instance, a factory nor a type, "
                        + "so it cannot be wrapped.");

    /// <summary>
    /// Adds a <see cref="HeldFirstFrameQueryProvider"/> for <paramref name="partition"/> to the query
    /// fan-in, resolvable as <c>GetRequiredService&lt;HeldFirstFrameQueryProvider&gt;()</c>. It answers
    /// at once until <see cref="HeldFirstFrameQueryProvider.Hold"/> is called.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="partition">The partition whose first query frames it can hold.</param>
    public static MeshBuilder AddHeldFirstFrame(this MeshBuilder builder, string partition)
        => builder.ConfigureServices(services =>
        {
            services.AddSingleton(new HeldFirstFrameQueryProvider(partition));
            services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<HeldFirstFrameQueryProvider>());
            return services;
        });
}
