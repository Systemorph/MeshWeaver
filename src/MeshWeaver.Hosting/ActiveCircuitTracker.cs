namespace MeshWeaver.Hosting;

/// <summary>
/// Counts the LIVE Blazor circuits this process is serving — the signal a shutting-down pod uses
/// to decide whether anyone is still working on it.
///
/// <para><b>Why this exists.</b> A rollout is surge-first (<c>maxUnavailable: 0</c>): the new pod
/// serves before the old one is removed, so HTTP never breaks. What DOES break is every session on
/// the old pod — k8s deletes it as soon as the replacement is ready, and the container's
/// <c>preStop</c> was a flat <c>sleep 15</c>, which drains the ingress upstream and nothing else.
/// Fifteen seconds later the silo goes down and every circuit on it dies mid-sentence. That is the
/// "the demo just crashed" report, and with a 6-hourly self-update poller it arrives
/// unannounced.</para>
///
/// <para>Grain placement is already on the right side of this: <c>MessageHubGrain</c> is
/// <c>[PreferLocalPlacement]</c>, so a circuit's hubs activate on the pod serving that circuit. Keep
/// the pod alive and its work keeps running; the only thing that kills it is the pod going away. So
/// the drain question is exactly "does this pod still have circuits?" — this counter answers it, and
/// <c>/drain</c> exposes the answer to the preStop hook.</para>
///
/// <para>Singleton and process-wide on purpose: circuits are scoped, the pod's shutdown decision is
/// not. Interlocked, because circuit open/close arrive on many threads.</para>
/// </summary>
public sealed class ActiveCircuitTracker
{
    private int count;

    /// <summary>Live circuits right now. Never negative.</summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>True when no circuit is left — the pod is free to stop without cutting anyone off.</summary>
    public bool Drained => Count == 0;

    /// <summary>Records a circuit opening. Called by the Blazor circuit handler that feeds it.</summary>
    public void Opened() => Interlocked.Increment(ref count);

    /// <summary>
    /// Records a circuit closing. Clamped at zero: a double-close (Blazor can report a circuit
    /// closed after a connection-down that already ended it) must never push the count negative —
    /// a negative count reads as "drained" forever after, which would hand back the very kill this
    /// exists to prevent.
    /// </summary>
    public void Closed()
    {
        int observed, updated;
        do
        {
            observed = Volatile.Read(ref count);
            if (observed == 0) return;
            updated = observed - 1;
        }
        while (Interlocked.CompareExchange(ref count, updated, observed) != observed);
    }
}
