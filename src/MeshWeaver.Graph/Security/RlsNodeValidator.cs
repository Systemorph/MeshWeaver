using System.Reactive.Linq;
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

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // System bypass + own-scope shortcuts — pure sync, no observable needed.
        var userId = GetUserId(context);
        if (userId == WellKnownUsers.System)
            return Observable.Return(NodeValidationResult.Valid());

        if (!string.IsNullOrEmpty(userId))
        {
            if (!string.IsNullOrEmpty(context.Node.MainNode)
                && string.Equals(context.Node.MainNode, userId, StringComparison.OrdinalIgnoreCase))
                return Observable.Return(NodeValidationResult.Valid());

            var nodePath = context.Node.Path;
            if (!string.IsNullOrEmpty(nodePath))
            {
                var userScopePath = $"User/{userId}";
                if (nodePath.Equals(userScopePath, StringComparison.OrdinalIgnoreCase)
                    || nodePath.StartsWith(userScopePath + "/", StringComparison.OrdinalIgnoreCase))
                    return Observable.Return(NodeValidationResult.Valid());
            }
        }

        var requiredPermission = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            NodeOperation.Create => GetCreatePermission(context.Node),
            NodeOperation.Update => Permission.Update,
            NodeOperation.Delete => Permission.Delete,
            _ => Permission.None
        };

        if (requiredPermission == Permission.None)
            return Observable.Return(NodeValidationResult.Valid());

        // Compose: hub-rule → custom-rule → permission check. Each step returns
        // an observable; chain via SelectMany. A null result from one step means
        // "fall through" — re-emit by wrapping with Observable.Return; otherwise
        // pass to the next step in the chain.
        return CheckHubRule(context, userId)
            .SelectMany(hubResult => hubResult != null
                ? Observable.Return<NodeValidationResult?>(hubResult)
                : CheckCustomRule(context, userId))
            .SelectMany(customResult => customResult != null
                ? Observable.Return(customResult)
                : CheckPermission(context, userId, requiredPermission));
    }

    private IObservable<NodeValidationResult?> CheckHubRule(NodeValidationContext context, string? userId)
    {
        if (string.IsNullOrEmpty(context.Node.NodeType))
            return Observable.Return<NodeValidationResult?>(null);

        var hubRule = _nodeTypeService?.GetAccessRule(context.Node.NodeType);
        if (hubRule == null
            || (hubRule.SupportedOperations.Count != 0
                && !hubRule.SupportedOperations.Contains(context.Operation)))
            return Observable.Return<NodeValidationResult?>(null);

        return hubRule.HasAccess(context, userId).Select<bool, NodeValidationResult?>(hasAccess =>
        {
            if (hasAccess)
            {
                _logger.LogTrace(
                    "RLS: Hub-config rule granted {UserId} - {Operation} on {Path} (NodeType: {NodeType})",
                    userId ?? "(anonymous)", context.Operation, context.Node.Path, context.Node.NodeType);
                return NodeValidationResult.Valid();
            }
            return null; // fall through to next rule
        });
    }

    private IObservable<NodeValidationResult?> CheckCustomRule(NodeValidationContext context, string? userId)
    {
        if (string.IsNullOrEmpty(context.Node.NodeType)
            || !_accessRules.TryGetValue(context.Node.NodeType, out var accessRule)
            || (accessRule.SupportedOperations.Count != 0
                && !accessRule.SupportedOperations.Contains(context.Operation)))
            return Observable.Return<NodeValidationResult?>(null);

        return accessRule.HasAccess(context, userId).Select<bool, NodeValidationResult?>(hasAccess =>
        {
            if (hasAccess)
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
        });
    }

    private IObservable<NodeValidationResult> CheckPermission(
        NodeValidationContext context, string? userId, Permission requiredPermission)
    {
        var pathToCheck = context.Operation == NodeOperation.Create
            ? context.Node.GetParentPath() ?? context.Node.Path
            : context.Node.Path;
        var effectiveUserId = userId ?? WellKnownUsers.Anonymous;

        IObservable<bool> hasPermissionObs = requiredPermission == Permission.Comment
            ? _securityService.GetEffectivePermissions(pathToCheck, effectiveUserId)
                .Select(p => p.HasFlag(Permission.Comment) || p.HasFlag(Permission.Update))
            : _securityService.HasPermission(pathToCheck, effectiveUserId, requiredPermission);

        return hasPermissionObs.Select(hasPermission =>
        {
            if (!hasPermission)
            {
                _logger.LogDebug(
                    "RLS: Access denied for user {UserId} - {Operation} on {Path} requires {Permission}",
                    userId ?? "(anonymous)", context.Operation, context.Node.Path, requiredPermission);
                return NodeValidationResult.Unauthorized(
                    $"Access denied: {context.Operation} permission required for node '{context.Node.Path}'");
            }

            _logger.LogTrace(
                "RLS: Access granted for user {UserId} - {Operation} on {Path}",
                userId ?? "(anonymous)", context.Operation, context.Node.Path);
            return NodeValidationResult.Valid();
        });
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
