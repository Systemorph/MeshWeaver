using System.Text;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Reads the SECRET out of an <c>Authorization</c> header, in the two shapes an OCI client
/// presents it.
///
/// <para>A registry client speaks BOTH: it sends <c>Basic base64(user:secret)</c> to the token
/// endpoint named by the <c>WWW-Authenticate</c> challenge (that is what <c>docker login</c>
/// stores and replays), and <c>Bearer &lt;token&gt;</c> to every route afterwards. This is the one
/// place the mirror reduces the two to the single value it authenticates.</para>
///
/// <para>🚨 The username half is DELIBERATELY discarded. The credential the fleet already carries
/// is an instance key — a bearer token — and <c>docker login -u &lt;anything&gt;</c> is how a
/// client is made to send it. Authenticating on the username too would mean inventing a second
/// identity concept that nothing issues.</para>
/// </summary>
public static class RegistryCredential
{
    /// <summary>The scheme a client uses against the token endpoint.</summary>
    public const string BasicScheme = "Basic";

    /// <summary>The scheme a client uses against every route after the token exchange.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// The secret carried by <paramref name="authorizationHeader"/>, or null when the header is
    /// absent, malformed, or names a scheme the mirror does not speak. Null is always "not
    /// authenticated" — never "allow".
    /// </summary>
    public static string? TryReadSecret(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;

        var header = authorizationHeader.Trim();
        var space = header.IndexOf(' ');
        if (space <= 0)
            return null;
        var scheme = header[..space];
        var value = header[(space + 1)..].Trim();
        if (value.Length == 0)
            return null;

        if (scheme.Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
            return value;

        if (!scheme.Equals(BasicScheme, StringComparison.OrdinalIgnoreCase))
            return null;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }

        var pair = Encoding.UTF8.GetString(decoded);
        var colon = pair.IndexOf(':');
        if (colon < 0)
            return null;
        // 🚨 Split on the FIRST colon only: a token may itself contain colons (an ACR refresh
        // token does), and splitting on the last — or on all — would hand back a truncated secret
        // that fails to authenticate for a reason no log would explain.
        var secret = pair[(colon + 1)..];
        return secret.Length == 0 ? null : secret;
    }

    /// <summary>
    /// The <c>Authorization</c> header value that presents <paramref name="secret"/> as a bearer —
    /// the shape the mirror's own routes are authenticated with, and what the token endpoint's
    /// answer makes a client send next.
    /// </summary>
    public static string AsBearerHeader(string secret) => $"{BearerScheme} {secret}";
}
