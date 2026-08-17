using System.Collections.Immutable;
using MeshWeaver.Observability;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// The watcher's judgement about its OWN input: when a query result means "the log store lost this
/// stretch" rather than "nothing happened".
///
/// <para>🚨 Extracted so the decision is testable on its own. It plugs the one hole in the watcher's
/// otherwise-safe outage handling: while Loki is unreachable the query THROWS, the pass is logged
/// loudly and the cursor does not advance — that half was always correct. It is the RECOVERY, against
/// a store that came back EMPTY, that used to pass in silence: zero lines, no reports, cursor
/// advanced, window gone. memex-cloud 2026-08-12 lost its whole incident window that way, and the
/// red-log ticketing — which reads FROM Loki — filed nothing about the outage that blinded it.</para>
/// </summary>
public static class LogPipelineGap
{
    /// <summary>
    /// True when a query result proves lost evidence: NOTHING came back for a window far longer than
    /// a poll interval, over a stretch we were CONTINUOUSLY WATCHING.
    ///
    /// <para>Both conditions are load-bearing. The window's LENGTH separates the two empty cases
    /// without a heuristic: in steady state the window is about one poll wide, so a genuinely idle
    /// namespace (a deployment scaled to zero) yields short empty windows that are not worth
    /// ticketing. And CONTINUITY is what licenses the conclusion — a long window can also come from
    /// a cold start (<see cref="LogWatcherOptions.ColdStartLookback"/> synthesises one 15 min wide,
    /// already past the alarm threshold) or from a cursor floored by
    /// <see cref="LogWatcherOptions.MaxCatchUp"/> after real watcher downtime. In those the watcher
    /// simply was not there, so emptiness says nothing about the store. Only when we polled that
    /// stretch ourselves and the store now shows nothing has evidence gone missing.</para>
    /// </summary>
    /// <param name="lineCount">Lines the (UNFILTERED) query returned.</param>
    /// <param name="window">The queried window's length.</param>
    /// <param name="alarmAfter">Minimum window length for an empty result to count as loss.</param>
    /// <param name="continuouslyWatched">
    /// Whether the window came from a persisted, un-floored cursor —
    /// <see cref="WatcherState.IsContinuousCursor"/>.
    /// </param>
    public static bool IsLostWindow(
        int lineCount, TimeSpan window, TimeSpan alarmAfter, bool continuouslyWatched) =>
        lineCount == 0 && window >= alarmAfter && continuouslyWatched;

    /// <summary>
    /// The incident for a lost window.
    ///
    /// <para>The fingerprint is per-namespace and carries NO timestamps on purpose: a repeat is the
    /// same incident (this namespace's log pipeline is losing windows), so it dedups into one node
    /// whose occurrence count says how often, instead of one node per outage. It travels the normal
    /// ingest path to the portal, which lands it in Postgres — the one store that survives Loki being
    /// gone, which is the entire point of reporting it there.</para>
    /// </summary>
    public static LogIncidentReport Report(string ns, DateTimeOffset start, DateTimeOffset end) =>
        new()
        {
            Fingerprint = $"log-pipeline-silent-window-{ns}",
            NormalizedDetail = "Loki returned zero lines for a long, continuously-watched window.",
            Category = "MeshWeaver.LogWatcher.Pipeline",
            Severity = LogSeverity.Critical,
            NormalizedMessage =
                $"Log pipeline lost a window for namespace '{ns}': an UNFILTERED Loki query over a "
                + "long window returned zero lines, so the store cannot show what happened there. "
                + "Check whether Loki was evicted (it must not be BestEffort, must not run on the "
                + "workload node pool, and must not use an emptyDir store) or whether nothing is "
                + "deployed in that namespace.",
            Namespace = ns,
            Occurrences = 1,
            FirstSeen = start,
            LastSeen = end,
            Samples = ImmutableList.Create(new LogSample(
                end,
                null,
                $"Loki query_range [{start:u}, {end:u}) — {(end - start).TotalMinutes:F0} minute(s), "
                + "0 lines returned")),
        };

    /// <summary>
    /// True when the query came back AT its entry limit — meaning the window was not fully read and
    /// the remainder is, at this moment, <b>unseen</b>.
    ///
    /// <para>Loki returns at most <c>limit</c> entries, and the watcher's query is deliberately
    /// unfiltered, so a busy namespace hits the cap on lines of every severity. The cursor is then
    /// advanced only to the last line actually read, which makes the remainder <i>deferred</i>
    /// rather than lost — but only as long as the watcher out-reads the namespace. It never used to
    /// say any of this anywhere a human reads a verdict; on 2026-08-17 several consecutive windows
    /// on memex-cloud reported exactly 5000 lines and nothing recorded that the number was a CAP.</para>
    /// </summary>
    /// <param name="lineCount">Lines the query returned.</param>
    /// <param name="limit">The limit the query asked for.</param>
    /// <returns>True when the window was truncated.</returns>
    public static bool IsTruncated(int lineCount, int limit) => limit > 0 && lineCount >= limit;

