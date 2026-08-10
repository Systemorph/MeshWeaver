using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Extension methods for mesh configuration and type registration.
/// </summary>
public static class MeshExtensions
{
    /// <summary>
    /// Registers mesh-related types with the hub's type registry.
    /// </summary>
    /// <param name="config">The message hub configuration.</param>
    /// <returns>The configured message hub configuration.</returns>
    public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
    {
        // Register mesh-related types with short names for data consistency
        // Using short names ensures TypeSource registrations use the same collection name
        config.TypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
        config.TypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
        config.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
        config.TypeRegistry.WithType(typeof(MeshNodeState), nameof(MeshNodeState));
        // MeshChangeEvent rides over the Orleans memory stream for the cross-silo change-feed
        // relay (OrleansMeshChangeFeed). The Orleans allowed-types gate (MeshTypeNameFilter) only
        // permits IMessageDelivery + types the hub's ITypeRegistry knows; unregistered, a
        // MeshChangeEvent published to the stream is rejected at the manifest gate and never
        // reaches a cross-silo subscriber. Register it (short name) so it can cross that boundary.
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Services.MeshChangeEvent), nameof(MeshWeaver.Mesh.Services.MeshChangeEvent));
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Services.MeshChangeKind), nameof(MeshWeaver.Mesh.Services.MeshChangeKind));
        // AccessContext rides as a TYPED field on every IMessageDelivery. Unregistered, the
        // polymorphic resolver stamps it a full-name $type ("MeshWeaver.Messaging.AccessContext") —
        // harmless for the typed field (it round-trips) but it's ongoing log noise (the
        // PolymorphicTypeInfoResolver "serializing UNREGISTERED type" warning fires once per hub) and
        // dirties persisted deliveries. Register it (full-name READ alias FIRST, short name LAST) so
        // it serialises with a stable short discriminator on every hub that applies AddMeshTypes.
        config.TypeRegistry.WithType(typeof(MeshWeaver.Messaging.AccessContext), typeof(MeshWeaver.Messaging.AccessContext).FullName!);
        config.TypeRegistry.WithType(typeof(MeshWeaver.Messaging.AccessContext), nameof(MeshWeaver.Messaging.AccessContext));
        // Core identity/activity node-content types live in THIS assembly but were never registered
        // anywhere, so any hub reading a {user} root node ("User") or its _UserActivity satellite
        // ("UserActivityRecord") got an untyped JsonElement ("TypeRegistry lacks the $type
        // discriminator") → "renders empty, reactive waits time out" — the chat-window-disappears /
        // home-areas-hang-on-"awaiting first data" class of bug. Register them in the core registry
        // every host applies. (PartitionAccessPolicy is already registered via AddGraph; the AI
        // partition hubs additionally get all three via AddAITypes.)
        // Full-name READ alias FIRST (legacy nodes persisted with a full-name $type), short nameof LAST
        // so this hub keeps WRITING the short name. See WithGraphTypes for the full rationale.
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Security.User), typeof(MeshWeaver.Mesh.Security.User).FullName!);
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Security.User), nameof(MeshWeaver.Mesh.Security.User));
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Activity.UserActivityRecord), typeof(MeshWeaver.Mesh.Activity.UserActivityRecord).FullName!);
        config.TypeRegistry.WithType(typeof(MeshWeaver.Mesh.Activity.UserActivityRecord), nameof(MeshWeaver.Mesh.Activity.UserActivityRecord));
        config.TypeRegistry.WithType(typeof(CreateNodeRequest), nameof(CreateNodeRequest));
        config.TypeRegistry.WithType(typeof(CreateNodeResponse), nameof(CreateNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeCreationRejectionReason), nameof(NodeCreationRejectionReason));
        config.TypeRegistry.WithType(typeof(DeleteNodeRequest), nameof(DeleteNodeRequest));
        config.TypeRegistry.WithType(typeof(DeleteNodeResponse), nameof(DeleteNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeDeletionRejectionReason), nameof(NodeDeletionRejectionReason));
        config.TypeRegistry.WithType(typeof(MoveNodeRequest), nameof(MoveNodeRequest));
        config.TypeRegistry.WithType(typeof(MoveNodeResponse), nameof(MoveNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeMoveRejectionReason), nameof(NodeMoveRejectionReason));
        config.TypeRegistry.WithType(typeof(CopyNodeRequest), nameof(CopyNodeRequest));
        config.TypeRegistry.WithType(typeof(CopyNodeResponse), nameof(CopyNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeCopyRejectionReason), nameof(NodeCopyRejectionReason));
        config.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
        config.TypeRegistry.WithType(typeof(ExecuteScriptRequest), nameof(ExecuteScriptRequest));
        config.TypeRegistry.WithType(typeof(ExecuteScriptResponse), nameof(ExecuteScriptResponse));

        // Per-node pre-flight delete validation. Posted by HandleDeleteNodeRequest to each
        // node in the subtree. Owning hub runs local INodeValidators + domain rules.
        config.TypeRegistry.WithType(typeof(ValidateDeleteRequest), nameof(ValidateDeleteRequest));
        config.TypeRegistry.WithType(typeof(ValidateDeleteResponse), nameof(ValidateDeleteResponse));

        // NodeType compilation lookup. Posted by NodeTypeService → owning per-node hub
        // (per the GetCompilationPathRequest contract). Registered here so that any hub
        // posting the request (e.g. from another silo or via a portal client) shares the
        // same short type name and the JSON discriminator round-trips.
        config.TypeRegistry.WithType(typeof(GetCompilationPathRequest), nameof(GetCompilationPathRequest));
        config.TypeRegistry.WithType(typeof(GetCompilationPathResponse), nameof(GetCompilationPathResponse));

        // Explicit compile trigger + test runner. Posted from layout area buttons and tests
        // to the owning NodeType hub, which checks IsUpToDate and flips CompilationStatus.
        config.TypeRegistry.WithType(typeof(CreateReleaseRequest), nameof(CreateReleaseRequest));
        config.TypeRegistry.WithType(typeof(CreateReleaseResponse), nameof(CreateReleaseResponse));
        config.TypeRegistry.WithType(typeof(RunTestsRequest), nameof(RunTestsRequest));
        config.TypeRegistry.WithType(typeof(RunTestsResponse), nameof(RunTestsResponse));

        // Import/Delete types
        config.TypeRegistry.WithType(typeof(ImportNodesRequest), nameof(ImportNodesRequest));
        config.TypeRegistry.WithType(typeof(ImportNodesResponse), nameof(ImportNodesResponse));
        config.TypeRegistry.WithType(typeof(ImportContentRequest), nameof(ImportContentRequest));
        config.TypeRegistry.WithType(typeof(ImportContentResponse), nameof(ImportContentResponse));
        config.TypeRegistry.WithType(typeof(SyncContentFilesRequest), nameof(SyncContentFilesRequest));
        config.TypeRegistry.WithType(typeof(InlineContentFile), nameof(InlineContentFile));
        config.TypeRegistry.WithType(typeof(DeleteContentRequest), nameof(DeleteContentRequest));
        config.TypeRegistry.WithType(typeof(DeleteContentResponse), nameof(DeleteContentResponse));

        return config;
    }

    /// <summary>
    /// Overrides the default 30-second ceiling applied to mesh persistence operations
    /// (create, update, delete, move). Raise this for long-running tests or batch jobs;
    /// lower it to fail faster in environments where slow ops are suspicious.
    /// </summary>
    public static MessageHubConfiguration WithMeshOperationTimeout(
        this MessageHubConfiguration config, TimeSpan timeout)
        => config.WithServices(services =>
        {
            services.AddSingleton(new MeshOperationOptions { Timeout = timeout });
            return services;
        });

    private sealed record NodeOperationHandlersMarker;

    private sealed record NodeOperationExecutionMarker;

    /// <summary>
    /// Declares this hub a node-CRUD EXECUTION TARGET: <see cref="NodeOperationTarget"/> will send
    /// <see cref="CreateNodeRequest"/> and friends here instead of to the root mesh hub. Implies
    /// <see cref="WithNodeOperationHandlers"/>; idempotent.
    ///
    /// <para>🚨 <b>This is deliberately NOT the same marker as
    /// <see cref="WithNodeOperationHandlers"/>.</b> Every per-node hub registers the handlers (via
    /// <c>AddMeshDataSource</c>) because it must be able to RECEIVE node ops addressed to its own
    /// node — the delete fan-out posts <c>DeleteNodeRequest</c> to <c>new Address(path)</c>, and the
    /// create handler forwards its <c>Argument</c> the same way. Handling ops for YOUR node says
    /// nothing about being a good place to EXECUTE someone else's write, so the two capabilities need
    /// two markers. Conflating them targets a caller's own node hub, and per-node hubs are exactly
    /// the ones carrying <c>AddAccessControlPipeline</c>, whose
    /// <c>CreateNodePermissionAttribute</c> anchors its check at the RECEIVING HUB's path: a learner
    /// copying a course module into their own partition was denied
    /// "lacks Create permission on 'TestCourse/Module1/Ex1'" — Create evaluated on the read-only
    /// source they were merely rendering. Silent privilege reduction, and the exact hazard
    /// <c>EducationLayoutAreas.EnsurePersonalCopy</c> documents.</para>
    ///
    /// <para>So this marker goes only on hubs that are a genuine off-router work queue and carry no
    /// per-node access pipeline: the dedicated <c>import/{id}</c> hub, an MCP session hub, the Blazor
    /// portal hub.</para>
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <returns>The configuration for chaining.</returns>
    public static MessageHubConfiguration WithNodeOperationExecution(this MessageHubConfiguration config)
        => config.Get<NodeOperationExecutionMarker>() is not null
            ? config
            : config.Set(new NodeOperationExecutionMarker()).WithNodeOperationHandlers();

    /// <summary>
    /// The address node CRUD (<see cref="CreateNodeRequest"/> and friends) must be TARGETED at: the
    /// NEAREST hub up the parent chain that opted in via <see cref="WithNodeOperationExecution"/>
    /// and is not the root mesh hub.
    ///
    /// <para>🚨 <b>The mesh hub is the ROUTER — it must not execute work.</b> Targeting it makes every
    /// create/delete/move run on the router's own action block; a burst starves real
    /// <c>SubscribeRequest</c> traffic and the whole portal wedges (prod 2026-06-11:
    /// "11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/&lt;self&gt; stale &gt;60s"). The
    /// dedicated <c>import/{id}</c> hub exists precisely to keep bulk creates off the router, but
    /// <c>MeshService</c> used to post to <c>GetMeshHub()</c> unconditionally — so that isolation
    /// bought nothing. This walk is what makes it real.</para>
    ///
    /// <para>When NO ancestor opted in — the common case for a per-node hub (a thread hub creating
    /// its <c>_Notification</c> satellite, a NodeType <c>Source</c> hub writing a compile activity)
    /// — the operation lands on the mesh's dedicated OFF-ROUTER execution hub
    /// (<see cref="NodeOperationFallbackTarget"/>), never on the router itself.</para>
    /// </summary>
    /// <param name="hub">The hub issuing the operation.</param>
    /// <returns>The address to target.</returns>
    public static Address NodeOperationTarget(this IMessageHub hub)
    {
        // Walk with the SAME self-reference guard GetMeshHub uses: a hub whose ParentHub resolves
        // to itself (the root) would otherwise spin this loop forever — an infinite loop on the
        // CRUD path, which presents as a hang, not an error.
        var current = hub;
        while (current is not null)
        {
            if (current.Configuration.Get<NodeOperationExecutionMarker>() is not null
                && !string.Equals(current.Address.Type, AddressExtensions.MeshType, StringComparison.Ordinal))
                return current.Address;
            var parent = current.Configuration.ParentHub;
            if (parent is null || ReferenceEquals(parent, current))
                break;
            current = parent;
        }
        return NodeOperationFallbackTarget(hub);
    }

    /// <summary>
    /// Address-id prefix of the mesh's dedicated node-operation execution hub. A <c>portal/</c>
    /// address on purpose: <c>portal</c> is already a stream-routed address type
    /// (<c>MeshConfiguration.DefaultStreamRoutedAddressTypes</c>), so on Orleans the RoutingGrain
    /// dispatches to it over the cluster-wide memory stream exactly as it does for the MCP session
    /// hubs and the gRPC transport hub — no new address type, no new routing rule.
    /// </summary>
    private const string NodeOperationHubPrefix = "nodeops-";

    /// <summary>
    /// The address of <paramref name="mesh"/>'s dedicated node-operation execution hub. Pure
    /// address arithmetic — it does NOT materialise the hub, so it is safe to call from inside a
    /// node-operation handler (materialising re-enters <c>GetHostedHub</c>).
    /// </summary>
    private static Address NodeOperationHubAddress(IMessageHub mesh) =>
        AddressExtensions.CreatePortalAddress($"{NodeOperationHubPrefix}{mesh.Address.Id}");

    /// <summary>
    /// True when <paramref name="hub"/> is the mesh's ONE central node-operation execution hub —
    /// i.e. the hub the <see cref="NodeOperationTarget"/> FALLBACK resolves to for every caller
    /// that declared no execution hub of its own.
    ///
    /// <para>That used to be the mesh hub itself, so the invariants that must run exactly once per
    /// create (above all <see cref="EnsurePartitionBootstrap"/>) tested <c>ReferenceEquals(hub,
    /// hub.GetMeshHub())</c>. Now the fallback is <c>portal/nodeops-{meshId}</c>, and the mesh hub
    /// still qualifies for the teardown path where that hub cannot be materialised — so the test is
    /// "is this the central execution hub", never "is this the router".</para>
    ///
    /// <para>Deliberately does NOT match the OTHER execution hubs (the static-repo <c>import</c>
    /// hub, an MCP session hub, a Blazor portal hub): those opted in explicitly, provision their
    /// own roots, and must not redo the central bootstrap — exactly as before.</para>
    /// </summary>
    private static bool IsCentralNodeOperationHub(IMessageHub hub)
    {
        var mesh = hub.GetMeshHub();
        return ReferenceEquals(hub, mesh) || hub.Address.Equals(NodeOperationHubAddress(mesh));
    }

    /// <summary>
    /// The mesh's ONE dedicated node-CRUD execution hub — <c>portal/nodeops-{meshId}</c>, hosted by
    /// the mesh hub, created on first use and shared thereafter.
    ///
    /// <para>🚨 This exists because the previous fallback was <c>hub.GetMeshHub().Address</c>, i.e.
    /// THE ROUTER. Every per-node hub (thread, NodeType <c>Source</c>, activity) has no
    /// <see cref="WithNodeOperationExecution"/> ancestor, so every create/delete/move it issued ran
    /// on the mesh hub's single-threaded action block AND its response came back stamped
    /// <c>Sender = mesh/{id}</c>. Both halves are exactly what <c>ROUTER_TRAFFIC</c> reports —
    /// production 2026-08 logged <c>"RawJson has the mesh hub as sender (sender: mesh/…, target:
    /// …/Source/FNodeTypeAtomicSolution)"</c> once per per-node hub, which is why it was the single
    /// largest source of ERROR lines: the "sender" role is reported by the RECEIVING hub, so the
    /// count scales with the number of node hubs, not with the number of message types.</para>
    ///
    /// <para>Safe to target because it carries NO per-node access pipeline: a
    /// <c>CreateNodePermissionAttribute</c> anchors its check at the RECEIVING hub's path, which is
    /// why <see cref="WithNodeOperationExecution"/> must never land on a per-node hub (the learner
    /// course-install denial). <c>portal/nodeops-{meshId}</c> is an infrastructure hub with no node
    /// of its own — the same shape as the MCP session hub and the Blazor portal hub, both of which
    /// already execute node CRUD off the router.</para>
    ///
    /// <para>Returns <c>null</c> only when the hub cannot be materialised (the mesh is already
    /// tearing down); callers then fall back to the mesh address — the historical behaviour, and at
    /// that point the operation is being dropped anyway.</para>
    /// </summary>
    /// <param name="hub">Any hub in the mesh; the execution hub is resolved from its mesh root.</param>
    /// <returns>The shared execution hub, or <c>null</c> while the mesh is disposing.</returns>
    public static IMessageHub? NodeOperationExecutionHub(this IMessageHub hub)
    {
        var mesh = hub.GetMeshHub();
        // Teardown: never materialise a hub during disposal (HostedHubsCollection refuses it and
        // logs a warning). The caller falls back to the historical target so the shutdown path
        // behaves as before.
        if (mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
            return null;

        var routingService = mesh.ServiceProvider.GetService<IRoutingService>();
        // 🚨 INHERIT THE ROUTER'S PERMISSION EVALUATOR. A hub's configuration starts EMPTY —
        // MessageHubExtensions.CreateMessageHub builds a fresh MessageHubConfiguration and nothing
        // is inherited from the parent — and HubPermissionExtensions.ResolveEvaluator does NOT walk
        // the parent chain: `hub.Configuration.Get<EffectivePermissionsDelegate>() ??
        // DefaultEvaluator`, whose default returns Permission.All (no gating). So moving node CRUD
        // onto a hub that did not copy the evaluator would make RlsNodeValidator grant EVERY write.
        // Copying the mesh hub's own delegate keeps the gate byte-for-byte what it was on the
        // router — and keeps a mesh deliberately built WITHOUT RLS ungated, exactly as before.
        // (Pinned by OwnerlessPartitionRepairTest: without this the stranger's create succeeds.)
        var permissionEvaluator = mesh.Configuration.Get<EffectivePermissionsDelegate>();
        return mesh.GetHostedHub(
            NodeOperationHubAddress(mesh),
            config =>
            {
                // Same wiring as SessionHubResolver's MCP session hub: its own IWorkspace (the
                // node-operation handlers reach mesh-node streams through it) plus the routing
                // registration that makes responses land here cross-silo.
                config = config
                    // 🚨 SHARE THE MESH HUB'S TYPE REGISTRY — this hub is the mesh's serialization
                    // identity for node content, not a separate one. A hub LEARNS a content type as
                    // a serialization side effect (PolymorphicTypeInfoResolver auto-registers an
                    // unregistered non-collectible type under its short name), and that warms only
                    // the SERIALISING hub. Node CRUD used to run on the mesh hub, so the mesh
                    // registry is where every dynamically-registered content type landed — and the
                    // `cache/{meshId}` hub, whose registry chains to it, is what reads node Content
                    // back for validators and views. Give this hub a private child registry instead
                    // and the mesh (hence the cache) never learns the type: `$type` fails to resolve
                    // and Content degrades to an untyped JsonElement, so `Content is T` goes false —
                    // update validators stop firing (NodeOperationsWithUpdateValidatorTest) and
                    // views render empty. Exactly the `Source/…` NodeType content shape from the
                    // production ROUTER_TRAFFIC line, so this is a live risk, not a test artefact.
                    // Sharing keeps the learning byte-for-byte where it was before the retarget.
                    .WithTypeRegistry(mesh.TypeRegistry)
                    .AddData()
                    .WithNodeOperationExecution()
                    .WithInitialization(h =>
                    {
                        if (routingService is not null)
                            h.RegisterForDisposal(routingService.RegisterStream(h));
                    });
                return permissionEvaluator is null
                    ? config
                    : config.WithPermissionEvaluator(permissionEvaluator);
            },
            HostedHubCreation.Always);
    }

    private static Address NodeOperationFallbackTarget(IMessageHub hub)
        => hub.NodeOperationExecutionHub()?.Address ?? hub.GetMeshHub().Address;

    /// <summary>
    /// The hub a node-operation request/response exchange should be ISSUED ON. The caller's own hub
    /// — except when that hub is the ROOT MESH HUB, the mesh's ROUTER: a request posted there makes
    /// the router an END of the delivery in both directions (the request goes out stamped
    /// <c>Sender = mesh/{id}</c> and its response is addressed straight back at <c>mesh/{id}</c>),
    /// which is exactly what the <c>ROUTER_TRAFFIC</c> detector reports — and, for a target-less
    /// request, EXECUTES the work on the router's single-threaded action block, starving the
    /// routing it exists to do.
    ///
    /// <para>This is the one shared seam for every mesh-singleton service that takes the DI-injected
    /// <see cref="IMessageHub"/> (which in the mesh's root container IS the router) and issues
    /// request/response work on it: the plugin-catalog boot services, the log-incident ingest, the
    /// content importers, one-shot <c>GetMeshNode</c> reads. They all hop onto
    /// <see cref="NodeOperationExecutionHub"/> — the mesh's dedicated off-router execution hub,
    /// routing-registered so responses land on it cross-silo, sharing the mesh's type registry and
    /// permission evaluator. For any hub that is NOT the router this returns the hub unchanged, so
    /// portal/session/import-hub callers keep their identity byte-for-byte.</para>
    ///
    /// <para>Identity is unaffected: the ambient <c>AccessService</c> is the mesh-wide singleton
    /// every hosted hub's provider chains to, so an <c>ImpersonateAsSystem</c> (or any ambient
    /// context) active at Subscribe time is read identically by this hub's post pipeline.</para>
    ///
    /// <para>Falls back to the hub itself only while the mesh is tearing down
    /// (<see cref="NodeOperationExecutionHub"/> returns <c>null</c>) — the historical behaviour, at
    /// a point where the operation is being dropped anyway.</para>
    /// </summary>
    /// <param name="hub">The hub the caller holds — returned unchanged unless it is the root mesh hub.</param>
    /// <returns>The off-router issuing hub.</returns>
    public static IMessageHub NodeOperationIssuingHub(this IMessageHub hub) =>
        string.Equals(hub.Address.Type, AddressExtensions.MeshType, StringComparison.Ordinal)
            ? hub.NodeOperationExecutionHub() ?? hub
            : hub;

    /// <summary>
    /// Registers handlers for mesh node operations. Idempotent — calling twice on the
    /// same configuration is a no-op on the second call. Without this guard, every
    /// extra call would add a duplicate set of handlers; each delivery would invoke
    /// HandleCreateNodeRequest/Update/etc. twice, producing two responses per request
    /// (the second one observing the side-effects of the first → spurious
    /// "Node already exists"/"Node not found" failures). Concrete trigger: any hub
    /// that gets both <see cref="MeshBuilder"/>'s <c>AddMesh</c> and
    /// <c>AddDefaultLayoutAreas</c> (which calls <c>AddMeshDataSource</c>, which
    /// calls this).
    /// </summary>
    public static MessageHubConfiguration WithNodeOperationHandlers(this MessageHubConfiguration config)
    {
        if (config.Get<NodeOperationHandlersMarker>() is not null)
            return config;
        return config
            .Set(new NodeOperationHandlersMarker())
            .AddMeshTypes()
            .WithHandler<CreateNodeRequest>(HandleCreateNodeRequest)
            .WithHandler<CreateNodesRequest>(HandleCreateNodesRequest)
            .WithHandler<CreateOrUpdateNodeRequest>(HandleCreateOrUpdateNodeRequest)
            .WithHandler<DeleteNodeRequest>(HandleDeleteNodeRequest)
            .WithHandler<ValidateDeleteRequest>(HandleValidateDeleteRequest)
            .WithHandler<MoveNodeRequest>(HandleMoveNodeRequest)
            .WithHandler<CopyNodeRequest>(HandleCopyNodeRequest)
            .WithHandler<HeartBeatEvent>(HandleHeartBeat);
    }

    /// <summary>
    /// Registers only the <see cref="HeartBeatEvent"/> handler. Use on hubs that
    /// should swallow heartbeats silently (e.g. per-node hubs spawned from a
    /// NodeType's configuration) without pulling in the full node-operation
    /// handler set. Without this handler the message service logs a warning per
    /// heartbeat, so targets that receive heartbeats but don't need to keep an
    /// Orleans grain alive should still register it as a no-op.
    /// </summary>
    public static MessageHubConfiguration WithHeartBeatHandler(this MessageHubConfiguration config)
        => config.WithHandler<HeartBeatEvent>(HandleHeartBeat);

    /// <summary>
    /// Handles HeartBeatEvent: signals the Orleans grain to delay deactivation.
    /// Walks up the parent hub chain because GrainKeepAliveCallback is set on the
    /// grain's top-level hub, not on child hubs (threads, messages, _Exec).
    /// In monolith mode, no GrainKeepAliveCallback is registered → no-op.
    /// </summary>
    private static IMessageDelivery HandleHeartBeat(
        IMessageHub hub, IMessageDelivery<HeartBeatEvent> delivery)
    {
        var current = hub;
        while (current != null)
        {
            var callback = current.Configuration.Get<GrainKeepAliveCallback>();
            if (callback != null)
            {
                // Debug, NOT Information: this fires once per HeartBeatEvent (per sync stream,
                // every 45s). At the accumulation scale a wedge produces (hundreds-to-thousands
                // of live streams) an Information line here was ~11% of the pod's CPU (console
                // logger) and pure Loki ingest noise — the heartbeat itself is the signal, the
                // log line is diagnostics.
                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.GrainKeepAlive");
                // Debug, NOT Information: this fires for EVERY sync-stream keep-alive heartbeat on EVERY
                // open stream, every heartbeat interval — the single highest-volume log line on a busy
                // portal (≈half of all log lines + ~11% of CPU under load, measured via dotnet-trace on
                // the wedged e2e portal: ConsoleLoggerProcessor.ProcessLogQueue). At Information it ships
                // to Loki on every tick and bleeds ingest budget for zero diagnostic value (a grain
                // staying alive is the expected steady state). Keep it at Debug for when a deactivation
                // is actually being investigated.
                logger?.LogDebug("HeartBeat: keeping grain alive for {Hub} (callback on {Parent})",
                    hub.Address, current.Address);
                callback.KeepAlive();
                break;
            }
            var parent = current.Configuration.ParentHub;
            if (parent == current) break;
            current = parent;
        }
        return delivery.Processed();
    }

    /// <summary>
    /// Fully synchronous handler — returns <see cref="IMessageDelivery"/>, never <see cref="Task"/>.
    /// Its storage / change-feed leaves are ALREADY <see cref="IObservable{T}"/> (or reach the I/O
    /// boundary through <c>IIoPool</c>) and are composed via <c>SelectMany</c>/<c>Subscribe</c>; the
    /// terminal response is posted from inside the deepest callback. The handler itself returns
    /// <c>request.Processed()</c> immediately so the hub scheduler is never blocked.
    ///
    /// <para>🚨 This doc-comment used to claim the async work was wrapped in
    /// <c>Observable.FromAsync</c>. It is not, and it must not be: <c>Observable.FromAsync</c> is
    /// FORBIDDEN outside <c>IoPool</c> (it runs the synchronous prologue on the SUBSCRIBING thread —
    /// the hub scheduler — with no concurrency bound). The claim was stale and advertised a banned
    /// pattern to the next reader; corrected here rather than left for someone to copy.</para>
    ///
    /// <para>🚨 <b>Every terminal path answers — that is an invariant, not a best effort (#981).</b>
    /// The response is posted from a composed observable, so a chain that terminates WITHOUT posting
    /// would leave the requester's <c>hub.Observe</c> callback pending forever. Two things close that:
    /// <list type="number">
    ///   <item>a declined write (the <c>null</c> try-then-claim sentinel) FAULTS via
    ///     <see cref="RequireClaimedWrite"/> instead of being filtered away, so the single-adapter
    ///     path answers exactly like the composite <c>PersistenceService</c> already did;</item>
    ///   <item>the terminal <c>onCompleted</c> arm posts a failure when — and only when — nothing
    ///     emitted AND nothing answered, so an empty completion can never be silent.</item>
    /// </list>
    /// Every terminal post goes through the local <c>Respond</c>, which is what makes (2) exact.
    /// The <c>RequestFateLedger</c> trail still distinguishes a terminated chain from a merely slow
    /// one — see <c>Doc/Architecture/DebuggingMessageFlow</c>.</para>
    /// See <c>Doc/Architecture/AsynchronousCalls</c>.
    /// </summary>
    private static IMessageDelivery HandleCreateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CreateNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MeshWeaver.Mesh.CreateNode");
        var meshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        // Resolved once and threaded through both save paths (confirm + create) so the
        // Created/Updated publish is composed into the storage observable via the
        // StorageAdapterChangeFeedExtensions helpers — no chance of publishing the
        // event before the storage write has committed.
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();

        // 🚨 #981 — the ONE place a terminal CreateNodeResponse leaves this handler.
        //
        // The reply is owed by a DETACHED reactive chain (this handler returns Processed()
        // immediately), and that chain has several branches that post a failure and then return
        // Observable.Empty. So "the chain completed without emitting" is NOT the same question as
        // "the requester was left unanswered" — and only the second one is a defect. Recording the
        // answer here is what lets the terminal backstop below be EXACT: it fires only when nothing
        // at all answered, so a branch that already posted a Fail is never answered twice and a
        // success is never converted into a spurious failure.
        //
        // Every terminal post in this handler goes through here. A future branch that posts and
        // returns Empty therefore suppresses the backstop automatically — the invariant does not
        // depend on anyone remembering which branches can precede an empty completion.
        var responded = false;
        void Respond(CreateNodeResponse response)
        {
            responded = true;
            hub.Post(response, o => o.ResponseFor(request));
        }

        if (meshConfig == null)
        {
            Respond(CreateNodeResponse.Fail("MeshConfiguration not available", NodeCreationRejectionReason.Unknown));
            return request.Processed();
        }

        // FAIL CLOSED on missing storage: a create that cannot persist must error,
        // never ack. The old fallback (save = Observable.Return(node)) reported
        // Success while writing NOTHING — on the 2026-06-11 prod portal every MCP
        // create was acked "Created: …" and silently lost. Storage-less meshes are
        // not a supported mode (tests use AddInMemoryPersistence); a null adapter
        // here is always a wiring defect on the responding hub — name it loudly.
        if (persistence == null)
        {
            logger.LogError(
                "[CreateNode] REFUSED {Path}: no IStorageAdapter on hub {Hub} — the create would be acked but never persisted. " +
                "Register persistence (AddPartitioned*Persistence / AddInMemoryPersistence) on this hub's service provider.",
                request.Message.Node.Path, hub.Address);
            Respond(CreateNodeResponse.Fail(
                $"No storage adapter on hub '{hub.Address}' — refusing to create '{request.Message.Node.Path}' because it could not be persisted.",
                NodeCreationRejectionReason.Unknown));
            return request.Processed();
        }

        var createRequest = request.Message;

        // Surface the AccessContext that travelled with the message delivery.
        // Local diagnostic: flip MeshWeaver.Mesh.IMeshCatalog to Debug in
        // appsettings to read which identity each CreateNodeRequest carries.
        // Stays off CI by default — test/appsettings.json keeps Warning.
        logger.LogDebug(
            "[CreateNode] received path={Path} accessCtx.ObjectId={Caller} accessCtx.Name={Name} accessCtx.IsVirtual={Virtual}",
            createRequest.Node.Path,
            request.AccessContext?.ObjectId ?? "(null)",
            request.AccessContext?.Name ?? "(null)",
            request.AccessContext?.IsVirtual);

        // Identity resolution: if no explicit CreatedBy, use the sender's AccessContext identity.
        if (string.IsNullOrEmpty(createRequest.CreatedBy)
            && request.AccessContext?.ObjectId is { Length: > 0 } senderId)
            createRequest = createRequest with { CreatedBy = senderId };

        var capturedRequest = createRequest;
        var node = createRequest.Node;

        // 0. Path validation (sync — fail-fast).
        if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Path))
        {
            Respond(CreateNodeResponse.Fail("Node path and Id must not be empty",
                NodeCreationRejectionReason.ValidationFailed));
            return request.Processed();
        }

        // 0b. Reject nodes that are neither typed nor have content. A bare MeshNode with
        // no NodeType and no Content can't spawn a useful per-node hub (no content type
        // means no AddMeshDataSource / GetDataRequest handler), so it's always a caller bug.
        if (string.IsNullOrWhiteSpace(node.NodeType) && node.Content == null)
        {
            Respond(CreateNodeResponse.Fail(
                "Node must have a NodeType or Content set; bare nodes are not allowed.",
                NodeCreationRejectionReason.ValidationFailed));
            return request.Processed();
        }

        // 0c. Structural fail-fast: an Activity MeshNode must NEVER be anchored at a top-level /
        // ownerless path. A bare `_Activity/{id}` (empty owner, MainNode="") — or any `_Activity`
        // folder with no owning node before it — has no per-node hub to route to, so every poster
        // (SubmitCodeRequest / DataChangeRequest) and every subscriber (the GUI progress panels)
        // NotFound-storms the router (the prod `_Activity/import-*` / `_Activity/compile-*` storm).
        // Reject at the create boundary — loudly, at the source — instead of letting the phantom
        // escape downstream. Runs BEFORE EnsurePartitionBootstrap + the validators (and BEFORE the
        // System bypass inside those validators) because this is a STRUCTURAL invariant that holds
        // for every identity, including System-driven compile/import/startup activities. Covers all
        // creators: CreateNode AND CreateOrUpdateNode (whose inner create funnels through here).
        if (ActivityNodeGuard.IsOwnerless(node, out var ownerlessReason))
        {
            logger.LogError("[CreateNode] REFUSED ownerless Activity {Path}: {Reason}", node.Path, ownerlessReason);
            Respond(CreateNodeResponse.Fail(ownerlessReason, NodeCreationRejectionReason.InvalidPath));
            return request.Processed();
        }

        // 0d. Structural fail-fast: an AccessAssignment must be scoped to the node it is filed
        // under. A grant is scoped by MainNode, NOT by its folder — so `Admin/_Access/{user}_Access`
        // with an EMPTY MainNode is a ROOT grant (All on every partition, every space, every user's
        // private home) that merely looks like a platform-admin grant. memex 2026-07-28: 43 accounts
        // held exactly that shape against ONE correctly-scoped admin, and they were still being
        // created that day. Every KNOWN writer sets MainNode correctly, so an unknown path produces
        // them — which is precisely why this belongs at the boundary rather than in each writer.
        // Runs with the other STRUCTURAL invariants: before the validators and before their System
        // bypass, because a root grant is catastrophic regardless of who writes it.
        //
        // 🚨 The guard MUST see the NORMALISED node, hence the call above. MeshNode.MainNode defaults
        // to Path, so a satellite created the ordinary way (`MeshNode.FromPath("{p}/_Access/a1")`,
        // no explicit MainNode) arrives with MainNode == its own path while the path encodes scope
        // "{p}" — a mismatch the guard refuses. Normalising first is what makes the framework's own
        // documented satellite shape legal; guarding the RAW node rejected every caller that relies
        // on the auto-derivation this method has always performed.
        node = NormalizeSatelliteMainNode(node, meshConfig);

        if (AccessAssignmentGuard.IsScopeInvalid(node, out var scopeReason))
        {
            logger.LogError("[CreateNode] REFUSED mis-scoped AccessAssignment {Path}: {Reason}", node.Path, scopeReason);
            Respond(CreateNodeResponse.Fail(scopeReason, NodeCreationRejectionReason.InvalidPath));
            return request.Processed();
        }

        // 1. Read existing — persistence first (catalog.GetNode auto-creates from templates),
        //    then fall back to the in-memory config. persistence.GetNode is already
        //    IObservable so we don't need to wrap it in Observable.FromAsync.
        var existingObs = persistence != null
            ? persistence.Read(node.Path, hub.JsonSerializerOptions)
            : Observable.Return<MeshNode?>(null);

        // Handler-side trail (#981). This handler returns Processed() immediately and owes its
        // reply from the DETACHED chain below, so the pipeline's own HANDLER_EXIT stage proves
        // nothing about whether the reply is coming. These stages are what let a pending-callback
        // report say "the chain completed empty" (nothing will ever answer) rather than "no reply
        // yet" — the ambiguity that left two #981 captures unexplained. `emitted` is written and
        // read on the chain's own terminal arms only.
        var emitted = false;
        hub.NoteRequestStage(request.Id, $"CREATE_CHAIN_SUBSCRIBED path={node.Path}");

        existingObs
            .Select(existing =>
            {
                if (existing == null)
                {
                    var configNode = hub.ServiceProvider.FindStaticNode(node.Path);
                    // A definition-only catalog type-def is NOT a real node at this path (Postgres
                    // owns the nodeType:NodeType partition root) — it must never stand in as an
                    // "existing" node and block creating the real PG root. See NodeTypeCatalogs.md.
                    if (configNode is { IsDefinitionOnly: true })
                        configNode = null;
                    if (configNode is not null)
                        return configNode;
                }
                return existing;
            })
            .SelectMany(existingNode =>
            {
                if (existingNode != null)
                {
                    // Transient → Active confirmation path.
                    if (existingNode.State == MeshNodeState.Transient && node.State == MeshNodeState.Active)
                    {
                        var confirmedNode = existingNode with
                        {
                            State = MeshNodeState.Active,
                            Name = node.Name ?? existingNode.Name,
                            Icon = node.Icon ?? existingNode.Icon,
                            Category = node.Category ?? existingNode.Category,
                            Content = node.Content ?? existingNode.Content
                        };
                        // Commit-then-publish: Updated event fires inside the helper's
                        // .Do operator, which runs only after the storage write emits
                        // (post-commit). The no-persistence fallback below skips the
                        // publish entirely — historically that path published an Updated
                        // event with no backing write, so cross-replica subscribers saw
                        // a phantom row update.
                        var saveObs = persistence != null
                            ? persistence.WriteAndPublishUpdated(confirmedNode, hub.JsonSerializerOptions, changeFeed)
                                .RequireClaimedWrite(hub, request.Id, persistence, confirmedNode.Path)
                            : Observable.Return(confirmedNode);
                        return saveObs.Select(savedConfirmed => (mode: "confirm", node: savedConfirmed));
                    }
                    // Node exists & not a confirmation → fail.
                    Respond(CreateNodeResponse.Fail(
                        $"Node already exists at path: {node.Path}",
                        NodeCreationRejectionReason.NodeAlreadyExists));
                    return Observable.Empty<(string mode, MeshNode node)>();
                }

                // 1b. Auto-set MainNode for satellite types before validation: the OWNING MAIN NODE —
                // the namespace with its satellite tail cut off (`{owner}/_Access` → `{owner}`), which
                // for the legacy no-segment placement (`{owner}` itself) is the namespace unchanged.
                // NOT the raw namespace: that is the satellite CONTAINER, a path no node lives at, and
                // every consumer of MainNode reads it as a real node — SatelliteAccessRule DELEGATES the
                // satellite's permissions to it, `rebuild_user_effective_permissions` projects an
                // AccessAssignment's grant at prefix COALESCE(main_node, namespace) (so a container
                // stamp granted access one level too deep — under `{scope}/_Access` instead of on
                // `{scope}`), satellite-table scope filters match `main_node` as the attachment point,
                // and the access-granted notification named "{scope}/_Access" instead of the node that
                // was shared. A root-level satellite (`_Access/{id}`, the root-scope grant) has no
                // owner: MainNode = "" is that shape's documented value (see ActivityNodeGuard).
                // 🚨 Already APPLIED ABOVE, before the AccessAssignment scope guard — that guard
                // compares MainNode against the scope the path encodes, so it must run on the
                // normalised node. Retained here (idempotent: the condition is false once MainNode
                // has been rewritten) because it is the `if` of the chain whose `else if` is 1b'.
                if (!string.IsNullOrEmpty(node.NodeType)
                    && !string.IsNullOrEmpty(node.Namespace)
                    && meshConfig.IsSatelliteNodeType(node.NodeType)
                    && node.MainNode == node.Path)
                {
                    node = NormalizeSatelliteMainNode(node, meshConfig);
                }
                // 1b'. Repair a STALE BARE-ID self-default MainNode on a MAIN (non-satellite) node.
                // MainNode is a STORED property: unlike the computed Path/Segments it does NOT follow
                // a `with { Namespace = … }` rebase. A node first built BARE
                // (`new MeshNode("Datenextraktion")` → MainNode defaults to the bare Id
                // "Datenextraktion") and only LATER given a namespace keeps that stale bare MainNode
                // while its Path becomes the full path. Persisted, the bare value flows
                // Node.MainNode → NavigationContext.PrimaryPath → NavigationService.CurrentNamespace →
                // the chat composer's StartThread namespace → a thread created under the NON-EXISTENT
                // "Datenextraktion" partition (the agent's short id) → Postgres 42P01
                // (`relation "datenextraktion.mesh_nodes" does not exist`). Re-stamp it to the node's
                // real Path so a main node is never persisted pointing at a phantom partition.
                // Trigger is deliberately the EXACT bug shape — MainNode == the bare Id on a namespaced
                // node — NOT a blanket `MainNode != Path`: a non-satellite node may legitimately point
                // MainNode at a PARENT path (e.g. GitHubSyncConfig's `MainNode = spacePath`), which is
                // never equal to its own Id and so is left untouched. Satellites are handled in 1b.
                else if (!string.IsNullOrEmpty(node.NodeType)
                    && !string.IsNullOrEmpty(node.Namespace)
                    && !meshConfig.IsSatelliteNodeType(node.NodeType)
                    && node.MainNode == node.Id)
                {
                    node = node with { MainNode = node.Path };
                }

                // 1c. SELF-HEALING PARTITION BOOTSTRAP. Ensure the partition's Space root +
                //     creator grant exist BEFORE the requested child is validated/persisted.
                //     A missing root makes the bare partition address un-routable (GetDataRequest
                //     routing loop → faulted data source), and RLS would otherwise deny the first
                //     child-write into a fresh partition. Sequenced ahead of the validators via
                //     SelectMany so root + grant are in place by the time RLS / the write-guard run.
                //     See EnsurePartitionBootstrap.
                // 2. Validators → 3. NodeType existence → 4-7. Enrich + save + change feed + version
                return EnsurePartitionBootstrap(hub, node, capturedRequest, logger, request.Id)
                    // 1d. A SYSTEM-OWNED space grants nobody write access. Sequenced AFTER the
                    //     bootstrap (which may have just created the partition) and ahead of the
                    //     validators, and folded into the same rejection tuple so the failure is
                    //     posted by the one code path that already knows how.
                    .SelectMany(_ => SystemOwnedGrantRejection(hub, node))
                    .SelectMany(grantRejection => grantRejection is not null
                        ? Observable.Return<(string? ErrorMessage, NodeCreationRejectionReason Reason)?>(
                            (grantRejection, NodeCreationRejectionReason.ValidationFailed))
                        : RunCreationValidatorsObs(hub, node, capturedRequest))
                    .SelectMany(validationError =>
                    {
                        if (validationError != null)
                        {
                            logger.LogWarning(
                                "Validator rejected node creation at {Path}: {Error}",
                                node.Path, validationError.Value.ErrorMessage);
                            Respond(CreateNodeResponse.Fail(
                                validationError.Value.ErrorMessage ?? "Validation failed",
                                validationError.Value.Reason));
                            return Observable.Empty<(string mode, MeshNode node)>();
                        }

                        // 3. NodeType existence check. Recognise types from
                        // (a) MeshConfiguration.Nodes (config-time AddMeshNodes),
                        // (b) IStaticNodeProvider (the canonical seed surface — see
                        //     Doc/Architecture/TestStateIsolation), and
                        // (c) persistence (dynamically-created NodeType definitions).
                        IObservable<bool> typeExistsObs;
                        if (string.IsNullOrEmpty(node.NodeType))
                        {
                            typeExistsObs = Observable.Return(true);
                        }
                        else if (hub.ServiceProvider.FindStaticNode(node.NodeType) is not null)
                        {
                            typeExistsObs = Observable.Return(true);
                        }
                        else if (persistence != null)
                        {
                            typeExistsObs = persistence.Exists(node.NodeType);
                        }
                        else
                        {
                            typeExistsObs = Observable.Return(false);
                        }

                        return typeExistsObs.SelectMany(typeExists =>
                        {
                            if (!typeExists)
                            {
                                Respond(CreateNodeResponse.Fail(
                                    $"NodeType '{node.NodeType}' is not registered",
                                    NodeCreationRejectionReason.InvalidNodeType));
                                return Observable.Empty<(string mode, MeshNode node)>();
                            }

                            // 4. Active state + creation stamps (Created/LastModified + identity).
                            //    Always stamp CreatedDate so the UI never has to guess a creation
                            //    time; if the caller pre-set it (import flow) we preserve it.
                            var now = DateTimeOffset.UtcNow;
                            var identity = capturedRequest.CreatedBy;
                            var newNode = node with
                            {
                                State = MeshNodeState.Active,
                                CreatedDate = node.CreatedDate == default ? now : node.CreatedDate,
                                CreatedBy = string.IsNullOrEmpty(node.CreatedBy) ? identity : node.CreatedBy,
                                LastModified = node.LastModified == default ? now : node.LastModified,
                                LastModifiedBy = string.IsNullOrEmpty(node.LastModifiedBy) ? identity : node.LastModifiedBy,
                                // Stamp an initial Version of 1 so the post-save JSON includes the
                                // field (the hub's JsonSerializerOptions has
                                // DefaultIgnoreCondition=WhenWritingDefault → Version=0 is omitted
                                // on serialisation, which breaks callers that read it back for
                                // optimistic-concurrency Update).
                                Version = node.Version > 0 ? node.Version : 1,
                            };

                            // 5. Persist the RAW node — enrichment lives ONLY at the
                            //    hub-instantiation site (the factory), never on the create
                            //    path. HubConfiguration is a non-serialisable delegate that
                            //    persistence drops anyway, and pre-persist enrichment would
                            //    re-enter routing through workspace.GetMeshNodeStream →
                            //    SubscribeRequest → catalog and create a runtime activation
                            //    cycle. CreateNode emits the node as-stored; consumers that
                            //    need an enriched node ask the factory at activation time.
                            return Observable.Defer(() =>
                            {
                                var enriched = newNode;
                                logger.LogDebug("[CreateNode] step=save-start path={Path} persistence={HasPersistence} adapter={Adapter}",
                                    enriched.Path, persistence != null, persistence?.GetType().Name);
                                // Commit-then-publish: Created event fires inside the helper's
                                // .Do operator, which runs only after the storage write emits
                                // (post-commit). No-persistence fallback skips the publish —
                                // see the confirm branch above for the rationale.
                                var saveObs = persistence != null
                                    ? persistence.WriteAndPublishCreated(enriched, hub.JsonSerializerOptions, changeFeed)
                                        .RequireClaimedWrite(hub, request.Id, persistence, enriched.Path)
                                        .Do(s => logger.LogDebug("[CreateNode] step=save-emit path={Path} version={Version}",
                                            s.Path, s.Version))
                                    : Observable.Return(enriched);
                                return saveObs.Select(saved => (mode: "create", node: saved));
                            });
                        });
                    });
            })
            .Subscribe(
                tuple =>
                {
                    var resultNode = tuple.node;
                    var mode = tuple.mode;
                    emitted = true;
                    hub.NoteRequestStage(request.Id, $"CREATE_CHAIN_EMITTED mode={mode}");

                    // MeshChangeEvent.Created/Updated already published inside the
                    // save observable via WriteAndPublishCreated/WriteAndPublishUpdated
                    // — guarantees the event fires only after the storage write committed.
                    // This Subscribe handles the remaining side-effects (live-query
                    // notification, response Post, version-history write, logging) which
                    // happen after the change-feed publish in the chain.

                    // Live Query delta is surfaced by the storage adapter's
                    // Changes feed (IStorageAdapter.Changes) from inside its Write —
                    // no separate notify path from this handler.

                    // Version history is now written inside PersistenceService.SaveNode
                    // (chained off the post-save MeshNode emission) — no explicit
                    // WriteVersion needed here, and no race between competing save paths.

                    if (mode == "confirm")
                    {
                        // Workspace fan-out for transient confirmation (fire-and-forget — same
                        // semantics as the previous code).
                        hub.Post(DataChangeRequest.Update([resultNode]),
                            o => o.WithTarget(new Address(resultNode.Path)));
                    }

                    logger.LogInformation(
                        mode == "confirm" ? "Confirmed transient node at {Path}" : "Node created at {Path} by {CreatedBy}",
                        resultNode.Path, capturedRequest.CreatedBy ?? "system");

                    // Forward the optional Argument to the new node's hub (fire-and-forget).
                    // This lets a single CreateNodeRequest atomically create the node AND
                    // queue its first piece of work — e.g. a Thread's first
                    // ThreadInput.AppendUserInput — without a second client round-trip. We
                    // preserve the original requester's AccessContext so the target hub's
                    // permission attribute checks against the user, not the mesh hub.
                    if (mode == "create" && capturedRequest.Argument is { } arg)
                    {
                        var nodeAddress = new Address(resultNode.Path);
                        logger.LogDebug(
                            "[ArgFwd] Forwarding {ArgType} to {NodePath} (accessCtx={AccessCtx})",
                            arg.GetType().Name, resultNode.Path,
                            request.AccessContext?.ObjectId ?? "(null)");
                        var argDelivery = hub.Post(arg, o =>
                        {
                            o = o.WithTarget(nodeAddress);
                            return request.AccessContext is { } accessCtx
                                ? o.WithAccessContext(accessCtx)
                                : o;
                        });
                        logger.LogDebug(
                            "[ArgFwd] Post returned delivery={DeliveryNull} for {ArgType} → {NodePath}",
                            argDelivery == null ? "null" : argDelivery.Id, arg.GetType().Name, resultNode.Path);
                    }

                    // Run post-creation handlers and post the terminal response. On every
                    // terminal path (success/error) a response MUST go out so the caller never
                    // waits forever.
                    //
                    // 🚨 ALL-OR-NOTHING (#638). A create is provision → write the row → run the
                    // post-creation handlers, and a handler that declares FailsCreateOnError is
                    // part of the create's CONTRACT, not a side effect (the Space creator-Admin
                    // grant: a Space whose grant never landed is an ownerless, un-writable,
                    // un-deletable partition — the ghost roots this issue is named after).
                    // Reporting Fail while LEAVING the row was the whole defect: the caller
                    // cannot retry (create answers "already exists") and cannot clean up (nobody
                    // has rights on the partition). So a failed critical handler now COMPENSATES:
                    // the row this create wrote is deleted again, and the response carries the
                    // ORIGINAL cause plus the rollback outcome.
                    logger.LogDebug("[CreateNode] step=post-handlers-start path={Path}", resultNode.Path);
                    hub.NoteRequestStage(request.Id, "CREATE_POST_HANDLERS_START");
                    RunPostCreationHandlersObs(hub, resultNode, capturedRequest.CreatedBy, logger)
                        .Subscribe(
                            _ => { },
                            ex =>
                            {
                                hub.NoteRequestStage(request.Id,
                                    $"CREATE_POST_HANDLERS_ERROR {ex.GetType().Name}");
                                logger.LogError(ex,
                                    "Post-creation handler chain errored at {Path} — rolling the create back (#638)",
                                    resultNode.Path);
                                CompensateFailedCreate(hub, resultNode, mode, logger)
                                    .Subscribe(
                                        outcome => Respond(CreateNodeResponse.Fail(
                                            $"Create failed in a post-creation step: {ex.Message} {outcome}",
                                            NodeCreationRejectionReason.Unknown)),
                                        // The compensation itself already converts its own faults into
                                        // an outcome string; this branch exists so a response goes out
                                        // even if it faults on a path we did not foresee — never a
                                        // silent swallow, never a caller left waiting.
                                        compensationEx => Respond(CreateNodeResponse.Fail(
                                            $"Create failed in a post-creation step: {ex.Message} "
                                            + $"Rolling back '{resultNode.Path}' FAILED ({compensationEx.Message}) — "
                                            + "the partially-created node is still present and must be removed manually.",
                                            NodeCreationRejectionReason.Unknown)));
                            },
                            () =>
                            {
                                logger.LogDebug("[CreateNode] step=post-handlers-done path={Path} — posting Ok", resultNode.Path);
                                hub.NoteRequestStage(request.Id, "CREATE_POST_HANDLERS_DONE");
                                Respond(CreateNodeResponse.Ok(resultNode));
                            });
                },
                ex =>
                {
                    hub.NoteRequestStage(request.Id, $"CREATE_CHAIN_ERROR {ex.GetType().Name}");
                    if (ex is InvalidOperationException)
                    {
                        logger.LogWarning(ex, "Node creation failed for path {Path}", node.Path);
                        Respond(CreateNodeResponse.Fail(ex.Message, NodeCreationRejectionReason.ValidationFailed));
                    }
                    else
                    {
                        logger.LogError(ex, "Unexpected error during node creation at {Path}", node.Path);
                        Respond(CreateNodeResponse.Fail($"Unexpected error: {ex.Message}",
                            NodeCreationRejectionReason.Unknown));
                    }
                },
                () =>
                {
                    // 🚨 THE NO-SILENT-HANG BACKSTOP (#981).
                    //
                    // This chain owes the reply, and it can terminate WITHOUT emitting: several
                    // branches return `Observable.Empty<(string, MeshNode)>()`, and every upstream
                    // leaf (the existence Read, EnsurePartitionBootstrap, the validators, the
                    // NodeType Exists probe, the save) is an adapter-supplied observable that is
                    // free to complete empty. Most of those branches post a Fail first — those are
                    // ANSWERED and must be left alone. What must never happen is the remainder:
                    // termination with no answer, which leaves the requester's `hub.Observe`
                    // callback pending FOREVER. That is the #981 signature exactly
                    // (`CreateNodeRequest@mesh/<self>`, unanswered, every queue empty, nothing
                    // wedged on an action block) — a hang is never a legitimate outcome, so the
                    // backstop is the invariant, not a guess.
                    //
                    // 🚨 It is gated on BOTH flags, and each guard is load-bearing:
                    //   • `emitted`   — the chain produced a node, so the reply is owed by the
                    //                   post-creation Subscribe above, whose own arms both answer.
                    //                   Answering here would race a success into a failure.
                    //   • `responded` — a branch already answered (already-exists, validation,
                    //                   unknown NodeType). Answering again would post a SECOND
                    //                   response for one correlation.
                    // So the only case left is genuinely unanswerable-otherwise, and the only
                    // correct answer for it is a failure: nothing emitted ⇒ no node was created
                    // ⇒ no Ok can ever be right.
                    if (emitted || responded)
                        return;
                    hub.NoteRequestStage(request.Id,
                        $"CREATE_CHAIN_COMPLETED_EMPTY path={node.Path} (unanswered — replying Fail)");
                    logger.LogError(
                        "[CreateNode] chain for {Path} COMPLETED WITHOUT emitting a node and without posting a "
                        + "response — answering Fail so the caller is not left waiting. Find the upstream that "
                        + "completed empty (a Where that dropped the only element, an Observable.Empty branch, or "
                        + "a storage leaf that completed without emitting).",
                        node.Path);
                    Respond(CreateNodeResponse.Fail(
                        $"Could not create '{node.Path}': the create pipeline terminated without producing a node "
                        + "and without reporting a reason. This is a defect in the create chain, not a rejection of "
                        + "the request — retrying is unlikely to help until it is fixed.",
                        NodeCreationRejectionReason.Unknown));
                });

        return request.Processed();
    }

    /// <summary>
    /// Turns <see cref="IStorageAdapter.Write"/>'s DECLINE sentinel into the SAME speaking fault the
    /// composite <c>PersistenceService.Write</c> already raises for the identical condition — and
    /// records <c>CREATE_SAVE_DECLINED</c> on the request's fate trail so a capture names the adapter
    /// that declined instead of just saying the chain went empty (#981, #1011).
    ///
    /// <para><b>The asymmetry this removes.</b> <c>null</c> from <c>Write</c> is the documented
    /// try-then-claim sentinel — "this adapter does not own this path", NOT "the write succeeded".
    /// <c>PersistenceService</c> folds it across its writable providers and, when every one declines,
    /// THROWS <i>"Could not save '{path}': no writable storage provider accepted the node."</i>, which
    /// the create handler's <c>onError</c> arm answers correctly. But the resolved
    /// <see cref="IStorageAdapter"/> is not always that composite: the non-partitioned wirings resolve
    /// a single adapter (decorated), and several of those decline by contract —
    /// <c>PathFilteringStorageAdapter</c> for a non-matching path, <c>PostgreSqlPathRoutingAdapter</c>
    /// / <c>SnowflakePathRoutingAdapter</c> for an unroutable partition, <c>RoutingProxyAdapter</c>
    /// when no partition hub claims the path, <c>StaticNodeStorageAdapter</c> always. The old
    /// <c>.Where(n => n is not null)</c> dropped that null, the chain completed empty, and no response
    /// was ever posted — so ONE condition either failed cleanly or HUNG FOREVER depending purely on
    /// which adapter the hub happened to resolve. Whether a caller gets an answer must not depend on
    /// the storage wiring underneath it.</para>
    ///
    /// <para>The fault is an <see cref="InvalidOperationException"/> for the same reason: that is what
    /// <c>PersistenceService</c> throws, so both paths now produce a byte-identically-shaped
    /// <c>CreateNodeResponse.Fail(…, ValidationFailed)</c> from the one <c>onError</c> arm.</para>
    /// </summary>
    /// <param name="save">The adapter write (already composed with its change-feed publish).</param>
    /// <param name="hub">The hub handling the request — used to record the ledger stage.</param>
    /// <param name="requestId">The awaited request's delivery id.</param>
    /// <param name="adapter">The adapter that produced the sentinel, named in the fault + stage.</param>
    /// <param name="path">The path that was not claimed.</param>
    /// <returns>The saved node, or a fault naming the declining adapter. Never a silent completion.</returns>
    private static IObservable<MeshNode> RequireClaimedWrite(
        this IObservable<MeshNode?> save,
        IMessageHub hub,
        string? requestId,
        IStorageAdapter adapter,
        string path)
        => save.SelectMany(saved =>
        {
            if (saved is not null)
                return Observable.Return(saved);
            hub.NoteRequestStage(requestId,
                $"CREATE_SAVE_DECLINED adapter={adapter.GetType().Name} path={path}");
            return Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Could not save '{path}': the storage adapter '{adapter.GetType().Name}' declined the write "
                + "(the try-then-claim null sentinel — no writable storage provider accepted the node)."));
        });

    /// <summary>
    /// COMPENSATING ROLLBACK for a create whose CRITICAL post-creation step failed (#638) —
    /// the second half of "a create is all-or-nothing".
    ///
    /// <para>Only a handler that declares <c>INodePostCreationHandler.FailsCreateOnError</c>
    /// reaches here (<see cref="RunPostCreationHandlersObs"/> swallows best-effort handlers), so
    /// the create's contract is genuinely unmet: the canonical case is a top-level
    /// <c>Space</c> whose creator-Admin grant did not land, which used to leave a partition
    /// root nobody owns — un-writable (RLS denies everyone), un-deletable, and un-re-creatable
    /// ("Node already exists"). Removing the row is what makes the caller's retry work.</para>
    ///
    /// <para><b>It deletes ONLY the row THIS create wrote.</b> Two guards, in order:
    /// <list type="number">
    ///   <item><c>mode == "create"</c> — the transient→Active <i>confirm</i> path targets a node
    ///     that ALREADY existed before the request, so there is nothing of ours to remove.</item>
    ///   <item>A lineage check against the durable row: only a row whose
    ///     <see cref="MeshNode.CreatedDate"/> is still the stamp this create wrote is removed. A
    ///     different stamp means the path is no longer "our" node (a concurrent recreate), and
    ///     the rollback stands down and says so rather than destroying someone else's state.</item>
    /// </list>
    /// Re-running it is harmless: an already-absent row reports "nothing to roll back".</para>
    ///
    /// <para><b>Partition artifacts are deliberately NOT dropped.</b> A top-level create may have
    /// provisioned the partition's backing store (schema + tables) through
    /// <c>OwnsPartitionProvisioningValidator</c> before the row was written. Provisioning is
    /// idempotent, so leaving it costs the retry nothing — whereas DROPping it is not reversible
    /// and would destroy data whenever the schema was NOT introduced by this create (exactly the
    /// case when a user retries a create over the residue of an earlier one). An empty
    /// provisioned schema with no root is inert; the write guard's speaking error names that
    /// state (<c>PartitionWriteGuardValidator.DescribeOwnerlessPartition</c>). The additional
    /// nodes a handler emits (e.g. the <c>Admin/Partition/{id}</c> definition) are written only
    /// AFTER its <c>Handle</c> succeeded (the <c>Concat</c> in the runner), so a failed critical
    /// handler leaves none behind.</para>
    ///
    /// <para>Emits exactly one human-readable outcome sentence, which the caller appends to the
    /// original failure — a rollback that could not run is REPORTED, never swallowed.</para>
    /// </summary>
    private static IObservable<string> CompensateFailedCreate(
        IMessageHub hub, MeshNode created, string mode, ILogger logger)
    {
        if (!string.Equals(mode, "create", StringComparison.Ordinal))
            return Observable.Return(
                $"The node at '{created.Path}' existed before this request, so nothing was rolled back.");

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return("Nothing was persisted (no storage adapter), so there was nothing to roll back.");

        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        return persistence.Read(created.Path, hub.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .SelectMany(stored =>
            {
                if (stored is null)
                    return Observable.Return($"No row remains at '{created.Path}' — nothing to roll back.");

                if (stored.CreatedDate != created.CreatedDate)
                {
                    logger.LogError(
                        "[CreateNode] rollback STOOD DOWN at {Path}: the stored row (created {StoredCreated:O}) is not "
                        + "the one this create wrote (created {OurCreated:O}) — refusing to delete a node we did not create",
                        created.Path, stored.CreatedDate, created.CreatedDate);
                    return Observable.Return(
                        $"The node at '{created.Path}' was NOT rolled back: the stored row is no longer the one this "
                        + "request wrote. Remove it manually before retrying.");
                }

                // The rollback is infrastructure repairing its OWN half-finished write, on a node
                // whose ownership grant is exactly what failed — so it runs as System (the caller
                // provably cannot authorize a delete on a partition nobody was granted).
                return AsSystem(accessService,
                        () => persistence.DeleteAndPublish(created.Path, changeFeed, created.NodeType).Take(1))
                    .Do(_ => logger.LogWarning(
                        "[CreateNode] rolled back partially-created node at {Path} — the create is all-or-nothing (#638)",
                        created.Path))
                    .Select(_ => $"The partially-created node at '{created.Path}' was rolled back; the create can be retried.");
            })
            .Catch<string, Exception>(ex =>
            {
                logger.LogError(ex,
                    "[CreateNode] ROLLBACK FAILED at {Path} — the partially-created node is still present",
                    created.Path);
                return Observable.Return(
                    $"Rolling back '{created.Path}' FAILED ({ex.Message}) — the partially-created node is still "
                    + "present and must be removed manually.");
            })
            .Take(1);
    }

    /// <summary>
    /// Handles <see cref="CreateNodesRequest"/> — the BULK sibling of
    /// <see cref="HandleCreateNodeRequest"/>. One request creates N plain nodes with ONE batched
    /// existence read, ONE partition bootstrap per distinct partition, the full validator/RLS +
    /// type-existence pass for EVERY node BEFORE anything is written, then ONE
    /// <see cref="IStorageAdapter.WriteMany"/> in caller order with the change-feed
    /// <c>Created</c> publishes following post-commit in that same order, and the post-creation
    /// handlers per created node. Satellites and <c>AccessAssignment</c> nodes are refused (their
    /// guards and side effects are deliberately per-node); existing paths are skipped and
    /// reported, never overwritten. Validate-all-then-write: a pre-write failure writes NOTHING.
    ///
    /// Fully synchronous handler — returns <see cref="IMessageDelivery"/>, never Task; the
    /// terminal response is posted from inside the reactive chain (see
    /// <see cref="HandleCreateNodeRequest"/>'s note).
    /// </summary>
    private static IMessageDelivery HandleCreateNodesRequest(
        IMessageHub hub,
        IMessageDelivery<CreateNodesRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Mesh.CreateNodes");
        var meshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();

        void PostFail(string error, NodeCreationRejectionReason reason, string? failedPath = null,
            ImmutableList<MeshNode>? created = null)
            => hub.Post(CreateNodesResponse.Fail(error, reason, failedPath, created),
                o => o.ResponseFor(request));

        if (meshConfig == null)
        {
            PostFail("MeshConfiguration not available", NodeCreationRejectionReason.Unknown);
            return request.Processed();
        }
        // FAIL CLOSED on missing storage — same contract as the singular create (a create that
        // cannot persist must error, never ack).
        if (persistence == null)
        {
            logger.LogError(
                "[CreateNodes] REFUSED batch of {Count}: no IStorageAdapter on hub {Hub} — the creates would be acked but never persisted.",
                request.Message.Nodes?.Count ?? 0, hub.Address);
            PostFail(
                $"No storage adapter on hub '{hub.Address}' — refusing the batch because it could not be persisted.",
                NodeCreationRejectionReason.Unknown);
            return request.Processed();
        }

        var createdBy = request.Message.CreatedBy;
        if (string.IsNullOrEmpty(createdBy) && request.AccessContext?.ObjectId is { Length: > 0 } senderId)
            createdBy = senderId;

        var nodes = request.Message.Nodes ?? ImmutableList<MeshNode>.Empty;
        if (nodes.Count == 0)
        {
            hub.Post(CreateNodesResponse.Ok(ImmutableList<MeshNode>.Empty, ImmutableList<string>.Empty),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // ——— Phase 0: synchronous structural guards — the whole batch fails on the first offender,
        // so a caller can never land half a plan behind a refusal it didn't see. ———
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in nodes)
        {
            // A null entry (deserialization artifact / caller bug) must refuse the batch as a
            // structured response, never surface as a NullReferenceException.
            if (candidate is null)
            {
                PostFail("Batch contains a null node entry",
                    NodeCreationRejectionReason.ValidationFailed);
                return request.Processed();
            }
            if (string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Path))
            {
                PostFail("Node path and Id must not be empty",
                    NodeCreationRejectionReason.ValidationFailed, candidate.Path);
                return request.Processed();
            }
            if (string.IsNullOrWhiteSpace(candidate.NodeType) && candidate.Content == null)
            {
                PostFail($"Node '{candidate.Path}' must have a NodeType or Content set; bare nodes are not allowed.",
                    NodeCreationRejectionReason.ValidationFailed, candidate.Path);
                return request.Processed();
            }
            // Satellites (_Access, _Activity, _Thread, …) carry per-node guards (ownerless-activity,
            // assignment scope/system-owned) and satellite-MainNode normalization that are
            // deliberately per-node lifecycle — refuse them here rather than half-support them.
            if (candidate.Segments.Any(segment => segment.StartsWith('_')))
            {
                PostFail(
                    $"'{candidate.Path}' is a satellite path — satellites are per-node lifecycle; use CreateNodeRequest/CreateOrUpdateNodeRequest.",
                    NodeCreationRejectionReason.InvalidPath, candidate.Path);
                return request.Processed();
            }
            if (string.Equals(candidate.NodeType, AccessAssignmentNodeTypeName, StringComparison.OrdinalIgnoreCase))
            {
                PostFail(
                    $"'{candidate.Path}' is an AccessAssignment — grants are per-node lifecycle; use CreateNodeRequest.",
                    NodeCreationRejectionReason.ValidationFailed, candidate.Path);
                return request.Processed();
            }
            if (!seenPaths.Add(candidate.Path))
            {
                PostFail($"Duplicate path in batch: '{candidate.Path}'",
                    NodeCreationRejectionReason.InvalidPath, candidate.Path);
                return request.Processed();
            }
        }

        var options = hub.JsonSerializerOptions;
        var capturedBy = createdBy;
        // What WriteMany reported as committed — read by the terminal error path so a storage
        // failure mid-batch reports what actually landed instead of guessing.
        var written = ImmutableList<MeshNode>.Empty;
        // The paths the write phase ATTEMPTED (set right before WriteMany subscribes). WriteMany
        // can throw after partially committing (Postgres windows commit per table, then a later
        // window rethrows) WITHOUT emitting — `written` then stays empty although nodes exist.
        // The error path probes these to keep the failure report honest.
        string[]? attemptedPaths = null;

        // ——— Phase 1: existence — ONE batched authoritative read plus the static/config fallback
        // the singular create consults (a definition-only catalog type-def is NOT a real node). ———
        persistence.ReadMany(nodes.Select(n => n.Path).ToArray(), options)
            .Select(existingNode => existingNode.Path)
            .ToList()
            .SelectMany(persistedPaths =>
            {
                var existing = new HashSet<string>(persistedPaths, StringComparer.Ordinal);
                foreach (var candidate in nodes)
                {
                    if (existing.Contains(candidate.Path))
                        continue;
                    var configNode = hub.ServiceProvider.FindStaticNode(candidate.Path);
                    if (configNode is not null && configNode is not { IsDefinitionOnly: true })
                        existing.Add(candidate.Path);
                }

                var existingPaths = nodes.Where(n => existing.Contains(n.Path))
                    .Select(n => n.Path).ToImmutableList();
                var toCreate = nodes
                    .Where(n => !existing.Contains(n.Path))
                    // The 1b' stale-bare-Id MainNode repair from the singular path (satellites are
                    // refused above, so the satellite normalization does not apply here).
                    .Select(n => !string.IsNullOrEmpty(n.NodeType)
                                 && !string.IsNullOrEmpty(n.Namespace)
                                 && !meshConfig.IsSatelliteNodeType(n.NodeType)
                                 && n.MainNode == n.Id
                        ? n with { MainNode = n.Path }
                        : n)
                    .ToImmutableList();

                if (toCreate.Count == 0)
                {
                    hub.Post(CreateNodesResponse.Ok(ImmutableList<MeshNode>.Empty, existingPaths),
                        o => o.ResponseFor(request));
                    return Observable.Empty<(ImmutableList<MeshNode> Created, ImmutableList<string> Existing)>();
                }

                // ——— Phase 2: partition bootstrap ONCE per distinct partition (the bootstrap
                // itself is idempotent and re-healing; batching it is pure round-trip economy). ———
                var bootstrap = toCreate
                    .Where(n => !string.IsNullOrEmpty(n.Namespace))
                    .GroupBy(n => n.Segments[0], StringComparer.Ordinal)
                    .Select(group => EnsurePartitionBootstrap(
                        hub, group.First(),
                        new CreateNodeRequest(group.First()) { CreatedBy = capturedBy }, logger))
                    .Concat()
                    .ToList()
                    .Select(_ => System.Reactive.Unit.Default);

                // ——— Phase 3: validators (RLS included) for EVERY node, sequential, before any
                // write. First failure fails the whole request — nothing has been written yet. ———
                var validate = toCreate
                    .Select(n => RunCreationValidatorsObs(
                            hub, n, new CreateNodeRequest(n) { CreatedBy = capturedBy })
                        .Select(error => (Node: n, Error: error)))
                    .Concat()
                    .Where(t => t.Error != null)
                    .Take(1)
                    .Select(t => ((MeshNode Node, (string? ErrorMessage, NodeCreationRejectionReason Reason)? Error)?)t)
                    .DefaultIfEmpty(null);

                // ——— Phase 4: type existence per DISTINCT NodeType (static provider, else
                // persistence — same recognition order as the singular create). ———
                var typesToProbe = toCreate
                    .Select(n => n.NodeType)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var probeTypes = typesToProbe
                    .Select(type => hub.ServiceProvider.FindStaticNode(type) is not null
                        ? Observable.Return((Type: type, Exists: true))
                        : persistence.Exists(type).Select(exists => (Type: type, Exists: exists)))
                    .Concat()
                    .Where(t => !t.Exists)
                    .Take(1)
                    .Select(t => ((string Type, bool Exists)?)t)
                    .DefaultIfEmpty(null);

                return bootstrap
                    .SelectMany(_ => validate)
                    .SelectMany(validationFailure =>
                    {
                        if (validationFailure is { } failure)
                        {
                            logger.LogWarning(
                                "[CreateNodes] validator rejected {Path}: {Error} — batch of {Count} refused, nothing written",
                                failure.Node.Path, failure.Error!.Value.ErrorMessage, toCreate.Count);
                            PostFail(failure.Error.Value.ErrorMessage ?? "Validation failed",
                                failure.Error.Value.Reason, failure.Node.Path);
                            return Observable.Empty<(ImmutableList<MeshNode>, ImmutableList<string>)>();
                        }

                        return probeTypes.SelectMany(missingType =>
                        {
                            if (missingType is { } missing)
                            {
                                var offender = toCreate.First(n => string.Equals(
                                    n.NodeType, missing.Type, StringComparison.Ordinal));
                                PostFail($"NodeType '{missing.Type}' is not registered",
                                    NodeCreationRejectionReason.InvalidNodeType, offender.Path);
                                return Observable.Empty<(ImmutableList<MeshNode>, ImmutableList<string>)>();
                            }

                            // ——— Phase 5: stamps — identical to the singular create. ———
                            var now = DateTimeOffset.UtcNow;
                            var stamped = toCreate.Select(n => n with
                            {
                                State = MeshNodeState.Active,
                                CreatedDate = n.CreatedDate == default ? now : n.CreatedDate,
                                CreatedBy = string.IsNullOrEmpty(n.CreatedBy) ? capturedBy : n.CreatedBy,
                                LastModified = n.LastModified == default ? now : n.LastModified,
                                LastModifiedBy = string.IsNullOrEmpty(n.LastModifiedBy) ? capturedBy : n.LastModifiedBy,
                                Version = n.Version > 0 ? n.Version : 1,
                            }).ToImmutableList();

                            // ——— Phase 6: ONE ordered WriteManyAndPublishCreated; the Created
                            // publishes ride the post-commit emission in caller order
                            // (commit-then-publish, exactly like WriteAndPublishCreated) — so
                            // stream caches, live queries AND the resolution caches invalidate
                            // for every node. The helper is the single publish site every bulk
                            // write goes through; the installer's System-side bulk path skipped
                            // it and left nodes in storage that the running mesh could not
                            // resolve. ———
                            attemptedPaths = stamped.Select(n => n.Path).ToArray();
                            return persistence.WriteManyAndPublishCreated(stamped, options, changeFeed)
                                .Select(list => list.ToImmutableList())
                                .Do(list => written = list)
                                .SelectMany(list =>
                                {
                                    if (list.Count != stamped.Count)
                                    {
                                        PostFail(
                                            $"Storage accepted {list.Count} of {stamped.Count} nodes — the batch did not land completely.",
                                            NodeCreationRejectionReason.Unknown, created: list);
                                        return Observable.Empty<(ImmutableList<MeshNode>, ImmutableList<string>)>();
                                    }

                                    // ——— Phase 7: post-creation handlers per created node,
                                    // sequential — same semantics as the singular create
                                    // (FailsCreateOnError propagates; best-effort handlers
                                    // log-and-continue inside the runner). ———
                                    return list
                                        .Select(saved => RunPostCreationHandlersObs(hub, saved, capturedBy, logger))
                                        .Concat()
                                        .ToList()
                                        .Select(_ => (list, existingPaths));
                                });
                        });
                    });
            })
            .Subscribe(
                result =>
                {
                    logger.LogInformation(
                        "[CreateNodes] created {Created} node(s), {Existing} already existed, by {CreatedBy}",
                        result.Item1.Count, result.Item2.Count, capturedBy ?? "system");
                    hub.Post(CreateNodesResponse.Ok(result.Item1, result.Item2),
                        o => o.ResponseFor(request));
                },
                ex =>
                {
                    void ReportError()
                    {
                        if (written.Count > 0)
                        {
                            logger.LogError(ex,
                                "[CreateNodes] failed AFTER {Written} node(s) were persisted — reporting the partial landing",
                                written.Count);
                            PostFail($"Nodes persisted but a later step failed: {ex.Message}",
                                NodeCreationRejectionReason.Unknown, created: written);
                        }
                        else if (ex is InvalidOperationException)
                        {
                            logger.LogWarning(ex, "[CreateNodes] batch refused");
                            PostFail(ex.Message, NodeCreationRejectionReason.ValidationFailed);
                        }
                        else
                        {
                            logger.LogError(ex, "[CreateNodes] unexpected error");
                            PostFail($"Unexpected error: {ex.Message}", NodeCreationRejectionReason.Unknown);
                        }
                    }

                    // WriteMany can partially COMMIT and then throw without emitting (Postgres
                    // windows are each their own transaction) — `written` is then empty although
                    // nodes exist. The attempted paths were all absent before the write (existence-
                    // filtered), so anything present NOW landed in this batch: probe and report it,
                    // never claim a clean refusal for a half-landed batch. Best-effort probe: a
                    // failing read falls back to the plain report rather than masking the error.
                    if (attemptedPaths is { Length: > 0 } probe && written.Count == 0)
                        persistence.ReadMany(probe, options)
                            .ToList()
                            .Catch<IList<MeshNode>, Exception>(probeEx =>
                            {
                                logger.LogWarning(probeEx,
                                    "[CreateNodes] partial-landing probe failed — reporting without it");
                                return Observable.Return((IList<MeshNode>)new List<MeshNode>());
                            })
                            .Subscribe(found =>
                            {
                                written = found.ToImmutableList();
                                ReportError();
                            });
                    else
                        ReportError();
                });

        return request.Processed();
    }

    /// <summary>
    /// The <c>Space</c> node type name — referenced by literal so this Mesh.Contract-level
    /// handler needs no dependency on the MeshWeaver.Graph assembly that defines the type.
    /// </summary>
    private const string PartitionRootNodeTypeName = "Space";

    /// <summary>The <c>AccessAssignment</c> node type name — same rationale as <see cref="PartitionRootNodeTypeName"/>.</summary>
    private const string AccessAssignmentNodeTypeName = "AccessAssignment";

    /// <summary>
    /// SELF-HEALING PARTITION BOOTSTRAP — the centralized invariant that every mesh partition
    /// has a persisted ROOT node (<c>Namespace==""</c>, <c>Id==partition</c>, NodeType
    /// <c>Space</c>). Without that root a <see cref="GetDataRequest"/> targeting the bare
    /// partition address has no terminal node to resolve to → the router loops → the
    /// partition's data source (<c>ds/&lt;Partition&gt;</c>) faults → catalog UIs break. The
    /// invariant used to be written in three scattered places (the static-repo importer,
    /// onboarding, the Space post-creation handler); here it is centralized on the one create
    /// handler every node create flows through, and made idempotent + re-healing.
    ///
    /// <para>For a CHILD create (non-empty namespace that is NOT itself an <c>_Access</c>
    /// assignment) it (1) re-creates the partition's <c>Space</c> root if it is absent —
    /// provisioning the partition's backing store first — and (2) grants the creator Admin under
    /// <c>{partition}/_Access</c> if absent. Both writes run under
    /// <see cref="AccessService.ImpersonateAsSystem"/> (a brand-new partition is owned by
    /// nobody, so the creator cannot authorize its own root/grant — the canonical
    /// infrastructure-write case).</para>
    ///
    /// <para><b>Gated to stay inside the existing security + partition model — it never
    /// implicitly creates a partition for someone who couldn't create there anyway:</b></para>
    /// <list type="bullet">
    ///   <item><b>Central node-operation hub only</b> (<see cref="IsCentralNodeOperationHub"/> —
    ///     the mesh's dedicated <c>portal/nodeops-{meshId}</c> execution hub, which is what the
    ///     <see cref="NodeOperationTarget"/> fallback resolves to; the mesh hub itself still counts
    ///     for the teardown path). Other create handlers — the static-repo import hub (which
    ///     already provisions its own roots and runs as System), MCP session / portal hubs, and
    ///     per-node hubs — don't redo it.</item>
    ///   <item><b>Host uses the Space partition model.</b> Skips entirely when the <c>Space</c>
    ///     node type is not registered (a host serving raw / doc / embedded partitions has its
    ///     own root mechanism — forcing a <c>Space</c> root there is wrong, and would fail the
    ///     type-existence check).</item>
    ///   <item><b>Authoritative existence.</b> Root + grant are probed by EXACT path through the
    ///     storage adapter AND the static/config node provider (a partition whose root is a
    ///     static node — e.g. the seeded test root — is NOT re-created). EXACT path is mandatory:
    ///     <c>scope:descendants</c> emits <c>LIKE 'P/%'</c> and never matches the
    ///     <c>namespace=""</c> root.</item>
    ///   <item><b>Authorization gate.</b> The heal runs ONLY when the creator actually holds
    ///     <see cref="Permission.Create"/> on the partition (the same predicate RLS uses). An
    ///     unauthorized creator triggers NO heal — the requested child is then denied by the
    ///     validators exactly as before, so the bootstrap can never launder an implicit-space
    ///     creation past <c>PartitionWriteGuardValidator</c>'s "no partition, no write" rule.
    ///     <b>One exception, and it grants the caller nothing</b>: an OWNERLESS partition (a root
    ///     exists, names a real creator, and <c>{partition}/_Access</c> is completely empty) has
    ///     its ORIGINAL creator's grant restored — see
    ///     <see cref="RepairOwnerlessPartitionGrant"/>. Gating that repair on the permission of
    ///     the very user whose grant is missing is what made the #638 residue self-heal for
    ///     platform admins only.</item>
    /// </list>
    ///
    /// <para><b>No recursion:</b> the root create (empty namespace) and the grant create (path
    /// under <c>/_Access/</c>) are exactly the two node shapes skipped at the top, so they never
    /// re-enter the bootstrap. A ROOT-SCOPE grant (<c>_Access/{subject}_Access</c>, scope
    /// <c>""</c>) does not match that <c>/_Access/</c> test — it is stopped instead by the
    /// partition-segment gate below, which is what keeps <c>_Access</c> from being mistaken for a
    /// partition and provisioned as a schema (#714). <b>Idempotent + race-safe:</b> a concurrent
    /// first-writer that loses the create race sees "already exists" and treats it as success. <b>Re-heals:</b>
    /// root + grant presence are re-probed on every child create, so a partition left half-broken
    /// (root-missing, root GHOSTED — a row with no type and no content, grant-missing,
    /// grant-less altogether, or any combination) is repaired on the next child create —
    /// nothing is permanently cached as "bootstrapped".</para>
    /// </summary>
    /// <param name="requestId">
    /// The awaited create delivery's id, or <c>null</c> for callers with no correlation to report
    /// against (the batch create). Used ONLY to record <c>BOOTSTRAP_PERM_*</c> stages on the
    /// <c>RequestFateLedger</c>; <c>NoteRequestStage</c> is a no-op when nothing awaits the id.
    /// </param>
    private static IObservable<System.Reactive.Unit> EnsurePartitionBootstrap(
        IMessageHub hub, MeshNode node, CreateNodeRequest request, ILogger logger, string? requestId = null)
    {
        // Central node-operation hub only — see remarks and IsCentralNodeOperationHub.
        if (!IsCentralNodeOperationHub(hub))
            return Observable.Return(System.Reactive.Unit.Default);

        // Skip the two node shapes the bootstrap itself writes: a partition root (empty
        // namespace) and an _Access assignment. Skipping them is what guarantees the root/grant
        // writes below never re-enter this method (no recursion / no infinite re-entry).
        if (string.IsNullOrEmpty(node.Namespace)
            || node.Path.Contains("/_Access/", StringComparison.Ordinal))
            return Observable.Return(System.Reactive.Unit.Default);

        // Only when the host uses the Space partition model. A host without the Space NodeType
        // (raw doc/embedded servers, minimal test hosts) has its own root mechanism — never force
        // a Space root onto it (that would also fail the type-existence check downstream).
        if (hub.ServiceProvider.FindStaticNode(PartitionRootNodeTypeName) is null)
            return Observable.Return(System.Reactive.Unit.Default);

        var partition = node.Segments.Count > 0 ? node.Segments[0] : null;
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(System.Reactive.Unit.Default);

        // 🚨 The first segment must actually BE a partition (#714). A partition becomes a
        // backing-store SCHEMA derived from its name, so only a name satisfying the ONE shared
        // rule can be bootstrapped — anything else would provision a schema the router refuses
        // to route to, i.e. a ghost by construction.
        //
        // The shape this caught: a ROOT-SCOPE access grant lives at
        // `_Access/{subject}_Access` (namespace `_Access`, scope ""). The skip test above is
        // `Path.Contains("/_Access/")`, whose leading slash matches `{P}/_Access/…` but NOT that
        // root-scope path — so it fell through here, `Segments[0]` read `_Access` as a PARTITION,
        // and the bootstrap created a `Space` root at `_Access` AND provisioned a schema
        // literally named `_access`. The router resolves a `_`-prefixed first segment ONLY via a
        // registered PartitionDefinition with an explicit schema (`_Access` → `system_access`)
        // and never derives one from the name, so nothing could ever read or write `_access`.
        // The rule is general, not an `_Access` special case: no `_`-prefixed satellite container
        // (`_Thread`, `_Activity`, …) and no URL-shaped junk segment is a bootstrappable
        // partition.
        if (!PartitionDefinition.IsValidPartitionSegment(partition))
            return Observable.Return(System.Reactive.Unit.Default);

        // Never bootstrap a system-managed MIRROR partition (User, Auth). These reject ALL
        // interactive writes (PartitionWriteGuardValidator Rule 1), so a create attempt into e.g.
        // `Auth/…` — which this method reaches BEFORE the validators run — would otherwise heal a
        // bogus Space root + creator-Admin grant on `Auth` (the RLS CheckPermission below passes for
        // a global admin), and THEN have the child write rejected by the structural guard, leaving
        // the errant grant (and its "you've been given access to Auth" email) behind. The mirror is
        // middleware-only; there is nothing to bootstrap.
        if (WellKnownPartitions.IsMirror(partition))
            return Observable.Return(System.Reactive.Unit.Default);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (persistence is null || meshService is null)
            return Observable.Return(System.Reactive.Unit.Default);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var creator = request.CreatedBy;
        var isRealCreator = !string.IsNullOrEmpty(creator)
            && !string.Equals(creator, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);
        var grantPath = isRealCreator ? $"{partition}/_Access/{creator}_Access" : null;

        // Authoritative existence probes (persistence + static/config fallback), run together.
        // The ROOT probe keeps its SOURCE: only a DURABLE row may be repaired in place (a
        // static/config root lives in configuration, never in the store — rewriting it into
        // storage would materialise a config entry as data). See RepairGhostRoot.
        var rootObs = ReadRootWithSource(hub, persistence, partition);
        var grantObs = grantPath is null
            ? Observable.Return<MeshNode?>(null)
            : ReadNodeAuthoritative(hub, persistence, grantPath);
        // A GitSynced partition is SYSTEM-OWNED by definition — its content is rewritten from the
        // repo and only system-security writes it. That is the ownership signal the root itself
        // does not carry (ProvisionAndCreateRoot writes the root as System, so root.CreatedBy is
        // never the owner), and it is what distinguishes "I am creating my own partition" from
        // "I am a deploy touching somebody else's".
        var syncObs = ReadNodeAuthoritative(hub, persistence, $"{partition}/_GitSync");

        return Observable.Zip(rootObs, grantObs, syncObs, (root, grant, sync) => (root, grant, sync))
            .SelectMany(t =>
            {
                // 🚨 EXISTENCE IS NOT ENOUGH — the root must be USABLE (#638). A row that is
                // there but carries neither a NodeType nor Content is a GHOST: the residue of a
                // create that wrote the row and then failed. `root is not null` accepted exactly
                // that residue as "the partition is fine", so the one seam that re-heals
                // partitions skipped the only partitions that actually needed healing — a ghost
                // root can be neither read, nor created ("already exists"), nor routed to.
                var rootUsable = IsUsableRoot(t.root.Node);
                // For a System / unattributed creator there is no per-creator grant to ensure.
                var grantExists = !isRealCreator || t.grant is not null;
                if (rootUsable && grantExists)
                    return Observable.Return(System.Reactive.Unit.Default);

                // Authorization gate — heal ONLY for a creator who could legitimately create here.
                // System short-circuits to Permission.All; an unauthorized creator gets no heal,
                // so the requested child is denied by the validators exactly as before.
                var effectiveUser = string.IsNullOrEmpty(creator) ? WellKnownUsers.Anonymous : creator;
                // 🚨 TakeDecisionOutsideGate, not a bare Take(1) — #899. The continuation
                // below CREATES the partition root and mints the creator grant, i.e. it
                // writes and publishes to the change feed. Running that inside the
                // permission fold's CombineLatest gate makes every shared gate the
                // (synchronous, by contract) fan-out touches half of a lock-order inversion.
                // See HubPermissionExtensions.TakeDecisionOutsideGate.
                // 🔍 #981 — the stage that splits "the fold is STILL RUNNING" from "the fold
                // TERMINATED". Every capture so far shows the create's chain subscribed and no
                // terminal stage after it, which is ambiguous precisely here: this permission fold
                // is the one place in the create path that can legitimately take SECONDS (a
                // cold-start synced query on a fresh partition), and it is bounded at 15 s — well
                // above the 2 s quiescing budget that DETECTS the pending callback. So a capture
                // that ends at BOOTSTRAP_PERM_AWAIT is a create waiting on a slow-but-healthy
                // authorization probe, and one that reaches a verdict/timeout/empty stage is not.
                // Without this pair the two are indistinguishable in the trail.
                hub.NoteRequestStage(requestId,
                    $"BOOTSTRAP_PERM_AWAIT partition={partition} user={effectiveUser}");
                return hub.CheckPermission(partition, effectiveUser, Permission.Create)
                    .TakeDecisionOutsideGate()
                    .Timeout(TimeSpan.FromSeconds(15))
                    .Catch<bool, Exception>(ex =>
                    {
                        hub.NoteRequestStage(requestId,
                            $"BOOTSTRAP_PERM_FAULTED {ex.GetType().Name}");
                        logger.LogDebug(ex,
                            "[PartitionBootstrap] authorization probe for {User} on '{Partition}' faulted; skipping heal",
                            effectiveUser, partition);
                        return Observable.Return(false);
                    })
                    // 🚨 An authorization probe that COMPLETES WITHOUT A VERDICT must not vanish.
                    //
                    // `TakeDecisionOutsideGate()` is `Take(1).SelectMany(...)`, and the `.Timeout`
                    // above CANNOT catch this case: Timeout faults on SILENCE, not on a clean
                    // finish. So a fold that completes without emitting would sail past every bound
                    // in this chain, EnsurePartitionBootstrap would emit nothing, and the create's
                    // whole chain would terminate unanswered — the same shape as the
                    // `.Where(n => n is not null)` that used to swallow a declined write.
                    //
                    // Today this is UNREACHABLE through the shipped evaluator, and the reason is
                    // worth writing down because it is also why a STALL is the realistic failure:
                    // the fold rides `SyncedQueryMeshNodes`, whose `allChanges` merges a
                    // `Subject` that is never completed — so that substrate can never complete at
                    // all, only stall (which the Timeout above does catch). But
                    // `EffectivePermissionsDelegate` is a DI extension point: an evaluator that
                    // answers `Observable.Empty<Permission>()` is a legal implementation, and the
                    // framework must not hang because one did. Fail CLOSED with the same verdict
                    // the faulted-fold arm already produces, and say so loudly — a guard that is
                    // inert today costs nothing and removes an entire silent-hang class.
                    .Select(verdict => (bool?)verdict)
                    .DefaultIfEmpty(null)
                    .Select(verdict =>
                    {
                        if (verdict is null)
                        {
                            hub.NoteRequestStage(requestId,
                                $"BOOTSTRAP_PERM_COMPLETED_EMPTY partition={partition} user={effectiveUser}");
                            logger.LogError(
                                "[PartitionBootstrap] the authorization probe for {User} on '{Partition}' COMPLETED "
                                + "WITHOUT A VERDICT — treating it as denied (heal skipped) so the create still "
                                + "answers. An EffectivePermissionsDelegate must emit a decision; find the evaluator "
                                + "that completed empty.",
                                effectiveUser, partition);
                            return false;
                        }
                        hub.NoteRequestStage(requestId,
                            $"BOOTSTRAP_PERM_VERDICT authorized={verdict.Value}");
                        return verdict.Value;
                    })
                    .SelectMany(authorized =>
                    {
                        if (!authorized)
                            // 🚨 #638 — the ONE heal that must NOT sit behind this gate. A
                            // partition whose creator-grant never landed denies EVERYONE,
                            // starting with the very user whose grant is missing, so gating the
                            // repair on that user's permission makes the residue self-heal only
                            // for a platform admin. The repair below is deliberately NOT "grant
                            // the caller what they asked for": it restores the grant of the
                            // partition root's ORIGINAL creator, and only when the partition
                            // carries no grants AT ALL — the framework repairing its own failed
                            // bookkeeping, not an authorization decision.
                            return RepairOwnerlessPartitionGrant(
                                hub, partition, t.root.Node, systemOwned: t.sync is not null,
                                persistence, meshService, accessService, logger);

                        var healRoot = rootUsable
                            ? Observable.Return(System.Reactive.Unit.Default)
                            : ProvisionAndCreateRoot(hub, partition, meshService, accessService, logger,
                                // A DURABLE but unusable row is repaired in place; an absent root
                                // (or a static/config one) takes the ordinary create path.
                                ghost: t.root.Durable ? t.root.Node : null);
                        // 🚨 A deploy is not an ownership claim. Running `git_hub_sync update` on
                        // `Skill` — a partition that had existed for weeks — wrote one activity
                        // node into it and walked away with Admin, reproducibly, on every sync.
                        // The self-heal still does its job for a user's own partition, which is
                        // what it exists for; it just no longer fires on a SYSTEM-OWNED one, where
                        // the caller is a deployer and never the owner.
                        var systemOwned = t.sync is not null;
                        var mintGrant = isRealCreator && !grantExists && !systemOwned;
                        if (isRealCreator && !grantExists && systemOwned)
                            logger.LogInformation(
                                "[PartitionBootstrap] '{Creator}' gets NO grant on GitSynced partition "
                                + "'{Partition}' — it is system-owned; writing into it is a deploy, "
                                + "not an ownership claim", creator, partition);
                        return healRoot.SelectMany(_ => mintGrant
                            ? CreateCreatorGrant(partition, creator!, meshService, accessService, logger)
                            : Observable.Return(System.Reactive.Unit.Default));
                    });
            });
    }

    /// <summary>
    /// Reads a single node by EXACT path authoritatively: the storage adapter first (a read fault
    /// on a not-yet-provisioned PG schema means the node is, by definition, absent → null), then
    /// the static/config node provider — so a partition whose root is a static node is recognized
    /// as present and never re-created.
    ///
    /// <para>🚨 A DEFINITION-ONLY static entry is NOT a node at this path and must never answer an
    /// existence probe — the same rule <c>HandleCreateNodeRequest</c> and the batch create already
    /// apply. A NodeType whose discriminator equals its catalog's partition name registers its
    /// type definition at the bare partition path (<c>@Agent</c>, <c>@Skill</c>, <c>@Harness</c>);
    /// <see cref="MeshNode.IsDefinitionOnly"/> is what declares that entry a DEFINITION rather than
    /// a served node, and it is the platform's only name-keyed home for the non-serialisable
    /// <c>HubConfiguration</c> delegate. Letting it answer here made
    /// <c>EnsurePartitionBootstrap</c> believe the partition root already existed, so
    /// <c>ProvisionAndCreateRoot</c> never ran: no schema was provisioned and no durable root was
    /// written, while every other seam correctly saw nothing. That is exactly the ghost partition
    /// root of #902 — present to the existence check, absent to reads, un-creatable ("already
    /// exists"), with no version history — and it is why the platform's agent catalog could not be
    /// repaired by any route. See Doc/Architecture/NodeTypeCatalogs.md.</para>
    /// </summary>
    private static IObservable<MeshNode?> ReadNodeAuthoritative(
        IMessageHub hub, IStorageAdapter persistence, string path)
        => persistence.Read(path, hub.JsonSerializerOptions)
            .Take(1)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .DefaultIfEmpty(null)
            .Select(n => n ?? StaticNodeAt(hub, path));

    /// <summary>
    /// The static/config node genuinely SERVED at <paramref name="path"/>, or <c>null</c> — a
    /// definition-only entry is a type definition, not a node, and never counts as one.
    /// </summary>
    private static MeshNode? StaticNodeAt(IMessageHub hub, string path) =>
        hub.ServiceProvider.FindStaticNode(path) is { IsDefinitionOnly: false } served ? served : null;

    /// <summary>
    /// <see cref="ReadNodeAuthoritative"/> for a partition ROOT, keeping WHERE the node came
    /// from: <c>Durable</c> is true only for a row that really sits in the store. The bootstrap
    /// needs that distinction because it may REPAIR an unusable root in place, and only a durable
    /// row may be rewritten — a static/config root is configuration, and writing it into storage
    /// would turn a config entry into data.
    /// </summary>
    private static IObservable<(MeshNode? Node, bool Durable)> ReadRootWithSource(
        IMessageHub hub, IStorageAdapter persistence, string path)
        => persistence.Read(path, hub.JsonSerializerOptions)
            .Take(1)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .DefaultIfEmpty(null)
            .Select(n => n is not null ? (n, true) : (StaticNodeAt(hub, path), false));

    /// <summary>
    /// Is this partition root node USABLE, i.e. a real root rather than the GHOST residue of a
    /// half-completed create (#638/#902)?
    ///
    /// <para>A root that carries NEITHER a <see cref="MeshNode.NodeType"/> NOR
    /// <see cref="MeshNode.Content"/> is nothing anyone can use: no type means no per-node hub
    /// configuration and no content type, so the bare partition address has no terminal node to
    /// resolve to. It is what a create leaves behind when the row lands and everything after it
    /// fails. Either one present is enough — the bootstrap's OWN roots are typed
    /// (<c>Space</c>) with no content, while an imported/typed root may carry content instead —
    /// so this test repairs the residue without ever touching a legitimately-minimal root.</para>
    /// </summary>
    private static bool IsUsableRoot(MeshNode? root) =>
        root is not null
        && (!string.IsNullOrWhiteSpace(root.NodeType) || root.Content is not null);

    /// <summary>
    /// Provisions every provider's backing store (PG schema + tables) then writes the partition's
    /// <c>Space</c> root under System. Idempotent — a concurrent-create "already exists" is success.
    ///
    /// <para>When <paramref name="ghost"/> is supplied — a DURABLE row that exists but is not a
    /// usable root — the root is repaired IN PLACE instead (see <see cref="RepairGhostRoot"/>).
    /// Going through <c>CreateNode</c> there could never work: the row makes the create answer
    /// "already exists", which this method treats as success, so the ghost survived every
    /// bootstrap that ran over it.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> ProvisionAndCreateRoot(
        IMessageHub hub, string partition, IMeshService meshService,
        AccessService? accessService, ILogger logger, MeshNode? ghost = null)
    {
        // Reactive + pooled + promise-cached; the InMemory / FileSystem providers no-op. Merge +
        // ToList so the chain always emits exactly once (even with no providers) before the write.
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provision = providers.Length == 0
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(providers.Select(p => p.EnsurePartitionProvisioned(partition)))
                .ToList()
                .Select(_ => System.Reactive.Unit.Default);

        if (ghost is not null)
            return provision.SelectMany(_ => RepairGhostRoot(hub, partition, ghost, accessService, logger));

        var root = new MeshNode(partition)
        {
            NodeType = PartitionRootNodeTypeName,
            State = MeshNodeState.Active,
            Name = partition,
        };

        return provision.SelectMany(_ =>
            AsSystem(accessService, () => meshService.CreateNode(root).Take(1))
                .Select(_ => System.Reactive.Unit.Default)
                .Catch<System.Reactive.Unit, Exception>(ex => IsAlreadyExists(ex)
                    ? Observable.Return(System.Reactive.Unit.Default)
                    : Observable.Throw<System.Reactive.Unit>(ex))
                .Do(_ => logger.LogInformation(
                    "[PartitionBootstrap] created missing Space root for partition '{Partition}'", partition)));
    }

    /// <summary>
    /// Repairs a GHOST partition root — a durable row with no type and no content — IN PLACE, so
    /// the partition becomes routable again without losing the node's lineage (#638/#902).
    ///
    /// <para>The write is stamped at <see cref="MeshNode.NextVersion(long)"/> of the row we just
    /// read, i.e. forward BY CONSTRUCTION: <c>MonotonicWriteGuardStorageAdapter</c> refuses any
    /// write landing below the stored version, and a ghost never sits at 0 (it is the residue of
    /// a node that WAS written), so a repair minted from a blank snapshot is silently discarded —
    /// the same flooring #909 added to the create-or-update path. Id/Namespace/CreatedDate ride
    /// along from the ghost, so this is the SAME node repaired, not a new one shadowing it.</para>
    ///
    /// <para>Runs as System (a partition whose root is broken grants nobody anything) and writes
    /// through the storage adapter + change feed rather than the node's own hub: activating a hub
    /// on a content-less root is precisely what used to hang, and the change-feed publish is what
    /// invalidates the resolution caches that still hold the ghost as unroutable. Errors are NOT
    /// swallowed — they propagate and fail the create that triggered the heal, with the real cause.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> RepairGhostRoot(
        IMessageHub hub, string partition, MeshNode ghost, AccessService? accessService, ILogger logger)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return(System.Reactive.Unit.Default);
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();

        var repaired = ghost with
        {
            NodeType = PartitionRootNodeTypeName,
            State = MeshNodeState.Active,
            Name = string.IsNullOrWhiteSpace(ghost.Name) ? partition : ghost.Name,
            Version = MeshNode.NextVersion(ghost.Version),
            LastModified = DateTimeOffset.UtcNow,
            LastModifiedBy = WellKnownUsers.System,
        };

        return AsSystem(accessService,
                () => persistence.WriteAndPublishUpdated(repaired, hub.JsonSerializerOptions, changeFeed).Take(1))
            .Select(_ => System.Reactive.Unit.Default)
            .Do(_ => logger.LogWarning(
                "[PartitionBootstrap] repaired GHOST root of partition '{Partition}' in place (v{From} → v{To}): the row "
                + "existed but carried no type and no content — the residue of a half-completed create (#638)",
                partition, ghost.Version, repaired.Version));
    }

    /// <summary>
    /// Restores the grant of a partition root's ORIGINAL creator when the partition carries NO
    /// access grants at all (#638) — the framework repairing its own failed bookkeeping.
    ///
    /// <para><b>Why it must run for an UNAUTHORIZED caller.</b> A partition with zero grants
    /// denies everyone, and the first person it denies is the user whose grant went missing. The
    /// bootstrap's ordinary heal sits behind <c>CheckPermission(partition, creator, Create)</c>,
    /// which that user can never pass — so the residue self-healed only for a platform admin who
    /// happened to write into it.</para>
    ///
    /// <para><b>Why it is not a privilege escalation.</b> It never grants the CALLER anything.
    /// The subject is taken from the ROOT's <see cref="MeshNode.CreatedBy"/> — the identity that
    /// created the partition and should have been granted Admin by the create that failed — and
    /// only when:
    /// <list type="bullet">
    ///   <item>a root exists and names a real (non-System, non-anonymous) creator — a
    ///     System-created root has no owner to restore, and inventing one is not repair;</item>
    ///   <item>the partition is not SYSTEM-OWNED (GitSynced) — those are owned by the deploy;</item>
    ///   <item>and <c>{partition}/_Access</c> is EMPTY — in the store AND in configuration (a
    ///     statically-seeded grant is a grant). One remaining grant means the partition's
    ///     ownership is intact and a missing individual grant is a deliberate access decision, not
    ///     a broken create. (An indeterminate probe counts as "has grants": no repair.)</item>
    /// </list>
    /// The write itself is idempotent — a lost race sees "already exists" and is success.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> RepairOwnerlessPartitionGrant(
        IMessageHub hub, string partition, MeshNode? root, bool systemOwned, IStorageAdapter persistence,
        IMeshService meshService, AccessService? accessService, ILogger logger)
    {
        if (root is null || systemOwned)
            return Observable.Return(System.Reactive.Unit.Default);

        var owner = root.CreatedBy;
        if (string.IsNullOrEmpty(owner)
            || string.Equals(owner, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
            || string.Equals(owner, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(System.Reactive.Unit.Default);

        // A CONFIGURED grant is a grant: static nodes never reach the store, so the durable probe
        // below would read a perfectly-owned partition as ownerless.
        if (hub.ServiceProvider.EnumerateStaticNodes()
            .Any(n => n.Path.StartsWith($"{partition}/_Access/", StringComparison.OrdinalIgnoreCase)))
            return Observable.Return(System.Reactive.Unit.Default);

        return persistence.ListChildPaths($"{partition}/_Access")
            .Take(1)
            .Select(children => children.NodePaths?.Any() == true)
            // Indeterminate (an unreadable / not-yet-provisioned store) is NOT "no grants":
            // fail closed and repair nothing.
            .Catch<bool, Exception>(ex =>
            {
                logger.LogDebug(ex,
                    "[PartitionBootstrap] could not list '{Partition}/_Access'; skipping the ownerless-partition repair",
                    partition);
                return Observable.Return(true);
            })
            .SelectMany(hasGrants => hasGrants
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.Defer(() =>
                {
                    logger.LogWarning(
                        "[PartitionBootstrap] partition '{Partition}' carries NO access grants at all — restoring the "
                        + "grant of its ORIGINAL creator '{Owner}' (root.CreatedBy). This repairs a create whose "
                        + "creator-Admin grant never landed (#638); the caller is granted nothing.",
                        partition, owner);
                    return CreateCreatorGrant(partition, owner!, meshService, accessService, logger);
                }));
    }

    /// <summary>
    /// Writes the creator's Admin <c>AccessAssignment</c> under <c>{partition}/_Access</c> as
    /// System, mirroring exactly the shape onboarding / <c>SpacePostCreationHandler</c> write
    /// (id <c>{creator}_Access</c>, the <c>Admin</c> role, <c>MainNode = partition</c>).
    /// Idempotent — a concurrent-create "already exists" is success.
    /// </summary>
    private static IObservable<System.Reactive.Unit> CreateCreatorGrant(
        string partition, string creator, IMeshService meshService,
        AccessService? accessService, ILogger logger)
    {
        var grant = new MeshNode($"{creator}_Access", $"{partition}/_Access")
        {
            NodeType = AccessAssignmentNodeTypeName,
            Name = $"{creator} Access",
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = creator,
                DisplayName = creator,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        };

        return AsSystem(accessService, () => meshService.CreateNode(grant).Take(1))
            .Select(_ => System.Reactive.Unit.Default)
            .Catch<System.Reactive.Unit, Exception>(ex => IsAlreadyExists(ex)
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.Throw<System.Reactive.Unit>(ex))
            .Do(_ => logger.LogInformation(
                "[PartitionBootstrap] granted {Role} to creator '{Creator}' on partition '{Partition}'",
                Role.Admin.Id, creator, partition));
    }

    /// <summary>
    /// Establishes the well-known System identity on the write's OWN subscribe thread so the cold
    /// <see cref="IMeshService.CreateNode"/> captures System into its <c>CreatedBy</c> at its
    /// <c>Defer</c> (a brand-new partition root/grant is owned by nobody — the canonical
    /// infrastructure-write). Mirrors <c>StaticRepoImporter.AsSystem</c>.
    /// </summary>
    private static IObservable<T> AsSystem<T>(AccessService? access, Func<IObservable<T>> write)
        => access is null
            ? Observable.Defer(write)
            : Observable.Using(() => access.ImpersonateAsSystem(), _ => write());

    /// <summary>
    /// True if the exception (or any inner) reports an "already exists" outcome — the idempotent-create
    /// success signal when a concurrent first-writer won the race.
    /// </summary>
    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>
    /// 100% reactive delete handler — no <c>await</c>, no <c>Observable.FromAsync</c> wrapping
    /// blocking <c>IMeshStorage</c> calls. Because <see cref="IMeshService.DeleteNode"/> now
    /// targets the node's own hub (via <c>new Address(path)</c>), this handler always runs on
    /// the node's own hub and can therefore:
    /// <list type="bullet">
    /// <item>Read its own node via <c>hub.GetWorkspace().GetStream&lt;MeshNode&gt;().Take(1)</c> —
    /// the workspace's MeshNode type source is a replay-cached stream that emits the own node
    /// synchronously on subscribe (see <c>Doc/Architecture/CqrsAndContentAccess</c>).</item>
    /// <item>Discover children via <c>meshService.ObserveQuery&lt;MeshNode&gt;</c> with
    /// <c>namespace:{path}</c> — reactive query, no <c>IAsyncEnumerable</c> enumeration on
    /// the thread pool.</item>
    /// <item>Fan out recursive child deletes via <c>Observable.Merge</c> + <c>ToArray</c> —
    /// each child bounded by <c>Timeout</c>, so a lost response surfaces as a failure instead
    /// of hanging forever. No <c>Interlocked</c> counter.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Central delete orchestrator. Four phases:
    /// <list type="number">
    /// <item><description><b>Collect.</b> Root + (recursive) descendants via
    /// <see cref="IStorageAdapter"/> (storage adapter — no workspace/type-source detour).</description></item>
    /// <item><description><b>Permission.</b> Check <see cref="Permission.Delete"/> for
    /// every path via <c>SecurityService</c>. Any denial fails the whole op
    /// with the full list of denied paths in the <see cref="ActivityLog"/>.</description></item>
    /// <item><description><b>Validate.</b> Run <see cref="INodeValidator"/> chain for
    /// every node. Errors block; warnings block unless
    /// <see cref="DeleteNodeRequest.ConfirmWarnings"/> is set. Custom hubs that want
    /// cross-hub validation can additionally post <see cref="ValidateDeleteRequest"/>
    /// — there's a default handler registered by <see cref="WithNodeOperationHandlers"/>
    /// on every hub that opts in.</description></item>
    /// <item><description><b>Commit.</b> Bulk-delete via <see cref="IStorageAdapter"/>
    /// directly, bottom-up. Publish change events. Reply + DisposeRequest(s) from the
    /// mesh hub so FIFO guarantees the caller sees the Ok before the deleted hubs tear
    /// down.</description></item>
    /// </list>
    /// </summary>
    private static IMessageDelivery HandleDeleteNodeRequest(
        IMessageHub hub,
        IMessageDelivery<DeleteNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshNode>>();
        var opts = hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions();
        var persistence = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        var meshHub = ResolveMeshHub(hub);
        // "Delete wins" tombstone — populated SYNCHRONOUSLY here so it is in place before this
        // delete's response returns, i.e. before any later hub activation can resurrect the row.
        // (Null on meshes without Graph — the resurrect race is Graph's per-node-hub save path.)
        var recentlyDeleted = hub.ServiceProvider.GetService<RecentlyDeletedRegistry>();

        var deleteRequest = request.Message;
        // 🚨 Capture the caller's identity FROM THE DELIVERY at handler entry.
        // accessService.Context is set by the delivery pipeline before this
        // handler runs, but it gets LOST across the .SelectMany boundary into
        // the workspace stream callback (the callback runs on the workspace's
        // emission scheduler; AsyncLocal flow is not preserved). Reading
        // accessService.Context inside CheckDeletePermissionForNode would
        // therefore see null and fall through to CircuitContext (the
        // DevLogin admin in tests) — masking the actual caller in non-test
        // setups. Capture explicitly here and thread the userId through.
        var senderUserId = request.AccessContext?.ObjectId
                           ?? accessService?.Context?.ObjectId
                           ?? accessService?.CircuitContext?.ObjectId
                           ?? WellKnownUsers.Anonymous;
        if (string.IsNullOrEmpty(deleteRequest.DeletedBy)
            && !string.IsNullOrEmpty(senderUserId)
            && senderUserId != WellKnownUsers.Anonymous)
            deleteRequest = deleteRequest with { DeletedBy = senderUserId };

        var capturedRequest = deleteRequest;
        var path = capturedRequest.Path;
        var startedAt = DateTime.UtcNow;

        logger.LogInformation(
            "[DeleteNode] start path={Path} recursive={Recursive} confirmWarnings={Confirm} deletedBy={DeletedBy}",
            path, capturedRequest.Recursive, capturedRequest.ConfirmWarnings,
            capturedRequest.DeletedBy ?? "system");

        var baseActivity = new ActivityLog("NodeDeletion")
        {
            HubPath = path,
            Start = startedAt,
            User = !string.IsNullOrEmpty(capturedRequest.DeletedBy)
                ? new UserInfo(capturedRequest.DeletedBy, capturedRequest.DeletedBy)
                : null
        };

        void PostFailed(string error, NodeDeletionRejectionReason reason, ImmutableList<LogMessage> logMessages, ImmutableList<string>? affected = null)
        {
            var failLog = baseActivity with
            {
                Messages = logMessages,
                AffectedPaths = affected ?? [path],
                End = DateTime.UtcNow,
                Status = ActivityStatus.Failed
            };
            hub.Post(
                DeleteNodeResponse.Fail(error, reason) with { Log = failLog },
                o => o.ResponseFor(request));
        }

        // Accumulator for per-node activity messages emitted by each leaf's
        // own delete handler (validator warnings, etc.) — surfaced in the
        // top-level activity log on success.
        var collectedMessages = ImmutableList.CreateBuilder<LogMessage>();

        // 1. Load the root MeshNode directly from persistence — avoids
        //    activating the per-node hub at `path` (which workspace.GetMeshNodeStream
        //    would trigger via SubscribeRequest). Per-node hub cold-start
        //    activation can take 5-45s in CI (NodeType compile, dependency
        //    load, JIT), causing the previous 5s Timeout to throw NodeNotFound
        //    even when the node clearly exists in storage. The delete flow
        //    only needs the node's content to validate + plan — it does NOT
        //    need the live per-node hub state.
        persistence.Read(path, hub.JsonSerializerOptions)
            .DefaultIfEmpty(null!)
            .SelectMany(rootNode =>
            {
                if (rootNode is null)
                {
                    logger.LogDebug("[DeleteNode] not-found path={Path}", path);
                    PostFailed(
                        $"Node not found at path: {path}",
                        NodeDeletionRejectionReason.NodeNotFound,
                        [new LogMessage($"Node not found at path: {path}", LogLevel.Error)]);
                    return Observable.Empty<System.Reactive.Unit>();
                }

                // 2. Validate + check Delete permission for THIS node (root of the
                //    operation). Descendants are validated by their own per-node
                //    hub when fan-out fires a non-recursive DeleteNodeRequest at
                //    each leaf's address — never load all descendant nodes upfront.
                return CheckDeletePermissionForNode(hub, senderUserId, rootNode, logger)
                    .SelectMany(denied =>
                    {
                        if (denied)
                        {
                            logger.LogWarning("[DeleteNode] permission-denied path={Path}", path);
                            PostFailed(
                                $"Delete permission denied for '{path}'",
                                NodeDeletionRejectionReason.Unauthorized,
                                [new LogMessage($"Delete permission denied for '{path}'", LogLevel.Error)],
                                ImmutableList.Create(path));
                            return Observable.Empty<System.Reactive.Unit>();
                        }

                        return RunDeletionValidatorsWithWarningsObs(hub, rootNode, capturedRequest, request.AccessContext)
                            .SelectMany(vresult =>
                            {
                                if (vresult.Error is { Length: > 0 } err)
                                {
                                    logger.LogWarning("[DeleteNode] validator-rejected path={Path} err={Err}", path, err);
                                    PostFailed(
                                        $"Cannot delete '{path}': {err}",
                                        NodeDeletionRejectionReason.ValidationFailed,
                                        [new LogMessage($"Cannot delete '{path}': {err}", LogLevel.Error)],
                                        ImmutableList.Create(path));
                                    return Observable.Empty<System.Reactive.Unit>();
                                }

                                if (!vresult.Warnings.IsEmpty && !capturedRequest.ConfirmWarnings)
                                {
                                    logger.LogInformation(
                                        "[DeleteNode] warnings-require-confirmation path={Path} warnings={Count}",
                                        path, vresult.Warnings.Count);
                                    var msgs = vresult.Warnings
                                        .Select(w => new LogMessage($"'{path}': {w}", LogLevel.Warning))
                                        .ToImmutableList();
                                    PostFailed(
                                        $"Delete of '{path}' has {vresult.Warnings.Count} warning(s) (first: {vresult.Warnings[0]}). Set ConfirmWarnings=true to proceed.",
                                        NodeDeletionRejectionReason.WarningsRequireConfirmation,
                                        msgs,
                                        ImmutableList.Create(path));
                                    return Observable.Empty<System.Reactive.Unit>();
                                }

                                var warningMsgs = vresult.Warnings
                                    .Select(w => new LogMessage($"'{path}': {w}", LogLevel.Warning))
                                    .ToImmutableList();
                                lock (collectedMessages) collectedMessages.AddRange(warningMsgs);

                                // 3. Collect descendant paths (paths only — no content).
                                //    🚨 The subtree-deletion scope opens BEFORE the plan is
                                //    enumerated: from here until the operation completes
                                //    (success or failure — Observable.Using releases the
                                //    scope on OnError / OnCompleted / unsubscribe alike),
                                //    the storage write guard refuses every in-process write
                                //    at or under `path`, so nothing can be created between
                                //    planning and commit (issue #839's mid-flight Release
                                //    satellites). Cross-process writers are covered by the
                                //    storage-verified drain loop in DeleteSubtreeUntilDrained.
                                return Observable.Using(
                                    () => recentlyDeleted?.BeginSubtreeDeletion(path)
                                          ?? System.Reactive.Disposables.Disposable.Empty,
                                    subtreeScope => CollectPathsForDelete(hub, path, capturedRequest.Recursive, opts.Timeout, logger)
                                    .SelectMany(collected =>
                                    {
                                        if (!capturedRequest.Recursive && collected.HasUnlistedChildren)
                                        {
                                            logger.LogDebug("[DeleteNode] has-children path={Path}", path);
                                            var msg = $"Node at '{path}' has children. Use recursive delete to remove it.";
                                            PostFailed(msg, NodeDeletionRejectionReason.HasChildren,
                                                [new LogMessage(msg, LogLevel.Error)]);
                                            return Observable.Empty<System.Reactive.Unit>();
                                        }

                                        // 3b. Bulk-atomic pre-validation (recursive only). Post
                                        //     ValidateDeleteRequest at every descendant address and
                                        //     wait for all responses; if any descendant rejects the
                                        //     delete, abort the whole operation BEFORE any storage
                                        //     side effects fire. Without this, sibling deletes that
                                        //     pass validation would race ahead via Observable.Merge
                                        //     in HierarchicalPathDeletion and physically delete
                                        //     before the failing sibling reports — leaving the
                                        //     subtree partially destroyed when the user expected an
                                        //     all-or-nothing failure.
                                        var preValidate = capturedRequest.Recursive
                                            ? PreValidateDescendantsObs(meshHub, path, collected.ToDelete, request.AccessContext, opts.Timeout, logger)
                                            : Observable.Return<(string Path, string Error, NodeDeletionRejectionReason Reason)?>(null);

                                        return preValidate.SelectMany(failure =>
                                        {
                                            if (failure is { } f)
                                            {
                                                if (f.Reason == NodeDeletionRejectionReason.Unauthorized)
                                                {
                                                    // A permission denial on a descendant is an EXPECTED
                                                    // outcome, refused atomically BEFORE any deletion —
                                                    // name the real condition at Warning, and answer with
                                                    // Unauthorized, not a generic validation failure
                                                    // mislabelled "unexpected" (issue #1128).
                                                    logger.LogWarning(
                                                        "[DeleteNode] permission-denied path={Root} deniedAt={Path} — refused before any deletion: {Err}",
                                                        path, f.Path, f.Error);
                                                    PostFailed(
                                                        f.Error,
                                                        NodeDeletionRejectionReason.Unauthorized,
                                                        [new LogMessage(f.Error, LogLevel.Error)],
                                                        collected.ToDelete.ToImmutableList());
                                                    return Observable.Empty<System.Reactive.Unit>();
                                                }

                                                logger.LogWarning(
                                                    "[DeleteNode] pre-validation failed path={Root} blockedBy={Path} err={Err}",
                                                    path, f.Path, f.Error);
                                                var msg = $"Cannot delete '{f.Path}': {f.Error}";
                                                PostFailed(
                                                    msg,
                                                    NodeDeletionRejectionReason.ValidationFailed,
                                                    [new LogMessage(msg, LogLevel.Error)],
                                                    collected.ToDelete.ToImmutableList());
                                                return Observable.Empty<System.Reactive.Unit>();
                                            }

                                        // 4. Bottom-up fan-out with storage-verified drain.
                                        //    Descendants → per-node hubs (each re-enters this
                                        //    handler with Recursive=false); root → local storage
                                        //    delete (already validated above). After each pass the
                                        //    subtree is RE-ENUMERATED from storage; anything that
                                        //    appeared mid-flight is deleted in a follow-up pass
                                        //    (bounded), and success is only reported once the
                                        //    enumeration comes back empty. The "delete wins"
                                        //    tombstones are marked per pass inside
                                        //    DeleteSubtreeUntilDrained, BEFORE any leaf hub is
                                        //    activated (see the resurrect-race note there).
                                        logger.LogDebug(
                                            "[DeleteNode] committing path={Path} count={Count}",
                                            path, collected.ToDelete.Count);

                                        // 🚨 Execute the COMMIT under the SYSTEM identity, not the
                                        // caller's. The authorization decision for the whole cascade
                                        // was taken atomically ABOVE, before any mutation: the caller's
                                        // Delete permission on the root (phase 2) plus the per-leaf
                                        // [RequiresPermission(Delete)] delivery gate that every
                                        // descendant's ValidateDeleteRequest just passed (pre-flight).
                                        // Re-evaluating the CALLER's permission per-leaf DURING the
                                        // commit was issue #1128: the plan contains the subtree's own
                                        // `_Access` grant satellites, the bottom-up fan-out deletes
                                        // them early, the caller's authorization evaporates MID-COMMIT,
                                        // and the cascade aborted half-done ("partial-deleted=31" with
                                        // no rollback) even though the caller was fully authorized when
                                        // the operation was admitted. Decide once, up front, under the
                                        // caller; execute the already-decided cascade under system.
                                        // DeletedBy still carries the caller for the audit trail, and
                                        // per-leaf validators still run at each leaf's own hub.
                                        var executionContext = new AccessContext
                                        {
                                            ObjectId = WellKnownUsers.System,
                                            Name = WellKnownUsers.System
                                        };

                                        return DeleteSubtreeUntilDrained(
                                                meshHub, storage, path, collected.ToDelete,
                                                capturedRequest, executionContext,
                                                recentlyDeleted, logger, collectedMessages)
                                            .Timeout(opts.Timeout)
                                            // 5. Post-deletion side effects for the ROOT node — e.g.
                                            //    dropping the backing partition store when a
                                            //    partition-owning Space root is deleted. The subtree
                                            //    is already gone, so a handler failure can't
                                            //    un-delete anything: it lands as a Warning on the
                                            //    activity and the response stays Ok.
                                            .SelectMany(deletedPaths =>
                                                RunPostDeletionHandlersObs(
                                                        hub, rootNode, capturedRequest.DeletedBy, logger, collectedMessages)
                                                    .Select(_ => deletedPaths))
                                            .Do(deletedPaths =>
                                            {
                                                var messages = collectedMessages.ToImmutable();
                                                var okLog = baseActivity with
                                                {
                                                    Messages = messages,
                                                    AffectedPaths = deletedPaths.ToImmutableList(),
                                                    End = DateTime.UtcNow,
                                                    Status = messages.Any(m => m.LogLevel >= LogLevel.Warning)
                                                        ? ActivityStatus.Warning
                                                        : ActivityStatus.Succeeded
                                                };

                                                logger.LogInformation(
                                                    "[DeleteNode] succeeded path={Path} count={Count} warnings={Warnings} by={DeletedBy}",
                                                    path, deletedPaths.Count,
                                                    messages.Count(m => m.LogLevel >= LogLevel.Warning),
                                                    capturedRequest.DeletedBy ?? "system");

                                                // MeshChangeEvent.Deleted is published per-path inside
                                                // FanOutDeleteSubtree's storage.DeleteAndPublish — once
                                                // per leaf, immediately after its commit. The previous
                                                // shape published from here AFTER all deletes completed,
                                                // which (a) delayed subscribers' invalidation until the
                                                // slowest leaf finished, and (b) duplicated per-leaf
                                                // publishes that already happened during descendant
                                                // re-entries through this same handler.

                                                // 🚨 ResponseFor(request) — NOT a hand-rolled
                                                // WithTarget(request.Sender)+WithProperty(RequestId).
                                                // Both set the same target + request-id correlation,
                                                // but ResponseFor ALSO auto-propagates the request's
                                                // AccessContext. This post runs deep inside the fan-out
                                                // .Do/.SelectMany continuation, on the workspace
                                                // emission scheduler where the ambient AsyncLocal
                                                // AccessContext is WIPED (the same reason the handler
                                                // captures senderUserId at entry). Without the
                                                // propagated context the fail-closed PostPipeline DROPS
                                                // this success response — the caller then never gets a
                                                // reply and its DeleteNodeRequest times out at 60s even
                                                // though the delete SUCCEEDED ("DeleteNodeResponse posted
                                                // with no AccessContext" → "[STALE-CALLBACK]
                                                // DeleteNodeRequest > 30000ms" → the delete wedges).
                                                // PostFailed already uses ResponseFor, so failure
                                                // responses were fine — only success wedged.
                                                // 🚨 RELEASE THE SUBTREE-DELETION GUARD BEFORE THE SUCCESS
                                                // RESPONSE — the response IS the "torn down" signal, so a
                                                // caller that recreates the same path the moment it lands
                                                // (delete → recreate is ordinary usage; see
                                                // WorkspaceCacheEvictionTest.NewSubscriber_AfterRecreate_
                                                // GetsFreshSnapshot) must not be refused by the guard.
                                                //
                                                // Observable.Using alone CANNOT give that ordering: Rx
                                                // disposes the resource only when the subscription is torn
                                                // down, which happens AFTER OnCompleted has propagated
                                                // downstream — so the scope outlived the response by ~5 ms
                                                // (measured), and every write in that window was refused
                                                // with "the subtree '…' is currently being deleted".
                                                //
                                                // That window was always there; it was MASKED because the
                                                // whole delete used to run inline on the per-node hub's
                                                // TURN, so the caller's follow-up create was queued behind
                                                // it and could never interleave. The moment the permission
                                                // decision hops off the fold's gate (#899,
                                                // TakeDecisionOutsideGate) the pipeline leaves the turn and
                                                // the race is real and near-certain — the accidental
                                                // serialisation is gone. Fixing the ORDERING is the root
                                                // cause; keeping the pipeline pinned to the turn would only
                                                // restore the accident.
                                                //
                                                // Everything the guard protects (plan, commit, drain,
                                                // post-deletion handlers) has completed above. The
                                                // enclosing Observable.Using stays as the safety net for
                                                // the error / timeout / unsubscribe paths, and
                                                // SubtreeDeletionScope.Dispose is Interlocked-idempotent,
                                                // so this early release plus the Using's release is one
                                                // decrement, never two.
                                                subtreeScope.Dispose();

                                                meshHub.Post(
                                                    DeleteNodeResponse.Ok() with { Log = okLog },
                                                    o => o.ResponseFor(request));
                                            })
                                            .Select(_ => System.Reactive.Unit.Default);
                                        });
                                    }));
                            });
                    });
            })
            .Subscribe(
                _ => { },
                ex =>
                {
                    var isTimeout = ex is TimeoutException;
                    var partial = ex.Data["DeletedPaths"] as IReadOnlyList<string>
                        ?? Array.Empty<string>();
                    // "Node not found" pulled from the inner DeliveryFailureException —
                    // when the subscribe call hits a non-existent owner address the
                    // sync stream surfaces "No node found at '<path>'". Normalise to
                    // a NodeNotFound rejection with a "not found" phrase callers can
                    // match (Should().WithMessage("*not found*")).
                    var isNotFound = ex.Message.IndexOf("No node found", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Map DeliveryFailureException by ErrorType — a forwarded RLS denial
                    // surfaces as DeliveryFailureException(Unauthorized) and would
                    // otherwise fall through to Unknown, hiding the access-denied
                    // signal from callers.
                    var dfxReason = ex is DeliveryFailureException dfx
                        ? dfx.Failure?.ErrorType switch
                        {
                            ErrorType.Unauthorized => (NodeDeletionRejectionReason?)NodeDeletionRejectionReason.Unauthorized,
                            ErrorType.NotFound => NodeDeletionRejectionReason.NodeNotFound,
                            _ => null,
                        }
                        : null;
                    // 🚨 Name the real condition (issue #1128): a permission denial is an
                    // EXPECTED outcome, never an "unexpected" fail-level event. When it
                    // arrives here with NOTHING deleted it is a plain Warning; when nodes
                    // were already deleted before the denial, the subtree is left torn —
                    // that partial mutation stays a LOUD error until the day it can no
                    // longer happen (the commit now runs under the system identity after
                    // up-front authorization, so this leg is a canary, not a code path).
                    var isUnauthorized = dfxReason == NodeDeletionRejectionReason.Unauthorized;
                    if (isUnauthorized && partial.Count > 0)
                        logger.LogError(ex,
                            "[DeleteNode] permission-denied MID-COMMIT path={Path} — {Partial} node(s) were "
                            + "already deleted before the denial; the subtree is left partially deleted",
                            path, partial.Count);
                    else if (isUnauthorized)
                        logger.LogWarning(
                            "[DeleteNode] permission-denied path={Path}: {Reason}", path, ex.Message);
                    else
                        logger.LogError(ex, "[DeleteNode] {Kind} path={Path} partial-deleted={Partial}",
                            isTimeout ? "timeout" : (isNotFound ? "not-found" : "unexpected"), path, partial.Count);
                    var failMsgs = collectedMessages.ToImmutable()
                        .Add(new LogMessage(
                            isNotFound ? $"Node not found at path '{path}'" : ex.Message,
                            LogLevel.Error));
                    PostFailed(
                        isTimeout
                            ? $"Delete of '{path}' exceeded {opts.Timeout.TotalSeconds:0}s timeout"
                            : (isNotFound
                                ? $"Node not found at path '{path}'"
                                : (isUnauthorized
                                    // Already legible ("Access denied: user 'x' lacks Delete
                                    // permission on 'y'") — no "Unexpected error:" prefix.
                                    ? ex.Message
                                    : $"Unexpected error: {ex.Message}")),
                        isTimeout
                            ? NodeDeletionRejectionReason.Unknown
                            : (dfxReason
                                ?? (isNotFound
                                    ? NodeDeletionRejectionReason.NodeNotFound
                                    : (ex is InvalidOperationException
                                        ? NodeDeletionRejectionReason.ValidationFailed
                                        : NodeDeletionRejectionReason.Unknown))),
                        failMsgs,
                        partial.ToImmutableList());
                });

        return request.Processed();
    }

    /// <summary>
    /// Phase 1 — enumerate the paths to delete, AUTHORITATIVELY from storage via
    /// <see cref="IStorageAdapter.ListDescendantPaths"/>. **Paths only** — no
    /// content is loaded; validators that need a live node use
    /// <c>workspace.GetMeshNodeStream(path)</c> downstream.
    ///
    /// <para>🚨 The plan must NOT come from the catalog query (<c>IMeshService.Query</c>):
    /// that index is eventually consistent — stale after writes per
    /// <c>Doc/Architecture/CqrsAndContentAccess.md</c> — and planning off it let
    /// dozens of live descendants survive a "successful" recursive delete
    /// (issue #839: 32/~90 and 68 survivors on 2026-08-05/06). Storage enumeration
    /// needs no RLS exemption either — it is infrastructure below the security
    /// layer, and the handler already gated the operation on the caller's Delete
    /// permission at the root (Phase 2); per-leaf checks fire at each descendant's
    /// own hub via the recursive DeleteNodeRequest fan-out.</para>
    ///
    /// <para>The enumeration is strict descendants (root excluded) so the
    /// bottom-up fan-out in <see cref="HierarchicalPathDeletion.DeleteSubtree"/>
    /// terminates at the root rather than re-entering through it. The root path
    /// is added to the returned set afterwards so it is deleted last (when it
    /// becomes a leaf).</para>
    ///
    /// <para><c>HasUnlistedChildren</c> counts DIRECT children only (one extra
    /// path segment) — the pre-existing non-recursive contract. Deeper satellite
    /// descendants under node-less segments (<c>{path}/_Thread/{id}</c>) never
    /// blocked a non-recursive delete and still don't.</para>
    /// </summary>
    private static IObservable<(bool RootExists, ImmutableHashSet<string> ToDelete, bool HasUnlistedChildren)>
        CollectPathsForDelete(
            IMessageHub hub,
            string path,
            bool recursive,
            TimeSpan timeout,
            ILogger logger)
    {
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var empty = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

        if (!recursive)
        {
            // Non-recursive: only delete the root if it has no DIRECT children.
            return storage.ListDescendantPaths(path)
                .Take(1)
                .Select(descendants => (
                    RootExists: true,
                    empty.Add(path),
                    descendants.Any(d => IsDirectChildOf(d, path))))
                .Timeout(timeout);
        }

        return storage.ListDescendantPaths(path)
            .Take(1)
            .Select(descendants =>
            {
                var set = empty
                    .Union(descendants.Where(p => !string.IsNullOrEmpty(p)))
                    .Add(path);
                logger.LogDebug("[DeleteNode] collected path={Path} total={Count}", path, set.Count);
                return (RootExists: true, set, false);
            })
            .Timeout(timeout);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is exactly one path segment below
    /// <paramref name="parent"/> (i.e. a direct child, not a deeper descendant).
    /// </summary>
    private static bool IsDirectChildOf(string candidate, string parent)
        => candidate.Length > parent.Length + 1
           && candidate[parent.Length] == '/'
           && candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
           && candidate.IndexOf('/', parent.Length + 1) < 0;

    /// <summary>
    /// Upper bound on enumerate → delete passes in <see cref="DeleteSubtreeUntilDrained"/>.
    /// Each pass beyond the first only runs when the post-pass storage enumeration found
    /// survivors (mid-flight creations from another process); a subtree that cannot be
    /// drained within this bound fails the operation LOUDLY instead of reporting success
    /// over live descendants.
    /// </summary>
    private const int MaxDeleteDrainPasses = 5;

    /// <summary>
    /// Runs <see cref="FanOutDeleteSubtree"/> passes until storage VERIFIES the subtree is
    /// empty. After each pass the subtree is re-enumerated authoritatively
    /// (<see cref="IStorageAdapter.ListDescendantPaths"/>); survivors — nodes that appeared
    /// mid-flight, e.g. compile-watcher <c>Release</c> satellites written from another
    /// process — are tombstoned and deleted in a follow-up pass. Success is ONLY reported
    /// once an enumeration comes back empty; exhausting <see cref="MaxDeleteDrainPasses"/>
    /// with survivors still present fails the operation (issue #839: the previous shape
    /// reported success off a point-in-time plan and never looked back at storage).
    /// </summary>
    private static IObservable<IReadOnlyList<string>> DeleteSubtreeUntilDrained(
        IMessageHub meshHub,
        IStorageAdapter storage,
        string rootPath,
        ImmutableHashSet<string> plannedPaths,
        DeleteNodeRequest baseRequest,
        AccessContext? callerAccessContext,
        RecentlyDeletedRegistry? recentlyDeleted,
        ILogger logger,
        ImmutableList<LogMessage>.Builder collectedMessages)
        => RunDeletePass(
            meshHub, storage, rootPath, plannedPaths, baseRequest, callerAccessContext,
            recentlyDeleted, logger, collectedMessages,
            pass: 1, deletedSoFar: ImmutableList<string>.Empty);

    private static IObservable<IReadOnlyList<string>> RunDeletePass(
        IMessageHub meshHub,
        IStorageAdapter storage,
        string rootPath,
        ImmutableHashSet<string> toDelete,
        DeleteNodeRequest baseRequest,
        AccessContext? callerAccessContext,
        RecentlyDeletedRegistry? recentlyDeleted,
        ILogger logger,
        ImmutableList<LogMessage>.Builder collectedMessages,
        int pass,
        ImmutableList<string> deletedSoFar)
    {
        // 🚨 "Delete wins" — tombstone every path of THIS pass BEFORE the fan-out.
        // FanOutDeleteSubtree ACTIVATES each leaf's per-node hub to process its own
        // delete, and that activation's save (the workspace sees the just-loaded node
        // as an "add") is exactly what resurrects the row. Marking here — before any
        // leaf hub is activated — guarantees the resurrecting save's guard already
        // sees the tombstone. Marking only on success (after the delete) lost a
        // check-before-mark race: the activation save checked ~14 ms before the mark
        // and slipped through.
        foreach (var dp in toDelete)
            recentlyDeleted?.MarkDeleted(dp);
        recentlyDeleted?.MarkDeleted(rootPath);

        return FanOutDeleteSubtree(
                meshHub, storage, rootPath, toDelete, baseRequest, callerAccessContext,
                logger, collectedMessages, rootAlreadyDeleted: pass > 1)
            .Catch<IReadOnlyList<string>, Exception>(ex =>
            {
                // Fold this pass's partial deletions into the accumulated total so
                // the caller's failure response reports every path actually removed.
                var passDeleted = ex.Data["DeletedPaths"] as IReadOnlyList<string>
                    ?? Array.Empty<string>();
                ex.Data["DeletedPaths"] = (IReadOnlyList<string>)deletedSoFar.AddRange(passDeleted);
                return Observable.Throw<IReadOnlyList<string>>(ex);
            })
            .SelectMany(deletedPaths =>
            {
                var acc = deletedSoFar.AddRange(deletedPaths);

                // VERIFY against storage — the plan was a point-in-time snapshot;
                // only an empty re-enumeration proves the subtree is actually gone.
                return storage.ListDescendantPaths(rootPath)
                    .Take(1)
                    .SelectMany(survivors =>
                    {
                        var survivorSet = survivors
                            .Where(p => !string.IsNullOrEmpty(p))
                            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                            .Remove(rootPath);
                        if (survivorSet.IsEmpty)
                            return Observable.Return((IReadOnlyList<string>)acc);

                        if (pass >= MaxDeleteDrainPasses)
                        {
                            var ex = new InvalidOperationException(
                                $"Recursive delete of '{rootPath}' could not drain the subtree after "
                                + $"{pass} pass(es): {survivorSet.Count} descendant(s) still present "
                                + $"(e.g. '{survivorSet.First()}'). A writer keeps re-creating nodes "
                                + "under the subtree faster than they can be deleted.");
                            ex.Data["DeletedPaths"] = (IReadOnlyList<string>)acc;
                            return Observable.Throw<IReadOnlyList<string>>(ex);
                        }

                        logger.LogInformation(
                            "[DeleteNode] drain pass {Pass} path={Path} survivors={Count} — "
                            + "nodes appeared mid-flight; deleting them too",
                            pass + 1, rootPath, survivorSet.Count);

                        return RunDeletePass(
                            meshHub, storage, rootPath, survivorSet, baseRequest,
                            callerAccessContext, recentlyDeleted, logger, collectedMessages,
                            pass + 1, acc);
                    });
            });
    }

    /// <summary>
    /// Bottom-up traversal of the path set via <see cref="HierarchicalPathDeletion"/>.
    /// <para>
    /// <b>Root path:</b> deleted via local <see cref="IStorageAdapter.Delete"/>
    /// — already validated by the calling handler before fan-out. This avoids
    /// self-recursion that would arise if we posted <see cref="DeleteNodeRequest"/>
    /// at our own address (the same handler would re-enter). On drain passes
    /// (<paramref name="rootAlreadyDeleted"/>) the root row is usually gone —
    /// <see cref="IStorageAdapter.DeleteIfExists"/> makes the re-delete idempotent
    /// while still removing (and publishing) a root that was re-created mid-flight.
    /// </para>
    /// <para>
    /// <b>Descendant paths:</b> posted as non-recursive <see cref="DeleteNodeRequest"/>
    /// at each leaf's own per-node hub. Each leaf runs its own validation,
    /// permission check, and storage delete via the same handler (Recursive=false
    /// branch).
    /// </para>
    /// <para>
    /// Collected per-leaf activity messages are accumulated into
    /// <paramref name="collectedMessages"/> for the top-level activity log.
    /// </para>
    /// </summary>
    private static IObservable<IReadOnlyList<string>> FanOutDeleteSubtree(
        IMessageHub meshHub,
        IStorageAdapter storage,
        string rootPath,
        ImmutableHashSet<string> descendantPaths,
        DeleteNodeRequest baseRequest,
        AccessContext? callerAccessContext,
        ILogger logger,
        ImmutableList<LogMessage>.Builder collectedMessages,
        bool rootAlreadyDeleted = false)
    {
        return HierarchicalPathDeletion.DeleteSubtree(
            rootPath,
            descendantPaths.Remove(rootPath),
            path =>
            {
                if (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Root: delete locally via storage — already validated by the
                    // calling handler. Avoids re-entering this same handler via
                    // hub.Observe (which would cause an infinite request loop).
                    //
                    // Commit-then-publish: DeleteAndPublish chains the
                    // MeshChangeEvent.Deleted publish into the storage observable
                    // so it fires only AFTER storage.Delete emits (post-commit).
                    // Descendant deletes re-enter this same handler and hit this
                    // branch for THEIR own path, so each leaf publishes once.
                    logger.LogDebug("[DeleteNode] storage.Delete (root) {Path}", path);
                    var changeFeed = meshHub.ServiceProvider.GetService<IMeshChangeFeed>();

                    if (rootAlreadyDeleted)
                        // Drain pass: the root row was deleted in pass 1. DeleteIfExists
                        // is the idempotent variant — a no-op when the row is gone, a
                        // real delete + publish when something re-created it mid-flight.
                        return storage.DeleteIfExists(path)
                            .Do(removed =>
                            {
                                if (removed)
                                    changeFeed?.Publish(MeshChangeEvent.Deleted(path));
                            })
                            .Select(_ => path);

                    return storage.DeleteAndPublish(path, changeFeed)
                        .Do(_ =>
                        {
                            // Storage adapter's Changes feed fires the Deleted
                            // event from inside storage.Delete — no extra notify here.
                            // 🚨 Invalidate the process-wide MeshNodeStreamCache so
                            // subsequent reads of this path don't see the pre-delete
                            // value held in the Replay(1) entry.
                            meshHub.ServiceProvider.GetService<IMeshNodeStreamCache>()?
                                .Invalidate(path);
                            // 🚨 Dispose the per-node hub at this path if one was
                            // activated — the cache invalidate clears the
                            // process-wide cache entry, but the hub itself retains
                            // its own MeshNodeReference reducer state and would
                            // re-emit the cached pre-delete value to the next
                            // subscriber. Disposing forces routing to re-activate
                            // a fresh hub on the next request, which reads from
                            // (now-empty) storage and emits null.
                            // Symptom this addresses: CreateNode_IdChanged saw the
                            // transient after delete because the per-node hub for
                            // the transient path still held the cached node in its
                            // own data-source stream.
                            try
                            {
                                var hostedHub = meshHub.GetHostedHub(new Address(path),
                                    c => c, HostedHubCreation.Never);
                                hostedHub?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex,
                                    "[DeleteNode] best-effort hub disposal failed for {Path}", path);
                            }
                        });
                }

                // Descendant: fan-out via per-node hub. The leaf hub re-enters
                // this same handler with Recursive=false → validates + deletes itself.
                // Stamp the caller's AccessContext explicitly — this Observe fires
                // from a SelectMany continuation on the workspace's emission
                // scheduler where AsyncLocal is unreliable; without an explicit
                // stamp, the owner's [RequiresPermission(Delete)] denies on
                // whatever hub-self identity is ambient (`sync/<id>`).
                logger.LogDebug("[DeleteNode] post leaf delete {Path}", path);
                return meshHub.Observe(
                        // CascadeRootPath rides to the leaf's handler so per-leaf validators
                        // exempt space-teardown invariants (last-admin) exactly like the
                        // pre-flight ValidateDeleteRequest(p, rootPath) already does.
                        baseRequest with { Path = path, Recursive = false, CascadeRootPath = rootPath },
                        o => callerAccessContext is null
                            ? o.WithTarget(new Address(path))
                            : o.WithTarget(new Address(path)).WithAccessContext(callerAccessContext))
                    .Take(1)
                    .SelectMany(delivery =>
                    {
                        if (delivery.Message is DeleteNodeResponse resp && resp.Success)
                        {
                            if (resp.Log?.Messages is { Count: > 0 } msgs)
                                lock (collectedMessages) collectedMessages.AddRange(msgs);
                            return Observable.Return(path);
                        }
                        var failResp = delivery.Message as DeleteNodeResponse;
                        var reason = failResp?.Error ?? "Unknown error";
                        return Observable.Throw<string>(new InvalidOperationException(
                            $"Delete failed for '{path}': {reason}"));
                    });
            });
    }

    /// <summary>
    /// Bulk-atomic pre-flight: post <see cref="ValidateDeleteRequest"/> at every
    /// descendant address (root excluded — already validated by the caller) and
    /// return the FIRST failure as <c>(Path, Error, Reason)</c>, or <c>null</c>
    /// if all descendants pass. Subscribed before any storage side effects fire,
    /// so a single failing descendant aborts the whole subtree delete with no
    /// partial state — sibling deletes that pass validation never run.
    ///
    /// <para>🚨 This pre-flight is ALSO the per-descendant PERMISSION check
    /// (issue #1128): <see cref="ValidateDeleteRequest"/> carries the same
    /// <c>[RequiresPermission(Delete)]</c> delivery gate as the leaf
    /// <see cref="DeleteNodeRequest"/> fan-out, evaluated at each leaf's own hub
    /// under the caller's <see cref="AccessContext"/>. An Unauthorized refusal
    /// here is the atomic up-front denial — reported as
    /// <see cref="NodeDeletionRejectionReason.Unauthorized"/> so callers see a
    /// legible permission denial, never a partially deleted subtree. The commit
    /// that follows a fully-granted pre-flight then runs under the system
    /// identity, immune to the cascade deleting its own <c>_Access</c> grants.</para>
    /// </summary>
    private static IObservable<(string Path, string Error, NodeDeletionRejectionReason Reason)?> PreValidateDescendantsObs(
        IMessageHub meshHub,
        string rootPath,
        ImmutableHashSet<string> allPaths,
        AccessContext? callerAccessContext,
        TimeSpan timeout,
        ILogger logger)
    {
        var descendants = allPaths
            .Where(p => !string.Equals(p, rootPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (descendants.Length == 0)
            return Observable.Return<(string, string, NodeDeletionRejectionReason)?>(null);

        var perPath = descendants.Select(p => meshHub
            // 🚨 Stamp the caller's AccessContext on every ValidateDeleteRequest.
            // This post fires from a SelectMany continuation on the workspace's
            // emission scheduler where AsyncLocal AccessContext is unreliable —
            // without an explicit stamp, the PostPipeline falls back to whatever
            // hub-self impersonation is ambient (e.g. `sync/<streamId>`) and the
            // owner's [RequiresPermission(Delete)] gate denies. The original
            // request's AccessContext carries the caller's full identity + roles,
            // captured at handler entry where AsyncLocal was correct.
            .Observe(new ValidateDeleteRequest(p, rootPath), o => callerAccessContext is null
                ? o.WithTarget(new Address(p))
                : o.WithTarget(new Address(p)).WithAccessContext(callerAccessContext))
            .Take(1)
            .Select(d =>
            {
                var resp = d.Message as ValidateDeleteResponse;
                if (resp is null || resp.IsValid)
                    return ((string, string, NodeDeletionRejectionReason)?)null;
                return (p, resp.Errors[0], NodeDeletionRejectionReason.ValidationFailed);
            })
            .Catch<(string, string, NodeDeletionRejectionReason)?, Exception>(ex =>
            {
                // The [RequiresPermission(Delete)] gate on ValidateDeleteRequest refused
                // this leaf for the CALLER — the atomic up-front permission denial. It is
                // an expected outcome, decided before any deletion; classify it so the
                // caller gets Unauthorized with the gate's legible message ("Access
                // denied: user 'x' lacks Delete permission on 'y'"), not a fail-level
                // "unexpected" report (issue #1128).
                if (ex is DeliveryFailureException { Failure.ErrorType: ErrorType.Unauthorized })
                {
                    logger.LogDebug(
                        "[DeleteNode] pre-flight permission denied {Path}: {Message}", p, ex.Message);
                    return Observable.Return<(string, string, NodeDeletionRejectionReason)?>(
                        (p, ex.Message, NodeDeletionRejectionReason.Unauthorized));
                }
                logger.LogWarning(ex,
                    "[DeleteNode] pre-validate descendant failed {Path}", p);
                return Observable.Return<(string, string, NodeDeletionRejectionReason)?>(
                    (p, ex.Message, NodeDeletionRejectionReason.ValidationFailed));
            }));

        // Collect every descendant's outcome; emit the first non-null failure
        // (or null when all pass). Merge — not Concat — so independent
        // per-leaf hubs validate in parallel; the failure with the lowest
        // emission order wins via FirstOrDefault.
        return Observable.Merge(perPath)
            .Where(r => r.HasValue)
            .Take(1)
            .DefaultIfEmpty(null)
            .Timeout(timeout);
    }

    /// <summary>
    /// Check <see cref="Permission.Delete"/> for a single node's primary path.
    /// Returns <c>true</c> if delete is denied.
    /// </summary>
    private static IObservable<bool> CheckDeletePermissionForNode(
        IMessageHub hub,
        string userId,
        MeshNode node,
        ILogger logger)
    {
        var pathToCheck = node.MainNode ?? node.Path;

        // 🚨 TakeDecisionOutsideGate, NOT a bare Take(1) — issue #899.
        //
        // Take(1) is still required (GetEffectivePermissions rides the live AccessAssignment
        // synced query and is hot, so without it the chain never completes and the handler
        // hangs), but it is NOT sufficient. HandleDeleteNodeRequest chains
        // `.SelectMany(denied => <the entire delete pipeline>)` onto this, and on a warm
        // permission cache the fold emits synchronously during Subscribe while holding its
        // CombineLatest gate — so the validator run, the storage delete, the cache
        // invalidation and the change-feed publish ALL ran inside that lock. Two per-node
        // hubs deleting concurrently (a recursive space delete fans one DeleteNodeRequest per
        // leaf) then acquired {own fold gate, shared synced-query gate} in opposite orders and
        // deadlocked: both action blocks parked forever and the delete posted neither a
        // DeleteNodeResponse nor a failure.
        //
        // The decision is unchanged — still taken inside the fold; only the pipeline that
        // follows is moved off the gate.
        return hub.GetEffectivePermissions(pathToCheck, userId)
            .TakeDecisionOutsideGate()
            .Select(perms =>
            {
                var denied = !perms.HasFlag(Permission.Delete);
                if (denied)
                    logger.LogDebug(
                        "[DeleteNode] permission-denied for {User} on {Path} (effective={Perms})",
                        userId, node.Path, perms);
                return denied;
            });
    }

    /// <summary>
    /// Default handler for <see cref="ValidateDeleteRequest"/>. Fetches the target node
    /// (via <see cref="IStorageAdapter"/>), runs the hub's registered
    /// <see cref="INodeValidator"/> chain for <see cref="NodeOperation.Delete"/>, and
    /// returns the first validator failure as an Error (empty Warnings in the default
    /// implementation — custom hubs can override this handler to emit Warnings).
    /// </summary>
    private static IMessageDelivery HandleValidateDeleteRequest(
        IMessageHub hub,
        IMessageDelivery<ValidateDeleteRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshNode>>();
        var persistence = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var opts = hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions();
        var path = request.Message.Path;

        var existingNodeObs = persistence.Read(path, hub.JsonSerializerOptions);

        // Running validators against a fabricated DeleteNodeRequest keeps
        // RunDeletionValidatorsObs unchanged — every validator sees the same inputs it
        // would see during the real delete.
        var proxyDeleteRequest = new DeleteNodeRequest(path);

        existingNodeObs
            .Timeout(opts.Timeout)
            .SelectMany(node =>
            {
                if (node == null)
                    return Observable.Return(
                        ValidateDeleteResponse.FromError($"Node not found at path: {path}"));

                return RunDeletionValidatorsObs(hub, node, proxyDeleteRequest, request.Message.RootPath)
                    .Select(err => err is null
                        ? ValidateDeleteResponse.Ok()
                        : ValidateDeleteResponse.FromError(err.Value.ErrorMessage ?? "Validation failed"));
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "[ValidateDelete] {Path} failed — treating as error", path);
                return Observable.Return(
                    ValidateDeleteResponse.FromError($"Validation error: {ex.Message}"));
            })
            .Subscribe(response =>
            {
                hub.Post(response, o => o.ResponseFor(request));
            });

        return request.Processed();
    }

    /// <summary>
    /// Emits the rejection reason when the node is a privileged grant on a SYSTEM-OWNED (GitSynced)
    /// partition, else <c>null</c>. See <see cref="AccessAssignmentGuard.IsForbiddenOnSystemOwned"/>
    /// for why that shape is refused.
    ///
    /// <para><b>The storage read is paid for only by the shape that can fail.</b> The pure checks
    /// run first — node type, grant path, and whether the assignment confers write at all — so the
    /// hot path (an entitlement's <c>Viewer</c> grant, written on every enrollment) never touches
    /// persistence. Only an Admin/Editor grant costs the one <c>{partition}/_GitSync</c> read, which
    /// is the same probe <c>EnsurePartitionBootstrap</c> already performs on this path.</para>
    ///
    /// <para>Content is materialised through the TYPED accessor, never a raw <c>JsonElement</c>
    /// test: a grant arrives typed on the hub that owns it, as <c>JsonObject</c> from the node
    /// builders, and as <c>JsonElement</c> over the wire — and a shape test that misses would read
    /// "no roles", i.e. silently allow exactly what this guard exists to refuse.</para>
    /// </summary>
    private static IObservable<string?> SystemOwnedGrantRejection(IMessageHub hub, MeshNode node)
    {
        if (!string.Equals(node.NodeType, AccessAssignmentGuard.AccessAssignmentNodeType,
                StringComparison.OrdinalIgnoreCase))
            return Observable.Return<string?>(null);

        var scope = AccessAssignmentGuard.ScopeFromPath(node.Path);
        if (string.IsNullOrEmpty(scope))
            return Observable.Return<string?>(null);

        var assignment = node.ContentAs<AccessAssignment>(hub.JsonSerializerOptions);
        if (!AccessAssignmentGuard.ConfersWriteAccess(assignment))
            return Observable.Return<string?>(null);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return<string?>(null);

        var partition = AccessAssignmentGuard.PartitionOf(scope);
        return ReadNodeAuthoritative(hub, persistence, $"{partition}/_GitSync")
            .Select(sync => AccessAssignmentGuard.IsForbiddenOnSystemOwned(
                node, assignment, systemOwned: sync is not null, out var reason)
                ? reason
                : null);
    }

    /// <summary>
    /// Sync-friendly observable variant of the creation-validator runner. Iterates
    /// validators sequentially via <c>Concat</c> (preserves short-circuit semantics —
    /// stops at the first failure), emits the first failure as a tuple or <c>null</c>
    /// if all pass. Consumers compose via <c>SelectMany</c>; no <c>await</c>.
    /// </summary>
    private static IObservable<(string? ErrorMessage, NodeCreationRejectionReason Reason)?> RunCreationValidatorsObs(
        IMessageHub hub,
        MeshNode node,
        CreateNodeRequest request)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Create))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, NodeCreationRejectionReason)?>(null);

        return validators
            .Select(v => v.Validate(context))
            .Concat()
            .Where(result => !result.IsValid)
            .Select(result =>
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeAlreadyExists => NodeCreationRejectionReason.NodeAlreadyExists,
                    NodeRejectionReason.InvalidNodeType => NodeCreationRejectionReason.InvalidNodeType,
                    NodeRejectionReason.InvalidPath => NodeCreationRejectionReason.InvalidPath,
                    NodeRejectionReason.Unauthorized => NodeCreationRejectionReason.ValidationFailed,
                    _ => NodeCreationRejectionReason.ValidationFailed
                };
                return ((string?, NodeCreationRejectionReason)?)(result.ErrorMessage, reason);
            })
            .Take(1)
            .DefaultIfEmpty(null);
    }

    /// <summary>
    /// Sync-friendly observable variant of the post-creation handler runner. Returns
    /// an observable that emits no values and completes once all handlers have run.
    /// Failures from individual handlers are logged but never break the chain — they
    /// surface as <c>OnNext(false)</c> elements that the caller can ignore. Additional
    /// nodes from each handler are persisted via <c>IStorageAdapter</c> wrapped in
    /// <c>Observable.FromAsync</c>; no <c>await</c> in handler code itself.
    /// </summary>
    private static IObservable<System.Reactive.Unit> RunPostCreationHandlersObs(
        IMessageHub hub,
        MeshNode node,
        string? createdBy,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(node.NodeType))
            return Observable.Empty<System.Reactive.Unit>();

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var handlers = hub.ServiceProvider.GetServices<INodePostCreationHandler>()
            .Where(h => h.NodeType.Equals(node.NodeType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (handlers.Count == 0)
            return Observable.Empty<System.Reactive.Unit>();

        // For each matching handler: invoke Handle, then persist any additional nodes it returns.
        // Sequentially via Concat to preserve the original order's side-effect dependencies.
        // Handle's error is propagated ONLY for handlers that declare FailsCreateOnError (a
        // required-side-effect handler — e.g. the Space creator-Admin grant); the create handler's
        // Subscribe turns that into a CreateNodeResponse.Fail. Best-effort handlers (onboarding
        // seeds) keep log-and-continue. NEVER blanket-swallow a critical grant into a silent Ok —
        // that shipped ownerless, un-navigable Spaces (AGENTS.md: no .Catch(Observable.Empty)).
        return handlers
            .Select(handler =>
            {
                var rawHandle = handler.Handle(node, createdBy);
                var handleObs = handler.FailsCreateOnError
                    ? rawHandle.Do(_ => { }, ex => logger.LogError(ex,
                        "Critical post-creation handler {Handler} failed for node {Path} — failing the create",
                        handler.GetType().Name, node.Path))
                    : rawHandle.Catch<System.Reactive.Unit, Exception>(ex =>
                    {
                        logger.LogWarning(ex,
                            "Post-creation handler {Handler} failed for node {Path}",
                            handler.GetType().Name, node.Path);
                        return Observable.Return(System.Reactive.Unit.Default);
                    });

                IEnumerable<MeshNode> additional;
                try
                {
                    additional = handler.GetAdditionalNodes(node) ?? Array.Empty<MeshNode>();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Post-creation handler {Handler}.GetAdditionalNodes threw for node {Path}",
                        handler.GetType().Name, node.Path);
                    additional = Array.Empty<MeshNode>();
                }

                if (persistence == null || !additional.Any())
                    return handleObs;

                var saveExtras = additional
                    .Select(extra => persistence.Write(extra with { State = MeshNodeState.Active }, hub.JsonSerializerOptions)
                        .Where(saved => saved is not null)
                        .Select(saved => saved!)
                        .Do(saved =>
                        {
                            hub.Post(DataChangeRequest.Update([saved]),
                                o => o.WithTarget(new Address(saved.Path)));
                            logger.LogInformation(
                                "Post-creation handler created additional node at {Path}", saved.Path);
                        })
                        .Catch<MeshNode, Exception>(ex =>
                        {
                            logger.LogWarning(ex,
                                "Failed to persist post-creation additional node from {Handler} for {Path}",
                                handler.GetType().Name, node.Path);
                            return Observable.Empty<MeshNode>();
                        })
                        .Select(_ => System.Reactive.Unit.Default))
                    .Concat();

                return handleObs.Concat(saveExtras);
            })
            .Concat();
    }

    /// <summary>
    /// Runs the registered <see cref="INodePostDeletionHandler"/>s matching the deleted
    /// ROOT node's type, sequentially (<c>Concat</c>), after the subtree has been removed
    /// from persistence. A handler failure is logged and appended to
    /// <paramref name="collectedMessages"/> as a Warning — the nodes are already gone, so
    /// the delete response stays Ok (with Warning status) rather than reporting a failure
    /// for a deletion that DID happen. Emits exactly once (also with zero handlers) so the
    /// delete chain's <c>SelectMany</c> always proceeds to post the response.
    /// </summary>
    private static IObservable<System.Reactive.Unit> RunPostDeletionHandlersObs(
        IMessageHub hub,
        MeshNode node,
        string? deletedBy,
        ILogger logger,
        ImmutableList<LogMessage>.Builder collectedMessages)
    {
        if (string.IsNullOrEmpty(node.NodeType))
            return Observable.Return(System.Reactive.Unit.Default);

        var handlers = hub.ServiceProvider.GetServices<INodePostDeletionHandler>()
            .Where(h => h.NodeType.Equals(node.NodeType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (handlers.Count == 0)
            return Observable.Return(System.Reactive.Unit.Default);

        return handlers
            .Select(handler => handler.Handle(node, deletedBy)
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    logger.LogError(ex,
                        "Post-deletion handler {Handler} failed for node {Path}",
                        handler.GetType().Name, node.Path);
                    lock (collectedMessages)
                        collectedMessages.Add(new LogMessage(
                            $"Post-deletion cleanup ({handler.GetType().Name}) failed for '{node.Path}': {ex.Message}",
                            LogLevel.Warning));
                    return Observable.Return(System.Reactive.Unit.Default);
                }))
            .Concat()
            .ToList()
            .Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// Walks up <see cref="MessageHubConfiguration.ParentHub"/> to the topmost hub —
    /// the mesh hub, which is never torn down by its own operations and is therefore
    /// the stable place to post terminal delete replies + DisposeRequests from.
    /// Public so callers (e.g. activity tracking) can resolve the mesh hub from
    /// any child hub's scope when they need to target node-CRUD handlers that
    /// live only on the root.
    /// </summary>
    public static IMessageHub GetMeshHub(this IMessageHub hub)
    {
        var current = hub;
        while (current.Configuration.ParentHub is { } parent && !ReferenceEquals(parent, current))
            current = parent;
        return current;
    }

    private static IMessageHub ResolveMeshHub(IMessageHub hub) => hub.GetMeshHub();

    /// <summary>
    /// Sync-friendly observable variant of the deletion-validator runner. Iterates
    /// validators sequentially via <c>Concat</c> (preserves short-circuit semantics —
    /// stops at the first failure); emits the first failure as a tuple or <c>null</c>
    /// if all pass. No <c>await</c>.
    /// </summary>
    private static IObservable<(string? ErrorMessage, NodeDeletionRejectionReason Reason)?> RunDeletionValidatorsObs(
        IMessageHub hub,
        MeshNode node,
        DeleteNodeRequest request,
        string? cascadeRootPath = null)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext,
            DeleteCascadeRootPath = cascadeRootPath ?? request.Path
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Delete))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, NodeDeletionRejectionReason)?>(null);

        return validators
            .Select(v => v.Validate(context))
            .Concat()
            .Where(result => !result.IsValid)
            .Select(result =>
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeDeletionRejectionReason.NodeNotFound,
                    NodeRejectionReason.HasChildren => NodeDeletionRejectionReason.HasChildren,
                    NodeRejectionReason.Unauthorized => NodeDeletionRejectionReason.ValidationFailed,
                    _ => NodeDeletionRejectionReason.ValidationFailed
                };
                return ((string?, NodeDeletionRejectionReason)?)(result.ErrorMessage, reason);
            })
            .Take(1)
            .DefaultIfEmpty(null);
    }

    /// <summary>
    /// Delete-specific validator runner that collects BOTH errors (first-only, short-circuit)
    /// AND warnings (all, aggregated). Returns one tuple per node: (firstError or null, all
    /// warnings emitted by validators that accepted the delete).
    /// </summary>
    private static IObservable<(string? Error, ImmutableList<string> Warnings)>
        RunDeletionValidatorsWithWarningsObs(
            IMessageHub hub,
            MeshNode node,
            DeleteNodeRequest request,
            AccessContext? deliveryAccessContext = null)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = node,
            Request = request,
            // 🚨 The DELIVERY's AccessContext first — the ambient AsyncLocal does not
            // survive the scheduler hops between handler entry and this call (the same
            // reason the handler captures senderUserId at entry), and for a cascade-leg
            // delete the delivery carries the explicit SYSTEM execution stamp that
            // RlsNodeValidator's cascade bypass keys on (issue #1128). Falling back to
            // the ambient context preserves the pre-existing behavior for callers that
            // did not thread the delivery through.
            AccessContext = deliveryAccessContext ?? accessService?.Context ?? accessService?.CircuitContext,
            // This runner validates the ROOT node of the delete. For a standalone delete the
            // cascade root is the request path itself; a leaf delete issued by the subtree
            // fan-out carries the ORIGINAL root so validators exempt space-teardown invariants
            // (see DeleteNodeRequest.CascadeRootPath).
            DeleteCascadeRootPath = request.CascadeRootPath ?? request.Path
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Delete))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, ImmutableList<string>)>((null, ImmutableList<string>.Empty));

        return validators
            .Select(v => v.Validate(context))
            .Concat()
            .ToList()
            .Select(results =>
            {
                var firstError = results.FirstOrDefault(r => !r.IsValid);
                var warnings = results
                    .Where(r => r.IsValid && !string.IsNullOrEmpty(r.Warning))
                    .Select(r => r.Warning!)
                    .ToImmutableList();
                return ((string?)firstError?.ErrorMessage, warnings);
            });
    }

    /// <summary>
    /// Hard deadline for any forward-and-await-response pattern in node operation handlers.
    /// Proper error propagation should bring a real response back well before this fires —
    /// the safety catch only runs if the framework lost the response somewhere. When it
    /// trips it logs an ERROR with enough context to find and fix the propagation bug.
    /// </summary>
    private static readonly TimeSpan NodeOpForwardTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Single-verb upsert handler for <see cref="CreateOrUpdateNodeRequest"/>.
    /// Two strict paths, both honoring "the per-node hub is the sole owner of
    /// its state — direct writes to persistence are illegal":
    ///
    /// <list type="number">
    /// <item><b>Missing target</b> → forward as <see cref="CreateNodeRequest"/>.
    /// The per-node hub spins up and persists its own initial state.</item>
    /// <item><b>Existing target</b> → call
    /// <c>workspace.GetMeshNodeStream(path).Update(state =&gt; UpdateAccordingToSourceNode(state, sourceNode, options))</c>.
    /// The Update routes to the owning per-node hub via the data-sync
    /// protocol; the hub applies the change to its own MeshNode through its
    /// own workspace's <c>MeshNodeReference</c> reducer; <c>MeshNodeTypeSource</c>
    /// debounces and persists. NEVER direct <c>persistence.Write</c> — that
    /// bypasses the sole-owner rule.</item>
    /// </list>
    ///
    /// <para>Reads-from-persistence are allowed (existence check is a
    /// routing-layer discovery) — only writes go through the per-node hub.
    /// All flow is reactive; no <c>await</c>, no <c>Task.FromAsync</c>.</para>
    /// </summary>
    private static IMessageDelivery HandleCreateOrUpdateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CreateOrUpdateNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MeshWeaver.Mesh.Services.IMeshCatalog");
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var startedAt = DateTime.UtcNow;
        var inboundRequest = request.Message;
        var node = inboundRequest.Node;

        var requestedBy = inboundRequest.RequestedBy
            ?? request.AccessContext?.ObjectId;
        if (!string.IsNullOrEmpty(requestedBy)
            && string.IsNullOrEmpty(inboundRequest.RequestedBy))
            inboundRequest = inboundRequest with { RequestedBy = requestedBy };

        var baseActivity = new ActivityLog("NodeUpsert")
        {
            HubPath = node.Path,
            AffectedPaths = ImmutableList<string>.Empty.Add(node.Path),
            Start = startedAt,
            User = !string.IsNullOrEmpty(requestedBy)
                ? new UserInfo(requestedBy, requestedBy)
                : null,
        };

        if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Path))
        {
            PostFail("Node path and Id must not be empty", NodeUpsertRejectionReason.InvalidPath);
            return request.Processed();
        }

        // Same STRUCTURAL invariant as CreateNode: an AccessAssignment's MainNode must name the
        // partition/scope its path sits under. Guarding only the create path would leave the hole
        // open — an upsert could set MainNode back to empty afterwards, which silently converts a
        // partition grant into a ROOT grant (All on every partition). Both write paths, or neither.
        //
        // 🚨 And — exactly as on the create path — the guard must see the NORMALISED node: MainNode
        // defaults to Path, so an un-normalised satellite reads as "scoped to itself" and trips the
        // mismatch branch. Normalising here also fixes a real asymmetry: upsert never derived a
        // satellite's MainNode, so an upserted satellite pointed at itself instead of its owner.
        var upsertMeshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();
        if (upsertMeshConfig != null)
            node = NormalizeSatelliteMainNode(node, upsertMeshConfig);

        if (AccessAssignmentGuard.IsScopeInvalid(node, out var upsertScopeReason))
        {
            logger.LogError("[UpsertNode] REFUSED mis-scoped AccessAssignment {Path}: {Reason}", node.Path, upsertScopeReason);
            PostFail(upsertScopeReason, NodeUpsertRejectionReason.InvalidPath);
            return request.Processed();
        }

        if (inboundRequest.Patch is not null)
        {
            PostFail("Patch-mode upserts are not yet supported.",
                NodeUpsertRejectionReason.PatchFailed);
            return request.Processed();
        }

        var existingObs = persistence != null
            ? persistence.Read(node.Path, hub.JsonSerializerOptions)
            : Observable.Return<MeshNode?>(null);

        var inboundCtx = request.AccessContext;

        // Same rule as the create path: a SYSTEM-OWNED space grants nobody write access. Guarding
        // only the create would leave the hole open through the UPDATE branch below — an existing
        // Viewer grant could simply be upserted up to Admin, which is the identical ownership claim
        // with a version bump instead of a create. Both write paths, or neither.
        var gatedExisting = SystemOwnedGrantRejection(hub, node)
            .SelectMany(grantRejection =>
            {
                if (grantRejection is null)
                    return existingObs;
                logger.LogError("[UpsertNode] REFUSED privileged grant on system-owned partition {Path}: {Reason}",
                    node.Path, grantRejection);
                PostFail(grantRejection, NodeUpsertRejectionReason.ValidationFailed);
                return Observable.Empty<MeshNode?>();
            });

        gatedExisting.Subscribe(
            existing =>
            {
                if (existing is null)
                {
                    DispatchInnerCreate();
                    return;
                }
                if (IsNoOpUpsert(existing, node, hub.JsonSerializerOptions))
                {
                    SkipNoOpIfAuthorized(existing);
                    return;
                }
                ApplyUpdateViaStream(existing);
            },
            ex =>
            {
                logger.LogWarning(ex,
                    "[CreateOrUpdate] persistence read failed for {Path}", node.Path);
                PostFail($"Persistence read failed: {ex.Message}",
                    NodeUpsertRejectionReason.Unknown);
            });

        return request.Processed();

        void DispatchInnerCreate()
        {
            var inner = new CreateNodeRequest(node) { CreatedBy = requestedBy };
            // 🚨 PRE-REGISTERING Observe(request, options) — never Post-then-Observe(delivery) (#981).
            //
            // This runs OFF the hub action block: `existingObs` is `persistence.Read(...)`, which for
            // any adapter that is not a synchronous in-memory hit (partition-routed / embedded-resource
            // / pooled backends) emits on an I/O thread. So `hub.Post` here is NOT reentrant into the
            // turn loop — `KickDrain` finds `draining == false` and schedules the turn onto
            // `turnScheduler` STRAIGHT AWAY, on another thread, while this thread walks on to the next
            // statement. `PostImplGeneric` also runs `ScheduleNotify` synchronously, so POSTED /
            // RECEIVED / ENQUEUED are already behind us by the time `Post` returns.
            //
            // With the old `Post(...)` + `Observe(forwarded)` pair, everything between those two
            // statements was unprotected: preempt this thread (a saturated CI runner does exactly
            // that) and the turn can run the whole create and post its `CreateNodeResponse` first.
            // `HandleCallbacks` finds no subject for the correlation, treats the response as consumed
            // and DROPS it — then this line registers a subject that nothing will ever answer. The
            // upsert never replies, and the caller's callback sits pending until the teardown
            // quiescing budget reports it as a leaked `CreateNodeRequest@mesh/<self>`.
            //
            // It also destroyed the evidence: `RequestFateLedger` starts a trail at REGISTRATION, so
            // the post-hoc overload lost POSTED/RECEIVED/ENQUEUED/ROUTED and left a trail reading
            // "AWAITING → REGISTERED_AFTER_POST" and nothing else — which is precisely the capture
            // that could not name this mechanism.
            //
            // `Observe(request, options)` registers the AsyncSubject BEFORE posting, so the response
            // is buffered no matter how early it lands, and the trail records every stage.
            //
            // Identity is unaffected by the swap. The overload re-seeds emissions from the AMBIENT
            // context, which is exactly what is unreliable on this thread (MeshService.CreateNode's
            // note) — but nothing here leans on it: `inner.CreatedBy` pins the identity as a request
            // FIELD, the post stamps `inboundCtx` explicitly, `ApplyUpdateViaStream` opens its own
            // `SwitchAccessContext(inboundCtx)`, and PostOk/PostFail are `ResponseFor` posts.
            hub.Observe(inner, o =>
                {
                    var withTarget = o.WithTarget(hub.Address);
                    return inboundCtx is not null ? withTarget.WithAccessContext(inboundCtx) : withTarget;
                })
                .Subscribe(
                    d =>
                    {
                        if (d.Message is CreateNodeResponse cr && cr.Success && cr.Node is not null)
                            PostOk(cr.Node, isCreate: true, $"Created node at '{node.Path}'");
                        // Upsert semantics must be RACE-FREE: the read-then-branch above is a
                        // TOCTOU — the node can materialise between the persistence read (null)
                        // and the inner create's own existence check (e.g. an earlier write of
                        // the same path flushing through the DEBOUNCED per-node persist, the
                        // partition bootstrap's root heal, or any concurrent creator). A verb
                        // named create-OR-update must never fail "already exists" — losing the
                        // create race simply means the node exists NOW, so fall through to the
                        // update path (same treat-as-success rule EnsurePartitionBootstrap
                        // documents for its own root-create race).
                        else if ((d.Message as CreateNodeResponse)?.RejectionReason
                                 == NodeCreationRejectionReason.NodeAlreadyExists)
                        {
                            logger.LogDebug(
                                "[CreateOrUpdate] lost the create race for {Path}; applying as update",
                                node.Path);
                            ApplyUpdateViaStream(node);
                        }
                        else
                            PostFail(
                                (d.Message as CreateNodeResponse)?.Error ?? "Inner CreateNode returned no response",
                                MapCreateRejection((d.Message as CreateNodeResponse)?.RejectionReason));
                    },
                    ex =>
                    {
                        logger.LogWarning(ex,
                            "[CreateOrUpdate] inner CreateNode faulted for {Path}", node.Path);
                        PostFail($"Inner CreateNode faulted: {ex.Message}",
                            NodeUpsertRejectionReason.Unknown);
                    });
        }

        // 🚨 THE NO-OP UPSERT GUARD — the owner-side churn breaker. An upsert whose applied fields
        // are IDENTICAL to the persisted state must not reach the owner: the stream write
        // unconditionally re-stamps LastModified and mints a fresh Version, which re-broadcasts the
        // node to every subscriber (the deploy screen-flicker), appends a version-history row, and
        // for NodeType/Code nodes can flip IsDirty into a pointless recompile + hub recycle. A full
        // re-sync of unchanged content (a GitSync re-import whose _Activity manifest was lost, a
        // plugin re-install without a manifest) hits this for EVERY node. Acknowledge with the
        // persisted state instead — but only for a caller who could have written anyway
        // (SkipNoOpIfAuthorized): success-without-write must never become a permission bypass or a
        // content-confirmation oracle for callers the owner would have refused.
        void SkipNoOpIfAuthorized(MeshNode existing)
        {
            (string.IsNullOrEmpty(requestedBy)
                    ? Observable.Return(false)
                    // 🚨 TakeDecisionOutsideGate, not a bare Take(1) — #899. Both branches of
                    // the Subscribe below do real work (PostOk, or ApplyUpdateViaStream — a
                    // cross-hub stream write that publishes). See
                    // HubPermissionExtensions.TakeDecisionOutsideGate.
                    : hub.GetEffectivePermissions(node.Path, requestedBy!)
                        .TakeDecisionOutsideGate()
                        .Timeout(NodeOpForwardTimeout)
                        .Select(p => p.HasFlag(Permission.Update) || p.HasFlag(Permission.Sync))
                        .Catch((Exception _) => Observable.Return(false)))
                .Subscribe(
                    authorized =>
                    {
                        if (authorized)
                        {
                            logger.LogDebug(
                                "[CreateOrUpdate] no-op upsert for {Path}: identical to persisted state; skipped",
                                node.Path);
                            PostOk(existing, isCreate: false,
                                $"Node at '{node.Path}' unchanged — no-op upsert skipped");
                        }
                        else
                            // Not (provably) authorized → the normal owner path stays the single
                            // authority on allow/deny; it will refuse exactly as before.
                            ApplyUpdateViaStream(existing);
                    },
                    ex =>
                    {
                        logger.LogDebug(ex,
                            "[CreateOrUpdate] no-op permission probe failed for {Path}; taking the write path",
                            node.Path);
                        ApplyUpdateViaStream(existing);
                    });
        }

        void ApplyUpdateViaStream(MeshNode existing)
        {
            // Apply the update through the canonical mesh-node stream write API
            // (UpdateNodeRequest retired). hub.GetMeshNodeStream(path).Update routes
            // to the owning per-node hub via the IMeshNodeStreamCache (RFC 7396 merge
            // patch); the owner re-validates RLS + stamps auditing authoritatively and
            // its MeshNodeTypeSource debounces + persists. A denial surfaces on the
            // returned observable's OnError (the cache's write gate raises it when the
            // caller's permissions are warm).
            //
            // AccessContext: this runs inside the persistence-read Subscribe callback,
            // which may land on a non-handler thread where the AsyncLocal identity is
            // no longer set. Stamp the inbound identity around the synchronous
            // Update() call so its eager AccessContext capture (for both the merge
            // lambda and the outbound patch's WithAccessContext) sees the originating
            // user rather than null.
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (inboundCtx is not null && accessService is not null
                ? accessService.SwitchAccessContext(inboundCtx)
                : null)
            {
                // Apply the source-node update onto the LIVE node (the lambda parameter),
                // not a separately-read `existing` snapshot — avoids clobbering a concurrent
                // edit.
                //
                // 🚨 But the VERSION and the identity stamps are floored on the DURABLE row this
                // handler already read, not on the owner's live snapshot. Version is the node's
                // monotonic persistence clock, and `MonotonicWriteGuardStorageAdapter` REFUSES any
                // write that lands below the stored row. When the owner's live snapshot is BEHIND
                // durable truth — a hub that could not hydrate the stored node and started from a
                // blank `MeshNode.FromPath` — the mint comes out at 1, the guard refuses it, and
                // the owner's `AdoptDurableTruth` rebase then re-adopts the stored row: the
                // upsert's content is discarded while the request reports SUCCESS.
                //
                // That is not a corner case, it is how a GHOST PARTITION ROOT becomes permanent
                // (#902/#638): a content-less root at some version can be neither read, nor
                // created ("Node already exists"), nor upserted — every repair is silently thrown
                // away, which is exactly how the platform's Agent catalog stayed gone with no
                // mechanism anywhere able to restore it. Flooring on the row we JUST read makes
                // the write forward by construction, so the repair lands on the first attempt.
                // Content is untouched by this: it still comes from `live`.
                hub.GetMeshNodeStream(node.Path)
                    .Update(live => UpdateAccordingToSourceNode(live, node, hub.JsonSerializerOptions) with
                    {
                        Version = Math.Max(live.Version, existing.Version),
                        // Identity fields the merge is meant to PRESERVE — recovered from the
                        // durable row when the live snapshot has none, so a repaired node keeps
                        // its own lineage instead of being reborn with a default creation stamp.
                        CreatedDate = live.CreatedDate == default ? existing.CreatedDate : live.CreatedDate,
                        CreatedBy = live.CreatedBy ?? existing.CreatedBy,
                    })
                    .Subscribe(
                        saved => PostOk(saved, isCreate: false, $"Updated node at '{node.Path}'"),
                        ex =>
                        {
                            logger.LogWarning(ex,
                                "[CreateOrUpdate] inner UpdateNode faulted for {Path}", node.Path);
                            PostFail($"Inner UpdateNode faulted: {ex.Message}",
                                ex is UnauthorizedAccessException
                                    ? NodeUpsertRejectionReason.Unauthorized
                                    : NodeUpsertRejectionReason.Unknown);
                        });
            }
        }

        void PostOk(MeshNode result, bool isCreate, string logLine)
        {
            var okLog = baseActivity with
            {
                Messages = baseActivity.Messages.Add(
                    new LogMessage(logLine, Microsoft.Extensions.Logging.LogLevel.Information)),
                End = DateTime.UtcNow,
                Status = ActivityStatus.Succeeded,
            };
            hub.Post(
                isCreate
                    ? CreateOrUpdateNodeResponse.Created(result, okLog)
                    : CreateOrUpdateNodeResponse.Updated(result, okLog),
                o => o.ResponseFor(request));
        }

        void PostFail(string error, NodeUpsertRejectionReason reason)
        {
            var failLog = baseActivity with
            {
                Messages = baseActivity.Messages.Add(
                    new LogMessage(error, Microsoft.Extensions.Logging.LogLevel.Error)),
                End = DateTime.UtcNow,
                Status = ActivityStatus.Failed,
            };
            hub.Post(
                CreateOrUpdateNodeResponse.Fail(error, reason, failLog),
                o => o.ResponseFor(request));
        }

        static NodeUpsertRejectionReason MapCreateRejection(NodeCreationRejectionReason? r) => r switch
        {
            NodeCreationRejectionReason.InvalidPath => NodeUpsertRejectionReason.InvalidPath,
            NodeCreationRejectionReason.InvalidNodeType => NodeUpsertRejectionReason.InvalidNodeType,
            NodeCreationRejectionReason.ValidationFailed => NodeUpsertRejectionReason.ValidationFailed,
            _ => NodeUpsertRejectionReason.Unknown,
        };
    }

    /// <summary>
    /// Point a satellite's <c>MainNode</c> at its OWNING main node — the namespace with the
    /// satellite tail cut off (<c>{owner}/_Access</c> → <c>{owner}</c>) — when it still carries the
    /// record's self-default (<c>MainNode == Path</c>, i.e. never explicitly set).
    ///
    /// <para>Shared by BOTH write paths so they agree. <c>CreateNode</c> has always done this;
    /// <c>UpsertNode</c> did not, which left a satellite upserted without an explicit MainNode
    /// pointing at ITSELF — the exact shape every MainNode consumer misreads (SatelliteAccessRule
    /// delegates permissions to it, <c>rebuild_user_effective_permissions</c> projects a grant at
    /// <c>COALESCE(main_node, namespace)</c>, satellite scope filters match it as the attachment
    /// point). The <c>AccessAssignment</c> scope guard runs on BOTH paths, so both must normalise
    /// before it or the guard rejects the framework's own default shape.</para>
    ///
    /// <para>Idempotent: once MainNode differs from Path the node is returned unchanged, so calling
    /// it twice on one write is safe.</para>
    /// </summary>
    private static MeshNode NormalizeSatelliteMainNode(MeshNode node, MeshConfiguration meshConfig) =>
        !string.IsNullOrEmpty(node.NodeType)
        && !string.IsNullOrEmpty(node.Namespace)
        && meshConfig.IsSatelliteNodeType(node.NodeType)
        && node.MainNode == node.Path
            ? node with { MainNode = SatelliteTableMapping.OwnerOfSatellitePath(node.Namespace) }
            : node;

    /// <summary>
    /// True when applying <paramref name="sourceNode"/> onto <paramref name="existing"/> via
    /// <see cref="UpdateAccordingToSourceNode"/> would change nothing but the churn stamps
    /// (LastModified/Version) — the write can then be skipped entirely. MUST mirror that merge
    /// field-for-field (same null-keeps-state convention); a field added there without a compare
    /// here would silently stop landing on unchanged-otherwise nodes. Content compares
    /// structurally through the hub's serializer (bridging typed content against the persisted
    /// <see cref="JsonElement"/> representation); any doubt — a serialization failure, a mixed
    /// shape — reports "changed", which merely takes today's write path. Conservative by
    /// construction: it can only ever under-skip, never over-skip.
    /// </summary>
    private static bool IsNoOpUpsert(MeshNode existing, MeshNode sourceNode, JsonSerializerOptions options)
    {
        // Mirror the merge's operational-ownership rule (PreserveMeshOwnedOperational) BEFORE
        // comparing, or the skip could never fire for a NodeType node: the incoming copy's stale
        // bookkeeping would read as a content difference on every re-import, take the write path,
        // and bump Version + LastModified for nothing. That churn is what marks the node
        // "server-modified" and makes the two-way sync start preserving a server copy nobody
        // edited — issue #748's stated harm, at its cheapest seam.
        sourceNode = PreserveMeshOwnedOperational(existing, sourceNode, options);
        if ((sourceNode.Name ?? existing.Name) != existing.Name
            || (sourceNode.NodeType ?? existing.NodeType) != existing.NodeType
            || (sourceNode.Icon ?? existing.Icon) != existing.Icon
            || (sourceNode.Category ?? existing.Category) != existing.Category
            || (sourceNode.Description ?? existing.Description) != existing.Description
            || (sourceNode.Order ?? existing.Order) != existing.Order
            || (sourceNode.State == default ? existing.State : sourceNode.State) != existing.State
            || (sourceNode.PreRenderedHtml ?? existing.PreRenderedHtml) != existing.PreRenderedHtml)
            return false;
        var excludeApplied = sourceNode.ExcludeFromContext ?? existing.ExcludeFromContext;
        if (!ReferenceEquals(excludeApplied, existing.ExcludeFromContext)
            && (excludeApplied is null || existing.ExcludeFromContext is null
                || !excludeApplied.SequenceEqual(existing.ExcludeFromContext)))
            return false;
        var contentApplied = sourceNode.Content ?? existing.Content;
        if (ReferenceEquals(contentApplied, existing.Content))
            return true;
        if (existing.Content is null)
            return false;
        try
        {
            return JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(contentApplied, options),
                JsonSerializer.SerializeToElement(existing.Content, options));
        }
        catch (Exception)
        {
            return false; // unserializable content → the write path decides, exactly as before
        }
    }

    /// <summary>
    /// Merge function for <see cref="CreateOrUpdateNodeRequest"/>'s full-instance
    /// upsert. Copies every writable field from <paramref name="sourceNode"/>
    /// onto <paramref name="state"/>; preserves <paramref name="state"/>'s
    /// identity (Id, Path, CreatedDate, CreatedBy, Version) and stamps a
    /// fresh LastModified. Falls back to <paramref name="sourceNode"/> when
    /// <paramref name="state"/> is null (defensive — the create path
    /// dispatches CreateNodeRequest before reaching this lambda, so state
    /// should always be non-null here).
    /// </summary>
    private static MeshNode UpdateAccordingToSourceNode(
        MeshNode state, MeshNode sourceNode, JsonSerializerOptions options)
    {
        if (state is null) return sourceNode;
        sourceNode = PreserveMeshOwnedOperational(state, sourceNode, options);
        return state with
        {
            Name = sourceNode.Name ?? state.Name,
            NodeType = sourceNode.NodeType ?? state.NodeType,
            Icon = sourceNode.Icon ?? state.Icon,
            Category = sourceNode.Category ?? state.Category,
            // Description / Order / ExcludeFromContext were MISSING here: an upsert of an
            // EXISTING node silently dropped them — a GitSync re-import could never land a
            // frontmatter change to these fields (the chrome-less brochures kept their headers
            // while freshly-created nodes worked). Same null-keeps-state convention as the
            // rest: clearing a field is an explicit stream.Update, not an absent source value.
            Description = sourceNode.Description ?? state.Description,
            Order = sourceNode.Order ?? state.Order,
            ExcludeFromContext = sourceNode.ExcludeFromContext ?? state.ExcludeFromContext,
            Content = sourceNode.Content ?? state.Content,
            State = sourceNode.State == default ? state.State : sourceNode.State,
            PreRenderedHtml = sourceNode.PreRenderedHtml ?? state.PreRenderedHtml,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 🚨 The MESH-OWNED compile bookkeeping on a NodeType node
    /// (<see cref="NodeTypeOperationalContent.MemberNames"/>) is never taken from an UPSERT's
    /// incoming copy — it comes from <paramref name="state"/>, the node as its owner currently
    /// holds it. Issue #748.
    ///
    /// <para><b>Why this belongs HERE and nowhere else.</b> Every upsert writer holds a copy of the
    /// node that is stale by construction: a repo file embeds whatever verdict it carried when it was
    /// exported, an installer ships the package author's, and a sync's <c>existing</c> snapshot comes
    /// from the eventually-consistent query index — which lags the compile pipeline, because the
    /// compile does not run under the importer's lock. Letting any of those land REGRESSES live
    /// compile state to a stale verdict: a healthy type reverts to the previous <c>Pending</c> with a
    /// dangling <c>latestReleasePath</c> (memex 2026-08-02, four SocialMedia types), or a
    /// weeks-old "Ok" claims an assembly that no longer exists and the type parks on a cold cache
    /// (the stale-green class). <c>StaticRepoImporter</c> used to patch this up client-side against
    /// exactly that lagged snapshot — which is the CQRS rule's forbidden shape ("never read a single
    /// node's content from the query") and could only ever be as fresh as the index.</para>
    ///
    /// <para>The owner's merge is the one place where the question has an authoritative answer, and
    /// answering it here covers EVERY upsert writer (GitSync import, plugin install, webhook,
    /// instance sync, MCP) instead of one. Cross-hub, the effect is stronger than "read fresher":
    /// the members end up equal to the value this handle already holds, so they DROP OUT of the RFC
    /// 7396 patch entirely and the owner keeps its own — a stale mirror cannot even express the
    /// regression. Deliberate writes are unaffected: every compile-state writer (the watchers,
    /// <c>RequestNodeTypeRelease</c>, the MCP compile tool) goes through
    /// <c>GetMeshNodeStream(path).Update</c>, never through an upsert.</para>
    ///
    /// <para>Absent-in-live means absent in the result: a compile verdict baked into a repo file can
    /// never seed a node that has none. Non-NodeType nodes and content carrying no operational
    /// member return the SAME instance — no reshaping, no cost.</para>
    /// </summary>
    private static MeshNode PreserveMeshOwnedOperational(
        MeshNode state, MeshNode sourceNode, JsonSerializerOptions options) =>
        NodeTypeOperationalContent.PreserveLiveOperational(
            // The upsert convention is null-keeps-state, so an incoming node that omits NodeType is
            // still an update OF a NodeType node. Probe on the EFFECTIVE type or the rule would be
            // silently skipped for exactly those writers that ship the sparsest node — but only
            // reshape when the LIVE node actually is a NodeType, so a non-NodeType upsert (the
            // overwhelming majority) returns the same instance, exactly as the doc above promises.
            sourceNode.NodeType is null && state.NodeType is not null
                ? sourceNode with { NodeType = state.NodeType }
                : sourceNode,
            state,
            options);

    /// <summary>
    /// Sync handler for MoveNodeRequest — Copy subtree to target, then reactively delete
    /// every source path. Composition is pure <see cref="IObservable{T}"/> end-to-end:
    /// <c>CopyNode</c> → <c>Query</c> (source subtree paths) → <c>storage.Delete</c>
    /// per path, with change notifications fired so the query catalog refreshes.
    /// No <c>await</c>, no recursive <c>DeleteNodeRequest</c> orchestration. Mirror shape
    /// of <see cref="HandleCopyNodeRequest"/>.
    /// </summary>
    private static IMessageDelivery HandleMoveNodeRequest(
        IMessageHub hub,
        IMessageDelivery<MoveNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MeshWeaver.Mesh.Services.IMeshCatalog");
        var moveRequest = request.Message;
        var meshService = hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
        var sourcePath = moveRequest.SourcePath;
        var targetPath = moveRequest.TargetPath;

        // Move = Copy (with satellites + descendants) → reactive delete of every source path.
        // Delete only fires after Copy succeeds (SelectMany short-circuits on copy error).
        // Source-subtree enumeration is AUTHORITATIVE from storage (ListDescendantPaths),
        // never the eventually-consistent catalog query — the same stale-plan defect that
        // left recursive-delete survivors (issue #839) would leave source rows behind here.
        meshService.CopyNode(sourcePath, targetPath, includeDescendants: true, includeSatellites: true)
            .SelectMany(copied =>
                storage.ListDescendantPaths(sourcePath)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(15))
                    .SelectMany(descendants =>
                    {
                        var paths = descendants
                            .Where(p => !string.IsNullOrEmpty(p))
                            .Append(sourcePath)
                            .ToImmutableList();

                        if (paths.IsEmpty)
                            return Observable.Return(copied);

                        // Bottom-up delete (longest path first) so parent storage entries
                        // are removed only after their descendants. Each delete is its own
                        // observable; Merge runs them concurrently, ToList awaits all.
                        //
                        // Commit-then-publish: DeleteAndPublish chains the
                        // MeshChangeEvent.Deleted into the storage observable, so the
                        // event for each path fires only after that path's storage
                        // commit completes. The storage adapter's Changes feed
                        // fires the Deleted notification from inside its Delete.
                        return paths
                            .OrderByDescending(p => p.Length)
                            .ToObservable()
                            .SelectMany(p => storage.DeleteAndPublish(p, changeFeed))
                            .ToList()
                            .Select(_ => copied);
                    }))
            .Subscribe(
                movedNode =>
                {
                    changeFeed?.Publish(MeshChangeEvent.Created(movedNode));
                    hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request));
                    logger.LogInformation("Node moved {Source} -> {Target}", sourcePath, targetPath);
                },
                ex =>
                {
                    var msg = ex.Message ?? "Unknown error";
                    var reason = msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                        ? NodeMoveRejectionReason.TargetAlreadyExists
                        : msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                            ? NodeMoveRejectionReason.SourceNotFound
                            : NodeMoveRejectionReason.Unknown;
                    logger.LogError(ex, "Move {Source} -> {Target} failed", sourcePath, targetPath);
                    hub.Post(MoveNodeResponse.Fail(msg, reason), o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    /// <summary>
    /// Sync handler for <see cref="CopyNodeRequest"/>. Implements copy as
    /// <c>Query</c> (initial set of source + subtree) → <c>Select(CreateNode)</c>
    /// for each, all in observable composition. No <c>await</c>, no persistence read,
    /// no remote MeshNodeReference subscription. Per <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private static IMessageDelivery HandleCopyNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CopyNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MeshWeaver.Mesh.Services.IMeshCatalog");
        var copyRequest = request.Message;
        var meshService = hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var sourcePath = copyRequest.SourcePath;
        var targetPath = copyRequest.TargetPath;

        // 🚨 Capture the caller's identity at handler entry — it is live on the delivery here
        // (MessageHub restored it from delivery.AccessContext before this body ran). The per-node
        // CreateNode calls below are subscribed from SelectMany continuations on the workspace
        // emission scheduler, where the AsyncLocal AccessContext is WIPED. Without re-establishing
        // the caller's identity at each create's post site, MeshService.CaptureContext reads null,
        // the CreateNodeRequest posts with no AccessContext, and the PostPipeline fails closed —
        // the cross-partition copy/move bug (only the root landed; recursive children errored with
        // "AccessContext must never be null … message=CreateNodeRequest"). Mirrors the explicit
        // WithAccessContext stamping FanOutDeleteSubtree already does for recursive deletes.
        var callerAccessContext = request.AccessContext
            ?? accessService?.Context ?? accessService?.CircuitContext;

        // Wraps a per-node CreateNode so its eager AccessContext capture (MeshService.CaptureContext)
        // sees the caller's identity even though this runs on a scheduler thread. Observable.Using
        // opens the SwitchAccessContext scope on Subscribe — exactly when the cold CreateNode's Defer
        // reads the AsyncLocal and posts — and disposes it as the create completes.
        IObservable<MeshNode> CreateUnderCaller(MeshNode node) =>
            callerAccessContext is null || accessService is null
                ? meshService.CreateNode(node)
                : Observable.Using(
                    () => accessService.SwitchAccessContext(callerAccessContext),
                    _ => meshService.CreateNode(node));

        logger.LogDebug("[CopyNode] start source={Source} target={Target} (descendants={Desc} satellites={Sat})",
            sourcePath, targetPath, copyRequest.IncludeDescendants, copyRequest.IncludeSatellites);

        // Subtree query covers source + descendants + satellites (anything under sourcePath).
        // Query's first emission is the initial result set; we Take(1) and project each
        // node into a CreateNode call at the new target path.
        meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{sourcePath} scope:subtree"))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .Catch<QueryResultChange<MeshNode>, Exception>(ex =>
            {
                logger.LogWarning(ex, "[CopyNode] source query {Path} failed", sourcePath);
                return Observable.Empty<QueryResultChange<MeshNode>>();
            })
            .DefaultIfEmpty()
            .SelectMany(change =>
            {
                var nodes = change?.Items ?? (IReadOnlyList<MeshNode>)Array.Empty<MeshNode>();
                logger.LogDebug("[CopyNode] subtree returned {Count} nodes", nodes.Count);
                var sourceNode = nodes.FirstOrDefault(n =>
                    string.Equals(n.Path, sourcePath, StringComparison.Ordinal));
                if (sourceNode == null)
                {
                    hub.Post(CopyNodeResponse.Fail(
                            $"Source node not found at path: {sourcePath}",
                            NodeCopyRejectionReason.SourceNotFound),
                        o => o.ResponseFor(request));
                    return Observable.Empty<(MeshNode Root, int Desc, int Sat)>();
                }

                // Filter subtree by include flags (descendants vs satellites).
                var others = nodes
                    .Where(n => !string.Equals(n.Path, sourcePath, StringComparison.Ordinal))
                    .Where(n =>
                    {
                        var isSatellite = !string.Equals(n.MainNode, n.Path, StringComparison.Ordinal);
                        return isSatellite ? copyRequest.IncludeSatellites : copyRequest.IncludeDescendants;
                    })
                    .ToList();
                var descCount = others.Count(n => string.Equals(n.MainNode, n.Path, StringComparison.Ordinal));
                var satCount = others.Count - descCount;

                // Create root, then create all children in parallel via Merge — Move semantics
                // require all inserts to complete before the source is deleted. Every create runs
                // under the caller's identity (CreateUnderCaller) so the routed per-descendant
                // CreateNodeRequest carries a valid AccessContext across the scheduler hop.
                return CreateUnderCaller(RetargetNode(sourceNode, sourcePath, targetPath))
                    .SelectMany(rootCreated =>
                    {
                        if (others.Count == 0)
                            return Observable.Return<(MeshNode Root, int Desc, int Sat)>((rootCreated, descCount, satCount));
                        return others.ToObservable()
                            .Select(n => RetargetNode(n, sourcePath, targetPath))
                            .SelectMany(retargeted => CreateUnderCaller(retargeted))
                            .ToList()
                            .Select(_ => ((MeshNode Root, int Desc, int Sat))(rootCreated, descCount, satCount));
                    });
            })
            .Subscribe(
                t =>
                {
                    hub.Post(CopyNodeResponse.Ok(t.Root, t.Desc, t.Sat), o => o.ResponseFor(request));
                    logger.LogInformation("Copied {Source} -> {Target} (descendants={Desc}, satellites={Sat})",
                        sourcePath, targetPath, t.Desc, t.Sat);
                },
                ex =>
                {
                    var msg = ex.Message ?? "Unknown error";
                    var reason = msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                        ? NodeCopyRejectionReason.TargetAlreadyExists
                        : msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                            ? NodeCopyRejectionReason.SourceNotFound
                            : NodeCopyRejectionReason.Unknown;
                    logger.LogError(ex, "Copy {Source} -> {Target} failed", sourcePath, targetPath);
                    hub.Post(CopyNodeResponse.Fail(msg, reason), o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    /// <summary>
    /// Builds a new MeshNode by relocating <paramref name="node"/> from <paramref name="oldRoot"/>
    /// to <paramref name="newRoot"/>. Path is derived from Namespace + Id; MainNode is rewritten
    /// when it pointed inside the old subtree.
    /// </summary>
    private static MeshNode RetargetNode(MeshNode node, string oldRoot, string newRoot)
    {
        var newPath = string.Equals(node.Path, oldRoot, StringComparison.Ordinal)
            ? newRoot
            : node.Path.StartsWith(oldRoot + "/", StringComparison.Ordinal)
                ? newRoot + node.Path[oldRoot.Length..]
                : node.Path;
        var segs = newPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ns = segs.Length > 1 ? string.Join("/", segs.Take(segs.Length - 1)) : "";
        var id = segs[^1];
        var newMainNode = string.Equals(node.MainNode, oldRoot, StringComparison.Ordinal)
            ? newRoot
            : node.MainNode.StartsWith(oldRoot + "/", StringComparison.Ordinal)
                ? newRoot + node.MainNode[oldRoot.Length..]
                : node.MainNode;
        return node with
        {
            Id = id,
            Namespace = ns,
            MainNode = newMainNode,
            LastModified = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Reactive variant of the move-validator runner. Iterates validators sequentially
    /// via <c>Concat</c> (preserves short-circuit semantics — stops at the first failure),
    /// emits the first failure as a tuple or <c>null</c> if all pass.
    /// </summary>
    private static IObservable<(string? ErrorMessage, NodeMoveRejectionReason Reason)?> RunMoveValidatorsObs(
        IMessageHub hub,
        MeshNode node,
        MoveNodeRequest request)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Move,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>();
        return validators
            .Where(v => v.SupportedOperations.Count == 0 || v.SupportedOperations.Contains(NodeOperation.Move))
            .Select(v => v.Validate(context))
            .Concat()
            .Where(result => !result.IsValid)
            .Select(result =>
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeMoveRejectionReason.SourceNotFound,
                    NodeRejectionReason.Unauthorized => NodeMoveRejectionReason.ValidationFailed,
                    _ => NodeMoveRejectionReason.ValidationFailed
                };
                return ((string?, NodeMoveRejectionReason)?)(result.ErrorMessage, reason);
            })
            .Take(1)
            .DefaultIfEmpty(null);
    }
}
