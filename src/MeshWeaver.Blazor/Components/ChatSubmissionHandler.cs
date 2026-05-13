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

    private readonly TimeSpan _dedupWindow;
    private readonly Func<DateTime> _now;
    private bool _disposed;
    private DateTime? _lastAcceptedAt;

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

    public ChatSubmissionHandler(
        TimeSpan? dedupWindow = null,
        Func<DateTime>? now = null)
    {
        _dedupWindow = dedupWindow ?? TimeSpan.FromMilliseconds(500);
        _now = now ?? (() => DateTime.UtcNow);
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

        // UX-only debounce: ThreadChatView force-releases immediately after Submit so the
        // input stays enabled for queueing while the previous round is processed by the
        // server. Without this guard, a double-click / Enter+Send race appends the same
        // message twice (the server watcher batches them into one round, but the user
        // sees two duplicate cells). The duplicate-EXECUTION race that this guard used
        // to mask is now fixed in ThreadSubmissionServer (atomic single-write append +
        // hardened reentrancy guard); this remains only as a UX safeguard against
        // accidental double-clicks of the same exact text.
        if (LastSubmittedText == text
            && _lastAcceptedAt.HasValue
            && (_now() - _lastAcceptedAt.Value) < _dedupWindow)
            return false;

        State = SubmissionState.Submitting;
        LastSubmittedText = text;
        _lastAcceptedAt = _now();
        SubmissionCount++;

        return true;
    }

    /// <summary>
    /// Transitions from Submitting to WaitingForResponse after the message has been posted.
    /// </summary>
    public void OnMessagePosted()
    {
        if (State != SubmissionState.Submitting)
            return;

        State = SubmissionState.WaitingForResponse;
    }

    /// <summary>
    /// Called when the user's message appears in the thread display.
    /// Transitions back to Idle and re-enables input.
    /// </summary>
    public void OnResponseAppeared()
    {
        State = SubmissionState.Idle;
    }

    /// <summary>
    /// Forces a release back to Idle state (e.g., on error).
    /// </summary>
    public void ForceRelease()
    {
        State = SubmissionState.Idle;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
