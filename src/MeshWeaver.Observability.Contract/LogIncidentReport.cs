using System.Collections.Immutable;

namespace MeshWeaver.Observability;

/// <summary>
/// The wire shape the in-cluster log watcher POSTs to <c>/api/log-incidents</c>: one already
/// fingerprinted, already deduplicated burst. The watcher does the Loki reading, the grouping and
/// the fingerprinting; the portal does the lifecycle. Keeping the fingerprint on the wire (rather
/// than recomputing it here) means the watcher's local dedup state and the mesh's incident nodes
/// agree by construction — there is no second, drifting definition of "the same error".
///
/// <para>The report is <b>cumulative-safe</b>: <see cref="Occurrences"/> counts only the lines in
/// THIS report, so a replayed report double-counts occurrences but never creates a duplicate
/// incident. That is the intended trade — at-least-once delivery with a stable identity.</para>
/// </summary>
public record LogIncidentReport
{
    /// <summary>The burst's fingerprint — the incident's identity and node id.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The .NET log category the lines were emitted under.</summary>
    public required string Category { get; init; }

    /// <summary>Error (<c>fail:</c>) or Critical (<c>crit:</c>).</summary>
    public LogSeverity Severity { get; init; }

    /// <summary>The exception type name, when the burst carried one.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The logged message with volatile parts masked.</summary>
    public required string NormalizedMessage { get; init; }

    /// <summary>
    /// The masked text the fingerprint discriminates on within the fault site: the exception's own
    /// message when the burst carried one, else the logged message. This is what tells thirteen
    /// different compiler errors at one frame apart from each other.
    /// </summary>
    public string NormalizedDetail { get; init; } = string.Empty;

    /// <summary>
    /// How many distinct message shapes this report covers. Always <c>1</c> except when a log site
    /// blew past the watcher's per-site variant budget in one window and its bursts were folded onto
    /// a single site-level incident — then it is the number of shapes that were folded in, and the
    /// ticket says so instead of opening that many tickets.
    /// </summary>
    public int Variants { get; init; } = 1;

    /// <summary>The top application stack frame, when the burst carried a stack trace.</summary>
    public string? TopFrame { get; init; }

    /// <summary>The Kubernetes namespace the lines came from.</summary>
    public string? Namespace { get; init; }

    /// <summary>The pods the lines came from.</summary>
    public ImmutableList<string> Pods { get; init; } = ImmutableList<string>.Empty;

    /// <summary>How many matching lines this report covers.</summary>
    public long Occurrences { get; init; }

    /// <summary>Earliest line timestamp in this report.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>Latest line timestamp in this report.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Verbatim evidence lines (the watcher caps how many it sends).</summary>
    public ImmutableList<LogSample> Samples { get; init; } = ImmutableList<LogSample>.Empty;
}

/// <summary>The portal's answer to one reported burst — enough for the watcher to log usefully
/// without polling the mesh.</summary>
/// <param name="Path">The incident node's path.</param>
/// <param name="Created">True when this report created the incident (a first sighting).</param>
/// <param name="Status">The incident's lifecycle status after ingest.</param>
/// <param name="IssueUrl">The filed issue, when one already exists.</param>
public record LogIncidentReportResult(
    string Path,
    bool Created,
    LogIncidentStatus Status,
    string? IssueUrl);
