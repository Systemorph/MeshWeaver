using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The SECOND issuer on the registry's token path (#2483): GitHub Actions' OIDC token, verified by
/// the mesh itself against GitHub's published JWKS.
///
/// <para><b>Why a second issuer instead of a second credential.</b> Every GitHub Actions run can
/// already ask for a short-lived, signed JWT describing itself — <c>repository</c>, <c>ref</c>,
/// <c>event_name</c>, <c>job_workflow_ref</c> — with no secret stored anywhere. Azure's federated
/// credential is nothing more than Azure verifying that token against a rule; the mesh can verify it
/// too, and then the rule lives on a node the mesh owns (<see cref="BuildPrincipal"/>) rather than in
/// a cloud tenant no query can join. That is the whole of <c>Doc/Architecture/PluginBuildContract</c>
/// → "The build principal".</para>
///
/// <para>🚨 <b>One verifier, two issuers, a trust node per issuer.</b> The fork is on the token's
/// <c>iss</c> claim, read UNVERIFIED and used only to pick a verifier — never to grant anything:
/// <see cref="SyncAccessToken"/>'s HMAC leg for the registry's own tokens, this leg for GitHub's. The
/// HS256 leg still never honours a token's own <c>alg</c>, and neither does this one:
/// <see cref="Algorithm"/> is the only algorithm accepted here, so an <c>alg: none</c> or an
/// HMAC-shaped forgery replayed against an RSA public key is refused before any key is touched.</para>
///
/// <para>🚨 <b>A verified signature is not an authorization.</b> Every GitHub Actions run in the world
/// gets a token signed by these same keys. What this class establishes is only <i>which repository, on
/// which event, asked</i>; whether that repository may do anything here is decided against a
/// <see cref="BuildPrincipal"/> node. A verifier that checked the signature and stopped would
/// authenticate the entire public GitHub.</para>
/// </summary>
public static class GitHubActionsToken
{
    /// <summary>
    /// The one issuer accepted here — pinned, never configurable. A configurable issuer is a
    /// configurable trust anchor: an operator (or a misapplied overlay) could point the verifier at a
    /// key set an attacker controls, and every claim below would then be attacker-authored.
    /// </summary>
    public const string Issuer = "https://token.actions.githubusercontent.com";

    /// <summary>The <c>alg</c> a token must declare. Nothing else is accepted — in particular not
    /// <c>none</c>, and never the token's own choice.</summary>
    public const string Algorithm = "RS256";

    /// <summary>GitHub's OpenID discovery document, under the pinned <see cref="Issuer"/>.</summary>
    public const string OpenIdConfigurationUri = Issuer + "/.well-known/openid-configuration";

    /// <summary>GitHub's documented JWKS endpoint — the fallback when discovery yields nothing
    /// usable, and the shape a discovered <c>jwks_uri</c> is checked against.</summary>
    public const string JwksUri = Issuer + "/.well-known/jwks";

    /// <summary>
    /// Tolerance applied to <c>exp</c> and <c>nbf</c>. Two minutes, deliberately tighter than the
    /// five minutes token libraries default to: these tokens live for minutes and a build that
    /// retries costs nothing, whereas every second of slack is a second an intercepted token still
    /// works.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Largest token considered, in characters. A GitHub OIDC token is ~1–2 KB; the bound
    /// keeps a hostile header from costing a large decode on an UNAUTHENTICATED request.</summary>
    public const int MaxTokenChars = 8192;

