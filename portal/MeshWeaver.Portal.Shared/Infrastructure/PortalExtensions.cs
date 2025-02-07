using MeshWeaver.Portal.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Portal.Shared.Web.Infrastructure;

public static class PortalExtensions
{
    public static IServiceCollection AddPortalWebServices(this IServiceCollection services)
    {
        return  services.AddSingleton<CacheStorageAccessor>()
            .AddPortalServices();

    }
}
