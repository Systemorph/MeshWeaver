using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Reactive;
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

    // BackgroundService boundary — single .ToTask() bridge for the framework's Task
    // contract. Inside is one observable chain; the rest of the file is reactive.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation(
            "PostStatsRefresher started (interval {Interval}, window {Window}, parallelism {Dop}, backoff {Backoff})",
            _options.StatsTickInterval, _options.StatsRefreshWindow,
            _options.StatsRefreshDegreeOfParallelism, _options.StatsRefreshFailureBackoff);

        var dop = Math.Max(1, _options.StatsRefreshDegreeOfParallelism);

        return Observable.Interval(_options.StatsTickInterval)
            .StartWith(-1L)
            .SelectMany(_ => _source.GetDueRefreshesAsync(_options.StatsRefreshWindow, stoppingToken)
                .ToObservableSequence()
                .Select(target => ProcessTarget(target))
                .Merge(maxConcurrent: dop)
                .Catch((Exception ex) =>
                {
                    if (ex is OperationCanceledException) return Observable.Empty<Unit>();
                    _logger?.LogError(ex, "PostStatsRefresher tick failed");
                    return Observable.Empty<Unit>();
                }))
            .ToTask(stoppingToken);
    }

    private IObservable<Unit> ProcessTarget(StatsRefreshTarget target)
    {
        // Per-target backoff: skip if last failure is within the backoff window.
        var backoffKey = $"{target.Platform}|{target.PostPath}";
        if (_failureBackoff.TryGetValue(backoffKey, out var lastFail)
            && DateTimeOffset.UtcNow - lastFail < _options.StatsRefreshFailureBackoff)
        {
            return Observable.Return(Unit.Default);
        }

        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Platform, target.Platform, StringComparison.OrdinalIgnoreCase));
        if (publisher is null) return Observable.Return(Unit.Default);

        return Observable.FromAsync(ct => publisher.GetStatsAsync(target.Urn, target.Credential, ct))
            .SelectMany(stats => _bridge.ApplyStats(target.PostPath, stats))
            .Do(_ =>
            {
                // Success — clear any prior failure so the target rejoins normal cadence.
                _failureBackoff.TryRemove(backoffKey, out var _ignored);
            })
            .Select(_ => Unit.Default)
            .Catch((Exception ex) =>
            {
                if (ex is OperationCanceledException) return Observable.Empty<Unit>();
                _failureBackoff[backoffKey] = DateTimeOffset.UtcNow;
                _logger?.LogWarning(ex,
                    "Stats refresh failed for {PostPath} (next retry no sooner than {NextRetry:O})",
                    target.PostPath, DateTimeOffset.UtcNow + _options.StatsRefreshFailureBackoff);
                return Observable.Return(Unit.Default);
            });
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
