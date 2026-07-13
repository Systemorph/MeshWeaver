using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Cross-process change propagation for the Snowflake backend. Snowflake has no LISTEN/NOTIFY,
/// so this poller is the counterpart of <c>PostgreSqlChangeListener</c>: it tails
/// <c>events.event_log</c> on <see cref="SnowflakeStorageOptions.ChangeFeedPollInterval"/>,
/// filters out rows this silo appended itself (matched on <see cref="SnowflakeOriginId"/> —
/// in-process changes are already published synchronously from Write/Delete), maps the rest to
/// <see cref="DataChangeNotification"/>s with <c>Entity = null</c> (subscribers re-read, exactly
/// like the PG listener), and pushes them into the attached target observer.
///
/// <para>This is the LIVE feed only: the in-memory cursor seeds at the log's current
/// <see cref="SnowflakeEventLogStore.MaxSeq"/> — never at 0 — because historical catch-up is
/// <c>EventLogReplayService</c>'s job, not the live feed's. All composition stays inside Rx
/// operators over the store's already-pooled observables: no extra <c>IIoPool</c>, no
/// <c>Observable.FromAsync</c>, no <c>Task.Run</c>. Instance state only — the cursor, the
/// busy flag and the subscription all die with this mesh-scoped singleton.</para>
/// </summary>
public sealed class SnowflakeChangeFeedPoller : IDisposable
{
    /// <summary>Page size per poll read; a full page triggers an immediate drain re-poll.</summary>
    private const int PageSize = 500;

    private readonly SnowflakeEventLogStore _store;
    private readonly SnowflakeOriginId _origin;
    private readonly SnowflakeStorageOptions _options;
    private readonly ILogger<SnowflakeChangeFeedPoller>? _logger;

    // The target observer (the routing adapter's merged Changes feed), attached by DI wiring at
    // startup. Volatile: written once from the startup path, read from ThreadPool poll cycles.
    private volatile IObserver<DataChangeNotification>? _target;
    // Last seq this poller has consumed. Only the (busy-flag-serialised) poll cycle writes it
    // after seeding; Volatile keeps reads coherent across the varying ThreadPool tick threads.
    private long _cursor;
    // 0 = cursor not yet seeded from MaxSeq; 1 = seeded, poll cycles may run.
    private int _seeded;
    // Skip-if-busy flag: 1 while a poll cycle (incl. its drain loop) is in flight, so a slow
    // cycle overlapping the next interval tick is skipped instead of piled onto.
    private int _busy;
    private IDisposable? _subscription;

    /// <summary>Creates the poller over the event-log store, this silo's origin identity and the poll options.</summary>
    /// <param name="store">The Snowflake event-log store whose already-pooled observables the poller composes over.</param>
    /// <param name="origin">This silo's origin id; rows stamped with it are dropped (own writes are already published in-process).</param>
    /// <param name="options">Provides <see cref="SnowflakeStorageOptions.ChangeFeedPollInterval"/>.</param>
    /// <param name="logger">Optional logger for lifecycle and per-cycle failure diagnostics.</param>
    public SnowflakeChangeFeedPoller(
        SnowflakeEventLogStore store,
        SnowflakeOriginId origin,
        SnowflakeStorageOptions options,
        ILogger<SnowflakeChangeFeedPoller>? logger = null)
    {
        _store = store;
        _origin = origin;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Attaches the observer that receives the foreign-silo <see cref="DataChangeNotification"/>s
    /// (the routing adapter's merged Changes feed) — called by the DI wiring in
    /// <c>SnowflakeExtensions</c> at startup. While unattached, polled events are dropped (the
    /// cursor still advances): the live feed has no subscribers to serve yet, and durable
    /// catch-up remains <c>EventLogReplayService</c>'s job.
    /// </summary>
    /// <param name="target">The observer to push mapped change notifications into.</param>
    public void Attach(IObserver<DataChangeNotification> target) => _target = target;

    /// <summary>
    /// Starts the poll pipeline: one interval per <see cref="SnowflakeStorageOptions.ChangeFeedPollInterval"/>;
    /// each tick runs at most one cycle (skip-if-busy). The FIRST successful cycle seeds the
    /// cursor from <see cref="SnowflakeEventLogStore.MaxSeq"/>; subsequent cycles read/forward.
    /// A failed cycle (seed or poll) is logged and simply retried by the next tick — the
    /// interval itself is the retry cadence, so a transient Snowflake error can never kill the
    /// feed. Not re-entrant: called once from the hosted service (a second call restarts).
    /// </summary>
    public void Start()
    {
        Stop();
        _subscription = Observable.Interval(_options.ChangeFeedPollInterval)
            .SelectMany(_ => Tick())
            .Subscribe(
                _ => { },
                // Unreachable by construction (every cycle error is caught inside Tick), but a
                // silent dead feed is a wedge — surface it loudly if it ever happens.
                ex => _logger?.LogError(ex, "Snowflake change-feed poll pipeline terminated unexpectedly"));
        _logger?.LogInformation(
            "Snowflake change-feed poller started (interval {Interval})", _options.ChangeFeedPollInterval);
    }

    /// <summary>Stops the poll pipeline by disposing the interval subscription; idempotent.</summary>
    public void Stop() => Interlocked.Exchange(ref _subscription, null)?.Dispose();

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    /// One interval tick: acquire the skip-if-busy flag (or no-op if a previous cycle is still
    /// in flight), run seed-or-drain, convert any cycle error into a logged warning (the next
    /// tick retries), and release the flag on termination — including unsubscription on
    /// <see cref="Stop"/> mid-cycle.
    /// </summary>
    private IObservable<Unit> Tick() => Observable.Defer(() =>
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return Observable.Return(Unit.Default);
        var cycle = Volatile.Read(ref _seeded) == 1 ? DrainOnce() : Seed();
        return cycle
            .Catch((Exception ex) =>
            {
                _logger?.LogWarning(ex,
                    "Snowflake change-feed poll cycle failed; retrying on the next interval tick");
                return Observable.Return(Unit.Default);
            })
            .Finally(() => Volatile.Write(ref _busy, 0));
    });

