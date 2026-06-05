using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Node validator that enforces Row-Level Security based on permissions.
/// Checks if the current user has the required permission for the operation.
/// </summary>
public class RlsNodeValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<RlsNodeValidator> _logger;
    private readonly IReadOnlyDictionary<string, INodeTypeAccessRule> _accessRules;

    public RlsNodeValidator(
        IMessageHub hub,
        ILogger<RlsNodeValidator> logger,
        IEnumerable<INodeTypeAccessRule> accessRules)
    {
        _hub = hub;
        _logger = logger;
        _accessRules = accessRules
            .GroupBy(r => r.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// This validator handles Read, Create, and Delete operations.
    /// Read validation is enforced via MeshCatalog.ValidateReadAsync for node reads.
    /// Update validation is enforced on the canonical <c>stream.Update</c> patch path
    /// by <c>RlsDataValidator</c> (the per-node hub re-checks RLS on the merge patch);
    /// this validator therefore no longer participates in Update.
    /// </summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Delete];

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

            // Per-user own-scope shortcut: every user owns the partition
            // named after their userId. A node at `{userId}` or `{userId}/…`
            // is in their own partition, granted unconditionally without
            // walking the access-rule chain.
            var nodePath = context.Node.Path;
            if (!string.IsNullOrEmpty(nodePath))
            {
                if (nodePath.Equals(userId, StringComparison.OrdinalIgnoreCase)
                    || nodePath.StartsWith(userId + "/", StringComparison.OrdinalIgnoreCase))
                    return Observable.Return(NodeValidationResult.Valid());
            }
        }

        var requiredPermission = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            NodeOperation.Create => GetCreatePermission(context.Node),
            NodeOperation.Delete => Permission.Delete,
            _ => Permission.None
        };

        if (requiredPermission == Permission.None)
            return Observable.Return(NodeValidationResult.Valid());

        // Compose: hub-rule → custom-rule → permission check. Each step returns
        // an observable; chain via SelectMany. A null result from one step means
        // "fall through" — re-emit by wrapping with Observable.Return; otherwise
        // pass to the next step in the chain.
        //
        // Take(1) closes the final stream: CheckPermission rides
        // SecurityService.HasPermission, which is hot and never completes
        // (lives on the live AccessAssignment synced query). Without Take(1)
        // the .Concat() in RunCreationValidatorsObs would wait forever on the
        // first validator and the create handler would never post a response.
        return CheckHubRule(context, userId)
            .SelectMany(hubResult => hubResult != null
                ? Observable.Return<NodeValidationResult?>(hubResult)
                : CheckCustomRule(context, userId))
            .SelectMany(customResult => customResult != null
                ? Observable.Return(customResult)
                : CheckPermission(context, userId, requiredPermission))
            .Take(1);
    }

    private IObservable<NodeValidationResult?> CheckHubRule(NodeValidationContext context, string? userId)
    {
        if (string.IsNullOrEmpty(context.Node.NodeType))
            return Observable.Return<NodeValidationResult?>(null);

        // _nodeTypeService.GetAccessRule was always returning null
        // (the underlying _accessRules dict in NodeTypeService was never
        // populated). Removed in Stage 4 of the NodeTypeService deletion —
        // hub-rule path falls through to the next rule in the chain.
        return Observable.Return<NodeValidationResult?>(null);
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
            ? _hub.GetEffectivePermissions(pathToCheck, effectiveUserId)
                .Select(p => p.HasFlag(Permission.Comment) || p.HasFlag(Permission.Update))
            : _hub.CheckPermission(pathToCheck, effectiveUserId, requiredPermission);

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
