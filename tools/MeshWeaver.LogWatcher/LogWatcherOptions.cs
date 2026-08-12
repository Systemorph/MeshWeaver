using System.Collections.Immutable;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// Configuration for the in-cluster log watcher, bound from the <c>LogWatcher</c> section (and
/// therefore from <c>LogWatcher__*</c> environment variables in the Deployment).
/// </summary>
public sealed record LogWatcherOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "LogWatcher";

    /// <summary>
    /// Loki's HTTP base URL. In-cluster this is the loki-stack service, e.g.
    /// <c>http://loki.monitoring.svc.cluster.local:3100</c>.
    /// </summary>
    public string LokiUrl { get; init; } = "http://loki.monitoring.svc.cluster.local:3100";

    /// <summary>
    /// Kubernetes namespaces to watch. Each is queried separately so a burst always knows which
    /// deployment it came from.
    /// </summary>
    public ImmutableList<string> Namespaces { get; init; } =
        ImmutableList.Create("memex", "memex-cloud");

    /// <summary>
    /// The portal that receives incidents (its <c>/api/log-incidents</c> endpoint), e.g.
    /// <c>https://memex.meshweaver.cloud</c>.
    /// </summary>
    public string? PortalUrl { get; init; }

    /// <summary>
    /// The shared secret presented to the portal as <c>Authorization: Bearer …</c>. Must match the
    /// portal's <c>LogWatch:IngestToken</c>. Ship via a Kubernetes secret, never in the chart.
    /// </summary>
    public string? IngestToken { get; init; }

    /// <summary>How often to poll Loki.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far back to read on a cold start (no persisted cursor). Deliberately short: on a fresh
    /// install the point is to start ticketing what happens NEXT, not to replay a week of history
    /// into a hundred issues at once.
    /// </summary>
    public TimeSpan ColdStartLookback { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back the cursor may ever be dragged. Caps the catch-up query after a long outage so
    /// one poll cannot ask Loki for a week of logs and time out forever.
    /// </summary>
    public TimeSpan MaxCatchUp { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Lag applied to the query's upper bound. Promtail ships with a small delay, so reading right
    /// up to "now" would repeatedly miss lines that land a second later — and, because the cursor
    /// would have moved past them, miss them permanently.
    /// </summary>
    public TimeSpan IngestLag { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Max entries Loki may return per query. The query is deliberately UNFILTERED (the grouper
    /// needs each red header's continuation lines), so this bounds every line in the window, not
    /// just the red ones — hence the headroom. Hitting it is safe: the worker advances the cursor
    /// only to the last line actually read and catches up on the next poll, logging that it did.
    /// </summary>
    public int QueryLimit { get; init; } = 5000;

    /// <summary>
    /// A window at least this long that comes back with ZERO lines is reported as a log-pipeline
    /// incident instead of being silently accepted as "nothing happened".
    ///
    /// <para>🚨 Why a LENGTH threshold, and why it is not sufficient on its own. The query is
    /// UNFILTERED (see <see cref="QueryLimit"/>), so a running portal cannot legitimately produce no
    /// lines at all. But in steady state the window is only about one <see cref="PollInterval"/>
    /// wide, and a genuinely idle namespace (a deployment scaled to zero) does return zero for those
    /// — not worth ticketing. Long windows arise three ways, and only ONE of them makes emptiness
    /// evidence: the cursor could not advance because every poll threw (Loki was unreachable) —
    /// that stretch we watched, so the store losing it is provable. The other two are a cold start
    /// (<see cref="ColdStartLookback"/> synthesises a window already wider than this threshold) and a
    /// cursor floored by <see cref="MaxCatchUp"/> after watcher downtime; there the watcher simply was
    /// not present. <see cref="WatcherState.IsContinuousCursor"/> separates them, and
    /// <c>LogPipelineGap.IsLostWindow</c> requires both.</para>
    ///
    /// <para>That is exactly what happened on 2026-08-12: Loki was evicted at 05:35, its emptyDir
    /// store went with it, and when it returned the watcher re-read the outage window, got nothing,
    /// advanced its cursor and reported nothing. The incident that destroyed the evidence was itself
    /// never ticketed.</para>
    /// </summary>
    public TimeSpan SilentWindowAlarm { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Evidence lines sent per report.</summary>
    public int MaxSamplesPerReport { get; init; } = 5;

    /// <summary>Each evidence line is truncated to this many characters.</summary>
    public int MaxSampleLength { get; init; } = 2000;

    /// <summary>
    /// Directory holding the cursor + the pending-report queue. Must be a mounted volume — on an
    /// <c>emptyDir</c> a pod restart replays the lookback window and re-reports what was already
    /// delivered.
    /// </summary>
    public string StateDirectory { get; init; } = "/var/lib/mw-log-watcher";

    /// <summary>
    /// Log categories never ticketed. Matched as a prefix. Use for known-noisy infrastructure
    /// chatter that is genuinely not a defect; prefer suppressing the incident in the portal, which
    /// keeps counting occurrences, over silencing it here, which does not.
    /// </summary>
    public ImmutableList<string> IgnoreCategories { get; init; } = ImmutableList<string>.Empty;

    /// <summary>True when the watcher has everything it needs to report.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PortalUrl) && !string.IsNullOrWhiteSpace(IngestToken);
}
