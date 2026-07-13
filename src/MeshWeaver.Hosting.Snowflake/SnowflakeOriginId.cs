namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// The per-silo identity of this process's Snowflake event-log writer. Snowflake has no
/// LISTEN/NOTIFY, so cross-process change propagation polls <c>events.event_log</c>
/// (<see cref="SnowflakeChangeFeedPoller"/>) — but a silo's own writes are already published
/// in-process synchronously from Write/Delete, so replaying them from the poll would duplicate
/// every local notification. The <see cref="SnowflakeEventLogStore"/> stamps this id into the
/// <c>origin_id</c> column of every appended row, and the poller filters rows carrying its own
/// id back out, leaving only foreign silos' events on the live feed.
///
/// <para>Registered as a mesh-scoped DI singleton (in <c>SnowflakeExtensions</c>) so the store
/// and the poller share ONE value; its lifetime is the mesh's — a restart mints a fresh id,
/// which is correct because a restarted silo has no in-process subscribers that already saw
/// the pre-restart writes (startup catch-up is <c>EventLogReplayService</c>'s job).</para>
/// </summary>
public sealed record SnowflakeOriginId
{
    /// <summary>
    /// The unique origin value stamped on appended event-log rows — a fresh
    /// <see cref="Guid"/> in compact (<c>"N"</c>) form per instance.
    /// </summary>
    public string Value { get; init; } = Guid.NewGuid().ToString("N");
}
