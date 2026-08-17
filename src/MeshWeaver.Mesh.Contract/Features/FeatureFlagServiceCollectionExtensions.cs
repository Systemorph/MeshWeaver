using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Features;

/// <summary>Registers the deployment's feature-flag reader as a mesh-scoped singleton.</summary>
public static class FeatureFlagServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IFeatureFlags"/> over the host's <see cref="IConfiguration"/>. Called
    /// once from <c>MeshBuilder</c>; <c>TryAdd</c> so a host (or a test) that registered its own
    /// reader first wins. A mesh built with no <see cref="IConfiguration"/> in DI declares no flags
    /// rather than throwing — a flag surface is a deployment concern, not a hard dependency.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddFeatureFlags(this IServiceCollection services)
    {
        services.TryAddSingleton<IFeatureFlags>(sp => new ConfigurationFeatureFlags(
            sp.GetService<IConfiguration>(),
            sp.GetService<ILogger<ConfigurationFeatureFlags>>()));
        return services;
    }
}
