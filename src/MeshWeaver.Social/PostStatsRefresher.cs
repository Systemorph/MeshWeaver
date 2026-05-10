using System;
using System.Collections.Concurrent;
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
/// Targets are processed in parallel (bounded by
/// <see cref="SocialOptions.StatsRefreshDegreeOfParallelism"/>) so a slow API
/// or large refresh window can't let a single tick overshoot the next interval.
///
/// Per-target failures are tracked and skipped for
/// <see cref="SocialOptions.StatsRefreshFailureBackoff"/> after the most recent
/// failure — keeps a degraded platform from generating thousands of repeat warnings
/// every tick while the underlying issue is fixed.
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

    // Per-target last-failure timestamps. Targets that failed within
    // SocialOptions.StatsRefreshFailureBackoff are skipped on subsequent ticks
    // to avoid hammering a degraded platform with the same failing call every
    // tick interval.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failureBackoff =
        new(StringComparer.Ordinal);

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
        _logger?.LogInformation(
            "PostStatsRefresher started (interval {Interval}, window {Window}, parallelism {Dop}, backoff {Backoff})",
            _options.StatsTickInterval, _options.StatsRefreshWindow,
            _options.StatsRefreshDegreeOfParallelism, _options.StatsRefreshFailureBackoff);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Materialise the due-refresh set first so we can drive
                // Parallel.ForEachAsync with a known-size IAsyncEnumerable.
                // The set is typically small (recent-window only).
                var dop = Math.Max(1, _options.StatsRefreshDegreeOfParallelism);
                await Parallel.ForEachAsync(
                    _source.GetDueRefreshesAsync(_options.StatsRefreshWindow, stoppingToken),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = dop,
                        CancellationToken = stoppingToken
                    },
                    async (target, ct) => await ProcessTargetAsync(target, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "PostStatsRefresher tick failed");
            }

            try { await Task.Delay(_options.StatsTickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async ValueTask ProcessTargetAsync(StatsRefreshTarget target, CancellationToken ct)
    {
        // Per-target backoff: skip if last failure is within the backoff window.
        var backoffKey = $"{target.Platform}|{target.PostPath}";
        if (_failureBackoff.TryGetValue(backoffKey, out var lastFail)
            && DateTimeOffset.UtcNow - lastFail < _options.StatsRefreshFailureBackoff)
        {
            return;
        }

        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Platform, target.Platform, StringComparison.OrdinalIgnoreCase));
        if (publisher is null) return;

        try
        {
            var stats = await publisher.GetStatsAsync(target.Urn, target.Credential, ct);
            await _bridge.ApplyStats(target.PostPath, stats).FirstAsync().ToTask(ct);
            // Success — clear any prior failure so the target rejoins normal cadence.
            _failureBackoff.TryRemove(backoffKey, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failureBackoff[backoffKey] = DateTimeOffset.UtcNow;
            _logger?.LogWarning(ex,
                "Stats refresh failed for {PostPath} (next retry no sooner than {NextRetry:O})",
                target.PostPath, DateTimeOffset.UtcNow + _options.StatsRefreshFailureBackoff);
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
