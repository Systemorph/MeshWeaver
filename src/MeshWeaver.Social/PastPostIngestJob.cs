using System;
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
/// Background service that periodically fetches past posts from each configured
/// platform and creates corresponding Post nodes in the mesh. Deduplicates by
/// <c>PlatformUrn</c> so re-running doesn't create duplicates.
///
/// Runs once on startup (backfill) and then on <see cref="SocialOptions.PastPostIngestInterval"/>.
/// The hosting app supplies <see cref="IPastPostIngestSource"/> (yields profiles + credentials
/// to fetch for) and <see cref="IPastPostSink"/> (persists each historic post as a mesh node,
/// skipping existing Urns).
/// </summary>
public sealed class PastPostIngestJob : BackgroundService
{
    private readonly IPastPostIngestSource _source;
    private readonly IPastPostSink _sink;
    private readonly IEnumerable<IPlatformPublisher> _publishers;
    private readonly SocialOptions _options;
    private readonly ILogger<PastPostIngestJob>? _logger;

    public PastPostIngestJob(
        IPastPostIngestSource source,
        IPastPostSink sink,
        IEnumerable<IPlatformPublisher> publishers,
        SocialOptions options,
        ILogger<PastPostIngestJob>? logger = null)
    {
        _source = source;
        _sink = sink;
        _publishers = publishers;
        _options = options;
        _logger = logger;
    }

    // BackgroundService boundary — single .ToTask() bridge for the framework's Task
    // contract. Inside is one observable chain; the rest of the file is reactive.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("PastPostIngestJob started (interval {Interval})", _options.PastPostIngestInterval);

        return Observable.Interval(_options.PastPostIngestInterval)
            .StartWith(-1L)
            .SelectMany(_ => IngestOnce(stoppingToken)
                .Catch((Exception ex) =>
                {
                    if (ex is OperationCanceledException) return Observable.Empty<Unit>();
                    _logger?.LogError(ex, "PastPostIngestJob tick failed");
                    return Observable.Empty<Unit>();
                }))
            .ToTask(stoppingToken);
    }

    private IObservable<Unit> IngestOnce(CancellationToken stoppingToken) =>
        _source.GetIngestTargetsAsync(stoppingToken)
            .ToObservableSequence()
            .SelectMany(target => IngestTargetOnce(target, stoppingToken))
            .DefaultIfEmpty(Unit.Default);

    private IObservable<Unit> IngestTargetOnce(IngestTarget target, CancellationToken stoppingToken)
    {
        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Platform, target.Platform, StringComparison.OrdinalIgnoreCase));
        if (publisher is null) return Observable.Return(Unit.Default);

        return Observable.FromAsync(async ct =>
        {
            var imported = 0;
            await foreach (var past in publisher.ListPastPostsAsync(
                target.Credential, target.SinceInclusive, _options.PastPostIngestPageSize, ct))
            {
                var created = await _sink.UpsertAsync(target, past, ct);
                if (created) imported++;
            }

            if (imported > 0)
                _logger?.LogInformation("Ingested {Count} past posts from {Platform} for {ProfilePath}",
                    imported, target.Platform, target.ProfilePath);
            return Unit.Default;
        });
    }
}

/// <summary>Which profile to pull history for.</summary>
public sealed record IngestTarget(
    string ProfilePath,
    string Platform,
    PlatformCredential Credential,
    DateTimeOffset? SinceInclusive);

public interface IPastPostIngestSource
{
    IAsyncEnumerable<IngestTarget> GetIngestTargetsAsync(CancellationToken ct);
}

public interface IPastPostSink
{
    /// <summary>Returns true if a new node was created, false if an existing one was updated or skipped.</summary>
    Task<bool> UpsertAsync(IngestTarget target, PastPost post, CancellationToken ct);
}
