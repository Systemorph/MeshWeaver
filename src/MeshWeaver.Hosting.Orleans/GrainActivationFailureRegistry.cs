using System.Collections.Concurrent;
using MeshWeaver.Mesh.Services;

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
///
/// <para><b>Invalidation.</b> The registry also subscribes to the
/// <see cref="IMeshChangeFeed"/> invalidation broadcast (the same signal a recycle —
/// <c>MeshOperations.RecycleCore</c> — and every post-commit write publish, relayed
/// cross-silo by <c>PathCacheInvalidatorGrain</c>): a change event for a path clears any
/// stored activation error for that grain key. A recycled / just-written node must get a
/// completely fresh activation attempt — without this, <see cref="RoutingGrain"/>'s
/// NACK fallback served the STALE pre-recycle error text (e.g. a compile failure that was
/// already fixed) to every sender until the grain happened to activate cleanly once.</para>
/// </summary>
public sealed class GrainActivationFailureRegistry : IDisposable
{
    // grainKey (node address path) → last activation error message. Instance, never static.
    private readonly ConcurrentDictionary<string, string> _lastError =
        new(StringComparer.Ordinal);

    private readonly IDisposable? _changeFeedSubscription;

    /// <summary>
    /// Creates the registry, optionally attached to the mesh change feed so
    /// invalidation broadcasts (recycle / post-commit writes) clear stale errors.
    /// </summary>
    /// <param name="changeFeed">The mesh change feed to observe; when null (minimal
    /// fixtures) the registry still works, it just never auto-clears on broadcasts.</param>
    public GrainActivationFailureRegistry(IMeshChangeFeed? changeFeed = null)
    {
        _changeFeedSubscription = changeFeed?.Subscribe(evt => Clear(evt.Path));
    }

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

    /// <inheritdoc />
    public void Dispose() => _changeFeedSubscription?.Dispose();
}
