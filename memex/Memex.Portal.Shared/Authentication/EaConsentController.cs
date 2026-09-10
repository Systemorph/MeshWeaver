using System.Reactive.Linq;
using MeshWeaver.Mesh;        // IEaGraphAuth — the SDK-free seam, now in the mesh contract
using MeshWeaver.Messaging;   // AccessService, ObserveCompletion
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

    /// <summary>
    /// Starts (or skips) the delegated-Graph consent for the signed-in user.
    ///
    /// <para>🚨 <b>THE <c>Task</c> LIVES HERE AND NOWHERE ELSE (#3433).</b> An MVC action is
    /// genuinely Task-shaped — that signature is ASP.NET's, not ours — so this is where the ONE
    /// bridge from the reactive <see cref="IEaGraphAuth"/> seam belongs:
    /// <c>ObserveCompletion</c>, which completes with <c>RunContinuationsAsynchronously</c> so the
    /// request never resumes on the thread that signalled. What it must NOT do is push the Task
    /// shape back onto the seam, whose OTHER consumer is the Executive Assistant's agent tools —
    /// they run on a hub, where an await parks the turn that has to deliver the reply to the
    /// credential read.</para>
    ///
    /// <para>Only <see cref="EaConnection.Connected"/> skips the dialog.
    /// <see cref="EaConnection.Undetermined"/> — the read did not answer — goes THROUGH consent:
    /// re-consenting a live grant is harmless (Microsoft re-issues it), whereas skipping it on a
    /// guess would strand a user who genuinely never connected. The guess is logged, never
    /// silent.</para>
    /// </summary>
    /// <param name="returnUrl">Where to send the user afterwards; sanitised before either use.</param>
    /// <param name="force">Runs the full consent even for a connected user (scope additions, rotation).</param>
    /// <param name="ct">Cancels the wait on the connection read, not the read itself.</param>
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
        // (credential rotation, a revoked grant the stored token hides). A SCOPE ADDITION needs no
        // force: EaGraphAuth classifies a grant consented for a smaller scope set than the build's
        // as NotConnected, so this fast path is not taken for it — which is what ended the
        // 2026-09-10 loop where the read scopes had landed, the reconnect link bounced a
        // "connected" user straight back, and every Teams call kept failing on the refused refresh.
        // Sanitised BEFORE either use: the fast path redirects to it directly, and the consent
        // path round-trips it through the IdP as `state` and redirects to it on the way back —
        // so an unsanitised value is an open redirect on both routes, not just the visible one.
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        // The ONE Task bridge, at this action's own edge — see the remarks above for why it may
        // not move onto the seam.
        var connection = await ea.GetConnection(userId)
            .ObserveCompletion(
                ex => logger.LogWarning(ex,
                    "EA connect: the connection read for {User} faulted after the answer had already "
                    + "settled", userId),
                ct);
        if (!force && connection is { IsConnected: true })
            return Redirect(safeReturnUrl);
        if (!force && connection is { Connection: EaConnection.Undetermined })
            logger.LogWarning(
                "EA connect: could not determine whether {User} is already connected ({Reason}) — "
                + "running the consent flow rather than guessing", userId, connection.Diagnostic);
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

        // Same single bridge as Connect: the Task is the MVC action's, not the seam's.
        var ok = await ea.ExchangeAndStore(code, CallbackUri, userId)
            .ObserveCompletion(
                ex => logger.LogWarning(ex,
                    "EA consent callback: the code exchange for {User} faulted after the result had "
                    + "already settled", userId),
                ct);
        logger.LogInformation("EA consent for {User}: {Result}", userId, ok ? "connected" : "failed");
        return Redirect(returnUrl);
    }
}
