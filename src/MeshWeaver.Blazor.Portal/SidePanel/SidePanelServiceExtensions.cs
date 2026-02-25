using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal.SidePanel;

/// <summary>
/// Extension methods for registering side panel services.
/// </summary>
public static class SidePanelServiceExtensions
{
    /// <summary>
    /// Adds the side panel state service with persistent state support.
    /// Call this after AddInteractiveServerComponents().
    /// </summary>
    public static IServerSideBlazorBuilder AddSidePanelState(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddScoped<SidePanelStateService>();
        builder.RegisterPersistentService<SidePanelStateService>(RenderMode.InteractiveServer);
        return builder;
    }
}
