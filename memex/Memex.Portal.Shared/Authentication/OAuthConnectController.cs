using System.Linq;
using System.Reactive.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MeshWeaver.Hosting.AspNetCore.Portal; // PortalApplication
using MeshWeaver.Mesh.Security;         // ApiToken (the minted token content)
using MeshWeaver.Messaging;             // AccessService / AccessContext
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Minimal OAuth 2.0 authorization server for MCP clients (claude.ai Connectors, Claude Desktop).
/// Implements authorization code flow with PKCE + RFC 7591 Dynamic Client Registration.
/// Issues mw_ API tokens as access tokens, reusing the existing ApiTokenService infrastructure.
/// </summary>
[ApiController]
public class OAuthConnectController(
    IServiceProvider serviceProvider,
    ILogger<OAuthConnectController> logger) : ControllerBase
{
    private OAuthCodeStore CodeStore => serviceProvider.GetRequiredService<OAuthCodeStore>();
    private ApiTokenService TokenService => serviceProvider.GetRequiredService<ApiTokenService>();

    /// <summary>
    /// Resolves the mesh <c>User.Id</c> for the issued token. 🚨 It MUST be the mesh
    /// User.Id (e.g. <c>rbuergi</c>), NEVER the raw <c>preferred_username</c> claim — Entra
    /// fills that with the email/UPN, and an email userId routes the token node + its
    /// <c>_Access</c> self-scope into a non-existent <c>{email}</c> partition (401 on every
    /// freshly-minted token once the router stopped lazy-creating schemas).
    /// <para>Prefers the authoritative identity <c>UserContextMiddleware</c> stamped on the
    /// portal hub's <see cref="AccessService"/> (email→User.Id); falls back to normalising the
    /// claim to the username (email local-part) when no resolved context is present — e.g.
    /// controller unit tests, or any call before the middleware ran.</para>
    /// </summary>
    private string? ResolveMeshUserId()
    {
        var ctx = serviceProvider.GetService<PortalApplication>()?
            .Hub.ServiceProvider.GetService<AccessService>()?.Context;
        var resolved = ctx?.ObjectId;
        if (!string.IsNullOrEmpty(resolved) && !resolved.Contains('@'))
            return resolved;
        var claim = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Email);
        return UsernameFromEmail(claim);
    }

    /// <summary>Email-shaped identifier → its local part (the post-v10 username / mesh
    /// partition key, e.g. <c>rbuergi@systemorph.com → rbuergi</c>); unchanged when there's no <c>@</c>.</summary>
    private static string? UsernameFromEmail(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }

    /// <summary>
    /// RFC 8414 — OAuth Authorization Server Metadata.
    /// MCP clients discover this via the authorization_servers URL from the protected resource metadata.
    /// </summary>
    [HttpGet("/.well-known/oauth-authorization-server")]
    [AllowAnonymous]
    public IActionResult GetServerMetadata()
    {
        var origin = $"{Request.Scheme}://{Request.Host}";
        logger.LogInformation("OAuth metadata requested from {Origin}", origin);
        return Ok(new
        {
            issuer = origin,
            authorization_endpoint = $"{origin}/authorize",
            token_endpoint = $"{origin}/token",
            registration_endpoint = $"{origin}/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
        });
    }

    /// <summary>
    /// RFC 7591 — Dynamic Client Registration.
    /// MCP clients (Claude Desktop, claude.ai Connectors) self-register here with their redirect URIs
    /// before running the authorization flow. The <c>client_id</c> is DERIVED DETERMINISTICALLY from the
    /// client's own metadata (name + redirect URIs) and this deployment's origin, so a client that
    /// re-registers — which MCP clients do on every reconnect — gets the SAME id back and is recognised
    /// as the same application.
    ///
    /// <para>🚨 It used to be <c>RandomNumberGenerator</c>, and that is what made users re-consent on
    /// every single connection: a fresh random id is, to the authorization server and to any consent
    /// record keyed on it, a brand-new application. Nothing else changes — the id is still opaque to the
    /// caller and echoed back in <c>/authorize</c> and <c>/token</c>, where the code store validates
    /// client_id+redirect_uri consistency between the two calls.</para>
    ///
    /// <para>A <c>client_id</c> is public by construction in OAuth (these are PUBLIC clients:
    /// <c>token_endpoint_auth_method=none</c>), so deriving rather than randomising it gives away
    /// nothing — the flow's security rests on exact redirect_uri matching plus PKCE, both unchanged.
    /// The origin is folded into the hash so ids are not shared across deployments.</para>
    /// </summary>
    [HttpPost("/register")]
    [AllowAnonymous]
    public IActionResult RegisterClient([FromBody] ClientRegistrationRequest? request)
    {
        if (request is null)
        {
            logger.LogWarning("OAuth /register called with empty or invalid body");
            return BadRequest(new { error = "invalid_client_metadata", error_description = "Request body is required" });
        }

        logger.LogInformation(
            "OAuth client registration: client_name={ClientName}, redirect_uris={RedirectUris}, grant_types={GrantTypes}, auth_method={AuthMethod}",
            request.ClientName ?? "(unset)",
            request.RedirectUris is null ? "(none)" : string.Join(",", request.RedirectUris),
            request.GrantTypes is null ? "(unset)" : string.Join(",", request.GrantTypes),
            request.TokenEndpointAuthMethod ?? "(unset)");

        if (request.RedirectUris is null || request.RedirectUris.Length == 0)
        {
            logger.LogWarning("OAuth /register rejected: redirect_uris missing for client {ClientName}", request.ClientName);
            return BadRequest(new { error = "invalid_redirect_uri", error_description = "redirect_uris is required" });
        }

        var clientId = DeriveClientId(
            $"{Request.Scheme}://{Request.Host}", request.ClientName, request.RedirectUris);

        var response = new ClientRegistrationResponse
        {
            ClientId = clientId,
            ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientName = request.ClientName,
            RedirectUris = request.RedirectUris,
            GrantTypes = request.GrantTypes ?? new[] { "authorization_code" },
            ResponseTypes = request.ResponseTypes ?? new[] { "code" },
            TokenEndpointAuthMethod = request.TokenEndpointAuthMethod ?? "none",
        };

        logger.LogInformation(
            "Issued OAuth client_id {ClientId} for {ClientName} (derived — a re-registering client receives the same id)",
            clientId, request.ClientName);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// Derives a stable <c>client_id</c> from the registering client's own metadata.
    /// Redirect URIs are ordered before hashing so the id does not depend on the order the client
    /// happened to list them in, and the deployment origin is included so the same client registering
    /// against two portals receives two distinct ids.
    /// </summary>
    internal static string DeriveClientId(string origin, string? clientName, string[] redirectUris)
    {
        var canonical = string.Join(
            "\n",
            new[] { origin, clientName?.Trim() ?? string.Empty }
                .Concat(redirectUris
                    .Select(u => u?.Trim() ?? string.Empty)
                    .OrderBy(u => u, StringComparer.Ordinal)));

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(digest, 0, 24)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// OAuth Authorization Endpoint — redirects authenticated users to the client's redirect_uri
    /// with an authorization code. Unauthenticated users are sent to /login first.
    /// The code is persisted as a mesh node (replica-safe: the /token exchange may land on a
    /// different pod), so this action bridges the store's IObservable to Task at the HTTP
    /// boundary — same single-bridge shape as <see cref="ExchangeToken"/>.
    /// </summary>
    [HttpGet("/authorize")]
    public Task<IActionResult> Authorize(
        [FromQuery] string response_type,
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string? state,
        [FromQuery] string? scope,
        [FromQuery] string? code_challenge,
        [FromQuery] string? code_challenge_method,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "OAuth /authorize: response_type={ResponseType}, client_id={ClientId}, redirect_uri={RedirectUri}, has_state={HasState}, has_pkce={HasPkce}, authenticated={Authenticated}",
            response_type, client_id, redirect_uri,
            !string.IsNullOrEmpty(state), !string.IsNullOrEmpty(code_challenge),
            User?.Identity?.IsAuthenticated == true);

        if (response_type != "code")
        {
            logger.LogWarning("OAuth /authorize rejected: unsupported response_type={ResponseType}", response_type);
            return Task.FromResult<IActionResult>(BadRequest(new { error = "unsupported_response_type" }));
        }

        if (string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(redirect_uri))
        {
            logger.LogWarning("OAuth /authorize rejected: missing client_id or redirect_uri");
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_request", error_description = "client_id and redirect_uri are required" }));
        }

        // If user is not authenticated, redirect to login carrying THIS request as the return URL.
        //
        // 🚨 The returnUrl is a LOCAL path — `{Path}{QueryString}`, never `{Scheme}://{Host}{Path}…`.
        // Every returnUrl SINK in the portal validates local-only (ReturnUrlPolicy.Sanitize here,
        // LocalUrl.IsLocal in the GUI), because an unvalidated one is an open redirect. Those rules
        // are correct and must not be relaxed; what was wrong was this SOURCE minting an absolute
        // URL that no sink can accept. The login page's LocalOrNull then dropped it silently, the
        // provider link was built with no returnUrl at all, and the user landed on "/" after signing
        // in with the /authorize request gone — so no authorization code was ever issued and the MCP
        // client waited forever for an "authentication successful" that could not arrive (#5074).
        // The absolute form was also needless: the login page and this endpoint are the same origin.
        // OnboardingMiddleware carries the same request the same way, for the same reason.
        if (User?.Identity?.IsAuthenticated != true)
        {
            var authorizePath = $"{Request.Path}{Request.QueryString}";
            var loginUrl = $"/login?returnUrl={Uri.EscapeDataString(ReturnUrlPolicy.Sanitize(authorizePath))}";
            logger.LogInformation("OAuth /authorize: redirecting unauthenticated caller to {LoginUrl}", loginUrl);
            return Task.FromResult<IActionResult>(Redirect(loginUrl));
        }

        // Extract user identity from cookie claims (email/name are display
        // fields). The token's userId is the MESH User.Id, resolved by
        // UserContextMiddleware onto AccessService.Context for this cookie
        // request — NOT preferred_username, which Entra fills with the email.
        var email = User.FindFirstValue(ClaimTypes.Email)
                    ?? User.FindFirstValue("email")
                    ?? User.FindFirstValue("preferred_username")
                    ?? "";
        var name = User.FindFirstValue(ClaimTypes.Name)
                   ?? User.FindFirstValue("name")
                   ?? email;
        var userId = ResolveMeshUserId();

        if (string.IsNullOrEmpty(email))
        {
            logger.LogWarning("OAuth /authorize rejected: authenticated principal has no email/preferred_username claim");
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_request", error_description = "Unable to determine user identity" }));
        }

        // Refuse to issue a code with an unresolved or email-shaped userId — it
        // would mint the token into a parallel {email} partition that owns none
        // of the user's data (the original prod 401). A missing mesh identity
        // means the User node isn't provisioned yet; the user should retry after
        // a normal browser login populates the identity cache.
        if (string.IsNullOrEmpty(userId) || userId.Contains('@'))
        {
            logger.LogWarning(
                "OAuth /authorize rejected: no resolved mesh identity for {Email} (userId='{UserId}'). "
                + "Retry after a browser login provisions/loads the User node.",
                email, userId ?? "(null)");
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_request", error_description = "Unable to determine user identity" }));
        }

        // Generate + persist the authorization code (mesh node — visible to every
        // replica). The redirect only fires once the node write committed; a code
        // whose persistence failed must never reach the client (it could not be
        // exchanged anywhere), so a store error surfaces as 500 server_error.
        return CodeStore.GenerateCode(
                userId: userId,
                userName: name,
                userEmail: email,
                clientId: client_id,
                redirectUri: redirect_uri,
                codeChallenge: code_challenge,
                codeChallengeMethod: code_challenge_method)
            .Select(code =>
            {
                logger.LogInformation("Issued OAuth authorization code for user {Email}, client {ClientId}", email, client_id);

                // Redirect to client with code (and state if provided)
                var callbackUrl = string.IsNullOrEmpty(state)
                    ? $"{redirect_uri}?code={Uri.EscapeDataString(code)}"
                    : $"{redirect_uri}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";

                return (IActionResult)Redirect(callbackUrl);
            })
            .Catch<IActionResult, Exception>(ex =>
            {
                logger.LogError(ex,
                    "OAuth /authorize failed to persist the authorization code for user {Email}, client {ClientId}",
                    email, client_id);
                return Observable.Return<IActionResult>(StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { error = "server_error", error_description = "Failed to persist the authorization code" }));
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger.LogWarning(ex,
                    "OAuth /authorize for client {ClientId} faulted after the response had already been sent",
                    client_id),
                ct)!;
    }

    /// <summary>
    /// OAuth Token Endpoint — exchanges an authorization code for an API token.
    /// The issued token is a standard mw_ API token, indistinguishable from manually created ones.
    /// </summary>
    [HttpPost("/token")]
    [AllowAnonymous]
    public Task<IActionResult> ExchangeToken([FromForm] TokenRequest request, CancellationToken ct)
    {
        logger.LogInformation(
            "OAuth /token: grant_type={GrantType}, client_id={ClientId}, redirect_uri={RedirectUri}, has_code={HasCode}, has_verifier={HasVerifier}",
            request.grant_type, request.client_id, request.redirect_uri,
            !string.IsNullOrEmpty(request.code), !string.IsNullOrEmpty(request.code_verifier));

        if (request.grant_type != "authorization_code")
        {
            logger.LogWarning("OAuth /token rejected: unsupported grant_type={GrantType}", request.grant_type);
            return Task.FromResult<IActionResult>(BadRequest(new { error = "unsupported_grant_type" }));
        }

        if (string.IsNullOrEmpty(request.code) || string.IsNullOrEmpty(request.client_id) || string.IsNullOrEmpty(request.redirect_uri))
        {
            logger.LogWarning("OAuth /token rejected: missing code/client_id/redirect_uri");
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_request" }));
        }

        // 🚨 Resolved HERE, while the request's scope is provably alive (MeshWeaver.Feedback#13).
        // The chain below outlives the request whenever the client aborts: `.ObserveCompletion(…, ct)`
        // cancels the WAIT, not the source, so the exchange keeps running after the response is gone.
        // Read lazily inside it, these properties resolved from a disposed request scope — the
        // "faulted after the response had already been sent" warning measured on memex. Both are
        // root singletons, so the captured instances stay valid for the whole chain.
        var codeStore = CodeStore;
        var tokens = TokenService;

        // Exchange the code against the mesh-backed store (replica-safe: the code may
        // have been minted by any pod, and the single-use consume is atomic across
        // replicas — first delete wins), then mint the mw_ API token. One reactive
        // chain, single bridge to Task at .ObserveCompletion(…, ct) — never Rx's .ToTask(), which
        // resumes the awaiter INLINE on the signalling thread (forbidden since 2026-08-30).
        return codeStore.ExchangeCode(
                request.code,
                request.client_id,
                request.redirect_uri,
                request.code_verifier)
            .SelectMany(exchange =>
            {
                if (exchange.Entry is null)
                {
                    // The reason names the exact failing check (unknown/consumed, lost consume
                    // race, expired, client_id, redirect_uri, PKCE) — the wire response stays
                    // a generic invalid_grant per RFC 6749.
                    logger.LogWarning(
                        "OAuth token exchange failed for client {ClientId}: {FailureReason}",
                        request.client_id, exchange.FailureReason);
                    return Observable.Return<IActionResult>(BadRequest(new { error = "invalid_grant" }));
                }

                var entry = exchange.Entry;

                // 🚨 A client that has already given up receives nothing we mint — so mint nothing.
                // The token would be a year-long credential no client holds, removed only by a
                // later supersede for the same client_id, which for a client whose id changes per
                // registration never comes (MeshWeaver.Feedback#13 / #12). The code is consumed
                // either way: it is single-use, and the client re-runs the whole flow.
                if (ct.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "OAuth /token for client {ClientId}: the client abandoned the exchange before a "
                        + "token was minted — no token issued, nothing to revoke",
                        request.client_id);
                    return Observable.Return<IActionResult>(BadRequest(new { error = "invalid_request" }));
                }

                // Create an mw_ API token via the existing token service. Lifetime
                // is long-lived because OAuth clients (MCP, CLI tools) typically
                // can't run interactive re-auth flows — a token that expires in 30
                // days surprises users who connect once and come back months later.
                // Refresh-token flow isn't implemented yet; until it is, default to
                // 1 year. Bump if needed via TokenLifetime below.
                var label = $"OAuth: {request.client_id}";

                return tokens.CreateToken(
                        userId: entry.UserId,
                        userName: entry.UserName,
                        userEmail: entry.UserEmail,
                        label: label,
                        expiresAt: DateTimeOffset.UtcNow.Add(TokenLifetime))
                    // 🚨 ONE live credential per client, not N (#1493). Every authorization used to
                    // mint a FRESH year-long token and leave the previous one live and listed, so a
                    // reinstall, a new device, or a revoked-and-reconnected integration each left
                    // another usable credential behind — for a year. That is what dominated the 86
                    // ApiToken rows found on the live portal, and each of them is a key that still
                    // opens the door.
                    //
                    // Ordered mint-THEN-supersede, never the reverse: if the mint fails the client
                    // must keep the credential it already has, so revoking first could strand it
                    // with none at all.
                    //
                    // 🚨 And the abandonment check sits BETWEEN the two: a client that gave up while
                    // the mint was in flight never receives the new token, so it is REVOKED and the
                    // supersede is SKIPPED — superseding first would delete the credential the client
                    // still holds and leave it with none (MeshWeaver.Feedback#13).
                    //
                    // The check is not one snapshot: SupersedePreviousTokens re-reads it after its
                    // listing and before EACH delete, and answers false when it stopped because the
                    // client left — the new token is then revoked just the same.
                    .SelectMany(creation => (ct.IsCancellationRequested
                            ? Observable.Return(false)
                            : SupersedePreviousTokens(
                                tokens, entry.UserId, label, creation.Node.Path, MintedAt(creation), ct))
                        .SelectMany(completed => completed
                            ? Observable.Return((Creation: creation, Abandoned: (IActionResult?)null))
                            : RevokeUndeliveredToken(tokens, creation, request.client_id)))
                    .Select(step =>
                    {
                        if (step.Abandoned is { } refused)
                            return refused;
                        var creation = step.Creation;
                        logger.LogInformation("Issued OAuth access token for user {Email}, client {ClientId}", entry.UserEmail, request.client_id);
                        return (IActionResult)Ok(new
                        {
                            access_token = creation.RawToken,
                            token_type = "Bearer",
                            expires_in = (int)TokenLifetime.TotalSeconds,
                        });
                    });
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger.LogWarning(ex,
                    "OAuth /token for client {ClientId} faulted after the response had already been sent",
                    request.client_id),
                ct)!;
    }

    /// <summary>
    /// Removes this client's PREVIOUS tokens once a fresh authorization has minted its replacement
    /// (#1493), so a `(user, client_id)` pair has exactly one live credential.
    ///
    /// <para>Identity is the label — <c>OAuth: {client_id}</c> — and <c>client_id</c> is DERIVED
    /// from the client's own metadata (see <see cref="DeriveClientId"/>), so a shared label means
    /// the same client. When ids were still random per registration this filter never matched
    /// anything, which is exactly why the token rows piled up; a re-registering client keeping its
    /// id is what makes superseding fire at all. The token just minted is excluded by PATH, which
    /// also makes the read's lag harmless: a listing that has not caught up yet simply leaves an
    /// older row for the next authorization to collect, and can never take the new one.</para>
    ///
    /// <para>Deleted, not merely marked revoked: this issue is BOTH "one live credential" and the
    /// unbounded accumulation behind it, and a revoked row keeps accumulating. It matches what
    /// #1477's expiry sweep already does with a dead credential.</para>
    ///
    /// <para>Never fails the exchange. The client has a valid token by this point; housekeeping
    /// that could not complete is a Warning and the next authorization tries again.</para>
    /// </summary>
    private IObservable<bool> SupersedePreviousTokens(
        ApiTokenService tokens, string userId, string label, string keepPath, DateTimeOffset mintedAt,
        CancellationToken abandoned)
    {
        return tokens.GetTokensForUser(userId)
            .Take(1)
            .SelectMany(all =>
            {
                // 🚨 Supersede only what is strictly OLDER, in a TOTAL order — never "everything
                // that is not mine". Two concurrent exchanges for the same (user, client_id) each
                // see the other's freshly-minted token in this listing, and a not-mine rule would
                // have them delete each other's: both clients then walk away holding a credential
                // that was removed moments later. Ordering by (CreatedAt, path) makes the outcome
                // convergent instead — every participant deletes strictly below itself, so the
                // NEWEST token survives no matter which exchange evaluates last, and there is
                // still exactly one live credential at the end.
                //
                // The path tiebreak matters: CreatedAt is a UTC timestamp and two exchanges can
                // land on the same tick, where "strictly older by time" would let both survive.
                var superseded = all
                    .Where(t => t.Label == label && t.NodePath != keepPath)
                    .Where(t => t.CreatedAt < mintedAt
                                || (t.CreatedAt == mintedAt
                                    && string.CompareOrdinal(t.NodePath, keepPath) < 0))
                    .Select(t => t.NodePath)
                    .ToArray();
                if (superseded.Length == 0)
                    return Observable.Return(true);
                // Re-checked AFTER the listing (it is a live read, and the client may have left while
                // it ran) and again before each delete below: an abandoned exchange must not retire
                // the credential the client still holds (MeshWeaver.Feedback#13).
                if (abandoned.IsCancellationRequested)
                    return Observable.Return(false);

                // 🚨 Name the rows — twice, and the two lines say different things. The 401 a
                // superseded holder gets is logged by the validator under the token's HASH
                // PREFIX, which is the last segment of these paths, so a path here and that
                // warning correlate on one string. Without the paths, "superseding 1 previous
                // token(s)" cannot be tied to the 401 it caused, and the alternation #5074 reports
                // (two processes of one installation sharing a client_id and evicting each
                // other's credential) is unattributable inside the log window.
                //
                // This first line is INTENT: the candidate set, before any delete has run.
                // DeleteToken answers false for a path that was already gone (a concurrent
                // exchange got there first) and faults on a refusal (the per-path catch below
                // keeps that token live), so what was actually removed is known only after the
                // Concat completes — the second line, below, reports that. A reader tying a 401
                // to a removal uses the second line; the first says what this exchange set out
                // to do, which is what a refusal is measured against.
                logger.LogInformation(
                    "OAuth: superseding {Count} previous token(s) for user {UserId}, client label {Label} — "
                    + "candidates {SupersededPaths}, keeping {KeptPath}; "
                    + "a re-authorization replaces the client's credential rather than adding one",
                    superseded.Length, userId, label, string.Join(", ", superseded), keepPath);

                // Self-paced (Concat, never Merge): one delete at a time, the same shape the expiry
                // sweep uses, so a client that re-authorized many times drains gently. Each
                // delete's own bool is what it REMOVED (true) or found already absent (false);
                // a fault is caught per path and counts as not removed.
                return Observable.Concat(superseded.Select(path => Observable.Defer(() => abandoned.IsCancellationRequested
                        ? Observable.Return((Path: path, Removed: false, Skipped: true))
                        : tokens.DeleteToken(path)
                        .Select(removed => (Path: path, Removed: removed, Skipped: false))
                        .Catch<(string Path, bool Removed, bool Skipped), Exception>(ex =>
                        {
                            logger.LogWarning(ex,
                                "OAuth: could not supersede previous token {Path} — it stays live until "
                                + "the next authorization or its expiry", path);
                            return Observable.Return((Path: path, Removed: false, Skipped: false));
                        }))))
                    .ToList()
                    .Do(outcomes =>
                    {
                        var removed = outcomes.Where(o => o.Removed).Select(o => o.Path).ToArray();
                        logger.LogInformation(
                            "OAuth: superseded {RemovedCount} of {Count} previous token(s) for user {UserId}, "
                            + "client label {Label} — removed {RemovedPaths}; kept {KeptPath}",
                            removed.Length, outcomes.Count, userId, label,
                            removed.Length == 0 ? "(none)" : string.Join(", ", removed), keepPath);
                    })
                    .Select(outcomes => !outcomes.Any(o => o.Skipped));
            })
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "OAuth: could not list previous tokens for user {UserId} to supersede them", userId);
                return Observable.Return(!abandoned.IsCancellationRequested);
            });
    }

    /// <summary>
    /// Removes a token this exchange minted for a client that abandoned the exchange before the
    /// response could carry it (MeshWeaver.Feedback#13). Nobody holds it, so leaving it would be a
    /// year-long live credential with no holder. A refusal is logged with the path — the token then
    /// stays live and the line is what lets an operator remove it — and never faults the exchange,
    /// whose caller is already gone.
    /// </summary>
    private IObservable<(TokenCreationResult Creation, IActionResult? Abandoned)> RevokeUndeliveredToken(
        ApiTokenService tokens, TokenCreationResult creation, string clientId)
        => tokens.DeleteToken(creation.Node.Path)
            .Do(
                removed => logger.LogInformation(
                    "OAuth /token for client {ClientId}: the client abandoned the exchange while the token "
                    + "was being minted — {Outcome} the undelivered token {Path}; the client's previous "
                    + "credential was NOT superseded",
                    clientId, removed ? "revoked" : "found already absent", creation.Node.Path),
                ex => logger.LogWarning(ex,
                    "OAuth /token for client {ClientId}: could not revoke the undelivered token {Path} — "
                    + "it stays live, held by no client, until revoked or expired",
                    clientId, creation.Node.Path))
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .Select(_ => (creation, (IActionResult?)BadRequest(new { error = "invalid_request" })));

    /// <summary>
    /// The creation stamp of the token this exchange just minted — the ordering key
    /// <see cref="SupersedePreviousTokens"/> compares against. Falls back to "now" if the content
    /// is not the expected shape, which only ever makes the sweep MORE conservative on the tie
    /// (it can then supersede a token minted in the same instant, never a newer one).
    /// </summary>
    private static DateTimeOffset MintedAt(TokenCreationResult creation) =>
        creation.Node.Content is ApiToken t ? t.CreatedAt : DateTimeOffset.UtcNow;

    /// <summary>
    /// Lifetime for OAuth-issued API tokens. Single source of truth — the
    /// expiresAt timestamp on the token row and the expires_in OAuth response
    /// field both read from this so they can't drift apart.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(365);
}

/// <summary>
/// Binds the form-encoded token request body.
/// </summary>
public class TokenRequest
{
    public string grant_type { get; set; } = "";
    public string? code { get; set; }
    public string? client_id { get; set; }
    public string? redirect_uri { get; set; }
    public string? code_verifier { get; set; }
}

/// <summary>
/// RFC 7591 Dynamic Client Registration request. Fields use snake_case JSON names per the spec.
/// </summary>
public class ClientRegistrationRequest
{
    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public string[]? RedirectUris { get; set; }

    [JsonPropertyName("grant_types")]
    public string[]? GrantTypes { get; set; }

    [JsonPropertyName("response_types")]
    public string[]? ResponseTypes { get; set; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// RFC 7591 Dynamic Client Registration response.
/// </summary>
public class ClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("client_id_issued_at")]
    public long ClientIdIssuedAt { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public string[]? RedirectUris { get; set; }

    [JsonPropertyName("grant_types")]
    public string[]? GrantTypes { get; set; }

    [JsonPropertyName("response_types")]
    public string[]? ResponseTypes { get; set; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }
}
