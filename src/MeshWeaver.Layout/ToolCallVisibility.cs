namespace MeshWeaver.Layout;

/// <summary>
/// Decides which of a message's <see cref="ToolCallEntry"/> entries the chat
/// tool-calls section shows up front and which collapse into a "show all"
/// control. Keeps the bubble compact while a round fans out across many tools.
///
/// <para>The window is filled, in order:</para>
/// <list type="number">
///   <item><description><b>Running</b> first (a delegation actively streaming its sub-thread).</description></item>
///   <item><description><b>Pending</b> next (dispatched, result not back yet).</description></item>
///   <item><description><b>Completed</b> backfill — when fewer than <see cref="DefaultVisibleCap"/>
///     are active, the most-recently-finished calls fill the remaining slots so the window stays full.</description></item>
/// </list>
///
/// <para>A just-finished call also <b>lingers</b> in the window for
/// <see cref="CompletedLinger"/> even when the active set already fills the cap,
/// so a completion flashes its ✓ before dropping into the collapsed remainder.</para>
///
/// <para>Pure + deterministic (the caller passes <c>nowUtc</c>) so it unit-tests
/// without a clock and renders identically server-side and in the circuit.</para>
/// </summary>
public static class ToolCallVisibility
{
    /// <summary>Maximum number of <i>active</i> (running + pending) entries kept in the visible window.</summary>
    public const int DefaultVisibleCap = 5;

    /// <summary>How long a freshly-completed call stays in the visible window before it may collapse.</summary>
    public static readonly TimeSpan CompletedLinger = TimeSpan.FromSeconds(5);

    /// <summary>Actively executing — only a delegation streaming its sub-thread carries this status.</summary>
    public static bool IsRunning(ToolCallEntry call) => call.Status == ToolCallStatus.Streaming;

    /// <summary>
    /// Dispatched but no result yet. <see cref="ToolCallEntry.Status"/> defaults to
    /// <see cref="ToolCallStatus.Success"/> via the record initializer even before the result lands,
    /// so "pending" is "default status, result still null" — a terminal Failed/Cancelled counts as completed.
    /// </summary>
    public static bool IsPending(ToolCallEntry call) =>
        call.Status == ToolCallStatus.Success && call.Result is null;

    /// <summary>Settled — has a result, or reached a terminal Failed/Cancelled status.</summary>
    public static bool IsCompleted(ToolCallEntry call) => !IsRunning(call) && !IsPending(call);

    /// <summary>A completed call within the <see cref="CompletedLinger"/> window of <paramref name="nowUtc"/>.</summary>
    public static bool IsFreshlyCompleted(ToolCallEntry call, DateTime nowUtc) =>
        IsCompleted(call) && nowUtc - call.Timestamp < CompletedLinger;

    /// <summary>The split of a tool-call list into the visible window and the collapsed remainder.</summary>
    /// <param name="Visible">Entries shown up front (running → pending → completed backfill / lingering).</param>
    /// <param name="Hidden">Entries folded into the "show all" control.</param>
    /// <param name="RunningCount">Total running across the whole list.</param>
    /// <param name="PendingCount">Total pending across the whole list.</param>
    /// <param name="CompletedCount">Total completed across the whole list.</param>
    public sealed record View(
        IReadOnlyList<ToolCallEntry> Visible,
        IReadOnlyList<ToolCallEntry> Hidden,
        int RunningCount,
        int PendingCount,
        int CompletedCount)
    {
        /// <summary>True when there is a collapsed remainder worth a "show all" control.</summary>
        public bool HasHidden => Hidden.Count > 0;
    }

    /// <summary>
    /// Partitions <paramref name="calls"/> into the visible window and the collapsed remainder.
    /// </summary>
    /// <param name="calls">The message's tool calls (null/empty yields an empty view).</param>
    /// <param name="nowUtc">Reference time for the linger window — pass <c>DateTime.UtcNow</c>.</param>
    /// <param name="cap">Active-window size (defaults to <see cref="DefaultVisibleCap"/>).</param>
    public static View Partition(IReadOnlyList<ToolCallEntry>? calls, DateTime nowUtc, int cap = DefaultVisibleCap)
    {
        if (calls is null || calls.Count == 0)
            return new View([], [], 0, 0, 0);

        var running = calls.Where(IsRunning).ToList();
        var pending = calls.Where(IsPending).ToList();
        // Most-recently-finished first, so backfill and linger surface the newest completions.
        var completed = calls.Where(IsCompleted).OrderByDescending(c => c.Timestamp).ToList();

        // Active owns the window: running on top, then pending, capped.
        var active = running.Concat(pending).ToList();
        var activeShown = active.Take(cap).ToList();

        // Backfill leftover slots with the newest completed ("when fewer than 5, also show closed");
        // ALSO keep any just-finished call on screen for the linger window even when the cap is full.
        var slots = Math.Max(0, cap - activeShown.Count);
        var freshCount = completed.Count(c => IsFreshlyCompleted(c, nowUtc));
        var completedShown = completed.Take(Math.Max(slots, freshCount)).ToList();

        var visible = activeShown.Concat(completedShown).ToList();
        var hidden = active.Skip(activeShown.Count)
            .Concat(completed.Skip(completedShown.Count))
            .ToList();

        return new View(visible, hidden, running.Count, pending.Count, completed.Count);
    }
}
