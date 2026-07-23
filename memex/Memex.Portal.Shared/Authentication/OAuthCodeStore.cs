using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Mesh-backed store for OAuth authorization codes with PKCE support.
///
/// <para>
/// Each pending code is a MeshNode at <c>Admin/OAuthCode/{hashPrefix}</c> (Admin partition,
/// regular <c>mesh_nodes</c> table — PG-persisted, shared by every portal replica). This is
/// what makes the store <b>replica-safe</b>: the previous in-memory ConcurrentDictionary
/// broke the moment KEDA scaled the portal past one replica — <c>/authorize</c> minted the
/// code on pod A, the MCP client's <c>/token</c> exchange landed on pod B, and the exchange
/// failed with invalid_grant (prod 2026-07-23, three pods, every MCP connect dead). Pod
/// restarts likewise wiped all pending codes. Now any replica can exchange a code minted by
/// any other, and codes survive restarts.
/// </para>
///
/// <para>
/// The raw code is never persisted — the node id is the first 12 chars of the code's
/// SHA-256 hash and the content carries the full hash (same scheme as
/// <see cref="ApiTokenService"/>). Codes expire after <see cref="CodeLifetime"/> and are
/// single-use: <b>consumption IS the node delete</b>, and only the caller whose
/// <see cref="IMeshService.DeleteNode"/> actually removed the node may honor the exchange
/// (a missing node makes DeleteNode error with NodeNotFound). First delete wins — the
/// owning per-node hub serialises the deletes, so single-use is atomic across replicas
/// without any distributed lock.
/// </para>
///
/// <para>
/// All nodes live in the Admin partition, which ordinary users have no rights on — every
/// mesh operation here runs under the System identity
/// (<see cref="AccessService.ImpersonateAsSystem"/>), the same pattern
/// <see cref="ApiTokenService"/> uses for the global token index.
/// 🚨 No async/await/Task in this file — the surface is <see cref="IObservable{T}"/>
/// end-to-end; <see cref="OAuthConnectController"/> bridges at the HTTP boundary only.
/// </para>
/// </summary>
internal class OAuthCodeStore(IMeshService meshService, IMessageHub hub, ILogger<OAuthCodeStore> logger)
{
    private const string NodeTypeOAuthCode = "OAuthCode";
    private const string CodeNamespace = "Admin/OAuthCode";

    /// <summary>
    /// Upper bound on the exchange-side node read. Generous because the code node's
    /// per-node hub may cold-activate on the replica that received /token (it was
    /// created on a different replica); the MCP client is blocking on the /token
    /// HTTP response anyway. Typical case is sub-second.
    /// </summary>
    /// <summary>
    /// Bounded read-your-writes wait for the code node to become readable at /token.
    /// A valid code resolves in ~1–2 s (Take(1) on the first matching emission); an
    /// unknown / already-consumed / expired code never matches and falls through to this
    /// timeout → invalid_grant. Default 10 s: ample margin over the observed create→routable
    /// lag (~1 s on memex-cloud) while keeping the negative-path (rare: replay/expiry) fast.
    /// Init-only so tests can shorten it — never a static bound to "tune away" a failure.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Codes expire 5 minutes after issuance (RFC 6749 §4.1.2 recommends ≤10).
    /// Init-settable so tests can pin the expiry branch deterministically
    /// (a zero lifetime makes every issued code already expired) instead of sleeping.
    /// </summary>
    internal TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Generates a new authorization code and persists it as a mesh node under the
    /// System identity. Cold observable — the node write happens on Subscribe and the
    /// raw code is emitted once the create commits (so a following /token exchange on
    /// ANY replica can find it). Errors propagate: a code that could not be persisted
    /// must not be handed to the client.
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

