namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// 🚨 <b>The router's OWN bound on a memory-stream post fired — issue #2322.</b> Raised by
/// <c>RoutingGrain.PostToStreamCore</c>'s Rx guard, and by nothing else.
///
/// <para><b>Why it needs a type of its own.</b> The guard bounds a post whose only await is a grain
/// call to <c>IMemoryStreamQueueGrain.Enqueue</c>, which Orleans already bounds at its own 30 s
/// <c>ResponseTimeout</c> — so the leg can produce a <see cref="TimeoutException"/> for two
/// completely different reasons: <i>the transport gave up</i>, or <i>the post never completed at
/// all</i>. The bare <c>Observable.Timeout(dueTime, scheduler)</c> overload raises the same plain
/// <see cref="TimeoutException"/> as the first case, so the catch arm could not tell them apart and
/// printed the GUARD's budget for both. Production consequently reported <i>"did not complete within
/// 00:01:00"</i> about a leg that had in fact died and reported promptly, at ~30 s, naming the
/// wedged <c>memorystreamqueue</c> activation — a wrong number that sent triage looking for a double
/// publish and 30 s of avoidable latency, neither of which existed.</para>
///
/// <para>🚨 <b>It stays a <see cref="TimeoutException"/> deliberately.</b> Every classifier above
/// this leg (<c>RoutingGrain.IsTransientFailure</c>,
/// <c>OrleansRoutingService.IsTransientFailure</c>, <c>ClassifyDeliveryException</c>) matches
/// <see cref="TimeoutException"/> by type; narrowing that would silently change how a
/// never-completing post is retried and reported. This carries only the DIAGNOSTIC distinction.</para>
/// </summary>
/// <param name="addressPath">The destination whose post did not complete.</param>
/// <param name="budget">The router's own bound, i.e. the budget that was exceeded.</param>
internal sealed class StreamPostGuardTimeoutException(string addressPath, TimeSpan budget)
    : TimeoutException(
        $"The stream-routed post to '{addressPath}' neither completed nor faulted within the router's "
        + $"own {budget} bound. The post's only await is an Orleans grain call which Orleans bounds at "
        + "its 30s ResponseTimeout, so exceeding this bound means the leg is dead somewhere that "
        + "bound cannot see.")
{
    /// <summary>The destination whose post did not complete.</summary>
    public string AddressPath { get; } = addressPath;

    /// <summary>The router's own bound — never the transport's.</summary>
    public TimeSpan Budget { get; } = budget;
}
