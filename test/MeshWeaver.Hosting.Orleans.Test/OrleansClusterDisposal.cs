using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Drains Orleans <see cref="TestCluster"/> disposals on the <see cref="IIoPool"/> instead of
/// awaiting them inline at per-class teardown.
///
/// <para><b>Why.</b> Awaiting <c>Cluster.DisposeAsync()</c> on the xUnit teardown thread
/// DEADLOCKS the suite under load: the silo shutdown drives continuations that the
/// blocked teardown thread owns, and a zombie silo (in-flight <c>Memory</c>-stream
/// messages draining against an already-disposed DI scope — see
/// <see cref="OrleansShutdownRaceSuppressor"/>) never completes. Because every Orleans
/// test class spins up and tears down its OWN cluster, one wedged teardown stalls the
/// whole sequential run (<c>maxParallelThreads:1</c>).</para>
///
/// <para><b>Fix.</b> No <c>async</c>/<c>await</c> and no hand-rolled <c>Task.Run</c>/<c>Task.WaitAll</c>
/// on the teardown thread. Each cluster's ordered stop→dispose is pushed onto
/// <see cref="IoPool.Unbounded"/> — the sanctioned async boundary — as a COLD
/// <see cref="IObservable{T}"/>, made hot immediately (so the next class's cluster starts right away
/// and disposals drain concurrently), and its completion replayed to the final drain. Every disposal
/// is then awaited ONCE — SYNCHRONOUSLY, via Rx blocking — at the
/// <see cref="OrleansDisposalDrainFixture">assembly fixture</see>'s teardown (which runs AFTER all
/// test classes but BEFORE the native in-process runner's foreground-thread check), bounded so a
/// genuinely-wedged silo is abandoned rather than hanging the wait.</para>
///
/// <para>The native xUnit v3 runner force-exits non-zero — <c>"Foreground threads were left running,
/// forcing process exit"</c> — when a silo's shutdown threads are still draining at the check.
/// Under vstest that check didn't run, so the leak was invisible; the assembly-fixture drain (not the
/// old <c>ProcessExit</c> hook, which fires AFTER the check) closes it.</para>
/// </summary>
internal static class OrleansClusterDisposal
{
    // Each cluster's stop→dispose as a hot, replayed observable — its completion is what the final
    // drain blocks on. Replay-backed so a disposal that finishes before the drain subscribes still
    // replays its OnCompleted.
    private static readonly ConcurrentBag<IObservable<Unit>> Pending = new();

    /// <summary>
    /// Hand a cluster's disposal to the I/O pool — NEVER awaited on the teardown thread. Null-safe
    /// and best-effort per leg (the cluster is on its way down; a shutdown-race exception is benign).
    ///
    /// <para><b>Graceful ordered stop BEFORE dispose.</b> <see cref="TestCluster.DisposeAsync"/>
    /// disposes the Orleans CLIENT host through the generic <c>IHost.DisposeAsync()</c>, which only
    /// disposes the service provider and NEVER runs <c>StopAsync()</c>. So the client's connection
    /// message pump (<c>Orleans.Runtime.Messaging.Connection.ProcessIncoming</c>) keeps deserializing
    /// in-flight messages while the client's Autofac container is torn down; <c>CodecProvider</c> then
    /// lazily resolves a codec from the already-disposed <c>LifetimeScope</c> and throws
    /// <see cref="ObjectDisposedException"/>, which under CI load escapes unobserved and reds a
    /// 123/123-green shard. The silos are already graceful (<c>InProcessSiloHandle.DisposeAsync</c>
    /// runs <c>StopSiloAsync</c>); only the client host skips its <c>StopAsync</c>. Its graceful stop
    /// is <see cref="TestCluster.StopClusterClientAsync"/> — which <c>DisposeAsync</c> skips and
    /// <see cref="TestCluster.StopAllSilosAsync"/> does NOT cover. We run it FIRST (client stops
    /// initiating), THEN the silos, THEN dispose — so by dispose time no connection pump is resolving
    /// a codec. Pinned by <c>TeardownStragglerCapturer</c>: disposed-scope throws per run stay 0.</para>
    /// </summary>
    public static void DisposeInBackground(TestCluster? cluster)
    {
        if (cluster is null)
            return;

        // Ordered: client stop → silo stop → dispose. Each leg runs on the I/O pool (off the
        // teardown thread, ConfigureAwait(false) inside the pool); each Catch-swallows its own
        // stop-race so a failed leg can't skip the ones that follow. SelectMany sequences them.
        var drain =
            RunVoid(cluster.StopClusterClientAsync)
                .SelectMany(_ => RunVoid(cluster.StopAllSilosAsync))
                .SelectMany(_ => RunVoid(() => cluster.DisposeAsync().AsTask()));

        // Make it hot NOW so the disposal drains concurrently with the next class booting, and
        // replay its terminal notification to the (later) synchronous drain. Connect starts it.
        var replayed = drain.Replay(1);
        replayed.Connect();
        Pending.Add(replayed);
    }

    /// <summary>
    /// Runs one <see cref="Task"/>-returning leaf on <see cref="IoPool.Unbounded"/> and projects it to
    /// <see cref="Unit"/>, swallowing a benign shutdown-race so a failed leg completes rather than
    /// faulting the sequence. The pool IS the async boundary — nothing is <c>await</c>ed here.
    /// </summary>
    private static IObservable<Unit> RunVoid(Func<Task> leaf) =>
        IoPool.Unbounded
            .Run(_ => leaf().ContinueWith(static _ => Unit.Default, TaskScheduler.Default))
            .Catch(Observable.Return(Unit.Default));

    /// <summary>
    /// Block (bounded) until every pooled disposal has completed. SYNCHRONOUS — Rx blocking,
    /// no <c>async</c>/<c>await</c>. Called from the assembly fixture's teardown so the process
    /// doesn't reach the runner's foreground-thread check while a silo's shutdown threads are
    /// still draining.
    /// </summary>
    public static void WaitAll(TimeSpan timeout)
    {
        var all = Pending.ToArray();
        if (all.Length == 0)
            return;
        try
        {
            // Merge every replayed completion; block on the merged stream's terminal notification.
            // DefaultIfEmpty guards a leg that replays only OnCompleted (no OnNext). Timeout abandons
            // a genuinely-wedged silo instead of hanging the run.
            Observable.Merge(all).DefaultIfEmpty(Unit.Default).Timeout(timeout).Wait();
        }
        catch
        {
            /* best-effort — a wedged silo is abandoned; TeardownStragglerCapturer still names it */
        }
    }
}
