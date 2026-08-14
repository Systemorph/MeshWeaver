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
    /// missing or does not carry an instance key.
    ///
    /// <para>Accepts <c>Bearer mwi_…</c> — what MeshWeaver's own clients send — and
    /// <c>Basic base64(user:mwi_…)</c>, because a <b>NuGet client cannot send Bearer</b>: its
    /// <c>packageSourceCredentials</c> speak Basic, and the only alternative is shipping a
    /// credential-provider plugin. Accepting both keeps ONE credential and ONE validator; the
    /// username half is ignored, since the key is the whole secret.</para>
    ///
    /// <para>🚨 The key stays in the header either way. Putting it in a URL — a query string or a
    /// path segment — would leak it into every access log, proxy log and browser history along the
    /// route, which is what makes the Basic form worth supporting rather than the easier
    /// <c>?apiKey=</c>.</para>
    ///
    /// <para>Shape is checked here; whether the key is <i>valid</i> is decided by hashing it and
    /// resolving the index — never by string comparison against a configured list.</para>
    /// </summary>
    public static string? ExtractKey(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;
        var trimmed = authorizationHeader.Trim();

        if (trimmed.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            var key = trimmed[(Scheme.Length + 1)..].Trim();
            return key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key : null;
        }

        if (trimmed.StartsWith(BasicScheme + " ", StringComparison.OrdinalIgnoreCase))
            return ExtractFromBasic(trimmed[(BasicScheme.Length + 1)..].Trim());

        return null;
    }

    /// <summary>The HTTP auth scheme a NuGet client travels under.</summary>
    public const string BasicScheme = "Basic";

    /// <summary>
    /// The password half of a Basic credential, when it is an instance key. Malformed base64 and a
    /// missing colon are treated as "no key" rather than throwing: an unparsable header is an
    /// unauthenticated caller (401), never a 500.
    /// </summary>
    private static string? ExtractFromBasic(string encoded)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
            return null;

        var password = decoded[(separator + 1)..].Trim();
        return password.StartsWith(KeyPrefix, StringComparison.Ordinal) ? password : null;
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
