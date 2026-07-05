using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Structural guard that makes a USER partition root undeletable by an interactive caller.
///
/// <para>A user's partition root is their home node — <c>{userId}</c> at the root namespace
/// (<c>''</c>) with NodeType <c>User</c> (the exact shape <c>UserOnboardingService.CreateUser</c>
/// writes). A recursive delete of that node wipes the user's ENTIRE partition — threads, API
/// tokens, model settings, the self-admin grant — and drops the user into the onboarding
/// redirect on their next sign-in. That is precisely the catastrophic incident this guard exists
/// to make impossible: a node-menu Delete invoked on a thread anchored directly under the home
/// (the thread's <c>MainNode = {userId}</c>) resolved to the partition root and deleted the whole
/// partition. The menu-target fix (<c>NodeMenuItemsExtensions.GetMenuContext</c>) stops the menu
/// from ever aiming at the owner; this guard is the defence-in-depth that rejects the delete even
/// if some other caller aims at a user root directly.</para>
///
/// <para>Runs on <see cref="NodeOperation.Delete"/> only, alongside the other delete validators
/// (they AND-compose — the first failure wins), and — as an <see cref="IOwnerEnforcedNodeValidator"/>
/// — authoritatively on the owning per-node hub where the delete ROOT is validated
/// (<c>MeshExtensions.RunDeletionValidatorsWithWarningsObs</c>). A rejection on the root aborts the
/// whole cascade before any fan-out fires.</para>
///
/// <para><b>System is exempt.</b> Legitimate infrastructure / deliberate admin off-boarding runs
/// under <c>ImpersonateAsSystem</c> and may still remove a user partition; only the interactive
/// path — the user's own circuit or an admin's circuit — is blocked, which is exactly where the
/// accidental menu delete originates. <c>Space</c> roots stay deletable (they own a partition too
/// but carry an explicit <c>PartitionDropPostDeletionHandler</c> teardown); this guard is scoped
/// to <c>User</c> roots only.</para>
/// </summary>
public sealed class PartitionRootDeletionGuard : INodeValidator, IOwnerEnforcedNodeValidator
{
    private readonly ILogger<PartitionRootDeletionGuard>? _logger;

    /// <summary>Initializes the guard.</summary>
    /// <param name="logger">Optional logger; blocked deletes are recorded at Warning.</param>
    public PartitionRootDeletionGuard(ILogger<PartitionRootDeletionGuard>? logger = null)
        => _logger = logger;

    /// <summary>Delete only — this guard never affects create/read/update/move.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    /// <summary>
    /// Rejects the delete when the target node is a user partition root and the caller is not the
    /// System identity; otherwise accepts.
    /// </summary>
    /// <param name="context">The delete validation context (the ROOT node of the operation).</param>
    /// <returns>An observable emitting exactly one <see cref="NodeValidationResult"/>.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!IsUserPartitionRoot(context.Node))
            return Observable.Return(NodeValidationResult.Valid());

        // System (middleware / onboarding / deliberate admin off-boarding) may still remove a
        // user partition; only interactive callers are blocked. Same System-exempt pattern as
        // PartitionWriteGuardValidator.
        var userId = context.AccessContext?.ObjectId;
        if (string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        _logger?.LogWarning(
            "PartitionRootDeletionGuard: blocked Delete of user partition root '{Path}' by {User}",
            context.Node.Path, userId ?? "(anonymous)");
        return Observable.Return(NodeValidationResult.Invalid(
            $"'{context.Node.Path}' is a user partition root (home) and cannot be deleted — that would " +
            "remove the entire partition (threads, tokens, settings, access grants) and lock the user out. " +
            "Delete the specific child node (e.g. the thread) instead.",
            NodeRejectionReason.Unauthorized));
    }

    /// <summary>
    /// A USER partition root: a bare single-segment path (no <c>/</c>, not a <c>_</c>-satellite) at
    /// the root namespace (<c>''</c>) whose NodeType is <c>User</c> — the
    /// <c>UserOnboardingService.CreateUser</c> partition-root shape. Shared with the node-menu
    /// suppression so "what is protected" has one definition.
    /// </summary>
    /// <param name="node">The node to classify; may be null.</param>
    public static bool IsUserPartitionRoot(MeshNode? node)
        => node is not null
           && string.Equals(node.NodeType, "User", StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrEmpty(node.Namespace)
           && !string.IsNullOrWhiteSpace(node.Path)
           && !node.Path.Contains('/')
           && !node.Path.StartsWith('_');
}
