using System.Collections.Immutable;
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
        config.TypeRegistry.WithType(typeof(CopyNodeRequest), nameof(CopyNodeRequest));
        config.TypeRegistry.WithType(typeof(CopyNodeResponse), nameof(CopyNodeResponse));
        config.TypeRegistry.WithType(typeof(NodeCopyRejectionReason), nameof(NodeCopyRejectionReason));
        config.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));

        // Per-node pre-flight delete validation. Posted by HandleDeleteNodeRequest to each
        // node in the subtree. Owning hub runs local INodeValidators + domain rules.
        config.TypeRegistry.WithType(typeof(ValidateDeleteRequest), nameof(ValidateDeleteRequest));
        config.TypeRegistry.WithType(typeof(ValidateDeleteResponse), nameof(ValidateDeleteResponse));

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

    /// <summary>
    /// Registers handlers for mesh node operations.
    /// </summary>
    public static MessageHubConfiguration WithNodeOperationHandlers(this MessageHubConfiguration config)
    {
        return config
            .AddMeshTypes()
            .WithHandler<CreateNodeRequest>(HandleCreateNodeRequest)
            .WithHandler<DeleteNodeRequest>(HandleDeleteNodeRequest)
            .WithHandler<ValidateDeleteRequest>(HandleValidateDeleteRequest)
            .WithHandler<UpdateNodeRequest>(HandleUpdateNodeRequest)
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
                            };

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

                    // Run post-creation handlers and post the terminal response. On every
                    // terminal path (success/error) a response MUST go out so the caller never
                    // waits forever. The node is already persisted — but if a post-creation
                    // handler errored, surface that as a Fail so the caller can react (don't
                    // silently lie with Ok).
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
    /// <summary>
    /// Central delete orchestrator. Four phases:
    /// <list type="number">
    /// <item><description><b>Collect.</b> Root + (recursive) descendants via
    /// <see cref="IMeshStorage"/> (storage adapter — no workspace/type-source detour).</description></item>
    /// <item><description><b>Permission.</b> Check <see cref="Permission.Delete"/> for
    /// every path via <see cref="ISecurityService"/>. Any denial fails the whole op
    /// with the full list of denied paths in the <see cref="ActivityLog"/>.</description></item>
    /// <item><description><b>Validate.</b> Run <see cref="INodeValidator"/> chain for
    /// every node. Errors block; warnings block unless
    /// <see cref="DeleteNodeRequest.ConfirmWarnings"/> is set. Custom hubs that want
    /// cross-hub validation can additionally post <see cref="ValidateDeleteRequest"/>
    /// — there's a default handler registered by <see cref="WithNodeOperationHandlers"/>
    /// on every hub that opts in.</description></item>
    /// <item><description><b>Commit.</b> Bulk-delete via <see cref="IStorageService"/>
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
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
        var storage = hub.ServiceProvider.GetRequiredService<IStorageService>();
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var meshHub = ResolveMeshHub(hub);

        var deleteRequest = request.Message;
        if (string.IsNullOrEmpty(deleteRequest.DeletedBy)
            && request.AccessContext?.ObjectId is { Length: > 0 } deleteSenderId)
            deleteRequest = deleteRequest with { DeletedBy = deleteSenderId };

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

        CollectNodesForDelete(persistence, path, capturedRequest.Recursive, opts.Timeout, logger)
            .SelectMany(collected =>
            {
                if (collected.Root == null)
                {
                    logger.LogDebug("[DeleteNode] not-found path={Path}", path);
                    PostFailed(
                        $"Node not found at path: {path}",
                        NodeDeletionRejectionReason.NodeNotFound,
                        [new LogMessage($"Node not found at path: {path}", LogLevel.Error)]);
                    return Observable.Empty<System.Reactive.Unit>();
                }

                if (!capturedRequest.Recursive && collected.HasUnlistedChildren)
                {
                    logger.LogDebug("[DeleteNode] has-children path={Path}", path);
                    var msg = $"Node at '{path}' has children. Use recursive delete to remove it.";
                    PostFailed(msg, NodeDeletionRejectionReason.HasChildren,
                        [new LogMessage(msg, LogLevel.Error)]);
                    return Observable.Empty<System.Reactive.Unit>();
                }

                var toDelete = collected.ToDelete;

                return CheckDeletePermissionsForAll(securityService, accessService, toDelete, logger)
                    .SelectMany(deniedPaths =>
                    {
                        if (deniedPaths.Count > 0)
                        {
                            logger.LogWarning(
                                "[DeleteNode] permission-denied path={Path} denied=[{Denied}]",
                                path, string.Join(",", deniedPaths));
                            var msgs = deniedPaths
                                .Select(p => new LogMessage(
                                    $"Delete permission denied for '{p}'", LogLevel.Error))
                                .ToImmutableList();
                            var primary = deniedPaths[0];
                            PostFailed(
                                deniedPaths.Count == 1
                                    ? $"Delete permission denied for '{primary}'"
                                    : $"Delete permission denied for {deniedPaths.Count} nodes (first: '{primary}')",
                                NodeDeletionRejectionReason.Unauthorized,
                                msgs,
                                toDelete.Select(n => n.Path).ToImmutableList());
                            return Observable.Empty<System.Reactive.Unit>();
                        }

                        return ValidateAllLocal(hub, toDelete, capturedRequest, logger)
                            .SelectMany(validations =>
                            {
                                var errorEntries = validations
                                    .SelectMany(v => v.Errors.Select(e => (v.Path, Msg: e)))
                                    .ToImmutableList();
                                var warningEntries = validations
                                    .SelectMany(v => v.Warnings.Select(w => (v.Path, Msg: w)))
                                    .ToImmutableList();

                                if (!errorEntries.IsEmpty)
                                {
                                    logger.LogWarning(
                                        "[DeleteNode] validator-rejected path={Path} errors={Count}",
                                        path, errorEntries.Count);
                                    var msgs = errorEntries
                                        .Select(e => new LogMessage(
                                            $"Cannot delete '{e.Path}': {e.Msg}", LogLevel.Error))
                                        .ToImmutableList();
                                    var primary = errorEntries[0];
                                    PostFailed(
                                        errorEntries.Count == 1
                                            ? $"Cannot delete '{primary.Path}': {primary.Msg}"
                                            : $"Cannot delete '{path}': {errorEntries.Count} validation errors (first: '{primary.Path}' — {primary.Msg})",
                                        NodeDeletionRejectionReason.ValidationFailed,
                                        msgs,
                                        toDelete.Select(n => n.Path).ToImmutableList());
                                    return Observable.Empty<System.Reactive.Unit>();
                                }

                                if (!warningEntries.IsEmpty && !capturedRequest.ConfirmWarnings)
                                {
                                    logger.LogInformation(
                                        "[DeleteNode] warnings-require-confirmation path={Path} warnings={Count}",
                                        path, warningEntries.Count);
                                    var msgs = warningEntries
                                        .Select(w => new LogMessage(
                                            $"'{w.Path}': {w.Msg}", LogLevel.Warning))
                                        .ToImmutableList();
                                    var primary = warningEntries[0];
                                    PostFailed(
                                        $"Delete of '{path}' has {warningEntries.Count} warning(s) (first: '{primary.Path}' — {primary.Msg}). Set ConfirmWarnings=true to proceed.",
                                        NodeDeletionRejectionReason.WarningsRequireConfirmation,
                                        msgs,
                                        toDelete.Select(n => n.Path).ToImmutableList());
                                    return Observable.Empty<System.Reactive.Unit>();
                                }

                                logger.LogDebug(
                                    "[DeleteNode] committing path={Path} count={Count}",
                                    path, toDelete.Count);
                                return BulkDeleteViaStorage(storage, toDelete, opts.Timeout, logger)
                                    .Do(_ =>
                                    {
                                        var warningMsgs = warningEntries
                                            .Select(w => new LogMessage(
                                                $"'{w.Path}': {w.Msg}", LogLevel.Warning))
                                            .ToImmutableList();

                                        var okLog = baseActivity with
                                        {
                                            Messages = warningMsgs,
                                            AffectedPaths = toDelete.Select(n => n.Path).ToImmutableList(),
                                            End = DateTime.UtcNow,
                                            Status = warningMsgs.IsEmpty
                                                ? ActivityStatus.Succeeded
                                                : ActivityStatus.Warning
                                        };

                                        logger.LogInformation(
                                            "[DeleteNode] succeeded path={Path} count={Count} warnings={Warnings} by={DeletedBy}",
                                            path, toDelete.Count, warningMsgs.Count,
                                            capturedRequest.DeletedBy ?? "system");

                                        var changeFeed = meshHub.ServiceProvider.GetService<IMeshChangeFeed>();
                                        foreach (var node in toDelete)
                                            changeFeed?.Publish(MeshChangeEvent.Deleted(node.Path));

                                        meshHub.Post(
                                            DeleteNodeResponse.Ok() with { Log = okLog },
                                            o => o
                                                .WithTarget(request.Sender)
                                                .WithProperty(PostOptions.RequestId, request.Id));

                                        foreach (var node in toDelete)
                                            meshHub.Post(
                                                new DisposeRequest(),
                                                o => o.WithTarget(new Address(node.Path)));
                                    });
                            });
                    });
            })
            .Subscribe(
                _ => { },
                ex =>
                {
                    var isTimeout = ex is TimeoutException;
                    logger.LogError(ex, "[DeleteNode] {Kind} path={Path}",
                        isTimeout ? "timeout" : "unexpected", path);
                    PostFailed(
                        isTimeout
                            ? $"Delete of '{path}' exceeded {opts.Timeout.TotalSeconds:0}s timeout"
                            : $"Unexpected error: {ex.Message}",
                        isTimeout
                            ? NodeDeletionRejectionReason.Unknown
                            : (ex is InvalidOperationException
                                ? NodeDeletionRejectionReason.ValidationFailed
                                : NodeDeletionRejectionReason.Unknown),
                        [new LogMessage(ex.Message, LogLevel.Error)]);
                });

        return request.Processed();
    }

    /// <summary>
    /// Phase 1 — fetch root + (recursive) descendants via <see cref="IMeshStorage"/>.
    /// IAsyncEnumerable is the native shape of storage iteration; we bridge to an
    /// observable at the boundary with <c>Observable.FromAsync</c>. Returns bottom-up
    /// order (deepest first) so bulk delete processes children before parents.
    /// </summary>
    private static IObservable<(MeshNode? Root, IReadOnlyList<MeshNode> ToDelete, bool HasUnlistedChildren)>
        CollectNodesForDelete(
            IMeshStorage persistence,
            string path,
            bool recursive,
            TimeSpan timeout,
            ILogger logger)
    {
        return Observable.FromAsync<(MeshNode? Root, IReadOnlyList<MeshNode> ToDelete, bool HasUnlistedChildren)>(async ct =>
        {
            var root = await persistence.GetNodeAsync(path, ct);
            if (root == null)
                return (null, Array.Empty<MeshNode>(), false);

            if (!recursive)
            {
                bool anyChildren = false;
                await foreach (var _ in persistence.GetChildrenAsync(path).WithCancellation(ct))
                {
                    anyChildren = true;
                    break;
                }
                return (root, new[] { root }, anyChildren);
            }

            var descendants = new List<MeshNode>();
            await foreach (var d in persistence.GetAllDescendantsAsync(path).WithCancellation(ct))
                descendants.Add(d);

            var all = descendants.Append(root)
                .OrderByDescending(n => n.Path.Count(c => c == '/'))
                .ThenByDescending(n => n.Path, StringComparer.Ordinal)
                .ToImmutableList();

            logger.LogDebug("[DeleteNode] collected path={Path} total={Count}", path, all.Count);
            return (root, (IReadOnlyList<MeshNode>)all, false);
        })
        .Timeout(timeout);
    }

    /// <summary>
    /// Phase 2 — check <see cref="Permission.Delete"/> for every node's primary path.
    /// </summary>
    private static IObservable<IReadOnlyList<string>> CheckDeletePermissionsForAll(
        ISecurityService? securityService,
        AccessService? accessService,
        IReadOnlyList<MeshNode> nodes,
        ILogger logger)
    {
        if (securityService == null || nodes.Count == 0)
            return Observable.Return<IReadOnlyList<string>>(Array.Empty<string>());

        var userId = accessService?.Context?.ObjectId
                     ?? accessService?.CircuitContext?.ObjectId
                     ?? WellKnownUsers.Anonymous;

        return Observable.FromAsync<IReadOnlyList<string>>(async ct =>
        {
            var denied = new List<string>();
            foreach (var node in nodes)
            {
                var pathToCheck = node.MainNode ?? node.Path;
                var perms = await securityService.GetEffectivePermissionsAsync(pathToCheck, userId, ct);
                if (!perms.HasFlag(Permission.Delete))
                {
                    denied.Add(node.Path);
                    logger.LogDebug(
                        "[DeleteNode] permission-denied for {User} on {Path} (effective={Perms})",
                        userId, node.Path, perms);
                }
            }
            return (IReadOnlyList<string>)denied;
        });
    }

    /// <summary>
    /// Phase 3 — run the hub's <see cref="INodeValidator"/> chain for every node
    /// locally. Collects errors across all nodes (doesn't short-circuit on first)
    /// so the caller's ActivityLog shows the complete picture.
    /// </summary>
    private static IObservable<IReadOnlyList<(string Path, ImmutableList<string> Errors, ImmutableList<string> Warnings)>>
        ValidateAllLocal(
            IMessageHub hub,
            IReadOnlyList<MeshNode> nodes,
            DeleteNodeRequest request,
            ILogger logger)
    {
        if (nodes.Count == 0)
            return Observable.Return<IReadOnlyList<(string, ImmutableList<string>, ImmutableList<string>)>>(
                Array.Empty<(string, ImmutableList<string>, ImmutableList<string>)>());

        return nodes
            .Select(n => RunDeletionValidatorsWithWarningsObs(hub, n, request)
                .Select(r => (
                    Path: n.Path,
                    Errors: r.Error is null
                        ? ImmutableList<string>.Empty
                        : ImmutableList.Create(r.Error),
                    Warnings: r.Warnings)))
            .Concat()
            .ToList()
            .Select(results => (IReadOnlyList<(string, ImmutableList<string>, ImmutableList<string>)>)
                results.ToImmutableList());
    }

    /// <summary>
    /// Phase 4 — bulk-delete every path by calling <see cref="IStorageService"/> directly.
    /// Bottom-up order; single timeout covers the full bulk so a stuck adapter fails the
    /// whole op rather than leaving a partial deletion.
    /// </summary>
    private static IObservable<System.Reactive.Unit> BulkDeleteViaStorage(
        IStorageService storage,
        IReadOnlyList<MeshNode> nodesBottomUp,
        TimeSpan timeout,
        ILogger logger)
    {
        if (nodesBottomUp.Count == 0)
            return Observable.Return(System.Reactive.Unit.Default);

        return Observable.FromAsync(async ct =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            foreach (var node in nodesBottomUp)
            {
                logger.LogDebug("[DeleteNode] storage.DeleteNode {Path}", node.Path);
                await storage.DeleteNodeAsync(node.Path, recursive: false, linked.Token);
            }
            return System.Reactive.Unit.Default;
        });
    }

    /// <summary>
    /// Default handler for <see cref="ValidateDeleteRequest"/>. Fetches the target node
    /// (via <see cref="IMeshStorage"/>), runs the hub's registered
    /// <see cref="INodeValidator"/> chain for <see cref="NodeOperation.Delete"/>, and
    /// returns the first validator failure as an Error (empty Warnings in the default
    /// implementation — custom hubs can override this handler to emit Warnings).
    /// </summary>
    private static IMessageDelivery HandleValidateDeleteRequest(
        IMessageHub hub,
        IMessageDelivery<ValidateDeleteRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshNode>>();
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
        var opts = hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions();
        var path = request.Message.Path;

        var existingNodeObs = Observable.FromAsync(token => persistence.GetNodeAsync(path, token));

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

                return RunDeletionValidatorsObs(hub, node, proxyDeleteRequest)
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
    /// Walks up <see cref="MessageHubConfiguration.ParentHub"/> to the topmost hub —
    /// the mesh hub, which is never torn down by its own operations and is therefore
    /// the stable place to post terminal delete replies + DisposeRequests from.
    /// </summary>
    private static IMessageHub ResolveMeshHub(IMessageHub hub)
    {
        var current = hub;
        while (current.Configuration.ParentHub is { } parent && !ReferenceEquals(parent, current))
            current = parent;
        return current;
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
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v.SupportedOperations.Count == 0
                        || v.SupportedOperations.Contains(NodeOperation.Delete))
            .ToList();

        if (validators.Count == 0)
            return Observable.Return<(string?, ImmutableList<string>)>((null, ImmutableList<string>.Empty));

        return validators
            .Select(v => Observable.FromAsync(token => v.ValidateAsync(context, token)))
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
        var meshConfig = hub.ServiceProvider.GetService<IMeshCatalog>()?.Configuration;
        var workspace = hub.GetWorkspace();

        logger.LogDebug("[UpdateNode] start hub={Hub}, target={Target}, sender={Sender}, deliveryId={DeliveryId}",
            hub.Address, updatedNode.Path, request.Sender, request.Id);

        // Forward to the owning per-node hub when this hub doesn't own the path.
        // Only the per-node hub (registered via AddMeshDataSource) has a
        // MeshNodeReference reducer. The mesh hub forwards and relays the response.
        // Compare via Address.Path (segments only) — ToString() / ToFullString()
        // include "~host" for hosted hubs, which would always mismatch a bare path.
        if (!string.Equals(hub.Address.Path, updatedNode.Path, StringComparison.Ordinal))
        {
            logger.LogInformation("[UpdateNode] forwarding from {Hub} (path={HubPath}) to per-node hub {Target}",
                hub.Address, hub.Address.Path, updatedNode.Path);
            var forwarded = hub.Post(capturedRequest,
                o => o.WithTarget(new Address(updatedNode.Path)))!;
            hub.RegisterCallback(forwarded, d =>
            {
                hub.Post(((IMessageDelivery<UpdateNodeResponse>)d).Message,
                    o => o.ResponseFor(request));
                return d;
            });
            return request.Processed();
        }

        // We own the path — read our own MeshNodeReference stream.
        ISynchronizationStream<MeshNode>? ownStream;
        try
        {
            ownStream = workspace.GetStream(new MeshNodeReference());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateNode] hub {Hub} has no MeshNodeReference stream for {Path}",
                hub.Address, updatedNode.Path);
            ownStream = null;
        }

        if (ownStream == null)
        {
            hub.Post(UpdateNodeResponse.Fail(
                $"Node not found at path: {updatedNode.Path}",
                NodeUpdateRejectionReason.NodeNotFound),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var existingNodeObs = ownStream
            .Select(change => (MeshNode?)change.Value)
            .Where(n => n != null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(ex =>
            {
                logger.LogWarning(ex, "[UpdateNode] read own node failed for {Path}", updatedNode.Path);
                return Observable.Return<MeshNode?>(null);
            });

        // Read existing → check NodeType → validate → persist → workspace ack → response.
        // Each step lives in a Subscribe callback; the handler returns synchronously below.
        existingNodeObs
            .SelectMany(existingNode =>
            {
                logger.LogInformation("[UpdateNode] step=read-complete path={Path} found={Found}",
                    updatedNode.Path, existingNode != null);
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

                        // Preserve creation stamps, refresh modification stamps.
                        var nodeToSave = updatedNode with
                        {
                            State = updatedNode.State != default ? updatedNode.State : existingNode.State,
                            HubConfiguration = existingNode.HubConfiguration,
                            CreatedDate = existingNode.CreatedDate != default ? existingNode.CreatedDate : updatedNode.CreatedDate,
                            CreatedBy = existingNode.CreatedBy ?? updatedNode.CreatedBy,
                            LastModified = DateTimeOffset.UtcNow,
                            LastModifiedBy = capturedRequest.UpdatedBy
                                ?? updatedNode.LastModifiedBy
                                ?? existingNode.LastModifiedBy
                        };

                        // Use the hub's data layer as the synchronization point: post a
                        // DataChangeRequest to ourselves (own per-node hub) and wait for the
                        // DataChangeResponse — that response only fires after the activity
                        // completes, which includes the persister flush. No async/await; pure
                        // hub.Post + RegisterCallback. The response.Ok we send the caller is
                        // chained off DataChangeResponse so the caller's read-after-write sees
                        // freshly persisted data.
                        return Observable.Return(nodeToSave);
                    });
            })
            .Subscribe(
                savedNode =>
                {
                    // Direct workspace write — the data source's MeshNode partition
                    // stream is the source of truth for the per-node hub's
                    // MeshNodeReference reducer. UpdateMeshNode posts UpdateStreamRequest
                    // synchronously into THIS hub's queue; subsequent messages on this
                    // hub (including a GetDataRequest the caller sends after our
                    // UpdateNodeResponse.Ok) are processed AFTER the stream tick, so
                    // read-after-write is consistent.
                    workspace.UpdateMeshNode(_ => savedNode, nodePath: savedNode.Path);

                    hub.ServiceProvider.GetService<IMeshChangeFeed>()
                        ?.Publish(MeshChangeEvent.Updated(savedNode));

                    // Version history — fire-and-forget Subscribe; non-critical.
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

    /// <summary>
    /// Sync handler for MoveNodeRequest — implements Move as Copy(IncludeSatellites=true) +
    /// DeleteNode(IncludeSatellites=true). The handler orchestrates the two service calls;
    /// the actual copy logic lives in <see cref="HandleCopyNodeRequest"/>.
    /// Response is posted from the mesh hub (the source hub may be deleted at this point).
    /// </summary>
    private static IMessageDelivery HandleMoveNodeRequest(
        IMessageHub hub,
        IMessageDelivery<MoveNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var moveRequest = request.Message;
        var meshService = hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var sourcePath = moveRequest.SourcePath;
        var targetPath = moveRequest.TargetPath;

        // Move = Copy (with satellites + descendants) → Delete source (with satellites).
        // Delete only fires after Copy succeeds (SelectMany short-circuits on copy error).
        meshService.CopyNode(sourcePath, targetPath, includeDescendants: true, includeSatellites: true)
            .SelectMany(copied =>
                meshService.DeleteNode(sourcePath)
                    .Select(_ => copied))
            .Subscribe(
                movedNode =>
                {
                    var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
                    changeFeed?.Publish(MeshChangeEvent.Deleted(sourcePath));
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
    /// <c>ObserveQuery</c> (initial set of source + subtree) → <c>Select(CreateNode)</c>
    /// for each, all in observable composition. No <c>await</c>, no persistence read,
    /// no remote MeshNodeReference subscription. Per <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private static IMessageDelivery HandleCopyNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CopyNodeRequest> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var copyRequest = request.Message;
        var meshService = hub.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var sourcePath = copyRequest.SourcePath;
        var targetPath = copyRequest.TargetPath;

        logger.LogDebug("[CopyNode] start source={Source} target={Target} (descendants={Desc} satellites={Sat})",
            sourcePath, targetPath, copyRequest.IncludeDescendants, copyRequest.IncludeSatellites);

        // Subtree query covers source + descendants + satellites (anything under sourcePath).
        // ObserveQuery's first emission is the initial result set; we Take(1) and project each
        // node into a CreateNode call at the new target path.
        meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
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
                // require all inserts to complete before the source is deleted.
                return meshService.CreateNode(RetargetNode(sourceNode, sourcePath, targetPath))
                    .SelectMany(rootCreated =>
                    {
                        if (others.Count == 0)
                            return Observable.Return<(MeshNode Root, int Desc, int Sat)>((rootCreated, descCount, satCount));
                        return others.ToObservable()
                            .Select(n => RetargetNode(n, sourcePath, targetPath))
                            .SelectMany(retargeted => meshService.CreateNode(retargeted))
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
            .Select(v => Observable.FromAsync(ct => v.ValidateAsync(context, ct)))
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
