using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The instance-token contract of the plugin-registry surface — one place for BOTH sides, so the
/// producer (the registry's <c>/api/plugins</c> endpoints) and the consumer
/// (<see cref="RegistryPackageSource"/>) cannot drift, mirroring <see cref="PluginRegistryPayloads"/>.
///
/// <para>The registry is NOT public: it serves only <b>registered MeshWeaver instances</b>. Registering
/// an instance means issuing it a token and adding that token to the registry's
/// <c>PluginCatalog:RegistryTokens</c> list (provisioned as config/secret on the registry — the
/// managed-instance control plane writes it there when it provisions an instance). The consumer sends
/// its token as <c>Authorization: Bearer &lt;token&gt;</c>
/// (<see cref="PluginRegistryReference.Token"/>). With no tokens configured the registry is open —
/// the local-dev / e2e-stub mode; a production registry always configures tokens.</para>
/// </summary>
public static class PluginRegistryTokens
{
    /// <summary>Config key holding the registry's issued instance tokens
    /// (<c>PluginCatalog:RegistryTokens:0</c>, <c>:1</c>, …).</summary>
    public const string SectionName = "PluginCatalog:RegistryTokens";

    /// <summary>The HTTP auth scheme the token travels under.</summary>
    public const string Scheme = "Bearer";

    /// <summary>Formats the <c>Authorization</c> header value the consumer sends.</summary>
    public static string AuthorizationHeader(string token) => $"{Scheme} {token}";

    /// <summary>
    /// Validates a request's <c>Authorization</c> header against the registry's issued tokens.
    /// The header must be <c>Bearer &lt;token&gt;</c> (scheme case-insensitive) and the token must
    /// match one of <paramref name="issuedTokens"/>. Every comparison is fixed-time
    /// (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>)
    /// so a mismatch reveals nothing about how much of a token matched.
    /// </summary>
    public static bool Validate(string? authorizationHeader, IReadOnlyCollection<string> issuedTokens)
    {
        if (issuedTokens.Count == 0 || string.IsNullOrWhiteSpace(authorizationHeader))
            return false;
        var trimmed = authorizationHeader.Trim();
        if (!trimmed.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = Encoding.UTF8.GetBytes(trimmed[(Scheme.Length + 1)..].Trim());
        // Check EVERY issued token (no early exit) so timing does not leak which token was closest.
        var valid = false;
        foreach (var issued in issuedTokens)
            valid |= CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(issued));
        return valid;
    }
}
