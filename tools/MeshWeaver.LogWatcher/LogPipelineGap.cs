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
}
