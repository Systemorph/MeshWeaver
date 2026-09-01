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
    /// <see cref="MeshNodeStreamHandle.Update{TContent}(Func{MeshNode, TContent, MeshNode})"/>.
    /// Returns <see cref="IObservable{MeshNode}"/>; <b>callers MUST Subscribe</b> — the cold
    /// observable's side effect only runs on Subscribe. See
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// <para>🚨 Unconvertible content faults the observable rather than skipping the write. This
    /// used to read <c>content != null ? update(node, content) : node</c> — a SILENT no-op: when
    /// the content arrived as JSON this reported success while writing nothing at all, so the
    /// caller's change simply vanished with no exception and no log. Absence and
    /// "could not be read" are now distinct (see the handle's overload docs).</para>
    /// </summary>
    public static IObservable<MeshNode> UpdateMeshNode<TContent>(this IWorkspace workspace,
        string nodePath, Func<MeshNode, TContent, MeshNode> update)
        where TContent : class
        => workspace.GetMeshNodeStream(nodePath)
            .Update<TContent>((node, content) => content is null ? node : update(node, content));

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
    /// 🚨 THE PARTITION ROOT OF A NODE, LIVE — the one read package-mark inheritance needs
    /// (<see cref="MeshNodeImageHelper.ResolveNodeIcon(MeshNode?, MeshNode?)"/>, issue #2075 item 2).
    ///
    /// <para><b>Why a stream and not a lookup.</b> The icon resolver is deliberately pure, total and
    /// synchronous over ONE node, so the root has to reach it from outside. This is the seam that
    /// fetches it, and it is an <see cref="IObservable{T}"/> like every other read in the mesh: a
    /// page CombineLatests it beside the node's own stream, so re-marking a package re-paints every
    /// page under it with no invalidation to write.</para>
    ///
    /// <para><b>A point read that is legitimate.</b> Reading one node by exact path is only ever
    /// correct for a path known to exist — a point read of an absent node answers a routing NotFound
    /// that opens the storm-breaker on it. A partition root exists for every node that has one, by
    /// construction: it is the namespace the node lives in. And when there is no distinct root
    /// (<see cref="MeshNodeImageHelper.PartitionRootPath"/> null — the node IS a root) NOTHING is
    /// read at all; the stream is a constant null.</para>
    ///
    /// <para><b>Starts null, upgrades.</b> The page renders on the node's own stream immediately and
    /// the mark arrives when the root does — the root read can never delay, or gate, a page that
    /// does not depend on it.</para>
    ///
    /// <para>🚨 <b>One fault is a STATE, not an error.</b> Access to a node does not imply access to
    /// its partition root — an <c>AccessAssignment</c> can share a single node out of a partition
    /// the viewer is not a member of — so a denial here means "this viewer inherits nothing", the
    /// same normal state <c>MeshNodeThumbnailControl.ShouldSurfaceStreamError</c> already names for
    /// the same reason. It is classified by
    /// <see cref="MeshWeaver.Layout.AreaErrorClassifier.IsExpectedUserActionFailure"/> and nothing
    /// else is caught: a genuine infrastructure fault propagates to the page, because a decoration
    /// quietly swallowing infrastructure faults is how a broken mesh renders as a working one.</para>
    /// </summary>
    /// <param name="workspace">The workspace to read through.</param>
    /// <param name="nodePath">🚨 A MESH NODE path — the guarantee above ("a partition root exists
    /// for every node that has one") is a statement about node paths, and only about those. A
    /// layout area passes <c>host.Hub.Address.Path</c>, which is the same value it already treats as
    /// the node path for permissions and URLs.</param>
    /// <returns>The partition root as it changes, starting with null; constant null when the node
    /// has no distinct partition root.</returns>
    public static IObservable<MeshNode?> ObservePartitionRoot(this IWorkspace workspace, string? nodePath)
    {
        if (MeshNodeImageHelper.PartitionRootPath(nodePath) is not { } rootPath)
            return Observable.Return<MeshNode?>(null);

        return workspace.GetMeshNodeStream(rootPath)
            .Select(root => (MeshNode?)root)
            .Catch<MeshNode?, Exception>(ex =>
                MeshWeaver.Layout.AreaErrorClassifier.IsExpectedUserActionFailure(ex)
                    ? Observable.Return<MeshNode?>(null)
                    : Observable.Throw<MeshNode?>(ex))
            .StartWith((MeshNode?)null)
            // Only the two fields inheritance reads. The root node emits on every touch of it
            // (LastModified, content edits, children); without this every such emission would
            // re-render every page in the partition for an icon that did not change.
            .DistinctUntilChanged(root => (root?.Path, root?.Icon));
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

        // 🚨 ORIGINATE FROM THE DEDICATED ACTIVITY HUB — never the calling
        // (portal / per-connection / MCP back-connection) hub. Running the write
        // from the caller opened its IMeshNodeStreamCache sync subscription on
        // that hub's cache (cache/{connectionId}), whose initial-state /
        // PatchDataResponse routed back through a TRANSIENT, UNREGISTERED mesh
        // root (mesh/{connectionId}) → [ROUTE] NotFound → 30s "no initial state
        // arrived within 30s" stall → the reconnecting MCP client was rejected.
        // The tracking hub is hosted off the mesh ROOT and resolves the shared,
        // registered mesh-root cache (cache/{meshRootId}). See ActivityTrackingHub.
        var activityHub = hub.GetActivityTrackingHub();
        var workspace = activityHub.GetWorkspace();
        // The tracking hub's own JSON options (WithGraphTypes) know UserActivityRecord —
        // the caller's hub may not, so use the tracking hub's for typed round-trip.
        var jsonOptions = activityHub.JsonSerializerOptions;

        // Each hub has its OWN AccessService; the caller's per-delivery AccessContext
        // lives on the CALLING hub's. Capture it here (handler thread, where it is set)
        // and re-establish it on the write hubs' AccessServices across each cold write's
        // Subscribe via RunAs (see AsCaller below) — so the activity write is attributed to the
        // acting user and owner-side RLS lets it land. Mirrors GitSync ActivityRunner's
        // per-write owner re-stamp; AsyncLocal does not survive the Rx scheduler hops.
        var callerAccess = hub.ServiceProvider.GetService<AccessService>();
        var callerCtx = callerAccess?.Context ?? callerAccess?.CircuitContext;
        var meshRoot = activityHub.GetMeshHub();
        var trackingAccess = activityHub.ServiceProvider.GetService<AccessService>();
        var rootAccess = meshRoot.ServiceProvider.GetService<AccessService>();

        // 🚨 RunAs, NEVER `Observable.Using(() => access.SwitchAccessContext(ctx), …)` (#1444/#1790,
        // and #3023 for this site). `Using` opens the AsyncLocal on the SUBSCRIBING thread and
        // disposes it when the inner observable TERMINATES — which for a cross-hub write is
        // whichever IoPool/response thread it finishes on, not the one that subscribed. The
        // subscriber (an MCP session hub turn, the portal host hub behind a click) is then left
        // latched as THIS caller for everything it does next, and AccessContext is what
        // PermissionEvaluator reads: a wrong-principal condition that fails open in the direction
        // of the previous user, silently. `ActivityRunner` carries the same warning verbatim.
        //
        // RunAs seals both ends inside one Subscribe (SubscribeScopedObservable). A null access
        // service or a null identity runs the work unswitched and still deferred, so the old
        // `callerCtx is null` short-circuit is preserved by construction rather than by a branch.
        IObservable<T> AsCaller<T>(Func<IObservable<T>> work) =>
            trackingAccess.RunAs(
                callerCtx,
                () => rootAccess is not null && !ReferenceEquals(rootAccess, trackingAccess)
                    ? rootAccess.RunAs(callerCtx, work)
                    : work());

        var encodedPath = req.NodePath.Replace("/", "_");
        // Activity records live under {userId}/_UserActivity/{id} — every user
        // owns a top-level partition named after their userId, and the
        // _UserActivity satellite holds their navigation/login records.
        var activityPath = $"{req.UserId}/_UserActivity/{encodedPath}";
        var now = DateTimeOffset.UtcNow;

        logger?.LogDebug(
            "TrackActivity ENTER: userId={UserId} activityPath={Path} type={ActivityType} via activityHub={ActivityHub}",
            req.UserId, activityPath, req.ActivityType, activityHub.Address);

        // workspace.GetMeshNodeStream is backed by the mesh ROOT's shared
        // IMeshNodeStreamCache (resolved by DI fallback from the tracking hub) —
        // repeat tracks for the same activity path reuse the warm handle. Used ONLY
        // to WRITE (stream.Update) the node when it already exists — never to probe an
        // absent path (see the GetQuery read below).
        var stream = workspace.GetMeshNodeStream(activityPath);

        // First-time creation resolves IMeshService / IStorageAdapter from the mesh ROOT —
        // IMeshService is AddScoped, so resolving from a leaf scope would target a hub with
        // no CreateNodeRequest handler. The ONBOARD-FIRST gate below probes the user's
        // partition root (a read never creates a schema) and skips the write when it's absent.
        var storage = meshRoot.ServiceProvider.GetService<IStorageAdapter>();
        var meshService = meshRoot.ServiceProvider.GetService<IMeshService>();

        // 🚨 Read existence via GetQuery (empty-on-absent), NEVER a point
        // GetMeshNodeStream(path).Take(1) probe. On a FIRST-time track the activity node
        // does not exist; a point-subscribe to that absent path routes to a RoutingGrain
        // NotFound + SYNC_STREAM OnError. Because TrackLogin sits on the COLD-LOGIN hot path
        // (every cold page load through UserContextMiddleware.TrackLogin), that failing
        // subscribe re-storms the router. A GetQuery over the exact path returns an EMPTY set
        // when the node is absent (the documented empty-on-absent behaviour) — no NotFound, no
        // resubscribe, nothing to storm — and returns typed Content when present.
        //
        // The increment is still folded onto the LIVE node inside the owner-serialised Update
        // below (FoldOntoLive), so the query's eventual consistency only ever decides
        // create-vs-update — never the AccessCount. The create-vs-update race is coalesced by
        // the CreateNode catch below, which folds the increment in via stream.Update.
        // 🚨 Build (and subscribe) the whole read+write pipeline UNDER the caller's identity.
        // WrapWithPerUserRls (SyncedQueryDataSourceExtensions) captures AccessService.Context
        // EAGERLY at the GetQuery(...) call — on the workspace's hub (the tracking hub) — so the
        // per-user RLS filter must see the caller's identity AT THAT CALL, not only at the write's
        // subscribe. RunAs enters the scope BEFORE invoking the work factory, so
        // GetQuery is called with the caller's context established on the tracking hub's
        // AccessService (fail-closed when callerCtx is null: no context ⇒ empty userId ⇒ the RLS
        // wrap yields no rows for the exact-path existence probe, and the subsequent write posts
        // context-null and is rejected by PostPipeline). The inner per-write AsCaller calls
        // still re-establish the identity on each write's own emission thread —
        // AsyncLocal does not flow across Rx scheduler hops, so the outer scope alone is not enough.
        var pipeline = AsCaller(
            () => workspace
            .GetQuery($"UserActivity|{activityPath}", $"path:{activityPath} nodeType:UserActivity select:path,id,namespace,name,nodeType,content")
            .Take(1)
            .Select(nodes => nodes.FirstOrDefault(n =>
                string.Equals(n.NodeType, "UserActivity", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(existing =>
            {
                var existingRecord = existing.ContentAs<UserActivityRecord>(jsonOptions);
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
                // the freshest AccessCount and two concurrent tracks can't lose an increment.
                MeshNode FoldOntoLive(MeshNode live)
                {
                    var liveRec = live.ContentAs<UserActivityRecord>(jsonOptions);
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

                // Each write runs under the acting user via AsCaller/RunAs: the scope is
                // entered at the inner Subscribe (where the write primitive captures
                // AccessContext) and left on that same Subscribe rather than on whatever
                // thread the write happens to terminate on,
                // so cross-hub RLS lets it land. See ActivityRunner for the canonical shape.
                if (existing != null)
                {
                    // 🚨 `record.AccessCount` is the count derived from the EVENTUALLY-CONSISTENT
                    // query snapshot above — it is NOT the count this write lands. The value that
                    // is written comes from FoldOntoLive, which re-reads AccessCount off the LIVE
                    // node inside the owner-serialised Update. Naming it `count=` made two
                    // concurrent tracks that merely READ the same stale snapshot look like two
                    // writers racing the same increment (#3001's second hypothesis, which the
                    // owner's three-way merge in fact already prevents). Say which number it is.
                    logger?.LogDebug(
                        "TrackActivity UPDATE: {Path} querySnapshotCount={SnapshotCount} "
                        + "(the written count is folded off the live node inside the Update)",
                        activityPath, record.AccessCount);
                    return AsCaller(() => stream.Update(FoldOntoLive));
                }

                var rootProbe = AsCaller(() => storage != null
                        ? storage.Read(req.UserId, jsonOptions).Take(1)
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

                    if (meshService != null)
                    {
                        logger?.LogDebug("TrackActivity CREATE: {Path}", activityPath);
                        return AsCaller(() => meshService.CreateNode(saveNode))
                            // Race coalesce: a concurrent track for the same path beat us to
                            // CreateNode — fold our increment in via Update instead of throwing.
                            .Catch<MeshNode, InvalidOperationException>(ex =>
                            {
                                if (!IsAlreadyExistsRace(ex))
                                    return Observable.Throw<MeshNode>(ex);
                                logger?.LogDebug(
                                    "TrackActivity CREATE to UPDATE race for {Path}: another concurrent track won; folding via Update.",
                                    activityPath);
                                return AsCaller(() => stream.Update(FoldOntoLive));
                            });
                    }

                    return storage != null
                        ? AsCaller(() => storage.Write(saveNode, jsonOptions))
                        : Observable.Empty<MeshNode>();
                });
            }));

        // The write is DETACHED from this request on purpose — TrackLogin sits on the cold-login
        // hot path and must not stall behind a node write — so the delivery is Processed below
        // while the pipeline is still running. Faults are observed (they surface at Error, never
        // as an unobserved exception), but "observed" is not "awaited": nothing about this
        // subscription belongs to the request any more.
        //
        // 🚨 That is why it REGISTERS. Orleans tracks work on an activation's scheduler inside a
        // request; a detached subscription on the thread pool is precisely what escapes that, so it
        // cannot keep the activation alive. Without the registration, a deactivation landing in
        // this window runs CancelCurrentExecution() + Dispose() on the hub the write is using and
        // kills it mid-flight — invisibly, because the request succeeded long ago. Registering
        // makes the hub's own disposal wait for it (bounded — see ActivityWriteTracker.Drain).
        var tracker = activityHub.ServiceProvider.GetService<ActivityWriteTracker>();
        var inFlight = tracker?.Begin(activityPath);
        pipeline
            // Finally, not the observer's onCompleted: the registration must clear on EVERY
            // termination — success, error, or an unsubscribe forced by disposal — or a single
            // failed write would keep the tracker non-empty and make every later shutdown burn the
            // full drain budget waiting for something that already ended.
            .Finally(() => inFlight?.Dispose())
            .Subscribe(
                _ => logger?.LogDebug("TrackActivity DONE: {Path}", activityPath),
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
        // The compile control-plane's own state, mirrored onto the NodeType node and therefore
        // serialised by every per-NodeType hub that compiles. Unregistered it is auto-registered
        // under its short name on that hub's first write, with the resolver's "register it
        // explicitly" warning — which is asking for exactly this line, so that a receiving hub
        // resolves it typed rather than as an untyped JsonElement.
        typeRegistry.WithType(typeof(NodeTypeCompileState), nameof(NodeTypeCompileState));
        typeRegistry.WithType(typeof(Comment), nameof(Comment));
        // Registered beside Comment, not with the collaboration module: Comment.Status is a
        // CommentStatus, so a _Comment satellite read on a mesh without the module would resolve
        // the record but degrade its Status to an untyped JsonElement — the silent-null class.
        typeRegistry.WithType(typeof(CommentStatus), nameof(CommentStatus));
        // Moved-node redirect declarations. MUST be registered: the redirect node is read on the
        // hub that resolves the navigation, NOT on its own hub, so without the $type the content
        // degrades to an untyped JsonElement and the declaration silently reads as absent — the
        // redirect would just stop working, with no error anywhere.
        typeRegistry.WithType(typeof(NodeRedirect), nameof(NodeRedirect));
        typeRegistry.WithType(typeof(RedirectScope), nameof(RedirectScope));
        typeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        // Slide MeshNode Content — presentation pages (see SlideNodeType). Registered
        // under the short name so slide nodes round-trip typed across hub boundaries.
        typeRegistry.WithType(typeof(SlideContent), nameof(SlideContent));
        // Deck MeshNode Content — the EXTERNAL, ordered slide manifest (see DeckNodeType).
        // Registered under the short name so a Deck round-trips typed across hub boundaries;
        // The Publish packs'"s slide views read a slide'"s parent Deck node to resolve the play order.
        typeRegistry.WithType(typeof(DeckContent), nameof(DeckContent));
        // Backend-computed editable-field metadata sent to the GUI inside the node-content editor control.
        typeRegistry.WithType(typeof(MeshNodeEditorField), nameof(MeshNodeEditorField));
        // Security content types (AccessAssignment / PartitionAccessPolicy / RoleAssignment / Role).
        // Each MUST register its $type here: without it, the node read across a hub boundary (the
        // GetQuery / MeshNodeStreamCache deserialization path) degrades to an untyped JsonElement,
        // every `Content is X` soft-cast fails, and the grant/policy is silently NOT applied — e.g. a
        // PublicRead partition (Skill, Harness, Provider) reads as empty/denied ("not found").
        //
        // LEGACY READ-COMPAT: a node persisted by a hub that lacked this registration carries a
        // namespace-qualified FULL-name $type (e.g. "MeshWeaver.Mesh.Security.PartitionAccessPolicy" —
        // the _Provider/_Policy storm). Register the full name as a READ alias FIRST, then the short
        // nameof LAST so this hub keeps WRITING the short name (nameByType is last-write-wins) while
        // resolving BOTH on read. PolymorphicTypeInfoResolver now warns whenever an unregistered type
        // is serialized, so any NEW full-name node is surfaced at its publishing hub, not silently stored.
        typeRegistry.WithType(typeof(AccessAssignment), typeof(AccessAssignment).FullName!);
        typeRegistry.WithType(typeof(AccessAssignment), nameof(AccessAssignment));
        typeRegistry.WithType(typeof(PartitionAccessPolicy), typeof(PartitionAccessPolicy).FullName!);
        typeRegistry.WithType(typeof(PartitionAccessPolicy), nameof(PartitionAccessPolicy));
        typeRegistry.WithType(typeof(RoleAssignment), typeof(RoleAssignment).FullName!);
        typeRegistry.WithType(typeof(RoleAssignment), nameof(RoleAssignment));
        typeRegistry.WithType(typeof(Role), typeof(Role).FullName!);
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
        // App — the per-user installed-app record ({user}/_App/{appId}, AppNodeType). Read by the
        // home's Apps grid on the USER hub (a cross-hub GetQuery), so without this registration the
        // content degrades to an untyped JsonElement and every installed app silently vanishes
        // from the grid.
        typeRegistry.WithType(typeof(App), nameof(App));
        // Email — the content of the built-in "Email" NodeType (EmailNodeType). It was the ONE
        // built-in content type missing from this list, and the omission was invisible until a
        // CROSS-HUB writer produced one: an in-process writer (EmailInboundProcessor) carries the
        // content typed past ContentDiscriminatorValidator, but a foreign hub's write arrives as
        // JSON with `$type: "Email"`, which the validator then correctly refuses as unresolvable —
        // that broke the Store contact form's notification phase in production (2026-08-12). Every
        // content type a built-in NodeType declares via WithContentType MUST be registered here;
        // the validator's strict branch assumes exactly that.
        typeRegistry.WithType(typeof(Email), nameof(Email));
        typeRegistry.WithType(typeof(EmailDirection), nameof(EmailDirection));
        typeRegistry.WithType(typeof(EmailStatus), nameof(EmailStatus));
        // EventSubscription — the content of the built-in "EventSubscription" NodeType and the
        // durable record behind every deferred reaction (email-invite → grant on sign-up, a timed
        // action, a delegated sub-thread reaching a resting state). It was the ONE content type
        // whose reader is a BACKGROUND SERVICE rather than a view, which is why the omission read
        // as silence instead of an empty render: EventSubscriptionRunner tracks its pending set
        // through workspace.GetQuery on the mesh hub, and that hub — which never WRITES an
        // EventSubscription on a cold boot — could not resolve the $type, so every node degraded
        // to an untyped JsonElement and the pending set came back EMPTY. With no pending set the
        // change-feed, trigger-node-watch, Timer and NodeStatus firing paths have no candidates at
        // all, and an invited user who signs up gets nothing until the next restart's cold-start
        // reconcile (Timer/NodeStatus subscriptions, which that reconcile does not cover, never
        // fire). Observed on every prod boot from 2026-07 (issue #1392): a dozen Admin/
        // EventSubscription grant nodes logging "stayed an untyped JsonElement".
        typeRegistry.WithType(typeof(EventSubscription), nameof(EventSubscription));
        typeRegistry.WithType(typeof(EventSubscriptionStatus), nameof(EventSubscriptionStatus));
        typeRegistry.WithType(typeof(EventTriggerType), nameof(EventTriggerType));
        typeRegistry.WithType(typeof(EventContinuationType), nameof(EventContinuationType));
        // ScheduledAction — the LEGACY predecessor EventSubscription supersedes. Still a built-in
        // NodeType, and EventSubscriptionRunner's startup migration reads it the same way, so it
        // needs the same registration: unresolvable, the migration silently folds nothing and an
        // in-flight legacy invite stays stranded forever.
        typeRegistry.WithType(typeof(ScheduledAction), nameof(ScheduledAction));
        typeRegistry.WithType(typeof(ScheduledActionStatus), nameof(ScheduledActionStatus));
        typeRegistry.WithType(typeof(ScheduledActionKind), nameof(ScheduledActionKind));
        typeRegistry.WithType(typeof(ApiToken), nameof(ApiToken));
        typeRegistry.WithType(typeof(MeshDataSourceConfiguration), nameof(MeshDataSourceConfiguration));
        typeRegistry.WithType(typeof(PartitionDefinition), nameof(PartitionDefinition));
        // Build protocol (Doc/Architecture/BuildCoordination) — the coordination state on
        // Admin/Build and its chunk children. Unregistered, the readiness subscription on every
        // silo would read the GO signal as an untyped JsonElement and never go ready.
        typeRegistry.WithType(typeof(BuildState), nameof(BuildState));
        typeRegistry.WithType(typeof(BuildStatus), nameof(BuildStatus));
        typeRegistry.WithType(typeof(BuildClaimRequest), nameof(BuildClaimRequest));
        typeRegistry.WithType(typeof(BuildGo), nameof(BuildGo));
        // Compile trigger contract — CreateReleaseRequest / RunTests* are the
        // UI-facing triggers on the per-NodeType hub. (The cross-hub
        // RunCompileRequest/Response pair was deleted with the activity-dispatch
        // path it served; the compile now runs inline in RunCompile.)
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
