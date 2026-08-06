using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The instance-key contract — one place for BOTH sides (the portal that issues a key and the
/// registry surface that validates one), mirroring how <c>PluginRegistryTokens</c> and
/// <c>PluginRegistryPayloads</c> keep producer and consumer from drifting.
///
/// <para>Same mechanism as personal API tokens (<see cref="ValidateTokenRequest.HashToken"/>):
/// 32 random bytes, URL-safe base64, a distinguishing prefix, and only the SHA-256 hash is ever
/// persisted. The <b>prefix differs on purpose</b> — <c>mwi_</c> vs the personal <c>mw_</c> — so a
/// key that turns up in a log or a config file is immediately identifiable as an instance
/// credential, and so neither validator can ever accept the other's key by accident.</para>
/// </summary>
public static class InstanceKeys
{
    /// <summary>Prefix on every issued instance key. Distinct from the personal-token
    /// <c>mw_</c> prefix — an instance key is not a user credential.</summary>
    public const string KeyPrefix = "mwi_";

    /// <summary>Entropy per key, in bytes.</summary>
    public const int KeyByteLength = 32;

    /// <summary>The HTTP auth scheme an instance key travels under.</summary>
    public const string Scheme = "Bearer";

    /// <summary>Length of the hash prefix used as the index node's id.</summary>
    public const int HashPrefixLength = 12;

    /// <summary>Mints a fresh instance key. The raw value is returned ONCE — only
    /// <see cref="Hash"/> of it is ever stored.</summary>
    public static string Generate() =>
        KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyByteLength))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    /// <summary>SHA-256 hex hash of a raw key — what gets persisted and compared.</summary>
    public static string Hash(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    /// <summary>The index node id for a key hash (see <see cref="MeshWeaverInstanceIndex"/>).</summary>
    public static string HashPrefix(string hash) => hash[..HashPrefixLength];

    /// <summary>Formats the <c>Authorization</c> header value an instance sends.</summary>
    public static string AuthorizationHeader(string rawKey) => $"{Scheme} {rawKey}";

    /// <summary>
    /// Extracts the raw key from an <c>Authorization</c> header, or null when the header is
    /// missing, not <c>Bearer</c>, or does not carry an instance key.
    ///
    /// <para>Shape is checked here; whether the key is <i>valid</i> is decided by hashing it and
    /// resolving the index — never by string comparison against a configured list.</para>
    /// </summary>
    public static string? ExtractKey(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;
        var trimmed = authorizationHeader.Trim();
        if (!trimmed.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
            return null;
        var key = trimmed[(Scheme.Length + 1)..].Trim();
        return key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key : null;
    }

    /// <summary>
    /// Fixed-time comparison of a presented key's hash against the stored hash. Both are hex
    /// SHA-256 strings of equal length; comparing them in fixed time keeps a mismatch from
    /// revealing how much of the key matched.
    /// </summary>
    public static bool HashEquals(string presentedHash, string storedHash) =>
        presentedHash.Length == storedHash.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presentedHash), Encoding.UTF8.GetBytes(storedHash));
}