    /// <summary>
    /// The incident for a truncated window.
    ///
    /// <para>🚨 <b>Raising the limit is not the fix and this report is not a nag.</b> Truncation
    /// means one namespace is producing lines faster than the watcher reads them, so a dominant
    /// noisy source can push every other error out of the sample the watcher actually looks at —
    /// which is precisely the case where the QUIET defect is the one that matters. The actionable
    /// number is the backlog (<paramref name="end"/> minus the cursor the read reached), because a
    /// backlog that keeps growing ends at the <see cref="LogWatcherOptions.MaxCatchUp"/> floor,
    /// where the skipped window IS lost (<see cref="SkippedWindowReport"/>).</para>
    ///
    /// <para>Per-namespace fingerprint, no timestamps: repeats are the same finding — this namespace
    /// out-talks its watcher — so they fold into one incident with a rising occurrence count.</para>
    /// </summary>
    /// <param name="ns">The namespace whose window was truncated.</param>
    /// <param name="start">The window's start.</param>
    /// <param name="end">The window's intended end.</param>
    /// <param name="reached">The timestamp the read actually reached — where the next poll resumes.</param>
    /// <param name="limit">The entry limit that was hit.</param>
    /// <returns>The report to queue.</returns>
    public static LogIncidentReport TruncatedReport(
        string ns, DateTimeOffset start, DateTimeOffset end, DateTimeOffset reached, int limit) =>
        new()
        {
            Fingerprint = $"log-query-truncated-{ns}",
            Category = "MeshWeaver.LogWatcher.Pipeline",
            Severity = LogSeverity.Error,
            NormalizedMessage =
                $"Log query truncated for namespace '{ns}': Loki returned its full {limit}-entry limit, "
                + "so the window was NOT fully read. The unread remainder is deferred to the next poll, "
                + "not dropped — but while a namespace out-talks its watcher, a noisy source crowds "
                + "quieter errors out of every sample, and a backlog that keeps growing eventually hits "
                + "the MaxCatchUp floor, where the skipped window IS lost. Find and quieten the dominant "
                + "source; raising the limit only moves the cap.",
            NormalizedDetail = $"Loki returned its full {limit}-entry limit for this namespace.",
            Namespace = ns,
            Occurrences = 1,
            FirstSeen = start,
            LastSeen = end,
            Samples = ImmutableList.Create(new LogSample(
                end,
                null,
                $"Loki query_range [{start:u}, {end:u}) — {limit} entries returned (the limit); "
                + $"resuming at {reached:u}, leaving a {(end - reached).TotalSeconds:F0}s backlog")),
        };

    /// <summary>
    /// The incident for a window the watcher will never read: a cursor older than
    /// <see cref="LogWatcherOptions.MaxCatchUp"/> is dragged forward, and everything between the old
    /// cursor and the new floor is skipped for good.
    ///
    /// <para>This is the ONE path in the watcher that genuinely loses evidence rather than deferring
    /// it, and until now it only said so in the watcher's own pod log — the exact "reported nowhere a
    /// verdict is read" shape that #1787 is about. Critical, because red logs in that stretch were
    /// never ticketed and never will be.</para>
    /// </summary>
    /// <param name="ns">The namespace whose cursor was floored.</param>
    /// <param name="from">The old cursor — the start of the skipped stretch.</param>
    /// <param name="to">The floor the cursor was dragged to — the end of it.</param>
    /// <returns>The report to queue.</returns>
    public static LogIncidentReport SkippedWindowReport(string ns, DateTimeOffset from, DateTimeOffset to) =>
        new()
        {
            Fingerprint = $"log-window-skipped-{ns}",
            Category = "MeshWeaver.LogWatcher.Pipeline",
            Severity = LogSeverity.Critical,
            NormalizedMessage =
                $"Log window SKIPPED for namespace '{ns}': the cursor was older than the catch-up cap "
                + "and was dragged forward, so red logs in the skipped stretch were never ticketed and "
                + "cannot be recovered. Either the watcher was down for longer than MaxCatchUp, or it "
                + "cannot keep up with this namespace's log volume.",
            NormalizedDetail = "The cursor was floored by MaxCatchUp; the skipped stretch is unrecoverable.",
            Namespace = ns,
            Occurrences = 1,
            FirstSeen = from,
            LastSeen = to,
            Samples = ImmutableList.Create(new LogSample(
                to,
                null,
                $"Skipped [{from:u}, {to:u}) — {(to - from).TotalMinutes:F0} minute(s) never read")),
        };
}
