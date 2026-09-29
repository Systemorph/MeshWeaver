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
