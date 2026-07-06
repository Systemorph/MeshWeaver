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

    /// <summary>
    /// Settle window between the coalesced change-feed pulse and the version staleness check.
    /// For a <c>MeshNode</c>-typed stream, a resubscribe only fires when — this long after the
    /// pulse — the stream still has NOT received the node version the change feed announced:
    /// a healthy subscriber gets every owner write through its own subscription (version catches
    /// up; no resubscribe), while a subscriber orphaned by a recycled owner grain never does
    /// (version stays behind; fresh SubscribeRequest). The window absorbs ordinary delivery skew
    /// between the change-feed publish and the sync emission. Default: 1 second.
    /// </summary>
    public TimeSpan ChangeFeedStalenessGrace { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Grace window <c>RouteStreamMessage</c> waits for a stream's per-stream <c>sync/{id}</c> sub-hub
    /// to register before dropping an inbound <see cref="DataChangedEvent"/>/stream message for it.
    ///
    /// <para>The owner's FIRST <c>Full</c> can race AHEAD of the subscriber's sync-hub creation (the
    /// <c>SynchronizationStream</c> ctor's <c>Host.GetHostedHub(sync/{id})</c> runs on a different
    /// action-block turn than the one routing the Full): the Full then arrives before <c>sync/{id}</c>
    /// is in the subscriber's <see cref="MeshWeaver.Messaging.HostedHubsCollection"/> and — pre-fix — is
    /// dropped, so that region renders blank ("random subset / blank first load" in the React portal).
    /// Instead of dropping immediately, the router waits reactively (on
    /// <see cref="MeshWeaver.Messaging.HostedHubsCollection.HubAdded"/>, purpose-built for this) up to
    /// this window for the sub-hub to appear, then re-delivers. A stream that is GENUINELY gone
    /// (disposed circuit, released read stream) never registers a sub-hub, so its message is still
    /// dropped — just this window later — with the same diagnostic. Bounded + self-disposing, so it
    /// never accumulates: it distinguishes "subscribed-but-not-yet-created" (HubAdded fires) from
    /// "gone" (window elapses). Default: 5 seconds; a cold sync-hub activation on a loaded CI runner
    /// can lag several hundred ms behind the first Full. Tests can shorten it.</para>
    /// </summary>
    public TimeSpan SyncHubRegistrationGrace { get; set; } = TimeSpan.FromSeconds(5);
}
