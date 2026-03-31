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
    private readonly IReadOnlyDictionary<string, INodeTypeAccessRule> _accessRules;
    private readonly INodeTypeService? _nodeTypeService;

    public RlsNodeValidator(
        ISecurityService securityService,
        ILogger<RlsNodeValidator> logger,
        IEnumerable<INodeTypeAccessRule> accessRules,
        INodeTypeService? nodeTypeService = null)
    {
        _securityService = securityService;
        _logger = logger;
        _nodeTypeService = nodeTypeService;
        _accessRules = accessRules
            .GroupBy(r => r.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
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
        // Self-access: hubs always have full control of their own nodes
        var userId = GetUserId(context);
        if (!string.IsNullOrEmpty(userId))
        {
            // Check MainNode match (original check)
            if (!string.IsNullOrEmpty(context.Node.MainNode)
                && string.Equals(context.Node.MainNode, userId, StringComparison.OrdinalIgnoreCase))
            {
                return NodeValidationResult.Valid();
            }

            // Check if node is under the user's own User scope (User/{userId} and descendants)
            var nodePath = context.Node.Path;
            if (!string.IsNullOrEmpty(nodePath))
            {
                var userScopePath = $"User/{userId}";
                if (nodePath.Equals(userScopePath, StringComparison.OrdinalIgnoreCase)
                    || nodePath.StartsWith(userScopePath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return NodeValidationResult.Valid();
                }
            }
        }

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

        // Check hub-config access rules (from NodeTypeService) — grant-only, fall through on no match
        if (!string.IsNullOrEmpty(context.Node.NodeType))
        {
            var hubRule = _nodeTypeService?.GetAccessRule(context.Node.NodeType);
            if (hubRule != null &&
                (hubRule.SupportedOperations.Count == 0 ||
                 hubRule.SupportedOperations.Contains(context.Operation)))
            {
                var hasHubAccess = await hubRule.HasAccessAsync(context, userId, ct);
                if (hasHubAccess)
                {
                    _logger.LogTrace(
                        "RLS: Hub-config rule granted {UserId} - {Operation} on {Path} (NodeType: {NodeType})",
                        userId ?? "(anonymous)", context.Operation, context.Node.Path, context.Node.NodeType);
                    return NodeValidationResult.Valid();
                }
            }
        }

        // Check DI-registered custom access rules for this node type
        if (!string.IsNullOrEmpty(context.Node.NodeType) &&
            _accessRules.TryGetValue(context.Node.NodeType, out var accessRule) &&
            (accessRule.SupportedOperations.Count == 0 ||
             accessRule.SupportedOperations.Contains(context.Operation)))
        {
            var hasCustomAccess = await accessRule.HasAccessAsync(context, userId, ct);
            if (hasCustomAccess)
            {
                _logger.LogTrace(
                    "RLS: Custom access rule granted {UserId} - {Operation} on {Path} (NodeType: {NodeType})",
                    userId ?? "(anonymous)", context.Operation, context.Node.Path, context.Node.NodeType);
                return NodeValidationResult.Valid();
            }

            _logger.LogDebug(
                "RLS: Custom access rule denied {UserId} - {Operation} on {Path} (NodeType: {NodeType})",
                userId ?? "(anonymous)", context.Operation, context.Node.Path, context.Node.NodeType);
            return NodeValidationResult.Unauthorized(
                $"Access denied: {context.Operation} permission required for node '{context.Node.Path}'");
        }

        // For Create operations, check permission on parent path
        // (user needs Create permission on the parent to create a child)
        var pathToCheck = context.Operation == NodeOperation.Create
            ? context.Node.GetParentPath() ?? context.Node.Path
            : context.Node.Path;

        // Always use explicit userId for permission checks to avoid admin context leaking.
        // When userId is null (anonymous), use WellKnownUsers.Anonymous.
        var effectiveUserId = userId ?? WellKnownUsers.Anonymous;

        // Check permission - for Comment permission, also accept Update (Edit implies Comment)
        bool hasPermission;
        if (requiredPermission == Permission.Comment)
        {
            var effectivePermissions = await _securityService.GetEffectivePermissionsAsync(pathToCheck, effectiveUserId, ct);
            hasPermission = effectivePermissions.HasFlag(Permission.Comment)
                         || effectivePermissions.HasFlag(Permission.Update);
        }
        else
        {
            hasPermission = await _securityService.HasPermissionAsync(
                pathToCheck,
                effectiveUserId,
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
        _ => Permission.Create
    };

    /// <summary>
    /// Extracts the user ID from the validation context.
    /// First checks explicit request identity (CreatedBy/UpdatedBy/DeletedBy),
    /// then falls back to AccessContext (the logged-in user).
    /// </summary>
    private static string? GetUserId(NodeValidationContext context)
    {
        // Check explicit request identity first
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
