namespace MeshWeaver.Messaging;

/// <summary>
/// Runs an observable's <b>subscribe</b> through the mesh's teardown-drainable I/O pool instead of a
/// bare <c>SubscribeOn(TaskPoolScheduler.Default)</c>.
///
/// <para>Layout-area render pipelines hop their subscribe off the owning hub's action block so a
/// view generator that queries in-render cannot wedge the hub turn. Historically that hop used
/// <c>SubscribeOn(TaskPoolScheduler.Default)</c> — a bare ThreadPool schedule that mesh teardown
/// cannot see. During teardown that render subscribe keeps executing on a ThreadPool thread AFTER
/// the hub's service scope is disposed (→ <see cref="System.ObjectDisposedException"/>
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

    /// <summary>
    /// <see cref="SubscribeThroughPool{T}(IObservable{T})"/>, with each subscription also OWNED UNTIL THE
    /// POOL DRAIN: the mesh's pool registry disposes it before it cancels the pool it runs on, and
    /// disposing (or terminating) the subscription earlier forgets it.
    ///
    /// <para><b>Who needs this (#7992).</b> A caller whose subscriptions have an owner that disposes them,
    /// but whose owner can outlive a silo's stop. A layout area's render subscriptions belong to its
    /// <c>LayoutAreaHost</c>, which is disposed with its hub — and on a silo every hub lives on past
    /// <c>IoPoolSiloTeardown</c> until the host stops. Without this the cancel found every live area's
    /// render legs still attached and reported them as unowned.</para>
    ///
    /// <para>The default implementation has no registry to own anything with and behaves exactly as the
    /// one-argument overload.</para>
    /// </summary>
    /// <param name="source">The observable whose subscribe runs on the pool.</param>
    /// <param name="ownedUntilDrainBy">Who owns the subscriptions, for the release's log line.</param>
    /// <returns>The pooled observable; each of its subscriptions is released before the pool's cancel.</returns>
    IObservable<T> SubscribeThroughPool<T>(IObservable<T> source, string ownedUntilDrainBy)
        => SubscribeThroughPool(source);
}
