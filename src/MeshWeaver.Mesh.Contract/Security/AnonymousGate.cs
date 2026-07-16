using System.Reactive.Linq;
using MeshWeaver.Messaging;

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
/// </summary>
public static class AnonymousGate
{
    /// <summary>
    /// True when a logged-OUT visitor may load <paramref name="path"/> — an explicit positive
    /// Anonymous Read grant; false ⇒ redirect to /login.
    ///
    /// <para><b>Fail-closed.</b> When no <see cref="EffectivePermissionsDelegate"/> is registered
    /// (RLS not installed — the canonical check used across the hosting layer), the default
    /// evaluator would return <see cref="Permission.All"/> and silently open every page to
    /// anonymous browsers; instead the gate returns false. Any error in the permission probe also
    /// settles on false.</para>
    /// </summary>
    public static IObservable<bool> AllowAnonymous(IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return Observable.Return(false);

        // Defer: CheckPermission has a synchronous prologue (service resolution) — any throw
        // must surface as OnError into the Catch below, never on the caller's stack.
        return Observable.Defer(() =>
                hub.CheckPermission(path, WellKnownUsers.Anonymous, Permission.Read).Take(1))
            .Catch<bool, Exception>(_ => Observable.Return(false));
    }
}
