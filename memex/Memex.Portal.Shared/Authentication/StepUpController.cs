using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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
/// <para><b>Reactive end to end.</b> Every action is ONE <see cref="IObservable{T}"/> pipeline, and
/// the only <see cref="Task"/> is the one MVC requires, made at the action's own edge by
/// <see cref="Edge"/> (<c>ObserveCompletion</c>). Everything the pipeline needs from the request —
/// the caller's access context, the viewer's language, the provider claim — is captured at that
/// edge, because the pipeline continues downstream of store and pool emissions where the request's
/// ambient context is no longer reliable.</para>
///
/// <para>🚨 Navigating here from in-app UI needs a FULL page load: this is an MVC endpoint, a
/// client-side Blazor navigation never reaches it.</para>
/// </summary>
[Authorize]
[Route(BasePath)]
public sealed partial class StepUpController(
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

    /// <summary>The cookie carrying the handle and state of the pending step-up — never its content.</summary>
    internal const string PendingCookie = "mw_stepup";

    /// <summary>How long a started step-up may take to come back.</summary>
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private const string ProtectorPurpose = "MeshWeaver.StepUp.Pending.v1";

    /// <summary>The most targets one step-up may cover (a bulk approval of a full inbox page).</summary>
    internal const int MaxTargets = 50;

    private string CallbackUri => $"{Request.Scheme}://{Request.Host}/{BasePath}/{CallbackAction}";

    /// <summary>What the browser carries between the redirect and the proof: a handle and the state, sealed.</summary>
    internal sealed record PendingHandle(string Handle, string State);

    /// <summary>Starts a step-up for the signed-in approver.</summary>
    /// <param name="targets">The action paths (repeatable, paired with <paramref name="bindings"/>).</param>
    /// <param name="bindings">The hashes approved, one per target.</param>
    /// <param name="returnUrl">Where to come back to; sanitised to a local URL.</param>
    /// <param name="ct">Cancels the wait on the store, not the store.</param>
    /// <returns>A redirect, or a page explaining the refusal.</returns>
    [HttpGet("")]
    public Task<IActionResult> Start(
        [FromQuery(Name = "target")] string[]? targets,
        [FromQuery(Name = "binding")] string[]? bindings,
        [FromQuery] string? returnUrl,
        CancellationToken ct = default)
    {
        var t = Texts();
        var safeReturn = ReturnUrlPolicy.Sanitize(returnUrl);
        var userId = access.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return Done(Unauthorized());

        var pairs = Pair(targets, bindings);
        if (pairs is null)
            return Done(Refusal(t, safeReturn, t.L("stepUp.refused.targets")));

        var options = StepUpOptions.From(configuration);
        var provider = StepUpClaims.ProviderOf(User);
        var entra = Entra();
        var entraUsable = entra.IsConfigured && entra.TenantIsSpecific;
        var loginHint = User.FindFirst("email")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var restart = Request.Path + Request.QueryString;
        var callbackUri = CallbackUri;
        // A Microsoft account's rung never depends on portal factors — they are not even read.
        var factors = options.Enabled && StepUpLadder.MayEnrollPortalFactors(provider)
            ? ReadFactors(userId)
            : Observable.Return(new FactorRead(true, null));

        var flow = factors.SelectMany(read =>
        {
            if (!read.Answered) return Observable.Return<IActionResult>(Failure(t, safeReturn, "unavailable"));
            var rung = StepUpLadder.Decide(provider, options, entraUsable, read.Factors);
            logger.LogInformation("Step-up start for {User}: rung {Rung}, {Count} target(s)", userId, rung, pairs.Count);
            switch (rung)
            {
                case StepUpRung.NotRequired:
                    return Observable.Return<IActionResult>(Redirect(EaConsentController.WithOutcome(safeReturn, "stepUp=notRequired")));
                case StepUpRung.RefuseUnknownSession:
                    return Observable.Return<IActionResult>(Refusal(t, safeReturn, t.L("stepUp.refused.unknownSession")));
                case StepUpRung.RefuseNotConfigured:
                    return Observable.Return<IActionResult>(Refusal(t, safeReturn, t.L("stepUp.refused.notConfigured")));
                case StepUpRung.Enroll:
                    return Observable.Return<IActionResult>(Page(StepUpPages.EnrollNeeded(t,
                        EnrollPath + "?returnUrl=" + Uri.EscapeDataString(restart))));
                case StepUpRung.Entra:
                case StepUpRung.Passkey:
                case StepUpRung.Totp:
                    break;
                default:
                    return Observable.Return<IActionResult>(Refusal(t, safeReturn, t.L("stepUp.refused.provider", provider)));
            }

            // The rung is recorded ON the pending step-up: it is the only rung that may complete it.
            return BeginPending(userId, pairs, safeReturn, rung).Select(pending =>
            {
                if (pending is null) return (IActionResult)Failure(t, safeReturn, "unavailable");
                var manage = EnrollPath + "?returnUrl=" + Uri.EscapeDataString(safeReturn);
                return rung switch
                {
                    StepUpRung.Passkey => (IActionResult)Page(StepUpPages.Passkey(t, manage)),
                    StepUpRung.Totp => Page(StepUpPages.Totp(t, "/" + BasePath + "/" + TotpVerifyAction, manage)),
                    _ => Redirect(entra.AuthorizeUrl(pending.State, pending.Nonce, callbackUri, loginHint, options.EntraAuthenticationContext!)),
                };
            });
        });
        return Edge(flow, () => Failure(t, safeReturn, "unavailable"), "start", userId, ct);
    }

    /// <summary>Completes the Entra rung: redeems the code, checks the token, mints and stamps the receipt.</summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The state echoed by Entra.</param>
    /// <param name="error">An OAuth error, when Entra refused.</param>
    /// <param name="errorDescription">Entra's description (carries the AADSTS code).</param>
    /// <param name="ct">Cancels the wait, not the work.</param>
    /// <returns>A redirect back, or a page explaining the failure.</returns>
    [HttpGet(CallbackAction)]
    public Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription = null, CancellationToken ct = default)
    {
        var t = Texts();
        var caller = access.Context;
        var userId = caller?.ObjectId;
        if (caller is null || string.IsNullOrEmpty(userId)) return Done(Unauthorized());

        var options = StepUpOptions.From(configuration);
        var provider = StepUpClaims.ProviderOf(User);
        var sessionOid = User.FindFirst(StepUpClaims.Oid)?.Value;
        var sessionAccount = User.FindFirst("email")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var callbackUri = CallbackUri;
        var hub = Hub();
        var entra = Entra(hub);
        var entraUsable = entra.IsConfigured && entra.TenantIsSpecific;

        var flow = TakePending(userId, state, complete: true).SelectMany(pending =>
        {
            if (pending is null) return Observable.Return<IActionResult>(Failure(t, "/", "state"));
            // Only a step-up STARTED for Entra completes here, and only for a session whose rung is Entra now.
            if (!StepUpLadder.MayComplete(pending.Rung, StepUpRung.Entra, provider, options, entraUsable, factors: null))
            {
                logger.LogWarning("Step-up for {User}: an Entra callback for a step-up started as '{Rung}' refused", userId, pending.Rung);
                return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "wrongRung"));
            }
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                logger.LogWarning("Step-up for {User}: Entra answered '{Error}': {Description}", userId, error, errorDescription);
                return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "microsoft"));
            }
            if (hub is null) return Observable.Return<IActionResult>(Failure(t, pending.ReturnUrl, "mint"));

            return entra
                .Redeem(code, callbackUri, issuer => new EntraStepUpExpectation(
                    issuer, pending.Nonce, sessionOid, sessionAccount, options, DateTimeOffset.UtcNow))
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Step-up for {User}: the Entra exchange faulted", userId);
                    return Observable.Return(EntraStepUpCheck.Fail("exchange"));
                })
                .SelectMany(check => !check.Ok
                    ? Observable.Return((Check: check, Receipt: (StepUpReceipt?)null))
                    : MintAndStamp(hub, caller, StepUpMethod.Entra, check.AuthenticatedAt, check.Evidence, pending.Targets)
                        .Select(r => (Check: check, Receipt: (StepUpReceipt?)r)))
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Step-up for {User}: minting or stamping the receipt failed", userId);
                    return Observable.Return((Check: EntraStepUpCheck.Fail("mint"), Receipt: (StepUpReceipt?)null));
                })
                .Select(outcome =>
                {
                    if (outcome.Receipt is null)
                    {
                        logger.LogWarning("Step-up for {User} refused at '{Reason}'", userId, outcome.Check.Reason);
                        return Failure(t, pending.ReturnUrl, outcome.Check.Reason ?? "exchange");
                    }
                    return (IActionResult)Redirect(SuccessUrl(pending, outcome.Receipt));
                });
        });
        return Edge(flow, () => Failure(t, "/", "mint"), "callback", userId, ct);
    }

    /// <summary>
    /// Mints the receipt (as System, inside the service) and stamps it onto every target AS THE
    /// APPROVER. The approver is passed in, captured at the action's edge: this runs downstream of an
    /// I/O-pool emission, where the request's <c>AsyncLocal</c> access context is no longer reliable,
    /// and a stamp written with no identity is refused. A factor-enrolment target
    /// (<c>Auth/_StepUpFactors/…</c>) is never stamped — the enrolment endpoint consumes that receipt
    /// itself, from the return URL.
    /// </summary>
    private IObservable<StepUpReceipt> MintAndStamp(IMessageHub hub, AccessContext caller, string method,
        DateTimeOffset authenticatedAt, string? evidence, IReadOnlyList<StepUpTarget> targets)
    {
        var issuer = hub.ServiceProvider.GetRequiredService<StepUpService>();
        var workspace = hub.GetWorkspace();
        return issuer.Mint(caller.ObjectId, method, targets, authenticatedAt, evidence)
            .SelectMany(receipt => targets
                .Where(t => !IsEnrollmentTarget(t))
                .Select(t => t.ActionPath).Distinct(StringComparer.Ordinal)
                .Select(path => access.RunAs(caller, () => workspace.GetMeshNodeStream(path)
                    .Update(node => node with { Content = StepUpPaths.Stamp(node.Content, caller.ObjectId, receipt.Id, hub.JsonSerializerOptions) })
                    .Take(1)
                    .Select(_ => Unit.Default)))
                .Merge()
                .DefaultIfEmpty(Unit.Default)
                .LastAsync()
                .Select(_ => receipt));
    }

    /// <summary>A target that authorizes enrolling another step-up factor rather than an approval.</summary>
    internal static bool IsEnrollmentTarget(StepUpTarget target) =>
        target.ActionPath.StartsWith(StepUpPaths.FactorsNamespace + "/", StringComparison.Ordinal);

    /// <summary>
    /// Where a successful step-up returns: the page with <c>stepUp=done</c> — and, when it covered a
    /// factor enrolment, the receipt id the enrolment endpoint consumes.
    /// </summary>
    private static string SuccessUrl(StepUpPending pending, StepUpReceipt receipt) =>
        EaConsentController.WithOutcome(ReturnUrlPolicy.Sanitize(pending.ReturnUrl),
            pending.Targets.Any(IsEnrollmentTarget) ? "stepUp=done&stepUpReceipt=" + receipt.Id : "stepUp=done");

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

    /// <summary>
    /// Stores the pending step-up SERVER-side (<c>Auth/_StepUpPending/{handle}</c>, as System) —
    /// recording the <paramref name="rung"/> the server decided — and gives the browser only the
    /// sealed handle and state: a bulk approval's targets would overflow a cookie. Emits null when the
    /// store did not answer.
    /// </summary>
    private IObservable<StepUpPending?> BeginPending(string userId, IReadOnlyList<StepUpTarget> targets, string returnUrl, string rung)
    {
        var hub = Hub();
        if (hub is null) return Observable.Return<StepUpPending?>(null);
        var pending = new StepUpPending
        {
            Id = StepUpSeal.NewId(),
            State = StepUpSeal.NewId(),
            Nonce = StepUpSeal.NewId(),
            UserId = userId,
            Rung = rung,
            Targets = [.. targets],
            ReturnUrl = returnUrl,
            ExpiresAt = DateTimeOffset.UtcNow + PendingLifetime,
        };
        var sealedHandle = Protector().Protect(JsonSerializer.Serialize(new PendingHandle(pending.Id, pending.State)), pending.ExpiresAt);
        return new StepUpPendingStore(hub).Begin(pending)
            .Select(_ =>
            {
                Response.Cookies.Append(PendingCookie, sealedHandle, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    // Lax: the IdP's redirect back is a top-level GET, which Lax admits.
                    SameSite = SameSiteMode.Lax,
                    MaxAge = PendingLifetime,
                    Path = "/" + BasePath,
                });
                return (StepUpPending?)pending;
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up for {User}: the pending step-up could not be stored", userId);
                return Observable.Return<StepUpPending?>(null);
            });
    }

    /// <summary>
    /// The pending step-up named by the sealed cookie, checked to belong to this request — same user,
    /// same state (constant-time), not expired. When <paramref name="complete"/>, it is TAKEN
    /// (<see cref="StepUpPendingStore.Take"/>): claimed atomically in the store, so of two concurrent
    /// requests carrying the same cookie only one ever receives it — one ceremony, one proof. The
    /// cookie is dropped and the node deleted afterwards as tidying; neither is the guarantee.
    /// </summary>
    private IObservable<StepUpPending?> TakePending(string userId, string? state, bool complete)
    {
        var handle = ReadHandle();
        if (complete) Response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/" + BasePath });
        var hub = Hub();
        if (handle is null || hub is null || !StepUpPaths.IsWellFormedId(handle.Handle)) return Observable.Return<StepUpPending?>(null);
        if (state is not null && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(handle.State), Encoding.UTF8.GetBytes(state)))
            return Observable.Return<StepUpPending?>(null);

        bool Admits(StepUpPending p) =>
            p.UserId == userId && p.State == handle.State && DateTimeOffset.UtcNow <= p.ExpiresAt;

        var store = new StepUpPendingStore(hub);
        var read = complete
            ? store.Take(handle.Handle, Admits)
                .Do(taken =>
                {
                    if (taken is not null)
                        store.Complete(handle.Handle).Subscribe(_ => { },
                            ex => logger.LogWarning(ex, "Step-up: the taken pending step-up {Handle} could not be deleted", handle.Handle));
                })
            : store.Read(handle.Handle).Select(p => p is not null && Admits(p) ? p : null);
        return read.Catch((Exception ex) =>
        {
            logger.LogWarning(ex, "Step-up for {User}: the pending step-up could not be read or claimed", userId);
            return Observable.Return<StepUpPending?>(null);
        });
    }

    private PendingHandle? ReadHandle()
    {
        if (!Request.Cookies.TryGetValue(PendingCookie, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<PendingHandle>(Protector().Unprotect(raw));
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

    /// <summary>
    /// The action's edge — the ONE <see cref="Task"/> bridge MVC needs. A pipeline that faults or
    /// completes empty answers <paramref name="onFault"/>, so a request never ends in an unhandled
    /// exception or a blank response; a fault after it settled is logged, never dropped.
    /// </summary>
    private Task<IActionResult> Edge(IObservable<IActionResult> flow, Func<IActionResult> onFault, string action, string userId, CancellationToken ct) =>
        flow
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Step-up {Action} for {User} faulted", action, userId);
                return Observable.Return(onFault());
            })
            .DefaultIfEmpty(onFault())
            .Take(1)
            .ObserveCompletion(ex => logger.LogWarning(ex, "Step-up {Action} for {User} faulted after settling", action, userId), ct)!;

    /// <summary>An answer decided before any store or pool call — no wait at all.</summary>
    private static Task<IActionResult> Done(IActionResult result) => Task.FromResult(result);

    private ITimeLimitedDataProtector Protector() =>
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    private IMessageHub? Hub() =>
        services.GetService<PortalApplication>()?.Hub ?? services.GetService<IMessageHub>();

    private EntraStepUp Entra(IMessageHub? hub = null)
    {
        var resolved = hub ?? Hub();
        var pool = resolved?.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        var metadata = resolved?.ServiceProvider.GetService<EntraMetadataCache>() ?? new EntraMetadataCache();
        return new EntraStepUp(configuration, httpFactory.CreateClient(nameof(EntraStepUp)), pool, metadata, logger);
    }

    private ContentResult Failure(StepUpPageTexts t, string returnUrl, string reason) =>
        Refusal(t, returnUrl, t.L("stepUp.failed", t.L("stepUp.reason." + reason)));

    /// <summary>A refusal or failure — localized, with a way back (<see cref="StepUpPages.Message"/>).</summary>
    private static ContentResult Refusal(StepUpPageTexts t, string returnUrl, string message) =>
        Page(StepUpPages.Message(t, message, ReturnUrlPolicy.Sanitize(returnUrl)), StatusCodes.Status403Forbidden);

    /// <summary>One of the step-up pages as the response.</summary>
    private static ContentResult Page(string html, int status = StatusCodes.Status200OK) =>
        new() { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = status };

    /// <summary>The viewer's language, captured at the action's edge.</summary>
    private StepUpPageTexts Texts() => new(access.ViewerLocale());
}
