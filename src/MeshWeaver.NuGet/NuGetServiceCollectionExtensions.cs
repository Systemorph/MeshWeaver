using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.NuGet;

/// <summary>
/// Dependency-injection registration helpers for the NuGet assembly resolver.
/// </summary>
public static class NuGetServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NuGet assembly resolver and a default no-op package cache as singletons.
    /// Both registrations use <c>TryAdd</c>, so a previously registered <c>INuGetPackageCache</c>
    /// (e.g. blob- or filesystem-backed) is preserved.
    ///
    /// <para>The resolver is built by an EXPLICIT factory on the mesh's
    /// <see cref="IoPoolNames.Http"/> pool, resolved with <c>GetRequiredService</c>: a container
    /// without an <see cref="IoPoolRegistry"/> fails loudly here instead of the resolver running
    /// unbounded, which is what leaving the choice to constructor selection risked.</para>
    /// </summary>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddNuGetResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<INuGetPackageCache>(NullNuGetPackageCache.Instance);
        services.TryAddSingleton<INuGetAssemblyResolver>(sp => new NuGetAssemblyResolver(
            sp.GetRequiredService<ILogger<NuGetAssemblyResolver>>(),
            sp.GetRequiredService<IoPoolRegistry>(),
            sp.GetRequiredService<INuGetPackageCache>(),
            sp.GetService<ReleaseLane>()));
        return services;
    }
}
