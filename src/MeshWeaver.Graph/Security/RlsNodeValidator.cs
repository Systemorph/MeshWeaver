using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly TimeSpan _establishmentBudget;

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
        // Genuinely optional configuration — a host that sets nothing gets the production default.
        _establishmentBudget =
            (hub.ServiceProvider.GetService<IOptions<RowLevelSecurityOptions>>()?.Value
                ?? new RowLevelSecurityOptions()).PermissionEstablishmentBudget;
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
        var pathToCheck = PathToCheck(context);

        return CheckHubRule(context, userId)
            .SelectMany(hubResult => hubResult != null
                ? Observable.Return<NodeValidationResult?>(hubResult)
                : CheckCustomRule(context, userId))
            .SelectMany(customResult => customResult != null
                ? Observable.Return(customResult)
                : CheckPermission(context, userId, requiredPermission, pathToCheck))
            .TakeDecisionOutsideGate()
            // 🚨 THE TERMINAL. Placed here rather than inside CheckPermission so it covers EVERY
            // branch: CheckCustomRule's INodeTypeAccessRule implementations reach the same
            // permission fold, and the denial path's ownerless-partition probe is a mesh read too.
            // See UnestablishedCheck for why this chain otherwise has no terminal at all (#1446).
            .Timeout(_establishmentBudget)
            .Catch((Exception ex) =>
                Observable.Return(UnestablishedCheck(context, userId, pathToCheck, ex)));
    }

    /// <summary>
    /// The scope a decision is taken on: for a CREATE that is the PARENT (the node itself does not
    /// exist yet and carries no grants), for everything else the node's own path.
    /// </summary>
    private static string PathToCheck(NodeValidationContext context)
        => context.Operation == NodeOperation.Create
            ? context.Node.GetParentPath() ?? context.Node.Path
            : context.Node.Path;

    /// <summary>
    /// 🚨 The result when this validator reached NO decision — #1446, and the ONLY terminal the
    /// chain above is guaranteed to have.
    ///
    /// <para>Everything on the way to a verdict can fail to produce ANY outcome. The fold behind
    /// <c>GetEffectivePermissions</c> is a <c>CombineLatest</c> over the grant and policy reads of
    /// the target's scope and every ancestor scope — and, through its <c>Zip</c> against the
    /// <c>Public</c> evaluation of the same path, a second copy of that fold. A leg that STARVES
    /// (the cross-silo case: the owning activation lives on a peer silo that never answers) never
    /// emits, never completes and never errors — <c>SyncedQueryMeshNodes</c> gates on
    /// <c>SeenInitial</c> over a merge containing a Subject that is never completed, so the leg can
    /// only stall. <c>TakeDecisionOutsideGate</c>'s <c>Take(1)</c> bounds the number of EMISSIONS,
    /// not the wait, so <c>RunCreationValidatorsObs</c>'s <c>Concat</c> then blocked on validator #1
    /// with nothing left to end it: <c>CreateNodeRequest</c> sat <c>Executing</c> for 33 s until its
    /// CALLER's <c>RequestTimeout</c> gave up — which reports the caller's impatience rather than
    /// the read that starved.</para>
    ///
    /// <para>🚨 <b>This is not a ceiling that turns a slow check into a denial.</b> The answer past
    /// the budget is UNAVAILABLE — neither a grant (unsafe) nor a denial (a lie that sends a
    /// correctly-entitled caller to request permissions they already hold, and files an availability
    /// incident as a policy decision so nobody looks for the read that starved). Same vocabulary and
    /// the same reasoning as <c>CompilationStatus.Unavailable</c> for a starved SOURCE read (#1218),
    /// and as <c>PermissionCheckOutcome.Undetermined</c>, which already gives the message GATE this
    /// distinction for a FAULTED fold. A fault is folded in here for the same reason: it says the
    /// check could not be established, not that access is refused. Still fail-CLOSED — the operation
    /// does not proceed; it just stops claiming to know why.</para>
    /// </summary>
    private NodeValidationResult UnestablishedCheck(
        NodeValidationContext context, string? userId, string pathToCheck, Exception cause)
    {
        _logger.LogWarning(cause,
            "RLS: the {Operation} permission check on {Path} could NOT be established — the "
            + "effective-permission read of {CheckedPath} for {UserId} did not answer within "
            + "{Budget}. Reporting an availability failure, not a decision",
            context.Operation, context.Node.Path, pathToCheck, userId ?? "(anonymous)",
            _establishmentBudget);

        return NodeValidationResult.Unavailable(
            $"The {context.Operation} permission check for node '{context.Node.Path}' could not be "
            + $"established: the effective-permission read of '{pathToCheck}' did not answer within "
            + $"{_establishmentBudget.TotalSeconds:0}s. This is an availability failure, NOT a "
            + "decision about your access — the operation was not evaluated and may be retried.");
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
        NodeValidationContext context, string? userId, Permission requiredPermission,
        string pathToCheck)
    {
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
    /// A recursive-delete cascade leg executed under the delivery-stamped SYSTEM
    /// context acts as system; otherwise the explicit request identity
    /// (CreatedBy/UpdatedBy/DeletedBy) is checked first, then the AccessContext
    /// (the logged-in user).
    /// </summary>
    private static string? GetUserId(NodeValidationContext context)
    {
        // 🚨 A CASCADE-LEG delete executed under the SYSTEM context acts as system, even
        // though DeletedBy carries the original caller for the audit trail. The recursive
        // delete authorizes the WHOLE subtree up front under the caller (root permission
        // check + the [RequiresPermission(Delete)] gate on every descendant's pre-flight
        // ValidateDeleteRequest) and then commits under the system identity — because the
        // plan contains the subtree's own _Access grant satellites, and re-deriving the
        // principal from DeletedBy here re-decided that authorization MID-COMMIT against
        // state the cascade itself was deleting, aborting the subtree half-deleted
        // (issue #1128, "partial-deleted=31"). BOTH signals are required:
        //  • CascadeRootPath — only the subtree fan-out stamps it; and
        //  • the SYSTEM AccessContext — carried on the DELIVERY by the fan-out's explicit
        //    WithAccessContext stamp and threaded into this context by the delete handler.
        // A leaked ambient "system-security" (the bootstrap-ImpersonateAsSystem AsyncLocal
        // residue AccessControlPipeline.ResolveIdentity documents) has CascadeRootPath null
        // and therefore never matches — DeletedBy/CreatedBy stay authoritative for every
        // direct request, exactly as before (RlsIntegrationTests' DeletedBy shape).
        if (context.Request is DeleteNodeRequest { CascadeRootPath: not null }
            && context.AccessContext?.ObjectId == WellKnownUsers.System)
            return WellKnownUsers.System;

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
