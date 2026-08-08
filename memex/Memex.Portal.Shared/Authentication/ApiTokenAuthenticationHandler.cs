using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// ASP.NET Core authentication handler that validates Bearer tokens
/// against the ApiTokenService. Builds a ClaimsPrincipal with claims
/// matching what UserContextMiddleware.ExtractUserContext() reads.
///
/// <para><b>Unavailable ≠ invalid (issue #637)</b>: when the token could not be
/// validated at all (storage read timeout during silo degradation), this handler
/// answers <c>503 Service Unavailable</c> + <c>Retry-After</c> instead of the 401
/// that a definitive invalid/expired token gets — a 401 here is indistinguishable
/// from a forged token and drives clients into discarding valid credentials.
/// <see cref="AuthenticateResult.Fail(string)"/> always turns into 401 via the
/// default challenge, so the unavailable outcome is flagged on
/// <see cref="HttpContext.Items"/> and <see cref="HandleChallengeAsync"/> rewrites
/// the challenge to 503 when the flag is present.</para>
/// </summary>
public class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiToken";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key flagging that authentication failed because token
    /// validation was UNAVAILABLE (retryable), not because the token was invalid. Set by
    /// <see cref="HandleAuthenticateAsync"/>, consumed by <see cref="HandleChallengeAsync"/>
    /// to answer 503 + Retry-After instead of the default 401.
    /// </summary>
    internal const string ValidationUnavailableItemKey = "Memex.ApiToken.ValidationUnavailable";

    /// <summary>Retry-After (seconds) advertised on the 503 unavailable challenge.</summary>
    internal const int RetryAfterSeconds = 5;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken))
            return AuthenticateResult.NoResult();

        var tokenService = serviceProvider.GetRequiredService<ApiTokenService>();
        // HTTP boundary — bridge IObservable to Task once. The service exposes
        // IObservable<TokenValidationResult> per the "no async in hub-reachable code" rule.
        var validationStopwatch = Stopwatch.StartNew();
        // 🚨 Bridge under the REQUEST's abort token: a client that gives up mid-validation must
        // release the wait at once rather than holding it to the validation read's own timeout.
        // That matters most in exactly the degraded window this change is about — storage slow,
        // clients retrying — where holding every abandoned request would compound the pressure.
        var validation = await tokenService.Validate(rawToken)
            .FirstAsync().ToTask(Context.RequestAborted);
        if (validation.Status == TokenValidationStatus.Unavailable)
        {
            // Token validation UNAVAILABLE — retry; NOT an auth failure. The token was
            // neither confirmed nor rejected, so the client must keep it and retry.
            Logger.LogWarning(
                "API token validation UNAVAILABLE for hash prefix {HashPrefix} after {ElapsedMs} ms ({Reason}) — retryable infrastructure fault, NOT an auth failure; answering 503 + Retry-After instead of 401",
                ApiTokenService.HashToken(rawToken)[..12], validationStopwatch.ElapsedMilliseconds,
                validation.Reason);
            Context.Items[ValidationUnavailableItemKey] = validation.Reason ?? "token validation unavailable";
            return AuthenticateResult.Fail("API token validation is temporarily unavailable (retryable)");
        }
        if (validation.Status == TokenValidationStatus.Invalid)
        {
            // 🚨 Never a silent 401: every rejected Bearer token gets a Warning naming
            // the hash prefix (never the raw token) and how long validation took —
            // ApiTokenService has already logged the exact failing stage
            // (index/token not-found, hash mismatch, revoked, expired) under the
            // same prefix. The silent Fail is what made the 2026-07-24 fresh-token
            // 401 storm untraceable server-side.
            Logger.LogWarning(
                "API token authentication failed for hash prefix {HashPrefix} after {ElapsedMs} ms — see the ApiTokenService warning with the same prefix for the failing stage",
                ApiTokenService.HashToken(rawToken)[..12], validationStopwatch.ElapsedMilliseconds);
            return AuthenticateResult.Fail("Invalid or expired API token");
        }

        var apiToken = validation.Token!;
        var claims = BuildClaims(apiToken).ToList();

        // Enrich with DB-resolved AccessAssignment roles so Bearer requests
        // see the same role set as cookie/OAuth sessions. Without this, the
        // principal only carries roles that were stamped on the API token at
        // creation time — any AccessAssignment granted to the user later
        // (e.g. an admin promotion after the token was minted) would silently
        // not apply for MCP requests, even though the same user logging in
        // through the browser would see them. Live mesh query, bounded so a
        // wedged data source can't slow auth.
        try
        {
            var dbRoles = await UserRoleResolver.LoadDbRolesAsync(serviceProvider, apiToken.UserId);
            foreach (var role in dbRoles)
            {
                if (string.IsNullOrEmpty(role)) continue;
                if (claims.Any(c => c.Type == ClaimTypes.Role && c.Value == role))
                    continue;
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }
        catch
        {
            // Role enrichment is best-effort; the token's own Roles still apply.
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        // Login tracking lives in UserContextMiddleware so it fires for both
        // Bearer and cookie authentication on the same code path — see
        // UserContextMiddleware.TrackLogin.
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Challenge override for the unavailable outcome. The base challenge answers 401 for
    /// EVERY failed authentication — correct for a definitive invalid/expired token, wrong
    /// for "validation could not run" (issue #637). When
    /// <see cref="HandleAuthenticateAsync"/> flagged the request as validation-unavailable,
    /// the challenge becomes <c>503 Service Unavailable</c> with a <c>Retry-After</c>
    /// header and a speaking body so API clients retry with the SAME token instead of
    /// discarding it and re-authenticating.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(ValidationUnavailableItemKey))
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            Response.ContentType = "text/plain; charset=utf-8";
            // Machine-facing API error text (wire surface, deliberately not localized —
            // same as the 401 challenge body conventions).
            await Response.WriteAsync(
                "API token validation is temporarily unavailable — retry shortly. "
                + "The token was NOT rejected; keep it and retry, do not re-authenticate.");
            return;
        }

        await base.HandleChallengeAsync(properties);
    }

    /// <summary>
    /// Builds the claim list for an authenticated API token. Public + static
    /// so unit tests can assert the claim shape (in particular: that
    /// <see cref="MeshWeaver.Mesh.Security.ApiToken.Roles"/> become
    /// <see cref="ClaimTypes.Role"/> claims) without needing an HTTP host.
    /// Mirrors what <c>UserContextMiddleware.ExtractUserContext()</c> reads
    /// back into <see cref="MeshWeaver.Messaging.AccessContext"/>.
    /// </summary>
    public static IReadOnlyList<Claim> BuildClaims(MeshWeaver.Mesh.Security.ApiToken apiToken)
    {
        // Build claims matching UserContextMiddleware.ExtractUserContext():
        //   ObjectId = preferred_username
        //   Name     = ClaimTypes.Name or "name"
        //   Email    = ClaimTypes.Email or "email"
        //   Roles    = each ClaimTypes.Role claim → AccessContext.Roles
        var claims = new List<Claim>
        {
            new("preferred_username", apiToken.UserId),
            new(ClaimTypes.Name, apiToken.UserName),
            new("name", apiToken.UserName),
            new(ClaimTypes.Email, apiToken.UserEmail),
            new("email", apiToken.UserEmail),
            new(ClaimTypes.NameIdentifier, apiToken.UserId),
            new("token_label", apiToken.Label),
        };

        // Stamp the token's Roles list as ClaimTypes.Role claims. Without
        // this, UserContextMiddleware sets AccessContext.Roles to an empty
        // list and SecurityService.GetEffectivePermissions can't resolve
        // claim-based Admin — every API-token request that depended on a
        // role grant rather than a static AccessAssignment got denied.
        // The token's Roles surface is exactly the right vehicle: the
        // creator chose them at token creation; the validator preserves
        // them through ValidateTokenResponse.Roles; the auth handler
        // mints them onto the principal here.
        foreach (var role in apiToken.Roles)
        {
            if (!string.IsNullOrEmpty(role))
                claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return claims;
    }
}
