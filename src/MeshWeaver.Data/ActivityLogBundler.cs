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
/// - Timer expires: flush bundle to IActivityLogStore
/// - Hub dispose: flush all via FlushOnDispose
/// </summary>
public class ActivityLogBundler : IDisposable
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly IActivityLogStore _store;
    private readonly string _hubPath;
    private readonly ILogger<ActivityLogBundler>? _logger;

    private readonly ConcurrentDictionary<BundleKey, ActiveBundle> _activeBundles = new();
    private bool _disposed;

    public ActivityLogBundler(IMessageHub hub, IActivityLogStore store, ILogger<ActivityLogBundler>? logger = null)
    {
        _store = store;
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

        var log = bundle.ToActivityLog();

        try
        {
            _store.SaveActivityLogAsync(_hubPath, log).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ActivityLogBundler: Failed to save activity log for hub={Hub}", _hubPath);
        }
    }

    private void FlushAll()
    {
        foreach (var key in _activeBundles.Keys.ToArray())
        {
            if (_activeBundles.TryRemove(key, out var bundle))
                FlushBundle(bundle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushAll();
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
