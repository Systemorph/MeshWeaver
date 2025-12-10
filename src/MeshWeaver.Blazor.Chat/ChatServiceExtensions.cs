using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Extension methods for registering chat services.
/// </summary>
public static class ChatServiceExtensions
{
    /// <summary>
    /// Adds the chat window state service with persistent state support.
    /// Call this after AddInteractiveServerComponents().
    /// </summary>
    /// <param name="builder">The server-side Blazor builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static IServerSideBlazorBuilder AddChatWindowState(this IServerSideBlazorBuilder builder)
    {
        builder.Services.AddScoped<ChatWindowStateService>();
        builder.RegisterPersistentService<ChatWindowStateService>(RenderMode.InteractiveServer);
        return builder;
    }
}
