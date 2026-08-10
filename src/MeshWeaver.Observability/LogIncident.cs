using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Observability;

/// <summary>
/// What the incident's owner (the triage agent, or an admin in the UI) wants to happen next.
/// This is the <b>control-plane</b> half of the <c>Status</c>/<c>RequestedStatus</c> pair —
/// callers never mutate <see cref="LogIncident.Status"/> directly, they set this field via
/// <c>GetMeshNodeStream(path).Update(...)</c> and the owning watcher reacts
/// (Doc/Architecture/ActivityControlPlane.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogIncidentRequest>))]
public enum LogIncidentRequest
{
    /// <summary>Nothing requested — the watchers leave the incident alone.</summary>
    [Description("None")]
    [Translation("de", "Keine")]
    None,

    /// <summary>Run (or re-run) triage: hand the incident to the LogTriage agent.</summary>
    [Description("Triage")]
    [Translation("de", "Analysieren")]
    Triage,

    /// <summary>The draft is ready — open the issue on GitHub.</summary>
    [Description("File")]
    [Translation("de", "Melden")]
    File,

    /// <summary>The fingerprint recurred — comment on (and reopen, if closed) the existing issue.</summary>
    [Description("Comment")]
    [Translation("de", "Kommentieren")]
    Comment,

    /// <summary>Known noise — stop ticketing this fingerprint.</summary>
    [Description("Suppress")]
    [Translation("de", "Unterdrücken")]
    Suppress,
}

/// <summary>
/// The triage agent's proposed ticket. The agent writes this onto the incident node and flips
/// <see cref="LogIncident.RequestedStatus"/> to <see cref="LogIncidentRequest.File"/>; the filer
/// turns it into a real GitHub issue. Kept separate from the filed result so a re-triage can
/// replace the draft without losing the issue link.
/// </summary>
public record LogIncidentDraft
{
    /// <summary>The issue title. Should name the defect, not restate the log line.</summary>
    [Description("Title")]
    [Translation("de", "Titel")]
    public string? Title { get; init; }

    /// <summary>The issue body (markdown).</summary>
    [Description("Body")]
    [Translation("de", "Beschreibung")]
    public string? Body { get; init; }

    /// <summary>Label names to apply to the new issue.</summary>
    public ImmutableList<string> Labels { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// The repository the agent chose, as <c>owner/name</c> or a full URL. Null ⇒ the configured
    /// category-prefix route decides. Set this ONLY to override the route (e.g. the stack trace
    /// clearly blames a plugin) — and say why in <see cref="RepositoryReason"/>.
    /// </summary>
    [Description("Repository")]
    [Translation("de", "Repository")]
    public string? Repository { get; init; }

    /// <summary>Why the agent overrode the routed repository. Recorded on the filed issue.</summary>
    [Description("Routing reason")]
    [Translation("de", "Begründung der Zuordnung")]
    public string? RepositoryReason { get; init; }
}

/// <summary>
/// A distinct red-log <b>fingerprint</b> seen in a watched namespace — the unit this system
/// tickets. One incident node exists per fingerprint (<c>Admin/_LogIncident/{fingerprint}</c>),
/// so a burst of ten thousand identical errors is one node, one GitHub issue, and one
/// occurrence counter rather than ten thousand tickets.
///
/// <para>The in-cluster watcher owns detection and fingerprinting; the portal owns the
/// lifecycle. Everything after ingest is driven by the <see cref="Status"/> /
/// <see cref="RequestedStatus"/> control-plane pair.</para>
/// </summary>
public record LogIncident
{
    /// <summary>
    /// The node id, and the identity of "the same error happening again": a stable hash over the
    /// FAULT — its top application frame and exception type — falling back to the log site
    /// (category + event id) for a burst that names no frame. The message never participates, and
    /// neither does the category of whoever caught and printed the fault. See
    /// <see cref="StructuralLogIncidentIdentity"/>.
    /// </summary>
    [Browsable(false)]
    [Key]
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>The .NET log category, e.g. <c>MeshWeaver.Data.MeshDataSource</c>.</summary>
    [Description("Category")]
    [Translation("de", "Kategorie")]
    public string Category { get; init; } = string.Empty;

