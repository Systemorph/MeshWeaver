using System.Reactive.Linq;
using System.Text.Json;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// THE verdict on a raw <c>mw_</c> API token: one pure function over a node READ, so every path
/// that authenticates a token reaches the same answer from the same records.
///
/// <para>Two callers, one rule. The <c>ApiToken/{hashPrefix}</c> hub's
/// <c>ValidateTokenRequest</c> handler supplies a read through the mesh; the HTTP auth middleware
/// supplies a read straight from the authoritative store (<see cref="Services.IStorageAdapter"/>)
/// so that authenticating a request does not depend on a hub hop being answered. On memex,
/// 2026-09-30 05:23Z, a request forwarded to <c>ApiToken/342abeed8e6d</c> on a replica that had
/// been Ready for 40 minutes was routed and never handled; every MCP call with that token then
/// waited 60 s and answered 503 for five minutes, and the MCP client read that as "sign in
/// again". The store read carries no such hop (Doc/Architecture/TokenValidationHotPath).</para>
///
/// <para>Three outcomes, never collapsed (issue #637): a SUCCESS; a DEFINITIVE negative (format,
/// not found, hash mismatch, revoked, expired or idle-expired (<see cref="OAuthTokenLifetime"/>),
/// service identity refused) via
/// <see cref="ValidateTokenResponse.Fail"/>; and UNAVAILABLE — a read that faulted or did not
/// answer within <see cref="ReadBound"/> — via <see cref="ValidateTokenResponse.Unavailable"/>,
/// which callers answer as retryable and never as an invalid token.</para>
/// </summary>
public static class ApiTokenVerdict
{
    /// <summary>The bound on each record read. A read that has not answered by then is UNAVAILABLE.</summary>
    public static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(10);

    /// <summary>The namespace of the per-hash index nodes (<c>ApiToken/{hashPrefix}</c>).</summary>
    public const string IndexNamespace = "ApiToken";

    /// <summary>How many hex characters of the hash name the index node.</summary>
    public const int HashPrefixLength = 12;

    /// <summary>The index node's path for a token hash: <c>ApiToken/{first 12 hex}</c>.</summary>
    /// <param name="tokenHash">The SHA-256 hex hash of the raw token.</param>
    /// <returns>The index path.</returns>
    public static string IndexPath(string tokenHash) => $"{IndexNamespace}/{tokenHash[..HashPrefixLength]}";

    /// <summary>
    /// Decides a raw token. Always emits exactly one response and completes — a read that faults
    /// or times out becomes UNAVAILABLE, never an error and never silence.
    /// </summary>
    /// <param name="rawToken">The raw bearer token as presented.</param>
    /// <param name="read">Reads one node by path; emits the node (or null / nothing when absent).
    /// Runs with whatever identity the caller established — the token records are system-owned.</param>
    /// <param name="options">The serializer options the records deserialize with.</param>
    /// <returns>The verdict.</returns>
    public static IObservable<ValidateTokenResponse> Decide(
        string? rawToken, Func<string, IObservable<MeshNode?>> read, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
            return Observable.Return(ValidateTokenResponse.Fail("Invalid token format"));

        var hash = ValidateTokenRequest.HashToken(rawToken);
        return ReadOne(read, IndexPath(hash))
            .SelectMany(indexNode =>
            {
                if (indexNode is null)
                    return Observable.Return(ValidateTokenResponse.Fail("Token not found"));

                var index = ContentOf<ApiTokenIndex>(indexNode, options);
                if (index is { TokenPath.Length: > 0 })
                {
                    if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                        return Observable.Return(ValidateTokenResponse.Fail("Invalid token"));
                    return ReadOne(read, index.TokenPath)
                        .SelectMany(tokenNode => IsNotATokenRecord(tokenNode)
                            // 🛡️ Only a TOKEN RECORD authenticates (review of #7585). The index outlives
                            // its record, so a record deleted and recreated at the same path as another
                            // type — carrying ApiToken-shaped content with no scopes — would otherwise
                            // authenticate past every rule that protects token records.
                            ? Observable.Return(ValidateTokenResponse.Fail("Token not found"))
                            : ContentOf<ApiToken>(tokenNode, options) is { } record
                              && !IsBoundToItsLocation(index.TokenPath, record)
                                // 🛡️ The principal is bound to WHERE the record lives (#7598).
                                ? Observable.Return(ValidateTokenResponse.Fail("Token not found"))
                                : DecideToken(ContentOf<ApiToken>(tokenNode, options), hash, read, options));
                }

                // A legacy index node carries the token record itself. It lives in the platform-only
                // ApiToken partition, so it has no owner's location to bind to — but it still never
                // authenticates as System or as a principal that is a path (review of #7601).
                var legacy = ContentOf<ApiToken>(indexNode, options);
                return legacy is not null && !IsAPersonOrServicePrincipal(legacy.UserId)
                    ? Observable.Return(ValidateTokenResponse.Fail("Token not found"))
                    : DecideToken(legacy, hash, read, options);
            })
            .Catch((Exception ex) => Observable.Return(
                ValidateTokenResponse.Unavailable($"Validation error: {ex.GetType().Name}: {ex.Message}")));
    }

