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
    /// </summary>
    public static void DisposeInBackground(TestCluster? cluster)
    {
        if (cluster is null)
            return;
        Pending.Add(Task.Run(async () =>
        {
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
