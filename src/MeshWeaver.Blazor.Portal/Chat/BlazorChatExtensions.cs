using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Extensions for adding Chat Blazor views to the application.
/// </summary>
public static class BlazorChatExtensions
{
    /// <summary>
    /// Adds the Chat Blazor views (ThreadChatView) to the configuration.
    /// Types are registered centrally via AddAI() — no duplicate registration here.
    /// </summary>
    public static MessageHubConfiguration AddChatViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .AddViews(registry => registry
                .WithView<ThreadChatControl, ThreadChatView>());
    }
}
