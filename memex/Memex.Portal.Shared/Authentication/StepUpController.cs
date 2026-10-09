using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The approval step-up endpoints (<c>Doc/Architecture/ApprovalStepUp</c>). A page that needs an
/// approval confirmed sends the approver to <see cref="StepUpPaths.StartUrl"/>; this controller
/// decides the ladder rung, runs it, mints ONE receipt covering every requested target, stamps the
/// receipt id onto each target (as the approver, through the node stream — which is what makes the
/// target's watcher re-evaluate), and returns to the page. The server-side consumer — never this
/// controller, never the page — decides whether the approval then counts.
///
/// <para>🚨 Navigating here from in-app UI needs a FULL page load: this is an MVC endpoint, a
/// client-side Blazor navigation never reaches it.</para>
/// </summary>
[Authorize]
[Route(BasePath)]
public sealed class StepUpController(
    IConfiguration configuration,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection,
    AccessService access,
    IServiceProvider services,
    ILogger<StepUpController> logger) : ControllerBase
{
    /// <summary>Route prefix — the same value as <see cref="StepUpPaths.StartRoute"/> without its leading slash.</summary>
    public const string BasePath = "auth/step-up";

    /// <summary>The Entra callback segment.</summary>
    public const string CallbackAction = "callback";

    /// <summary>The cookie carrying the sealed pending step-up between the redirect and the callback.</summary>
    internal const string PendingCookie = "mw_stepup";

    /// <summary>How long a started step-up may take to come back.</summary>
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private const string ProtectorPurpose = "MeshWeaver.StepUp.Pending.v1";

    /// <summary>The most targets one step-up may cover (a bulk approval of a full inbox page).</summary>
    internal const int MaxTargets = 50;

    private string CallbackUri => $"{Request.Scheme}://{Request.Host}/{BasePath}/{CallbackAction}";

    /// <summary>A started step-up, sealed into <see cref="PendingCookie"/>.</summary>
    internal sealed record Pending(string State, string Nonce, string UserId, IReadOnlyList<StepUpTarget> Targets, string ReturnUrl);

    /// <summary>Starts a step-up for the signed-in approver.</summary>
    /// <param name="targets">The action paths (repeatable, paired with <paramref name="bindings"/>).</param>
    /// <param name="bindings">The hashes approved, one per target.</param>
    /// <param name="returnUrl">Where to come back to; sanitised to a local URL.</param>
    /// <returns>A redirect, or a page explaining the refusal.</returns>
    [HttpGet("")]
    public IActionResult Start(
        [FromQuery(Name = "target")] string[]? targets,
        [FromQuery(Name = "binding")] string[]? bindings,
        [FromQuery] string? returnUrl)
    {
        var safeReturn = ReturnUrlPolicy.Sanitize(returnUrl);
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var pairs = Pair(targets, bindings);
        if (pairs is null)
            return Refusal(safeReturn, access.Localize("stepUp.refused.targets"));

        var options = StepUpOptions.From(configuration);
        var entra = Entra();
        var rung = StepUpLadder.Decide(StepUpClaims.ProviderOf(User), options, entra.IsConfigured && entra.TenantIsSpecific);
        logger.LogInformation("Step-up start for {User}: rung {Rung}, {Count} target(s)", userId, rung, pairs.Count);

        switch (rung)
        {
            case StepUpRung.NotRequired:
                return Redirect(EaConsentController.WithOutcome(safeReturn, "stepUp=notRequired"));
            case StepUpRung.RefuseUnknownSession:
                return Refusal(safeReturn, access.Localize("stepUp.refused.unknownSession"));
            case StepUpRung.RefuseNotConfigured:
                return Refusal(safeReturn, access.Localize("stepUp.refused.notConfigured"));
            case StepUpRung.Entra:
                break;
            default:
                return Refusal(safeReturn, access.Localize("stepUp.refused.provider", StepUpClaims.ProviderOf(User)));
        }

        var pending = new Pending(StepUpSeal.NewId(), StepUpSeal.NewId(), userId, pairs, safeReturn);
        Response.Cookies.Append(PendingCookie, Protector().Protect(JsonSerializer.Serialize(pending), DateTimeOffset.UtcNow + PendingLifetime),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                // Lax: the IdP's redirect back is a top-level GET, which Lax admits.
                SameSite = SameSiteMode.Lax,
                MaxAge = PendingLifetime,
                Path = "/" + BasePath,
            });
        var loginHint = User.FindFirst("email")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        return Redirect(entra.AuthorizeUrl(pending.State, pending.Nonce, CallbackUri, loginHint, options.EntraAuthenticationContext!));
    }

    /// <summary>Completes the Entra rung: redeems the code, checks the token, mints and stamps the receipt.</summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The state echoed by Entra.</param>
    /// <param name="error">An OAuth error, when Entra refused.</param>
    /// <param name="errorDescription">Entra's description (carries the AADSTS code).</param>
    /// <param name="ct">Cancels the wait, not the work.</param>
    /// <returns>A redirect back, or a page explaining the failure.</returns>
    [HttpGet(CallbackAction)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription = null, CancellationToken ct = default)
    {
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var pending = ReadPending();
        Response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/" + BasePath });
        if (pending is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(pending.State), Encoding.UTF8.GetBytes(state ?? "")) || pending.UserId != userId)
            return Failure(pending?.ReturnUrl ?? "/", "state");

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            logger.LogWarning("Step-up for {User}: Entra answered '{Error}': {Description}", userId, error, errorDescription);
            return Failure(pending.ReturnUrl, "microsoft");
        }

        var hub = Hub();
        if (hub is null) return Failure(pending.ReturnUrl, "mint");
        var options = StepUpOptions.From(configuration);
        var sessionOid = User.FindFirst(StepUpClaims.Oid)?.Value;
        var sessionAccount = User.FindFirst("email")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        // The ONE Task bridge, at the MVC action's own edge.
        var outcome = await Entra(hub)
            .Redeem(code, CallbackUri, issuer => new EntraStepUpExpectation(
                issuer, pending.Nonce, sessionOid, sessionAccount, options, DateTimeOffset.UtcNow))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up for {User}: the Entra exchange faulted", userId);
                return Observable.Return(EntraStepUpCheck.Fail("exchange"));
            })
            .SelectMany(check => !check.Ok
                ? Observable.Return((Check: check, Receipt: (StepUpReceipt?)null))
                : MintAndStamp(hub, userId, check, pending.Targets).Select(r => (Check: check, Receipt: (StepUpReceipt?)r)))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up for {User}: minting or stamping the receipt failed", userId);
                return Observable.Return((Check: EntraStepUpCheck.Fail("mint"), Receipt: (StepUpReceipt?)null));
            })
            .ObserveCompletion(ex => logger.LogWarning(ex, "Step-up callback for {User} faulted after settling", userId), ct);

        if (outcome.Receipt is null)
        {
            logger.LogWarning("Step-up for {User} refused at '{Reason}'", userId, outcome.Check.Reason);
            return Failure(pending.ReturnUrl, outcome.Check.Reason ?? "exchange");
        }
        return Redirect(EaConsentController.WithOutcome(ReturnUrlPolicy.Sanitize(pending.ReturnUrl), "stepUp=done"));
    }

    /// <summary>Mints the receipt (as System, inside the service) and stamps it onto every target as the approver.</summary>
    private IObservable<StepUpReceipt> MintAndStamp(IMessageHub hub, string userId, EntraStepUpCheck check, IReadOnlyList<StepUpTarget> targets)
    {
        var stepUp = hub.ServiceProvider.GetRequiredService<IStepUpService>();
        var workspace = hub.GetWorkspace();
        return stepUp.Mint(userId, StepUpMethod.Entra, targets, check.AuthenticatedAt, check.Evidence)
            .SelectMany(receipt => targets
                .Select(t => t.ActionPath).Distinct(StringComparer.Ordinal)
                .Select(path => workspace.GetMeshNodeStream(path)
                    .Update(node => node with { Content = StepUpPaths.Stamp(node.Content, userId, receipt.Id, hub.JsonSerializerOptions) })
                    .Take(1)
                    .Select(_ => Unit.Default))
                .Merge()
                .DefaultIfEmpty(Unit.Default)
                .LastAsync()
                .Select(_ => receipt));
    }

    /// <summary>Pairs the repeated <c>target</c>/<c>binding</c> parameters; null when they do not pair.</summary>
    internal static IReadOnlyList<StepUpTarget>? Pair(string[]? targets, string[]? bindings)
    {
        if (targets is null || bindings is null || targets.Length == 0 || targets.Length != bindings.Length || targets.Length > MaxTargets)
            return null;
        var list = new List<StepUpTarget>(targets.Length);
        for (var i = 0; i < targets.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(targets[i]) || string.IsNullOrWhiteSpace(bindings[i]))
                return null;
            list.Add(new StepUpTarget { ActionPath = targets[i].Trim().TrimStart('/'), Binding = bindings[i].Trim() });
        }
        return list;
    }

    private Pending? ReadPending()
    {
        if (!Request.Cookies.TryGetValue(PendingCookie, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<Pending>(Protector().Unprotect(raw));
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Step-up: the pending cookie did not unprotect (expired or foreign)");
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Step-up: the pending cookie did not parse");
            return null;
        }
    }

    private ITimeLimitedDataProtector Protector() =>
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    private IMessageHub? Hub() =>
        services.GetService<PortalApplication>()?.Hub ?? services.GetService<IMessageHub>();

    private EntraStepUp Entra(IMessageHub? hub = null)
    {
        var resolved = hub ?? Hub();
        var pool = resolved?.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        return new EntraStepUp(configuration, httpFactory.CreateClient(nameof(EntraStepUp)), pool, logger);
    }

    private ContentResult Failure(string returnUrl, string reason) =>
        Refusal(returnUrl, access.Localize("stepUp.failed", access.Localize("stepUp.reason." + reason)));

    /// <summary>
    /// The one page this flow renders itself: a refusal or failure, localized, with a way back. It is
    /// outside the Blazor shell by necessity (the IdP round trip is a full-page navigation), so it is
    /// deliberately minimal — a heading, one sentence, one link.
    /// </summary>
    private ContentResult Refusal(string returnUrl, string message)
    {
        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"").Append(WebUtility.HtmlEncode(access.ViewerLocale())).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(WebUtility.HtmlEncode(access.Localize("stepUp.title"))).Append("</title></head>")
            .Append("<body style=\"font-family:system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem\">")
            .Append("<h1>").Append(WebUtility.HtmlEncode(access.Localize("stepUp.title"))).Append("</h1>")
            .Append("<p>").Append(WebUtility.HtmlEncode(message)).Append("</p>")
            .Append("<p><a href=\"").Append(WebUtility.HtmlEncode(ReturnUrlPolicy.Sanitize(returnUrl))).Append("\">")
            .Append(WebUtility.HtmlEncode(access.Localize("stepUp.back"))).Append("</a></p>")
            .Append("</body></html>");
        return new ContentResult { Content = html.ToString(), ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status403Forbidden };
    }
}
