using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Registers the controlled I/O pools as mesh-scoped singletons.
/// </summary>
public static class IoPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IoPoolRegistry"/> (and its <see cref="IoPoolOptions"/>)
    /// as singletons. Uses <c>TryAddSingleton</c> so a host that pre-registered
    /// custom caps wins. Called once from <c>MeshBuilder</c>; the registry is then
    /// resolvable by every leaf adapter via <c>sp.GetService&lt;IoPoolRegistry&gt;()</c>
    /// and disposed with the mesh.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Optional override of the default caps, e.g. <c>o =&gt; o with { Blob = 64 }</c>.
    /// </param>
    public static IServiceCollection AddIoPools(
        this IServiceCollection services,
        Func<IoPoolOptions, IoPoolOptions>? configure = null)
    {
        services.TryAddSingleton(_ =>
            configure is null ? new IoPoolOptions() : configure(new IoPoolOptions()));
        services.TryAddSingleton<IoPoolRegistry>();
        // The async half of mesh teardown — resources enqueue async cleanup here from
        // their sync Dispose(); the mesh's DisposeAsync drains it before the scope dies.
        services.TryAddSingleton<AsyncDisposeQueue>();
        return services;
    }
}
