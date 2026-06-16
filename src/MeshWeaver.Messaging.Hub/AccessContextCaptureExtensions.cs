using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// Cross-cutting helper that makes <see cref="AccessService.Context"/>
/// "survive Subscribe()" on cold observables returned by framework primitives
/// (<c>IMeshService.CreateNode</c>,
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
/// resulting <c>AccessControl</c> check denies or — worse, before the
/// 2026-05-21 cleanup — silently falls back to stamping the hub's own
/// address as principal.</para>
///
/// <para><b>The model: MessageHub sets, framework primitive preserves.</b>
/// Every framework method that returns a cold <see cref="IObservable{T}"/>
/// wraps its return with <see cref="CarryAccessContext{T}(System.IObservable{T},System.IServiceProvider,System.Boolean)"/> internally. The
/// helper captures <see cref="AccessService.Context"/> at the moment the
/// primitive runs (the caller's thread, where AsyncLocal is correct) and
/// re-stamps it on every emission of the returned pipeline via a per-callback
/// <see cref="AccessService.SwitchAccessContext"/> scope. The scope is created
/// on entry to each <c>OnNext</c>/<c>OnError</c>/<c>OnCompleted</c> callback
/// and disposed as the callback returns — AsyncLocal is touched ONLY for the
/// duration of the callback, so no value can leak into the surrounding
/// logical execution context (the 2026-05-22 leak that drove the temporary
/// pass-through is closed by this scoping).</para>
///
/// <para>Callers never need a per-Subscribe wrapper — they keep writing the
/// natural <c>meshService.CreateNode(node).Subscribe(...)</c> shape and the
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
    /// on every emission via a per-callback <see cref="AccessService.SwitchAccessContext"/>
    /// scope.
    ///
    /// <para>Capture is eager — performed on the caller's thread synchronously
    /// — so the captured value reflects the AsyncLocal the framework primitive
    /// was invoked under. Restore happens on whatever thread the downstream
    /// emission lands on, immediately before the subscriber observes the
    /// value, and is rolled back as the subscriber's callback returns.</para>
    ///
    /// <para>Pass-through when no <see cref="AccessService"/> is registered
    /// (rare — usually only minimal test fixtures) or when no context is set
    /// (background flows must use explicit <see cref="AccessService.ImpersonateAsSystem"/>
    /// / <see cref="AccessService.ImpersonateAsHub"/>).</para>
    ///
    /// <para><b>Leak-fix (2026-05-28).</b> The earlier capture-and-restore
    /// implementation (reverted 2026-05-22) called
    /// <c>access.SetContext(captured)</c> on each emission without restoring.
    /// That mutated AsyncLocal on whatever thread Subscribe ran on (often
    /// the caller's) and left the captured value live for every subsequent
    /// operation on that logical execution context — the McpUpdate user1/user2
    /// cross-contamination bug. This implementation creates a
    /// <see cref="IDisposable"/> scope per callback and disposes it before the
    /// callback returns, so AsyncLocal is restored to its prior value on every
    /// path (success, error, completion). No long-lived mutation, no leak.</para>
    /// </summary>
    /// <param name="source">Cold observable returned by a framework primitive.</param>
    /// <param name="services">DI scope used to resolve <see cref="AccessService"/>.</param>
    public static IObservable<T> CarryAccessContext<T>(
        this IObservable<T> source, IServiceProvider services, bool restoreNullCapture = false)
    {
        var access = services.GetService<AccessService>();
        return CarryAccessContext(source, access, restoreNullCapture);
    }

    /// <summary>
    /// Overload that takes a pre-resolved <see cref="AccessService"/> — for
    /// callers that already hold a reference (e.g. inside an
    /// <see cref="IMessageHub"/> implementation) and want to avoid the DI
    /// lookup. Same capture-then-restore semantics as the IServiceProvider
    /// overload.
    /// </summary>
    public static IObservable<T> CarryAccessContext<T>(
        this IObservable<T> source, AccessService? access, bool restoreNullCapture = false)
    {
        if (access is null) return source;

        // 🚨 Capture by value at invocation time. The captured snapshot rides
        // the closure on the returned observable; it is NEVER re-read from
        // AsyncLocal at emission time. Future calls to access.SetContext(null)
        // or scope disposals on the caller's thread don't affect what this
        // pipeline restores.
        //
        // We capture Context ONLY, not CircuitContext. PostPipeline reads
        // `Context ?? CircuitContext` itself at post time, so a null capture
        // doesn't lose CircuitContext for downstream Posts — but synthesising
        // CircuitContext here would leak the Blazor circuit identity into
        // background-Subscribe code paths the user never asked for (the
        // 757d2a296 anti-pattern).
        //
        // 🚨 NULL capture, two modes:
        //
        // • restoreNullCapture=true (the WRITE-result observables —
        //   MeshNodeStreamHandle.Update/Overwrite): null is RESTORED AS NULL.
        //   The emission thread belongs to framework plumbing whose ambient
        //   AsyncLocal is its own infrastructure identity (the stream cache's
        //   read path runs ImpersonateAsSystem); passing through let that
        //   identity leak into the caller's callback — `Context ?? CircuitContext`
        //   resolved to system-security instead of the circuit user (the hop4
        //   leak in TypedErrorPropagationTest), and a nested write inside the
        //   callback would POST AS SYSTEM, an escalation. Clamping re-creates
        //   the caller's exact state for the callback's duration.
        //
        // • restoreNullCapture=false (default — read/query pipelines): null
        //   passes through UNWRAPPED, preserving the ambient identity at
        //   emission. ⚠️ Known debt: several ops/MCP flows chain further
        //   operations inside subscriber callbacks and currently DEPEND on the
        //   leaked ambient identity (clamping globally broke
        //   PatchWorkspaceAckTest.Patch_ConcurrentUpdates_NoDeadlock with
        //   AccessDenied, MeshPlugin Search, and script-execution stamping —
        //   2026-06-11). Migrating those call sites to explicit impersonation
        //   is the prerequisite for flipping the default to clamp.
        var captured = access.Context;
        if (captured is null && !restoreNullCapture) return source;
        return new CarryAccessContextObservable<T>(source, access, captured);
    }

    /// <summary>
    /// Wraps a cold observable so every emission to the subscriber happens
    /// inside an <see cref="AccessService.SwitchAccessContext"/> scope keyed
    /// to <paramref name="captured"/>. The scope is per-callback (entered on
    /// OnNext/OnError/OnCompleted, disposed on return) so AsyncLocal is
    /// touched only for the lifetime of the subscriber's callback.
    /// </summary>
    private sealed class CarryAccessContextObservable<T>(
        IObservable<T> source, AccessService access, AccessContext? captured) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) =>
            source.Subscribe(new RestoringObserver<T>(observer, access, captured));
    }

    /// <summary>
    /// Forwards each notification to the inner observer inside an
    /// <see cref="AccessService.SwitchAccessContext"/> scope. The scope
    /// covers the synchronous body of the subscriber's callback only;
    /// AsyncLocal is restored as soon as the callback returns. The
    /// dispose path also enters the scope so any teardown code observes
    /// the captured identity.
    /// </summary>
    private sealed class RestoringObserver<T>(
        IObserver<T> inner, AccessService access, AccessContext? captured) : IObserver<T>
    {
        public void OnNext(T value)
        {
            using (access.SwitchAccessContext(captured))
                inner.OnNext(value);
        }

        public void OnError(Exception error)
        {
            using (access.SwitchAccessContext(captured))
                inner.OnError(error);
        }

        public void OnCompleted()
        {
            using (access.SwitchAccessContext(captured))
                inner.OnCompleted();
        }
    }
}
