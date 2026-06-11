using Microsoft.Extensions.DependencyInjection;
using Radzen;

namespace MeshWeaver.Blazor.Radzen;

public static class RadzenServiceExtensions
{
    /// <summary>
    /// Adds Radzen Blazor services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The configured service collection for method chaining.</returns>
    public static IServiceCollection AddRadzenServices(this IServiceCollection services)
    {
        services.AddRadzenComponents();
        // Instance type-generator (memoization cache lives and dies with this
        // ServiceProvider — no process-wide static cache, see NoStaticState.md).
        services.AddSingleton<DynamicTypeGenerator>();
        return services;
    }
}
