namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Options governing the lifecycle of remote-subscriber sync streams.
/// </summary>
public class SyncStreamOptions
{
    /// <summary>
    /// Interval between HeartBeatEvents posted to the owner hub to keep its grain alive. The
    /// heartbeat also detects a PERMANENTLY-gone owner: a recycled/idle grain REACTIVATES on the
    /// heartbeat post and acks, so only a gone owner (e.g. a one-shot <c>_Activity/import-*</c> lock
    /// whose dedicated import hub was disposed) returns a terminal NotFound — which tears the
    /// keep-alive down so we never heartbeat a dead owner in an endless NotFound loop. Recycled-grain
    /// RE-SUBSCRIPTION (picking up a fresh snapshot after a restart) is driven by the mesh change feed,
    /// not the heartbeat. Default: 45 seconds. Tests use a much shorter interval to exercise the
    /// teardown/resubscribe paths without waiting.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Coalescing window for change-feed-triggered resubscribes. The mesh change feed fires
    /// one event per owner write; high-frequency owner writes (e.g. a per-HTTP-request
    /// <c>_UserActivity</c> update) produce a BURST of events. A single resubscribe already
    /// fetches the LATEST owner snapshot regardless of how many writes fired, so per-event
    /// resubscribe is redundant AND harmful — each one synchronously creates a fresh
    /// <c>sync/{id}</c> hub on the shared cache hub's single-threaded action block, and a
    /// burst can starve it so it cannot ack other SubscribeRequests within the callback
    /// timeout (the cache-hub wedge). The change-feed observable is throttled by this window
    /// so a burst collapses to ONE fresh-snapshot resubscribe. Recreate detection is
    /// preserved — a genuine owner restart still triggers a resubscribe, just debounced.
    /// Default: 1 second. Tests use a much shorter window to verify coalescing without waiting.
    /// </summary>
    public TimeSpan ChangeFeedResubscribeWindow { get; set; } = TimeSpan.FromSeconds(1);
}
