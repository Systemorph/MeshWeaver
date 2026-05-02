using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Activity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// ASP.NET Core authentication handler that validates Bearer tokens
/// against the ApiTokenService. Builds a ClaimsPrincipal with claims
/// matching what UserContextMiddleware.ExtractUserContext() reads.
/// </summary>
public class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiToken";

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
        // IObservable<ApiToken?> per the "no async in hub-reachable code" rule.
        var apiToken = await tokenService.ValidateToken(rawToken).FirstAsync().ToTask();
        if (apiToken == null)
            return AuthenticateResult.Fail("Invalid or expired API token");

        var claims = BuildClaims(apiToken);
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        // Track the login event so it appears in the user's activity stream.
        // Bearer/MCP requests skip OnboardingMiddleware (which is browser-only),
        // so without this every API-token authentication was invisible — the
        // user could not tell from the audit trail when their token was used.
        // Fire-and-forget post: a failure to write the activity record must
        // never break authentication.
        TrackLogin(apiToken, serviceProvider);

        return AuthenticateResult.Success(ticket);
    }

    private static void TrackLogin(MeshWeaver.Mesh.Security.ApiToken token, IServiceProvider services)
    {
        try
        {
            var hub = services.GetService<IMessageHub>();
            if (hub is null) return;
            hub.Post(new TrackActivityRequest(
                NodePath: token.UserId,
                UserId: token.UserId,
                NodeName: token.UserName,
                NodeType: "User",
                Namespace: ""
            )
            { ActivityType = ActivityType.Login });
        }
        catch
        {
            // Activity tracking is best-effort. Auth must succeed even if the
            // hub is unavailable / disposed / mid-restart.
        }
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
