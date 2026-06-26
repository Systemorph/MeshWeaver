using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Per-hub service that debounces high-frequency DataChangeRequests into bundled
/// ActivityLog entries. Uses a 300ms inactivity timeout.
///
/// Keyed by (ChangedBy, Category):
/// - First change: create bundle, start timer
/// - Subsequent same (user, category): increment count, reset timer
/// - Different user or category: flush current bundle, start new
/// - Timer expires: flush bundle via onFlush callback
/// - Hub dispose: flush all via FlushOnDispose
/// </summary>
public class ActivityLogBundler : IDisposable
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly Action<ActivityLog> _onFlush;
    private readonly string _hubPath;
    private readonly ILogger<ActivityLogBundler>? _logger;

    private readonly ConcurrentDictionary<BundleKey, ActiveBundle> _activeBundles = new();
    private bool _disposed;

    /// <summary>
    /// Creates a bundler bound to <paramref name="hub"/>; it registers itself for disposal so any
    /// pending timers are stopped when the hub tears down.
    /// </summary>
    /// <param name="hub">Hub whose data changes are bundled; supplies the hub path recorded on each log.</param>
    /// <param name="onFlush">Callback invoked with the bundled <see cref="ActivityLog"/> when a bundle is flushed.</param>
    /// <param name="logger">Optional logger for flush failures.</param>
    public ActivityLogBundler(IMessageHub hub, Action<ActivityLog> onFlush, ILogger<ActivityLogBundler>? logger = null)
    {
        _onFlush = onFlush;
        _hubPath = hub.Address.ToString();
        _logger = logger;
        hub.RegisterForDisposal(new FlushOnDispose(this));
    }

    /// <summary>
    /// Record a data change into the appropriate bundle.
    /// Called from HandleDataChangeRequest.
    /// </summary>
    public void RecordChange(DataChangeRequest request, string category)
    {
        if (_disposed) return;

        var key = new BundleKey(request.ChangedBy, category);

        _activeBundles.AddOrUpdate(key,
            // Factory: first change for this (user, category)
            _ => CreateNewBundle(key),
            // Update: subsequent change for same (user, category)
            (_, existing) =>
            {
                existing.IncrementCount();
                ResetTimer(existing);
                return existing;
            });
    }

    private ActiveBundle CreateNewBundle(BundleKey key)
    {
        var bundle = new ActiveBundle(key, _hubPath);
        ResetTimer(bundle);
        return bundle;
    }

    private void ResetTimer(ActiveBundle bundle)
    {
        lock (bundle.TimerLock)
        {
            bundle.Timer?.Dispose();
            bundle.Timer = new Timer(_ => OnTimerExpired(bundle.Key), null,
                DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerExpired(BundleKey key)
    {
        if (_activeBundles.TryRemove(key, out var bundle))
            FlushBundle(bundle);
    }

    private void FlushBundle(ActiveBundle bundle)
    {
        lock (bundle.TimerLock)
        {
            bundle.Timer?.Dispose();
            bundle.Timer = null;
        }

        // 🚨 Never log to the activity log once disposal has begun. _onFlush round-trips
        // through the hub/storage that is tearing down (persistence.Read(hubPath).Take(1),
        // no Timeout, then persistence.Write of the {hubPath}/_activity/{id} node). During
        // disposal that read never completes, so the flush subscription wedges and the hub
        // never finishes tearing down — its path then routes nowhere and the NEXT read of
        // it hangs forever ("ReadNode did not emit"). This catches a debounce timer that
        // fires concurrently with Dispose; the dispose path itself no longer flushes.
        if (_disposed) return;

        var log = bundle.ToActivityLog() with { HubPath = _hubPath };

        // Sync invocation — onFlush is responsible for its own reactive composition
        // (Subscribe-based, no await). Wrapping in try/catch so a buggy callback
        // never crashes the timer thread.
        try
        {
            _onFlush(log);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ActivityLogBundler: Failed to save activity log for hub={Hub}", _hubPath);
        }
    }

    /// <summary>
    /// Stops every pending debounce timer WITHOUT flushing. Flushing during hub disposal would
    /// round-trip through the tearing-down hub and wedge teardown; the bundled summary is dropped
    /// (the underlying data changes are already persisted).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop every debounce timer WITHOUT flushing. Flushing here would log to the
        // activity log during hub disposal — _onFlush round-trips through the tearing-down
        // hub/storage and wedges the teardown (see FlushBundle). The pending bundle is a
        // best-effort audit SUMMARY ("Bundled N data change(s)"); the underlying data
        // changes are already persisted, so dropping the summary on shutdown is correct and
        // far cheaper than a wedged hub whose path then hangs every subsequent read.
        foreach (var key in _activeBundles.Keys.ToArray())
        {
            if (_activeBundles.TryRemove(key, out var bundle))
            {
                lock (bundle.TimerLock)
                {
                    bundle.Timer?.Dispose();
                    bundle.Timer = null;
                }
            }
        }
    }

    private record struct BundleKey(string? ChangedBy, string Category);

    private class ActiveBundle
    {
        public BundleKey Key { get; }
        public string HubPath { get; }
        public DateTime Start { get; }
        public object TimerLock { get; } = new();
        public Timer? Timer { get; set; }
        private int _changeCount = 1;

        public ActiveBundle(BundleKey key, string hubPath)
        {
            Key = key;
            HubPath = hubPath;
            Start = DateTime.UtcNow;
        }

        public void IncrementCount() => Interlocked.Increment(ref _changeCount);

        public ActivityLog ToActivityLog() => new(Key.Category)
        {
            Start = Start,
            End = DateTime.UtcNow,
            Status = ActivityStatus.Succeeded,
            User = Key.ChangedBy != null ? new UserInfo(Key.ChangedBy, Key.ChangedBy) : null,
            Messages = ImmutableList.Create(
                new LogMessage($"Bundled {_changeCount} data change(s)", LogLevel.Information))
        };
    }

    private sealed class FlushOnDispose(ActivityLogBundler bundler) : IDisposable
    {
        public void Dispose() => bundler.Dispose();
    }
}
