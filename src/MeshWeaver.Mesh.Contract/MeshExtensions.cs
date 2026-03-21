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
            .WithHandler<MoveNodeRequest>(HandleMoveNodeRequest);
    }

    private static async Task<IMessageDelivery> HandleCreateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CreateNodeRequest> request,
        CancellationToken ct)
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

        try
        {
            var createRequest = request.Message;

            // Identity resolution: if no explicit CreatedBy, use the sender's
            // AccessContext identity from the authenticated pipeline.
            if (string.IsNullOrEmpty(createRequest.CreatedBy)
                && request.AccessContext?.ObjectId is { Length: > 0 } senderId)
                createRequest = createRequest with { CreatedBy = senderId };

            var node = createRequest.Node;

            // 0. Validate path is not empty or whitespace
            if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Path))
            {
                hub.Post(
                    CreateNodeResponse.Fail("Node path and Id must not be empty", NodeCreationRejectionReason.ValidationFailed),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 1. Check if node already exists
            // Use persistence directly (not catalog.GetNodeAsync which auto-creates from templates)
            var existingNode = persistence != null
                ? await persistence.GetNodeAsync(node.Path, ct)
                : null;
            // Also check in-memory configuration for statically registered nodes
            if (existingNode == null && catalog.Configuration.Nodes.TryGetValue(node.Path, out var configNode))
                existingNode = configNode;
            if (existingNode != null)
            {
                // If existing node is Transient and request wants Active, this is a "confirm" operation
                if (existingNode.State == MeshNodeState.Transient && node.State == MeshNodeState.Active)
                {
                    // Merge request node with existing node (preserve NodeType, update content/properties)
                    var confirmedNode = existingNode with
                    {
                        State = MeshNodeState.Active,
                        Name = node.Name ?? existingNode.Name,
                        Icon = node.Icon ?? existingNode.Icon,
                        Category = node.Category ?? existingNode.Category,
                        Content = node.Content ?? existingNode.Content
                    };

                    // Save via persistence
                    if (persistence != null)
                    {
                        await persistence.SaveNodeAsync(confirmedNode, ct);
                    }

                    // Update workspace stream via DataChangeRequest so GetDataRequest returns updated state
                    // Post the change to ourselves to update the workspace
                    hub.Post(DataChangeRequest.Update([confirmedNode]), o => o.WithTarget(hub.Address));

                    // Run post-creation handlers (e.g. grant creator Admin role)
                    await RunPostCreationHandlersAsync(hub, confirmedNode, createRequest.CreatedBy, logger, ct);

                    hub.Post(CreateNodeResponse.Ok(confirmedNode), o => o.ResponseFor(request));
                    logger.LogInformation("Confirmed transient node at {Path}", confirmedNode.Path);
                    return request.Processed();
                }

                // Node exists and is not a Transient->Active confirmation
                hub.Post(
                    CreateNodeResponse.Fail(
                        $"Node already exists at path: {node.Path}",
                        NodeCreationRejectionReason.NodeAlreadyExists),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 1b. Auto-set MainNode for satellite types before validation
            // so that SatelliteAccessRule can delegate to the parent node.
            if (!string.IsNullOrEmpty(node.NodeType)
                && !string.IsNullOrEmpty(node.Namespace)
                && catalog.Configuration.IsSatelliteNodeType(node.NodeType)
                && node.MainNode == node.Path) // still at default (self-referencing)
            {
                node = node with { MainNode = node.Namespace };
            }

            // 2. Run validators (global + NodeType-specific)
            var validationError = await RunCreationValidatorsAsync(hub, catalog, node, createRequest, ct);
            if (validationError != null)
            {
                logger.LogWarning("Validator rejected node creation at {Path}: {Error}",
                    node.Path, validationError.Value.ErrorMessage);

                hub.Post(
                    CreateNodeResponse.Fail(
                        validationError.Value.ErrorMessage ?? "Validation failed",
                        validationError.Value.Reason),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 3. Validate NodeType exists (if specified)
            if (!string.IsNullOrEmpty(node.NodeType))
            {
                var nodeTypeExists = catalog.Configuration.Nodes.ContainsKey(node.NodeType)
                    || (persistence != null && await persistence.ExistsAsync(node.NodeType, ct));
                if (!nodeTypeExists)
                {
                    hub.Post(
                        CreateNodeResponse.Fail($"NodeType '{node.NodeType}' is not registered", NodeCreationRejectionReason.InvalidNodeType),
                        o => o.ResponseFor(request));
                    return request.Processed();
                }
            }

            // 4. Create node with Active state (validated, ready to persist)
            var newNode = node with { State = MeshNodeState.Active };

            // 4a. MainNode already set in step 1b (before validation)

            // 5. Enrich with HubConfiguration based on NodeType
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService != null)
            {
                newNode = await nodeTypeService.EnrichWithNodeTypeAsync(newNode, ct);
            }

            // 6. Save to persistence
            if (persistence != null)
            {
                newNode = await persistence.SaveNodeAsync(newNode, ct);
            }

            // 7. Write version history snapshot (non-critical, skip satellite types like threads/comments)
            if (!catalog.Configuration.IsSatelliteNodeType(newNode.NodeType))
            {
                var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
                if (versionQuery != null)
                {
                    try { await versionQuery.WriteVersionAsync(newNode, hub.JsonSerializerOptions, ct); }
                    catch { /* version write failure is non-critical */ }
                }
            }

            logger.LogInformation("Node created at {Path} by {CreatedBy}", newNode.Path, createRequest.CreatedBy ?? "system");

            // 8. Run post-creation handlers (e.g. grant creator Admin role)
            await RunPostCreationHandlersAsync(hub, newNode, createRequest.CreatedBy, logger, ct);

            // 9. Return success response
            hub.Post(CreateNodeResponse.Ok(newNode), o => o.ResponseFor(request));

            return request.Processed();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Node creation failed for path {Path}", request.Message.Node.Path);
            hub.Post(
                CreateNodeResponse.Fail(ex.Message, NodeCreationRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during node creation at {Path}", request.Message.Node.Path);
            hub.Post(
                CreateNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    private static async Task<IMessageDelivery> HandleDeleteNodeRequest(
        IMessageHub hub,
        IMessageDelivery<DeleteNodeRequest> request,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var catalog = hub.ServiceProvider.GetService<IMeshCatalog>();

        if (catalog == null)
        {
            hub.Post(
                DeleteNodeResponse.Fail("IMeshCatalog not available", NodeDeletionRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        try
        {
            var deleteRequest = request.Message;

            // Identity resolution: if no explicit DeletedBy, use AccessContext identity
            if (string.IsNullOrEmpty(deleteRequest.DeletedBy)
                && request.AccessContext?.ObjectId is { Length: > 0 } deleteSenderId)
                deleteRequest = deleteRequest with { DeletedBy = deleteSenderId };

            var path = deleteRequest.Path;

            // 1. Check if node exists
            var existingNode = await catalog.GetNodeAsync(new Address(path));
            if (existingNode == null)
            {
                hub.Post(
                    DeleteNodeResponse.Fail(
                        $"Node not found at path: {path}",
                        NodeDeletionRejectionReason.NodeNotFound),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 2. Run validators on this node (global + NodeType-specific)
            var validationError = await RunDeletionValidatorsAsync(hub, catalog, existingNode, deleteRequest, ct);
            if (validationError != null)
            {
                logger.LogWarning("Validator rejected node deletion at {Path}: {Error}",
                    path, validationError.Value.ErrorMessage);

                hub.Post(
                    DeleteNodeResponse.Fail(
                        validationError.Value.ErrorMessage ?? "Validation failed",
                        validationError.Value.Reason),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 3. Get direct children
            var children = new List<MeshNode>();
            await foreach (var child in catalog.QueryAsync(path, maxResults: int.MaxValue, ct: ct))
                children.Add(child);

            if (children.Count == 0)
            {
                // Leaf node — delete immediately
                await catalog.DeleteNodeAsync(path, recursive: false, ct);
                hub.Post(DeleteNodeResponse.Ok(), o => o.ResponseFor(request));
                logger.LogInformation("Node deleted at {Path} by {DeletedBy}", path, deleteRequest.DeletedBy ?? "system");
                return request.Processed();
            }

            // Non-recursive delete with children — reject
            if (!deleteRequest.Recursive)
            {
                hub.Post(
                    DeleteNodeResponse.Fail(
                        $"Node at '{path}' has children. Use recursive delete to remove it.",
                        NodeDeletionRejectionReason.HasChildren),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 4. Has children — post DeleteNodeRequest for each child, do NOT await.
            //    Use RegisterCallback to collect responses asynchronously.
            //    When all children have responded, delete self and post response.
            var childResponses = new DeleteNodeResponse[children.Count];
            var remaining = children.Count;

            for (var i = 0; i < children.Count; i++)
            {
                var idx = i;
                var childRequest = new DeleteNodeRequest(children[i].Path) { DeletedBy = deleteRequest.DeletedBy, Recursive = true };
                var delivery = hub.Post(childRequest, o => o.WithTarget(hub.Address))!;

                _ = hub.RegisterCallback<DeleteNodeResponse>(delivery, response =>
                {
                    childResponses[idx] = response.Message;

                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        // All children responded — decide on parent deletion
                        hub.InvokeAsync(async _ =>
                        {
                            var failed = childResponses.Where(r => !r.Success).Select(r => r.Error).ToList();
                            if (failed.Count > 0)
                            {
                                hub.Post(
                                    DeleteNodeResponse.Fail(
                                        $"Cannot delete '{path}': {failed.Count} child deletion(s) failed: {string.Join("; ", failed)}",
                                        NodeDeletionRejectionReason.ChildDeletionFailed),
                                    o => o.ResponseFor(request));
                            }
                            else
                            {
                                await catalog.DeleteNodeAsync(path, recursive: false, ct);
                                hub.Post(DeleteNodeResponse.Ok(), o => o.ResponseFor(request));
                                logger.LogInformation("Node deleted at {Path} by {DeletedBy}", path, deleteRequest.DeletedBy ?? "system");
                            }
                        }, ex =>
                        {
                            logger.LogError(ex, "Error completing deletion of {Path}", path);
                            hub.Post(
                                DeleteNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                                o => o.ResponseFor(request));
                            return Task.CompletedTask;
                        });
                    }

                    return response;
                });
            }

            // Return immediately — response will be posted from the callbacks above
            return request.Processed();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Node deletion failed for path {Path}", request.Message.Path);
            hub.Post(
                DeleteNodeResponse.Fail(ex.Message, NodeDeletionRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during node deletion at {Path}", request.Message.Path);
            hub.Post(
                DeleteNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    /// <summary>
    /// Runs all creation validators from DI using the unified INodeValidator interface.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeCreationRejectionReason Reason)?> RunCreationValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog _,
        MeshNode node,
        CreateNodeRequest request,
        CancellationToken ct)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        // Run unified validators from DI
        var validators = hub.ServiceProvider.GetServices<INodeValidator>();
        foreach (var validator in validators)
        {
            // Check if validator handles Create operations
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Create))
            {
                continue;
            }

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeAlreadyExists => NodeCreationRejectionReason.NodeAlreadyExists,
                    NodeRejectionReason.InvalidNodeType => NodeCreationRejectionReason.InvalidNodeType,
                    NodeRejectionReason.InvalidPath => NodeCreationRejectionReason.InvalidPath,
                    NodeRejectionReason.Unauthorized => NodeCreationRejectionReason.ValidationFailed,
                    _ => NodeCreationRejectionReason.ValidationFailed
                };
                return (result.ErrorMessage, reason);
            }
        }

        return null; // All validators passed
    }

    /// <summary>
    /// Runs DI-registered post-creation handlers for the given node type.
    /// Failures are logged but do not affect the creation response.
    /// Additional nodes returned by handlers are persisted directly via IMeshStorage
    /// (bypassing the hub pipeline to avoid deadlocks).
    /// </summary>
    private static async Task RunPostCreationHandlersAsync(
        IMessageHub hub,
        MeshNode node,
        string? createdBy,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(node.NodeType))
            return;

        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();
        var handlers = hub.ServiceProvider.GetServices<INodePostCreationHandler>();
        foreach (var handler in handlers)
        {
            if (!handler.NodeType.Equals(node.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await handler.HandleAsync(node, createdBy, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Post-creation handler {Handler} failed for node {Path}",
                    handler.GetType().Name, node.Path);
            }

            // Persist additional nodes directly (bypass hub pipeline to avoid deadlocks)
            try
            {
                var additionalNodes = handler.GetAdditionalNodes(node);
                foreach (var additional in additionalNodes)
                {
                    if (persistence != null)
                    {
                        var saved = await persistence.SaveNodeAsync(additional with { State = MeshNodeState.Active }, ct);
                        hub.Post(DataChangeRequest.Update([saved]), o => o.WithTarget(hub.Address));
                        logger.LogInformation("Post-creation handler created additional node at {Path}", saved.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Post-creation handler {Handler} failed to create additional nodes for {Path}",
                    handler.GetType().Name, node.Path);
            }
        }
    }

    /// <summary>
    /// Runs all deletion validators from DI using the unified INodeValidator interface.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeDeletionRejectionReason Reason)?> RunDeletionValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog _,
        MeshNode node,
        DeleteNodeRequest request,
        CancellationToken ct)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = node,
            Request = request,
            AccessContext = accessService?.Context ?? accessService?.CircuitContext
        };

        // Run unified validators from DI
        var validators = hub.ServiceProvider.GetServices<INodeValidator>();
        foreach (var validator in validators)
        {
            // Check if validator handles Delete operations
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Delete))
            {
                continue;
            }

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeDeletionRejectionReason.NodeNotFound,
                    NodeRejectionReason.HasChildren => NodeDeletionRejectionReason.HasChildren,
                    NodeRejectionReason.Unauthorized => NodeDeletionRejectionReason.ValidationFailed,
                    _ => NodeDeletionRejectionReason.ValidationFailed
                };
                return (result.ErrorMessage, reason);
            }
        }

        return null; // All validators passed
    }

    private static async Task<IMessageDelivery> HandleUpdateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<UpdateNodeRequest> request,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var catalog = hub.ServiceProvider.GetService<IMeshCatalog>();

        if (catalog == null)
        {
            hub.Post(
                UpdateNodeResponse.Fail("IMeshCatalog not available", NodeUpdateRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        try
        {
            var updateRequest = request.Message;

            // Identity resolution: if no explicit UpdatedBy, use AccessContext identity
            if (string.IsNullOrEmpty(updateRequest.UpdatedBy)
                && request.AccessContext?.ObjectId is { Length: > 0 } updateSenderId)
                updateRequest = updateRequest with { UpdatedBy = updateSenderId };

            var updatedNode = updateRequest.Node;

            // 1. Check if node exists
            var existingNode = await catalog.GetNodeAsync(new Address(updatedNode.Path));
            if (existingNode == null)
            {
                hub.Post(
                    UpdateNodeResponse.Fail(
                        $"Node not found at path: {updatedNode.Path}",
                        NodeUpdateRejectionReason.NodeNotFound),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 2. Validate NodeType hasn't changed (if set)
            if (!string.IsNullOrEmpty(existingNode.NodeType) &&
                !string.IsNullOrEmpty(updatedNode.NodeType) &&
                existingNode.NodeType != updatedNode.NodeType)
            {
                hub.Post(
                    UpdateNodeResponse.Fail(
                        $"Cannot change NodeType from '{existingNode.NodeType}' to '{updatedNode.NodeType}'",
                        NodeUpdateRejectionReason.InvalidNodeType),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 3. Run validators (global + NodeType-specific)
            var validationError = await RunUpdateValidatorsAsync(hub, catalog, existingNode, updatedNode, updateRequest, ct);
            if (validationError != null)
            {
                logger.LogWarning("Validator rejected node update at {Path}: {Error}",
                    updatedNode.Path, validationError.Value.ErrorMessage);

                hub.Post(
                    UpdateNodeResponse.Fail(
                        validationError.Value.ErrorMessage ?? "Validation failed",
                        validationError.Value.Reason),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 4. Update the node - preserve HubConfiguration from existing; allow State changes
            var nodeToSave = updatedNode with
            {
                State = updatedNode.State != default ? updatedNode.State : existingNode.State,
                HubConfiguration = existingNode.HubConfiguration
            };

            // 5. Persist the validated node
            var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
            var savedNode = await persistence.SaveNodeAsync(nodeToSave, ct);

            // 5b. Write version history snapshot (non-critical, skip satellite types like threads/comments)
            if (!catalog.Configuration.IsSatelliteNodeType(savedNode.NodeType))
            {
                var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
                if (versionQuery != null)
                {
                    try { await versionQuery.WriteVersionAsync(savedNode, hub.JsonSerializerOptions, ct); }
                    catch { /* version write failure is non-critical */ }
                }
            }

            // 6. Update workspace stream via DataChangeRequest (fire-and-forget, non-blocking)
            //    Do NOT await — posting to the same hub inside a handler would deadlock.
            var changeDelivery = hub.Post(
                DataChangeRequest.Update([nodeToSave]),
                o => o.WithTarget(hub.Address));

            // 7. Return success response immediately after persistence
            hub.Post(UpdateNodeResponse.Ok(nodeToSave), o => o.ResponseFor(request));

            logger.LogInformation("Node updated successfully at {Path} by {UpdatedBy}",
                nodeToSave.Path, updateRequest.UpdatedBy ?? "system");
            return request.Processed();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Node update failed for path {Path}", request.Message.Node.Path);
            hub.Post(
                UpdateNodeResponse.Fail(ex.Message, NodeUpdateRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during node update at {Path}", request.Message.Node.Path);
            hub.Post(
                UpdateNodeResponse.Fail($"Unexpected error: {ex.Message}"),
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    /// <summary>
    /// Runs all update validators from DI using the unified INodeValidator interface.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeUpdateRejectionReason Reason)?> RunUpdateValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog _,
        MeshNode existingNode,
        MeshNode updatedNode,
        UpdateNodeRequest request,
        CancellationToken ct)
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

        // Run unified validators from DI
        var validators = hub.ServiceProvider.GetServices<INodeValidator>();
        foreach (var validator in validators)
        {
            // Check if validator handles Update operations
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Update))
            {
                continue;
            }

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                var reason = result.Reason switch
                {
                    NodeRejectionReason.NodeNotFound => NodeUpdateRejectionReason.NodeNotFound,
                    NodeRejectionReason.InvalidNodeType => NodeUpdateRejectionReason.InvalidNodeType,
                    NodeRejectionReason.ConcurrencyConflict => NodeUpdateRejectionReason.ConcurrencyConflict,
                    NodeRejectionReason.Unauthorized => NodeUpdateRejectionReason.ValidationFailed,
                    _ => NodeUpdateRejectionReason.ValidationFailed
                };
                return (result.ErrorMessage, reason);
            }
        }

        return null; // All validators passed
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

            // 4. Move the node
            var movedNode = await persistence.MoveNodeAsync(moveRequest.SourcePath, moveRequest.TargetPath, ct);

            // 5. Return success
            hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request));

            logger.LogInformation("Node moved from {Source} to {Target}",
                moveRequest.SourcePath, moveRequest.TargetPath);
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
