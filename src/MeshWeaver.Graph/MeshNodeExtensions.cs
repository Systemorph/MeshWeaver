using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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
using Microsoft.Extensions.Caching.Memory;
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
         Func<MeshNode, MeshNode> update, string? nodePath = null)
    {
        // Get the data source's own EntityStore stream — this is the same stream that
        // CreateSynchronizationStream reduces from, so updates propagate to all subscribers.
        var workspace = stream.Host.GetWorkspace();
        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        if (dataSource == null)
            throw new InvalidOperationException("No data source registered for MeshNode");
        var dsStream = dataSource.GetStreamForPartition(null)
            ?? throw new InvalidOperationException("No stream for MeshNode partition");

        dsStream.Update(state =>
        {
            var store = state ?? new EntityStore();
            var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
            if (collection is null)
                throw new InvalidOperationException(
                    $"MeshNode collection not found in stream. Available collections: [{string.Join(", ", store.Collections.Keys)}]");

            var nodeId = nodePath is null ? null : nodePath.Contains('/') ? nodePath[(nodePath.LastIndexOf('/') + 1)..] : nodePath;
            var current = (nodeId is null ?
                collection.Instances.Values.FirstOrDefault() : collection.Instances.GetValueOrDefault(nodeId)) as MeshNode;
            if (current == null)
                throw new InvalidOperationException(
                    $"MeshNode '{nodePath}' (id='{nodeId}') not found in stream. Available: [{string.Join(", ", collection.Instances.Keys.Select(k => k.ToString()))}]");

            var updated = update(current);
            if (string.IsNullOrEmpty(updated.Id))
                throw new InvalidOperationException(
                    $"UpdateMeshNode produced a node with empty Id for path '{nodePath}'");

            var newStore = store.Update(nameof(MeshNode), c => c.Update(updated.Id, updated));
            return dsStream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }],
                dsStream.StreamId));
        }, ex =>
        {
            var logger = stream.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Graph.UpdateMeshNode");
            logger?.LogError(ex, "UpdateMeshNode failed for {NodePath}", nodePath);
        });
    }

    /// <summary>
    /// Updates a MeshNode's content with a typed update function.
    /// Path-aware typed-content update wrapper that delegates to
    /// <see cref="MeshNodeStreamHandle.Update"/>. Returns
    /// <see cref="IObservable{MeshNode}"/>; <b>callers MUST Subscribe</b> — the cold
    /// observable's side effect only runs on Subscribe. See
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    public static IObservable<MeshNode> UpdateMeshNode<TContent>(this IWorkspace workspace,
        string nodePath, Func<MeshNode, TContent, MeshNode> update)
        where TContent : class
        => workspace.GetMeshNodeStream(nodePath).Update(node =>
        {
            var content = node.Content as TContent;
            return content != null ? update(node, content) : node;
        });

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

    // Per-workspace cache of activity stream handles. The handle (and its underlying
    // RemoteStream subscription) stays warm for ActivityStreamCacheTtl after each touch
    // — repeat tracks for the same activity path skip the per-node-hub cold-subscribe
    // round-trip. Keyed by IWorkspace via ConditionalWeakTable so caches GC with their
    // workspace and we don't pin dead hubs in long-lived test processes.
    private static readonly ConditionalWeakTable<IWorkspace, IMemoryCache> _activityStreamCaches = new();
    private static readonly TimeSpan ActivityStreamCacheTtl = TimeSpan.FromMinutes(5);

    private static MeshNodeStreamHandle GetCachedActivityStream(IWorkspace workspace, string path)
    {
        var cache = _activityStreamCaches.GetValue(workspace, _ => new MemoryCache(new MemoryCacheOptions()));
        return cache.GetOrCreate(path, entry =>
        {
            entry.SlidingExpiration = ActivityStreamCacheTtl;
            return workspace.GetMeshNodeStream(path);
        })!;
    }

    private static IMessageDelivery HandleTrackActivity(
        IMessageHub hub,
        IMessageDelivery<TrackActivityRequest> delivery)
    {
        var req = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.ActivityTracking");

        // Guard: userId must be the User MeshNode's Id (e.g. "alice"), not the
        // email. UserContextMiddleware.TryLoadMeshUserAsync resolves email →
        // username before posting, but the lookup can fail (User node missing,
        // brand-new mesh, transient query error). An email-shaped userId would
        // build an activity path containing '@', which the Address parser
        // interprets as a hub-host separator — the resulting path is
        // unaddressable and every routing attempt logs [ROUTE] NotFound until
        // the request finally gives up. Better to skip with one warning than
        // to spam the route layer with unresolvable paths.
        if (string.IsNullOrEmpty(req.UserId)
            || req.UserId.Contains('@')
            || req.NodePath.Contains('@'))
        {
            logger?.LogWarning(
                "TrackActivity skipped: userId={UserId} nodePath={NodePath} — " +
                "expected username, got email/empty. UserContextMiddleware's " +
                "email→username resolution failed upstream; tracking with this " +
                "shape would build unaddressable paths.",
                req.UserId, req.NodePath);
            return delivery.Processed();
        }

        var workspace = hub.GetWorkspace();
        var encodedPath = req.NodePath.Replace("/", "_");
        // Activity records live under {userId}/_UserActivity/{id} — every user
        // owns a top-level partition named after their userId, and the
        // _UserActivity satellite holds their navigation/login records.
        var activityPath = $"{req.UserId}/_UserActivity/{encodedPath}";
        var now = DateTimeOffset.UtcNow;

        logger?.LogDebug(
            "TrackActivity ENTER: userId={UserId} activityPath={Path} type={ActivityType}",
            req.UserId, activityPath, req.ActivityType);

        var stream = GetCachedActivityStream(workspace, activityPath);

        // Read latest from the cached RemoteStream → fold into new record → write.
        // The probe's two miss-modes:
        //   1. Per-node hub doesn't exist yet (first-ever track for this path):
        //      routing returns DeliveryFailureException("No node found at ...")
        //      almost immediately — the SubscribeRequest fails fast.
        //   2. Hub exists but hasn't emitted Initial within 2 s: TimeoutException.
        // Both mean "treat as first-time create". We catch any exception on the
        // probe and route through to CreateNode.
        //
        // Known race: two concurrent tracks for the same path both see the
        // miss probe and both attempt CreateNode → one wins, the other gets
        // InvalidOperationException("Node already exists"). The CreateNode
        // call below catches that race and falls through to a stream.Update
        // so the second tracker's increment isn't lost.
        stream.Take(1).Timeout(TimeSpan.FromSeconds(2))
            .Catch<MeshNode, Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "TrackActivity probe miss for {Path} ({Kind}) — treating as first-time create.",
                    activityPath, ex.GetType().Name);
                return Observable.Return<MeshNode>(null!);
            })
            .SelectMany(existing =>
            {
                var existingRecord = existing?.Content as UserActivityRecord;
                var record = new UserActivityRecord
                {
                    Id = encodedPath,
                    NodePath = req.NodePath,
                    UserId = req.UserId,
                    // Honour the request's ActivityType — Login events from the
                    // auth middleware fold in here alongside Read events from
                    // navigation. Same persistence path, different filter axis.
                    ActivityType = req.ActivityType,
                    FirstAccessedAt = existingRecord?.FirstAccessedAt ?? now,
                    LastAccessedAt = now,
                    AccessCount = (existingRecord?.AccessCount ?? 0) + 1,
                    NodeName = req.NodeName,
                    NodeType = req.NodeType,
                    Namespace = req.Namespace
                };
                var saveNode = MeshNode.FromPath(activityPath) with
                {
                    NodeType = "UserActivity",
                    Name = req.NodeName ?? encodedPath,
                    MainNode = req.UserId,
                    State = MeshNodeState.Active,
                    Content = record
                };

                if (existing != null)
                {
                    logger?.LogDebug(
                        "TrackActivity UPDATE: {Path} count={Count}",
                        activityPath, record.AccessCount);
                    return stream.Update(_ => saveNode);
                }

                // First-time creation: per-node hub isn't activated yet, RemoteStream
                // can't write. Fall through to IMeshService.CreateNode which routes via
                // CreateNodeRequest and activates the hub. Subsequent tracks land in the
                // Update branch above and reuse the now-warm cached stream.
                var meshService = hub.ServiceProvider.GetService<IMeshService>();
                if (meshService != null)
                {
                    logger?.LogDebug(
                        "TrackActivity CREATE: {Path}",
                        activityPath);
                    return meshService.CreateNode(saveNode)
                        // Race coalesce: a concurrent track for the same path
                        // beat us to CreateNode — fold our increment in via
                        // Update on the now-warm stream instead of throwing.
                        .Catch<MeshNode, InvalidOperationException>(ex =>
                        {
                            if (!IsAlreadyExistsRace(ex))
                                return Observable.Throw<MeshNode>(ex);
                            logger?.LogDebug(
                                "TrackActivity CREATE→UPDATE race for {Path}: another concurrent track won; folding via Update.",
                                activityPath);
                            return stream.Update(_ => saveNode);
                        });
                }

                var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
                return storage?.Write(saveNode, hub.JsonSerializerOptions)
                       ?? Observable.Empty<MeshNode>();
            })
            .Subscribe(
                _ => logger?.LogDebug(
                    "TrackActivity DONE: {Path}", activityPath),
                ex => logger?.LogError(ex,
                    "Failed to track activity for user={UserId} path={Path}",
                    req.UserId, req.NodePath));
        return delivery.Processed();
    }

    /// <summary>
    /// True for the specific "Node already exists" signal raised by
    /// <c>MeshService.CreateNode</c> when persistence rejects a duplicate
    /// path. Distinguishes the concurrent-track race from genuine
    /// <see cref="InvalidOperationException"/>s (validation failures,
    /// missing parent, etc.) which must still surface as errors.
    /// </summary>
    private static bool IsAlreadyExistsRace(InvalidOperationException ex)
        => ex.Message.StartsWith("Node already exists:", StringComparison.Ordinal);

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
        typeRegistry.WithType(typeof(GetPermissionRequest), nameof(GetPermissionRequest));
        typeRegistry.WithType(typeof(GetPermissionResponse), nameof(GetPermissionResponse));
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
        // Compile trigger / activity contract — the per-NodeType hub posts
        // RunCompileRequest to its compile-activity hub and must deserialise
        // the RunCompileResponse that comes back; CreateReleaseRequest /
        // RunTests* are the UI-facing triggers on the same hub.
        typeRegistry.WithType(typeof(RunCompileRequest), nameof(RunCompileRequest));
        typeRegistry.WithType(typeof(RunCompileResponse), nameof(RunCompileResponse));
        typeRegistry.WithType(typeof(CreateReleaseRequest), nameof(CreateReleaseRequest));
        typeRegistry.WithType(typeof(CreateReleaseResponse), nameof(CreateReleaseResponse));
        typeRegistry.WithType(typeof(RunTestsRequest), nameof(RunTestsRequest));
        typeRegistry.WithType(typeof(RunTestsResponse), nameof(RunTestsResponse));
        // Release MeshNode Content carries NodeTypeRelease. Without this entry
        // the polymorphic serializer falls back to FullName on the wire,
        // receiving hubs lack a matching short-name registration, and the
        // payload arrives as JsonElement — the pinned-release branch in
        // EnrichWithNodeType then can't cast back to NodeTypeRelease, logs
        // "pinned release could not be resolved", and the per-instance hub
        // falls through to the error overlay. Repro:
        // CodeEditRecompileTest.NodeType_RequestedReleasePath_PinsToHistoricalRelease.
        typeRegistry.WithType(typeof(NodeTypeRelease), nameof(NodeTypeRelease));
        return typeRegistry;
    }
}
