namespace MeshWeaver.AI;

/// <summary>
/// Encrypts / decrypts the literal credential stored on a
/// <see cref="ModelProviderConfiguration.ApiKey"/> before it is persisted to
/// (and read back from) the mesh — i.e. Postgres. Answers "is it safe to keep
/// LLM keys in PG": with a master key configured the value at rest is AES-256-GCM
/// ciphertext, so a DB / backup leak alone yields no usable key.
///
/// <para>Backward compatible: <see cref="Protect"/> is idempotent and
/// <see cref="Unprotect"/> passes through any value not carrying the
/// <c>enc:</c> tag, so pre-existing plaintext rows keep working and re-saving
/// re-encrypts them. With no master key configured (see
/// <see cref="IMasterKeyProvider"/>) both methods are pure passthrough.</para>
/// </summary>
public interface IProviderKeyProtector
{
    /// <summary>
    /// Returns an <c>enc:v1:</c>-tagged ciphertext for <paramref name="plaintext"/>,
    /// or the input unchanged when it is null/empty, already tagged, or encryption
    /// is disabled.
    /// </summary>
    string? Protect(string? plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Returns the input unchanged when it is
    /// null/empty or untagged (legacy plaintext); returns <c>null</c> when a
    /// tagged value cannot be decrypted (wrong/missing master key).
    /// </summary>
    string? Unprotect(string? stored);
}
