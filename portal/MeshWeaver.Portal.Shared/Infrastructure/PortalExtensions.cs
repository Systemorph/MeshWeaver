using MeshWeaver.Portal.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Portal.Shared.Infrastructure;

public static class PortalExtensions
{
    public static IServiceCollection AddPortalServices(this IServiceCollection services)
    {
        return  services.AddSingleton<CacheStorageAccessor>()
            .AddSingleton<IAppVersionService, AppVersionService>();
    }
}
