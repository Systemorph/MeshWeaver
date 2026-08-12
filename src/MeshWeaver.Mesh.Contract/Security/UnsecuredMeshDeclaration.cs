using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 THE EXPLICIT DECLARATION THAT THIS MESH HAS NO ACCESS CONTROL.
///
/// <para><b>Why this type exists at all.</b> Until it did, "this deployment deliberately runs
/// without row-level security" and "somebody forgot to call <c>AddRowLevelSecurity()</c>" were the
/// SAME observable state: no <see cref="EffectivePermissionsDelegate"/> registered. Every consumer
/// then had to guess which one it was looking at, and the guesses did not agree — some fail closed
/// (<see cref="AnonymousGate"/>, <c>HandleGetPermission</c>), some fail wide open
/// (<c>ResolveEvaluator</c> falls back to an evaluator that answers <see cref="Permission.All"/>).
/// That is precisely the shape this repo forbids in CI gates and forbids here for the same reason:
/// <b>a gate that never ran must never be indistinguishable from a gate that passed.</b></para>
///
/// <para><b>The rule.</b> Running without a gate is now a STATEMENT, never an inference. A host that
/// means it says so — <c>builder.AllowUnsecuredMesh("reason")</c> — and that declaration is logged
/// loudly at startup. A host that does not say so, and maps the security-relevant HTTP routes, is
/// refused at boot rather than quietly serving every partition to every caller.</para>
///
/// <para><b>Legitimate uses.</b> Single-user embedded/sidecar meshes (the local gRPC sidecar), and
/// unit-test fixtures that exercise routing or layout rather than access. Both genuinely want no
/// evaluator; both should have to say so out loud.</para>
/// </summary>
/// <param name="Reason">
/// Why this mesh runs ungated. Free text, required, and written verbatim into the startup log —
/// its whole job is to make the next reader able to tell "deliberate" from "regression" without
/// archaeology.
/// </param>
public sealed record UnsecuredMeshDeclaration(string Reason);

/// <summary>
/// Declares — out loud — that a mesh runs without access control. See
/// <see cref="UnsecuredMeshDeclaration"/> for why the absence of a delegate is not allowed to mean
/// this on its own.
/// </summary>
public static class UnsecuredMeshExtensions
{
    /// <summary>
    /// Declares this hub's mesh as deliberately ungated. Without this, a host that maps the
    /// security-relevant routes while registering no <see cref="EffectivePermissionsDelegate"/> is
    /// refused at startup.
    /// </summary>
    /// <param name="config">The hub configuration to mark.</param>
    /// <param name="reason">Why this mesh runs without access control; logged at startup.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration AllowUnsecuredMesh(
        this MessageHubConfiguration config, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return config.Set(new UnsecuredMeshDeclaration(reason));
    }
}
