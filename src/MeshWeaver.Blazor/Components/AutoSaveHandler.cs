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

    /// <summary>
    /// Disposes the handler: releases the throttle subscription and disposes the value subject,
    /// then FLUSHES any edit that was still inside the throttle window. Subsequent calls to
    /// <c>OnValueChanged</c> are silently ignored after disposal.
    ///
    /// <para>That order is deliberate — tearing the throttle down first makes the flush the only
    /// remaining writer, where flushing first would leave a window in which both could fire.</para>
    ///
    /// <para>🚨 <b>The flush is the point (issue #1606).</b> <c>Throttle</c> holds the last value for
    /// the whole interval, so a dispose inside that window used to DROP it — silently, with no error
    /// anywhere. That is not a rare teardown case: the Blazor views that own this handler create it
    /// in <c>BindData</c> and register it with <c>AddBinding</c>, and <c>OnParametersSet</c> runs
    /// <c>DisposeBindings()</c> → <c>BindData()</c>. So ANY re-render with new parameters inside 500 ms
    /// of a keystroke destroyed the pending save — and a course example cell re-renders on every
    /// emission of the node it is bound to, including the echo of the learner's own previous save.
    /// The learner typed, the cell looked editable, and the text was gone after a reload.</para>
    ///
    /// <para>A pending value that equals <see cref="LastSyncedValue"/> is skipped, exactly as
    /// <c>OnThrottledValue</c> skips it. If the throttle fires concurrently with this dispose the
    /// worst case is the same value written twice, which the writers already treat as a no-op (they
    /// return the node unchanged when the text matches, making the merge patch empty) — so this
    /// needs no lock, and a lock here would be a hand-woven gate on a UI teardown path.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Stop the throttle FIRST so the flush below is the only remaining writer, then flush what
        // it was holding. Order matters: flushing first would leave a window for a double write.
        _subscription.Dispose();
        _valueSubject.Dispose();

        var pending = CurrentValue;
        if (pending is not null && pending != LastSyncedValue)
            OnThrottledValue(pending);
    }
}
