namespace MeshWeaver.Mesh.Services;

/// <summary>
/// A declared HOLE in a cross-process change feed: between <see cref="LostAt"/> and
/// <see cref="ResumedAt"/> the feed could not deliver, and whatever was committed in that window
/// will never arrive as a <see cref="DataChangeNotification"/>.
///
/// <para><b>Why this exists.</b> A durable backend's cross-process feed is not a log. PostgreSQL's
/// <c>LISTEN/NOTIFY</c> delivers a notification only to sessions that are listening at the instant
/// it is sent, and never replays one — so when the listener's connection drops and is re-opened,
/// every commit in between is gone. Before this signal existed nothing said so, and every cache
/// that is retracted ONLY by the feed (path resolution, the stream cache's negative windows, live
/// query results, a per-node hub's own-node mirror) stayed stale on that replica with no recovery
/// path (Systemorph/MeshWeaver.Plugins#3000).</para>
///
/// <para><b>What a consumer does with it: re-read its authoritative state.</b> A gap names no path,
/// so a consumer cannot know WHAT changed — only that anything may have. The answer is the same
/// one a consumer gives to an unknown notification, applied to everything it holds: drop derived
/// state and re-ask the store (re-run a live query, re-read an own node, clear a resolution cache).
/// Never a timer that polls in case a gap happened — the producer KNOWS when it lost the
/// connection, and this is where it says so.</para>
///
/// <para>Published only for a gap after the feed had first come up: a feed that has never
/// delivered had no subscriber state to invalidate, and the startup window has its own readiness
/// signal (the listener's <c>Listening</c>).</para>
/// </summary>
/// <param name="Source">Which feed lost delivery (e.g. <c>postgresql:mesh_node_changes</c>), for logs.</param>
/// <param name="LostAt">When the producer observed the loss — the last instant delivery was known good is earlier still.</param>
/// <param name="ResumedAt">When delivery resumed: notifications after this instant arrive normally.</param>
/// <param name="Reason">Why delivery was lost (the connection error), or <see langword="null"/>.</param>
public sealed record ChangeFeedGap(
    string Source,
    DateTimeOffset LostAt,
    DateTimeOffset ResumedAt,
    string? Reason = null)
{
    /// <summary>
    /// This gap as a RE-QUERY TRIGGER for a live-query pipeline whose re-run is driven by
    /// <see cref="DataChangeNotification"/>s: a path-less <see cref="DataChangeKind.Updated"/>
    /// carrying the gap as its entity. Merge it AFTER the pipeline's relevance filters — a gap is
    /// relevant to every live query, and a path filter would discard it.
    ///
    /// <para>Only a trigger: such pipelines take their rows from the re-query alone (never from a
    /// notification's entity — #1250), so nothing reads this one's path or entity as data.</para>
    /// </summary>
    /// <returns>The trigger notification.</returns>
    public DataChangeNotification ToRequeryTrigger()
        => new(string.Empty, DataChangeKind.Updated, this, ResumedAt);
}
