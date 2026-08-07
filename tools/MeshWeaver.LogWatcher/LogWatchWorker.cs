using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// The watch loop: every <see cref="LogWatcherOptions.PollInterval"/>, read the red lines each
/// namespace has produced since its cursor, group them into fingerprinted reports, queue them, and
/// drain the queue to the portal.
///
/// <para>Order matters and is deliberate. Reports are <b>persisted before they are delivered</b>
/// and the cursor advances <b>only after</b> the window's reports are safely queued — so a crash,
/// an unreachable portal, or a restart costs a redelivery (harmless: the fingerprint dedups it)
/// rather than a silently un-ticketed error.</para>
/// </summary>
public sealed class LogWatchWorker(
    LokiClient loki,
    IncidentReporter reporter,
    WatcherState state,
    LogWatcherOptions options,
    ILogger<LogWatchWorker>? logger = null) : BackgroundService
{
    /// <inheritdoc />
    // The hosted-service boundary — the single sanctioned Task bridge; everything inside is one
    // observable chain (Doc/Architecture/AsynchronousCalls.md).
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            logger?.LogError(
                "Log watcher is not configured: set LogWatcher__PortalUrl and LogWatcher__IngestToken. "
                + "Idling — no red log will be ticketed.");
            return Task.CompletedTask;
        }

        logger?.LogInformation(
            "Log watcher started — Loki {Loki}, namespaces [{Namespaces}], every {Interval}, reporting to {Portal}",
            options.LokiUrl, string.Join(", ", options.Namespaces), options.PollInterval, options.PortalUrl);

        return state.Load()
            .SelectMany(_ => Observable
                .Interval(options.PollInterval)
                .StartWith(-1L)
                // Concat, not Merge: a slow poll must not overlap the next one, or two passes would
                // read the same window and the cursor writes would race.
                .Select(_ => Observable.Defer(Tick))
                .Concat())
            .ToTask(stoppingToken);
    }

    /// <summary>One pass: collect from every namespace, then drain whatever is pending.</summary>
    private IObservable<Unit> Tick() =>
        options.Namespaces
            .ToObservable()
            .Select(ns => Observable.Defer(() => Collect(ns)))
            .Concat()
            .ToList()
            .SelectMany(_ => Drain())
            .Catch((Exception ex) =>
            {
                // A failed pass must not kill the loop — the cursor did not advance, so the next
                // tick re-reads the same window. Loud, and self-healing.
                logger?.LogError(ex, "Log-watch pass failed; the next tick will re-read the same window");
                return Observable.Return(Unit.Default);
            });

    /// <summary>
    /// Reads and queues one namespace's window, then advances its cursor.
    ///
    /// <para>The window's upper bound trails <c>now</c> by
    /// <see cref="LogWatcherOptions.IngestLag"/>: Promtail delivers with a small delay, and reading
    /// right up to "now" would step the cursor past lines that had not landed yet — losing them for
    /// good. Trailing the edge trades a few seconds of latency for not dropping errors.</para>
    /// </summary>
    private IObservable<Unit> Collect(string ns)
    {
        var now = DateTimeOffset.UtcNow;
        var start = state.CursorFor(ns, now);
        var end = now - options.IngestLag;
        if (end <= start)
            return Observable.Return(Unit.Default);

        return loki.Query(ns, start, end, options.QueryLimit).SelectMany(entries =>
        {
            var reports = BurstAggregator.Aggregate(
                entries, options.MaxSamplesPerReport, options.MaxSampleLength, options.IgnoreCategories);

            if (reports.Count > 0)
                logger?.LogInformation(
                    "{Namespace}: {Bursts} distinct fingerprint(s) from {Lines} red line(s)",
                    ns, reports.Count, entries.Count);

            // Truncation guard: at the query limit the window was not fully read, so advancing the
            // cursor to `end` would skip the remainder. Advance only to the last line actually seen
            // and let the next tick pick up from there.
            var truncated = entries.Count >= options.QueryLimit;
            var cursor = truncated && entries.Count > 0
                ? entries[^1].Timestamp
                : end;
            if (truncated)
                logger?.LogWarning(
                    "{Namespace}: hit the {Limit}-entry query limit — resuming at {Cursor:u} instead of {End:u}",
                    ns, options.QueryLimit, cursor.UtcDateTime, end.UtcDateTime);

            // Queue BEFORE the cursor moves: if the process dies between these two writes the
            // window is simply re-read, which the fingerprint makes harmless.
            return (reports.Count > 0 ? state.Enqueue(reports) : Observable.Return(Unit.Default))
                .SelectMany(_ => state.Advance(ns, cursor));
        });
    }

    /// <summary>
    /// Delivers the pending queue oldest-first, one at a time. Stops at the first retryable
    /// failure so a wedged portal is not hammered once per queued report — the next tick tries
    /// again from the head.
    /// </summary>
    private IObservable<Unit> Drain()
    {
        var pending = state.Pending;
        if (pending.IsEmpty)
            return Observable.Return(Unit.Default);

        logger?.LogInformation("Delivering {Count} report(s) to the portal", pending.Count);
        return Deliver(pending, 0);
    }

    private IObservable<Unit> Deliver(ImmutableList<LogIncidentReport> pending, int index)
    {
        if (index >= pending.Count)
            return Observable.Return(Unit.Default);

        var report = pending[index];
        return reporter.Send(report).SelectMany(result => result switch
        {
            IncidentReporter.Delivery.Retry => Observable.Return(Unit.Default),
            // Accepted and Rejected both leave the queue: a rejected report cannot be fixed by
            // resending it, and keeping it would block every report behind it forever.
            _ => state.Delivered(report).SelectMany(_ => Deliver(pending, index + 1)),
        });
    }
}
