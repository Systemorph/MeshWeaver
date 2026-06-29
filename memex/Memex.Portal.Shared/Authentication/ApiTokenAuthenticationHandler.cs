using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MeshWeaver.Messaging;
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
