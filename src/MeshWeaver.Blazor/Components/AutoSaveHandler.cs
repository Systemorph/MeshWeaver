using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Handles throttled auto-save operations. Designed to be testable independently of Blazor components.
/// Uses Throttle (debounce) behavior: waits for a period of silence before emitting the last value.
/// Tracks sync state to prevent race conditions between local edits and stream feedback.
/// </summary>
public class AutoSaveHandler : IDisposable
{
    private readonly Subject<string> _valueSubject = new();
    private readonly IDisposable _subscription;
    private readonly Action<string> _saveAction;
    private bool _disposed;

    /// <summary>
    /// Gets the last value that was saved (for testing purposes).
    /// </summary>
    public string? LastSavedValue { get; private set; }

    /// <summary>
    /// Gets the count of saves performed (for testing purposes).
    /// </summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// Gets the last value that was successfully synced to the stream.
    /// Used to detect echo responses and prevent them from overwriting local changes.
    /// </summary>
    public string? LastSyncedValue { get; private set; }

    /// <summary>
    /// Gets the current local value (most recent value from OnValueChanged).
    /// Used to detect pending local changes that should not be overwritten.
    /// </summary>
    public string? CurrentValue { get; private set; }

    /// <summary>
    /// Creates an AutoSaveHandler with the specified throttle interval.
    /// </summary>
    /// <param name="throttleInterval">Time to wait after last change before saving.</param>
    /// <param name="saveAction">Action to perform when saving.</param>
    /// <param name="scheduler">Optional scheduler for testing. If null, uses default scheduler.</param>
    public AutoSaveHandler(TimeSpan throttleInterval, Action<string> saveAction, IScheduler? scheduler = null)
    {
        _saveAction = saveAction ?? throw new ArgumentNullException(nameof(saveAction));

        var observable = _valueSubject
            .Throttle(throttleInterval, scheduler ?? Scheduler.Default);

        _subscription = observable.Subscribe(OnThrottledValue);
    }

    private void OnThrottledValue(string value)
    {
        // Skip if nothing changed since last sync (avoid redundant saves)
        if (value == LastSyncedValue)
            return;

        LastSyncedValue = value;
        LastSavedValue = value;
        SaveCount++;
        _saveAction(value);
    }

    /// <summary>
    /// Called when content changes. The value will be saved after the throttle interval
    /// if no further changes occur.
    /// </summary>
    public void OnValueChanged(string value)
    {
        if (_disposed)
            return;

        CurrentValue = value;
        _valueSubject.OnNext(value);
    }

    /// <summary>
    /// Determines whether an external update (from stream) should be applied to the editor.
    /// Returns false if the update is an echo of our own sync or if we have pending local changes.
    /// </summary>
    /// <param name="value">The value received from the stream.</param>
    /// <returns>True if the update should be applied, false if it should be ignored.</returns>
    public bool ShouldApplyExternalUpdate(string value)
    {
        // Don't apply if it's an echo of what we last synced
        if (value == LastSyncedValue)
            return false;

        // Don't apply if we have pending local changes (CurrentValue differs from LastSyncedValue)
        if (CurrentValue != null && CurrentValue != LastSyncedValue)
            return false;

        return true;
    }

    /// <summary>
    /// Called when an external update has been applied to the editor.
    /// Updates tracking state to reflect the new baseline.
    /// </summary>
    /// <param name="value">The value that was applied.</param>
    public void OnExternalUpdateApplied(string value)
    {
        LastSyncedValue = value;
        CurrentValue = value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subscription.Dispose();
        _valueSubject.Dispose();
    }
}
