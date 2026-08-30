using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Drains Orleans <c>TestCluster</c> disposals on the <see cref="IIoPool"/> instead of
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
    // 🚨 What lives here is a COMPLETION SIGNAL per in-flight teardown — never the drain itself,
    // and never an entry that has already settled.
    //
    // It used to be a ConcurrentBag<IObservable<Unit>> of CONNECTED Replay(1) drains. A connected
    // Replay holds its SOURCE for as long as the connectable itself is reachable, and the source
    // here is the SelectMany chain built in DisposeInBackground — whose lambdas close over the
    // TestCluster AND the Orleans client IHost. Nothing was ever removed from the bag, so every
    // cluster this assembly ever built was reachable from a static field until the process exited:
    // ~90 test classes × one silo + client host each (Autofac container, grain catalog, serializer
    // codecs, mesh hubs, workspaces, seeded MeshNodes, collectible NodeType ALCs). "Dispose it in
    // the background" is supposed to mean the cluster goes away once its disposal finishes; storing
    // the drain meant it never did.
    //
    // 🚨 Necessary, NOT sufficient — say so plainly, because the number does not move on its own.
    // A heap dump taken 95 s into a local run WITH this fix already applied still found all 194
    // LruGrainDirectoryCache instances live: the silos are ALSO rooted by undisposed timers sitting
    // in the process-wide System.Threading.TimerQueue (gcroot: TimerQueueTimer → GrainTimer →
    // PersistentStreamPullingManager → … → Orleans.Runtime.Silo, and a second family through an
    // Rx Sample(…) periodic timer). Removing one of two roots frees nothing, which is exactly what
    // the before/after RSS showed. What DID move the heap is TestGrainDirectorySizing — see that
    // file for the measurement and for why a large heap fails TESTS. This entry is still a real
    // root and is now guarded by ClusterDisposalRetentionTest; the timer roots are a separate,
    // unfixed finding recorded on issue #2346.
    private static readonly ConcurrentDictionary<long, AsyncSubject<Unit>> Pending = new();
    private static long drainId;

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
        Enqueue(
            RunVoid(cluster.StopClusterClientAsync)
                .SelectMany(_ => RunVoid(cluster.StopAllSilosAsync))
                .SelectMany(_ => RunVoid(() => cluster.DisposeAsync().AsTask())));
    }

    /// <summary>
    /// Same ordered drain for a cluster whose Orleans client host is owned by
    /// <see cref="OrleansTestClusterHost"/> rather than by <c>TestCluster</c> (see
    /// <see cref="OrleansTestCluster.DeployAsync"/>). The ordering rationale above is
    /// unchanged — the client stops initiating BEFORE the silos stop and BEFORE anything is
    /// disposed, so no connection pump is resolving a codec out of a torn-down container.
    /// Here the graceful client stop is a plain <c>IHost.StopAsync</c>, which is strictly
    /// better than <c>TestCluster.StopClusterClientAsync</c>: the host's own
    /// <c>DisposeAsync</c> never runs <c>StopAsync</c>, and this path always does.
    /// </summary>
    public static void DisposeInBackground(OrleansTestClusterHost? host)
    {
        if (host is null)
            return;

        var cluster = host.Cluster;
        var clientHost = host.ClientHost;

        Enqueue(
            RunVoid(() => clientHost is null ? Task.CompletedTask : clientHost.StopAsync())
                .SelectMany(_ => RunVoid(cluster.StopAllSilosAsync))
                .SelectMany(_ => RunVoid(() =>
                {
                    clientHost?.Dispose();
                    return Task.CompletedTask;
                }))
                .SelectMany(_ => RunVoid(() => cluster.DisposeAsync().AsTask())));
    }

    /// <summary>
    /// Makes an ordered drain hot NOW so the disposal proceeds concurrently with the next class
    /// booting, and publishes its terminal notification to the (later) synchronous drain.
    ///
    /// <para>🚨 The registry is handed an <see cref="AsyncSubject{T}"/> — a signal that references
    /// NOTHING — and the entry is removed the instant the drain settles, so the only thing holding
    /// the <c>TestCluster</c> graph is the live subscription, which Rx releases on
    /// termination. A cluster therefore becomes collectable the moment its own disposal finishes,
    /// which is what "dispose in the background" was always supposed to mean. Storing the drain
    /// (as a connected <c>Replay</c>) instead rooted every silo for the life of the process — see
    /// the field comment above and issue #2346.</para>
    ///
    /// <para><see cref="AsyncSubject{T}"/> is the right signal precisely because it replays its
    /// terminal notification: a drain that settles before <see cref="WaitAll"/> subscribes is
    /// already gone from the dictionary, and one that settles between the snapshot and the
    /// subscribe still hands <see cref="WaitAll"/> a completion rather than hanging it.</para>
    /// </summary>
    /// <returns>The registry id of this teardown — for <see cref="IsPending"/>; callers ignore it.</returns>
    internal static long Enqueue(IObservable<Unit> drain)
    {
        var id = Interlocked.Increment(ref drainId);
        var settled = new AsyncSubject<Unit>();
        Pending[id] = settled;

        // Subscribe (not Connect+retain) is what makes the drain hot. Every terminal path — value,
        // error, completion — settles exactly once; RunVoid already Catch-swallows a benign
        // shutdown race per leg, so OnError here would be genuinely unexpected and must still
        // release the entry rather than strand WaitAll on it.
        drain.Subscribe(
            _ => { },
            _ => Settle(id, settled),
            () => Settle(id, settled));
        return id;
    }

    /// <summary>
    /// Whether ONE teardown is still in flight. Deliberately per-id rather than a total count:
    /// other test classes are tearing their clusters down concurrently, so a count is not a
    /// property any single test can assert on.
    /// </summary>
    internal static bool IsPending(long id) => Pending.ContainsKey(id);

    /// <summary>Releases one drain's registry entry and publishes its completion.</summary>
    private static void Settle(long id, AsyncSubject<Unit> settled)
    {
        Pending.TryRemove(id, out _);
        settled.OnNext(Unit.Default);
        settled.OnCompleted();
    }

    /// <summary>
    /// The gate every cluster teardown passes through — BOUNDED, not <see cref="IoPool.Unbounded"/>.
    ///
    /// <para>Unbounded meant every cluster this assembly ever built could be shutting down AT ONCE
    /// underneath the class currently running. Each disposal is made hot immediately so the next
    /// class's cluster can start, which is correct — but silo shutdown is not cheap, so the bound
    /// keeps teardown from competing with the suite.</para>
    ///
    /// <para>🚨 The previous wording claimed this bound IS the shard-0 Orleans flake fix. It is not,
    /// and the flake is NOT resource contention. Measured 2026-08-10 on the real 4-vCPU runner, whole
    /// project per iteration (<c>Flake repro (manual)</c>): with <c>maxParallelThreads:4</c> it failed
    /// at iteration 3/6 (OrleansDelegationTest.Resubmit_AfterDelegation_DoesNotDeadlock); with the
    /// suite-wide SERIAL config it failed at iteration 2/6 (OrleansSubmitFromIdleTest +
    /// ThreadStartWedgeReproTest). Removing the parallelism changes nothing.</para>
    ///
    /// <para>Nor is the process starved when it happens. Across five serial local runs of the whole
    /// project the PASSING tests keep an identical profile — median 0.57-0.62 s, p90 1.33-1.84 s,
    /// 131-136 s in total — whether the run is green at ~136 s or red at ~230 s. The entire extra
    /// wall-clock of a red run is the victim's own 45 s budget. Everything else runs at full speed
    /// while exactly one test waits.</para>
    ///
    /// <para>What it actually looks like, with the silo logger finally wired (see
    /// <c>DynamicCompilationSiloConfigurator</c>): the victim creates its node, and then the silo
    /// logs NOTHING for the whole 45 s — no <c>[ACTIVATE]</c>, no <c>[ENRICH-DIAG]</c>, no
    /// <c>[COMPILE-TRACE]</c> — while other Information-level lines keep flowing. The request never
    /// reaches the grain. The victim rotates because it is whoever asks during the window, and the
    /// class recovers instantly afterwards (OrleansBrokenNodeTypeAccessTest's second test passes in
    /// 2.26 s right after the first has burned 46.6 s; the class ALONE runs both in 3.2 s). Do not
    /// re-derive "starvation" from the rotating names — that has now been asserted and refuted
    /// three times.</para>
    ///
    /// <para>🚨 <b>Scope of the two paragraphs above, corrected 2026-08-26 (#2346).</b> They are
    /// about CPU and about PARALLELISM, and both still hold: the bound is not the fix, and removing
    /// <c>maxParallelThreads:4</c> changes nothing. They do NOT cover the process's HEAP, and that
    /// turned out to be a second condition with the same rotating-victim signature. This test host
    /// reached 4.2–4.5 GB, and under workstation GC the resulting gen-2 pauses froze the WHOLE
    /// process for seconds — Orleans' own <c>LocalSiloHealthMonitor</c> and <c>Watchdog</c> logged
    /// 1.9 s ThreadPool delays and a 3.55 s runtime stall (2.74 s of it GC) at 3775 MB in the CI run
    /// that failed <c>OrleansGrainTeardownStragglerTest</c>. A stall like that is invisible in a
    /// MEDIAN — which is exactly why the "everything else runs at full speed" reading above missed
    /// it, and why it could reach a cluster-free, pure-CPU test (<c>RoutingBackpressureShapeTest</c>)
    /// that has no silo to blame. Where the memory actually went, and the fix, is
    /// <see cref="TestGrainDirectorySizing"/>. It does not explain the 45 s silo-silent window
    /// described above; that one is still open.</para>
    ///
    /// <para>Bounding is the framework's own prescription for this ("concurrency bounding channels
    /// through <c>IIoPool</c>"), not a tuning knob: it keeps teardown off the xUnit thread — which is
    /// what avoids the original deadlock. Legs of different clusters never wait on each other, so a
    /// bound cannot deadlock the drain.</para>
    /// </summary>
    private static readonly IIoPool DisposalPool = new IoPool(2);

    /// <summary>
    /// Runs one <see cref="Task"/>-returning leaf on <see cref="DisposalPool"/> and projects it to
    /// <see cref="Unit"/>, swallowing a benign shutdown-race so a failed leg completes rather than
    /// faulting the sequence. The pool IS the async boundary — nothing is <c>await</c>ed here.
    /// </summary>
    private static IObservable<Unit> RunVoid(Func<Task> leaf) =>
        DisposalPool
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
        // Only teardowns that have NOT settled are still in the dictionary — a settled one needs no
        // waiting and, by then, holds no cluster.
        var all = Pending.Values.ToArray();
        if (all.Length == 0)
            return;
        try
        {
            // Merge every pending completion; block on the merged stream's terminal notification.
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
