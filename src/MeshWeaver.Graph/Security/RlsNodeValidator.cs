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
public class RlsNodeValidator : INodeValidator, IOwnerEnforcedNodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<RlsNodeValidator> _logger;
    private readonly IReadOnlyDictionary<string, INodeTypeAccessRule> _accessRules;

    /// <summary>
    /// Initializes a new instance of the row-level-security node validator.
    /// </summary>
    /// <param name="hub">The message hub used for permission checks.</param>
    /// <param name="logger">The logger used to record access grants and denials.</param>
    /// <param name="accessRules">The per-node-type access rules, indexed by node type (last registration wins per type).</param>
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
    /// This validator handles Read, Create, Update, and Delete operations.
    /// Read validation is enforced via MeshCatalog.ValidateReadAsync for node reads.
    /// Update is validated on the canonical <c>stream.Update</c> patch path: the
    /// per-node hub runs the registered <see cref="INodeValidator"/>s (this one
    /// included) before applying the RFC 7396 merge patch, in addition to the
    /// inbound <c>[RequiresPermission(Permission.Update)]</c> gate on
    /// <c>PatchDataRequest</c>.
    /// </summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <summary>
    /// Validates an operation against row-level security, granting system and own-scope
    /// writes outright and otherwise checking the hub rule, the custom per-type access
    /// rule, and the effective permissions for the required permission.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable that emits the validation result for the operation.</returns>
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
        //
        // Take(1) closes the final stream: CheckPermission rides
        // SecurityService.HasPermission, which is hot and never completes
        // (lives on the live AccessAssignment synced query). Without Take(1)
        // the .Concat() in RunCreationValidatorsObs would wait forever on the
        // first validator and the create handler would never post a response.
        //
        // 🚨 …and it is TakeDecisionOutsideGate, not a bare Take(1) — issue #899. This is the
        // single highest-leverage placement in the repo: every validated create / update /
        // delete funnels through here, and the CALLER chains the REAL WRITE onto this
        // verdict (RunCreationValidatorsObs → persist + WriteAndPublishCreated;
        // RunDeletionValidatorsObs → the delete + its publish). Both remaining branches
        // reach the permission fold — CheckCustomRule's INodeTypeAccessRule implementations
        // (SatelliteAccessRule, Space/User/PartitionNodeType) call hub.CheckPermission just
        // as CheckPermission does — and on a warm cache that fold emits synchronously during
        // Subscribe while holding its CombineLatest gate. Without the hop the write and its
        // (synchronous, by contract) change-feed fan-out run inside that lock, which is one
        // half of the two-hub lock-order inversion. Placing it here rather than inside
        // CheckPermission covers the custom-rule path too.
        return CheckHubRule(context, userId)
            .SelectMany(hubResult => hubResult != null
                ? Observable.Return<NodeValidationResult?>(hubResult)
                : CheckCustomRule(context, userId))
            .SelectMany(customResult => customResult != null
                ? Observable.Return(customResult)
                : CheckPermission(context, userId, requiredPermission))
            .TakeDecisionOutsideGate();
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

        // 🚨 Permission.Sync is a write-authoriser that bypasses the content read-only cap: a
        // partition whose _Policy denies Create/Update/Delete (Agent, Model, Doc) still admits a
        // static-repo SYNC write when the caller holds Sync. Sync does NOT grant Read — a private
        // partition stays private. So Sync is ORed in for write operations only.
        // See Permission.Sync / Doc/Architecture/StaticRepoImport.md.
        var isWrite = context.Operation
            is NodeOperation.Create or NodeOperation.Update or NodeOperation.Delete;

        IObservable<bool> hasPermissionObs = requiredPermission == Permission.Comment
            ? _hub.GetEffectivePermissions(pathToCheck, effectiveUserId)
                .Select(p => p.HasFlag(Permission.Comment) || p.HasFlag(Permission.Update) || p.HasFlag(Permission.Sync))
            : isWrite
                ? _hub.GetEffectivePermissions(pathToCheck, effectiveUserId)
                    .Select(p => p.HasFlag(requiredPermission) || p.HasFlag(Permission.Sync))
                : _hub.CheckPermission(pathToCheck, effectiveUserId, requiredPermission);

        // 🚨 TakeDecisionOutsideGate BEFORE the projection, not only at the end of Validate — the
        // denial branch below does REAL WORK (a storage probe for the ownerless-partition
        // diagnosis), and the permission fold emits synchronously while holding its CombineLatest
        // gate on a warm cache (#899). Taking the decision off the gate here keeps that I/O out of
        // the lock; the outer TakeDecisionOutsideGate in Validate stays for the other branches.
        return hasPermissionObs.TakeDecisionOutsideGate().SelectMany(hasPermission =>
        {
            if (hasPermission)
            {
                _logger.LogTrace(
                    "RLS: Access granted for user {UserId} - {Operation} on {Path}",
                    userId ?? "(anonymous)", context.Operation, context.Node.Path);
                return Observable.Return(NodeValidationResult.Valid());
            }

            _logger.LogDebug(
                "RLS: Access denied for user {UserId} - {Operation} on {Path} requires {Permission}",
                userId ?? "(anonymous)", context.Operation, context.Node.Path, requiredPermission);
            var denial = $"Access denied: {context.Operation} permission required for node '{context.Node.Path}'";

            // A write refused by a partition that carries NO grants at all is not an ordinary
            // permission decision — it is the #638 residue (a create that provisioned the
            // partition and never recorded its ownership), and "Access denied" tells nobody that.
            // Diagnose it HERE because this is where the generic message is minted; the probe runs
            // only on a denial, so the granted path is untouched.
            if (context.Operation is not (NodeOperation.Create or NodeOperation.Update))
                return Observable.Return(NodeValidationResult.Unauthorized(denial));

            return PartitionWriteGuardValidator.DescribeOwnerlessPartition(_hub, context.Node.Path)
                .Take(1)
                .Select(diagnosis => NodeValidationResult.Unauthorized(
                    diagnosis is null ? denial : $"{denial}. {diagnosis}"))
                .Catch<NodeValidationResult, Exception>(ex =>
                {
                    // The diagnosis is a courtesy on top of a decision already taken — a failing
                    // probe must never change the verdict, but it IS logged.
                    _logger.LogDebug(ex,
                        "RLS: ownerless-partition diagnosis failed for {Path} — reporting the plain denial",
                        context.Node.Path);
                    return Observable.Return(NodeValidationResult.Unauthorized(denial));
                });
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
