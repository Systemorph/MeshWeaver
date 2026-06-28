using System.ComponentModel;
using System.Reactive.Linq;
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
            var content = node.ContentAs<TContent>(workspace.Hub.JsonSerializerOptions);
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

        // workspace.GetMeshNodeStream is itself backed by the process-wide
        // IMeshNodeStreamCache (MeshNodeStreamCache.cs) — repeat tracks for the
        // same activity path reuse the warm handle without a second cache layer.
        // Used ONLY to WRITE (stream.Update) the node when it already exists — never
        // to probe an absent path (see the GetQuery read below).
        var stream = workspace.GetMeshNodeStream(activityPath);

        // 🚨 Read existence via GetQuery (empty-on-absent), NEVER a point
        // GetMeshNodeStream(path).Take(1) probe. On a FIRST-time track the activity node
        // does not exist; a point-subscribe to that absent path routes to a RoutingGrain
        // NotFound + SYNC_STREAM OnError. Because TrackLogin sits on the COLD-LOGIN hot path
        // (every cold page load through UserContextMiddleware.TrackLogin), that failing
        // subscribe re-storms the router. A GetQuery over the exact path returns an EMPTY set
        // when the node is absent (the documented empty-on-absent behaviour) — no NotFound, no
        // resubscribe, nothing to storm — and returns typed Content (deserialised through this
        // hub's options) when present. Mirrors AiSettingsNodeType.EnsureExists / AgentChatClient's
        // _Selection read.
        //
        // The increment is still folded onto the LIVE node inside the owner-serialised Update
        // below (FoldOntoLive), so the query's eventual consistency only ever decides
        // create-vs-update — never the AccessCount. The create-vs-update race (two concurrent
        // tracks, or a query that lags a just-created node) is coalesced by the CreateNode catch
        // below, which folds the increment in via stream.Update instead of throwing — no lost count.
        workspace
            .GetQuery($"UserActivity|{activityPath}", $"path:{activityPath} nodeType:UserActivity")
            .Take(1)
            .Select(nodes => nodes.FirstOrDefault(n =>
                string.Equals(n.NodeType, "UserActivity", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(existing =>
            {
                var existingRecord = existing.ContentAs<UserActivityRecord>(hub.JsonSerializerOptions);
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

                // 🚨 Fold the increment onto the LIVE node INSIDE the Update lambda, not a
                // separately-read snapshot. The owner serializes Updates, so each lambda sees
                // the freshest AccessCount and two concurrent tracks can't lose an increment —
                // the old discard-lambda `_ => saveNode` slammed a count computed from the
                // pre-Update probe read. Carry the live version; the owner mints the fresh one.
                MeshNode FoldOntoLive(MeshNode live)
                {
                    var liveRec = live.ContentAs<UserActivityRecord>(hub.JsonSerializerOptions);
                    return live with
                    {
                        NodeType = "UserActivity",
                        Name = req.NodeName ?? encodedPath,
                        MainNode = req.UserId,
                        State = MeshNodeState.Active,
                        Content = record with
                        {
                            AccessCount = (liveRec?.AccessCount ?? 0) + 1,
                            FirstAccessedAt = liveRec?.FirstAccessedAt ?? record.FirstAccessedAt,
                        },
                        Version = live.Version,
                    };
                }

                if (existing != null)
                {
                    logger?.LogDebug(
                        "TrackActivity UPDATE: {Path} count={Count}",
                        activityPath, record.AccessCount);
                    return stream.Update(FoldOntoLive);
                }

                // First-time creation: per-node hub isn't activated yet, RemoteStream
                // can't write. Fall through to IMeshService.CreateNode which routes via
                // CreateNodeRequest and activates the hub. Subsequent tracks land in the
                // Update branch above and reuse the now-warm cached stream.
                //
                // Resolve IMeshService from the MESH ROOT — IMeshService is registered
                // AddScoped, so resolving from this hub's scope (e.g. portal/anonymous)
                // would build a MeshService whose injected IMessageHub is the LOCAL hub,
                // and CreateNode would target the local hub's address. That hub doesn't
                // have a CreateNodeRequest handler → "No handler found for ... in
                // portal/anonymous". Resolving from the mesh root makes MeshService
                // capture the mesh hub as its target.
                // ONBOARD-FIRST GATE: activity tracking must NEVER be the thing that
                // creates a user's partition — partition creation is onboarding's job
                // (UserOnboardingService.CreateUser). Writing this activity node first
                // would lazily create the {userId} schema via the path-routing
                // PendingCreate branch, so a login/navigation BEFORE onboarding would
                // materialise the partition ahead of the User node (the "partition
                // created before onboarding" bug). Probe the user's partition ROOT with
                // a read-only storage read (a read never creates a schema) and skip the
                // write when it's absent — the identity isn't onboarded yet.
                var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
                var meshService = hub.GetMeshHub().ServiceProvider.GetService<IMeshService>();
                var rootProbe = (storage != null
                        ? storage.Read(req.UserId, hub.JsonSerializerOptions).Take(1)
                        : Observable.Return<MeshNode?>(null))
                    .Catch<MeshNode?, Exception>(probeEx =>
                    {
                        logger?.LogDebug(probeEx,
                            "TrackActivity root probe failed for {UserId} — treating as not onboarded.",
                            req.UserId);
                        return Observable.Return<MeshNode?>(null);
                    });

                return rootProbe.SelectMany(userRoot =>
                {
                    if (userRoot is null)
                    {
                        logger?.LogDebug(
                            "TrackActivity SKIP create for {Path}: user '{UserId}' has no partition root yet " +
                            "(not onboarded). Activity tracking must not create a partition ahead of onboarding.",
                            activityPath, req.UserId);
                        return Observable.Empty<MeshNode>();
                    }

                    // First-time creation: per-node hub isn't activated yet, RemoteStream
                    // can't write. Fall through to IMeshService.CreateNode which routes via
                    // CreateNodeRequest and activates the hub. Subsequent tracks land in the
                    // Update branch above and reuse the now-warm cached stream. Resolve
                    // IMeshService from the MESH ROOT so MeshService captures the mesh hub as
                    // its target (a local-scope resolve would target portal/anonymous which
                    // has no CreateNodeRequest handler).
                    if (meshService != null)
                    {
                        logger?.LogDebug("TrackActivity CREATE: {Path}", activityPath);
                        return meshService.CreateNode(saveNode)
                            // Race coalesce: a concurrent track for the same path beat us to
                            // CreateNode — fold our increment in via Update instead of throwing.
                            .Catch<MeshNode, InvalidOperationException>(ex =>
                            {
                                if (!IsAlreadyExistsRace(ex))
                                    return Observable.Throw<MeshNode>(ex);
                                logger?.LogDebug(
                                    "TrackActivity CREATE to UPDATE race for {Path}: another concurrent track won; folding via Update.",
                                    activityPath);
                                return stream.Update(FoldOntoLive);
                            });
                    }

                    return storage?.Write(saveNode, hub.JsonSerializerOptions)
                           ?? Observable.Empty<MeshNode>();
                });
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

    /// <summary>
    /// Registers all graph-related content and message types on the type registry under
    /// their short names, enabling polymorphic serialization/deserialization across hubs.
    /// </summary>
    /// <param name="typeRegistry">The type registry to register the graph types on.</param>
    /// <returns>The same type registry, for chaining.</returns>
    public static ITypeRegistry WithGraphTypes(this ITypeRegistry typeRegistry)
    {
        typeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
        typeRegistry.WithType(typeof(CodeConfiguration), nameof(CodeConfiguration));
        typeRegistry.WithType(typeof(Comment), nameof(Comment));
        typeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        typeRegistry.WithType(typeof(AccessAssignment), nameof(AccessAssignment));
        // PartitionAccessPolicy is a sibling security content type to AccessAssignment and MUST register
        // its $type here too. Without it, a `_Policy` node read across a hub boundary (the GetQuery /
        // MeshNodeStreamCache deserialization path) degrades to an untyped JsonElement, every
        // `Content is PartitionAccessPolicy` soft-cast fails, and the partition's PublicRead policy is
        // silently NOT applied — so PublicRead partitions (Skill, Harness, Provider) read as empty/denied
        // (skills + harnesses "not found"). Registering the discriminator makes the policy type-resolve.
        typeRegistry.WithType(typeof(PartitionAccessPolicy), nameof(PartitionAccessPolicy));
        typeRegistry.WithType(typeof(RoleAssignment), nameof(RoleAssignment));
        typeRegistry.WithType(typeof(Role), nameof(Role));
        typeRegistry.WithType(typeof(AccessObject), nameof(AccessObject));
        typeRegistry.WithType(typeof(GetPermissionRequest), nameof(GetPermissionRequest));
        typeRegistry.WithType(typeof(GetPermissionResponse), nameof(GetPermissionResponse));
        typeRegistry.WithType(typeof(GroupMembership), nameof(GroupMembership));
        typeRegistry.WithType(typeof(MembershipEntry), nameof(MembershipEntry));
        typeRegistry.WithType(typeof(MeshNodeCardControl), nameof(MeshNodeCardControl));
        typeRegistry.WithType(typeof(MeshNodeContentEditorControl), nameof(MeshNodeContentEditorControl));
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
        // Internal compile-dispatch trigger — InstallCompileWatcher posts
        // DispatchCompileTrigger to the per-NodeType hub's own address; the
        // handler runs on the hub's ActionBlock and owns the Pending→Compiling
        // transition + activity dispatch. Type-registry entry is needed even
        // for self-post so the framework's routing/serialisation pipeline can
        // resolve the type-name on the wire.
        typeRegistry.WithType(typeof(DispatchCompileTrigger), nameof(DispatchCompileTrigger));
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
