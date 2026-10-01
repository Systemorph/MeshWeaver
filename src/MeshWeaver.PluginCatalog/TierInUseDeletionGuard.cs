using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 Refuses deleting a plan TIER node (<c>Admin/Tiers/{id}</c>) while a registered instance still
/// stands on that plan (#5894).
///
/// <para><b>Why.</b> The ladder is data (<see cref="PlanTierLadder"/>): the tier node IS what makes a
/// plan id known. Delete it and every instance record still carrying that id reads as an UNKNOWN
/// plan, which decides at the baseline (<see cref="PlanTierRanks.CoversInstance"/>, fail closed) —
/// the instance silently loses every package above <c>free</c>. Measured on the public registry: the
/// node <c>Admin/Tiers/sme</c> was deleted while a dedicated client instance stood on <c>sme</c>, and
/// that instance was refused 18 packages, Mail and Teams among them, each reported as "this instance
/// is on free". The instances have to be moved to another plan first (Settings ▸ Instance grants).</para>
///
/// <para><b>What counts as "using".</b> An instance whose stored plan resolves
/// (<see cref="PlanTierRanks.Canonical"/>) to the tier's id; a record with no plan stands on the
/// baseline (<see cref="PlanTierRanks.BaselinePlan"/>), so it uses the <c>free</c> node. A node that
/// carries a RETIRED id (<see cref="PlanTierRanks.RetiredPlans"/>) is used by nobody — the retired id
/// resolves to its successor, whose own node ranks it — and stays deletable. Deleting the container
/// <c>Admin/Tiers</c> itself is refused while any instance is registered at all, because it would
/// delete every tier at once. A recursive delete rooted ABOVE the container (e.g. <c>Admin</c>) is
/// covered too: its pre-flight posts a <c>ValidateDeleteRequest</c> to every descendant, and each
/// tier node's own delete chain — this guard included — answers it.</para>
///
/// <para><b>Not covered: a MOVE.</b> <c>HandleMoveNodeRequest</c> is copy-then-<c>DeleteMany</c>
/// straight on storage and runs no <see cref="INodeValidator"/> for its source, so moving a tier node
/// out of <c>Admin/Tiers</c> drops it from the ladder unguarded — a framework gap shared by every
/// delete validator, not one this class can close by widening <see cref="SupportedOperations"/>.</para>
///
/// <para><b>System is NOT exempt.</b> The harm is the same whoever deletes the node, and the ladder
/// has no infrastructure path that deletes a tier node.</para>
///
/// <para>The instance census runs as System: instance records live in their registrants' partitions
/// and the deleting admin holds no read grant on them, so a census under the caller's identity would
/// count only the admin's own instances and wave the delete through. It reads the MERGED Initial of
/// the fan-in (every provider's answer), and a census that fails REFUSES the delete — an unknown
/// count is never "zero".</para>
/// </summary>
public sealed class TierInUseDeletionGuard(IMessageHub hub, ILogger<TierInUseDeletionGuard>? logger = null)
    : INodeValidator, IOwnerEnforcedNodeValidator
{
    /// <summary>How many using instances a refusal names before it summarises the rest.</summary>
    private const int MaxNamed = 10;

    /// <summary>Delete only — a tier node may be created, read and re-ranked freely. (A move runs no
    /// validator at all today; see the class remarks.)</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    /// <summary>Refuses the delete of a tier node (or of the tier container) that instances still use.</summary>
    /// <param name="context">The delete validation context (the ROOT node of the operation).</param>
    /// <returns>An observable emitting exactly one <see cref="NodeValidationResult"/>.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.Operation != NodeOperation.Delete)
            return Observable.Return(NodeValidationResult.Valid());

        var node = context.Node;
        var isContainer = string.Equals(node.Path, PlanTierLadder.Namespace, StringComparison.OrdinalIgnoreCase);
        var isTier = string.Equals(node.Namespace, PlanTierLadder.Namespace, StringComparison.OrdinalIgnoreCase);
        if (!isContainer && !isTier)
            return Observable.Return(NodeValidationResult.Valid());

        // A retired id is resolved to its successor everywhere, so no instance stands on its node.
        if (isTier && PlanTierRanks.SuccessorOf(node.Id) is not null)
            return Observable.Return(NodeValidationResult.Valid());

        var tierId = isTier ? PlanTierRanks.Canonical(node.Id) : null;
        return UsingInstances(tierId)
            .Select(users =>
            {
                if (users.IsEmpty)
                    return NodeValidationResult.Valid();
                var named = string.Join(", ", users.Take(MaxNamed));
                var more = users.Count > MaxNamed ? $" and {users.Count - MaxNamed} more" : "";
                var what = tierId is null
                    ? $"the plan ladder '{PlanTierLadder.Namespace}'"
                    : $"the plan tier '{tierId}'";
                logger?.LogWarning(
                    "TierInUseDeletionGuard: refused deleting {Path} by {User} — {Count} registered instance(s) still use it: {Instances}",
                    node.Path, context.AccessContext?.ObjectId ?? "(anonymous)", users.Count, named + more);
                return NodeValidationResult.Invalid(
                    $"Cannot delete {what}: {users.Count} registered instance(s) still stand on it ({named}{more}). "
                    + "Deleting it would make their plan unknown to the registry, which then licenses them only "
                    + "the free tier. Move them to another plan on Settings ▸ Instance grants first.",
                    NodeRejectionReason.ValidationFailed);
            })
            .Catch<NodeValidationResult, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "TierInUseDeletionGuard: could not establish which instances use {Path} — refusing the delete",
                    node.Path);
                return Observable.Return(NodeValidationResult.Invalid(
                    $"Cannot delete '{node.Path}' now: the registry could not establish which instances stand on it "
                    + $"({ex.Message}). Try again.",
                    NodeRejectionReason.Unavailable));
            });
    }

    /// <summary>
    /// The ids of the registered instances standing on <paramref name="tierId"/> — or, for null, on
    /// any plan at all. Read as System from the fan-in's merged Initial.
    /// </summary>
    private IObservable<ImmutableList<string>> UsingInstances(string? tierId)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Instances are filed under whichever user registered them — no partition to anchor to, so
        // the census declares its fan-out (#3202), exactly as FindInstancePath does.
        var request = new MeshQueryRequest
        {
            Query = MeshWideQuery.Declare($"nodeType:{MeshWeaverInstanceNodeType.NodeType}"),
        };
        return accessService.RunAsSystem(() => meshService.Query<MeshNode>(request))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(change => change.Items
                // The key-hash index rows share the NodeType; they are routing hints, not instances.
                .Where(n => !n.Path.StartsWith(MeshWeaverInstanceNodeType.IndexNamespace + "/", StringComparison.Ordinal))
                .Select(n => n.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions))
                .OfType<MeshWeaverInstance>()
                .Where(instance => tierId is null || StandsOn(instance, tierId))
                .Select(instance => instance.InstanceId)
                .Order(StringComparer.Ordinal)
                .ToImmutableList());
    }

    /// <summary>Whether <paramref name="instance"/>'s plan — the baseline when it stores none —
    /// resolves to <paramref name="tierId"/>.</summary>
    internal static bool StandsOn(MeshWeaverInstance instance, string tierId) =>
        (PlanTierRanks.Canonical(instance.Plan) is { Length: > 0 } plan ? plan : PlanTierRanks.BaselinePlan) == tierId;
}
