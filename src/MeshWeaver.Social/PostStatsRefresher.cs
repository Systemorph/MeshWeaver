using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Background service that periodically refreshes engagement stats on recently-
/// published posts. The hosting app supplies an <see cref="IStatsRefreshSource"/>
/// that yields which posts are due for refresh (typically "PublishedAt within the
/// last <see cref="SocialOptions.StatsRefreshWindow"/>"); this service dispatches
/// each to its platform publisher and applies the result via the bridge.
///
/// Kept separate from <see cref="ScheduledPostPublisher"/> because stats-fetch
/// cadence (30m default) is much coarser than publish cadence (60s default), and
/// failures in stats fetch are non-fatal — we shouldn't retry aggressively.
/// </summary>
public sealed class PostStatsRefresher : BackgroundService
{
    private readonly IStatsRefreshSource _source;
    private readonly IEnumerable<IPlatformPublisher> _publishers;
    private readonly IApprovalPublishBridge _bridge;
    private readonly SocialOptions _options;
    private readonly ILogger<PostStatsRefresher>? _logger;

    public PostStatsRefresher(
        IStatsRefreshSource source,
        IEnumerable<IPlatformPublisher> publishers,
        IApprovalPublishBridge bridge,
        SocialOptions options,
        ILogger<PostStatsRefresher>? logger = null)
    {
        _source = source;
        _publishers = publishers;
        _bridge = bridge;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("PostStatsRefresher started (interval {Interval}, window {Window})",
            _options.StatsTickInterval, _options.StatsRefreshWindow);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var target in _source.GetDueRefreshesAsync(_options.StatsRefreshWindow, stoppingToken))
                {
                    var publisher = _publishers.FirstOrDefault(p =>
                        string.Equals(p.Platform, target.Platform, StringComparison.OrdinalIgnoreCase));
                    if (publisher is null) continue;

                    try
                    {
                        var stats = await publisher.GetStatsAsync(target.Urn, target.Credential, stoppingToken);
                        await _bridge.ApplyStats(target.PostPath, stats).FirstAsync().ToTask(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "Stats refresh failed for {PostPath}", target.PostPath);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "PostStatsRefresher tick failed");
            }

            try { await Task.Delay(_options.StatsTickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>
/// What the stats refresher needs per post. The hosting app's implementation
/// scans the mesh for published posts in the refresh window and yields these.
/// </summary>
public sealed record StatsRefreshTarget(
    string PostPath,
    string Platform,
    string Urn,
    PlatformCredential Credential);

/// <summary>
/// Hosting-app callback that returns which posts need stats refreshed. Streamed
/// because the mesh query is async and we don't want to materialize a huge list.
/// </summary>
public interface IStatsRefreshSource
{
    IAsyncEnumerable<StatsRefreshTarget> GetDueRefreshesAsync(TimeSpan window, CancellationToken ct);
}
