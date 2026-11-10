namespace MeshWeaver.Hosting;

/// <summary>
/// Measures how long a work item waits in the ThreadPool's queue before it starts — the quantity
/// Orleans' <c>LocalSiloHealthMonitor</c> reports as ".NET Thread Pool execution stalled for N s"
/// (MeshWeaver#5388). One probe item is in the queue at a time: <see cref="Poll"/> queues a fresh one
/// as soon as the previous one has started, and otherwise answers how long the outstanding one has
/// been waiting.
///
/// <para>🚨 <b>Why the counters alone were not enough.</b> The <c>[POOLSTALL]</c> capture first fired
/// only on a heartbeat period in which the pool completed NOTHING with work queued at both ends. On
/// memex-cloud pod <c>…5cf7b87497-4l9xx</c> (2026-11-09 22:55Z) Orleans logged stalls of 15.6&#160;s and
/// 10.1&#160;s while the heartbeat read <c>poolPending=11196 poolCompleted(+9310)</c> and
/// <c>poolPending=11813 poolCompleted(+1284)</c>: a burst of ~11,000 queued items that 32 threads were
/// still draining. Each period completed SOMETHING, so the capture never fired, and five of the six
/// stalls logged after the capture shipped left no frames. A queue that drains too slowly delays
/// every item behind it exactly as a parked pool does; this probe measures that delay directly.</para>
///
/// <para>Owned by the heartbeat's dedicated thread, which is the only caller of <see cref="Poll"/>.
/// The started flag is the one value shared with a pool thread, written once per probe.</para>
/// </summary>
/// <param name="enqueue">Queues a callback on the ThreadPool. The production one is
/// <see cref="ThreadPool.UnsafeQueueUserWorkItem(WaitCallback, object?)"/>, which puts the item on the
/// GLOBAL queue — where a timer's callback, and so the Orleans health monitor's, also waits.</param>
internal sealed class ThreadPoolQueueProbe(Action<Action> enqueue)
{
    /// <summary>A probe on the real ThreadPool.</summary>
    public ThreadPoolQueueProbe()
        : this(static callback => ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), callback))
    {
    }

    private TimeSpan? queuedAt;
    private Probe? outstanding;

    /// <summary>
    /// Queues a probe if none is outstanding, and answers how long the outstanding one has waited
    /// without starting — <see cref="TimeSpan.Zero"/> when it has started (or was just queued).
    /// </summary>
    /// <param name="now">The heartbeat's monotonic clock.</param>
    /// <returns>How long the outstanding probe has been queued without starting.</returns>
    public TimeSpan Poll(TimeSpan now)
    {
        if (outstanding is { } probe && queuedAt is { } since)
        {
            if (Volatile.Read(ref probe.Started) == 0)
                return now - since;
            outstanding = null;
        }

        var next = new Probe();
        queuedAt = now;
        outstanding = next;
        enqueue(() => Volatile.Write(ref next.Started, 1));
        return TimeSpan.Zero;
    }

    private sealed class Probe
    {
        public int Started;
    }
}
