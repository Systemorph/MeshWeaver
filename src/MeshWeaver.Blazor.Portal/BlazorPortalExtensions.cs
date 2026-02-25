using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.Blazor.Portal.Infrastructure;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Blazor.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal;

public static class BlazorPortalExtensions
{
    /// <summary>
    /// Adds portal services including DimensionManager, CacheStorageAccessor, AppVersionService,
    /// and SidePanelStateService with persistent state support.
    /// Also registers the "nonfile" route constraint used by ApplicationPage and AreaPage.
    /// Call this on the IServerSideBlazorBuilder returned by AddInteractiveServerComponents().
    /// </summary>
    public static IServerSideBlazorBuilder AddBlazorPortalServices(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddBlazorPortalCoreServices();
        builder.Services.AddNonfileRouteConstraint();
        builder.AddSidePanelState();
        return builder;
    }

    /// <summary>
    /// Adds core portal services (DimensionManager, CacheStorageAccessor, AppVersionService, BlazorAutocompleteService).
    /// Use AddBlazorPortalServices(IServerSideBlazorBuilder) for full support including side panel state persistence.
    /// </summary>
    public static IServiceCollection AddBlazorPortalCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<DimensionManager>();
        services.AddScoped<CacheStorageAccessor>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<BlazorAutocompleteService>();  // Centralized @ autocomplete
        return services;
    }
}
