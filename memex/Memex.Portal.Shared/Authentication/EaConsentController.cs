using MeshWeaver.Messaging;   // AccessService
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Drives the Executive Assistant's <b>per-user, just-in-time</b> Microsoft consent. The EA tool hands the
/// user a link to <c>/auth/ea/connect</c> the first time it needs their mailbox/calendar; that redirects to
/// Microsoft's consent screen for the EA's <i>delegated</i> Graph scopes, and the <c>/auth/ea/callback</c>
/// exchanges the code and stores the user's encrypted refresh token. The acting user is taken from the
/// authenticated principal at both steps — the OAuth <c>state</c> only carries the return URL.
/// </summary>
[Authorize]
[Route("auth/ea")]
public sealed class EaConsentController(
    IEaGraphAuth ea, AccessService access, ILogger<EaConsentController> logger) : ControllerBase
{
    private string CallbackUri => $"{Request.Scheme}://{Request.Host}/auth/ea/callback";

    [HttpGet("connect")]
    public IActionResult Connect([FromQuery] string? returnUrl = null)
    {
        if (!ea.IsConfigured) return BadRequest("The Executive Assistant Graph integration is not configured.");
        if (string.IsNullOrEmpty(access.Context?.ObjectId)) return Unauthorized();
        return Redirect(ea.BuildConsentUrl(Uri.EscapeDataString(returnUrl ?? "/"), CallbackUri));
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        var returnUrl = string.IsNullOrEmpty(state) ? "/" : Uri.UnescapeDataString(state);
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            logger.LogWarning("EA consent callback for {User} returned error '{Error}'", userId, error);
            return Redirect(returnUrl);
        }

        var ok = await ea.ExchangeAndStoreAsync(code, CallbackUri, userId, ct);
        logger.LogInformation("EA consent for {User}: {Result}", userId, ok ? "connected" : "failed");
        return Redirect(returnUrl);
    }
}
