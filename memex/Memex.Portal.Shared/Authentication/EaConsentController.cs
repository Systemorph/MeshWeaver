using MeshWeaver.Mesh;        // IEaGraphAuth — the SDK-free seam, now in the mesh contract
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
[Route(BasePath)]
public sealed class EaConsentController(
    IEaGraphAuth ea, AccessService access, ILogger<EaConsentController> logger) : ControllerBase
{
    /// <summary>Route prefix for this controller. Used by the <see cref="RouteAttribute"/> itself.</summary>
    public const string BasePath = "auth/ea";

    /// <summary>Action segment that starts the consent flow.</summary>
    public const string ConnectAction = "connect";

    /// <summary>Action segment Microsoft redirects back to with the authorization code.</summary>
    public const string CallbackAction = "callback";

    /// <summary>
    /// The absolute path that starts the delegated-Graph consent flow: <c>/auth/ea/connect</c>.
    ///
    /// <para>This constant is the SINGLE definition of that route — the <see cref="RouteAttribute"/>
    /// and <see cref="HttpGetAttribute"/> above are built from the same parts, so the endpoint and
    /// everything that links to it cannot drift. Every consumer (the send-document dialog via
    /// <see cref="MeshWeaver.Mesh.IEmailSender.ConnectAsUserHref"/>, the Executive Assistant's
    /// chat consent link) reads it from here rather than re-typing the path; a rename now breaks
    /// compilation instead of shipping a button that 404s.</para>
    ///
    /// <para>🚨 Navigating here from in-app UI REQUIRES a full browser load
    /// (<c>ctx.NavigateTo(url, forceLoad: true)</c>). This is a server-side MVC endpoint, so a
    /// client-side Blazor navigation never reaches it — the router matches its own catch-all page
    /// and reports "does not match any registered address pattern".</para>
    /// </summary>
    public const string ConnectPath = "/" + BasePath + "/" + ConnectAction;

    /// <summary>Query-string parameter carrying where to return after consent.</summary>
    public const string ReturnUrlParameter = "returnUrl";

    private string CallbackUri => $"{Request.Scheme}://{Request.Host}/{BasePath}/{CallbackAction}";

    /// <summary>
    /// Only ever redirect to a local path. Delegates to <see cref="ReturnUrlPolicy.Sanitize"/>.
    ///
    /// <para>🚨 This was a correct hand-written copy of the rule — it already rejected both
    /// <c>//host</c> and <c>/\host</c>. It is delegated anyway, because being right was not the
    /// problem: this assembly had FIVE implementations of one rule and only two were right
    /// (#2302). Correct duplicates are what make an incorrect one look normal.</para>
    /// </summary>
    internal static string SafeReturnUrl(string? candidate) => ReturnUrlPolicy.Sanitize(candidate);

    [HttpGet(ConnectAction)]
    public async Task<IActionResult> Connect(
        [FromQuery] string? returnUrl = null, [FromQuery] bool force = false, CancellationToken ct = default)
    {
        if (!ea.IsConfigured) return BadRequest("The Executive Assistant Graph integration is not configured.");
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        // Already connected → nothing to consent: bounce straight back to the caller instead of
        // forcing Microsoft's dialog (BuildConsentUrl carries prompt=consent) on a user whose
        // grant is stored — visiting the connect link twice used to re-prompt every time and read
        // as "my consent is not saved". ?force=true still runs the full consent deliberately
        // (scope additions, credential rotation, a revoked grant the stored token hides).
        // Sanitised BEFORE either use: the fast path redirects to it directly, and the consent
        // path round-trips it through the IdP as `state` and redirects to it on the way back —
        // so an unsanitised value is an open redirect on both routes, not just the visible one.
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        if (!force && await ea.IsConnectedAsync(userId, ct))
            return Redirect(safeReturnUrl);
        return Redirect(ea.BuildConsentUrl(Uri.EscapeDataString(safeReturnUrl), CallbackUri));
    }

    [HttpGet(CallbackAction)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        // `state` is whatever came back from the IdP — treated as untrusted input, exactly like
        // the query parameter it originated from. Re-sanitised here rather than trusted because
        // it left our process in between.
        var returnUrl = SafeReturnUrl(string.IsNullOrEmpty(state) ? null : Uri.UnescapeDataString(state));
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
