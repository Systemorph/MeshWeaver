using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("PastPostIngestJob started (interval {Interval})", _options.PastPostIngestInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var target in _source.GetIngestTargetsAsync(stoppingToken))
                {
                    var publisher = _publishers.FirstOrDefault(p =>
                        string.Equals(p.Platform, target.Platform, StringComparison.OrdinalIgnoreCase));
                    if (publisher is null) continue;

                    var imported = 0;
                    await foreach (var past in publisher.ListPastPostsAsync(
                        target.Credential, target.SinceInclusive, _options.PastPostIngestPageSize, stoppingToken))
                    {
                        var created = await _sink.UpsertAsync(target, past, stoppingToken);
                        if (created) imported++;
                    }

                    if (imported > 0)
                        _logger?.LogInformation("Ingested {Count} past posts from {Platform} for {ProfilePath}",
                            imported, target.Platform, target.ProfilePath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "PastPostIngestJob tick failed");
            }

            try { await Task.Delay(_options.PastPostIngestInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
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
