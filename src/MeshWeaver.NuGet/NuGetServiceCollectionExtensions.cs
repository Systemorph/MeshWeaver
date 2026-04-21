using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.NuGet;

public static class NuGetServiceCollectionExtensions
{
    public static IServiceCollection AddNuGetResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<INuGetPackageCache>(NullNuGetPackageCache.Instance);
        services.TryAddSingleton<INuGetAssemblyResolver, NuGetAssemblyResolver>();
        return services;
    }
}
