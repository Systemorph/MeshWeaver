using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The anonymous navigation gate's decision logic, separated from the Blazor wiring so it is
/// integration-testable against the REAL <see cref="PermissionEvaluator"/> (no mocks). The Blazor
/// <c>NavigationService</c> maps the decision to load / login.
///
/// <para>A logged-OUT visitor may load a page ONLY when it carries an explicit positive Anonymous
/// Read grant (a public course cover / catalog / landing). EVERYTHING else — including gated
/// content whose partition configures a paywall — goes to <c>/login</c> first: sign-in is always
/// the first step, and the paywall redirect for the then-AUTHENTICATED visitor is handled by the
/// area-level access-denied redirect (<c>PartitionAccessPolicy.RedirectOnDenied</c> in
/// <c>NamedAreaView</c>).</para>
///
/// <para>🚨 <b>The gate has THREE answers, not two (issue #2901).</b> "You may not" and "we could
/// not find out" are different facts and the caller must be able to tell them apart:
/// <list type="bullet">
///   <item><description><see cref="PermissionCheckOutcome.Granted"/> — serve the page / the
///     bytes.</description></item>
///   <item><description><see cref="PermissionCheckOutcome.Denied"/> — a verdict was reached and it
///     is no: redirect to <c>/login</c> (or answer 404 on a content route, so the status cannot be
///     used as an existence oracle). A durable answer the caller may act on and cache.</description></item>
///   <item><description><see cref="PermissionCheckOutcome.IsUndetermined"/> — the permission fold
///     faulted, or terminated without a verdict. <b>Serve nothing</b> (it is not a grant) and
///     <b>assert nothing</b> (it is not a denial): answer <em>temporarily unavailable</em>, i.e.
///     HTTP 503 on an API route, a retry on a navigation. Never a silent redirect to
///     <c>/login</c> — that tells a visitor who may well be entitled that the page is not for
///     them, and it hides a degraded dependency behind a routine-looking
///     bounce.</description></item>
/// </list>
/// The asymmetry is deliberate and load-bearing: undetermined must never widen anonymous access
/// (<see cref="PermissionCheckOutcome.IsGranted"/> is <c>false</c> on that leg, so a consumer that
/// ignores the tri-state still fails CLOSED), and must not narrow into a fabricated denial either.
/// </para>
/// </summary>
public static class AnonymousGate
{
    /// <summary>
    /// 🚨 THE GATE, tri-state — use this whenever the answer decides what a visitor is SHOWN or
    /// TOLD. <see cref="AllowAnonymous"/> is the lossy projection of it and cannot express
    /// "temporarily unavailable".
    ///
    /// <para>Emits <b>exactly one</b> outcome and NEVER faults — same contract as
    /// <see cref="HubPermissionExtensions.CheckPermissionOutcome(IMessageHub,string,string,Permission)"/>,
    /// which it delegates to, and for the same reason: every consumer reads "no outcome" as
    /// "nothing objected", so an empty or faulted gate stream is an ALLOW, not a refusal. It carries
    /// NO time bound on purpose (see <c>Doc/Architecture/AccessControl</c> — the message gate's
    /// contract); a caller that needs one applies its own <c>.Timeout(...)</c> and maps the timeout
    /// to the same unavailable answer as <see cref="PermissionCheckOutcome.IsUndetermined"/>.</para>
    ///
    /// <para><b>No evaluator ⇒ Denied, definitively — not undetermined.</b> When no
    /// <see cref="EffectivePermissionsDelegate"/> is registered (RLS not installed — the canonical
    /// check used across the hosting layer), the default evaluator would answer
    /// <see cref="Permission.All"/> and silently open every page to anonymous browsers, so the gate
    /// refuses. That refusal is a permanent, deterministic property of how this mesh was
    /// CONFIGURED: an ungated mesh has no way to express an anonymous grant, therefore nothing on
    /// it is anonymous-readable, and no amount of retrying changes that. Classifying it as
    /// undetermined would turn every unsecured deployment into a permanent 503 and would itself be
    /// the lie this tri-state exists to prevent ("retryable" for something that never retries
    /// clean). "Deliberately ungated" vs "somebody forgot" is a separate statement and has its own
    /// type — <see cref="UnsecuredMeshDeclaration"/>.</para>
    ///
    /// <para>🚨 Composed inside <see cref="Observable.Defer{TResult}(Func{IObservable{TResult}})"/>
    /// ON PURPOSE: reading <c>hub.Configuration</c> / <c>hub.ServiceProvider</c> THROWS on a hub
    /// whose DI scope is mid-disposal, and that synchronous throw would otherwise escape on the
    /// CALLER's stack, past every classifier. Under the <c>Defer</c> it becomes an
    /// <see cref="PermissionCheckOutcome.Undetermined"/> — honest, and fail-closed.</para>
    /// </summary>
    /// <param name="hub">The hub whose configured evaluator answers the check.</param>
    /// <param name="path">The node path a logged-out visitor is asking for.</param>
    public static IObservable<PermissionCheckOutcome> Evaluate(IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable
            .Defer(() =>
            {
                if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
                    return Observable.Return(PermissionCheckOutcome.Denied);

                // The logger is resolved HERE — inside the Defer, under the Catch below — because
                // hub.ServiceProvider throws on a disposing scope just like hub.Configuration does.
                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(AnonymousGate).FullName!);

                return hub
                    .CheckPermissionOutcome(path, WellKnownUsers.Anonymous, Permission.Read)
                    .Take(1)
                    // The fold reaching no verdict is an OPERATIONAL fact about this deployment,
                    // and it used to leave no trace at all: the swallow that stood here turned it
                    // into a bare `false` and the visitor into a /login bounce, so the only symptom
                    // a degraded permission fold produced was a support ticket. Warning, once per
                    // check, naming the path and the classifier's own reason.
                    .Do(outcome =>
                    {
                        if (outcome.IsUndetermined)
                            logger?.LogWarning(
                                "Anonymous gate on '{Path}' reached NO verdict — serving nothing and "
                                + "reporting unavailable (retryable). This is a degraded dependency, "
                                + "not a statement about the visitor's rights: {Reason}",
                                path,
                                outcome.UndeterminedReason);
                    });
            })
            .Catch<PermissionCheckOutcome, Exception>(ex => Observable.Return(
                PermissionCheckOutcome.Undetermined(
                    $"the anonymous gate on '{path}' could not be composed: "
                    + $"{ex.GetType().Name}: {ex.Message}")));
    }

    /// <summary>
    /// True when a logged-OUT visitor may load <paramref name="path"/> — an explicit positive
    /// Anonymous Read grant; false ⇒ do not serve.
    ///
    /// <para><b>Fail-closed, and LOSSY.</b> This is <see cref="Evaluate"/> projected onto
    /// <see cref="PermissionCheckOutcome.IsGranted"/>, so BOTH a definitive denial and an
    /// undetermined fold answer <c>false</c> — which is the correct direction for ACCESS (a fold
    /// that reached no verdict can never widen what an anonymous visitor sees) but is NOT a
    /// sufficient basis for telling the visitor anything.</para>
    ///
    /// <para>🚨 Keep using this only where "unknown" and "not public" lead to the SAME correct
    /// action and nothing is asserted to a human — omitting a page from the sitemap, withholding
    /// SEO metadata. The moment the answer produces a redirect, a status code or a message, switch
    /// to <see cref="Evaluate"/> and branch on
    /// <see cref="PermissionCheckOutcome.IsUndetermined"/> first.</para>
    /// </summary>
    /// <param name="hub">The hub whose configured evaluator answers the check.</param>
    /// <param name="path">The node path a logged-out visitor is asking for.</param>
    public static IObservable<bool> AllowAnonymous(IMessageHub hub, string path)
        => Evaluate(hub, path).Select(outcome => outcome.IsGranted);
}
