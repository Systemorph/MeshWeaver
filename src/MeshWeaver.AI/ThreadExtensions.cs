using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for adding thread support to mesh node hubs.
/// </summary>
public static class ThreadExtensions
{
    public static MessageHubConfiguration AddThreadSupport(this MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return configuration
            .WithHandler<CreateThreadRequest>(HandleCreateThread);
    }

    /// <summary>
    /// Handles CreateThreadRequest — fully sync handler.
    /// meshService.CreateNodeAsync routes to the mesh hub internally and uses
    /// RegisterCallback (non-blocking). ContinueWith posts response when done.
    /// </summary>
    private static IMessageDelivery HandleCreateThread(
        IMessageHub hub,
        IMessageDelivery<CreateThreadRequest> delivery)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var hubPath = hub.Address.ToString();
        var request = delivery.Message;

        var speakingId = ThreadNodeType.GenerateSpeakingId(request.UserMessageText);
        var ns = string.IsNullOrEmpty(hubPath)
            ? ThreadNodeType.ThreadPartition
            : $"{hubPath}/{ThreadNodeType.ThreadPartition}";

        var name = request.UserMessageText.Length > 60
            ? request.UserMessageText[..57] + "..."
            : request.UserMessageText;

        // Capture current user for "my threads" filtering across partitions
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var createdBy = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;

        var threadNode = new MeshNode(speakingId, ns)
        {
            Name = name,
            NodeType = ThreadNodeType.NodeType,
            Content = new AI.Thread { ParentPath = hubPath, CreatedBy = createdBy }
        };

        meshService.CreateNodeAsync(threadNode).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                hub.Post(new CreateThreadResponse
                {
                    Success = false,
                    Error = t.Exception?.InnerException?.Message ?? t.Exception?.Message ?? "Unknown error"
                }, o => o.ResponseFor(delivery));
            }
            else
            {
                hub.Post(new CreateThreadResponse
                {
                    Success = true,
                    ThreadPath = t.Result.Path,
                    ThreadName = t.Result.Name
                }, o => o.ResponseFor(delivery));
            }
        });

        return delivery.Processed();
    }
}
