using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Storage-backed store for OAuth authorization codes with PKCE support.
///
/// <para>
/// Each pending code is a row at <c>Admin/OAuthCode/{hashPrefix}</c> written and read
/// DIRECTLY through <see cref="IStorageAdapter"/> — the shared Postgres store on
/// multi-replica portals. Auth is the front door: it must be deterministic on every
/// replica and must not depend on the mesh's cross-silo routing/subscribe machinery
/// being healthy. Both prior shapes failed multi-replica: the in-memory dictionary
/// broke the moment KEDA scaled past one pod (prod 2026-07-23), and the mesh-node
/// version that replaced it (#620) rode <c>GetMeshNodeStream</c>/<c>DeleteNode</c>
/// cross-silo — /token on a non-minting pod hit "[ROUTE] No node found at
/// Admin/OAuthCode/…" with 40 s stale SubscribeRequests while the row sat in
/// Postgres all along (memex-cloud 2026-07-25).
/// </para>
///
/// <para>
/// The storage-direct shape closes both races by construction:
/// <see cref="GenerateCode"/> emits only after <see cref="IStorageAdapter.Write"/>
/// committed the row — the client cannot present a code any replica can't see —
/// and consumption is <see cref="IStorageAdapter.DeleteIfExists"/>, whose single
/// DELETE row count is the atomic cross-replica "first delete wins" gate (a lost
/// race — duplicate callback, replay against another replica — surfaces as a
/// failure, never a second success).
/// </para>
///
/// <para>
/// The raw code is never persisted — the node id is the first 12 chars of the code's
/// SHA-256 hash and the content carries the full hash (same scheme as
/// <see cref="ApiTokenService"/>). Codes expire after <see cref="CodeLifetime"/>.
/// 🚨 No async/await/Task in this file — the surface is <see cref="IObservable{T}"/>
/// end-to-end; <see cref="OAuthConnectController"/> bridges at the HTTP boundary only.
/// </para>
/// </summary>
internal class OAuthCodeStore(IStorageAdapter storage, IMessageHub hub, ILogger<OAuthCodeStore> logger)
{
    private const string NodeTypeOAuthCode = "OAuthCode";
    private const string CodeNamespace = "Admin/OAuthCode";

