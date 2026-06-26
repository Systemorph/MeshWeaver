namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Manages chat submission lifecycle to prevent concurrent submissions,
/// text truncation, and provide input disable/enable state.
/// Designed to be testable independently of Blazor components.
/// Runs on Blazor's single synchronization context — no locking needed.
/// </summary>
public class ChatSubmissionHandler : IDisposable
{
    /// <summary>
    /// Tracks the current phase of a chat submission cycle so that the UI can
    /// block duplicate submissions and disable the input field at the right times.
    /// </summary>
    public enum SubmissionState
    {
        /// <summary>
        /// No submission in progress; the input field is enabled and ready to accept text.
        /// </summary>
        Idle,
        /// <summary>
        /// A message has been accepted and is being posted to the thread — the input field is disabled.
        /// </summary>
        Submitting,
        /// <summary>
        /// The message has been posted and the handler is waiting for the user message
        /// to appear in the thread display before re-enabling input.
        /// </summary>
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

    /// <summary>
    /// Creates a new <c>ChatSubmissionHandler</c>.
    /// </summary>
    /// <param name="dedupWindow">
    /// How long after an accepted submission the same text is silently rejected to guard
    /// against accidental double-clicks. Defaults to 500 ms.
    /// </param>
    /// <param name="now">
    /// Clock factory used to determine whether the dedup window has elapsed.
    /// Defaults to <c>DateTime.UtcNow</c>. Override in unit tests for deterministic timing.
    /// </param>
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

    /// <summary>
    /// Marks the handler as disposed so that subsequent <c>TryBeginSubmit</c> calls
    /// return <c>false</c> without side effects.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
    }
}
