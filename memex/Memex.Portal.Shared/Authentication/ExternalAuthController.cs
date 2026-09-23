using System.Security.Claims;
using MeshWeaver.Hosting.AspNetCore.Portal.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalAuthOptions = MeshWeaver.Hosting.AspNetCore.Portal.Authentication.AuthenticationOptions;

namespace Memex.Portal.Shared.Authentication;

[ApiController]
[Route("auth")]
public class ExternalAuthController : ControllerBase
{
    private readonly PortalAuthOptions _authOptions;
    private readonly ILogger<ExternalAuthController> _logger;

    public ExternalAuthController(IOptions<PortalAuthOptions> authOptions, ILogger<ExternalAuthController> logger)
    {
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Initiates OAuth challenge for the specified provider.
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string provider, [FromQuery] string? returnUrl)
    {
        var config = _authOptions.Providers.FirstOrDefault(
            p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));

        if (config == null)
            return BadRequest($"Unknown provider: {provider}");

        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Callback), new { provider }) +
                          (returnUrl != null ? $"?returnUrl={Uri.EscapeDataString(returnUrl)}" : ""),
            Items = { ["provider"] = provider }
        };

        return Challenge(properties, provider);
    }

    /// <summary>
    /// Handles the OAuth callback, normalizes claims, and issues a cookie.
    /// </summary>
    [HttpGet("callback/{provider}")]
    public async Task<IActionResult> Callback(string provider, [FromQuery] string? returnUrl)
    {
        // The OIDC middleware already processed the auth code at CallbackPath (/signin-microsoft)
        // and signed in via the cookie scheme. Read the authenticated user from cookies.
        var result = await HttpContext.AuthenticateAsync();
        if (!result.Succeeded || result.Principal == null)
        {
            // 🚨 Named, never silent. This is the SECOND producer of `/login?error=auth_failed` (the
            // first is the OIDC handler's remote-failure hook, which logs on its own): the provider
            // answered, the handler signed the cookie in at /signin-{provider}, and this very next
            // request cannot read it back — a cookie the browser did not return, a Data Protection
            // key ring this replica does not share, a sign-in that never happened on this host. A
            // person reporting "auth_failed" used to leave no trace here at all: a 12-hour log
            // query over one such report returned zero lines.
            _logger.LogWarning(
                "{Provider} sign-in reached /auth/callback on {Host} without an authenticated "
                + "principal — {Failure}; cookies on the request: {CookieCount}; None: {None}",
                provider, Request.Host.Value,
                result.Failure?.Message ?? (result.None ? "no authentication ticket at all" : "ticket rejected"),
                Request.Cookies.Count, result.None);
            return Redirect("/login?error=auth_failed");
        }

        var externalClaims = result.Principal.Claims.ToList();
        var (objectId, name, email) = NormalizeClaims(provider, externalClaims);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, objectId),
            new(ClaimTypes.Name, name),
            new("name", name),
            new("preferred_username", objectId),
        };

        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
            claims.Add(new Claim("email", email));
        }

        // Preserve any role claims
        foreach (var roleClaim in externalClaims.Where(c => c.Type == ClaimTypes.Role))
            claims.Add(roleClaim);

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14)
            });

        return Redirect(ReturnUrlPolicy.Sanitize(returnUrl));
    }

    /// <summary>
    /// Signs out the current user.
    /// </summary>
    [HttpPost("logout")]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? returnUrl)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect(ReturnUrlPolicy.Sanitize(returnUrl));
    }

    /// <summary>
    /// Normalizes claims from external providers.
    /// ObjectId is always the primary email address (UPN).
    /// </summary>
    private static (string objectId, string name, string email) NormalizeClaims(
        string provider, List<Claim> claims)
    {
        var name = claims.FirstOrDefault(c => c.Type == "name")?.Value
                   ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value
                   ?? "Unknown";
        var email = claims.FirstOrDefault(c => c.Type == "email")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                    ?? claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                    ?? "";

        // ObjectId = email address, always
        var objectId = email;

        if (string.IsNullOrEmpty(objectId))
        {
            // Fallback to sub/NameIdentifier only if no email available
            objectId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                       ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                       ?? "unknown";
        }

        return (objectId, name, email);
    }
}
