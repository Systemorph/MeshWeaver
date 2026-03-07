using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

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
    /// This validator handles all CRUD operations.
    /// Read validation is enforced via MeshCatalog.ValidateReadAsync for node reads.
    /// Write validation is enforced via HandleUpdateNodeRequest/HandleDeleteNodeRequest handlers.
    /// </summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    public async Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Map operation to required permission
        var requiredPermission = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            NodeOperation.Create => GetCreatePermission(context.Node),
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
            ? context.Node.GetParentPath() ?? context.Node.Path
            : context.Node.Path;

        // Check permission - for Comment permission, also accept Update (Edit implies Comment)
        bool hasPermission;
        if (requiredPermission == Permission.Comment)
        {
            Permission effectivePermissions;
            if (!string.IsNullOrEmpty(userId))
                effectivePermissions = await _securityService.GetEffectivePermissionsAsync(pathToCheck, userId, ct);
            else
                effectivePermissions = await _securityService.GetEffectivePermissionsAsync(pathToCheck, ct);

            hasPermission = effectivePermissions.HasFlag(Permission.Comment)
                         || effectivePermissions.HasFlag(Permission.Update);
        }
        else if (!string.IsNullOrEmpty(userId))
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
    /// Determines the required permission for a Create operation based on node type.
    /// Comment creation requires Comment permission, Thread creation requires Update permission.
    /// </summary>
    private static Permission GetCreatePermission(MeshNode node) => node.NodeType switch
    {
        CommentNodeType.NodeType => Permission.Comment,
        ThreadNodeType.NodeType => Permission.Update,
        _ => Permission.Create
    };

    /// <summary>
    /// Extracts the user ID from the validation context.
    /// Prioritizes the request-specific user (explicit identity for the operation),
    /// then falls back to AccessContext (authenticated session user).
    /// </summary>
    private static string? GetUserId(NodeValidationContext context)
    {
        // First try request-specific user properties (explicit identity for the operation)
        var requestUserId = context.Request switch
        {
            CreateNodeRequest createReq => createReq.CreatedBy,
            UpdateNodeRequest updateReq => updateReq.UpdatedBy,
            DeleteNodeRequest deleteReq => deleteReq.DeletedBy,
            _ => null
        };
        if (!string.IsNullOrEmpty(requestUserId))
            return requestUserId;

        // Fall back to AccessContext (authenticated session user)
        if (!string.IsNullOrEmpty(context.AccessContext?.ObjectId))
            return context.AccessContext.ObjectId;

        return null;
    }
}
