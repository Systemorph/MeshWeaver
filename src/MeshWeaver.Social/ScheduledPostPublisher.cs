using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
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

    // Controlled I/O pool (Http resource class). The genuinely-async publish leaf
    // routes through _http.Invoke so the network round-trip runs off the hub
    // scheduler and is bounded. Falls back to the stateless unbounded pool when
    // no registry is wired (tests).
    private readonly IIoPool _http;

    public ScheduledPostPublisher(
        IPublishQueue queue,
        IEnumerable<IPlatformPublisher> publishers,
        IApprovalPublishBridge bridge,
        SocialOptions options,
        ILogger<ScheduledPostPublisher>? logger = null,
        IoPoolRegistry? registry = null)
    {
        _queue = queue;
        _publishers = publishers;
        _bridge = bridge;
        _options = options;
        _logger = logger;
        _http = registry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    // BackgroundService boundary — single .ToTask() bridge for the framework's Task
    // contract. Inside is one observable chain; the rest of the file is reactive.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("ScheduledPostPublisher started (interval {Interval})", _options.PublishTickInterval);

        return Observable.Interval(_options.PublishTickInterval)
            .StartWith(-1L)
            .SelectMany(_ => DrainAndPublishOnce()
                .Catch((Exception ex) =>
                {
                    if (ex is OperationCanceledException) return Observable.Empty<Unit>();
                    _logger?.LogError(ex, "ScheduledPostPublisher tick failed");
                    return Observable.Empty<Unit>();
                }))
            .ToTask(stoppingToken);
    }

    private IObservable<Unit> DrainAndPublishOnce() =>
        Observable.Defer(() =>
        {
            var due = _queue.DrainDue(DateTimeOffset.UtcNow);
            return due.ToObservable().SelectMany(PublishWithRetry);
        });

    private IObservable<Unit> PublishWithRetry(PublishableSnapshot snapshot)
    {
        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Platform, snapshot.Platform, StringComparison.OrdinalIgnoreCase));
        if (publisher is null)
        {
            _logger?.LogWarning("No IPlatformPublisher registered for {Platform} — dropping {PostPath}",
                snapshot.Platform, snapshot.PostPath);
            return Observable.Return(Unit.Default);
        }

        var request = new PlatformPublishRequest(
            snapshot.PostPath, snapshot.AuthorHandle, snapshot.Text,
            snapshot.MediaUrls, snapshot.Credential);

        return AttemptPublish(publisher, request, snapshot, attempt: 1)
            .SelectMany(result => _bridge.ApplyPublish(snapshot.PostPath, result)
                .Do(_ =>
                {
                    if (result.Urn is not null && result.Error is null)
                        _logger?.LogInformation("Published {PostPath} → {Platform} {Urn}",
                            snapshot.PostPath, snapshot.Platform, result.Urn);
                })
                .Select(_ => Unit.Default));
    }

    private IObservable<PublishResult> AttemptPublish(
        IPlatformPublisher publisher,
        PlatformPublishRequest request,
        PublishableSnapshot snapshot,
        int attempt) =>
        _http.Invoke(ct => publisher.PublishAsync(request, ct))
            .Catch((Exception ex) =>
            {
                _logger?.LogWarning(ex, "Publish attempt {Attempt}/{Max} threw for {PostPath}",
                    attempt, _options.MaxPublishAttempts, snapshot.PostPath);
                return Observable.Return(new PublishResult(null, null, DateTimeOffset.UtcNow, Error: ex.Message));
            })
            .SelectMany(result =>
            {
                if (result.Urn is not null && result.Error is null)
                    return Observable.Return(result);

                _logger?.LogWarning("Publish attempt {Attempt}/{Max} failed for {PostPath}: {Error}",
                    attempt, _options.MaxPublishAttempts, snapshot.PostPath, result.Error ?? "no urn");

                if (attempt >= _options.MaxPublishAttempts)
                    return Observable.Return(result);

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                return Observable.Timer(backoff)
                    .SelectMany(_ => AttemptPublish(publisher, request, snapshot, attempt + 1));
            });
}
