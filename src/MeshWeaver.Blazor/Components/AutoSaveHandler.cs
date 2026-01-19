using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Handles throttled auto-save operations. Designed to be testable independently of Blazor components.
/// Uses Throttle (debounce) behavior: waits for a period of silence before emitting the last value.
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

        _valueSubject.OnNext(value);
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