        return AsSystem(() => meshService.CreateNode(node)).Select(_ => code);
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
    /// OAuth hardening — prevents retry brute-force). The consume is the node DELETE, and
    /// the exchange is only honored when THIS caller's delete removed the node — a lost
    /// race (duplicate callback, replay against another replica) surfaces as a failure,
    /// never as a second success.
    /// </para>
    /// </summary>
    public IObservable<CodeExchangeResult> ExchangeCode(
        string code, string clientId, string redirectUri, string? codeVerifier)
    {
        if (string.IsNullOrEmpty(code))
            return Observable.Return(CodeExchangeResult.Failure(UnknownCodeReason));

        var hash = HashRawCode(code);
        var path = $"{CodeNamespace}/{hash[..HashPrefixLength]}";

        // Read the code node through the AUTHORITATIVE single-node primitive —
        // workspace.GetMeshNodeStream(path) — NOT the one-shot hub.GetMeshNode(path)
        // routing read. On a multi-silo portal the code is minted by /authorize on one
        // replica and redeemed by /token on another; a freshly-created node is not
        // instantly routable, so a one-shot GetMeshNode hits [ROUTE] NotFound and the
        // exchange fails (observed on memex-cloud: "Node created at Admin/OAuthCode/{id}"
        // immediately followed by "[ROUTE] NotFound ... Admin/OAuthCode/{id}"). The
        // stream read activates the owning per-node hub from shared Postgres and rides
        // out the create/routing/persistence lag via the transient breaker; the 50 ms
        // poll re-subscribes until the node with our matching CodeHash surfaces (same
        // read-your-writes pattern as OrleansCacheUpdateMultiSiloTest). A valid code
        // resolves in ~1–2 s (Take(1) on first match); a genuinely unknown / already-
        // consumed / expired code never matches and falls through to the ReadTimeout →
        // invalid_grant.
        return AsSystem(() => ReadCodeNode(path, hash))
            .SelectMany(node =>
            {
                var entry = ExtractEntry(node);
                if (entry is null
                    || !string.Equals(entry.CodeHash, hash, StringComparison.OrdinalIgnoreCase))
                    return Observable.Return(CodeExchangeResult.Failure(UnknownCodeReason));

                // Consume FIRST — the delete is the atomic cross-replica single-use gate.
                // DeleteNode errors with "Node not found" when another exchange already
                // consumed the code between our read and this delete; that loser maps to
                // invalid_grant. Any OTHER delete failure (infrastructure) propagates so
                // it surfaces as a server error, not a silent invalid_grant.
                return AsSystem(() => meshService.DeleteNode(path))
                    .Select(_ => true)
                    .Catch<bool, Exception>(ex =>
                        IsNotFound(ex)
                            ? Observable.Return(false)
                            : Observable.Throw<bool>(ex))
                    .Select(consumed => consumed
                        ? Validate(entry, clientId, redirectUri, codeVerifier)
                        : CodeExchangeResult.Failure(
                            "already consumed: lost the single-use consume race (first delete wins) "
                            + "— e.g. a duplicate callback or the same code replayed against another replica"));
            });
    }