    /// <summary>
    /// 🛡️ THE INVARIANT the write rules lean on (review of #7585): a credential authenticates ONLY
    /// from a node TYPED as an API-token record. Content alone never makes a credential — not a
    /// foreign-typed node, not an untyped or empty-typed one — because every rule that protects
    /// token records (RLS, the owner's gate, the commit point) recognises them by type. Token
    /// records are written typed (the store's <c>node_type = 'ApiToken'</c>). A legacy index that
    /// carries the record itself is decided on its own branch and is untouched.
    /// </summary>
    /// <summary>
    /// 🛡️ A token's PRINCIPAL is bound to the LOCATION of its record (#7598). The index is written
    /// only by the platform, as System, and names the path the token was minted at. A person's token
    /// is minted in that person's own partition (<c>{UserId}/…</c>), and a service's token in its own
    /// token namespace under the Admin partition (<see cref="ServiceIdentity.TokenNamespace"/>). So
    /// whatever later happens to the record — rewritten, cleared and restored, recreated, confirmed
    /// from a transient, carried in a data change, reached through a cold owner — it authenticates
    /// only as the principal whose location it is. A write inside a person's own partition can then
    /// only ever yield a credential for that person. This is decided on the verdict, so it holds for
    /// every write seam at once, including the ones no write rule sees.
    /// </summary>
    /// <param name="tokenPath">The record's path, as the platform-written index names it.</param>
    /// <param name="token">The record's content.</param>
    /// <returns>True when the record's principal is the one its location belongs to.</returns>
    public static bool IsBoundToItsLocation(string tokenPath, ApiToken token)
    {
        if (string.IsNullOrEmpty(tokenPath) || !IsAPersonOrServicePrincipal(token.UserId))
            return false;
        var path = tokenPath.Trim('/');
        // CLASSIFY the location first, then read its principal (review of #7601): a prefix test on
        // the principal alone let the service namespace authenticate its partition's name ("Admin")
        // and the pre-migration layout its first segment ("User").
        if (path.StartsWith(ServiceIdentity.Namespace + "/", StringComparison.OrdinalIgnoreCase))
            return ServiceIdentity.IsServiceObjectId(token.UserId)
                   && path.StartsWith(ServiceIdentity.TokenNamespace(token.UserId) + "/", StringComparison.OrdinalIgnoreCase);
        if (ServiceIdentity.IsServiceObjectId(token.UserId) || !string.IsNullOrEmpty(token.ServiceIdentityPath))
            return false;
        var segments = path.Split('/');
        var owner = segments.Length > 2 && string.Equals(segments[0], "User", StringComparison.OrdinalIgnoreCase)
            ? segments[1]   // the pre-migration layout, User/{userId}/…
            : segments[0];  // a person's own partition, {userId}/…
        return segments.Length > 1 && string.Equals(owner, token.UserId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="userId"/> can name a principal at all: not empty, not System,
    /// and a single path segment (a principal is an id, never a path).</summary>
    private static bool IsAPersonOrServicePrincipal(string? userId) =>
        !string.IsNullOrWhiteSpace(userId)
        // Case-insensitive, as the platform predicate reads it (AccessService.IsPlatformPrincipal):
        // a case variant of System is System to every consumer of the identity (review of #7601).
        && !string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
        && !userId.Contains('/');

    private static bool IsNotATokenRecord(MeshNode? node)
        => node is not null && !ApiTokenScope.IsTokenRecordType(node.NodeType);

    private static IObservable<ValidateTokenResponse> DecideToken(
        ApiToken? apiToken, string hash, Func<string, IObservable<MeshNode?>> read, JsonSerializerOptions options)
    {
        if (apiToken is null)
            return Observable.Return(ValidateTokenResponse.Fail("Token not found"));
        if (!string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(ValidateTokenResponse.Fail("Invalid token"));
        if (apiToken.IsRevoked)
            return Observable.Return(ValidateTokenResponse.Fail("Token revoked"));
        if (apiToken.ExpiresAt is { } expiresAt && expiresAt < DateTimeOffset.UtcNow)
            return Observable.Return(ValidateTokenResponse.Fail("Token expired"));
        // The same idle rule the HTTP authentication handler applies (OAuthTokenLifetime): a token
        // refused there must not authenticate through this verdict instead.
        if (OAuthTokenLifetime.IsIdle(apiToken, DateTimeOffset.UtcNow))
            return Observable.Return(ValidateTokenResponse.Fail("OAuth token expired after being unused"));

        // Diagnostic only — no permission decision reads the mint-time roles (see ApiToken.Roles).
        var response = ValidateTokenResponse.Ok(apiToken.UserId, apiToken.UserName, apiToken.UserEmail, apiToken.Roles)
            // The scope CAP travels with the verdict; an unscoped token carries null (not []).
            with { Scopes = ApiTokenScope.IsScoped(apiToken.Scopes) ? apiToken.Scopes : null };

        if (string.IsNullOrEmpty(apiToken.ServiceIdentityPath))
            // A person's token. One naming a SERVICE object id without an identity path was not
            // minted by the service surface — refuse it.
            return Observable.Return(ServiceIdentity.IsServiceObjectId(apiToken.UserId)
                ? ValidateTokenResponse.Fail("Service token without an identity record")
                : response);

        // A SERVICE token authenticates only while its identity record exists and is not revoked —
        // read on every use, so revoking the identity revokes every token it holds at once.
        return ReadOne(read, apiToken.ServiceIdentityPath)
            .Select(identityNode =>
            {
                var refusal = ServiceIdentity.Refuse(
                    ContentOf<ServiceIdentity>(identityNode, options), apiToken.UserId, apiToken.ServiceIdentityPath);
                return refusal is null ? response with { IsService = true } : ValidateTokenResponse.Fail(refusal);
            });
    }

    /// <summary>One bounded read: the first emission, null when the read completes empty, a
    /// TimeoutException past <see cref="ReadBound"/> (which <see cref="Decide"/> turns into
    /// UNAVAILABLE).</summary>
    private static IObservable<MeshNode?> ReadOne(Func<string, IObservable<MeshNode?>> read, string path)
        => Observable.Defer(() => read(path)).Take(1).DefaultIfEmpty().Timeout(ReadBound);

    /// <summary>Reads the content in whatever shape it arrives — typed on the owning hub, a
    /// JsonElement from a store or across a hub boundary.</summary>
    /// <summary>
    /// The <see cref="ApiToken"/> a node carries, read EXACTLY as the verdict reads it — typed content,
    /// or a raw JSON element deserialized as one — or null. The ONE reading the write rules use to
    /// recognise a credential, so a write cannot produce a node the verdict would authenticate that the
    /// rules did not see as one.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="options">The serializer options.</param>
    public static ApiToken? ReadToken(MeshNode? node, JsonSerializerOptions options) => ContentOf<ApiToken>(node, options);

    private static T? ContentOf<T>(MeshNode? node, JsonSerializerOptions options) where T : class
    {
        if (node is null)
            return null;
        if (node.ContentAs<T>(options) is { } typed)
            return typed;
        if (node.Content is not JsonElement element)
            return null;
        try { return JsonSerializer.Deserialize<T>(element.GetRawText(), options); }
        catch (JsonException) { return null; }
    }
}
