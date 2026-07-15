namespace MeshWeaver.AI.Delegation;

/// <summary>
/// Tunable thresholds for the parent-hub delegation heartbeat watcher
/// (<see cref="DelegationHandlers.HandleHeartbeatTick"/>). Registered as a
/// mesh-scoped singleton and resolved from the thread hub's ServiceProvider;
/// when unregistered the watcher falls back to
/// <see cref="DelegationHandlers.DefaultHeartbeatOptions"/> (these same defaults),
/// so production needs no explicit registration. A test registers an instance with
/// small values to exercise the cold-start / stall branches deterministically
/// without long wall-clock waits.
///
/// <para><b>Two distinct windows.</b> Time-to-first-activity and the gap between
/// activity stamps are different phenomena and must not share one threshold:
/// a reasoning model, a cold provider endpoint, or a slow first tool legitimately
/// takes tens of seconds to its FIRST delta, which is not a stall. See the branch
/// in <see cref="DelegationHandlers.HandleHeartbeatTick"/>.</para>
/// </summary>
public sealed record DelegationHeartbeatOptions
{
    /// <summary>
    /// Inter-activity timeout: the maximum gap BETWEEN <see cref="Thread.LastActivityAt"/>
    /// stamps once the sub-agent has started streaming, before a silent (stalled)
    /// stream is cancelled. Per-thread override: <see cref="Thread.HeartbeatTimeout"/>.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Settle grace from <see cref="Thread.ExecutionStartedAt"/> applied ONLY to the
    /// stalled branch (a sub-thread that was active and then went silent) so a
    /// momentary gap right after the first token is never treated as a stall.
    /// </summary>
    public TimeSpan ColdStartGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// First-activity budget: how long a sub-thread that has produced NO activity yet
    /// (<see cref="Thread.LastActivityAt"/> is null) may spend in first-token latency
    /// — agent allocation + model time-to-first-token + first tool round-trip — before
    /// it is treated as hung. Deliberately far larger than <see cref="HeartbeatTimeout"/>:
    /// judging first-token latency by the inter-activity timeout is what killed a
    /// live-but-slow sub-agent before it ever emitted (the "sub-thread never started"
    /// symptom). Backstopped by the parent's 10-min delegate_to_agent timeout.
    /// Per-thread override: <see cref="Thread.FirstActivityBudget"/>.
    /// </summary>
    public TimeSpan FirstActivityBudget { get; init; } = TimeSpan.FromSeconds(60);
}
