using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<ThreadSession>>();
        var request = delivery.Message;
        var hubPath = hub.Address.ToString();

        // Log access context for diagnosing RLS failures
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var identity = delivery.AccessContext?.ObjectId
                       ?? accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;
        logger.LogInformation("HandleCreateThread: namespace={Namespace}, hub={HubPath}, identity={Identity}",
            request.Namespace, hubPath, identity ?? "(none)");

        try
        {
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

            var speakingId = ThreadNodeType.GenerateSpeakingId(request.UserMessageText);
            var ns = string.IsNullOrEmpty(hubPath)
                ? ThreadNodeType.ThreadPartition
                : $"{hubPath}/{ThreadNodeType.ThreadPartition}";

            var name = request.UserMessageText.Length > 60
                ? request.UserMessageText[..57] + "..."
                : request.UserMessageText;

            var createdBy = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;

            var threadNode = new MeshNode(speakingId, ns)
            {
                Name = name,
                NodeType = ThreadNodeType.NodeType,
                Content = new AI.Thread { ParentPath = hubPath, CreatedBy = createdBy }
            };

            logger.LogInformation("HandleCreateThread: creating node at {Path}, createdBy={CreatedBy}",
                threadNode.Path, createdBy ?? "(none)");

            meshService.CreateNodeAsync(threadNode).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var error = t.Exception?.Flatten().InnerExceptions.FirstOrDefault()?.Message
                                ?? t.Exception?.Message ?? "Unknown error";
                    logger.LogError(t.Exception, "HandleCreateThread: FAILED for {Path}: {Error}",
                        threadNode.Path, error);
                    hub.Post(new CreateThreadResponse
                    {
                        Success = false,
                        Error = error
                    }, o => o.ResponseFor(delivery));
                }
                else
                {
                    logger.LogInformation("HandleCreateThread: SUCCESS for {Path}", t.Result.Path);
                    hub.Post(new CreateThreadResponse
                    {
                        Success = true,
                        ThreadPath = t.Result.Path,
                        ThreadName = t.Result.Name
                    }, o => o.ResponseFor(delivery));
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleCreateThread: UNHANDLED EXCEPTION for namespace={Namespace}", request.Namespace);
            hub.Post(new CreateThreadResponse
            {
                Success = false,
                Error = ex.Message
            }, o => o.ResponseFor(delivery));
        }

        return delivery.Processed();
    }
}