    /// <summary>
    /// Reads the code node by exact path, tolerating the create→persist→routable lag.
    /// <para>A freshly-created node is not instantly readable: <c>GetMeshNodeStream</c>
    /// throws "No node found at {path}" during the window before the persistence sampler
    /// flushes and (cross-silo) the owning per-node hub activates from Postgres. Each
    /// attempt swallows that transient error and, ONLY on no-match, re-subscribes after
    /// 50 ms — <c>Concat</c>, never <c>Merge</c>, so exactly ONE owner-hub subscription is
    /// live at a time. A missing / consumed / expired code therefore never accumulates
    /// concurrent subscriptions (the storm the Interval+SelectMany first cut would have
    /// caused); it simply keeps re-probing until the outer <see cref="ReadTimeout"/> fires
    /// → null → invalid_grant. A valid code emits on the first matching attempt and the
    /// outer <c>Take(1)</c> tears the whole chain down.</para>
    /// </summary>
    private IObservable<MeshNode?> ReadCodeNode(string path, string hash)
    {
        IObservable<MeshNode?> Attempt() =>
            hub.GetWorkspace().GetMeshNodeStream(path)
                .Take(1)
                .Where(n => ExtractEntry(n) is { } e
                            && string.Equals(e.CodeHash, hash, StringComparison.OrdinalIgnoreCase))
                .Select(n => (MeshNode?)n)
                .Catch<MeshNode?, Exception>(_ => Observable.Empty<MeshNode?>())
                .Concat(Observable.Defer(Attempt).DelaySubscription(TimeSpan.FromMilliseconds(50)));

        return Observable.Defer(Attempt)
            .Take(1)
            .Timeout(ReadTimeout)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));
    }

    /// <summary>
    /// Post-consume validation — runs only for the caller whose delete won.
    /// Same checks and failure-reason strings as always: expiry, client_id,
    /// redirect_uri, then PKCE. The node is already deleted at this point, so an
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
    /// Fire-and-forget sweep of expired code nodes, run on the generate path. One-shot
    /// snapshot off the cached synced query (listing children is a sanctioned query use);
    /// each expired sibling is deleted best-effort — losing a delete race against a
    /// concurrent sweep or exchange on another replica is the expected outcome for a
    /// loser and only logged, while an unexpected sweep failure surfaces as a warning.
    /// </summary>
    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - CodeLifetime;
        AsSystem(() => hub.GetWorkspace()
                .GetQuery("oauth-codes:cleanup", $"namespace:{CodeNamespace} nodeType:{NodeTypeOAuthCode}"))
            .Take(1)
            .SelectMany(snapshot => snapshot
                .Where(n => n?.Path is not null
                            && ExtractEntry(n) is { } e
                            && e.CreatedAt < cutoff)
                .Select(n => AsSystem(() => meshService.DeleteNode(n.Path))
                    .Catch<bool, Exception>(ex =>
                    {
                        logger.LogDebug(ex,
                            "Expired OAuth code cleanup skipped {Path} (already gone or delete rejected)",
                            n.Path);
                        return Observable.Return(false);
                    }))
                .Merge())
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "Expired OAuth code cleanup sweep failed"));
    }

    /// <summary>
    /// Runs <paramref name="inner"/> under the System identity — code nodes live in the
    /// Admin partition, which the calling end-user has no rights on. Subscribe-time scope
    /// (Observable.Using), same shape as <see cref="ApiTokenService"/>'s index writes.
    /// Null <see cref="AccessService"/> (bare unit-test DI) falls through to the caller's
    /// ambient identity.
    /// </summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> inner)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService is null
            ? Observable.Defer(inner)
            : Observable.Using(() => accessService.ImpersonateAsSystem(), _ => inner());
    }

    /// <summary>
    /// "not found" delete failures mean the code node was already consumed/cleaned —
    /// <see cref="IMeshService.DeleteNode"/> surfaces NodeNotFound as an
    /// <see cref="InvalidOperationException"/> with a "not found" message.
    /// </summary>
    private static bool IsNotFound(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("no node found", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private const int HashPrefixLength = 12;

    private static string HashRawCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>
    /// The mesh path a raw code's node lives at — exposed for tests
    /// (visibility waits, expiry manipulation) via InternalsVisibleTo.
    /// </summary>
    internal static string PathForCode(string code)
        => $"{CodeNamespace}/{HashRawCode(code)[..HashPrefixLength]}";

    /// <summary>
    /// Node content → <see cref="AuthorizationCode"/>. In-process the content usually stays
    /// the typed CLR record (<see cref="OAuthCodeNodeType.AddOAuthCodeType{TBuilder}"/>
    /// registers the type via <c>WithMeshType</c>); the JsonElement fallback covers reads
    /// through hubs that have not registered the type and older persisted rows — same
    /// fallback shape as <c>ApiTokenService.ExtractApiToken</c>.
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
/// Persisted content of an <c>Admin/OAuthCode/{hashPrefix}</c> node. Carries the full
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
