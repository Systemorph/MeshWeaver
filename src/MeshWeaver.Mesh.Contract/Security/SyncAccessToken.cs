using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A short-lived, scope-bearing access token an instance exchanges its durable
/// <c>mwi_</c> instance key for — the OAuth-shaped leg of the registry surface, and since #2804
/// the credential EVERY registry call is meant to carry.
///
/// <para><b>Why not just send the instance key.</b> An <c>mwi_</c> key is durable: it has no expiry
/// and re-issuing it is a manual admin action. A consumer that needs registry access therefore holds
/// a long-lived secret and presents it on every call — which is exactly the property that made a
/// per-repo GitHub PAT unacceptable. This token is minted for minutes, carries only the scope its
/// holder actually needs, and is worthless once expired.</para>
///
/// <para>🚨 <b>The token carries identity and scope — never authority.</b> It says "this is instance
/// X, asking only about these packages, until T". Whether X may actually pull them is re-decided on
/// every request against the live <see cref="PluginGrant"/> AND the live plan on the instance
/// record, so revoking a licence or promoting a plan takes effect at once rather than after the
/// token expires. A token can therefore only ever NARROW what the grant already allows — never
/// widen it, and never outlive a revocation. The plan is deliberately NOT a claim: a claim is a
/// snapshot, and a snapshot is how a promotion ends up waiting for an expiry.</para>
///
/// <para><b>Stateless by design.</b> The token is signed, not stored: minting writes no node, so
/// there is no per-issue write amplification (the defect class behind the personal-token mint storms)
/// and no expiry sweep to maintain. That is only safe BECAUSE authority is re-checked on use.</para>
///
/// <para><b>Wire format: a JWT</b> (RFC 7519) — <c>header.payload.signature</c>, each segment
/// URL-safe base64 without padding; the header is <c>{"alg":"HS256","typ":"JWT","kid":…}</c>, the
/// signature is HMAC-SHA256 over <c>header.payload</c>. Standard claims: <c>iss</c> (the registry),
/// <c>sub</c> (the instance id), <c>aud</c> (<see cref="Audience"/>), <c>iat</c>, <c>exp</c>,
/// <c>jti</c>; private claims <c>kh</c> (the hash of the instance key it was exchanged from — how
/// it resolves, and what makes re-issuing an instance key invalidate every outstanding token) and
/// <c>scope</c>. A standard shape so the verifier can take a SECOND issuer on the same path
/// (GitHub's OIDC for the build principal, #2483) and so any JWT tooling can read one. It is
/// distinguished from <c>mwi_</c>, <c>mwr_</c> and <c>mw_</c> credentials by SHAPE — three
/// segments with a JWT header — so no validator can accept another's credential by accident.</para>
/// </summary>
public static class SyncAccessToken
{
    /// <summary>The HTTP auth scheme the token travels under.</summary>
    public const string Scheme = "Bearer";

    /// <summary>The <c>aud</c> claim every registry token carries — a token minted for another
    /// audience on the same signing key (there is none today) would not verify here.</summary>
    public const string Audience = "meshweaver-registry";

    /// <summary>The <c>alg</c> a token must declare. Nothing else is accepted — in particular not
    /// <c>none</c>, the classic JWT forgery.</summary>
    public const string Algorithm = "HS256";

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
    /// <param name="instanceId">The registered instance the token speaks for (<c>sub</c>).</param>
    /// <param name="keyHash">SHA-256 hash of the instance key this token was exchanged FROM. It is
    /// how the token resolves to its instance — the same routing the raw key uses — and it is what
    /// makes re-issuing an instance key invalidate every outstanding token for free, since the
    /// instance record then carries a different hash.</param>
    /// <param name="scope">The <c>Source/Package</c> entries the holder may ask about. Empty means
    /// "everything the grant allows" — the token narrows nothing, it only shortens the lifetime.</param>
    /// <param name="issuedAt">Issue instant; the expiry is measured from here.</param>
    /// <param name="lifetime">Requested lifetime, clamped to <see cref="MaximumLifetime"/>.</param>
    /// <param name="signingKey">HMAC key, at least <see cref="MinimumSigningKeyBytes"/> bytes.</param>
    /// <param name="issuer">The <c>iss</c> claim — the registry's base URL. Optional: a registry that
    /// does not know its own public URL still mints a verifiable token.</param>
    /// <returns>The token string.</returns>
    /// <exception cref="ArgumentException">The instance id is blank or the signing key is too short.</exception>
    public static string Mint(
        string instanceId,
        string keyHash,
        IReadOnlyCollection<string> scope,
        DateTimeOffset issuedAt,
        TimeSpan lifetime,
        byte[] signingKey,
        string? issuer = null)
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

