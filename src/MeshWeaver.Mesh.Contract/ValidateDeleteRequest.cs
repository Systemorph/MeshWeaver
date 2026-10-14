using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Per-node pre-flight validation for deletion. Posted by the central
/// <c>DeleteNodeRequest</c> handler to every node in the subtree about to be
/// deleted; the owning hub runs its own <see cref="Services.INodeValidator"/>
/// chain (and any custom business rules) and responds with the aggregated
/// outcome.
///
/// <para><b>Separation of concerns.</b> The central handler already checks
/// <see cref="Permission.Delete"/> via <c>SecurityService</c> BEFORE
/// issuing this request, so validator code can focus on domain constraints
/// ("this is the last admin", "this has open obligations") rather than
/// repeating permission checks.</para>
///
/// <para><b>Errors vs Warnings.</b>
/// <list type="bullet">
/// <item><description><b>Errors</b> block the delete unconditionally — the central
/// handler returns a failed <c>DeleteNodeResponse</c> with the errors in the
/// <see cref="Data.ActivityLog"/>.</description></item>
/// <item><description><b>Warnings</b> block unless the caller sets
/// <c>DeleteNodeRequest.ConfirmWarnings = true</c>. First call returns the
/// warnings list so the UI can surface a confirmation dialog; second call
/// (with the flag set) proceeds.</description></item>
/// </list>
/// </para>
/// </summary>
/// <param name="Path">Full path of the node to validate for deletion.</param>
/// <param name="RootPath">
/// Root of the cascade this validation belongs to — i.e. the original
/// <see cref="DeleteNodeRequest.Path"/> when this node is being deleted as a descendant
/// of a larger subtree (recursive delete fan-out). Null when the node is being deleted
/// in isolation. Validators that enforce "must keep at least one X on a partition"
/// invariants (e.g. the last-admin rule) use this to recognise that the whole partition
/// is going away — deleting the last admin of a space is fine when the space itself is
/// part of the same delete.
/// </param>
[RequiresPermission(Permission.Delete)]
public sealed record ValidateDeleteRequest(string Path, string? RootPath = null) : IRequest<ValidateDeleteResponse>
{
    /// <summary>
    /// When set, this request is the OWNER-TURN half of a version-guarded delete
    /// (<see cref="DeleteNodeRequest.ExpectedVersion"/>, #6673), and it asks nothing else: the
    /// owning hub compares this value with its node's LIVE version inside its own commit turn —
    /// the turn every own write and every cross-hub patch commits on — and answers
    /// <see cref="ValidateDeleteResponse.Ok"/> — with <see cref="ValidateDeleteResponse.ConfirmedVersion"/>
    /// echoing this value, so an owner that never compared cannot pass for one that did — when they match,
    /// <see cref="NodeDeletionRejectionReason.Conflict"/> (with
    /// <see cref="ValidateDeleteResponse.ActualVersion"/>) when they do not, and
    /// <see cref="NodeDeletionRejectionReason.NodeNotFound"/> when the node is gone. The validator
    /// chain is NOT run: the delete already ran it at the root.
    /// <para>The live version includes a write the owner has acknowledged but not yet stored, which
    /// the durable row does not — that is the whole point of asking the owner.</para>
    /// </summary>
    public long? ExpectedVersion { get; init; }
}

/// <summary>
/// Outcome of a <see cref="ValidateDeleteRequest"/>. Empty lists mean
/// "no objection". See that type for semantics of errors vs warnings.
/// </summary>
public sealed record ValidateDeleteResponse
{
    /// <summary>Blocking problems. Any entry means delete must fail.</summary>
    public ImmutableList<string> Errors { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Advisory problems. Surface to the caller; delete proceeds only if the
    /// outer <c>DeleteNodeRequest.ConfirmWarnings</c> is true.
    /// </summary>
    public ImmutableList<string> Warnings { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Why the delete was refused, when it was. Meaningless while <see cref="IsValid"/> is true.
    ///
    /// <para>🚨 It exists because a descendant's refusal is not always a VERDICT — issue #1198. A
    /// validator whose permission fold never answered reports
    /// <see cref="Services.NodeRejectionReason.Unavailable"/>, and collapsing that into
    /// <see cref="NodeDeletionRejectionReason.ValidationFailed"/> on the way back to the caller
    /// files an availability incident as a policy decision, which is precisely the mis-reporting
    /// #1446 removed one level further in. The caller re-raises whatever arrives here.</para>
    /// </summary>
    public NodeDeletionRejectionReason Reason { get; init; } =
        NodeDeletionRejectionReason.ValidationFailed;

    /// <summary>
    /// The node's live version on the owner, when a version-guarded ask
    /// (<see cref="ValidateDeleteRequest.ExpectedVersion"/>) found it different. Null otherwise.
    /// </summary>
    public long? ActualVersion { get; init; }

    /// <summary>
    /// The <see cref="ValidateDeleteRequest.ExpectedVersion"/> the owner COMPARED and found equal to
    /// its live version, echoed back on a version-guarded ask's <see cref="Ok"/>. Null otherwise.
    ///
    /// <para>🚨 It is the capability echo that makes a mixed-version roll safe (#6750). An owner on
    /// an image that predates the version-guarded check does not know
    /// <see cref="ValidateDeleteRequest.ExpectedVersion"/>: its deserializer skips the unknown member,
    /// it runs the ordinary validator chain, and it answers a plain <see cref="Ok"/>. Read as "the
    /// version matched", that let a guarded delete through unchecked. The asker therefore treats a
    /// valid answer WITHOUT this echo (or with a different value) as no verdict —
    /// <see cref="NodeDeletionRejectionReason.Unavailable"/> — never as a match.</para>
    /// </summary>
    public long? ConfirmedVersion { get; init; }

    /// <summary>True when there are no blocking errors.</summary>
    public bool IsValid => Errors.IsEmpty;

    /// <summary>True when at least one advisory warning was reported.</summary>
    public bool HasWarnings => !Warnings.IsEmpty;

    /// <summary>Empty response — no errors, no warnings.</summary>
    public static ValidateDeleteResponse Ok() => new();

    /// <summary>Response with a single blocking error.</summary>
    public static ValidateDeleteResponse FromError(
        string error,
        NodeDeletionRejectionReason reason = NodeDeletionRejectionReason.ValidationFailed) =>
        new() { Errors = [error], Reason = reason };

    /// <summary>Response with a single advisory warning.</summary>
    public static ValidateDeleteResponse FromWarning(string warning) =>
        new() { Warnings = [warning] };
}
