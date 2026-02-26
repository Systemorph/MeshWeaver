using System.Security.Claims;
using MeshWeaver.Blazor.Portal.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortalAuthOptions = MeshWeaver.Blazor.Portal.Authentication.AuthenticationOptions;

namespace Memex.Portal.Shared.Authentication;

[ApiController]
[Route("auth")]
public class ExternalAuthController : ControllerBase
{
    private readonly PortalAuthOptions _authOptions;

    public ExternalAuthController(IOptions<PortalAuthOptions> authOptions)
    {
        _authOptions = authOptions.Value;
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
        var result = await HttpContext.AuthenticateAsync(provider);
        if (!result.Succeeded || result.Principal == null)
            return Redirect("/login?error=auth_failed");

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

        return Redirect(returnUrl ?? "/");
    }

    /// <summary>
    /// Signs out the current user.
    /// </summary>
    [HttpPost("logout")]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? returnUrl)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect(returnUrl ?? "/");
    }

    private static (string objectId, string name, string email) NormalizeClaims(
        string provider, List<Claim> claims)
    {
        string objectId;
        var name = claims.FirstOrDefault(c => c.Type == "name")?.Value
                   ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value
                   ?? "Unknown";
        var email = claims.FirstOrDefault(c => c.Type == "email")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                    ?? "";

        switch (provider.ToLowerInvariant())
        {
            case "microsoft":
                objectId = claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                           ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                           ?? email;
                break;

            case "google":
                var googleSub = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                                ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                                ?? "";
                objectId = $"google_{googleSub}";
                break;

            case "linkedin":
                var linkedInSub = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                                  ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                                  ?? "";
                objectId = $"linkedin_{linkedInSub}";
                break;

            case "apple":
                var appleSub = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                               ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                               ?? "";
                objectId = $"apple_{appleSub}";
                break;

            default:
                objectId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                           ?? email;
                break;
        }

        return (objectId, name, email);
    }
}
