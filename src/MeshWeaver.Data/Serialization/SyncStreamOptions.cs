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
