using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 THE framework's impersonation boundary: a scope that <b>cannot escape the operation it was
/// opened for</b>.
///
/// <para><b>The defect this closes (issue #1444).</b> The idiomatic system read/write is
/// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; work)</c>, and it is correct for
/// <i>the work itself</i>. It is not self-contained. Rx forwards <c>OnNext</c> to the subscriber
/// <b>before</b> <c>Using</c> disposes its resource, so everything the subscriber composes on that
/// emission — the next phase, a write, a permission check — is built while the impersonation is
/// still OPEN. And the mesh write primitives EAGER-CAPTURE <see cref="AccessService.Context"/> when
/// they are CALLED and re-stamp it around their own emissions
/// (<see cref="AccessContextCaptureExtensions.CarryAccessContext{T}(System.IObservable{T},System.IServiceProvider,bool)"/>),
/// so the System identity does not merely survive one hop — it is <b>re-acquired</b> at every hop
/// after it.</para>
///
/// <para><b>This is not hypothetical, and it has already reached an authorization decision.</b>
/// <c>AccessControlPipeline.HandleGetPermission</c> carries a comment describing exactly this shape:
/// <c>SecurityService</c>'s bootstrap-time <c>ImpersonateAsSystem</c> leaked past its using-block
/// onto the action-block thread, and trusting the ambient there "returned <c>Permission.All</c> for
/// every caller — including anonymous deliveries". That call site defends itself by resolving the
/// identity explicitly instead of reading the ambient. This type fixes the same class at the SOURCE,
/// so the next call site does not have to know Rx's disposal order to be safe.</para>
///
/// <para><b>What it does NOT do: invent an identity.</b> <see cref="ContainIdentity{T}"/> restores
/// exactly what was ambient at Subscribe — a real user for a user-driven flow, <c>system-security</c>
/// for a genuinely system-initiated one, and <b>nothing at all</b> when the subscriber had no
/// identity. That last case is deliberate and is what makes adopting this safe at an existing call
/// site: a background worker with no ambient context keeps whatever the framework leaves it (the
/// documented <c>restoreNullCapture: false</c> behaviour of read pipelines), so the only behaviour
/// that changes is the one the defect describes — a caller who HAD an identity, silently continuing
/// as System.</para>
/// </summary>
public static class ImpersonationScopeExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> under the well-known System identity with the scope SEALED: the
    /// impersonation is entered at Subscribe (so the cold reads/writes the factory yields carry the
    /// system context — the <c>Observable.Using</c>, never <c>Defer</c>-plus-<c>using</c>, rule),
    /// and every notification that reaches the subscriber is delivered under the subscriber's OWN
    /// identity, so nothing composed downstream inherits System.
    ///
    /// <para>Prefer this over a hand-written <c>Observable.Using(access.ImpersonateAsSystem, …)</c>
    /// at every site that RETURNS the scoped observable to a caller — returning it is precisely what
    /// turns a scope into an inheritance.</para>
    ///
    /// <para>A null <paramref name="access"/> (minimal test hosts) runs the work unimpersonated —
    /// deferred, so it stays cold.</para>
    /// </summary>
    /// <typeparam name="T">What the scoped operation emits.</typeparam>
    /// <param name="access">The access service, or null on a host without one.</param>
    /// <param name="work">The cold operation to run as System.</param>
    public static IObservable<T> RunAsSystem<T>(this AccessService? access, Func<IObservable<T>> work) =>
        access is null
            ? Observable.Defer(work)
            : Observable
                .Using(access.ImpersonateAsSystem, _ => Observable.Defer(work))
                .ContainIdentity(access);

    /// <summary>
    /// Runs <paramref name="work"/> as <paramref name="hub"/>'s own identity, sealed exactly as
    /// <see cref="RunAsSystem{T}"/> is.
    /// </summary>
    /// <typeparam name="T">What the scoped operation emits.</typeparam>
    /// <param name="access">The access service, or null on a host without one.</param>
    /// <param name="hub">The hub whose address is stamped as the principal.</param>
    /// <param name="work">The cold operation to run as the hub.</param>
    public static IObservable<T> RunAsHub<T>(
        this AccessService? access, IMessageHub hub, Func<IObservable<T>> work) =>
        access is null
            ? Observable.Defer(work)
            : Observable
                .Using(() => access.ImpersonateAsHub(hub), _ => Observable.Defer(work))
                .ContainIdentity(access);

    /// <summary>
    /// Seals an ALREADY-COMPOSED <paramref name="source"/>: every notification is delivered to the
    /// subscriber under the identity that was ambient when they subscribed, whatever identity
    /// produced the emission.
    ///
    /// <para>Use this where the impersonated region is not one call — a chain of system writes whose
    /// LAST emission is what the caller composes on. Sealing the chain's boundary is what stops the
    /// caller inheriting it.</para>
    ///
    /// <para>The restore is per-notification (entered on <c>OnNext</c>/<c>OnError</c>/
    /// <c>OnCompleted</c>, disposed as the callback returns), so nothing is left behind on the
    /// emitting thread — the same shape <c>CarryAccessContext</c> uses, and the same code.</para>
    /// </summary>
    /// <typeparam name="T">What the operation emits.</typeparam>
    /// <param name="source">The already-composed operation whose identity must not escape.</param>
    /// <param name="access">The access service, or null on a host without one.</param>
    public static IObservable<T> ContainIdentity<T>(this IObservable<T> source, AccessService? access) =>
        access is null ? source : new ContainedIdentityObservable<T>(source, access);

    /// <summary>
    /// Reads the subscriber's identity at Subscribe — before any scope this pipeline opens, which is
    /// the last moment the ambient context is reliably the subscriber's own — and restores it around
    /// every notification. A null read passes straight through: nothing to restore, nothing invented.
    /// </summary>
    private sealed class ContainedIdentityObservable<T>(IObservable<T> source, AccessService access)
        : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var caller = access.Context;
            return caller is null
                ? source.Subscribe(observer)
                : AccessContextCaptureExtensions.SubscribeRestoring(source, observer, access, caller);
        }
    }
}
