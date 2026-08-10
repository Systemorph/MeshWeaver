using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Observability;

/// <summary>
/// The portal's entry point for reported red-log bursts: create the incident on a FIRST sighting,
/// or fold a repeat into the existing one. This is where "any red log gets reported, but a burst
/// of ten thousand is still one ticket" is actually enforced.
///
/// <para>Reactive end-to-end and cold: <see cref="Report"/> does nothing until subscribed
/// (Doc/Architecture/AsynchronousCalls.md). Writes run under the <b>system</b> identity — the
/// watcher is infrastructure, not a signed-in user, and incidents live in the Admin partition
/// (Doc/Architecture/AccessContextPropagation.md).</para>
/// </summary>
public sealed class LogIncidentIngestService(
    IMessageHub hub,
    IOptions<LogWatchOptions> options,
    ILogger<LogIncidentIngestService>? logger = null)
{
    /// <summary>
    /// How long to wait for the incident's node stream to produce its first snapshot before
    /// concluding the node does not exist. A MISSING node normally resolves far sooner — the
    /// stream cache raises NotFound, which the read below catches — so this bound only covers the
    /// case where the owning hub never answers at all, and turning that into "treat as new" would
    /// duplicate an incident. It therefore propagates as a failure instead.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private readonly LogWatchOptions options = options.Value;

    /// <summary>
    /// Folds one reported burst into the mesh. Emits what happened, so the watcher can log the
    /// outcome without querying the mesh itself.
    ///
    /// <para>First sighting ⇒ a new node in <see cref="LogIncidentStatus.New"/> requesting
    /// <see cref="LogIncidentRequest.Triage"/> (the triage watcher takes it from there). Repeat ⇒
    /// occurrence count, last-seen, pods and samples are folded in; a repeat of an already-FILED
    /// incident additionally requests a recurrence comment, but only once per
    /// <see cref="LogWatchOptions.CommentInterval"/> so a continuously-firing fault cannot spam
    /// its own issue.</para>
    /// </summary>
    public IObservable<LogIncidentReportResult> Report(LogIncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(report.Fingerprint))
            return Observable.Throw<LogIncidentReportResult>(
                new ArgumentException("A report must carry a fingerprint.", nameof(report)));

        var path = LogIncidentNodeType.IncidentPath(report.Fingerprint);
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // 🚨 Observable.Using, not a `using` block: the identity has to be live when the write is
        // DISPATCHED (at subscribe), and a `using` around the chain's construction would already
        // have disposed by then. This is the canonical system-write shape (PackageInstaller,
        // CatalogLayoutAreas) — see Doc/Architecture/AccessContextPropagation.md.
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
            _ => ReadExisting(path).SelectMany(existing => existing is null
                ? Create(path, report)
                : Fold(path, existing, report)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  First sighting
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<LogIncidentReportResult> Create(string path, LogIncidentReport report)
    {
        var incident = new LogIncident
        {
            Fingerprint = report.Fingerprint,
            Category = report.Category,
            Severity = report.Severity,
            ExceptionType = report.ExceptionType,
            NormalizedMessage = report.NormalizedMessage,
            TopFrame = report.TopFrame,
            Namespace = report.Namespace,
            Pods = report.Pods,
            Occurrences = Math.Max(1, report.Occurrences),
            FirstSeen = report.FirstSeen,
            LastSeen = report.LastSeen,
            Samples = Trim(report.Samples),
            Status = LogIncidentStatus.New,
            // The control-plane request, not a direct status write: the triage watcher owns the
            // transition (Doc/Architecture/ActivityControlPlane.md).
            RequestedStatus = LogIncidentRequest.Triage,
        };

        var node = new MeshNode(report.Fingerprint, LogIncidentNodeType.IncidentNamespace)
        {
            NodeType = LogIncidentNodeType.NodeType,
            Name = Title(incident),
            State = MeshNodeState.Active,
            Content = incident,
        };

        logger?.LogInformation(
            "Red log first sighting {Fingerprint} in {Category} ({Occurrences} line(s)) — opening incident",
            report.Fingerprint, report.Category, report.Occurrences);

        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(response => response.Success
                ? Observable.Return(new LogIncidentReportResult(
                    path, Created: response.WasCreated, LogIncidentStatus.New, IssueUrl: null))
                : Observable.Throw<LogIncidentReportResult>(new InvalidOperationException(
                    $"Could not open incident {report.Fingerprint}: {response.Error}")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Repeat
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<LogIncidentReportResult> Fold(string path, LogIncident existing, LogIncidentReport report)
    {
        // The fold is computed INSIDE the update, against the node's live content — not against the
        // `existing` snapshot read a moment ago. Triage may have moved the status in between, and
        // folding onto the stale copy would write that transition back out.
        LogIncident? folded = null;
        return hub.UpdateIncident(path, current => folded = Merge(current, report, options), logger)
            .FirstAsync()
            .Select(_ =>
            {
                var result = folded ?? existing;
                logger?.LogInformation(
                    "Red log recurrence {Fingerprint} (+{New} ⇒ {Total}) — incident is {Status}",
                    report.Fingerprint, report.Occurrences, result.Occurrences, result.Status);
                return new LogIncidentReportResult(path, Created: false, result.Status, result.IssueUrl);
            });
    }

    /// <summary>
    /// Folds a repeat report into an existing incident. Pure — the whole recurrence policy
    /// (counting, sample retention, when a comment is due, when a closed issue should reopen) is
    /// decided here so it can be tested without a mesh.
    /// </summary>
    public static LogIncident Merge(LogIncident existing, LogIncidentReport report, LogWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);

        var occurrences = existing.Occurrences + Math.Max(1, report.Occurrences);
        var lastSeen = report.LastSeen > existing.LastSeen ? report.LastSeen : existing.LastSeen;
        var firstSeen = existing.FirstSeen == default || report.FirstSeen < existing.FirstSeen
            ? report.FirstSeen
            : existing.FirstSeen;

        var merged = existing with
        {
            Occurrences = occurrences,
            FirstSeen = firstSeen,
            LastSeen = lastSeen,
            Pods = existing.Pods.Union(report.Pods).ToImmutableList(),
            Samples = Trim(existing.Samples.AddRange(report.Samples), options.MaxSamples),
            // A recurrence can be MORE severe than the first sighting; never downgrade.
            Severity = report.Severity > existing.Severity ? report.Severity : existing.Severity,
        };

        return merged with { RequestedStatus = NextRequest(merged, options, lastSeen) };
    }

    /// <summary>
    /// What a recurrence should ask for. Suppressed incidents ask for nothing (that is the point of
    /// suppressing); an incident that already has an issue folds the recurrence into it, at most
    /// once per <see cref="LogWatchOptions.CommentInterval"/>; an incident whose triage never
    /// completed is re-requested so a portal restart mid-triage cannot strand it forever.
    ///
    /// <para>🚨 <b>The issue link outranks the status.</b> The check on
    /// <see cref="LogIncident.IssueNumber"/> comes before the <c>New</c>/<c>Failed</c> retry rule
    /// deliberately: a ticketed incident that later goes <c>Failed</c> — a recurrence comment that
    /// errored, or the stranded-triage reconcile marking it retryable — used to re-enter triage,
    /// and the agent's fresh draft then asked to <c>File</c> again. That chain is how
    /// <c>ROUTER_TRAFFIC</c> was filed EIGHT times in seven minutes on the watcher's first live run.
    /// Once an issue exists, a recurrence belongs on it, never in a new one.</para>
    ///
    /// <para>🚨 <b><c>Filing</c> without an issue is the one in-flight status that is re-asked.</b>
    /// The claim writes <c>Filing</c> BEFORE the GitHub round-trip, so a portal that dies in that
    /// window — or a write-back that fails — leaves the incident at <c>Filing</c> with no link.
    /// Nothing else can move it: the claim keys on <c>RequestedStatus</c>, which is already
    /// <c>None</c>; the stranded-triage reconcile only ever touches <c>Triaging</c>; and every rule
    /// above needs either an issue or a different status. That is precisely the dead end
    /// <c>Triaging</c> used to be — nineteen incidents accumulated there, invisible and un-ticketed —
    /// only now in a status the repair does not cover. Asking again is safe BECAUSE of the claim:
    /// a File request on an incident that has since been ticketed is redirected to the
    /// rate-limited comment, never to a second issue.</para>
    /// </summary>
    private static LogIncidentRequest NextRequest(
        LogIncident incident, LogWatchOptions options, DateTimeOffset now) => incident switch
    {
        { Status: LogIncidentStatus.Suppressed } => LogIncidentRequest.None,
        { IssueNumber: not null } => CommentDue(incident, options, now)
            ? LogIncidentRequest.Comment
            : LogIncidentRequest.None,
        { Status: LogIncidentStatus.Filed } => LogIncidentRequest.None,
        // New/Failed → (re)try triage. Triaging → leave the in-flight round alone; the control
        // plane reconciles it against the thread, the only authority on whether it is still running.
        { Status: LogIncidentStatus.New or LogIncidentStatus.Failed } => LogIncidentRequest.Triage,
        // Filing with no link ⇒ the claim was taken and nothing came back. Re-ask — but ONLY when
        // nothing is already being asked. The stranded case is by definition RequestedStatus.None
        // (the claim consumes the request before it writes Filing, which is why nothing else can
        // move it), so scoping the arm this way costs the repair nothing. Unscoped it would
        // overwrite a request someone made deliberately — an admin's Suppress, or any future
        // control-plane verb — on the very next report, and the incident would file itself anyway.
        // An automated recovery must never outrank an explicit instruction.
        { Status: LogIncidentStatus.Filing, RequestedStatus: LogIncidentRequest.None }
            => LogIncidentRequest.File,
        _ => incident.RequestedStatus,
    };

    /// <summary>
    /// Whether this incident may speak on its issue again. The ONE rate limit on recurrence noise —
    /// the control plane's claim reads it too, so a <c>File</c> request that arrives on an
    /// already-ticketed incident is bounded by exactly the same window as a <c>Comment</c> request.
    /// </summary>
    internal static bool CommentDue(LogIncident incident, LogWatchOptions options, DateTimeOffset now) =>
        incident.LastCommentedAt is not { } last || now - last >= options.CommentInterval;

    // ══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The incident's current content, or null when the node does not exist yet.
    ///
    /// <para>Reads the authoritative node stream, never a query: a query index is eventually
    /// consistent, so right after a create it would still report "absent" and the next report
    /// would overwrite the incident's lifecycle back to New
    /// (Doc/Architecture/CqrsAndContentAccess.md). A missing node surfaces as a NotFound error on
    /// the stream, which is what the catch below turns into "absent".</para>
    /// </summary>
    private IObservable<LogIncident?> ReadExisting(string path) =>
        hub.GetWorkspace().GetMeshNodeStream(path)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(ReadTimeout)
            .Select(node => node.ContentAs<LogIncident>(hub.JsonSerializerOptions, logger))
            .Catch((Exception ex) =>
            {
                // A timeout is NOT "absent" — treating an unresponsive owner as a first sighting
                // would duplicate the incident and re-file the ticket. Only propagate.
                if (ex is TimeoutException)
                {
                    logger?.LogWarning(ex, "Incident stream {Path} did not answer within {Timeout}", path, ReadTimeout);
                    return Observable.Throw<LogIncident?>(ex);
                }
                logger?.LogDebug("Incident {Path} not found — treating as a first sighting ({Reason})",
                    path, ex.Message);
                return Observable.Return<LogIncident?>(null);
            });

    private ImmutableList<LogSample> Trim(ImmutableList<LogSample> samples) => Trim(samples, options.MaxSamples);

    private static ImmutableList<LogSample> Trim(ImmutableList<LogSample> samples, int max) =>
        max > 0 && samples.Count > max
            ? samples.RemoveRange(0, samples.Count - max)
            : samples;

    /// <summary>
    /// The node's display name — what an admin sees in the incident list and what the fingerprint
    /// is recognisable by. Short by design; the full message lives in the content.
    /// </summary>
    private static string Title(LogIncident incident)
    {
        var subject = incident.ExceptionType is { Length: > 0 } ex
            ? ex
            : incident.NormalizedMessage;
        if (subject.Length > 80)
            subject = subject[..80].TrimEnd() + "…";
        var category = incident.Category.Split('.').LastOrDefault() ?? incident.Category;
        return $"{category}: {subject}";
    }
}
