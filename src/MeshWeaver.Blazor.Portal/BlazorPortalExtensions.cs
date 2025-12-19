using MeshWeaver.Blazor.Chat;
using MeshWeaver.Blazor.Portal.Infrastructure;
using MeshWeaver.Blazor.Portal.Resize;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal;

public static class BlazorPortalExtensions
{
    /// <summary>
    /// Adds portal services including DimensionManager, CacheStorageAccessor, AppVersionService,
    /// and ChatWindowStateService with persistent state support.
    /// Call this on the IServerSideBlazorBuilder returned by AddInteractiveServerComponents().
    /// </summary>
    public static IServerSideBlazorBuilder AddBlazorPortalServices(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddBlazorPortalCoreServices();
        builder.AddChatWindowState();
        return builder;
    }

    /// <summary>
    /// Adds core portal services (DimensionManager, CacheStorageAccessor, AppVersionService).
    /// Use AddBlazorPortalServices(IServerSideBlazorBuilder) for full support including chat state persistence.
    /// </summary>
    public static IServiceCollection AddBlazorPortalCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<DimensionManager>();
        services.AddScoped<CacheStorageAccessor>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        return services;
    }
}
