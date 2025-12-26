using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

public static class MeshExtensions
{
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

        if (catalog == null)
        {
            hub.Post(
                CreateNodeResponse.Fail("IMeshCatalog not available", NodeCreationRejectionReason.Unknown),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        MeshNode? transientNode = null;
        try
        {
            var createRequest = request.Message;
            var node = createRequest.Node;

            // 1. Check if node already exists
            var existingNode = await catalog.GetNodeAsync(new Address(node.Path));
            if (existingNode != null)
            {
                hub.Post(
                    CreateNodeResponse.Fail(
                        $"Node already exists at path: {node.Path}",
                        NodeCreationRejectionReason.NodeAlreadyExists),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 2. Validate NodeType if specified
            if (!string.IsNullOrEmpty(node.NodeType))
            {
                var nodeTypeConfig = catalog.GetNodeTypeConfiguration(node.NodeType);
                if (nodeTypeConfig == null)
                {
                    hub.Post(
                        CreateNodeResponse.Fail(
                            $"NodeType '{node.NodeType}' is not registered",
                            NodeCreationRejectionReason.InvalidNodeType),
                        o => o.ResponseFor(request));
                    return request.Processed();
                }
            }

            // 3. Create transient node
            transientNode = await catalog.CreateTransientNodeAsync(node, createRequest.CreatedBy, ct);

            // 4. Run validators (global + NodeType-specific)
            var validationError = await RunCreationValidatorsAsync(hub, catalog, transientNode, createRequest, ct);
            if (validationError != null)
            {
                // Validation failed - delete transient node
                logger.LogWarning("Validator rejected node creation at {Path}: {Error}",
                    transientNode.Path, validationError.Value.ErrorMessage);
                await catalog.DeleteNodeAsync(transientNode.Path, false, ct);

                hub.Post(
                    CreateNodeResponse.Fail(
                        validationError.Value.ErrorMessage ?? "Validation failed",
                        validationError.Value.Reason),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // 5. Confirm the node
            var confirmedNode = await catalog.ConfirmNodeAsync(transientNode.Path, ct);

            // 6. Return success response
            hub.Post(CreateNodeResponse.Ok(confirmedNode), o => o.ResponseFor(request));

            logger.LogInformation("Node created successfully at {Path}", confirmedNode.Path);
            return request.Processed();
        }
        catch (InvalidOperationException ex)
        {
            // Clean up transient node if it was created
            if (transientNode != null)
            {
                try
                {
                    await catalog.DeleteNodeAsync(transientNode.Path, false, ct);
                }
                catch { /* Ignore cleanup errors */ }
            }

            logger.LogWarning(ex, "Node creation failed for path {Path}", request.Message.Node.Path);
            hub.Post(
                CreateNodeResponse.Fail(ex.Message, NodeCreationRejectionReason.ValidationFailed),
                o => o.ResponseFor(request));
            return request.Processed();
        }
        catch (Exception ex)
        {
            // Clean up transient node if it was created
            if (transientNode != null)
            {
                try
                {
                    await catalog.DeleteNodeAsync(transientNode.Path, false, ct);
                }
                catch { /* Ignore cleanup errors */ }
            }

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
    /// Runs all creation validators: first global validators from DI, then NodeType-specific validators.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeCreationRejectionReason Reason)?> RunCreationValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog catalog,
        MeshNode node,
        CreateNodeRequest request,
        CancellationToken ct)
    {
        // 1. Run global validators from DI
        var globalValidators = hub.ServiceProvider.GetServices<INodeCreationValidator>();
        foreach (var validator in globalValidators)
        {
            var result = await validator.ValidateAsync(node, request, ct);
            if (!result.IsValid)
                return (result.ErrorMessage, result.Reason);
        }

        // 2. Run NodeType-specific validators
        if (!string.IsNullOrEmpty(node.NodeType))
        {
            var nodeTypeConfig = catalog.GetNodeTypeConfiguration(node.NodeType);
            if (nodeTypeConfig != null)
            {
                foreach (var validatorType in nodeTypeConfig.CreationValidatorTypes)
                {
                    var validator = (INodeCreationValidator?)ActivatorUtilities.CreateInstance(hub.ServiceProvider, validatorType);
                    if (validator != null)
                    {
                        var result = await validator.ValidateAsync(node, request, ct);
                        if (!result.IsValid)
                            return (result.ErrorMessage, result.Reason);
                    }
                }
            }
        }

        return null; // All validators passed
    }

    /// <summary>
    /// Runs all deletion validators: first global validators from DI, then NodeType-specific validators.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeDeletionRejectionReason Reason)?> RunDeletionValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog catalog,
        MeshNode node,
        DeleteNodeRequest request,
        CancellationToken ct)
    {
        // 1. Run global validators from DI
        var globalValidators = hub.ServiceProvider.GetServices<INodeDeletionValidator>();
        foreach (var validator in globalValidators)
        {
            var result = await validator.ValidateAsync(node, request, ct);
            if (!result.IsValid)
                return (result.ErrorMessage, result.Reason);
        }

        // 2. Run NodeType-specific validators
        if (!string.IsNullOrEmpty(node.NodeType))
        {
            var nodeTypeConfig = catalog.GetNodeTypeConfiguration(node.NodeType);
            if (nodeTypeConfig != null)
            {
                foreach (var validatorType in nodeTypeConfig.DeletionValidatorTypes)
                {
                    var validator = (INodeDeletionValidator?)ActivatorUtilities.CreateInstance(hub.ServiceProvider, validatorType);
                    if (validator != null)
                    {
                        var result = await validator.ValidateAsync(node, request, ct);
                        if (!result.IsValid)
                            return (result.ErrorMessage, result.Reason);
                    }
                }
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
            var validationError = await RunUpdateValidatorsAsync(hub, catalog, existingNode, updatedNode, ct);
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
            await catalog.UpdateAsync(nodeToSave);

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
    /// Runs all update validators: first global validators from DI, then NodeType-specific validators.
    /// </summary>
    private static async Task<(string? ErrorMessage, NodeUpdateRejectionReason Reason)?> RunUpdateValidatorsAsync(
        IMessageHub hub,
        IMeshCatalog catalog,
        MeshNode existingNode,
        MeshNode updatedNode,
        CancellationToken ct)
    {
        // 1. Run global validators from DI
        var globalValidators = hub.ServiceProvider.GetServices<INodeUpdateValidator>();
        foreach (var validator in globalValidators)
        {
            var result = await validator.ValidateAsync(existingNode, updatedNode, ct);
            if (!result.IsValid)
                return (result.ErrorMessage, result.Reason);
        }

        // 2. Run NodeType-specific validators (use existing node's NodeType)
        if (!string.IsNullOrEmpty(existingNode.NodeType))
        {
            var nodeTypeConfig = catalog.GetNodeTypeConfiguration(existingNode.NodeType);
            if (nodeTypeConfig != null)
            {
                foreach (var validatorType in nodeTypeConfig.UpdateValidatorTypes)
                {
                    var validator = (INodeUpdateValidator?)ActivatorUtilities.CreateInstance(hub.ServiceProvider, validatorType);
                    if (validator != null)
                    {
                        var result = await validator.ValidateAsync(existingNode, updatedNode, ct);
                        if (!result.IsValid)
                            return (result.ErrorMessage, result.Reason);
                    }
                }
            }
        }

        return null; // All validators passed
    }
}
