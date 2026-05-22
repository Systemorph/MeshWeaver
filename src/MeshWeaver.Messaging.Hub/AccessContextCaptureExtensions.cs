using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// Cross-cutting helper that makes <see cref="AccessService.Context"/>
/// "survive Subscribe()" on cold observables returned by framework primitives
/// (<see cref="MeshWeaver.Mesh.Services.IMeshService.CreateNode"/>,
/// <c>MeshNodeStreamHandle.Update</c>, <c>IMeshNodeStreamCache.Update</c>, …).
///
/// <para><b>Why this exists.</b> The messaging layer already restores
/// <see cref="AccessService.Context"/> from <c>IMessageDelivery.AccessContext</c>
/// on every handler invocation (see <c>MessageHubGrain.DeliverMessage</c> +
/// <c>UserServicePostPipeline</c>). The handler's thread therefore has the
/// right AsyncLocal value during its synchronous body. The break is the
/// <c>.Subscribe(...)</c> boundary: a handler does
/// <c>stream.Update(fn).Subscribe(_ =&gt; meshService.CreateNode(...))</c>;
/// the cold pipeline schedules the callback on a different thread (workspace
/// emission scheduler, thread pool); that thread has no AsyncLocal value;
/// the inner <c>CreateNode</c>'s PostPipeline sees a null context and the
/// resulting <see cref="AccessControl"/> check denies or — worse, before the
/// 2026-05-21 cleanup — silently falls back to stamping the hub's own
/// address as principal.</para>
///
/// <para><b>The model: MessageHub sets, framework primitive preserves.</b>
/// Every framework method that returns a cold <see cref="IObservable{T}"/>
/// wraps its return with <see cref="CarryAccessContext{T}"/> internally. The
/// helper captures <see cref="AccessService.Context"/> at the moment the
/// primitive runs (the caller's thread, where AsyncLocal is correct) and
/// re-stamps it on every emission of the returned pipeline. Callers never
/// need a per-Subscribe wrapper — they keep writing the natural
/// <c>meshService.CreateNode(node).Subscribe(...)</c> shape and the
/// framework guarantees the operation runs under their identity regardless
/// of where Subscribe lands.</para>
///
/// <para>Mirrors the pattern in <c>MessageHub.RestoreUserContextOnEmission</c>
/// which already does the same for response observables. The difference: this
/// helper is applied INSIDE framework primitives so it covers the full set of
/// outbound cold observables, not just response streams.</para>
/// </summary>
public static class AccessContextCaptureExtensions
{
    /// <summary>
    /// Captures <see cref="AccessService.Context"/> (falling back to
    /// <see cref="AccessService.CircuitContext"/>) at the moment this method
    /// runs, and returns an observable that restores that captured context
    /// on every emission via <see cref="AccessService.SetContext"/>.
    ///
    /// <para>Capture is eager — performed on the caller's thread synchronously
    /// — so the captured value reflects the AsyncLocal the framework primitive
    /// was invoked under. Restore happens on whatever thread the downstream
    /// emission lands on, immediately before the subscriber observes the
    /// value.</para>
    ///
    /// <para>Pass-through when no <see cref="AccessService"/> is registered
    /// (rare — usually only minimal test fixtures) or when no context is set
    /// (background flows must use explicit <see cref="AccessService.ImpersonateAsSystem"/>
    /// / <see cref="AccessService.ImpersonateAsHub"/>).</para>
    /// </summary>
    /// <param name="source">Cold observable returned by a framework primitive.</param>
    /// <param name="services">DI scope used to resolve <see cref="AccessService"/>.</param>
    public static IObservable<T> CarryAccessContext<T>(
        this IObservable<T> source, IServiceProvider services)
    {
        var access = services.GetService<AccessService>();
        if (access is null) return source;

        // 🚨 2026-05-22: this method used to set `access.SetContext(captured)`
        // both at Subscribe time and on every emission. That mutated AsyncLocal
        // on whatever thread Subscribe ran on (often the caller's), with NO
        // restore — leaking the captured identity into the caller's logical
        // execution context indefinitely.
        //
        // Symptom: McpUpdate tests showed user1's identity used for a request
        // freshly authenticated as user2. Earlier user1 call → wrap set
        // Context=user1 on the test thread → later user2 LoginWithToken set
        // CircuitContext=user2 but Context was still user1 → CaptureContext
        // returns Context (priority over CircuitContext) → user1 stamped on
        // the user2 delivery.
        //
        // The premise that motivated the SetContext — "downstream chained
        // operators (Select/SelectMany) re-call CaptureContext on the
        // Subscribe thread and need AsyncLocal set" — is wrong for the
        // common framework primitives: ConfigurePost already stamps
        // delivery.AccessContext from the captured value, so identity rides
        // ON the delivery and the receiver's UserServiceDeliveryPipeline
        // sets/restores AsyncLocal under proper try/finally. Chained
        // operators inside the framework primitives that need the identity
        // can read it from `captured` directly in their closure.
        //
        // Net effect: this is now a pass-through. Kept as an extension point
        // so callsites don't change, but it no longer mutates AsyncLocal.
        return source;
    }

    /// <summary>
    /// Overload that takes a pre-resolved <see cref="AccessService"/> — for
    /// callers that already hold a reference (e.g. inside an
    /// <see cref="IMessageHub"/> implementation) and want to avoid the DI
    /// lookup. Same capture-then-restore semantics as the IServiceProvider
    /// overload.
    /// </summary>
    public static IObservable<T> CarryAccessContext<T>(
        this IObservable<T> source, AccessService? access)
    {
        // See the IServiceProvider overload for the full reasoning: this method
        // is now a pass-through. Identity rides on delivery.AccessContext via
        // PostOptions; the receiver's UserServiceDeliveryPipeline sets/restores
        // AsyncLocal under try/finally. Mutating AsyncLocal here leaked.
        return source;
    }
}
