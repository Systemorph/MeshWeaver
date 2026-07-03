using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.Blazor.Portal.Infrastructure;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Blazor.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal;

/// <summary>
/// Extension methods for registering Blazor portal services (dimension management, cache storage,
/// app version, autocomplete, side panel state, and the route constraints used by portal pages).
/// </summary>
public static class BlazorPortalExtensions
{
    /// <summary>
    /// Adds portal services including DimensionManager, CacheStorageAccessor, AppVersionService,
    /// and SidePanelStateService with persistent state support.
    /// Call this on the IServerSideBlazorBuilder returned by AddInteractiveServerComponents().
    /// Static-asset path exclusion for the catch-all page is an endpoint convention
    /// (NonfileRouteConstraintExtensions.ExcludeStaticAssetPaths on MapRazorComponents),
    /// not a DI ConstraintMap registration — the Blazor Router never honors custom
    /// inline constraints, so ":nonfile" in a page template must never come back.
    /// </summary>
    public static IServerSideBlazorBuilder AddBlazorPortalServices(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddBlazorPortalCoreServices();
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
        services.AddScoped<BlazorAutocompleteService>();  // Centralized @ autocomplete (scoped: depends on scoped IMeshService)
        return services;
    }
}
