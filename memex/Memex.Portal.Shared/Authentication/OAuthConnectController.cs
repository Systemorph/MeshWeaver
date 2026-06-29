using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MeshWeaver.Blazor.Infrastructure; // PortalApplication
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
    /// before running the authorization flow. The mesh does not persist client registrations —
    /// it issues a random <c>client_id</c> that the caller echoes back in <c>/authorize</c> and
    /// <c>/token</c>; the code store validates client_id+redirect_uri consistency between those calls.
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

        var clientId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

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

        logger.LogInformation("Issued OAuth client_id {ClientId} for {ClientName}", clientId, request.ClientName);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// OAuth Authorization Endpoint — redirects authenticated users to the client's redirect_uri
    /// with an authorization code. Unauthenticated users are sent to /login first.
    /// </summary>
    [HttpGet("/authorize")]
    public IActionResult Authorize(
        [FromQuery] string response_type,
        [FromQuery] string client_id,
        [FromQuery] string redirect_uri,
        [FromQuery] string? state,
        [FromQuery] string? scope,
        [FromQuery] string? code_challenge,
        [FromQuery] string? code_challenge_method)
    {
        logger.LogInformation(
            "OAuth /authorize: response_type={ResponseType}, client_id={ClientId}, redirect_uri={RedirectUri}, has_state={HasState}, has_pkce={HasPkce}, authenticated={Authenticated}",
            response_type, client_id, redirect_uri,
            !string.IsNullOrEmpty(state), !string.IsNullOrEmpty(code_challenge),
            User?.Identity?.IsAuthenticated == true);

        if (response_type != "code")
        {
            logger.LogWarning("OAuth /authorize rejected: unsupported response_type={ResponseType}", response_type);
            return BadRequest(new { error = "unsupported_response_type" });
        }

        if (string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(redirect_uri))
        {
            logger.LogWarning("OAuth /authorize rejected: missing client_id or redirect_uri");
            return BadRequest(new { error = "invalid_request", error_description = "client_id and redirect_uri are required" });
        }

        // If user is not authenticated, redirect to login with return URL
        if (User?.Identity?.IsAuthenticated != true)
        {
            var authorizeUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
            var loginUrl = $"/login?returnUrl={Uri.EscapeDataString(authorizeUrl)}";
            logger.LogInformation("OAuth /authorize: redirecting unauthenticated caller to {LoginUrl}", loginUrl);
            return Redirect(loginUrl);
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
            return BadRequest(new { error = "invalid_request", error_description = "Unable to determine user identity" });
        }

        // Refuse to issue a code with an unresolved or email-shaped userId — it
        // would mint the token into a parallel {email} partition that owns none
        // of the user's data (the original atioz 401). A missing mesh identity
        // means the User node isn't provisioned yet; the user should retry after
        // a normal browser login populates the identity cache.
        if (string.IsNullOrEmpty(userId) || userId.Contains('@'))
        {
            logger.LogWarning(
                "OAuth /authorize rejected: no resolved mesh identity for {Email} (userId='{UserId}'). "
                + "Retry after a browser login provisions/loads the User node.",
                email, userId ?? "(null)");
            return BadRequest(new { error = "invalid_request", error_description = "Unable to determine user identity" });
        }

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

        var entry = CodeStore.ExchangeCode(
            request.code,
            request.client_id,
            request.redirect_uri,
            request.code_verifier);

        if (entry == null)
        {
            logger.LogWarning("OAuth token exchange failed: invalid or expired code for client {ClientId}", request.client_id);
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_grant" }));
        }

        // Create an mw_ API token via the existing token service. Lifetime
        // is long-lived because OAuth clients (MCP, CLI tools) typically
        // can't run interactive re-auth flows — a token that expires in 30
        // days surprises users who connect once and come back months later.
        // Refresh-token flow isn't implemented yet; until it is, default to
        // 1 year. Bump if needed via TokenLifetime below.
        //
        // No await: pull IObservable up to the controller's return type.
        // Single bridge to Task happens at .ToTask(ct) — passing the
        // request's cancellation token so a client disconnect tears down
        // the reactive subscription.
        return TokenService.CreateToken(
                userId: entry.UserId,
                userName: entry.UserName,
                userEmail: entry.UserEmail,
                label: $"OAuth: {request.client_id}",
                expiresAt: DateTimeOffset.UtcNow.Add(TokenLifetime))
            .Select(creation =>
            {
                logger.LogInformation("Issued OAuth access token for user {Email}, client {ClientId}", entry.UserEmail, request.client_id);
                return (IActionResult)Ok(new
                {
                    access_token = creation.RawToken,
                    token_type = "Bearer",
                    expires_in = (int)TokenLifetime.TotalSeconds,
                });
            })
            .FirstAsync()
            .ToTask(ct);
    }

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
