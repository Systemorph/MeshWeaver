using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
        config.TypeRegistry.WithType(typeof(CreateNodeRequest), nameof(CreateNodeRequest));
        config.TypeRegistry.WithType(typeof(CreateNodeResponse), nameof(CreateNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeCreationRejectionReason), nameof(NodeCreationRejectionReason));
        config.TypeRegistry.WithType(typeof(DeleteNodeRequest), nameof(DeleteNodeRequest));
        config.TypeRegistry.WithType(typeof(DeleteNodeResponse), nameof(DeleteNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeDeletionRejectionReason), nameof(NodeDeletionRejectionReason));
        config.TypeRegistry.WithType(typeof(UpdateNodeRequest), nameof(UpdateNodeRequest));
        config.TypeRegistry.WithType(typeof(UpdateNodeResponse), nameof(UpdateNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeUpdateRejectionReason), nameof(NodeUpdateRejectionReason));
        config.TypeRegistry.WithType(typeof(MoveNodeRequest), nameof(MoveNodeRequest));
        config.TypeRegistry.WithType(typeof(MoveNodeResponse), nameof(MoveNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeMoveRejectionReason), nameof(NodeMoveRejectionReason));

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
    /// Registers handlers for mesh node operations.
    /// </summary>
    public static MessageHubConfiguration WithNodeOperationHandlers(this MessageHubConfiguration config)
    {
        return config
            .AddMeshTypes()
            .WithHandler<CreateNodeRequest>(HandleCreateNodeRequest)
            .WithHandler<DeleteNodeRequest>(HandleDeleteNodeRequest)
            .WithHandler<UpdateNodeRequest>(HandleUpdateNodeRequest)
            .WithHandler<MoveNodeRequest>(HandleMoveNodeRequest)
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
                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.GrainKeepAlive");
                logger?.LogInformation("HeartBeat: keeping grain alive for {Hub} (callback on {Parent})",
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
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var catalog = hub.ServiceProvider.GetService<IMeshCatalog>();
        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();

        if (catalog == null)
        {
            hub.Post(
                CreateNodeResponse.Fail("IMeshCatalog not available", NodeCreationRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var createRequest = request.Message;

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

        // 1. Read existing — persistence first (catalog.GetNodeAsync auto-creates from templates),
        //    then fall back to the in-memory config. Wrap in Observable.FromAsync so no `await`.
        var existingObs = persistence != null
            ? Observable.FromAsync(token => persistence.GetNodeAsync(node.Path, token))
            : Observable.Return<MeshNode?>(null);

        existingObs
            .Select(existing =>
            {
                if (existing == null && catalog.Configuration.Nodes.TryGetValue(node.Path, out var configNode))
                    return configNode;
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
                        var saveObs = persistence != null
                            ? persistence.SaveNode(confirmedNode)
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
                    && catalog.Configuration.IsSatelliteNodeType(node.NodeType)
                    && node.MainNode == node.Path)
                {
                    node = node with { MainNode = node.Namespace };
                }

                // 2. Validators → 3. NodeType existence → 4-7. Enrich + save + change feed + version
                return RunCreationValidatorsObs(hub, node, capturedRequest)
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

                        // 3. NodeType existence check.
                        IObservable<bool> typeExistsObs;
                        if (string.IsNullOrEmpty(node.NodeType))
                        {
                            typeExistsObs = Observable.Return(true);
                        }
                        else if (catalog.Configuration.Nodes.ContainsKey(node.NodeType))
                        {
                            typeExistsObs = Observable.Return(true);
                        }
                        else if (persistence != null)
                        {
                            typeExistsObs = Observable.FromAsync(token =>
                                persistence.ExistsAsync(node.NodeType, token));
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

                            // 4. Active state.
                            var newNode = node with { State = MeshNodeState.Active };

                            // 5. Enrich (optional service).
                            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
                            var enrichedObs = nodeTypeService != null
                                ? Observable.FromAsync(token => nodeTypeService.EnrichWithNodeTypeAsync(newNode, token))
                                : Observable.Return(newNode);

                            // 6. Persist.
                            return enrichedObs.SelectMany(enriched =>
                            {
                                var saveObs = persistence != null
                                    ? persistence.SaveNode(enriched)
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

                    // Notify change feed (sync side-effect).
                    var changeEvent = mode == "create"
                        ? MeshChangeEvent.Created(resultNode)
                        : MeshChangeEvent.Updated(resultNode);
                    hub.ServiceProvider.GetService<IMeshChangeFeed>()?.Publish(changeEvent);

                    // Version history (non-critical, fire-and-forget Subscribe).
                    if (mode == "create" && !catalog.Configuration.IsSatelliteNodeType(resultNode.NodeType))
                    {
                        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
                        if (versionQuery != null)
                        {
                            Observable.FromAsync(token =>
                                    versionQuery.WriteVersionAsync(resultNode, hub.JsonSerializerOptions, token))
                                .Subscribe(
                                    _ => { },
                                    ex => logger.LogWarning(ex,
                                        "Version history write failed at {Path} (non-critical)",
                                        resultNode.Path));
                        }
                    }

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

                    // Run post-creation handlers (Subscribe-based) and post Ok inside the
                    // OnCompleted so the response only goes out after handlers have all run.
                    RunPostCreationHandlersObs(hub, resultNode, capturedRequest.CreatedBy, logger)
                        .Subscribe(
                            _ => { },
                            ex => logger.LogWarning(ex,
                                "Post-creation handler chain errored at {Path}", resultNode.Path),
                            () => hub.Post(CreateNodeResponse.Ok(resultNode),
                                o => o.ResponseFor(request)));
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
    /// Fully synchronous handler — returns <see cref="IMessageDelivery"/>, never <see cref="Task"/>.
    /// No <c>IMeshCatalog</c> usage:
    /// <list type="bullet">
    /// <item>Own-node read: <c>hub.GetWorkspace().GetStream&lt;MeshNode&gt;().Take(1)</c> — the
    /// node-operation handlers run on the node's own hub (registered via MeshDataSource), so
    /// the workspace already has the live MeshNode in its replay-cached BehaviorSubject.</item>
    /// <item>Children listing: internal <see cref="IMeshQueryCore"/> with <c>namespace:{path}</c>
    /// — bypasses access control because the caller has already passed RunDeletionValidatorsObs.</item>
    /// <item>Self / child deletion: <see cref="IMeshService.DeleteNode"/>, which Posts
    /// <see cref="DeleteNodeRequest"/> through the security pipeline and returns
    /// <c>IObservable&lt;bool&gt;</c>. No <c>catalog.DeleteNodeAsync</c> call.</item>
    /// </list>
    /// Recursive child deletes are issued in parallel; on the FIRST failure observed, the
    /// parent's Fail response is posted (in-flight child deletes are not aborted but the
    /// parent will not be deleted).
    /// </summary>
    private static IMessageDelivery HandleDeleteNodeRequest(
        IMessageHub hub,
        IMessageDelivery<DeleteNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshNode>>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var deleteRequest = request.Message;
        if (string.IsNullOrEmpty(deleteRequest.DeletedBy)
            && request.AccessContext?.ObjectId is { Length: > 0 } deleteSenderId)
            deleteRequest = deleteRequest with { DeletedBy = deleteSenderId };

        var capturedRequest = deleteRequest;
        var path = deleteRequest.Path;

        var nodeStream = hub.GetWorkspace()?.GetStream<MeshNode>();
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();

        // Read own node from the live workspace stream when the hub exposes one (BehaviorSubject —
        // emits current value synchronously on subscribe). Fall back to persistence when not (some
        // test/infra configurations don't materialize the stream). No catalog usage either way.
        var existingNodeObs = nodeStream != null
            ? nodeStream
                .Take(1)
                .Select(nodes => nodes?.FirstOrDefault(n => n.Path == path))
            : Observable.FromAsync(token => persistence.GetNodeAsync(path, token));

        existingNodeObs
            .SelectMany(existingNode =>
            {
                if (existingNode == null)
                {
                    hub.Post(
                        DeleteNodeResponse.Fail(
                            $"Node not found at path: {path}",
                            NodeDeletionRejectionReason.NodeNotFound),
                        o => o.ResponseFor(request));
                    return Observable.Empty<MeshNode>();
                }

                return RunDeletionValidatorsObs(hub, existingNode, capturedRequest)
                    .SelectMany(validationError =>
                    {
                        if (validationError != null)
                        {
                            logger.LogWarning(
                                "Validator rejected node deletion at {Path}: {Error}",
                                path, validationError.Value.ErrorMessage);
                            hub.Post(
                                DeleteNodeResponse.Fail(
                                    validationError.Value.ErrorMessage ?? "Validation failed",
                                    validationError.Value.Reason),
                                o => o.ResponseFor(request));
                            return Observable.Empty<MeshNode>();
                        }
                        return Observable.Return(existingNode);
                    });
            })
            .SelectMany(existingNode =>
                // Collect direct children via IMeshStorage (no access control needed —
                // the caller has already passed the deletion validator chain). Using
                // persistence directly is more reliable than routing through query
                // services whose scoping can vary across hub configurations.
                Observable.FromAsync(async token =>
                {
                    var list = new List<MeshNode>();
                    await foreach (var child in persistence.GetChildrenAsync(path).WithCancellation(token))
                        list.Add(child);
                    return list;
                })
                .Select(children => (existingNode, children: (IList<MeshNode>)children)))
            .Subscribe(
                tuple =>
                {
                    var children = tuple.children;

                    if (children.Count == 0)
                    {
                        // Leaf — delete via IMeshStorage directly. We CANNOT use
                        // IMeshService.DeleteNode here: that posts DeleteNodeRequest, which
                        // routes back to this handler for the SAME path and recurses forever.
                        DeleteSelfFromStorage(hub, path, capturedRequest, request, persistence, logger);
                        return;
                    }

                    if (!capturedRequest.Recursive)
                    {
                        hub.Post(
                            DeleteNodeResponse.Fail(
                                $"Node at '{path}' has children. Use recursive delete to remove it.",
                                NodeDeletionRejectionReason.HasChildren),
                            o => o.ResponseFor(request));
                        return;
                    }

                    // Recursive: delete children in parallel via IMeshService.DeleteNode.
                    // Track outcome with an Interlocked counter; on FIRST failure, post the
                    // parent's Fail response immediately. On ALL successes, delete self.
                    var remaining = children.Count;
                    var failureFlag = 0;
                    string? firstFailedPath = null;

                    foreach (var child in children)
                    {
                        var childPath = child.Path;
                        meshService.DeleteNode(childPath).Subscribe(
                            success =>
                            {
                                if (!success
                                    && Interlocked.CompareExchange(ref failureFlag, 1, 0) == 0)
                                {
                                    Interlocked.CompareExchange(ref firstFailedPath, childPath, null);
                                    hub.Post(
                                        DeleteNodeResponse.Fail(
                                            $"Cannot delete '{path}': child '{childPath}' deletion returned false.",
                                            NodeDeletionRejectionReason.ChildDeletionFailed),
                                        o => o.ResponseFor(request));
                                }

                                if (Interlocked.Decrement(ref remaining) == 0
                                    && Interlocked.CompareExchange(ref failureFlag, 0, 0) == 0)
                                {
                                    // All children deleted successfully — now delete self via
                                    // IMeshStorage (NOT IMeshService — that would re-trigger
                                    // this handler for the same path and recurse forever).
                                    DeleteSelfFromStorage(hub, path, capturedRequest, request, persistence, logger);
                                }
                            },
                            ex =>
                            {
                                if (Interlocked.CompareExchange(ref failureFlag, 1, 0) == 0)
                                {
                                    Interlocked.CompareExchange(ref firstFailedPath, childPath, null);
                                    logger.LogWarning(ex,
                                        "Child deletion failed for {ChildPath} under {Path}",
                                        childPath, path);
                                    hub.Post(
                                        DeleteNodeResponse.Fail(
                                            $"Cannot delete '{path}': child '{childPath}' threw: {ex.Message}",
                                            NodeDeletionRejectionReason.ChildDeletionFailed),
                                        o => o.ResponseFor(request));
                                }
                                Interlocked.Decrement(ref remaining);
                            });
                    }
                },
                ex =>
                {
                    if (ex is InvalidOperationException)
                    {
                        logger.LogWarning(ex, "Node deletion failed for path {Path}", path);
                        hub.Post(
                            DeleteNodeResponse.Fail(ex.Message, NodeDeletionRejectionReason.ValidationFailed),
                            o => o.ResponseFor(request));
                    }
                    else
                    {
                        logger.LogError(ex, "Unexpected error during node deletion at {Path}", path);
                        hub.Post(
                            DeleteNodeResponse.Fail($"Unexpected error: {ex.Message}",
                                NodeDeletionRejectionReason.Unknown),
                            o => o.ResponseFor(request));
                    }
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
            .Select(v => Observable.FromAsync(token => v.ValidateAsync(context, token)))
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
    /// nodes from each handler are persisted via <c>IMeshStorage</c> wrapped in
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

        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();
        var handlers = hub.ServiceProvider.GetServices<INodePostCreationHandler>()
            .Where(h => h.NodeType.Equals(node.NodeType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (handlers.Count == 0)
            return Observable.Empty<System.Reactive.Unit>();

        // For each matching handler: invoke HandleAsync (logged-and-swallowed), then
        // persist any additional nodes it returns. Sequentially via Concat to preserve
        // the original order's side-effect dependencies.
        return handlers
            .Select(handler =>
            {
                var handleObs = Observable.FromAsync(token => handler.HandleAsync(node, createdBy, token))
                    .Catch<System.Reactive.Unit, Exception>(ex =>
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
                    .Select(extra => persistence.SaveNode(extra with { State = MeshNodeState.Active })
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
    /// Issues a storage-level delete of the given path and posts the appropriate
    /// DeleteNodeResponse on completion. Used by the leaf and "all children deleted"
    /// branches of HandleDeleteNodeRequest. We CANNOT use IMeshService.DeleteNode here
    /// because that posts DeleteNodeRequest which routes back to this same handler for
    /// the same path and recurses forever. The storage-level call is fine because the
    /// caller has already passed RunDeletionValidatorsObs.
    /// </summary>
    private static void DeleteSelfFromStorage(
        IMessageHub hub,
        string path,
        DeleteNodeRequest capturedRequest,
        IMessageDelivery<DeleteNodeRequest> request,
        IMeshStorage persistence,
        ILogger logger)
    {
        // Post the response AFTER the storage delete actually commits so callers see a
        // consistent view: an awaited DeleteNode returns only once the node is gone from
        // persistence. Without this, race conditions occur — e.g. tests (and UI flows)
        // that query right after the delete can still observe the pre-delete node.
        //
        // The previous "reply first" approach guarded against Orleans/monolith hub
        // teardown during self-deletion. HandleDeleteNodeRequest runs on the mesh hub,
        // and a child-node delete does not tear down that hub — so the teardown concern
        // does not apply here. If a true self-teardown case emerges we post Fail from
        // OnError and the caller still unblocks.
        persistence.DeleteNode(path, recursive: false)
            .Subscribe(
                _ =>
                {
                    hub.Post(DeleteNodeResponse.Ok(), o => o.ResponseFor(request));
                    hub.ServiceProvider.GetService<IMeshChangeFeed>()
                        ?.Publish(MeshChangeEvent.Deleted(path));

                    // Dispose the grain at the deleted address so a subsequent recreate at
                    // the same path doesn't keep the old node's HubConfiguration. Without
                    // this, delete+create with a different nodeType leaves the grain bound
                    // to the previous nodeType's config until the next idle deactivation.
                    hub.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

                    logger.LogInformation(
                        "Node deleted at {Path} by {DeletedBy}",
                        path, capturedRequest.DeletedBy ?? "system");
                },
                ex =>
                {
                    logger.LogError(ex, "Storage delete failed for {Path}", path);
                    hub.Post(
                        DeleteNodeResponse.Fail($"Storage delete failed: {ex.Message}",
                            NodeDeletionRejectionReason.Unknown),
                        o => o.ResponseFor(request));
                });
    }

    /// <summary>
    /// Sync-friendly observable variant of the deletion-validator runner. Iterates
    /// validators sequentially via <c>Concat</c> (preserves short-circuit semantics —
    /// stops at the first failure); emits the first failure as a tuple or <c>null</c>
    /// if all pass. No <c>await</c>.
    /// </summary>
    private static IObservable<(string? ErrorMessage, NodeDeletionRejectionReason Reason)?> RunDeletionValidatorsObs(
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
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Delete))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, NodeDeletionRejectionReason)?>(null);

        return validators
            .Select(v => Observable.FromAsync(token => v.ValidateAsync(context, token)))
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
    /// Fully synchronous handler — returns <see cref="IMessageDelivery"/>, never <see cref="Task"/>.
    /// All hub-backed work goes through Post + RegisterCallback; non-hub async work (catalog reads,
    /// persistence writes, validator runs) is wrapped in <c>Observable.FromAsync</c> and composed
    /// via <c>Subscribe</c>. The handler returns <c>request.Processed()</c> immediately so the hub
    /// scheduler is never blocked. See <c>Doc/Architecture/AsynchronousCalls</c>.
    ///
    /// The terminal step (sending UpdateNodeResponse.Ok / Fail) is performed inside the deepest
    /// callback of the chain, so the response is only emitted once the workspace has acked the
    /// underlying DataChangeRequest — fixes the 2026-04-14 cached-display bug where Ok went out
    /// before the live workspace observed the change and Blazor views kept rendering stale content.
    /// </summary>
    private static IMessageDelivery HandleUpdateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<UpdateNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshNode>>();

        var updateRequest = request.Message;

        // Identity resolution: if no explicit UpdatedBy, use AccessContext identity
        if (string.IsNullOrEmpty(updateRequest.UpdatedBy)
            && request.AccessContext?.ObjectId is { Length: > 0 } updateSenderId)
            updateRequest = updateRequest with { UpdatedBy = updateSenderId };

        var capturedRequest = updateRequest;
        var updatedNode = updateRequest.Node;
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
        var meshConfig = hub.ServiceProvider.GetService<IMeshCatalog>()?.Configuration;

        // Read existing from our own workspace when the hub is backed by MeshDataSource —
        // the workspace's replay-cached MeshNode stream already has the live node. Fall back
        // to persistence when the hub doesn't expose the stream (some test/infra configs).
        // No catalog usage either way.
        var nodeStream = hub.GetWorkspace()?.GetStream<MeshNode>();
        var existingNodeObs = nodeStream != null
            ? nodeStream
                .Take(1)
                .Select(nodes => nodes?.FirstOrDefault(n => n.Path == updatedNode.Path))
            : Observable.FromAsync(token => persistence.GetNodeAsync(updatedNode.Path, token));

        // Read existing → check NodeType → validate → persist → workspace ack → response.
        // Each step lives in a Subscribe callback; the handler returns synchronously below.
        existingNodeObs
            .SelectMany(existingNode =>
            {
                if (existingNode == null)
                {
                    hub.Post(
                        UpdateNodeResponse.Fail(
                            $"Node not found at path: {updatedNode.Path}",
                            NodeUpdateRejectionReason.NodeNotFound),
                        o => o.ResponseFor(request));
                    return Observable.Empty<MeshNode>();
                }

                if (!string.IsNullOrEmpty(existingNode.NodeType)
                    && !string.IsNullOrEmpty(updatedNode.NodeType)
                    && existingNode.NodeType != updatedNode.NodeType)
                {
                    hub.Post(
                        UpdateNodeResponse.Fail(
                            $"Cannot change NodeType from '{existingNode.NodeType}' to '{updatedNode.NodeType}'",
                            NodeUpdateRejectionReason.InvalidNodeType),
                        o => o.ResponseFor(request));
                    return Observable.Empty<MeshNode>();
                }

                return RunUpdateValidatorsObs(hub, existingNode, updatedNode, capturedRequest)
                    .SelectMany(validationError =>
                    {
                        if (validationError != null)
                        {
                            logger.LogWarning(
                                "Validator rejected node update at {Path}: {Error}",
                                updatedNode.Path, validationError.Value.ErrorMessage);
                            hub.Post(
                                UpdateNodeResponse.Fail(
                                    validationError.Value.ErrorMessage ?? "Validation failed",
                                    validationError.Value.Reason),
                                o => o.ResponseFor(request));
                            return Observable.Empty<MeshNode>();
                        }

                        var nodeToSave = updatedNode with
                        {
                            State = updatedNode.State != default ? updatedNode.State : existingNode.State,
                            HubConfiguration = existingNode.HubConfiguration
                        };

                        return persistence.SaveNode(nodeToSave);
                    });
            })
            .Subscribe(
                savedNode =>
                {
                    hub.ServiceProvider.GetService<IMeshChangeFeed>()
                        ?.Publish(MeshChangeEvent.Updated(savedNode));

                    // Version history — fire-and-forget Subscribe; failures are non-critical.
                    if (meshConfig != null && !meshConfig.IsSatelliteNodeType(savedNode.NodeType))
                    {
                        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
                        if (versionQuery != null)
                        {
                            Observable.FromAsync(token =>
                                    versionQuery.WriteVersionAsync(savedNode, hub.JsonSerializerOptions, token))
                                .Subscribe(
                                    _ => { },
                                    ex => logger.LogWarning(ex,
                                        "Version history write failed at {Path} (non-critical)",
                                        savedNode.Path));
                        }
                    }

                    logger.LogInformation(
                        "Node persisted at {Path} by {UpdatedBy}",
                        savedNode.Path, capturedRequest.UpdatedBy ?? "system");

                    // Workspace fan-out is fire-and-forget — the target hub may or may not
                    // have a MeshNode data source mapped, and in some topologies no handler
                    // at all (returns DeliveryFailure). Either outcome is fine: persistence
                    // already succeeded and the PathResolver cache was invalidated via the
                    // MeshChangeFeed.Publish call above. Any subscribed workspace stream will
                    // receive the update; the rest is best-effort.
                    hub.Post(DataChangeRequest.Update([savedNode]),
                        o => o.WithTarget(new Address(savedNode.Path)));

                    hub.Post(UpdateNodeResponse.Ok(savedNode), o => o.ResponseFor(request));
                },
                ex =>
                {
                    if (ex is InvalidOperationException)
                    {
                        logger.LogWarning(ex, "Node update failed for path {Path}", updatedNode.Path);
                        hub.Post(
                            UpdateNodeResponse.Fail(ex.Message, NodeUpdateRejectionReason.ValidationFailed),
                            o => o.ResponseFor(request));
                    }
                    else
                    {
                        logger.LogError(ex, "Unexpected error during node update at {Path}", updatedNode.Path);
                        hub.Post(
                            UpdateNodeResponse.Fail($"Unexpected error: {ex.Message}",
                                NodeUpdateRejectionReason.Unknown),
                            o => o.ResponseFor(request));
                    }
                });

        return request.Processed();
    }

    /// <summary>
    /// Runs all update validators from DI using the unified INodeValidator interface.
    /// </summary>
    /// <summary>
    /// Sync-friendly observable variant of the unified update-validator runner.
    /// Iterates validators in order via <c>Concat</c> (preserves short-circuit semantics —
    /// the chain stops at the first failure) and returns either a tuple describing the
    /// failure or <c>null</c> if all validators pass. No <c>await</c>; consumers compose
    /// via <c>SelectMany</c> on a <c>Subscribe</c>-based chain.
    /// </summary>
    private static IObservable<(string? ErrorMessage, NodeUpdateRejectionReason Reason)?> RunUpdateValidatorsObs(
        IMessageHub hub,
        MeshNode existingNode,
        MeshNode updatedNode,
        UpdateNodeRequest request)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = updatedNode,
            ExistingNode = existingNode,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Update))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, NodeUpdateRejectionReason)?>(null);

        // Run validators sequentially via Concat; emit the first failure (or null at the end).
        return validators
            .Select(v => Observable.FromAsync(token => v.ValidateAsync(context, token)))
            .Concat()
            .Where(result => !result.IsValid)
            .Select(result =>
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeUpdateRejectionReason.NodeNotFound,
                    NodeRejectionReason.InvalidNodeType => NodeUpdateRejectionReason.InvalidNodeType,
                    NodeRejectionReason.ConcurrencyConflict => NodeUpdateRejectionReason.ConcurrencyConflict,
                    NodeRejectionReason.Unauthorized => NodeUpdateRejectionReason.ValidationFailed,
                    _ => NodeUpdateRejectionReason.ValidationFailed
                };
                return ((string?, NodeUpdateRejectionReason)?)(result.ErrorMessage, reason);
            })
            .Take(1)
            .DefaultIfEmpty(null);
    }

    private static async Task<IMessageDelivery> HandleMoveNodeRequest(
        IMessageHub hub,
        IMessageDelivery<MoveNodeRequest> request,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();

        if (persistence == null)
        {
            hub.Post(
                MoveNodeResponse.Fail("IMeshStorage not available", NodeMoveRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        try
        {
            var moveRequest = request.Message;

            // 1. Check source exists
            var sourceNode = await persistence.GetNodeAsync(moveRequest.SourcePath, ct);
            if (sourceNode == null)
            {
                hub.Post(
                    MoveNodeResponse.Fail(
                        $"Source node not found at path: {moveRequest.SourcePath}",
                        NodeMoveRejectionReason.SourceNotFound),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 2. Check target does not exist
            if (await persistence.ExistsAsync(moveRequest.TargetPath, ct))
            {
                hub.Post(
                    MoveNodeResponse.Fail(
                        $"Target path already exists: {moveRequest.TargetPath}",
                        NodeMoveRejectionReason.TargetAlreadyExists),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 3. Run validators
            var validationError = await RunMoveValidatorsAsync(hub, sourceNode, moveRequest, ct);
            if (validationError != null)
            {
                logger.LogWarning("Validator rejected node move from {Source} to {Target}: {Error}",
                    moveRequest.SourcePath, moveRequest.TargetPath, validationError.Value.ErrorMessage);

                hub.Post(
                    MoveNodeResponse.Fail(
                        validationError.Value.ErrorMessage ?? "Validation failed",
                        validationError.Value.Reason),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 4. Move the node — subscribe and post response in the callback.
            persistence.MoveNode(moveRequest.SourcePath, moveRequest.TargetPath)
                .Subscribe(
                    movedNode =>
                    {
                        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
                        changeFeed?.Publish(MeshChangeEvent.Deleted(moveRequest.SourcePath));
                        changeFeed?.Publish(MeshChangeEvent.Created(movedNode));
                        hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request));
                        logger.LogInformation("Node moved from {Source} to {Target}",
                            moveRequest.SourcePath, moveRequest.TargetPath);
                    },
                    ex =>
                    {
                        logger.LogError(ex, "Error moving node from {Source} to {Target}",
                            moveRequest.SourcePath, moveRequest.TargetPath);
                        hub.Post(
                            MoveNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                            o => o.ResponseFor(request));
                    });

            return request.Processed();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error moving node from {Source} to {Target}",
                request.Message.SourcePath, request.Message.TargetPath);
            hub.Post(
                MoveNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    private static async Task<(string? ErrorMessage, NodeMoveRejectionReason Reason)?> RunMoveValidatorsAsync(
        IMessageHub hub,
        MeshNode node,
        MoveNodeRequest request,
        CancellationToken ct)
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
        foreach (var validator in validators)
        {
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Move))
            {
                continue;
            }

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeMoveRejectionReason.SourceNotFound,
                    NodeRejectionReason.Unauthorized => NodeMoveRejectionReason.ValidationFailed,
                    _ => NodeMoveRejectionReason.ValidationFailed
                };
                return (result.ErrorMessage, reason);
            }
        }

        return null;
    }
}
