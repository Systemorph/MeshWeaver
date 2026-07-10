using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
    /// All async work is wrapped in <c>Observable.FromAsync</c> and composed via Subscribe; the
    /// terminal response is posted from inside the deepest callback. The handler itself returns
    /// <c>request.Processed()</c> immediately so the hub scheduler is never blocked.
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

        if (meshConfig == null)
        {
            hub.Post(
                CreateNodeResponse.Fail("MeshConfiguration not available", NodeCreationRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // FAIL CLOSED on missing storage: a create that cannot persist must error,
        // never ack. The old fallback (save = Observable.Return(node)) reported
        // Success while writing NOTHING — on the 2026-06-11 atioz portal every MCP
        // create was acked "Created: …" and silently lost. Storage-less meshes are
        // not a supported mode (tests use AddInMemoryPersistence); a null adapter
        // here is always a wiring defect on the responding hub — name it loudly.
        if (persistence == null)
        {
            logger.LogError(
                "[CreateNode] REFUSED {Path}: no IStorageAdapter on hub {Hub} — the create would be acked but never persisted. " +
                "Register persistence (AddPartitioned*Persistence / AddInMemoryPersistence) on this hub's service provider.",
                request.Message.Node.Path, hub.Address);
            hub.Post(
                CreateNodeResponse.Fail(
                    $"No storage adapter on hub '{hub.Address}' — refusing to create '{request.Message.Node.Path}' because it could not be persisted.",
                    NodeCreationRejectionReason.Unknown),
                o => o.ResponseFor(request));
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
            hub.Post(
                CreateNodeResponse.Fail("Node path and Id must not be empty",
                    NodeCreationRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // 0b. Reject nodes that are neither typed nor have content. A bare MeshNode with
        // no NodeType and no Content can't spawn a useful per-node hub (no content type
        // means no AddMeshDataSource / GetDataRequest handler), so it's always a caller bug.
        if (string.IsNullOrWhiteSpace(node.NodeType) && node.Content == null)
        {
            hub.Post(
                CreateNodeResponse.Fail(
                    "Node must have a NodeType or Content set; bare nodes are not allowed.",
                    NodeCreationRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // 0c. Structural fail-fast: an Activity MeshNode must NEVER be anchored at a top-level /
        // ownerless path. A bare `_Activity/{id}` (empty owner, MainNode="") — or any `_Activity`
        // folder with no owning node before it — has no per-node hub to route to, so every poster
        // (SubmitCodeRequest / DataChangeRequest) and every subscriber (the GUI progress panels)
        // NotFound-storms the router (the atioz `_Activity/import-*` / `_Activity/compile-*` storm).
        // Reject at the create boundary — loudly, at the source — instead of letting the phantom
        // escape downstream. Runs BEFORE EnsurePartitionBootstrap + the validators (and BEFORE the
        // System bypass inside those validators) because this is a STRUCTURAL invariant that holds
        // for every identity, including System-driven compile/import/startup activities. Covers all
        // creators: CreateNode AND CreateOrUpdateNode (whose inner create funnels through here).
        if (ActivityNodeGuard.IsOwnerless(node, out var ownerlessReason))
        {
            logger.LogError("[CreateNode] REFUSED ownerless Activity {Path}: {Reason}", node.Path, ownerlessReason);
            hub.Post(
                CreateNodeResponse.Fail(ownerlessReason, NodeCreationRejectionReason.InvalidPath),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // 1. Read existing — persistence first (catalog.GetNode auto-creates from templates),
        //    then fall back to the in-memory config. persistence.GetNode is already
        //    IObservable so we don't need to wrap it in Observable.FromAsync.
        var existingObs = persistence != null
            ? persistence.Read(node.Path, hub.JsonSerializerOptions)
            : Observable.Return<MeshNode?>(null);

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
                                .Where(n => n is not null)
                                .Select(n => n!)
                            : Observable.Return(confirmedNode);
                        return saveObs.Select(savedConfirmed => (mode: "confirm", node: savedConfirmed));
                    }
                    // Node exists & not a confirmation → fail.
                    hub.Post(
                        CreateNodeResponse.Fail(
                            $"Node already exists at path: {node.Path}",
                            NodeCreationRejectionReason.NodeAlreadyExists),
                        o => o.ResponseFor(request));
                    return Observable.Empty<(string mode, MeshNode node)>();
                }

                // 1b. Auto-set MainNode for satellite types before validation.
                if (!string.IsNullOrEmpty(node.NodeType)
                    && !string.IsNullOrEmpty(node.Namespace)
                    && meshConfig.IsSatelliteNodeType(node.NodeType)
                    && node.MainNode == node.Path)
                {
                    node = node with { MainNode = node.Namespace };
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
                return EnsurePartitionBootstrap(hub, node, capturedRequest, logger)
                    .SelectMany(_ => RunCreationValidatorsObs(hub, node, capturedRequest))
                    .SelectMany(validationError =>
                    {
                        if (validationError != null)
                        {
                            logger.LogWarning(
                                "Validator rejected node creation at {Path}: {Error}",
                                node.Path, validationError.Value.ErrorMessage);
                            hub.Post(
                                CreateNodeResponse.Fail(
                                    validationError.Value.ErrorMessage ?? "Validation failed",
                                    validationError.Value.Reason),
                                o => o.ResponseFor(request));
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
                                hub.Post(
                                    CreateNodeResponse.Fail(
                                        $"NodeType '{node.NodeType}' is not registered",
                                        NodeCreationRejectionReason.InvalidNodeType),
                                    o => o.ResponseFor(request));
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
                                        .Where(n => n is not null)
                                        .Select(n => n!)
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
                    // waits forever. The node is already persisted — but if a post-creation
                    // handler errored, surface that as a Fail so the caller can react (don't
                    // silently lie with Ok).
                    logger.LogDebug("[CreateNode] step=post-handlers-start path={Path}", resultNode.Path);
                    RunPostCreationHandlersObs(hub, resultNode, capturedRequest.CreatedBy, logger)
                        .Subscribe(
                            _ => { },
                            ex =>
                            {
                                logger.LogError(ex,
                                    "Post-creation handler chain errored at {Path} — node IS persisted but handler failed",
                                    resultNode.Path);
                                hub.Post(
                                    CreateNodeResponse.Fail(
                                        $"Node persisted but post-creation handler failed: {ex.Message}",
                                        NodeCreationRejectionReason.Unknown),
                                    o => o.ResponseFor(request));
                            },
                            () =>
                            {
                                logger.LogDebug("[CreateNode] step=post-handlers-done path={Path} — posting Ok", resultNode.Path);
                                hub.Post(CreateNodeResponse.Ok(resultNode),
                                    o => o.ResponseFor(request));
                            });
                },
                ex =>
                {
                    if (ex is InvalidOperationException)
                    {
                        logger.LogWarning(ex, "Node creation failed for path {Path}", node.Path);
                        hub.Post(
                            CreateNodeResponse.Fail(ex.Message, NodeCreationRejectionReason.ValidationFailed),
                            o => o.ResponseFor(request));
                    }
                    else
                    {
                        logger.LogError(ex, "Unexpected error during node creation at {Path}", node.Path);
                        hub.Post(
                            CreateNodeResponse.Fail($"Unexpected error: {ex.Message}",
                                NodeCreationRejectionReason.Unknown),
                            o => o.ResponseFor(request));
                    }
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
    ///   <item><b>Central mesh hub only.</b> Off-router create handlers — the static-repo import
    ///     hub (which already provisions its own roots and runs as System) and per-node hubs —
    ///     don't redo it.</item>
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
    ///     creation past <c>PartitionWriteGuardValidator</c>'s "no partition, no write" rule.</item>
    /// </list>
    ///
    /// <para><b>No recursion:</b> the root create (empty namespace) and the grant create (path
    /// under <c>/_Access/</c>) are exactly the two node shapes skipped at the top, so they never
    /// re-enter the bootstrap. <b>Idempotent + race-safe:</b> a concurrent first-writer that
    /// loses the create race sees "already exists" and treats it as success. <b>Re-heals:</b>
    /// root + grant presence are re-probed on every child create, so a partition left half-broken
    /// (root-missing, grant-missing, or both) is repaired on the next authorized child create —
    /// nothing is permanently cached as "bootstrapped".</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> EnsurePartitionBootstrap(
        IMessageHub hub, MeshNode node, CreateNodeRequest request, ILogger logger)
    {
        // Central mesh hub only — see remarks.
        if (!ReferenceEquals(hub, hub.GetMeshHub()))
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
        var rootObs = ReadNodeAuthoritative(hub, persistence, partition);
        var grantObs = grantPath is null
            ? Observable.Return<MeshNode?>(null)
            : ReadNodeAuthoritative(hub, persistence, grantPath);

        return Observable.Zip(rootObs, grantObs, (root, grant) => (root, grant))
            .SelectMany(t =>
            {
                var rootExists = t.root is not null;
                // For a System / unattributed creator there is no per-creator grant to ensure.
                var grantExists = !isRealCreator || t.grant is not null;
                if (rootExists && grantExists)
                    return Observable.Return(System.Reactive.Unit.Default);

                // Authorization gate — heal ONLY for a creator who could legitimately create here.
                // System short-circuits to Permission.All; an unauthorized creator gets no heal,
                // so the requested child is denied by the validators exactly as before.
                var effectiveUser = string.IsNullOrEmpty(creator) ? WellKnownUsers.Anonymous : creator;
                return hub.CheckPermission(partition, effectiveUser, Permission.Create)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(15))
                    .Catch<bool, Exception>(ex =>
                    {
                        logger.LogDebug(ex,
                            "[PartitionBootstrap] authorization probe for {User} on '{Partition}' faulted; skipping heal",
                            effectiveUser, partition);
                        return Observable.Return(false);
                    })
                    .SelectMany(authorized =>
                    {
                        if (!authorized)
                            return Observable.Return(System.Reactive.Unit.Default);

                        var healRoot = rootExists
                            ? Observable.Return(System.Reactive.Unit.Default)
                            : ProvisionAndCreateRoot(hub, partition, meshService, accessService, logger);
                        return healRoot.SelectMany(_ => isRealCreator && !grantExists
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
    /// </summary>
    private static IObservable<MeshNode?> ReadNodeAuthoritative(
        IMessageHub hub, IStorageAdapter persistence, string path)
        => persistence.Read(path, hub.JsonSerializerOptions)
            .Take(1)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .DefaultIfEmpty(null)
            .Select(n => n ?? hub.ServiceProvider.FindStaticNode(path));

    /// <summary>
    /// Provisions every provider's backing store (PG schema + tables) then writes the partition's
    /// <c>Space</c> root under System. Idempotent — a concurrent-create "already exists" is success.
    /// </summary>
    private static IObservable<System.Reactive.Unit> ProvisionAndCreateRoot(
        IMessageHub hub, string partition, IMeshService meshService,
        AccessService? accessService, ILogger logger)
    {
        // Reactive + pooled + promise-cached; the InMemory / FileSystem providers no-op. Merge +
        // ToList so the chain always emits exactly once (even with no providers) before the write.
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provision = providers.Length == 0
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(providers.Select(p => p.EnsurePartitionProvisioned(partition)))
                .ToList()
                .Select(_ => System.Reactive.Unit.Default);

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

                        return RunDeletionValidatorsWithWarningsObs(hub, rootNode, capturedRequest)
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
                                return CollectPathsForDelete(hub, path, capturedRequest.Recursive, opts.Timeout, logger)
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
                                            : Observable.Return<(string Path, string Error)?>(null);

                                        return preValidate.SelectMany(failure =>
                                        {
                                            if (failure is { } f)
                                            {
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

                                        // 4. Bottom-up fan-out. Descendants → per-node hubs (each
                                        //    re-enters this handler with Recursive=false);
                                        //    root → local storage delete (already validated above,
                                        //    no need to re-enter via hub.Observe and avoid recursion).
                                        logger.LogDebug(
                                            "[DeleteNode] committing path={Path} count={Count}",
                                            path, collected.ToDelete.Count);

                                        // 🚨 "Delete wins" — tombstone every path BEFORE the fan-out.
                                        // FanOutDeleteSubtree ACTIVATES each leaf's per-node hub to
                                        // process its own delete, and that activation's save (the
                                        // workspace sees the just-loaded node as an "add") is exactly
                                        // what resurrects the row. Marking here — before any leaf hub
                                        // is activated — guarantees the resurrecting save's guard
                                        // already sees the tombstone. Marking only on success (after
                                        // the delete) lost a check-before-mark race: the activation
                                        // save checked ~14 ms before the mark and slipped through.
                                        foreach (var dp in collected.ToDelete)
                                            recentlyDeleted?.MarkDeleted(dp);
                                        recentlyDeleted?.MarkDeleted(path);

                                        return FanOutDeleteSubtree(
                                                meshHub, storage, path, collected.ToDelete,
                                                capturedRequest, request.AccessContext, logger, collectedMessages)
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
                                                meshHub.Post(
                                                    DeleteNodeResponse.Ok() with { Log = okLog },
                                                    o => o.ResponseFor(request));
                                            })
                                            .Select(_ => System.Reactive.Unit.Default);
                                        });
                                    });
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
                                : $"Unexpected error: {ex.Message}"),
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
    /// Phase 1 — enumerate the paths to delete. **Paths only** via the catalog
    /// query with <c>select:path</c> projection — <see cref="MeshNode.Content"/>
    /// is stale on a query row and must never be read (per
    /// <c>Doc/Architecture/CqrsAndContentAccess.md</c>). Validators that need a
    /// live node use <c>workspace.GetMeshNodeStream(path)</c> downstream.
    ///
    /// <para>Uses <c>scope:descendants</c> (strictly children-and-below — root
    /// excluded) so the bottom-up fan-out in
    /// <see cref="HierarchicalPathDeletion.DeleteSubtree"/>
    /// terminates at the root rather than re-entering through it. The root
    /// path is added to the returned set after the query so it is deleted
    /// last (when it becomes a leaf).</para>
    /// </summary>
    private static IObservable<(bool RootExists, ImmutableHashSet<string> ToDelete, bool HasUnlistedChildren)>
        CollectPathsForDelete(
            IMessageHub hub,
            string path,
            bool recursive,
            TimeSpan timeout,
            ILogger logger)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var empty = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

        // 🚨 Descendant enumeration is INFRASTRUCTURE — the handler already gated
        // the operation on the caller's Delete permission at the ROOT (Phase 2:
        // CheckDeletePermissionForNode). Subtree enumeration is then just routing:
        // "what paths am I about to delete?". User-level RLS filtering on this
        // query would HIDE descendants the caller can't see, making non-recursive
        // delete proceed (HasUnlistedChildren=false) and recursive delete miss
        // entire branches from AffectedPaths. Run the query as System; per-leaf
        // delete checks fire at each descendant's own hub via the recursive
        // DeleteNodeRequest fan-out.
        if (!recursive)
        {
            // Non-recursive: only delete the root if it has no children. The
            // children-scope query with `select:path` projects each match to a
            // dict — use `Query<object>` so the projected items survive
            // the type filter (a `MeshNode` cast would drop every dict).
            return meshService
                .Query<object>(MeshQueryRequest.FromQuery(
                    $"namespace:{path} scope:children select:path",
                    MeshWeaver.Mesh.Security.WellKnownUsers.System))
                .Take(1)
                .Select(change => (RootExists: true, empty.Add(path), change.Items.Count > 0))
                .Timeout(timeout);
        }

        // Recursive: enumerate strict descendants via `scope:descendants`.
        // `select:path` projects each result to a dict (`{ "path": "..." }`),
        // so Query<object> is required — `Query<MeshNode>` would
        // silently drop every dict at the type filter. Root is added
        // explicitly so it is deleted last (after its subtree).
        return meshService
            .Query<object>(MeshQueryRequest.FromQuery(
                $"namespace:{path} scope:descendants select:path",
                MeshWeaver.Mesh.Security.WellKnownUsers.System))
            .Take(1)
            .Select(change =>
            {
                var set = empty
                    .Union(change.Items
                        .OfType<IDictionary<string, object?>>()
                        .Select(d => d.TryGetValue("path", out var v) ? v as string : null)
                        .Where(p => !string.IsNullOrEmpty(p))!)
                    .Add(path);
                logger.LogDebug("[DeleteNode] collected path={Path} total={Count}", path, set.Count);
                return (RootExists: true, set, false);
            })
            .Timeout(timeout);
    }

    /// <summary>
    /// Bottom-up traversal of the path set via <see cref="HierarchicalPathDeletion"/>.
    /// <para>
    /// <b>Root path:</b> deleted via local <see cref="IStorageAdapter.Delete"/>
    /// — already validated by the calling handler before fan-out. This avoids
    /// self-recursion that would arise if we posted <see cref="DeleteNodeRequest"/>
    /// at our own address (the same handler would re-enter).
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
        ImmutableList<LogMessage>.Builder collectedMessages)
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
                        baseRequest with { Path = path, Recursive = false },
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
    /// return the FIRST validator failure as <c>(Path, Error)</c>, or <c>null</c>
    /// if all descendants pass. Subscribed before any storage side effects fire,
    /// so a single failing descendant aborts the whole subtree delete with no
    /// partial state — sibling deletes that pass validation never run.
    /// </summary>
    private static IObservable<(string Path, string Error)?> PreValidateDescendantsObs(
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
            return Observable.Return<(string, string)?>(null);

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
                    return ((string, string)?)null;
                return (p, resp.Errors[0]);
            })
            .Catch<(string, string)?, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "[DeleteNode] pre-validate descendant failed {Path}", p);
                return Observable.Return<(string, string)?>((p, ex.Message));
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

        // Take(1) closes the inner observable — GetEffectivePermissions rides
        // the live AccessAssignment synced query and is hot, so without Take(1)
        // the .Select chain never completes and the handler hangs.
        return hub.GetEffectivePermissions(pathToCheck, userId)
            .Take(1)
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
            DeleteNodeRequest request)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext,
            // This runner validates the ROOT node of the delete, so the cascade root is the
            // request path itself.
            DeleteCascadeRootPath = request.Path
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
    /// <c>workspace.GetMeshNodeStream(path).Update(state =&gt; UpdateAccordingToSourceNode(state, sourceNode))</c>.
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

        existingObs.Subscribe(
            existing =>
            {
                if (existing is null)
                {
                    DispatchInnerCreate();
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
            var forwarded = hub.Post(inner, o =>
            {
                var withTarget = o.WithTarget(hub.Address);
                return inboundCtx is not null ? withTarget.WithAccessContext(inboundCtx) : withTarget;
            })!;
            hub.Observe(forwarded)
                .Subscribe(
                    d =>
                    {
                        if (d.Message is CreateNodeResponse cr && cr.Success && cr.Node is not null)
                            PostOk(cr.Node, isCreate: true, $"Created node at '{node.Path}'");
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
                // edit; carry the live version (the owner mints the fresh one on apply).
                hub.GetMeshNodeStream(node.Path)
                    .Update(live => UpdateAccordingToSourceNode(live, node) with { Version = live.Version })
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
    /// Merge function for <see cref="CreateOrUpdateNodeRequest"/>'s full-instance
    /// upsert. Copies every writable field from <paramref name="sourceNode"/>
    /// onto <paramref name="state"/>; preserves <paramref name="state"/>'s
    /// identity (Id, Path, CreatedDate, CreatedBy, Version) and stamps a
    /// fresh LastModified. Falls back to <paramref name="sourceNode"/> when
    /// <paramref name="state"/> is null (defensive — the create path
    /// dispatches CreateNodeRequest before reaching this lambda, so state
    /// should always be non-null here).
    /// </summary>
    private static MeshNode UpdateAccordingToSourceNode(MeshNode state, MeshNode sourceNode)
    {
        if (state is null) return sourceNode;
        return state with
        {
            Name = sourceNode.Name ?? state.Name,
            NodeType = sourceNode.NodeType ?? state.NodeType,
            Icon = sourceNode.Icon ?? state.Icon,
            Category = sourceNode.Category ?? state.Category,
            Content = sourceNode.Content ?? state.Content,
            State = sourceNode.State == default ? state.State : sourceNode.State,
            PreRenderedHtml = sourceNode.PreRenderedHtml ?? state.PreRenderedHtml,
            LastModified = DateTimeOffset.UtcNow,
        };
    }

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
        meshService.CopyNode(sourcePath, targetPath, includeDescendants: true, includeSatellites: true)
            .SelectMany(copied =>
                meshService.Query<object>(MeshQueryRequest.FromQuery(
                        $"path:{sourcePath} scope:subtree select:path"))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(15))
                    .SelectMany(change =>
                    {
                        var paths = change.Items
                            .OfType<IDictionary<string, object?>>()
                            .Select(d => d.TryGetValue("path", out var v) ? v as string : null)
                            .Where(p => !string.IsNullOrEmpty(p))
                            .Select(p => p!)
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