    /// <summary>
    /// Seeds the in-memory cursor at the log's current maximum seq, so the live feed starts at
    /// "now" instead of replaying history (which would duplicate <c>EventLogReplayService</c>'s
    /// startup catch-up into every subscriber).
    /// </summary>
    private IObservable<Unit> Seed() =>
        _store.MaxSeq().Take(1)
            .Select(maxSeq =>
            {
                Volatile.Write(ref _cursor, maxSeq);
                Volatile.Write(ref _seeded, 1);
                _logger?.LogInformation("Snowflake change-feed poller seeded live cursor at seq {Seq}", maxSeq);
                return Unit.Default;
            });

    /// <summary>
    /// One drain step: read a page after the cursor, advance the cursor to the page's last seq,
    /// forward the foreign rows, and — if the page was full — immediately recurse to drain the
    /// backlog before the busy flag is released (same trampoline as
    /// <c>EventLogReplayService.ReplayFrom</c>). A short (or empty) page ends the cycle.
    /// </summary>
    private IObservable<Unit> DrainOnce() =>
        _store.ReadFromWithOrigin(Volatile.Read(ref _cursor), PageSize)
            .SelectMany(rows =>
            {
                if (rows.Count == 0)
                    return Observable.Return(Unit.Default);
                Volatile.Write(ref _cursor, rows[^1].Entry.Seq);
                Publish(rows);
                return rows.Count < PageSize
                    ? Observable.Return(Unit.Default)
                    : DrainOnce();
            });

    /// <summary>
    /// Maps one page of event-log rows to <see cref="DataChangeNotification"/>s and pushes them
    /// into the attached target. Rows stamped with this silo's own origin id are skipped (their
    /// changes were already published in-process from Write/Delete); a null origin stamp is
    /// treated as foreign. <c>Entity</c> stays null — subscribers re-read, exactly like the
    /// pg_notify path. Dropped entirely while no target is attached.
    /// </summary>
    /// <param name="rows">The page read via <see cref="SnowflakeEventLogStore.ReadFromWithOrigin"/>.</param>
    private void Publish(IReadOnlyList<(EventLogEntry Entry, string? OriginId)> rows)
    {
        var target = _target;
        if (target is null)
            return;
        foreach (var (entry, originId) in rows)
        {
            if (string.Equals(originId, _origin.Value, StringComparison.Ordinal))
                continue;
            var evt = entry.Event;
            var notification = evt.Kind switch
            {
                MeshChangeKind.Created => DataChangeNotification.Created(evt.Path, null),
                MeshChangeKind.Deleted => DataChangeNotification.Deleted(evt.Path),
                _ => DataChangeNotification.Updated(evt.Path, null),
            };
            target.OnNext(notification);
        }
    }
}
