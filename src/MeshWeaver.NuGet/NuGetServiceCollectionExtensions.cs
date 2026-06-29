using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// </summary>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddNuGetResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<INuGetPackageCache>(NullNuGetPackageCache.Instance);
        services.TryAddSingleton<INuGetAssemblyResolver, NuGetAssemblyResolver>();
        return services;
    }
}
