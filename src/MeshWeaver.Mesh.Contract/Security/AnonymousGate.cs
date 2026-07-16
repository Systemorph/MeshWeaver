using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Where a logged-OUT visitor goes for a resolved mesh path. Exactly one of three outcomes:
/// <list type="bullet">
/// <item><see cref="Allow"/> — the node carries an explicit positive Anonymous Read grant
///   (a public cover / catalog / landing): load it.</item>
/// <item><see cref="RedirectTo"/> set — the node is NOT anonymous-readable but the nearest scope's
///   <see cref="PartitionAccessPolicy.RedirectOnDenied"/> names a public page (the paywall):
///   navigate there instead of a dead-end.</item>
/// <item>Neither — redirect to <c>/login</c> (the pre-existing hard-gate behavior).</item>
/// </list>
/// </summary>
public sealed record AnonymousGateDecision
{
    /// <summary>The visitor may load the page (explicit Anonymous Read grant).</summary>
    public bool Allow { get; init; }

    /// <summary>Paywall path to navigate to instead (normalized, no leading '/'); null = /login.</summary>
    public string? RedirectTo { get; init; }

    /// <summary>The fail-closed default: not readable, no paywall — /login.</summary>
    public static readonly AnonymousGateDecision Login = new();
}

/// <summary>
/// The anonymous navigation gate's decision logic, separated from the Blazor wiring so it is
/// integration-testable against the REAL <see cref="PermissionEvaluator"/> (no mocks). The Blazor
/// <c>NavigationService</c> maps the decision to load / navigate / login.
/// </summary>
public static class AnonymousGate
{
    /// <summary>
    /// Decide what a logged-OUT visitor may do with <paramref name="path"/>.
    ///
    /// <para><b>Fail-closed.</b> When no <see cref="EffectivePermissionsDelegate"/> is registered
    /// (RLS not installed — the canonical check used across the hosting layer), the default
    /// evaluator would return <see cref="Permission.All"/> and silently open every page to
    /// anonymous browsers; instead the gate returns <see cref="AnonymousGateDecision.Login"/>.
    /// Any error in the permission probe or the policy lookup also settles on Login.</para>
    /// </summary>
    public static IObservable<AnonymousGateDecision> DecideAnonymousAccess(
        IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return Observable.Return(AnonymousGateDecision.Login);

        // Defer: both extensions have synchronous prologues (service resolution) — any throw
        // must surface as OnError into the Catch below, never on the caller's stack.
        return Observable.Defer(() =>
                hub.CheckPermission(path, WellKnownUsers.Anonymous, Permission.Read)
                    .Take(1)
                    .SelectMany(anonymousReadable => anonymousReadable
                        ? Observable.Return(new AnonymousGateDecision { Allow = true })
                        : hub.GetRedirectOnDenied(path)
                            .Take(1)
                            .Select(target => IsSafeRedirect(path, target)
                                ? new AnonymousGateDecision { RedirectTo = target!.TrimStart('/') }
                                : AnonymousGateDecision.Login)))
            .Catch<AnonymousGateDecision, Exception>(_ =>
                Observable.Return(AnonymousGateDecision.Login));
    }

    /// <summary>
    /// Loop-guard shared by every consumer of <see cref="PartitionAccessPolicy.RedirectOnDenied"/>:
    /// a redirect is safe only when a target is configured AND it is not the denied path itself or
    /// an ancestor-equal segment of it (redirecting a denied page onto itself would loop forever).
    /// Counterpart of the Layout-side <c>AreaErrorClassifier.IsSafeRedirect</c> (same rule; that
    /// project does not reference this one).
    /// </summary>
    public static bool IsSafeRedirect(string? deniedPath, string? redirectPath)
    {
        if (string.IsNullOrEmpty(deniedPath) || string.IsNullOrWhiteSpace(redirectPath))
            return false;
        var target = redirectPath.Trim().TrimStart('/');
        if (target.Length == 0)
            return false;
        return !string.Equals(deniedPath, target, StringComparison.Ordinal)
            && !deniedPath.StartsWith(target + "/", StringComparison.Ordinal);
    }
}
