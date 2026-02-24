using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Extension methods for registering chat services.
/// </summary>
public static class ChatServiceExtensions
{
    /// <summary>
    /// Adds the side panel state service with persistent state support,
    /// and registers the side panel menu provider.
    /// Call this after AddInteractiveServerComponents().
    /// </summary>
    /// <param name="builder">The server-side Blazor builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static IServerSideBlazorBuilder AddSidePanelState(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddScoped<SidePanelStateService>();
        builder.RegisterPersistentService<SidePanelStateService>(RenderMode.InteractiveServer);
        return builder;
    }
}
