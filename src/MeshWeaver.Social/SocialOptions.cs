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
    /// How often <see cref="PastPostIngestJob"/> fetches new historic posts from platforms.
    /// Default 24h — "historic" is for backfill + slow sync of posts made outside the mesh.
    /// </summary>
    public TimeSpan PastPostIngestInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Max history items per platform per ingest run. Default 200.</summary>
    public int PastPostIngestPageSize { get; set; } = 200;
}
