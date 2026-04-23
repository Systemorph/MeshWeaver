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
/// <see cref="Permission.Delete"/> via <see cref="ISecurityService"/> BEFORE
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
[RequiresPermission(Permission.Delete)]
public sealed record ValidateDeleteRequest(string Path) : IRequest<ValidateDeleteResponse>;

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

    public bool IsValid => Errors.IsEmpty;
    public bool HasWarnings => !Warnings.IsEmpty;

    public static ValidateDeleteResponse Ok() => new();

    public static ValidateDeleteResponse FromError(string error) =>
        new() { Errors = [error] };

    public static ValidateDeleteResponse FromWarning(string warning) =>
        new() { Warnings = [warning] };
}
