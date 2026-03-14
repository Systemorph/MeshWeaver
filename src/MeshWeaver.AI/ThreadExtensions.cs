using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for adding thread support to mesh node hubs.
/// Registers the CreateThreadRequest handler on each node hub so that
/// threads can be created in the context of any node.
/// </summary>
public static class ThreadExtensions
{
    /// <summary>
    /// Adds thread creation support to a node hub configuration.
    /// Registers the CreateThreadRequest handler which creates Thread nodes
    /// under {hubAddress}/_Thread/{speakingId}.
    /// </summary>
    public static MessageHubConfiguration AddThreadSupport(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(CreateThreadRequest), typeof(CreateThreadResponse))
            .WithHandler<CreateThreadRequest>(HandleCreateThread);

    /// <summary>
    /// Handles CreateThreadRequest — creates a Thread node under {hubAddress}/_Thread/{speakingId}.
    /// Registered on each mesh node hub via AddThreadSupport() so the request is
    /// routed to the context node (not the mesh hub).
    ///
    /// Permission: checks Permission.Update via ISecurityService before creating.
    /// Thread creation maps to Permission.Update (see RlsNodeValidator.GetCreatePermission).
    ///
    /// Runs on a background task to avoid deadlocking the hub's execution pipeline
    /// (CreateNodeAsync internally posts CreateNodeRequest and awaits the callback).
    /// </summary>
    private static Task<IMessageDelivery> HandleCreateThread(
        IMessageHub hub,
        IMessageDelivery<CreateThreadRequest> delivery,
        CancellationToken ct)
    {
        // Capture services and context before entering background task.
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var hubPath = hub.Address.ToString();

        _ = Task.Run(async () =>
        {
            try
            {
                // Check Permission.Update before creating the thread.
                if (securityService != null)
                {
                    var hasPermission = await securityService.HasPermissionAsync(
                        hubPath, Permission.Update);
                    if (!hasPermission)
                    {
                        hub.Post(new CreateThreadResponse
                        {
                            Success = false,
                            Error = "Access denied: you do not have permission to create threads on this node."
                        }, o => o.ResponseFor(delivery));
                        return;
                    }
                }

                var request = delivery.Message;
                var speakingId = ThreadNodeType.GenerateSpeakingId(request.UserMessageText);
                var ns = string.IsNullOrEmpty(hubPath)
                    ? ThreadNodeType.ThreadPartition
                    : $"{hubPath}/{ThreadNodeType.ThreadPartition}";

                // Derive initial name from message text
                var name = request.UserMessageText.Length > 60
                    ? request.UserMessageText[..57] + "..."
                    : request.UserMessageText;

                // Use MeshNode(id, namespace) so the framework auto-sets
                // MainNode = namespace for satellite types.
                var threadNode = new MeshNode(speakingId, ns)
                {
                    Name = name,
                    NodeType = ThreadNodeType.NodeType,
                    Content = new AI.Thread
                    {
                        ParentPath = hubPath
                    }
                };

                // CancellationToken.None — the handler's ct is cancelled
                // after we return delivery.Processed() below.
                var created = await meshService.CreateNodeAsync(threadNode);

                hub.Post(new CreateThreadResponse
                {
                    Success = true,
                    ThreadPath = created.Path,
                    ThreadName = created.Name
                }, o => o.ResponseFor(delivery));
            }
            catch (Exception ex)
            {
                hub.Post(new CreateThreadResponse
                {
                    Success = false,
                    Error = ex.Message
                }, o => o.ResponseFor(delivery));
            }
        }, CancellationToken.None);

        return Task.FromResult<IMessageDelivery>(delivery.Processed());
    }
}
