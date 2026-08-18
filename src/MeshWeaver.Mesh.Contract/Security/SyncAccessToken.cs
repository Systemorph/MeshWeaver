using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A short-lived, scope-bearing access token an instance exchanges its durable
/// <c>mwi_</c> instance key for — the OAuth-shaped leg of the registry surface.
///
/// <para><b>Why not just send the instance key.</b> An <c>mwi_</c> key is durable: it has no expiry
/// and re-issuing it is a manual admin action. A consumer that needs registry access therefore holds
/// a long-lived secret and presents it on every call — which is exactly the property that made a
/// per-repo GitHub PAT unacceptable. This token is minted for minutes, carries only the scope its
/// holder actually needs, and is worthless once expired.</para>
///
/// <para>🚨 <b>The token carries identity and scope — never authority.</b> It says "this is instance
/// X, asking only about these packages, until T". Whether X may actually pull them is re-decided on
/// every request against the live <see cref="PluginGrant"/>, so revoking a sync licence takes effect
/// immediately rather than after the token expires. A token can therefore only ever NARROW what the
/// grant already allows — never widen it, and never outlive a revocation.</para>
///
/// <para><b>Stateless by design.</b> The token is signed, not stored: minting writes no node, so
/// there is no per-issue write amplification (the defect class behind the personal-token mint storms)
/// and no expiry sweep to maintain. That is only safe BECAUSE authority is re-checked on use.</para>
///
/// <para>Wire format is <c>mwa_&lt;payload&gt;.&lt;signature&gt;</c>, both URL-safe base64: the
/// payload is the JSON below, the signature is HMAC-SHA256 over the payload segment. The prefix is
/// disjoint from <c>mwi_</c> (instance), <c>mwr_</c> (registration) and the personal <c>mw_</c> for
/// the same reason those are disjoint from each other — so no validator can ever accept another's
/// credential by accident.</para>
/// </summary>
public static class SyncAccessToken
{
    /// <summary>Prefix on every minted access token.</summary>
    public const string KeyPrefix = "mwa_";

    /// <summary>The HTTP auth scheme the token travels under.</summary>
    public const string Scheme = "Bearer";

    /// <summary>Default lifetime when a caller does not ask for one.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Longest lifetime that will be issued. A caller may request less; asking for more is clamped
    /// rather than refused, so a consumer written against a future, more generous registry still
    /// works instead of failing at the exchange.
    /// </summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);

    /// <summary>Minimum length of an acceptable signing key, in bytes. A short key makes the
    /// signature forgeable, so it is refused at mint time rather than quietly accepted.</summary>
    public const int MinimumSigningKeyBytes = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Mints a token for <paramref name="instanceId"/> limited to <paramref name="scope"/>.
    /// </summary>
    /// <param name="instanceId">The registered instance the token speaks for.</param>
    /// <param name="keyHash">SHA-256 hash of the instance key this token was exchanged FROM. It is
    /// how the token resolves to its instance — the same routing the raw key uses — and it is what
    /// makes re-issuing an instance key invalidate every outstanding token for free, since the
    /// instance record then carries a different hash.</param>
    /// <param name="scope">The <c>Source/Package</c> entries the holder may ask about. Empty means
    /// "everything the grant allows" — the token narrows nothing, it only shortens the lifetime.</param>
    /// <param name="issuedAt">Issue instant; the expiry is measured from here.</param>
    /// <param name="lifetime">Requested lifetime, clamped to <see cref="MaximumLifetime"/>.</param>
    /// <param name="signingKey">HMAC key, at least <see cref="MinimumSigningKeyBytes"/> bytes.</param>
    /// <returns>The token string, including its prefix.</returns>
    /// <exception cref="ArgumentException">The instance id is blank or the signing key is too short.</exception>
    public static string Mint(
        string instanceId,
        string keyHash,
        IReadOnlyCollection<string> scope,
        DateTimeOffset issuedAt,
        TimeSpan lifetime,
        byte[] signingKey)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("A token must name the instance it speaks for.", nameof(instanceId));
        if (string.IsNullOrWhiteSpace(keyHash))
            throw new ArgumentException("A token must carry the hash of the key it was exchanged from.",
                nameof(keyHash));
        RequireUsableKey(signingKey);

        var effective = lifetime <= TimeSpan.Zero ? DefaultLifetime
            : lifetime > MaximumLifetime ? MaximumLifetime
            : lifetime;

        var payload = new Payload
        {
            InstanceId = instanceId,
            KeyHash = keyHash,
            Scope = scope is { Count: > 0 } ? [.. scope] : null,
            ExpiresAt = issuedAt.Add(effective).ToUnixTimeSeconds(),
            // Distinguishes two tokens minted in the same second for the same scope. Not a security
            // property on its own — the signature is — but it keeps tokens from being identical
            // strings, which makes them individually recognisable in a log.
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
        };