    /// <summary>
    /// Codes expire 5 minutes after issuance (RFC 6749 §4.1.2 recommends ≤10).
    /// Init-settable so tests can pin the expiry branch deterministically
    /// (a zero lifetime makes every issued code already expired) instead of sleeping.
    /// </summary>
    internal TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Generates a new authorization code and persists it straight to the shared
    /// store. Cold observable — the write happens on Subscribe and the raw code is
    /// emitted only once the row is committed, so a following /token exchange on
    /// ANY replica reads it deterministically. Errors propagate: a code that could
    /// not be persisted must not be handed to the client.
    /// </summary>
    public IObservable<string> GenerateCode(
        string userId,
        string userName,
        string userEmail,
        string clientId,
        string redirectUri,
        string? codeChallenge,
        string? codeChallengeMethod)
    {
        // Opportunistic, fire-and-forget sweep of expired sibling codes (abandoned
        // authorize flows would otherwise accumulate forever). No timer/watchdog —
        // it only ever runs on the reactive generate path.
        CleanupExpired();

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = HashRawCode(code);
        var hashPrefix = hash[..HashPrefixLength];

        var entry = new AuthorizationCode
        {
            CodeHash = hash,
            UserId = userId,
            UserName = userName,
            UserEmail = userEmail,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var node = new MeshNode(hashPrefix, CodeNamespace)
        {
            Name = $"OAuth code {hashPrefix}",
            NodeType = NodeTypeOAuthCode,
            State = MeshNodeState.Active,
            Content = entry,
        };

        return storage.Write(node, hub.JsonSerializerOptions).Select(_ => code);
    }

    /// <summary>
    /// Exchanges an authorization code for the stored entry.
    /// Emits a <see cref="CodeExchangeResult"/> whose <see cref="CodeExchangeResult.Entry"/>
    /// is null on failure — with the exact failing check in
    /// <see cref="CodeExchangeResult.FailureReason"/> so the caller can log a diagnosable
    /// warning (a bare "invalid_grant" made real-world flow failures unattributable).
    /// Validates PKCE code_verifier if a code_challenge was stored.
    ///
    /// <para>
    /// Consume-first is intentional: a code is single-use on ANY exchange attempt (standard
    /// OAuth hardening — prevents retry brute-force). The consume is the atomic
    /// <see cref="IStorageAdapter.DeleteIfExists"/> row delete, and the exchange is only
    /// honored when THIS caller's delete removed the row — a lost race (duplicate callback,
    /// replay against another replica) surfaces as a failure, never as a second success.
    /// No read-retry is needed: <see cref="GenerateCode"/> completes only after the row
    /// committed, so by the time the client presents the code, every replica sees it.
    /// </para>
    /// </summary>
    public IObservable<CodeExchangeResult> ExchangeCode(
        string code, string clientId, string redirectUri, string? codeVerifier)
    {
        if (string.IsNullOrEmpty(code))
            return Observable.Return(CodeExchangeResult.Failure(UnknownCodeReason));

        var hash = HashRawCode(code);
        var path = $"{CodeNamespace}/{hash[..HashPrefixLength]}";

        return storage.Read(path, hub.JsonSerializerOptions)
            .SelectMany(node =>
            {
                var entry = ExtractEntry(node);
                if (entry is null
                    || !string.Equals(entry.CodeHash, hash, StringComparison.OrdinalIgnoreCase))
                    return Observable.Return(CodeExchangeResult.Failure(UnknownCodeReason));

                // Consume — the atomic cross-replica single-use gate. DeleteIfExists
                // emits false when another exchange already consumed the code between
                // our read and this delete; that loser maps to invalid_grant. Any
                // infrastructure failure propagates so it surfaces as a server error,
                // not a silent invalid_grant.
                return storage.DeleteIfExists(path)
                    .Select(consumed => consumed
                        ? Validate(entry, clientId, redirectUri, codeVerifier)
                        : CodeExchangeResult.Failure(
                            "already consumed: lost the single-use consume race (first delete wins) "
                            + "— e.g. a duplicate callback or the same code replayed against another replica"));
            });
    }

    /// <summary>
    /// Post-consume validation — runs only for the caller whose delete won.
    /// Same checks and failure-reason strings as always: expiry, client_id,
    /// redirect_uri, then PKCE. The row is already deleted at this point, so an
    /// expired code is rejected AND gone (no separate cleanup needed for it).
    /// </summary>
    private CodeExchangeResult Validate(
        AuthorizationCode entry, string clientId, string redirectUri, string? codeVerifier)
    {
        var age = DateTimeOffset.UtcNow - entry.CreatedAt;
        if (age > CodeLifetime)
            return CodeExchangeResult.Failure(
                $"expired: age {(int)age.TotalSeconds}s > lifetime {(int)CodeLifetime.TotalSeconds}s");

        if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            return CodeExchangeResult.Failure("client_id mismatch between /authorize and /token");
        if (!string.Equals(entry.RedirectUri, redirectUri, StringComparison.Ordinal))
            return CodeExchangeResult.Failure("redirect_uri mismatch between /authorize and /token");

        if (!string.IsNullOrEmpty(entry.CodeChallenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
                return CodeExchangeResult.Failure(
                    "PKCE code_verifier missing (a code_challenge was supplied at /authorize)");

            if (!VerifyPkce(codeVerifier, entry.CodeChallenge, entry.CodeChallengeMethod))
                return CodeExchangeResult.Failure(
                    "PKCE verification failed (code_verifier does not match code_challenge)");
        }

        return new CodeExchangeResult(entry, null);
    }

    private static bool VerifyPkce(string codeVerifier, string codeChallenge, string? method)
    {
        if (string.Equals(method, "S256", StringComparison.OrdinalIgnoreCase))
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            var computed = Convert.ToBase64String(hash)
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            return string.Equals(computed, codeChallenge, StringComparison.Ordinal);
        }

        // plain method (or no method specified)
        return string.Equals(codeVerifier, codeChallenge, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fire-and-forget sweep of expired code rows, run on the generate path.
    /// Lists the namespace's children straight off the storage adapter, reads each
    /// row, and deletes the expired ones best-effort — losing a delete race against
    /// a concurrent sweep or exchange on another replica is the expected outcome for
    /// a loser and only logged, while an unexpected sweep failure surfaces as a warning.
    /// </summary>
    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - CodeLifetime;
        storage.ListChildPaths(CodeNamespace)
            .Take(1)
            .SelectMany(listing => listing.NodePaths
                .Select(path => storage.Read(path, hub.JsonSerializerOptions)
                    .Where(n => ExtractEntry(n) is { } e && e.CreatedAt < cutoff)
                    .SelectMany(_ => storage.DeleteIfExists(path))
                    .Catch<bool, Exception>(ex =>
                    {
                        logger.LogDebug(ex,
                            "Expired OAuth code cleanup skipped {Path} (already gone or delete rejected)",
                            path);
                        return Observable.Return(false);
                    }))
                .Merge())
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "Expired OAuth code cleanup sweep failed"));
    }

    private const int HashPrefixLength = 12;

    private static string HashRawCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>
    /// The storage path a raw code's row lives at — exposed for tests
    /// (visibility waits, expiry manipulation) via InternalsVisibleTo.
    /// </summary>
    internal static string PathForCode(string code)
        => $"{CodeNamespace}/{HashRawCode(code)[..HashPrefixLength]}";

    /// <summary>
    /// Node content → <see cref="AuthorizationCode"/>. Rows read back through the
    /// storage adapter usually carry a JsonElement payload; the direct-CLR case covers
    /// in-memory adapters that round-trip the typed record — same fallback shape as
    /// <c>ApiTokenService.ExtractApiToken</c>.
    /// </summary>
    private static AuthorizationCode? ExtractEntry(MeshNode? node)
    {
        switch (node?.Content)
        {
            case AuthorizationCode direct:
                return direct;
            case System.Text.Json.JsonElement jsonElement:
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<AuthorizationCode>(
                        jsonElement.GetRawText(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return null;
                }
            default:
                return null;
        }
    }

    private const string UnknownCodeReason =
        "unknown or already consumed code (never issued, expired-and-cleaned, or burnt by an "
        + "earlier exchange attempt on any replica — e.g. a duplicate callback)";
}

/// <summary>
/// Persisted content of an <c>Admin/OAuthCode/{hashPrefix}</c> row. Carries the full
/// SHA-256 hash of the code (never the raw code) plus everything the /token exchange
/// validates and the token issuance needs.
/// </summary>
internal record AuthorizationCode
{
    public required string CodeHash { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string UserEmail { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Result of <see cref="OAuthCodeStore.ExchangeCode"/>: exactly one of
/// <see cref="Entry"/> (success) or <see cref="FailureReason"/> (the exact failing
/// check, logged by the /token endpoint — the wire response stays a generic
/// invalid_grant per RFC 6749).
/// </summary>
internal record CodeExchangeResult(AuthorizationCode? Entry, string? FailureReason)
{
    public static CodeExchangeResult Failure(string reason) => new(null, reason);
}
