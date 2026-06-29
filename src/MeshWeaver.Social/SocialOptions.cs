using System;

namespace MeshWeaver.Social;

/// <summary>
/// Knobs for the Social publishing subsystem. Populate from configuration under
/// <c>"Social"</c> (e.g. <c>Social:PublishTickInterval</c>).
/// </summary>
public sealed class SocialOptions
{
    /// <summary>
    /// How often <see cref="ScheduledPostPublisher"/> drains the publish queue.
    /// Default 60s — LinkedIn/X schedules rarely need second-level precision, and a
    /// tighter interval burns rate limit on quiet mesh.
    /// </summary>
    public TimeSpan PublishTickInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Max publish attempts per post before marking as failed. Default 3.</summary>
    public int MaxPublishAttempts { get; set; } = 3;

    /// <summary>
    /// How often <see cref="PostStatsRefresher"/> sweeps recently-published posts.
    /// Default 30m — LinkedIn stats update with a lag of a few minutes anyway.
    /// </summary>
    public TimeSpan StatsTickInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Cutoff for stats refresh: only posts published within the last N days are polled.
    /// Default 30 days — engagement tails off after that and quota is better spent on fresh posts.
    /// </summary>
    public TimeSpan StatsRefreshWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Max concurrent stats-refresh calls per tick. Default 8 — bounds the
    /// per-tick wall-clock by roughly N×api-latency rather than serial sum.
    /// Without this, a 30m tick processing 100 posts at 500 ms/call serially
    /// would take 50 s — fine for that interval, but 4× as many posts and the
    /// next tick is overshot. Bound it explicitly so the cadence stays stable.
    /// </summary>
    public int StatsRefreshDegreeOfParallelism { get; set; } = 8;

    /// <summary>
    /// After a stats-refresh failure for a specific (platform, postPath), skip
    /// that target on subsequent ticks for this long. Default 15 m — long
    /// enough to ride out a typical platform 5xx blip + retry cooldown,
    /// short enough that a recovered target rejoins normal cadence within the
    /// hour. Set to <see cref="TimeSpan.Zero"/> to disable backoff entirely.
    /// </summary>
    public TimeSpan StatsRefreshFailureBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often <see cref="PastPostIngestJob"/> fetches new historic posts from platforms.
    /// Default 24h — "historic" is for backfill + slow sync of posts made outside the mesh.
    /// </summary>
    public TimeSpan PastPostIngestInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Max history items per platform per ingest run. Default 200.</summary>
    public int PastPostIngestPageSize { get; set; } = 200;
}
