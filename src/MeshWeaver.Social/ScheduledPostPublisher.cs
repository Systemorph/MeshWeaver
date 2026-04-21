using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Background service that publishes approved, due posts to their target platforms.
/// Ticks every <see cref="SocialOptions.PublishTickInterval"/>, drains due items
/// from the queue, and dispatches each to the matching <see cref="IPlatformPublisher"/>.
/// On success: applies the publish result via <see cref="IApprovalPublishBridge"/>.
/// On transient failure: retries with exponential backoff up to 3 times. On
/// permanent failure: records the error on the post via the bridge (no requeue).
///
/// The scheduler is NOT the source of truth for "what's due" — that's the queue.
/// The queue is populated primarily by <see cref="ApprovalToPublishHandler"/>, and
/// optionally by a sweep job that scans the mesh on startup (so posts approved
/// while the service was down don't get stranded).
/// </summary>
public sealed class ScheduledPostPublisher : BackgroundService
{
    private readonly IPublishQueue _queue;
    private readonly IEnumerable<IPlatformPublisher> _publishers;
    private readonly IApprovalPublishBridge _bridge;
    private readonly SocialOptions _options;
    private readonly ILogger<ScheduledPostPublisher>? _logger;

    public ScheduledPostPublisher(
        IPublishQueue queue,
        IEnumerable<IPlatformPublisher> publishers,
        IApprovalPublishBridge bridge,
        SocialOptions options,
        ILogger<ScheduledPostPublisher>? logger = null)
    {
        _queue = queue;
        _publishers = publishers;
        _bridge = bridge;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("ScheduledPostPublisher started (interval {Interval})", _options.PublishTickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = _queue.DrainDue(DateTimeOffset.UtcNow);
                foreach (var snapshot in due)
                {
                    await PublishWithRetryAsync(snapshot, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "ScheduledPostPublisher tick failed");
            }

            try { await Task.Delay(_options.PublishTickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishWithRetryAsync(PublishableSnapshot snapshot, CancellationToken ct)
    {
        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Platform, snapshot.Platform, StringComparison.OrdinalIgnoreCase));
        if (publisher is null)
        {
            _logger?.LogWarning("No IPlatformPublisher registered for {Platform} — dropping {PostPath}",
                snapshot.Platform, snapshot.PostPath);
            return;
        }

        var request = new PlatformPublishRequest(
            snapshot.PostPath, snapshot.AuthorHandle, snapshot.Text,
            snapshot.MediaUrls, snapshot.Credential);

        PublishResult? lastResult = null;
        for (var attempt = 1; attempt <= _options.MaxPublishAttempts; attempt++)
        {
            try
            {
                lastResult = await publisher.PublishAsync(request, ct);
                if (lastResult.Urn is not null && lastResult.Error is null)
                {
                    await _bridge.ApplyPublishAsync(snapshot.PostPath, lastResult, ct);
                    _logger?.LogInformation("Published {PostPath} → {Platform} {Urn}",
                        snapshot.PostPath, snapshot.Platform, lastResult.Urn);
                    return;
                }
                _logger?.LogWarning("Publish attempt {Attempt}/{Max} failed for {PostPath}: {Error}",
                    attempt, _options.MaxPublishAttempts, snapshot.PostPath, lastResult.Error ?? "no urn");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Publish attempt {Attempt}/{Max} threw for {PostPath}",
                    attempt, _options.MaxPublishAttempts, snapshot.PostPath);
                lastResult = new PublishResult(null, null, DateTimeOffset.UtcNow, Error: ex.Message);
            }

            if (attempt < _options.MaxPublishAttempts)
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(backoff, ct);
            }
        }

        // All attempts exhausted — record the failure on the post.
        if (lastResult is not null)
            await _bridge.ApplyPublishAsync(snapshot.PostPath, lastResult, ct);
    }
}
