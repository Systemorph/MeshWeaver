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

        // Security types for row-level security (stored in JSON files)
        config.TypeRegistry.WithType(typeof(UserAccess), nameof(UserAccess));
        return config;
    }

    /// <summary>
    /// Registers handlers for mesh node operations.
    /// </summary>
    public static MessageHubConfiguration WithNodeOperationHandlers(this MessageHubConfiguration config)
    {
        return config
            .WithHandler<CreateNodeRequest>(HandleCreateNodeRequest)
            .WithHandler<DeleteNodeRequest>(HandleDeleteNodeRequest)
            .WithHandler<UpdateNodeRequest>(HandleUpdateNodeRequest);
    }

    private static async Task<IMessageDelivery> HandleCreateNodeRequest(
        IMessageHub hub,
        IMessageDelivery<CreateNodeRequest> request,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<IMeshCatalog>>();
        var catalog = hub.ServiceProvider.GetService<IMeshCatalog>();
        var persistence = hub.ServiceProvider.GetService<IPersistenceService>();

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
            var node = createRequest.Node;

            // 1. Check if node already exists
            var existingNode = await catalog.GetNodeAsync(new Address(node.Path));
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
                        Description = node.Description ?? existingNode.Description,
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

            // 3. Create the node using the catalog
            var createdNode = await catalog.CreateNodeAsync(node, createRequest.CreatedBy, ct);

            // 4. Return success response
            hub.Post(CreateNodeResponse.Ok(createdNode), o => o.ResponseFor(request));

            logger.LogInformation("Node created at {Path}", createdNode.Path);
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

            // 2. Check for children if not recursive
            if (!deleteRequest.Recursive)
            {
                var hasChildren = false;
                await foreach (var _ in catalog.QueryAsync(path, maxResults: 1, ct: ct))
                {
                    hasChildren = true;
                    break;
                }

                if (hasChildren)
                {
                    hub.Post(
                        DeleteNodeResponse.Fail(
                            $"Node at path '{path}' has children. Use Recursive=true to delete.",
                            NodeDeletionRejectionReason.HasChildren),
                        o => o.ResponseFor(request));
                    return request.Processed();
                }
            }

            // 3. Run validators (global + NodeType-specific)
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

            // 4. Delete the node
            await catalog.DeleteNodeAsync(path, deleteRequest.Recursive, ct);

            // 4. Return success response
            hub.Post(DeleteNodeResponse.Ok(), o => o.ResponseFor(request));

            logger.LogInformation("Node deleted successfully at {Path} by {DeletedBy}", path, deleteRequest.DeletedBy ?? "system");
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
            AccessContext = accessService?.Context
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
            AccessContext = accessService?.Context
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

            // 4. Update the node - preserve State and HubConfiguration from existing
            var nodeToSave = updatedNode with
            {
                State = existingNode.State,
                HubConfiguration = existingNode.HubConfiguration
            };

            // 5. Return success response
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
            AccessContext = accessService?.Context
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
}