        var segment = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        return $"{KeyPrefix}{segment}.{Sign(segment, signingKey)}";
    }

    /// <summary>
    /// Verifies a token's signature and expiry and returns what it claims. Returns <c>null</c> for
    /// anything that is not a valid, unexpired token — a malformed string, a bad signature, an
    /// expired instant. There is deliberately no distinction in the result between those cases:
    /// telling a caller WHICH way their forgery failed helps only the forger.
    /// </summary>
    /// <param name="token">The raw token, with or without its prefix already stripped.</param>
    /// <param name="now">The instant to judge expiry against.</param>
    /// <param name="signingKey">The HMAC key the token must verify under.</param>
    /// <returns>The verified claims, or null.</returns>
    public static SyncAccessTokenClaims? Verify(string? token, DateTimeOffset now, byte[] signingKey)
    {
        if (string.IsNullOrWhiteSpace(token) || signingKey is not { Length: >= MinimumSigningKeyBytes })
            return null;

        var raw = token.StartsWith(KeyPrefix, StringComparison.Ordinal)
            ? token[KeyPrefix.Length..]
            : token;

        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1)
            return null;

        var segment = raw[..dot];
        var signature = raw[(dot + 1)..];

        // Fixed-time comparison: a byte-by-byte early exit leaks how much of a forged signature was
        // correct, which is enough to construct one a byte at a time.
        var expected = Sign(segment, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature)))
            return null;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Decode(segment), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.InstanceId)
            || string.IsNullOrWhiteSpace(payload.KeyHash))
            return null;
        if (now.ToUnixTimeSeconds() >= payload.ExpiresAt)
            return null;

        return new SyncAccessTokenClaims(
            payload.InstanceId,
            payload.KeyHash,
            payload.Scope ?? [],
            DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt));
    }

    /// <summary>
    /// Extracts a token from an <c>Authorization</c> header, or null when the header carries
    /// something else. Mirrors <see cref="InstanceKeys.ExtractKey"/>, including the <c>Basic</c>
    /// form, so a client that can only speak Basic can present either credential.
    /// </summary>
    /// <param name="authorizationHeader">The raw header value.</param>
    /// <returns>The token, or null.</returns>
    public static string? ExtractToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;
        var trimmed = authorizationHeader.Trim();

        if (trimmed.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = trimmed[(Scheme.Length + 1)..].Trim();
            return candidate.StartsWith(KeyPrefix, StringComparison.Ordinal) ? candidate : null;
        }

        const string basic = "Basic";
        if (!trimmed.StartsWith(basic + " ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed[(basic.Length + 1)..].Trim()));
            var colon = decoded.IndexOf(':');
            var candidate = (colon < 0 ? decoded : decoded[(colon + 1)..]).Trim();
            return candidate.StartsWith(KeyPrefix, StringComparison.Ordinal) ? candidate : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a signing key is long enough to be used.</summary>
    /// <param name="signingKey">Candidate key.</param>
    /// <returns>True when usable.</returns>
    public static bool IsUsableSigningKey(byte[]? signingKey) =>
        signingKey is { Length: >= MinimumSigningKeyBytes };

    private static void RequireUsableKey(byte[] signingKey)
    {
        if (!IsUsableSigningKey(signingKey))
            throw new ArgumentException(
                $"The token signing key must be at least {MinimumSigningKeyBytes} bytes.",
                nameof(signingKey));
    }

    private static string Sign(string segment, byte[] signingKey) =>
        Encode(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(segment)));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static byte[] Decode(string segment)
    {
        var padded = segment.Replace("-", "+").Replace("_", "/");
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    private sealed record Payload
    {
        [JsonPropertyName("i")] public string InstanceId { get; init; } = "";
        [JsonPropertyName("h")] public string KeyHash { get; init; } = "";
        [JsonPropertyName("s")] public string[]? Scope { get; init; }
        [JsonPropertyName("e")] public long ExpiresAt { get; init; }
        [JsonPropertyName("n")] public string Nonce { get; init; } = "";
    }
}

/// <summary>What a verified <see cref="SyncAccessToken"/> claims.</summary>
/// <param name="InstanceId">The instance the token speaks for.</param>
/// <param name="KeyHash">Hash of the instance key it was exchanged from — how it resolves.</param>
/// <param name="Scope">The <c>Source/Package</c> entries it is limited to. Empty = not narrowed.</param>
/// <param name="ExpiresAt">When it stops verifying.</param>
public sealed record SyncAccessTokenClaims(
    string InstanceId, string KeyHash, IReadOnlyCollection<string> Scope, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Whether this token permits ASKING about <paramref name="packageId"/> from
    /// <paramref name="sourceName"/>. An unnarrowed token permits everything — the grant still
    /// decides. Matching reuses <see cref="PluginGrantEntry"/>, so a scope string means exactly what
    /// the same string means in a grant, wildcards included.
    /// </summary>
    /// <param name="sourceName">Registry source name.</param>
    /// <param name="packageId">Package id.</param>
    /// <returns>True when in scope.</returns>
    public bool Covers(string sourceName, string packageId) =>
        Scope.Count == 0
        || Scope.Select(PluginGrantEntry.TryParse)
               .Any(e => e is not null && e.Matches(sourceName, packageId));
}
