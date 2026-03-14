using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Extensions for adding Chat Blazor views to the application.
/// </summary>
public static class BlazorChatExtensions
{
    /// <summary>
    /// Adds the Chat Blazor views (ThreadChatView) to the configuration.
    /// </summary>
    public static MessageHubConfiguration AddChatViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(ThreadChatControl),
                typeof(CreateThreadRequest), typeof(CreateThreadResponse),
                typeof(SubmitMessageRequest), typeof(SubmitMessageResponse),
                typeof(CancelThreadStreamRequest))
            .AddChatTypes()
            .AddViews(registry => registry
                .WithView<ThreadChatControl, ThreadChatView>());
    }

    /// <summary>
    /// Registers Chat/Thread-related types for JSON serialization.
    /// </summary>
    private static MessageHubConfiguration AddChatTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry
            .WithType(typeof(AI.Thread), nameof(AI.Thread))
            .WithType(typeof(ThreadMessage), nameof(ThreadMessage))
            .WithType(typeof(CreateNodeRequest), nameof(CreateNodeRequest))
            .WithType(typeof(CreateNodeResponse), nameof(CreateNodeResponse))
            .WithType(typeof(DeleteNodeRequest), nameof(DeleteNodeRequest))
            .WithType(typeof(DeleteNodeResponse), nameof(DeleteNodeResponse))
            .WithType(typeof(CreateThreadRequest), nameof(CreateThreadRequest))
            .WithType(typeof(CreateThreadResponse), nameof(CreateThreadResponse))
            .WithType(typeof(CancelThreadStreamRequest), nameof(CancelThreadStreamRequest))
            .WithType(typeof(ThreadCellReference), nameof(ThreadCellReference));
        return config;
    }
}
