namespace MeshWeaver.AI;

/// <summary>
/// Supplies the symmetric master key used by <see cref="IProviderKeyProtector"/>
/// to encrypt/decrypt stored provider credentials. Pluggable so the master key
/// can come from configuration (the default — see <see cref="ConfigMasterKeyProvider"/>)
/// or from an external KMS / Azure Key Vault in a hardened deployment.
///
/// <para>Returns <c>null</c> when no master key is configured. In that case
/// <see cref="IProviderKeyProtector"/> operates in passthrough mode (keys are
/// stored as plaintext, exactly as before this feature existed) so a dev box or
/// test with no key configured keeps working.</para>
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// The 32-byte (AES-256) master key, or <c>null</c> if encryption is disabled.
    /// Implementations should cache — this is called on every protect/unprotect.
    /// </summary>
    byte[]? GetMasterKey();
}
