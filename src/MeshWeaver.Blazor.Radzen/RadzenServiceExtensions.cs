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
        return services;
    }
}
