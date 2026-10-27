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
[ValidateDeletePermission]
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

    /// <summary>
    /// With <see cref="ExpectedVersion"/>: the id of the OWNER's seal this version-guarded delete asks
    /// for (#6856). When the version matches, the owning hub takes the seal in the same commit turn
    /// (<see cref="Services.RecentlyDeletedRegistry.TakeOwnerDeleteSeal"/>), defers its own writes to
    /// the path while it is held, and keeps it until the deleter sends <see cref="ReleaseDeleteSeal"/>
    /// with this id, or until the owning activation is torn down. Null asks for no seal (an older
    /// deleter); an owner that predates the field ignores it and answers without
    /// <see cref="ValidateDeleteResponse.SealedBy"/>.
    /// </summary>
    public string? SealId { get; init; }
}

/// <summary>
/// The deleter's COMMIT or ABORT message for a version-guarded delete (#6856), posted to the
/// node-operation hub of the process whose owner took the seal <see cref="SealId"/> when it confirmed
/// the version (<see cref="ValidateDeleteResponse.SealedBy"/>), or to the node itself when no answer
/// named that process. Either hub releases the seal in its process's registry. It releases that
/// seal: on <see cref="DeleteSealOutcome.Committed"/> the owner tombstones the path and refuses every
/// own write the seal deferred (the node is deleted); on <see cref="DeleteSealOutcome.Aborted"/> it
/// applies them. A successful delete waits for the answer before it reports success, so a caller
/// that recreates the path at once is not refused by the owner's row guard. A seal whose message never
/// arrives — the deleter died — is released by the owning activation's own teardown, never by a timer.
/// <para><see cref="InfrastructureOnlyAttribute"/>: only the mesh's own hubs may post it; a remote
/// participant's delivery is refused at ingress. It carries no permission attribute, so it is NOT ordered behind the version check's
/// permission fold, and an abort can overtake the check. The owner then leaves an abort mark for the
/// seal id (bound to its activation), and the check that runs later refuses to take the seal. The
/// seal id is an unguessable per-delete value, so the message can release only the seal of the delete
/// that sent it.</para>
/// </summary>
/// <param name="Path">The node the delete targeted (the owning hub's own path).</param>
/// <param name="SealId">The seal id the delete's <see cref="ValidateDeleteRequest.SealId"/> carried.</param>
/// <param name="Outcome">A <see cref="DeleteSealOutcome"/> value.</param>
[InfrastructureOnly]
public sealed record ReleaseDeleteSeal(string Path, string SealId, string Outcome) : IRequest<ReleaseDeleteSealResponse>;

/// <summary>The owner process's answer to <see cref="ReleaseDeleteSeal"/>.</summary>
/// <param name="Released">True when a seal held for the id was released by this message.</param>
public sealed record ReleaseDeleteSealResponse(bool Released);

/// <summary>
/// Why a delete seal was lifted (#6856) — an OPEN vocabulary of string constants. An own write the
/// seal deferred is refused after <see cref="Committed"/> and <see cref="OwnerTeardown"/>, and re-runs
/// its commit turn after anything else, where the turn's own checks (seal, tombstone) decide.
/// </summary>
public static class DeleteSealOutcome
{
    /// <summary>The delete removed the node: deferred writes are refused, the path is tombstoned.</summary>
    public const string Committed = nameof(Committed);

    /// <summary>The delete gave up after the owner's check: deferred writes are applied.</summary>
    public const string Aborted = nameof(Aborted);

    /// <summary>
    /// The owning activation was torn down while it held the seal — the deleter's message never came.
    /// Deferred writes are refused (they were never acknowledged); the next activation is unsealed.
    /// </summary>
    public const string OwnerTeardown = nameof(OwnerTeardown);

    /// <summary>
    /// The delete gave up while its root storage delete may still be running (its observing subscription
    /// timed out or was cancelled after the call was issued): neither a commit nor an abort is known. The
    /// owner runs the store fence, which decides — the row moved past the confirmed version (deferred
    /// writes run) or the row is gone (refused, the path tombstoned) — and stays sealed until it has.
    /// </summary>
    public const string Indeterminate = nameof(Indeterminate);

    /// <summary>A deleter-side seal in this process was lifted; the deferred write re-checks in its turn.</summary>
    public const string Lifted = nameof(Lifted);
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

    /// <summary>
    /// On a confirmed version-guarded ask that carried <see cref="ValidateDeleteRequest.SealId"/>: the
    /// node-operation hub of the PROCESS whose owner now holds the seal (#6856) — where the deleter
    /// sends its <see cref="ReleaseDeleteSeal"/>. Not the node's own address: once the delete has
    /// removed the row, that address no longer resolves, while the process's node-operation hub still
    /// does. Null: no seal was taken (an owner that predates the field, or an ask without a seal id).
    /// </summary>
    public Address? SealedBy { get; init; }

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

/// <summary>
/// Permission attribute for <see cref="ValidateDeleteRequest"/>: the same demand the delete itself
/// makes (<see cref="DeletePermission.RequiredFor"/>), so the pre-flight and the delete cannot
/// disagree about who may remove the node.
/// </summary>
public class ValidateDeletePermissionAttribute() : RequiresPermissionAttribute(Permission.Delete)
{
    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        yield return (hubPath, delivery.Message is ValidateDeleteRequest validate
            ? DeletePermission.RequiredFor(validate.Path)
            : Permission.Delete);
    }
}
