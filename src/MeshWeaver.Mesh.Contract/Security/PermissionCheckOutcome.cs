namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The outcome of ONE permission check — the seam that keeps "we could not find out" apart from
/// "we found out, and the answer is no" (issue #974, split out of #637).
///
/// <para>Exactly three shapes:
/// <list type="bullet">
///   <item><description><b>Granted</b> — the fold produced a verdict and the caller HAS the
///     permission.</description></item>
///   <item><description><b>Denied</b> — the fold produced a verdict and the caller does NOT have
///     the permission. A DEFINITIVE negative: the grants were read and they do not cover this
///     (path, permission).</description></item>
///   <item><description><b>Undetermined</b> (<see cref="UndeterminedReason"/> non-null) — the fold
///     FAULTED, so no verdict exists. The caller must report unavailability, NEVER a denial and
///     NEVER a grant. <see cref="IsGranted"/> is <c>false</c> here so a consumer that ignores the
///     tri-state still fails CLOSED — but a consumer that reports it as "Access denied" is
///     lying.</description></item>
/// </list></para>
///
/// <para><b>Why the distinction has to live at the fold.</b> Only the fold knows whether it
/// reached an answer. Collapsing a fault into the same <c>false</c> a real denial produces is what
/// sent correctly-entitled users to ask for permissions they already held: "Access denied: user 'x'
/// lacks Read permission on 'y'" is an ACTIONABLE-LOOKING sentence, and it was false. Once
/// collapsed, no caller can recover the difference — a message-string sniff further up the stack is
/// a guess that drifts the moment someone rewords an exception.</para>
///
/// <para>This is the authorization-side twin of <c>IdentityReadOutcome&lt;T&gt;</c> (#970) on the
/// identity side. Same rule, same reason.</para>
/// </summary>
public sealed record PermissionCheckOutcome
{
    /// <summary>
    /// True only on a positive verdict. <c>false</c> covers BOTH a definitive denial and an
    /// undetermined fold — deliberately, so a consumer that forgets the tri-state still fails
    /// closed. Branch on <see cref="IsUndetermined"/> before reporting a denial to a user.
    /// </summary>
    public bool IsGranted { get; init; }

    /// <summary>
    /// Why the fold reached no verdict, or <c>null</c> when it produced one (granted OR denied).
    /// A diagnostic for operators — never the string a consumer branches on.
    /// </summary>
    public string? UndeterminedReason { get; init; }

    /// <summary>True when the fold reached NO verdict — retryable, and never a statement about
    /// this caller's rights in either direction.</summary>
    public bool IsUndetermined => UndeterminedReason is not null;

    /// <summary>The fold produced a verdict: the caller HAS the permission.</summary>
    public static PermissionCheckOutcome Granted { get; } = new() { IsGranted = true };

    /// <summary>The fold produced a verdict: the caller does NOT have the permission.</summary>
    public static PermissionCheckOutcome Denied { get; } = new() { IsGranted = false };

    /// <summary>
    /// The fold FAULTED — no verdict. Report unavailable (retryable), never denied.
    /// </summary>
    public static PermissionCheckOutcome Undetermined(string reason) =>
        new() { IsGranted = false, UndeterminedReason = reason };

    /// <summary>Maps a plain boolean verdict from the fold onto the tri-state.</summary>
    public static PermissionCheckOutcome FromVerdict(bool granted) => granted ? Granted : Denied;
}
