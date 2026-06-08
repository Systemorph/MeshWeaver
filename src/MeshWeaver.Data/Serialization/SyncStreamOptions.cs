namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Options governing the lifecycle of remote-subscriber sync streams.
/// </summary>
public class SyncStreamOptions
{
    /// <summary>
    /// Interval between HeartBeatEvents posted to the owner hub. Doubles as the resubscribe
    /// detection window: when the owner is gone (recycled / idle / crashed), the next
    /// heartbeat returns DeliveryFailure and the subscriber re-issues SubscribeRequest to
    /// pick up a fresh snapshot from the new grain. Default: 45 seconds.
    /// Tests use a much shorter interval to verify the resubscribe path without waiting.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a freshly-subscribed remote stream waits for its initial state
    /// (a <c>ChangeType.Full</c>) before resubscribing to pull a fresh one. A
    /// dropped initial Full — the owner Acked the <c>SubscribeRequest</c> but its
    /// first <c>DataChangedEvent</c> never landed (lost in routing under load) —
    /// otherwise has NO recovery within a test's lifetime: the heartbeat is
    /// keepalive-only and fires at <see cref="HeartbeatInterval"/> (45 s), and the
    /// change feed resubscribes only on owner Created/Deleted. The subscriber would
    /// sit dark until its consumer's timeout, so every sub-45 s test flakes
    /// (msg-trace on a bulk run: 1092 SubscribeRequests, 0 HeartBeatEvents — the
    /// 45 s path never even ran). This watchdog bounds that recovery to a few
    /// seconds. Kept comfortably above a normal cold-activation round-trip (~1-2 s)
    /// so it doesn't fire for a Full that is merely in-flight; the single-flight
    /// resubscribe guard prevents a storm. Default: 5 seconds. Tests can shorten
    /// it. Set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    public TimeSpan InitialStateRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
