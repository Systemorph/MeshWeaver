// 🚨 The NAMESPACE is part of the binary contract, and it does NOT follow the assembly.
// This type used to live in MeshWeaver.AI; a module compiled before it moved holds a TypeRef to
// `MeshWeaver.AI.IMasterKeyProvider` scoped to the `MeshWeaver.AI` AssemblyRef. The
// [assembly: TypeForwardedTo] in src/MeshWeaver.AI/TypeForwards.cs redirects that TypeRef here —
// but ONLY while the full type NAME is unchanged, because a forwarder CANNOT rename. So
// `namespace MeshWeaver.AI;` inside MeshWeaver.Mesh.Contract is DELIBERATE AND PERMANENT.
// Tidying it to `MeshWeaver.Mesh.Security` is the #2370 outage all over again (#2398);
// MovedTypeBinaryContractTest and scripts/check-type-forwards.py both refuse it.
namespace MeshWeaver.AI;

/// <summary>
/// Supplies the symmetric master key used by <see cref="IProviderKeyProtector"/>
/// to encrypt/decrypt stored provider credentials. Pluggable so the master key
/// can come from configuration (the default — see <see cref="ConfigMasterKeyProvider"/>)
/// or from an external KMS / Azure Key Vault in a hardened deployment.
///
/// <para>Returns <c>null</c> when no master key is configured. That is a CONFIGURATION FAULT, not
/// a degraded mode: <see cref="IProviderKeyProtector.Protect"/> then REFUSES (throws) rather than
/// storing the secret in plaintext, while <see cref="IProviderKeyProtector.Unprotect"/> stays
/// tolerant so an instance holding legacy plaintext keeps reading it.</para>
///
/// <para>🚨 This is also the seam a caller queries when it has a structured, non-writing refusal to
/// report instead of a throw — the boot seed's <c>ProviderSeedOutcome.RefusedUnprotected</c> asks
/// <see cref="GetMasterKey"/> before it protects, so one unconfigured provider is reported rather
/// than faulting the whole seed. It is a question, never a licence: a caller answered <c>null</c>
/// must not write the secret by any other route.</para>
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// The 32-byte (AES-256) master key, or <c>null</c> when none is configured — in which case
    /// nothing new may be encrypted, and nothing new may be stored.
    /// Implementations should cache — this is called on every protect/unprotect.
    /// </summary>
    byte[]? GetMasterKey();
}
