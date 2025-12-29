using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Node validator that enforces Row-Level Security based on permissions.
/// Checks if the current user has the required permission for the operation.
/// </summary>
public class RlsNodeValidator : INodeValidator
{
    private readonly ISecurityService _securityService;
    private readonly ILogger<RlsNodeValidator> _logger;

    public RlsNodeValidator(ISecurityService securityService, ILogger<RlsNodeValidator> logger)
    {
        _securityService = securityService;
        _logger = logger;
    }

    /// <summary>
    /// This validator handles Create, Update, and Delete operations.
    /// Read operations are handled separately via SecurePersistenceServiceDecorator
    /// to avoid issues with internal reads during update/delete existence checks.
    /// </summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    public async Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Map operation to required permission
        var requiredPermission = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            NodeOperation.Create => Permission.Create,
            NodeOperation.Update => Permission.Update,
            NodeOperation.Delete => Permission.Delete,
            _ => Permission.None
        };

        // No permission required
        if (requiredPermission == Permission.None)
            return NodeValidationResult.Valid();

        // Get the user ID from AccessContext or from the request
        var userId = GetUserId(context);

        // For Create operations, check permission on parent path
        // (user needs Create permission on the parent to create a child)
        var pathToCheck = context.Operation == NodeOperation.Create
            ? context.Node.ParentPath ?? context.Node.Path
            : context.Node.Path;

        // Check permission - use the explicit userId if available
        bool hasPermission;
        if (!string.IsNullOrEmpty(userId))
        {
            hasPermission = await _securityService.HasPermissionAsync(
                pathToCheck,
                userId,
                requiredPermission,
                ct);
        }
        else
        {
            // Fallback to context-based check (uses AccessService internally)
            hasPermission = await _securityService.HasPermissionAsync(
                pathToCheck,
                requiredPermission,
                ct);
        }

        if (!hasPermission)
        {
            var displayUserId = userId ?? "(anonymous)";

            _logger.LogDebug(
                "RLS: Access denied for user {UserId} - {Operation} on {Path} requires {Permission}",
                displayUserId,
                context.Operation,
                context.Node.Path,
                requiredPermission);

            return NodeValidationResult.Unauthorized(
                $"Access denied: {context.Operation} permission required for node '{context.Node.Path}'");
        }

        _logger.LogTrace(
            "RLS: Access granted for user {UserId} - {Operation} on {Path}",
            userId ?? "(anonymous)",
            context.Operation,
            context.Node.Path);

        return NodeValidationResult.Valid();
    }

    /// <summary>
    /// Extracts the user ID from the validation context.
    /// Prioritizes AccessContext, then falls back to request-specific properties.
    /// </summary>
    private static string? GetUserId(NodeValidationContext context)
    {
        // First try AccessContext (set during authenticated requests)
        if (!string.IsNullOrEmpty(context.AccessContext?.ObjectId))
            return context.AccessContext.ObjectId;

        // Fall back to request-specific user properties
        return context.Request switch
        {
            MeshWeaver.Mesh.CreateNodeRequest createReq => createReq.CreatedBy,
            MeshWeaver.Mesh.UpdateNodeRequest updateReq => updateReq.UpdatedBy,
            MeshWeaver.Mesh.DeleteNodeRequest deleteReq => deleteReq.DeletedBy,
            _ => null
        };
    }
}