        var header = new Header { KeyId = KeyId(signingKey) };
        var payload = new Payload
        {
            Issuer = string.IsNullOrWhiteSpace(issuer) ? null : issuer.Trim(),
            Subject = instanceId,
            Audience = Audience,
            IssuedAt = issuedAt.ToUnixTimeSeconds(),
            ExpiresAt = issuedAt.Add(effective).ToUnixTimeSeconds(),
            // Distinguishes two tokens minted in the same second for the same scope. Not a security
            // property on its own — the signature is — but it keeps tokens from being identical
            // strings, which makes them individually recognisable in a log.
            TokenId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            KeyHash = keyHash,
            Scope = scope is { Count: > 0 } ? [.. scope] : null,
        };

        var signingInput = Encode(JsonSerializer.SerializeToUtf8Bytes(header, Json))
                           + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        return $"{signingInput}.{Sign(signingInput, signingKey)}";
    }

    /// <summary>
    /// Verifies a token's signature and expiry and returns what it claims. Returns <c>null</c> for
    /// anything that is not a valid, unexpired token — a malformed string, a bad signature, an
    /// expired instant. There is deliberately no distinction in the result between those cases:
    /// telling a caller WHICH way their forgery failed helps only the forger.
    /// </summary>
    /// <param name="token">The raw token.</param>
    /// <param name="now">The instant to judge expiry against.</param>
    /// <param name="signingKey">The HMAC key the token must verify under.</param>
    /// <returns>The verified claims, or null.</returns>
    public static SyncAccessTokenClaims? Verify(string? token, DateTimeOffset now, byte[] signingKey)
    {
        if (string.IsNullOrWhiteSpace(token) || signingKey is not { Length: >= MinimumSigningKeyBytes })
            return null;

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return null;

        // The header is checked BEFORE the signature: `alg` must be the one algorithm this
        // registry mints with. Honouring the token's own `alg` is the classic JWT hole — `none`, or
        // an RSA public key replayed as an HMAC secret.
        var header = Read<Header>(parts[0]);
        if (header is null
            || !string.Equals(header.Algorithm, Algorithm, StringComparison.Ordinal)
            || (header.Type is not null && !string.Equals(header.Type, "JWT", StringComparison.OrdinalIgnoreCase)))
            return null;

        // Fixed-time comparison: a byte-by-byte early exit leaks how much of a forged signature was
        // correct, which is enough to construct one a byte at a time.
        var expected = Sign(parts[0] + "." + parts[1], signingKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
            return null;

        var payload = Read<Payload>(parts[1]);
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.Subject)
            || string.IsNullOrWhiteSpace(payload.KeyHash))
            return null;
        if (payload.Audience is not null && !string.Equals(payload.Audience, Audience, StringComparison.Ordinal))
            return null;
        if (now.ToUnixTimeSeconds() >= payload.ExpiresAt)
            return null;

        return new SyncAccessTokenClaims(
            payload.Subject,
            payload.KeyHash,
            payload.Scope ?? [],
            DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt))
        {
            Issuer = payload.Issuer,
        };
    }

    /// <summary>
    /// Extracts a token from an <c>Authorization</c> header, or null when the header carries
    /// something else (a raw instance key, a personal token, nothing). Mirrors
    /// <see cref="InstanceKeys.ExtractKey"/>, including the <c>Basic</c> form, so a client that can
    /// only speak Basic can present either credential. Recognition is by SHAPE — three non-empty
    /// segments whose first decodes to a JSON object naming an <c>alg</c> — never by a prefix the
    /// other credentials could collide with.
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
            return LooksLikeToken(candidate) ? candidate : null;
        }

        if (!trimmed.StartsWith(InstanceKeys.BasicScheme + " ", StringComparison.OrdinalIgnoreCase))
            return null;
        return ExtractFromBasic(trimmed[(InstanceKeys.BasicScheme.Length + 1)..].Trim());
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> has the shape of one of these tokens — cheap enough to
    /// run on every unauthenticated request, and strict enough that no <c>mwi_</c>/<c>mwr_</c>/
    /// <c>mw_</c> credential (no dots) and no random string passes.
    /// </summary>
    /// <param name="candidate">The bearer value.</param>
    /// <returns>True when it is worth handing to <see cref="Verify"/>.</returns>
    public static bool LooksLikeToken(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxTokenChars)
            return false;
        var parts = candidate.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return false;
        return Read<Header>(parts[0]) is { Algorithm.Length: > 0 };
    }

    /// <summary>Largest token considered, in characters. A real token is a few hundred; anything
    /// near the bound is already not one, and the bound keeps a hostile header from costing a
    /// large decode per request.</summary>
    private const int MaxTokenChars = 4096;

    /// <summary>Largest Basic payload considered; see <see cref="ExtractFromBasic"/>.</summary>
    private const int MaxBasicPayloadChars = 4096;

    /// <summary>Decode buffer for <see cref="MaxBasicPayloadChars"/> (base64 is 4 chars per 3 bytes).</summary>
    private const int MaxBasicPayloadBytes = MaxBasicPayloadChars / 4 * 3;

    /// <summary>
    /// The password half of a Basic credential, when it is an access token.
    ///
    /// <para>🚨 No exception on the reject path, and a bounded decode buffer — the same discipline as
    /// <see cref="InstanceKeys.ExtractKey"/>, and for the same reason: this runs on an
    /// UNAUTHENTICATED request with attacker-controlled input, so a throwing parse would let anyone
    /// make the registry raise and unwind an exception per request.</para>
    /// </summary>
    private static string? ExtractFromBasic(string encoded)
    {
        if (encoded.Length is 0 or > MaxBasicPayloadChars)
            return null;

        Span<byte> buffer = stackalloc byte[MaxBasicPayloadBytes];
        if (!Convert.TryFromBase64String(encoded, buffer, out var written))
            return null;

        // UTF8.GetString uses replacement fallback, so invalid bytes become U+FFFD rather than
        // throwing — and a token containing U+FFFD simply fails the shape check below.
        var decoded = Encoding.UTF8.GetString(buffer[..written]);
        var separator = decoded.IndexOf(':');
        if (separator < 0)
            return null;

        var candidate = decoded[(separator + 1)..].Trim();
        return LooksLikeToken(candidate) ? candidate : null;
    }

    /// <summary>Whether a signing key is long enough to be used.</summary>
    /// <param name="signingKey">Candidate key.</param>
    /// <returns>True when usable.</returns>
    public static bool IsUsableSigningKey(byte[]? signingKey) =>
        signingKey is { Length: >= MinimumSigningKeyBytes };

    /// <summary>A short, non-secret identifier of a signing key — the <c>kid</c> header — so a log
    /// line or a verifier can tell which key a token was minted under without exposing it.</summary>
    /// <param name="signingKey">The key.</param>
    /// <returns>Eight hex characters of the key's SHA-256.</returns>
    public static string KeyId(byte[] signingKey) =>
        Convert.ToHexString(SHA256.HashData(signingKey))[..8].ToLowerInvariant();

    private static void RequireUsableKey(byte[] signingKey)
    {
        if (!IsUsableSigningKey(signingKey))
            throw new ArgumentException(
                $"The token signing key must be at least {MinimumSigningKeyBytes} bytes.",
                nameof(signingKey));
    }

    private static string Sign(string signingInput, byte[] signingKey) =>
        Encode(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(signingInput)));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static byte[]? Decode(string segment)
    {
        var padded = segment.Replace("-", "+").Replace("_", "/");
        var buffer = new byte[(padded.Length + 3) / 4 * 3];
        return Convert.TryFromBase64String(
            padded.PadRight((padded.Length + 3) / 4 * 4, '='), buffer, out var written)
            ? buffer[..written]
            : null;
    }

    /// <summary>A segment as JSON, or null — never a throw on the unauthenticated path.</summary>
    private static T? Read<T>(string segment) where T : class
    {
        var bytes = Decode(segment);
        if (bytes is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Header
    {
        [JsonPropertyName("alg")] public string Algorithm { get; init; } = SyncAccessToken.Algorithm;
        [JsonPropertyName("typ")] public string? Type { get; init; } = "JWT";
        [JsonPropertyName("kid")] public string? KeyId { get; init; }
    }

    private sealed record Payload
    {
        [JsonPropertyName("iss")] public string? Issuer { get; init; }
        [JsonPropertyName("sub")] public string Subject { get; init; } = "";
        [JsonPropertyName("aud")] public string? Audience { get; init; }
        [JsonPropertyName("iat")] public long IssuedAt { get; init; }
        [JsonPropertyName("exp")] public long ExpiresAt { get; init; }
        [JsonPropertyName("jti")] public string TokenId { get; init; } = "";
        [JsonPropertyName("kh")] public string KeyHash { get; init; } = "";
        [JsonPropertyName("scope")] public string[]? Scope { get; init; }
    }
}

/// <summary>What a verified <see cref="SyncAccessToken"/> claims.</summary>
/// <param name="InstanceId">The instance the token speaks for (<c>sub</c>).</param>
/// <param name="KeyHash">Hash of the instance key it was exchanged from — how it resolves.</param>
/// <param name="Scope">The <c>Source/Package</c> entries it is limited to. Empty = not narrowed.</param>
/// <param name="ExpiresAt">When it stops verifying.</param>
public sealed record SyncAccessTokenClaims(
    string InstanceId, string KeyHash, IReadOnlyCollection<string> Scope, DateTimeOffset ExpiresAt)
{
    /// <summary>The <c>iss</c> claim — which registry minted it, when it said.</summary>
    public string? Issuer { get; init; }

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
