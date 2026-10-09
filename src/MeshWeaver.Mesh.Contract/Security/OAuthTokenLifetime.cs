namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The idle rule for tokens the MCP OAuth exchange mints (policy <c>oauth-idle-expiry</c>): such a
/// token EXPIRES once it has gone unused for <see cref="IdleLifetime"/>, measured from its last use
/// (<see cref="ApiToken.LastUsedAt"/>) or, if it was never used, from its creation.
///
/// <para><b>One rule for every validator.</b> It lives here, beside <see cref="ApiTokenVerdict"/>,
/// so the HTTP authentication handler, the auth middleware's direct-store verdict and the
/// <c>ApiToken/{hashPrefix}</c> hub all refuse the same token: a rule applied on one path only
/// would let an expired token in through another.</para>
///
/// <para><b>Why idle, and why only OAuth (Plugins#2772).</b> Supersession keys on <c>client_id</c>,
/// which a loopback client derives from a redirect URI whose port varies, so each re-registration
/// opened a slot nothing could reach and left the previous one-year token live. Expiring an OAuth
/// token after a stretch of disuse ends that accumulation without touching the supersede key, and
/// it only ever removes credentials. A token a person minted by hand is never subject to it.</para>
///
/// <para>🚨 <b>The label is provenance only because the prefix is RESERVED.</b> Only the OAuth
/// exchange may mint a token whose label starts with <see cref="LabelPrefix"/>; every manual mint
/// surface refuses it (<see cref="IsReservedLabel"/>), so a person cannot opt a hand-minted token
/// into this rule by naming it.</para>
/// </summary>
public static class OAuthTokenLifetime
{
    /// <summary>The label prefix of every OAuth-minted token: <c>OAuth: {client_id}</c>.</summary>
    public const string LabelPrefix = "OAuth: ";

    /// <summary>How long an OAuth-minted token may go unused before it expires.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);

    /// <summary>The label the OAuth exchange gives the token it mints for <paramref name="clientId"/>.</summary>
    /// <param name="clientId">The OAuth client id.</param>
    /// <returns><c>OAuth: {clientId}</c>.</returns>
    public static string LabelFor(string clientId) => LabelPrefix + clientId;

    /// <summary>Whether <paramref name="label"/> is in the namespace reserved for OAuth-minted
    /// tokens — refused on every manual mint surface. Leading whitespace does not escape it.</summary>
    /// <param name="label">A token label as a caller supplied it.</param>
    /// <returns><c>true</c> when the label starts with the reserved prefix.</returns>
    public static bool IsReservedLabel(string? label) =>
        label is not null && label.TrimStart().StartsWith(LabelPrefix.TrimEnd(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="token"/> was minted by the OAuth exchange and has not been used for
    /// longer than <see cref="IdleLifetime"/> at <paramref name="now"/>. Pure.
    /// </summary>
    /// <param name="token">The stored token record.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <returns><c>true</c> when the token has expired through disuse.</returns>
    public static bool IsIdle(ApiToken token, DateTimeOffset now) =>
        token.Label.StartsWith(LabelPrefix, StringComparison.Ordinal)
        && now - (token.LastUsedAt ?? token.CreatedAt) > IdleLifetime;
}
