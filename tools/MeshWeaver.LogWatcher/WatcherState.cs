using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Observability;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// The watcher's durable state: how far each namespace has been read, and which reports have not
/// been accepted by the portal yet.
///
/// <para>Both halves exist for the same reason — <b>the portal being down is exactly when red logs
/// happen</b>. The cursor means a poll that could not be delivered is re-read rather than skipped;
/// the pending queue means a burst detected while the portal was wedged is delivered when it comes
/// back, instead of being dropped into a log line nobody reads.</para>
///
/// <para>Persisted as two small JSON files under
/// <see cref="LogWatcherOptions.StateDirectory"/>. Every file leaf runs on the blocking I/O pool
/// (Doc/Architecture/ControlledIoPooling.md); the in-memory copies are instance fields on this
/// mesh-lifetime singleton, never static (Doc/Architecture/NoStaticState.md).</para>
/// </summary>
public sealed class WatcherState(
    LogWatcherOptions options,
    IIoPool pool,
    ILogger<WatcherState>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly object gate = new();
    private ImmutableDictionary<string, DateTimeOffset> cursors =
        ImmutableDictionary<string, DateTimeOffset>.Empty;
    private ImmutableList<LogIncidentReport> pending = ImmutableList<LogIncidentReport>.Empty;

    private string CursorFile => Path.Combine(options.StateDirectory, "cursors.json");
    private string PendingFile => Path.Combine(options.StateDirectory, "pending.json");

    /// <summary>Reports not yet accepted by the portal, oldest first.</summary>
    public ImmutableList<LogIncidentReport> Pending
    {
        get { lock (gate) return pending; }
    }

    /// <summary>Loads persisted state, tolerating a missing or unreadable file (first run).</summary>
    public IObservable<Unit> Load() => pool.InvokeBlocking(_ =>
    {
        Directory.CreateDirectory(options.StateDirectory);
        lock (gate)
        {
            cursors = Read<ImmutableDictionary<string, DateTimeOffset>>(CursorFile)
                      ?? ImmutableDictionary<string, DateTimeOffset>.Empty;
            pending = Read<ImmutableList<LogIncidentReport>>(PendingFile)
                      ?? ImmutableList<LogIncidentReport>.Empty;
            logger?.LogInformation(
                "Watcher state loaded: {Cursors} cursor(s), {Pending} undelivered report(s)",
                cursors.Count, pending.Count);
        }
        return Unit.Default;
    });

    /// <summary>
    /// Where reading should resume for <paramref name="ns"/>. A cold start reads only
    /// <see cref="LogWatcherOptions.ColdStartLookback"/>; a cursor older than
    /// <see cref="LogWatcherOptions.MaxCatchUp"/> is dragged forward so one poll after a long
    /// outage cannot ask Loki for days of logs and time out on every attempt.
    /// </summary>
    public DateTimeOffset CursorFor(string ns, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!cursors.TryGetValue(ns, out var cursor))
                return now - options.ColdStartLookback;
            var floor = now - options.MaxCatchUp;
            if (cursor >= floor)
                return cursor;
            logger?.LogWarning(
                "Cursor for {Namespace} was {Cursor:u}, older than the {MaxCatchUp} catch-up cap — "
                + "resuming at {Floor:u}. Red logs in the skipped window were NOT ticketed.",
                ns, cursor.UtcDateTime, options.MaxCatchUp, floor.UtcDateTime);
            return floor;
        }
    }

    /// <summary>Advances a namespace's cursor and persists it.</summary>
    public IObservable<Unit> Advance(string ns, DateTimeOffset to)
    {
        lock (gate)
            cursors = cursors.SetItem(ns, to);
        return Save(CursorFile, cursors);
    }

    /// <summary>
    /// Queues reports for delivery. Called BEFORE any delivery is attempted, so a crash between
    /// detection and delivery loses nothing.
    /// </summary>
    public IObservable<Unit> Enqueue(IEnumerable<LogIncidentReport> reports)
    {
        ImmutableList<LogIncidentReport> snapshot;
        lock (gate)
        {
            pending = pending.AddRange(reports);
            snapshot = pending;
        }
        return Save(PendingFile, snapshot);
    }

    /// <summary>Drops a delivered report from the queue and persists what remains.</summary>
    public IObservable<Unit> Delivered(LogIncidentReport report)
    {
        ImmutableList<LogIncidentReport> snapshot;
        lock (gate)
        {
            pending = pending.Remove(report);
            snapshot = pending;
        }
        return Save(PendingFile, snapshot);
    }

    private IObservable<Unit> Save<T>(string file, T value) => pool.InvokeBlocking(_ =>
    {
        try
        {
            Directory.CreateDirectory(options.StateDirectory);
            // Write-then-rename: a crash mid-write leaves the previous good file, not a truncated
            // one that would be read as "no cursor" and replay the whole lookback window.
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException ex)
        {
            // Losing the cursor costs a replay, not data — never take the watcher down for it.
            logger?.LogError(ex, "Could not persist watcher state to {File}", file);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogError(ex,
                "Could not persist watcher state to {File} — is {Directory} a writable volume?",
                file, options.StateDirectory);
        }
        return Unit.Default;
    });

    private T? Read<T>(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not read watcher state from {File} — starting fresh", file);
            return default;
        }
    }
}
