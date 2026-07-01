namespace MeshWeaver.Hosting;

/// <summary>
/// Tuning knobs for <see cref="MeshNodeStreamCache"/>'s idle release of per-path READ
/// streams. Optional — when not registered in DI the defaults below apply. The values
/// mirror the cache's WRITE-side queue retention (<c>_updateQueues</c>' 10-minute
/// sliding expiration): a read entry whose shared stream has had no subscriber and no
/// read/write hit for <see cref="ReadStreamIdleExpiration"/> is released — its upstream
/// <c>SubscribeRequest</c> is closed (the owner-side mirror unsubscribes and the 45s
/// sync-stream heartbeat dies) and the next read transparently re-opens it. An entry
/// with a live subscriber is NEVER released, regardless of age.
/// </summary>
public sealed record MeshNodeStreamCacheOptions
{
    /// <summary>
    /// Sliding idle window for a cached read stream. Every <c>GetStream</c> subscription,
    /// unsubscription and <c>Update</c> on the path refreshes the window; only an entry
    /// with ZERO live subscribers that stayed untouched for the whole window is released.
    /// Matches the write-queue sliding expiration (10 minutes).
    /// </summary>
    public TimeSpan ReadStreamIdleExpiration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the idle sweep scans the read cache. The sweep only ever CLOSES idle
    /// entries — it never re-subscribes anything (the 2026-06-08 rule); re-opening is
    /// always driven by the next natural read.
    /// </summary>
    public TimeSpan ReadStreamSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
}
