using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
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
    ApiTokenService tokenService)
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

        var apiToken = await tokenService.ValidateTokenAsync(rawToken);
        if (apiToken == null)
            return AuthenticateResult.Fail("Invalid or expired API token");

        // Build claims matching UserContextMiddleware.ExtractUserContext():
        //   ObjectId = preferred_username
        //   Name     = ClaimTypes.Name or "name"
        //   Email    = ClaimTypes.Email or "email"
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

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