    /// <summary>Smallest RSA modulus accepted from the key set, in bytes (RSA-2048). A short key is
    /// a forgeable signature, so it is dropped at parse time rather than trusted at verify time.</summary>
    public const int MinimumModulusBytes = 256;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The <c>iss</c> claim of <paramref name="token"/>, read WITHOUT verifying anything.
    ///
    /// <para>🚨 This is a ROUTING read and nothing else. It selects which verifier runs; it can only
    /// ever send a forged token to a verifier that will reject it, and it grants nothing on its own.
    /// Every claim that matters — including <c>iss</c> itself — is re-read from the verified payload
    /// in <see cref="Verify"/>.</para>
    /// </summary>
    /// <param name="token">The raw bearer value.</param>
    /// <returns>The unverified issuer, or null when the value is not a readable JWT.</returns>
    public static string? PeekIssuer(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenChars)
            return null;
        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return null;
        return Read<Payload>(parts[1])?.Issuer;
    }

    /// <summary>Whether <paramref name="token"/> claims to come from GitHub Actions — the fork that
    /// picks this verifier over the registry's HMAC one.</summary>
    /// <param name="token">The raw bearer value.</param>
    /// <returns>True when the unverified <c>iss</c> is exactly <see cref="Issuer"/>.</returns>
    public static bool IsGitHubIssued(string? token) =>
        string.Equals(PeekIssuer(token), Issuer, StringComparison.Ordinal);

    /// <summary>
    /// Verifies <paramref name="token"/>'s signature, issuer, audience and validity window against
    /// <paramref name="keys"/>, and returns what it claims.
    ///
    /// <para>The result has THREE shapes, not two, and the third is the point: a token whose
    /// <c>kid</c> is absent from the presented key set is <see cref="GitHubTokenVerification.Undetermined"/>
    /// — nothing was established, and the caller may refresh the key set once and ask again. Folding
    /// that into "rejected" is how a routine GitHub key rotation becomes a fleet-wide outage; folding
    /// it into "accepted" would be a hole. Everything else that fails is a flat rejection, with no
    /// hint about which check failed: telling a caller how their forgery failed helps only the
    /// forger.</para>
    /// </summary>
    /// <param name="token">The raw bearer value.</param>
    /// <param name="now">The instant to judge <c>exp</c>/<c>nbf</c> against.</param>
    /// <param name="audiences">The audiences this registry accepts. EMPTY REFUSES EVERYTHING — a
    /// registry that does not know which audience its builds request cannot tell a token minted for
    /// it from one minted for any other service on the internet.</param>
    /// <param name="keys">GitHub's current signing keys, by <c>kid</c>.</param>
    /// <returns>The verification outcome.</returns>
    public static GitHubTokenVerification Verify(
        string? token,
        DateTimeOffset now,
        IReadOnlyCollection<string> audiences,
        IReadOnlyDictionary<string, GitHubSigningKey> keys)
    {
        // Fail closed on an unconfigured audience. A token from ANY workflow on GitHub verifies
        // against these keys; the audience is what says "this one was minted for us".
        if (audiences is not { Count: > 0 } || keys is not { Count: > 0 })
            return GitHubTokenVerification.Rejected;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenChars)
            return GitHubTokenVerification.Rejected;

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return GitHubTokenVerification.Rejected;

        // The header is checked BEFORE any key is looked up: `alg` must be the one algorithm this
        // issuer signs with. Honouring the token's own `alg` is the classic JWT hole.
        var header = Read<Header>(parts[0]);
        if (header is null
            || !string.Equals(header.Algorithm, Algorithm, StringComparison.Ordinal)
            || (header.Type is not null && !string.Equals(header.Type, "JWT", StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(header.KeyId))
            return GitHubTokenVerification.Rejected;

        if (!keys.TryGetValue(header.KeyId, out var key) || key is null)
            return GitHubTokenVerification.Undetermined;

        var signature = Decode(parts[2]);
        if (signature is null)
            return GitHubTokenVerification.Rejected;

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
        }
        catch (CryptographicException)
        {
            // A key the JWKS offered but this platform cannot import is not a reason to admit the
            // caller. Same discipline as everywhere else on this path: no verification, no entry.
            return GitHubTokenVerification.Rejected;
        }

        // The JWS signing input is the ASCII of "header.payload" — the base64url segments verbatim,
        // never a re-serialization of the decoded JSON.
        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return GitHubTokenVerification.Rejected;

        var payload = Read<Payload>(parts[1]);
        if (payload is null)
            return GitHubTokenVerification.Rejected;

        // Issuer, re-read from the VERIFIED payload — the unverified peek above only chose a verifier.
        if (!string.Equals(payload.Issuer, Issuer, StringComparison.Ordinal))
            return GitHubTokenVerification.Rejected;

        var audience = MatchedAudience(payload.Audience, audiences);
        if (audience is null)
            return GitHubTokenVerification.Rejected;

        if (payload.ExpiresAt <= 0)
            return GitHubTokenVerification.Rejected;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        if (now - ClockSkew >= expiresAt)
            return GitHubTokenVerification.Rejected;
        if (payload.NotBefore > 0
            && now + ClockSkew < DateTimeOffset.FromUnixTimeSeconds(payload.NotBefore))
            return GitHubTokenVerification.Rejected;

        // A token with no repository claim describes no build. Nothing downstream could match it,
        // and an empty string must never fall through to an empty declared repository on a node.
        if (string.IsNullOrWhiteSpace(payload.Repository))
            return GitHubTokenVerification.Rejected;

        return GitHubTokenVerification.Verified(new GitHubBuildClaims
        {
            Repository = payload.Repository.Trim(),
            RepositoryId = Blank(payload.RepositoryId),
            RepositoryOwner = Blank(payload.RepositoryOwner),
            RepositoryOwnerId = Blank(payload.RepositoryOwnerId),
            EventName = (payload.EventName ?? "").Trim(),
            Ref = Blank(payload.Ref),
            Subject = Blank(payload.Subject),
            JobWorkflowRef = Blank(payload.JobWorkflowRef),
            Workflow = Blank(payload.Workflow),
            Actor = Blank(payload.Actor),
            RunId = Blank(payload.RunId),
            Audience = audience,
            ExpiresAt = expiresAt,
        });
    }

    /// <summary>
    /// Reads a JWKS document into the RSA signing keys it offers, skipping every entry this verifier
    /// would not use anyway — a non-RSA key type, an <c>alg</c> that is not <see cref="Algorithm"/>,
    /// a <c>use</c> that is not <c>sig</c>, a missing <c>kid</c>, an undersized modulus. Never
    /// throws: the document is fetched over the network, so a malformed one must be an empty key set
    /// (which refuses everything) rather than an exception on an unauthenticated request path.
    /// </summary>
    /// <param name="json">The raw JWKS document.</param>
    /// <returns>The usable keys, by <c>kid</c>. Empty when nothing usable was found.</returns>
    public static IReadOnlyDictionary<string, GitHubSigningKey> ParseJwks(string? json)
    {
        var result = new Dictionary<string, GitHubSigningKey>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in keys.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                if (Text(entry, "kty") is not "RSA")
                    continue;
                if (Text(entry, "alg") is { } alg && !string.Equals(alg, Algorithm, StringComparison.Ordinal))
                    continue;
                if (Text(entry, "use") is { } use && !string.Equals(use, "sig", StringComparison.Ordinal))
                    continue;
                if (Text(entry, "kid") is not { Length: > 0 } kid)
                    continue;
                if (Text(entry, "n") is not { Length: > 0 } n || Text(entry, "e") is not { Length: > 0 } e)
                    continue;
                if (Decode(n) is not { } modulus || Decode(e) is not { } exponent)
                    continue;
                if (modulus.Length < MinimumModulusBytes || exponent.Length is 0)
                    continue;
                // A duplicate kid is a document we cannot reason about — keep the first and ignore
                // the rest rather than letting a later entry silently replace a key already in use.
                result.TryAdd(kid, new GitHubSigningKey { KeyId = kid, Modulus = modulus, Exponent = exponent });
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>jwks_uri</c> a discovery document advertises, accepted only when it is HTTPS on the
    /// SAME host as <see cref="Issuer"/>. Anything else — a different host, plain HTTP, an
    /// unparseable document — yields null and the caller falls back to the pinned
    /// <see cref="JwksUri"/>. Discovery is a convenience so a moved endpoint does not become an
    /// outage; it is never allowed to move the trust anchor.
    /// </summary>
    /// <param name="json">The raw OpenID configuration document.</param>
    /// <returns>The accepted JWKS URI, or null.</returns>
    public static string? JwksUriFromDiscovery(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var advertised = Text(document.RootElement, "jwks_uri");
            if (advertised is not { Length: > 0 }
                || !Uri.TryCreate(advertised, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                return null;
            var issuer = new Uri(Issuer);
            return string.Equals(uri.Host, issuer.Host, StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A repository claim reduced to its <c>owner/name</c> form, so a principal keeps matching when
    /// GitHub moves an organisation onto the IMMUTABLE claim format.
    ///
    /// <para>Both shapes occur in this fleet's federated credentials today:
    /// <c>Systemorph/MeshWeaver.SocialMedia</c> and
    /// <c>Systemorph@12345/MeshWeaver.SocialMedia@67890</c>. Stripping is unambiguous because neither
    /// a GitHub login nor a repository name may contain <c>@</c>, and only an all-digit suffix is
    /// removed — so no real name can be truncated by this.</para>
    /// </summary>
    /// <param name="repository">A <c>repository</c> claim or a declared repository.</param>
    /// <returns>The normalized <c>owner/name</c>, or an empty string.</returns>
    public static string NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "";
        var trimmed = repository.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
            return StripImmutableId(trimmed);
        return StripImmutableId(trimmed[..slash]) + "/" + StripImmutableId(trimmed[(slash + 1)..]);
    }

    /// <summary>Whether two repository references name the same repository, compared on their
    /// normalized <c>owner/name</c> form. Exact — never a prefix, never a wildcard: a prefix match
    /// would let <c>Systemorph/MeshWeaver.Evil</c> authenticate as <c>Systemorph/MeshWeaver</c>.</summary>
    /// <param name="left">One reference.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they name the same repository.</returns>
    public static bool RepositoryEquals(string? left, string? right)
    {
        var a = NormalizeRepository(left);
        var b = NormalizeRepository(right);
        // GitHub owner/repo names are case-insensitive but case-preserving, so an ordinal compare
        // would refuse a principal an admin typed with different casing than the claim carries.
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripImmutableId(string segment)
    {
        var at = segment.IndexOf('@');
        if (at <= 0 || at == segment.Length - 1)
            return segment;
        var suffix = segment[(at + 1)..];
        return suffix.All(char.IsAsciiDigit) ? segment[..at] : segment;
    }

    /// <summary>The configured audience the token's <c>aud</c> matches, or null. Handles both JWT
    /// shapes — a single string and an array — because the claim is defined as either.</summary>
    private static string? MatchedAudience(JsonElement audience, IReadOnlyCollection<string> accepted)
    {
        switch (audience.ValueKind)
        {
            case JsonValueKind.String:
                return Accepted(audience.GetString());
            case JsonValueKind.Array:
                foreach (var item in audience.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && Accepted(item.GetString()) is { } hit)
                        return hit;
                return null;
            default:
                return null;
        }

        string? Accepted(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;
            var normalized = candidate.Trim().TrimEnd('/');
            return accepted.Any(a =>
                !string.IsNullOrWhiteSpace(a)
                && string.Equals(a.Trim().TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                ? candidate.Trim()
                : null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[]? Decode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
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
        [JsonPropertyName("alg")] public string? Algorithm { get; init; }
        [JsonPropertyName("typ")] public string? Type { get; init; }
        [JsonPropertyName("kid")] public string? KeyId { get; init; }
    }

    private sealed record Payload
    {
        [JsonPropertyName("iss")] public string? Issuer { get; init; }
        [JsonPropertyName("sub")] public string? Subject { get; init; }
        // `aud` is defined as either a string or an array of strings; both shapes are handled.
        [JsonPropertyName("aud")] public JsonElement Audience { get; init; }
        [JsonPropertyName("exp")] public long ExpiresAt { get; init; }
        [JsonPropertyName("nbf")] public long NotBefore { get; init; }
        [JsonPropertyName("repository")] public string? Repository { get; init; }
        [JsonPropertyName("repository_id")] public string? RepositoryId { get; init; }
        [JsonPropertyName("repository_owner")] public string? RepositoryOwner { get; init; }
        [JsonPropertyName("repository_owner_id")] public string? RepositoryOwnerId { get; init; }
        [JsonPropertyName("event_name")] public string? EventName { get; init; }
        [JsonPropertyName("ref")] public string? Ref { get; init; }
        [JsonPropertyName("job_workflow_ref")] public string? JobWorkflowRef { get; init; }
        [JsonPropertyName("workflow")] public string? Workflow { get; init; }
        [JsonPropertyName("actor")] public string? Actor { get; init; }
        [JsonPropertyName("run_id")] public string? RunId { get; init; }
    }
}

/// <summary>
/// The outcome of verifying a GitHub Actions token — THREE shapes, because collapsing the third into
/// a boolean is the defect core #2901 exists to name.
/// </summary>
public sealed record GitHubTokenVerification
{
    /// <summary>What the token claims, when it verified. Null otherwise.</summary>
    public GitHubBuildClaims? Claims { get; init; }

    /// <summary>True when the token's <c>kid</c> was not in the presented key set — NOTHING was
    /// established, and a caller holding a possibly-stale key set may refresh once and re-verify.
    /// This is not an acceptance and not a rejection.</summary>
    public bool KeyUnknown { get; init; }

    /// <summary>True when the token verified.</summary>
    public bool IsVerified => Claims is not null;

    /// <summary>The token did not verify, and the answer is final.</summary>
    public static GitHubTokenVerification Rejected { get; } = new();

    /// <summary>The signing key was not in the presented set — undetermined, not refused.</summary>
    public static GitHubTokenVerification Undetermined { get; } = new() { KeyUnknown = true };

    /// <summary>The token verified; <paramref name="claims"/> is what it says.</summary>
    /// <param name="claims">The verified claims.</param>
    /// <returns>A verified outcome.</returns>
    public static GitHubTokenVerification Verified(GitHubBuildClaims claims) => new() { Claims = claims };
}

/// <summary>One RSA signing key from GitHub's JWKS, reduced to what a verification needs.</summary>
public sealed record GitHubSigningKey
{
    /// <summary>The key's <c>kid</c> — how a token names the key it was signed with.</summary>
    public string KeyId { get; init; } = "";

    /// <summary>RSA modulus (the JWK's <c>n</c>, base64url-decoded).</summary>
    public byte[] Modulus { get; init; } = [];

    /// <summary>RSA public exponent (the JWK's <c>e</c>, base64url-decoded).</summary>
    public byte[] Exponent { get; init; } = [];
}

/// <summary>
/// GitHub's current signing keys, with the instant they were read. The instant is what bounds a
/// forced refresh: an unknown <c>kid</c> may re-read the set, but only if the one in hand is old
/// enough — so a caller spraying invented key ids cannot turn every request into an outbound fetch.
/// </summary>
/// <param name="ByKeyId">The usable keys, by <c>kid</c>.</param>
/// <param name="FetchedAt">When this set was read.</param>
public sealed record GitHubSigningKeys(
    IReadOnlyDictionary<string, GitHubSigningKey> ByKeyId, DateTimeOffset FetchedAt);

/// <summary>
/// What a VERIFIED GitHub Actions token claims about the run that presented it. Identity and
/// context only — never authority: what the run may do is decided against a
/// <see cref="BuildPrincipal"/>.
/// </summary>
public sealed record GitHubBuildClaims
{
    /// <summary>The <c>repository</c> claim — <c>owner/name</c>, possibly in GitHub's immutable
    /// <c>owner@id/name@id</c> form. Compare it with
    /// <see cref="GitHubActionsToken.RepositoryEquals"/>, never with a raw string compare.</summary>
    public string Repository { get; init; } = "";

    /// <summary>The <c>repository_id</c> claim — GitHub's immutable numeric id for the repository.
    /// A principal may pin it, which survives a rename and defeats a name re-registration.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>The <c>repository_owner</c> claim.</summary>
    public string? RepositoryOwner { get; init; }

    /// <summary>The <c>repository_owner_id</c> claim — the owner's immutable numeric id.</summary>
    public string? RepositoryOwnerId { get; init; }

    /// <summary>The <c>event_name</c> claim (<c>push</c>, <c>pull_request</c>, …) — what the
    /// principal's event map is keyed by.</summary>
    public string EventName { get; init; } = "";

    /// <summary>The <c>ref</c> claim (<c>refs/heads/main</c>, <c>refs/pull/12/merge</c>, …).</summary>
    public string? Ref { get; init; }

    /// <summary>The <c>sub</c> claim, verbatim — recorded for the audit trail. Both the classic and
    /// the immutable subject formats appear here; nothing is matched on it.</summary>
    public string? Subject { get; init; }

    /// <summary>The <c>job_workflow_ref</c> claim — which workflow file ran.</summary>
    public string? JobWorkflowRef { get; init; }

    /// <summary>The <c>workflow</c> claim — the workflow's name.</summary>
    public string? Workflow { get; init; }

    /// <summary>The <c>actor</c> claim — who triggered the run.</summary>
    public string? Actor { get; init; }

    /// <summary>The <c>run_id</c> claim — which run, for correlating a decision with a build log.</summary>
    public string? RunId { get; init; }

    /// <summary>The configured audience this token matched.</summary>
    public string Audience { get; init; } = "";

    /// <summary>When the token stops verifying.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
