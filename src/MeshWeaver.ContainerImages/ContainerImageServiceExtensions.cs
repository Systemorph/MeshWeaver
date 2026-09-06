using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Registers the mirror's own services. The two things a host must still supply itself are the
/// <see cref="System.Net.Http.HttpClient"/> that reaches the upstream and the
/// <see cref="IContainerImageAuthenticator"/> that decides who may pull — the first because a
/// portal configures its own handler pipeline, the second because it is the seam this assembly
/// deliberately does not implement.
/// </summary>
public static class ContainerImageServiceExtensions
{
    /// <summary>
    /// Adds the read-through cache.
    ///
    /// <para>🚨 A SINGLETON, and that is load-bearing rather than incidental: the cache's eviction
    /// accounting is an INSTANCE field on it, so its lifetime must be the mesh's. A scoped or
    /// transient registration would reset the sweep counter on every request and the directory
    /// would grow past its budget forever — and a <c>static</c> one would outlive the mesh and
    /// bleed across tests and tenants, which is the failure this codebase refuses outright.</para>
    ///
    /// <para>The cache is inert until <see cref="ContainerImageOptions.CacheDirectory"/> is set,
    /// so registering it unconditionally costs nothing.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The collection, for chaining.</returns>
    public static IServiceCollection AddContainerImageMirror(this IServiceCollection services)
    {
        services.AddSingleton<ContainerBlobCache>();
        return services;
    }
}
