using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Drains Orleans <see cref="TestCluster"/> disposals on a BACKGROUND pool instead of
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
/// <para><b>Fix.</b> No <c>await</c> on the teardown thread — hand the cluster to a
/// background task ("io pool") and return immediately, so the next class's cluster starts
/// right away and disposals drain concurrently. Every disposal is then awaited ONCE, at
/// the very end (process exit), bounded so a genuinely-wedged silo is abandoned rather
/// than hanging the wait. (A real mesh <c>IIoPool</c> isn't available at xUnit teardown,
/// so the background-task pool stands in for it.)</para>
/// </summary>
internal static class OrleansClusterDisposal
{
    private static readonly ConcurrentBag<Task> Pending = new();

    /// <summary>
    /// Hand a cluster's disposal to a background pool thread — NEVER awaited on the
    /// teardown thread. Null-safe and best-effort (the cluster is on its way down; a
    /// shutdown-race exception is benign and swallowed).
    ///
    /// <para><b>Graceful stop BEFORE dispose — the root fix for the Autofac
    /// disposed-<c>LifetimeScope</c> straggler (task #20).</b> <see cref="TestCluster.DisposeAsync"/>
    /// disposes the Orleans CLIENT host through the generic <c>IHost.DisposeAsync()</c>, which
    /// only disposes the service provider and NEVER runs <c>StopAsync()</c>. So the client's
    /// connection message pump (<c>Orleans.Runtime.Messaging.Connection.ProcessIncoming</c>) keeps
    /// deserializing in-flight messages while the client's Autofac container is being torn down;
    /// <c>CodecProvider</c> then lazily resolves a serialization codec from the already-disposed
    /// <c>LifetimeScope</c> and throws <see cref="ObjectDisposedException"/> ("Instances cannot be
    /// resolved and nested lifetimes cannot be created from this LifetimeScope … already disposed").
    /// Unobserved under CI load, that reaches <see cref="AppDomain.UnhandledException"/> — the ONLY
    /// channel xUnit v3's in-proc console runner hooks — and is reported as an anonymous
    /// "Catastrophic failure" that reds a 123/123-green shard. (The silos are already graceful:
    /// <c>InProcessSiloHandle.DisposeAsync</c> runs <c>StopSiloAsync(graceful)</c> first; only the
    /// client host skips its <c>StopAsync</c>.) The pump is purely Orleans-internal, so the mesh
    /// drain added in #231 (<c>MeshTeardownHostedService</c>) never covered it — and because the
    /// client's <c>StopAsync</c> is skipped, that drain never even ran on the client.</para>
    ///
    /// <para><b>The residual gap #244 left (task #21): the CLIENT is stopped by a DIFFERENT method.</b>
    /// <see cref="TestCluster.StopAllSilosAsync"/> stops the SILOS only — it does NOT run the client
    /// host's <c>StopAsync</c>. So after #244 the client connection pump was still live when
    /// <c>DisposeAsync</c> disposed the client's Autofac container → the disposed-<c>LifetimeScope</c>
    /// straggler kept escaping under CI load (it re-flaked shard 3 of #244 and #258). The client's
    /// graceful stop is <see cref="TestCluster.StopClusterClientAsync"/> — the exact <c>StopAsync</c>
    /// that <c>TestCluster.DisposeAsync</c> skips. We call it FIRST (client stops initiating), THEN
    /// stop the silos, THEN dispose — so by dispose time NO connection pump (client or silo) is left
    /// resolving a codec. Measured by <c>TeardownStragglerCapturer</c>: the disposed-scope throw count
    /// per Orleans.Test run goes to 0, and stays 0 across the shard sequence (the cross-project mode).</para>
    /// </summary>
    public static void DisposeInBackground(TestCluster? cluster)
    {
        if (cluster is null)
            return;
        Pending.Add(Task.Run(async () =>
        {
            // Graceful ordered stop BEFORE any Autofac container disposes: CLIENT host first
            // (StopClusterClientAsync — the StopAsync that TestCluster.DisposeAsync SKIPS, the exact
            // gap that left the client pump resolving a codec from a disposing scope), THEN every silo,
            // THEN the dispose. Each step guarded separately so a stop-race can't skip the steps that
            // follow. All benign here — the cluster is going down; a genuinely surviving straggler is
            // still NAMED by TeardownStragglerCapturer.
            try { await cluster.StopClusterClientAsync(); }
            catch { /* client stop-race on a cluster going down — the silo stop + dispose still run */ }
            try { await cluster.StopAllSilosAsync(); }
            catch { /* silo stop-race — DisposeAsync still completes teardown */ }
            try { await cluster.DisposeAsync(); }
            catch { /* shutdown-race / zombie silo — benign, the cluster is going down */ }
        }));
    }

    /// <summary>
    /// Wait (bounded) for every background disposal. Called once at the very end, so the
    /// process doesn't exit mid-teardown while still leaving a wedged silo free to abandon.
    /// </summary>
    public static void WaitAll(TimeSpan timeout)
    {
        try { Task.WaitAll(Pending.ToArray(), timeout); }
        catch { /* best-effort — a wedged silo is abandoned at process exit */ }
    }

    // The "very end": after all tests + collections, the test host exits → drain the pool.
    // By now the disposals have had the whole run to complete concurrently, so this is a
    // short final flush, not a long stall.
    [ModuleInitializer]
    public static void Init() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WaitAll(TimeSpan.FromSeconds(20));
}
