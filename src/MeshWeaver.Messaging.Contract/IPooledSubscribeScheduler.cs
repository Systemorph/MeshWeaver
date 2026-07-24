namespace MeshWeaver.Messaging;

/// <summary>
/// Runs an observable's <b>subscribe</b> through the mesh's teardown-drainable I/O pool instead of a
/// bare <c>SubscribeOn(TaskPoolScheduler.Default)</c>.
///
/// <para>Layout-area render pipelines hop their subscribe off the owning hub's action block so a
/// view generator that queries in-render cannot wedge the hub turn. Historically that hop used
/// <c>SubscribeOn(TaskPoolScheduler.Default)</c> — a bare ThreadPool schedule that mesh teardown
/// cannot see. During teardown that render subscribe keeps executing on a ThreadPool thread AFTER
/// the hub's Autofac <c>LifetimeScope</c> is disposed (→ <see cref="System.ObjectDisposedException"/>
/// from <c>GetService</c>) and, for a node whose render touches types compiled into a collectible
/// <c>AssemblyLoadContext</c>, AFTER that ALC is unloaded (→ native use-after-unload SIGSEGV — the
/// endemic teardown crash <c>MeshWeaver.FutuRe.Test</c> reproduced as exit=139).</para>
///
/// <para>Routing the subscribe through this scheduler makes it a <b>tracked, gated, cancellable</b>
/// leaf on a drainable pool: mesh teardown's <c>IoPoolRegistry.DrainAll()</c> cancels + <b>joins</b>
/// the in-flight subscribe before the service scope disposes and the ALCs unload, so no render ever
/// observes a disposed scope or a freed ALC. This is the exact contract <c>MeshQuery</c> already
/// relies on for its change-feed subscribes.</para>
///
/// <para>Resolved from the hub's <c>ServiceProvider</c>; when a mesh has no I/O pools registered
/// (bare messaging-only hubs) the caller falls back to the historical
/// <c>SubscribeOn(TaskPoolScheduler.Default)</c> — those hubs compile no collectible ALCs, so the
/// drain has nothing to join.</para>
/// </summary>
public interface IPooledSubscribeScheduler
{
    /// <summary>
    /// Returns an observable that, when subscribed, runs <paramref name="source"/>'s subscribe on the
    /// drainable pool (off the calling hub/grain scheduler) as a tracked leaf that mesh teardown
    /// cancel+joins. Emissions flow through unchanged; ordering is preserved.
    /// </summary>
    IObservable<T> SubscribeThroughPool<T>(IObservable<T> source);
}
