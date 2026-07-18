using System.Collections.Concurrent;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Mesh-scoped registry of the LAST activation failure observed for each per-node-hub
/// grain, keyed by the grain's primary key (== the node's address path).
///
/// <para><b>The defect this closes (issue #464, Defect 3).</b> When a per-node
/// <see cref="MessageHubGrain"/> is stuck in a <i>persistent</i> activation-fault loop —
/// its activation faults almost instantly (a broken NodeType compile can't materialise a
/// hub configuration), so <c>DeactivateOnIdle</c> fires and the grain's alive window is
/// ≈ 0 — every delivery (and every one of <see cref="RoutingGrain.DeliverToGrainWithRetry"/>'s
/// transient retries) lands in a deactivation window. Orleans then answers the raw
/// <c>OrleansMessageRejectionException</c> ("tried to forward message … after
/// \"DeactivateOnIdle was called.\" … Rejecting now."). The retry helper NACKs THAT raw
/// text to the sender, so the GUI sees Orleans internals instead of the real cause
/// ("Compilation failed for 'X': CS1501 …") — which the grain resolved into
/// <see cref="MessageHubGrain"/>'s <c>_hubReadyRaw.OnError(ex)</c> on every activation but
/// which never reaches the sender because <c>DeliverMessage</c> never runs in the loop.</para>
///
/// <para><b>How it's used.</b> <see cref="MessageHubGrain"/> records the real activation
/// error here on every faulted / NACK-fallback activation, and clears it on a genuine
/// successful activation. <see cref="RoutingGrain"/>, on the exhausted-retry / non-transient
/// NACK path, falls back to the stored error for the grain key instead of the raw rejection
/// text — so the sender's <c>Observe(...)</c> fires <c>OnError</c> with a deterministic,
/// actionable cause and the GUI's resubscribe loop stops spinning.</para>
///
/// <para>Mesh-scoped singleton (registered in <c>AddOrleansMeshServices</c>): one instance
/// shared across the silo's grains, with an instance map only — NO static state
/// (<c>NoStaticState.md</c>). A process restart clears it, which is correct: a redeployed /
/// recompiled fix re-activates cleanly and the stale error is gone.</para>
/// </summary>
public sealed class GrainActivationFailureRegistry
{
    // grainKey (node address path) → last activation error message. Instance, never static.
    private readonly ConcurrentDictionary<string, string> _lastError =
        new(StringComparer.Ordinal);

    /// <summary>Record the real activation error for <paramref name="grainKey"/>.</summary>
    public void Record(string grainKey, string error)
    {
        if (string.IsNullOrEmpty(grainKey) || string.IsNullOrEmpty(error))
            return;
        _lastError[grainKey] = error;
    }

    /// <summary>Clear a stored error after the grain activated successfully.</summary>
    public void Clear(string grainKey)
    {
        if (string.IsNullOrEmpty(grainKey))
            return;
        _lastError.TryRemove(grainKey, out _);
    }

    /// <summary>The last recorded activation error for <paramref name="grainKey"/>, or
    /// <c>null</c> when none is known (the grain never faulted, or activated cleanly).</summary>
    public string? TryGet(string grainKey)
        => !string.IsNullOrEmpty(grainKey) && _lastError.TryGetValue(grainKey, out var e)
            ? e
            : null;
}
