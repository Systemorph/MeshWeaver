namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE classifier for "this fault is the hub's DI scope going away" — the DI-scope half of the
/// teardown contract (Doc/Architecture/ControlledIoPooling → "The mesh teardown drains THREE
/// things"). Every place that must tell a routine teardown from a genuine defect routes through
/// here: <c>MessageHub.HandleInitialize</c> (#2444), <c>RoutingGrain</c>'s delivery NACK (#2638),
/// the permission fold and the layout error placeholder (#2679).
///
/// <para>🚨 <b>The type test alone is NOT the signal, and that is deliberate.</b> An
/// <see cref="ObjectDisposedException"/> from an unrelated disposed dependency — a cache, a
/// connection, a stream somebody closed early — is a real defect and must keep being reported as
/// one. Only a PROBE that finds the hub's own scope no longer resolving turns the type test into a
/// positive statement about teardown. The probe resolves the hub's already-materialised
/// <see cref="IMessageHub"/> registration: on a live scope that is a cheap instance lookup with no
/// side effects; on a disposing scope Autofac throws from
/// <c>LifetimeScope.ThrowDisposedException</c> — which is the signal.</para>
///
/// <para><b>Why a disposed scope is proof the hub is going away.</b> The hub instance is a tracked
/// component of that same scope, so its <c>Dispose()</c> is already queued in the very disposer
/// that flipped the scope's disposed flag (Autofac sets the flag FIRST, then runs the disposer over
/// the tracked instances in reverse creation order). Anything observed in that window — an init
/// BuildupAction, a permission fold's next emission, a render's error placeholder — is racing a
/// teardown that has already been decided, never a fault on a live hub.</para>
///
/// <para>The walk is <see cref="ExceptionChain"/>'s — the exception GRAPH, not the
/// <c>InnerException</c> line — because these faults arrive through Rx <c>Catch</c> arms,
/// <c>Merge</c>s and <c>AggregateException</c>s whose ordering nobody controls; classifying by
/// which fault happened to land at index 0 is a race.</para>
/// </summary>
public static class ScopeTeardown
{
    /// <summary>
    /// True when <paramref name="exception"/> carries an <see cref="ObjectDisposedException"/>
    /// anywhere in its graph AND <paramref name="scopeDisposed"/> confirms the scope in question
    /// is gone. A null probe answers <c>false</c> — a caller with no scope to probe cannot make a
    /// teardown claim, so the fault stays what it was.
    /// </summary>
    /// <param name="exception">The fault to classify; may be null.</param>
    /// <param name="scopeDisposed">The probe — does the DI scope the faulting code resolved from
    /// still resolve? See <see cref="IsServiceScopeDisposed"/>.</param>
    public static bool IsScopeTeardown(Exception? exception, Func<bool>? scopeDisposed)
        => scopeDisposed is not null
           && ExceptionChain.Contains<ObjectDisposedException>(exception)
           && scopeDisposed();

    /// <summary>
    /// Probes whether <paramref name="hub"/>'s own DI scope still resolves. The probe is a lookup
    /// of the hub's already-materialised <see cref="IMessageHub"/> registration — side-effect free
    /// on a live scope, an <see cref="ObjectDisposedException"/> on a disposing one.
    /// </summary>
    /// <param name="hub">The hub whose scope to probe.</param>
    /// <returns><c>true</c> when the scope (or one of its parents) has been disposed.</returns>
    public static bool IsServiceScopeDisposed(this IMessageHub hub)
    {
        try
        {
            hub.ServiceProvider.GetService(typeof(IMessageHub));
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> is the teardown of <paramref name="hub"/>'s own DI
    /// scope: an <see cref="ObjectDisposedException"/> in the graph AND
    /// <see cref="IsServiceScopeDisposed"/> confirming the scope is gone. The convenience form of
    /// <see cref="IsScopeTeardown"/> for callers that hold the hub.
    /// </summary>
    /// <param name="hub">The hub whose scope the faulting code resolved from.</param>
    /// <param name="exception">The fault to classify; may be null.</param>
    public static bool IsTerminatedByScopeTeardown(this IMessageHub hub, Exception? exception)
        => IsScopeTeardown(exception, hub.IsServiceScopeDisposed);
}
