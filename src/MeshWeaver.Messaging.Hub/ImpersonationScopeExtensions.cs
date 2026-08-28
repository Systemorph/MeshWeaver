using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 THE framework's impersonation boundary: a scope that <b>cannot escape the operation it was
/// opened for</b> — neither forwards, onto what the subscriber composes, nor backwards, onto the
/// thread that subscribed.
///
/// <para><b>The first defect this closes (issue #1444).</b> The idiomatic system read/write is
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
/// <para><b>The second defect, and why <c>Observable.Using</c> is gone from here (issue #1790).</b>
/// Impersonation is an <c>AsyncLocal</c> store/restore pair, so it must be opened and closed on ONE
/// logical flow. <c>Observable.Using</c> opens it on the SUBSCRIBING thread and disposes it when the
/// inner observable TERMINATES — for a cross-hub request/response, the owning hub's response thread.
/// The two halves land on different threads: <b>the subscriber keeps <c>system-security</c>
/// latched</b> for everything it does next, and the terminating thread is handed the subscriber's
/// captured "previous" identity. <see cref="ContainIdentity{T}"/> does not close that half — it
/// restores around NOTIFICATIONS, never around the <c>Subscribe</c> that opened the scope — which is
/// why this type now opens and closes the scope inside one synchronous <c>Subscribe</c> instead.
/// Measured, not reasoned: the seal in <c>ActivityLogLogger</c> latched System onto the script
/// thread and a <c>--render</c> export then resolved embedded areas the submitting user may not
/// read.</para>
///
/// <para><b>Why closing at the end of Subscribe still covers the work.</b> Everything a cold
/// pipeline does eagerly happens INSIDE <c>Subscribe</c>: the factory runs, the framework primitives
/// capture <see cref="AccessService.Context"/> at their call site, the post is stamped, and any
/// scheduled continuation captures the ExecutionContext as it stands right then — impersonated. The
/// restore afterwards mutates only the subscribing flow's own context; an ExecutionContext already
/// captured is an immutable snapshot and keeps the System identity. So the work runs as System, and
/// the caller's thread is handed back exactly what it had.</para>
///
/// <para><b>What it does NOT do: invent an identity.</b> The restore puts back exactly what was
/// ambient at Subscribe — a real user for a user-driven flow, <c>system-security</c> for a genuinely
/// system-initiated one, and <b>nothing at all</b> when the subscriber had no identity. That last
/// case is deliberate and is what makes adopting this safe at an existing call site: a background
/// worker with no ambient context keeps whatever the framework leaves it (the documented
/// <c>restoreNullCapture: false</c> behaviour of read pipelines), so the only behaviour that changes
/// is the one the defects describe.</para>
///
/// <para><b>Neither half is hypothetical, and one has already reached an authorization decision.</b>
/// <c>AccessControlPipeline.HandleGetPermission</c> carries a comment describing exactly the first
/// shape: <c>SecurityService</c>'s bootstrap-time <c>ImpersonateAsSystem</c> leaked past its
/// using-block onto the action-block thread, and trusting the ambient there "returned
/// <c>Permission.All</c> for every caller — including anonymous deliveries". That call site defends
/// itself by resolving the identity explicitly instead of reading the ambient. This type fixes the
/// class at the SOURCE, so the next call site does not have to know Rx's disposal order to be
/// safe.</para>
/// </summary>
public static class ImpersonationScopeExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> under the well-known System identity with the scope SEALED at
    /// both ends: the impersonation is entered at Subscribe (so the cold reads/writes the factory
    /// yields carry the system context), <b>left on the way out of that same Subscribe</b> (so the
    /// subscribing thread is never latched — issue #1790), and every notification that reaches the
    /// subscriber is delivered under the subscriber's OWN identity, so nothing composed downstream
    /// inherits System (issue #1444).
    ///
    /// <para>Prefer this over a hand-written <c>Observable.Using(access.ImpersonateAsSystem, …)</c>
    /// at EVERY site — both the ones that return the scoped observable to a caller (returning it is
    /// precisely what turns a scope into an inheritance) and the fire-and-forget
    /// <c>….Subscribe(…)</c> ones (which is where the thread latch bites).</para>
    ///
    /// <para>Compose the WIDEST cold pipeline inside <paramref name="work"/>. Everything inside it
    /// keeps today's emission-time behaviour exactly; the seal applies at the boundary only.</para>
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
            : new SubscribeScopedObservable<T>(access, access.ImpersonateAsSystem, work);

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
            : new SubscribeScopedObservable<T>(access, () => access.ImpersonateAsHub(hub), work);

    /// <summary>
    /// Runs <paramref name="work"/> as an EXPLICIT identity the caller has already resolved — the
    /// <see cref="AccessService.SwitchAccessContext"/> shape — sealed exactly as
    /// <see cref="RunAsSystem{T}"/> is.
    ///
    /// <para>Use it where the identity is a value the caller carries rather than System: an export
    /// rendering an embedded area as the requesting user, a Blazor view re-establishing the durable
    /// circuit user for a mesh read it subscribes after an Rx hop. A null
    /// <paramref name="identity"/> runs the work unswitched (deferred, still cold) — nothing is
    /// invented.</para>
    /// </summary>
    /// <typeparam name="T">What the scoped operation emits.</typeparam>
    /// <param name="access">The access service, or null on a host without one.</param>
    /// <param name="identity">The identity to run under; null runs the work unswitched.</param>
    /// <param name="work">The cold operation to run under <paramref name="identity"/>.</param>
    public static IObservable<T> RunAs<T>(
        this AccessService? access, AccessContext? identity, Func<IObservable<T>> work) =>
        access is null || identity is null
            ? Observable.Defer(work)
            : new SubscribeScopedObservable<T>(access, () => access.SwitchAccessContext(identity), work);

    /// <summary>
    /// The overload that resolves the identity AT SUBSCRIBE rather than at composition — for a
    /// caller whose identity lives in an <c>AsyncLocal</c> that is only reliable on the subscribing
    /// thread (<c>BlazorView.ResolveCircuitUser</c>). Same seal as
    /// <see cref="RunAs{T}(MeshWeaver.Messaging.AccessService?,MeshWeaver.Messaging.AccessContext?,System.Func{System.IObservable{T}})"/>;
    /// a resolver returning null runs the work unswitched.
    /// </summary>
    /// <typeparam name="T">What the scoped operation emits.</typeparam>
    /// <param name="access">The access service, or null on a host without one.</param>
    /// <param name="resolveIdentity">Resolves the identity on the subscribing thread; may return null.</param>
    /// <param name="work">The cold operation to run under the resolved identity.</param>
    public static IObservable<T> RunAs<T>(
        this AccessService? access, Func<AccessContext?> resolveIdentity, Func<IObservable<T>> work) =>
        access is null
            ? Observable.Defer(work)
            : new SubscribeScopedObservable<T>(
                access,
                () => resolveIdentity() is { } identity ? access.SwitchAccessContext(identity) : null,
                work);

    /// <summary>
    /// Seals an ALREADY-COMPOSED <paramref name="source"/>: every notification is delivered to the
    /// subscriber under the identity that was ambient when they subscribed, whatever identity
    /// produced the emission.
    ///
    /// <para>Use this where the impersonated region is not one call — a chain of system writes whose
    /// LAST emission is what the caller composes on. Sealing the chain's boundary is what stops the
    /// caller inheriting it.</para>
    ///
    /// <para>🚨 It seals the FORWARD direction only. It opens no scope, so it has nothing to restore
    /// on the subscribing thread — if the chain it wraps opens an impersonation with
    /// <c>Observable.Using</c>, that thread stays latched. Use <see cref="RunAsSystem{T}"/> /
    /// <see cref="RunAsHub{T}"/> / <see cref="RunAs{T}(MeshWeaver.Messaging.AccessService?,MeshWeaver.Messaging.AccessContext?,System.Func{System.IObservable{T}})"/>,
    /// which own both ends.</para>
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

    /// <summary>
    /// The seal itself: ONE synchronous <c>Subscribe</c> that opens the impersonation, composes and
    /// subscribes the work inside it, and leaves it on the way out — on the same thread, always.
    ///
    /// <para>Deliberately NOT <c>Observable.Using</c>, which would defer the dispose to whichever
    /// thread the inner observable terminates on (#1790). Deliberately not <c>Defer</c> + a plain
    /// <c>using</c> around the factory either: that would close the scope before anything SUBSCRIBED
    /// to what the factory built, so the read/write would be issued unimpersonated (the bug shape
    /// <c>AccessContextPropagation.md</c> is about). Subscribe is the one boundary that is both — the
    /// moment the cold work actually starts, and a synchronous call frame we own.</para>
    /// </summary>
    private sealed class SubscribeScopedObservable<T>(
        AccessService access, Func<IDisposable?> openScope, Func<IObservable<T>> work) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            // Read the caller BEFORE impersonating — this is the last moment the ambient context is
            // reliably theirs, and it is what every notification is delivered under.
            var caller = access.Context;
            using (openScope())
            {
                IObservable<T> source;
                try
                {
                    source = work();
                }
                catch (Exception ex)
                {
                    // 🚨 Mirror Observable.Defer: a synchronous throw while COMPOSING is an
                    // OnError, not an exception escaping Subscribe. Without this, replacing a
                    // `Observable.Defer(...)` with `RunAsSystem(...)` silently changes the fault
                    // channel — and callers that classify faults off the sequence stop seeing
                    // them. IdentityRead.Bounded is exactly such a caller: it maps OnError to
                    // IdentityReadOutcome.Unavailable, and an escaping throw bypasses that
                    // classification entirely, turning "we could not find out" back into an
                    // unclassified request failure (the #637 collapse this codebase spent real
                    // effort to eliminate). Caught on MeshWeaver#2583 by review.
                    //
                    // Only the factory is guarded, which is precisely what Defer guards; an
                    // exception thrown by the subscribe itself keeps propagating as Rx expects.
                    observer.OnError(ex);
                    return Disposable.Empty;
                }
                // A null caller passes through unwrapped: clamping to null here would fail-close a
                // background flow that never had a user. Same rule as ContainIdentity.
                return caller is null
                    ? source.Subscribe(observer)
                    : AccessContextCaptureExtensions.SubscribeRestoring(source, observer, access, caller);
            }
        }
    }
}
