using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>Registration for the app-level event-log outbox.</summary>
public static class EventLogExtensions
{
    /// <summary>
    /// Registers the event-log writer + startup replay and the default in-memory
    /// <see cref="IEventLogStore"/>. A backend (e.g. Postgres) can override the store with
    /// <c>services.Replace(...)</c> for durability across restarts.
    /// </summary>
    public static IServiceCollection AddMeshEventLog(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventLogStore, InMemoryEventLogStore>();
        services.AddHostedService<EventLogWriter>();
        services.AddHostedService<EventLogReplayService>();
        return services;
    }
}

/// <summary>
/// A durable, append-only log of <see cref="MeshChangeEvent"/>s plus a per-consumer cursor — the
/// app-level outbox for the scheduled-action / general event-driven layer. The
/// <see cref="EventLogWriter"/> appends every change; the <see cref="EventLogReplayService"/> replays
/// entries newer than a consumer's cursor into the change feed on startup, so an event a consumer has
/// not yet processed (e.g. it lives in another silo, or restarted) still reaches it. Append is
/// idempotent by <c>(Path, Kind, Version)</c>, so a replayed event is not re-logged (no feedback loop).
///
/// <para>Best-effort, NOT a transactional outbox: an event published while the writer itself is down
/// is never logged. For the invite use case that gap is covered by <c>ScheduledActionRunner</c>'s
/// startup reconciliation against current state — the durable log adds cross-process delivery + a
/// queryable audit trail on top.</para>
/// </summary>
public interface IEventLogStore
{
    /// <summary>Appends an event, returning its sequence number. Idempotent by (Path, Kind, Version):
    /// a duplicate returns the existing seq and does not add a row.</summary>
    IObservable<long> Append(MeshChangeEvent change);

    /// <summary>Reads events with <c>seq &gt; <paramref name="afterSeq"/></c> in seq order (up to <paramref name="limit"/>).</summary>
    IObservable<IReadOnlyList<EventLogEntry>> ReadFrom(long afterSeq, int limit = 500);

    /// <summary>The highest assigned sequence number (0 when empty).</summary>
    IObservable<long> MaxSeq();

    /// <summary>The last-processed seq for <paramref name="consumerId"/> (0 if none recorded).</summary>
    IObservable<long> GetCursor(string consumerId);

    /// <summary>Advances <paramref name="consumerId"/>'s cursor to <paramref name="seq"/>.</summary>
    IObservable<Unit> SetCursor(string consumerId, long seq);
}

/// <summary>One logged event with its assigned sequence number.</summary>
public sealed record EventLogEntry(long Seq, MeshChangeEvent Event);

/// <summary>
/// In-memory <see cref="IEventLogStore"/> — the default (monolith / tests) and the base the Orleans
/// or PG store composes over. Instance state on a mesh-scoped singleton (never static), so it dies
/// with the mesh.
/// </summary>
public sealed class InMemoryEventLogStore : IEventLogStore
{
    private readonly object _gate = new();
    private long _seq;
    private ImmutableList<EventLogEntry> _entries = ImmutableList<EventLogEntry>.Empty;
    // (Path, Kind, Version) → assigned seq: O(1) idempotency dedup (a linear scan of _entries would be O(n) per append).
    private readonly Dictionary<(string, MeshChangeKind, long), long> _byKey = new();
    private readonly ConcurrentDictionary<string, long> _cursors = new();

    /// <inheritdoc />
    public IObservable<long> Append(MeshChangeEvent change) => Observable.Defer(() =>
    {
        lock (_gate)
        {
            var key = (change.Path, change.Kind, change.Version);
            if (_byKey.TryGetValue(key, out var existingSeq))
                return Observable.Return(existingSeq);
            var seq = ++_seq;
            _entries = _entries.Add(new EventLogEntry(seq, change));
            _byKey[key] = seq;
            return Observable.Return(seq);
        }
    });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<EventLogEntry>> ReadFrom(long afterSeq, int limit = 500) => Observable.Defer(() =>
    {
        lock (_gate)
            return Observable.Return((IReadOnlyList<EventLogEntry>)_entries
                .Where(e => e.Seq > afterSeq).OrderBy(e => e.Seq).Take(limit).ToList());
    });

    /// <inheritdoc />
    public IObservable<long> MaxSeq() => Observable.Defer(() =>
    {
        lock (_gate) return Observable.Return(_seq);
    });

    /// <inheritdoc />
    public IObservable<long> GetCursor(string consumerId) =>
        Observable.Return(_cursors.GetValueOrDefault(consumerId, 0L));

    /// <inheritdoc />
    public IObservable<Unit> SetCursor(string consumerId, long seq)
    {
        _cursors.AddOrUpdate(consumerId, seq, (_, cur) => Math.Max(cur, seq));
        return Observable.Return(Unit.Default);
    }
}

/// <summary>
/// Hosted service that durably records every change-feed event into the <see cref="IEventLogStore"/>.
/// Subscribe-based (best-effort): the durable-outbox limitation is documented on <see cref="IEventLogStore"/>.
/// </summary>
public sealed class EventLogWriter(
    IMeshChangeFeed changeFeed,
    IEventLogStore store,
    ILogger<EventLogWriter>? logger = null) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = changeFeed.Subscribe(change =>
            store.Append(change).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Event-log append failed for {Path}", change.Path)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _subscription?.Dispose();
}

/// <summary>
/// Hosted service that, on startup, replays log entries a consumer has not yet processed (seq &gt; its
/// cursor) into the change feed, then advances the cursor — so a consumer that missed events (another
/// silo, a restart) sees them. Append idempotency means the replayed events are not re-logged.
/// </summary>
public sealed class EventLogReplayService(
    IMeshChangeFeed changeFeed,
    IEventLogStore store,
    ILogger<EventLogReplayService>? logger = null) : IHostedService
{
    /// <summary>The consumer id whose cursor this replay advances (the scheduled-action runner).</summary>
    public const string RunnerConsumerId = "scheduled-actions";

    /// <summary>Page size for the replay drain — must match <see cref="IEventLogStore.ReadFrom"/>'s cap.</summary>
    private const int PageSize = 500;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Replay durably-logged-but-unprocessed events into the feed, checkpointing after each page and
        // draining ALL pages (a single ReadFrom is capped at PageSize — a backlog larger than that would
        // otherwise never fully replay). SetCursor faults propagate to the outer error sink, not swallowed.
        store.GetCursor(RunnerConsumerId)
            .SelectMany(ReplayFrom)
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Event-log replay failed"));
        return Task.CompletedTask;
    }

    /// <summary>Reads one page after <paramref name="cursor"/>, publishes it, advances the cursor, and — if
    /// the page was full — recurses to drain the next page. A short page (or empty) ends the drain.</summary>
    private IObservable<Unit> ReplayFrom(long cursor) =>
        store.ReadFrom(cursor, PageSize).SelectMany(entries =>
        {
            if (entries.Count == 0)
                return Observable.Return(Unit.Default);
            foreach (var entry in entries)
                changeFeed.Publish(entry.Event);
            var max = entries[^1].Seq;
            return store.SetCursor(RunnerConsumerId, max)
                .Do(_ => logger?.LogInformation(
                    "Replayed {Count} event-log entries (seq {From}→{To}) into the change feed",
                    entries.Count, cursor, max))
                .SelectMany(_ => entries.Count < PageSize
                    ? Observable.Return(Unit.Default)   // short page → backlog drained
                    : ReplayFrom(max));                 // full page → more may remain
        });

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