    /// <summary>Error or Critical.</summary>
    [Description("Severity")]
    [Translation("de", "Schweregrad")]
    public LogSeverity Severity { get; init; }

    /// <summary>The exception type name parsed out of the burst, when the line carried one.</summary>
    [Description("Exception")]
    [Translation("de", "Ausnahme")]
    public string? ExceptionType { get; init; }

    /// <summary>The message with volatile parts (ids, guids, paths, numbers) masked — what the
    /// fingerprint is computed over, and the most readable one-line summary of the fault.</summary>
    [Description("Message")]
    [Translation("de", "Meldung")]
    public string NormalizedMessage { get; init; } = string.Empty;

    /// <summary>The top application stack frame, when the burst carried a stack trace.</summary>
    [Description("Top frame")]
    [Translation("de", "Oberster Frame")]
    public string? TopFrame { get; init; }

    /// <summary>The Kubernetes namespace the lines came from (e.g. <c>memex</c>).</summary>
    [Description("Namespace")]
    [Translation("de", "Namespace")]
    public string? Namespace { get; init; }

    /// <summary>The pods this fingerprint has been seen on.</summary>
    public ImmutableList<string> Pods { get; init; } = ImmutableList<string>.Empty;

    /// <summary>How many matching lines have been seen, across every report.</summary>
    [Description("Occurrences")]
    [Translation("de", "Vorkommen")]
    public long Occurrences { get; init; }

    /// <summary>When this fingerprint was first seen.</summary>
    [Description("First seen")]
    [Translation("de", "Zuerst gesehen")]
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>When this fingerprint was last seen.</summary>
    [Description("Last seen")]
    [Translation("de", "Zuletzt gesehen")]
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Verbatim evidence lines, newest last. Bounded by the ingest service.</summary>
    [Browsable(false)]
    public ImmutableList<LogSample> Samples { get; init; } = ImmutableList<LogSample>.Empty;

    // ── Lifecycle (the control-plane pair) ───────────────────────────────────

    /// <summary>Where the incident is in its lifecycle. Written ONLY by the portal's watchers.</summary>
    [Description("Status")]
    [Translation("de", "Status")]
    [Editable(false)]
    public LogIncidentStatus Status { get; init; }

    /// <summary>What should happen next. Set by the triage agent or an admin; the watchers react.</summary>
    [Description("Requested")]
    [Translation("de", "Angefordert")]
    public LogIncidentRequest RequestedStatus { get; init; }

    /// <summary>The agent's proposed ticket, once triage has run.</summary>
    [Browsable(false)]
    public LogIncidentDraft? Draft { get; init; }

    /// <summary>Path of the triage thread, so the reasoning behind the ticket stays reachable.</summary>
    [Browsable(false)]
    public string? TriageThreadPath { get; init; }

    // ── Filed result ─────────────────────────────────────────────────────────

    /// <summary>The repository the issue was filed in, as <c>owner/name</c>.</summary>
    [Description("Repository")]
    [Translation("de", "Repository")]
    [Editable(false)]
    public string? Repository { get; init; }

    /// <summary>The filed issue's number.</summary>
    [Browsable(false)]
    public int? IssueNumber { get; init; }

    /// <summary>The filed issue's <c>html_url</c>.</summary>
    [Description("Issue")]
    [Translation("de", "Ticket")]
    [Editable(false)]
    public string? IssueUrl { get; init; }

    /// <summary>When the last recurrence comment was posted — the comment rate limit reads this.</summary>
    [Browsable(false)]
    public DateTimeOffset? LastCommentedAt { get; init; }

    /// <summary>The occurrence count at the last recurrence comment, so the next one can report a delta.</summary>
    [Browsable(false)]
    public long OccurrencesAtLastComment { get; init; }

    /// <summary>Why triage or filing failed, when <see cref="Status"/> is
    /// <see cref="LogIncidentStatus.Failed"/>.</summary>
    [Browsable(false)]
    public string? Error { get; init; }
}
