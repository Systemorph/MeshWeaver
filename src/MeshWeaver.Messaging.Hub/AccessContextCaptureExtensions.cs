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
        var captured = access.Context ?? access.CircuitContext;
        if (captured is null) return source;
        return Observable.Defer(() =>
        {
            access.SetContext(captured);
            return source.Do(_ => access.SetContext(captured));
        });
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
        if (access is null) return source;
        var captured = access.Context ?? access.CircuitContext;
        if (captured is null) return source;
        return Observable.Defer(() =>
        {
            access.SetContext(captured);
            return source.Do(_ => access.SetContext(captured));
        });
    }
}
