namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Manages chat submission lifecycle to prevent concurrent submissions,
/// text truncation, and provide input disable/enable state.
/// Designed to be testable independently of Blazor components.
/// Runs on Blazor's single synchronization context — no locking needed.
/// </summary>
public class ChatSubmissionHandler : IDisposable
{
    public enum SubmissionState
    {
        Idle,
        Submitting,
        WaitingForResponse
    }

    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleTimeout;
    private IDisposable? _timeoutDisposable;
    private bool _disposed;

    /// <summary>
    /// Current state of the submission handler.
    /// </summary>
    public SubmissionState State { get; private set; } = SubmissionState.Idle;

    /// <summary>
    /// Whether the input should be enabled (only when Idle).
    /// </summary>
    public bool IsInputEnabled => State == SubmissionState.Idle;

    /// <summary>
    /// The text that was last submitted.
    /// </summary>
    public string? LastSubmittedText { get; private set; }

    /// <summary>
    /// Total number of successful submissions.
    /// </summary>
    public int SubmissionCount { get; private set; }

    /// <summary>
    /// Creates a ChatSubmissionHandler with a configurable timeout.
    /// </summary>
    /// <param name="timeout">Timeout after which a stuck submission auto-releases. Default 30s.</param>
    /// <param name="scheduleTimeout">Optional scheduler for testing. If null, uses Task.Delay.</param>
    public ChatSubmissionHandler(
        TimeSpan? timeout = null,
        Func<TimeSpan, Action, IDisposable>? scheduleTimeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _scheduleTimeout = scheduleTimeout ?? DefaultScheduleTimeout;
    }

    /// <summary>
    /// Attempts to begin a new submission. Returns false if a submission is already in progress
    /// or if the text is empty/whitespace.
    /// </summary>
    /// <param name="text">The message text to submit.</param>
    /// <returns>True if submission started, false if rejected.</returns>
    public bool TryBeginSubmit(string? text)
    {
        if (_disposed)
            return false;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (State != SubmissionState.Idle)
            return false;

        State = SubmissionState.Submitting;
        LastSubmittedText = text;
        SubmissionCount++;

        return true;
    }

    /// <summary>
    /// Transitions from Submitting to WaitingForResponse after the message has been posted.
    /// Starts the timeout timer.
    /// </summary>
    public void OnMessagePosted()
    {
        if (State != SubmissionState.Submitting)
            return;

        State = SubmissionState.WaitingForResponse;
        StartTimeout();
    }

    /// <summary>
    /// Called when the user's message appears in the thread display.
    /// Transitions back to Idle and re-enables input.
    /// </summary>
    public void OnResponseAppeared()
    {
        CancelTimeout();
        State = SubmissionState.Idle;
    }

    /// <summary>
    /// Forces a release back to Idle state (e.g., on error).
    /// </summary>
    public void ForceRelease()
    {
        CancelTimeout();
        State = SubmissionState.Idle;
    }

    private void StartTimeout()
    {
        CancelTimeout();
        _timeoutDisposable = _scheduleTimeout(_timeout, OnTimeout);
    }

    private void OnTimeout()
    {
        if (State != SubmissionState.Idle)
        {
            State = SubmissionState.Idle;
        }
    }

    private void CancelTimeout()
    {
        _timeoutDisposable?.Dispose();
        _timeoutDisposable = null;
    }

    private static IDisposable DefaultScheduleTimeout(TimeSpan delay, Action callback)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                callback();
        }, TaskScheduler.Default);
        return cts;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelTimeout();
    }
}
