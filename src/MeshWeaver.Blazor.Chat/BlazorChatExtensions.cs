using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Extensions for adding Chat Blazor views to the application.
/// </summary>
public static class BlazorChatExtensions
{
    /// <summary>
    /// Adds the Chat Blazor views (AgentChatView, ThreadChatView) to the configuration.
    /// </summary>
    public static MessageHubConfiguration AddChatViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(AgentChatControl), typeof(ThreadChatControl))
            .AddViews(registry => registry
                .WithView<AgentChatControl, AgentChatView>()
                .WithView<ThreadChatControl, ThreadChatView>());
    }
}
