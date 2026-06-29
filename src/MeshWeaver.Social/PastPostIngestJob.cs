using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
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

    // Controlled I/O pool (Http resource class). The genuinely-async history-pull
    // leaf (fetch past posts + upsert) routes through _http.Invoke so the network
    // round-trip runs off the hub scheduler and is bounded. Falls back to the
    // stateless unbounded pool when no registry is wired (tests).
    private readonly IIoPool _http;

    /// <summary>
    /// Initializes a new instance of the <c>PastPostIngestJob</c> class.
    /// </summary>
    /// <param name="source">Supplies the profiles + credentials to pull history for.</param>
    /// <param name="sink">Persists each historic post as a mesh node, skipping existing Urns.</param>
    /// <param name="publishers">Registered platform publishers, matched by platform name.</param>
    /// <param name="options">Social ingest configuration (interval, page size).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="registry">Optional I/O pool registry; falls back to the unbounded pool in tests.</param>
    public PastPostIngestJob(
        IPastPostIngestSource source,
        IPastPostSink sink,
        IEnumerable<IPlatformPublisher> publishers,
        SocialOptions options,
        ILogger<PastPostIngestJob>? logger = null,
        IoPoolRegistry? registry = null)
    {
        _source = source;
        _sink = sink;
        _publishers = publishers;
        _options = options;
        _logger = logger;
        _http = registry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Runs the ingest loop: an initial backfill on startup, then a periodic sweep
    /// on <c>PastPostIngestInterval</c> until the host stops.
    /// </summary>
    /// <param name="stoppingToken">Token signalling host shutdown.</param>
    /// <returns>A task that completes when the host stops the service.</returns>
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

        return _http.Invoke(async ct =>
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

/// <summary>
/// Hosting-app callback that yields which profiles + credentials the ingest job
/// should pull post history for. Streamed because the mesh query is async.
/// </summary>
public interface IPastPostIngestSource
{
    /// <summary>Streams the profiles whose post history should be ingested.</summary>
    /// <param name="ct">Token to cancel enumeration.</param>
    /// <returns>An async stream of ingest targets.</returns>
    IAsyncEnumerable<IngestTarget> GetIngestTargetsAsync(CancellationToken ct);
}

/// <summary>
/// Hosting-app callback that persists each fetched historic post as a mesh node,
/// deduplicating by platform URN.
/// </summary>
public interface IPastPostSink
{
    /// <summary>Returns true if a new node was created, false if an existing one was updated or skipped.</summary>
    /// <param name="target">The ingest target the post belongs to.</param>
    /// <param name="post">The historic post to persist.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>True if a new node was created; otherwise false.</returns>
    Task<bool> UpsertAsync(IngestTarget target, PastPost post, CancellationToken ct);
}
