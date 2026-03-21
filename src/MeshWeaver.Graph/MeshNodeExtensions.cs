using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for MeshNode.
/// </summary>
public static class MeshNodeExtensions
{
    /// <summary>
    /// Gate name for MeshNode initialization. Messages are deferred until the node
    /// is loaded from persistence (Active) or activated via CreateNodeRequest.
    /// </summary>
    public const string MeshNodeInitGateName = "MeshNodeInit";

    /// <summary>
    /// Updates a MeshNode on an EntityStore stream.
    /// Reads the current MeshNode, applies the update function, and pushes the change.
    /// </summary>
    public static void UpdateMeshNode(this ISynchronizationStream<EntityStore> stream,
        string nodePath, Func<MeshNode, MeshNode> update)
    {
        stream.Update(state =>
        {
            var store = state ?? new EntityStore();
            var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
            var current = collection?.Instances.GetValueOrDefault(nodePath) as MeshNode;
            if (current == null)
                throw new InvalidOperationException(
                    $"MeshNode '{nodePath}' not found in stream. Available: [{string.Join(", ", collection?.Instances.Keys.Select(k => k.ToString()) ?? [])}]");

            var updated = update(current);
            if (string.IsNullOrEmpty(updated.Id))
                throw new InvalidOperationException(
                    $"UpdateMeshNode produced a node with empty Id for path '{nodePath}'");

            var newStore = store.Update(nameof(MeshNode), c => c.Update(nodePath, updated));
            return stream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                [new EntityUpdate(nameof(MeshNode), nodePath, updated) { OldValue = current }],
                stream.StreamId));
        }, ex =>
        {
            var logger = stream.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Graph.UpdateMeshNode");
            logger?.LogError(ex, "UpdateMeshNode failed for {NodePath}", nodePath);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Updates a MeshNode via the workspace. Uses GetStream for the local hub
    /// or GetRemoteStream for a remote hub address.
    /// </summary>
    public static void UpdateMeshNode(this IWorkspace workspace,
        Address? address, string nodePath, Func<MeshNode, MeshNode> update)
    {
        if (address == null || address.Equals(workspace.Hub.Address))
        {
            workspace.GetStream(typeof(MeshNode))?.UpdateMeshNode(nodePath, update);
        }
        else
        {
            var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                address, new MeshNodeReference());
            stream?.Update(current =>
            {
                if (current == null) return null;
                var updated = update(current);
                return new ChangeItem<MeshNode>(updated, stream.StreamId, stream.StreamId,
                    ChangeType.Patch, stream.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), nodePath, updated) { OldValue = current }]);
            });
        }
    }

    /// <summary>
    /// Updates a MeshNode's content with a typed update function.
    /// Reads the current content as TContent, applies the update, and pushes the change.
    /// Uses GetStream for the local hub or GetRemoteStream for a remote address.
    /// </summary>
    public static void UpdateMeshNode<TContent>(this IWorkspace workspace,
        Address? address, string nodePath, Func<MeshNode, TContent, MeshNode> update)
        where TContent : class
    {
        workspace.UpdateMeshNode(address, nodePath, node =>
        {
            var content = node.Content as TContent;
            return content != null ? update(node, content) : node;
        });
    }

    /// <summary>
    /// Gets the parent path for this node.
    /// Returns null for root-level nodes.
    /// </summary>
    public static string? GetParentPath(this MeshNode node) =>
        GetParentPath(node.Path);

    /// <summary>
    /// Gets the parent path from a given path string.
    /// Returns null for root-level paths.
    /// </summary>
    public static string? GetParentPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 1 ? null : string.Join("/", segments.Take(segments.Length - 1));
    }

    /// <summary>
    /// Gets the primary node path for this node.
    /// For satellite nodes, returns the MainNode path.
    /// For regular nodes, returns the node's own path.
    /// </summary>
    public static string GetPrimaryPath(this MeshNode node)
    {
        return node.MainNode;
    }

    /// <summary>
    /// Registers all graph-related content types with the type registry for polymorphic deserialization.
    /// This is the global registry for content types — used by the import tool, persistence layer,
    /// and runtime serialization. All built-in content types must be registered here.
    /// </summary>
    public static MessageHubConfiguration WithGraphTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry.WithGraphTypes();
        return config
            .WithHandler<TrackActivityRequest>(HandleTrackActivity);
    }

    private static IMessageDelivery HandleTrackActivity(
        IMessageHub hub,
        IMessageDelivery<TrackActivityRequest> delivery)
    {
        var req = delivery.Message;
        var storage = hub.ServiceProvider.GetService<IStorageService>();
        if (storage == null)
            return delivery.Processed();

        var options = hub.JsonSerializerOptions;
        var subHub = hub.GetHostedHub(new Address("_activity"));
        subHub.InvokeAsync(async ct =>
        {
            var encodedPath = req.NodePath.Replace("/", "_");
            var activityPath = $"User/{req.UserId}/_UserActivity/{encodedPath}";
            var now = DateTimeOffset.UtcNow;

            var existing = await storage.GetNodeAsync(activityPath, options, ct);
            var existingRecord = existing?.Content as UserActivityRecord;

            var record = new UserActivityRecord
            {
                Id = encodedPath,
                NodePath = req.NodePath,
                UserId = req.UserId,
                ActivityType = ActivityType.Read,
                FirstAccessedAt = existingRecord?.FirstAccessedAt ?? now,
                LastAccessedAt = now,
                AccessCount = (existingRecord?.AccessCount ?? 0) + 1,
                NodeName = req.NodeName,
                NodeType = req.NodeType,
                Namespace = req.Namespace
            };

            await storage.SaveNodeAsync(
                MeshNode.FromPath(activityPath) with
                {
                    NodeType = "UserActivity",
                    Name = req.NodeName ?? encodedPath,
                    MainNode = $"User/{req.UserId}",
                    State = MeshNodeState.Active,
                    Content = record
                },
                options, ct);
        }, ex =>
        {
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Graph.ActivityTracking");
            logger?.LogError(ex, "Failed to track activity for user={UserId} path={Path}", req.UserId, req.NodePath);
            return Task.CompletedTask;
        });

        return delivery.Processed();
    }

    public static ITypeRegistry WithGraphTypes(this ITypeRegistry typeRegistry)
    {
        typeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
        typeRegistry.WithType(typeof(CodeConfiguration), nameof(CodeConfiguration));
        typeRegistry.WithType(typeof(Comment), nameof(Comment));
        typeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        typeRegistry.WithType(typeof(AccessAssignment), nameof(AccessAssignment));
        typeRegistry.WithType(typeof(RoleAssignment), nameof(RoleAssignment));
        typeRegistry.WithType(typeof(Role), nameof(Role));
        typeRegistry.WithType(typeof(AccessObject), nameof(AccessObject));
        typeRegistry.WithType(typeof(GroupMembership), nameof(GroupMembership));
        typeRegistry.WithType(typeof(MembershipEntry), nameof(MembershipEntry));
        typeRegistry.WithType(typeof(MeshNodeCardControl), nameof(MeshNodeCardControl));
        typeRegistry.WithType(typeof(Approval), nameof(Approval));
        typeRegistry.WithType(typeof(ApprovalStatus), nameof(ApprovalStatus));
        typeRegistry.WithType(typeof(TrackedChange), nameof(TrackedChange));
        typeRegistry.WithType(typeof(TrackedChangeType), nameof(TrackedChangeType));
        typeRegistry.WithType(typeof(TrackedChangeStatus), nameof(TrackedChangeStatus));
        typeRegistry.WithType(typeof(Notification), nameof(Notification));
        typeRegistry.WithType(typeof(NotificationType), nameof(NotificationType));
        typeRegistry.WithType(typeof(ApiToken), nameof(ApiToken));
        typeRegistry.WithType(typeof(MeshDataSourceConfiguration), nameof(MeshDataSourceConfiguration));
        typeRegistry.WithType(typeof(PartitionDefinition), nameof(PartitionDefinition));
        return typeRegistry;
    }
}
