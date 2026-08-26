// 🚨 The NAMESPACE is part of the binary contract, and it does NOT follow the assembly.
// This type used to live in MeshWeaver.AI; a module compiled before it moved holds a TypeRef to
// `MeshWeaver.AI.IProviderKeyProtector` scoped to the `MeshWeaver.AI` AssemblyRef. The
// [assembly: TypeForwardedTo] in src/MeshWeaver.AI/TypeForwards.cs redirects that TypeRef here —
// but ONLY while the full type NAME is unchanged, because a forwarder CANNOT rename. So
// `namespace MeshWeaver.AI;` inside MeshWeaver.Mesh.Contract is DELIBERATE AND PERMANENT.
// Tidying it to `MeshWeaver.Mesh.Security` is the #2370 outage all over again (#2398);
// MovedTypeBinaryContractTest and scripts/check-type-forwards.py both refuse it.
namespace MeshWeaver.AI;

/// <summary>
/// Encrypts / decrypts a literal credential before it is persisted to (and read back from) the
/// mesh — i.e. Postgres. Answers "is it safe to keep secrets in PG": with a master key configured
/// the value at rest is AES-256-GCM ciphertext, so a DB / backup leak alone yields no usable key.
///
/// <para>Callers today: a model provider's <c>ApiKey</c>, a GitHub PAT
/// (<c>GitHubCredential</c>), the Entra EA credential, and the plugin catalog's sync-token
/// signing key. It lives in the platform rather than with any one of them precisely because it
/// is none of their concern — see Systemorph/MeshWeaver#2276.</para>
///
/// <para>Backward compatible on the way OUT: <see cref="Protect"/> is idempotent and
/// <see cref="Unprotect"/> passes through any value not carrying the
/// <c>enc:</c> tag, so pre-existing plaintext rows keep working and re-saving
/// re-encrypts them.</para>
///
/// <para>🚨 <b>The asymmetry is deliberate: fail on the way IN, tolerate on the way OUT.</b> With
/// no master key configured (see <see cref="IMasterKeyProvider"/>) <see cref="Protect"/> THROWS —
/// it used to pass the plaintext through, which is how an unconfigured deployment persisted a live
/// provider key in the clear (production, 2026-08-24). <see cref="Unprotect"/> stays a passthrough
/// in the same state, so an instance already holding legacy plaintext keeps working after an
/// upgrade instead of breaking on data the platform itself wrote; it just cannot write any more
/// until it is configured.</para>
/// </summary>
public interface IProviderKeyProtector
{
    /// <summary>
    /// Returns an <c>enc:v1:</c>-tagged ciphertext for <paramref name="plaintext"/>,
    /// or the input unchanged when it is null/empty or already tagged.
    ///
    /// <para>🚨 THROWS <see cref="InvalidOperationException"/> when no master key is configured —
    /// a missing master key is a configuration fault, not a degraded mode, and the alternative is
    /// persisting the secret in plaintext. A caller with a structured, non-writing refusal to
    /// report asks <see cref="IMasterKeyProvider"/> first and skips the write; everywhere else the
    /// throw is the intended answer.</para>
    /// </summary>
    string? Protect(string? plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Returns the input unchanged when it is
    /// null/empty or untagged (legacy plaintext); returns <c>null</c> when a
    /// tagged value cannot be decrypted (wrong/missing master key).
    /// </summary>
    string? Unprotect(string? stored);
}
