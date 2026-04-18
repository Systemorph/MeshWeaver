using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Minimal OAuth 2.0 authorization server for MCP clients (claude.ai Connectors, Claude Desktop).
/// Implements authorization code flow with PKCE. Issues mw_ API tokens as access tokens,
/// reusing the existing ApiTokenService infrastructure.
/// </summary>
[ApiController]
public class OAuthConnectController(
    IServiceProvider serviceProvider,
    ILogger<OAuthConnectController> logger) : ControllerBase
{
    private OAuthCodeStore CodeStore => serviceProvider.GetRequiredService<OAuthCodeStore>();
    private ApiTokenService TokenService => serviceProvider.GetRequiredService<ApiTokenService>();

    /// <summary>
    /// RFC 8414 — OAuth Authorization Server Metadata.
    /// MCP clients discover this via the authorization_servers URL from the protected resource metadata.
    /// </summary>
    [HttpGet("/.well-known/oauth-authorization-server")]
    [AllowAnonymous]
    public IActionResult GetServerMetadata()
    {
        var origin = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            issuer = $"{origin}/connect",
            authorization_endpoint = $"{origin}/connect/authorize",
            token_endpoint = $"{origin}/connect/token",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
        });
    }

    /// <summary>
    /// OAuth Authorization Endpoint — redirects authenticated users to the client's redirect_uri
    /// with an authorization code. Unauthenticated users are sent to /login first.
    /// </summary>
    [HttpGet("connect/authorize")]
    public IActionResult Authorize(
        [FromQuery] string response_type,
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string? state,
        [FromQuery] string? scope,
        [FromQuery] string? code_challenge,
        [FromQuery] string? code_challenge_method)
    {
        if (response_type != "code")
            return BadRequest(new { error = "unsupported_response_type" });

        if (string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(redirect_uri))
            return BadRequest(new { error = "invalid_request", error_description = "client_id and redirect_uri are required" });

        // If user is not authenticated, redirect to login with return URL
        if (User?.Identity?.IsAuthenticated != true)
        {
            var authorizeUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
            var loginUrl = $"/login?returnUrl={Uri.EscapeDataString(authorizeUrl)}";
            return Redirect(loginUrl);
        }

        // Extract user identity from cookie claims
        var email = User.FindFirstValue(ClaimTypes.Email)
                    ?? User.FindFirstValue("email")
                    ?? User.FindFirstValue("preferred_username")
                    ?? "";
        var name = User.FindFirstValue(ClaimTypes.Name)
                   ?? User.FindFirstValue("name")
                   ?? email;
        var userId = User.FindFirstValue("preferred_username")
                     ?? email;

        if (string.IsNullOrEmpty(email))
            return BadRequest(new { error = "invalid_request", error_description = "Unable to determine user identity" });

        // Generate authorization code
        var code = CodeStore.GenerateCode(
            userId: userId,
            userName: name,
            userEmail: email,
            clientId: client_id,
            redirectUri: redirect_uri,
            codeChallenge: code_challenge,
            codeChallengeMethod: code_challenge_method);

        logger.LogInformation("Issued OAuth authorization code for user {Email}, client {ClientId}", email, client_id);

        // Redirect to client with code (and state if provided)
        var callbackUrl = string.IsNullOrEmpty(state)
            ? $"{redirect_uri}?code={Uri.EscapeDataString(code)}"
            : $"{redirect_uri}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";

        return Redirect(callbackUrl);
    }

    /// <summary>
    /// OAuth Token Endpoint — exchanges an authorization code for an API token.
    /// The issued token is a standard mw_ API token, indistinguishable from manually created ones.
    /// </summary>
    [HttpPost("connect/token")]
    [AllowAnonymous]
    public async Task<IActionResult> ExchangeToken([FromForm] TokenRequest request)
    {
        if (request.grant_type != "authorization_code")
            return BadRequest(new { error = "unsupported_grant_type" });

        if (string.IsNullOrEmpty(request.code) || string.IsNullOrEmpty(request.client_id) || string.IsNullOrEmpty(request.redirect_uri))
            return BadRequest(new { error = "invalid_request" });

        var entry = CodeStore.ExchangeCode(
            request.code,
            request.client_id,
            request.redirect_uri,
            request.code_verifier);

        if (entry == null)
        {
            logger.LogWarning("OAuth token exchange failed: invalid or expired code for client {ClientId}", request.client_id);
            return BadRequest(new { error = "invalid_grant" });
        }

        // Create an mw_ API token via the existing token service
        var (rawToken, _) = await TokenService.CreateTokenAsync(
            userId: entry.UserId,
            userName: entry.UserName,
            userEmail: entry.UserEmail,
            label: $"OAuth: {request.client_id}",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        logger.LogInformation("Issued OAuth access token for user {Email}, client {ClientId}", entry.UserEmail, request.client_id);

        return Ok(new
        {
            access_token = rawToken,
            token_type = "Bearer",
            expires_in = (int)TimeSpan.FromDays(30).TotalSeconds,
        });
    }
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
