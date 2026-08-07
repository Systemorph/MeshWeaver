using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.GitSync;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Observability;

/// <summary>
/// Turns a triaged incident into a GitHub issue — and keeps that issue honest as the fault
/// recurs (a bounded "still happening" comment, and a reopen when a closed issue's cause comes
/// back).
///
/// <para>Authenticates as the <b>GitHub App</b>, never a user: this runs unattended, and a red log
/// at 03:00 must not depend on whose OAuth token happens to be stored. That is the same machine
/// identity the plugin registry uses (<see cref="GitHubAppTokenService"/>).</para>
///
/// <para>Pure service: every method returns a cold <see cref="IObservable{T}"/> that emits the
/// UPDATED incident content and performs no mesh write of its own — the control plane owns the
/// node. Reactive throughout; the GitHub leaves are already pooled inside the repo client.</para>
/// </summary>
public sealed class LogIncidentFiler(
    IGitHubRepoClient? repoClient,
    GitHubAppTokenService? appTokens,
    IOptions<LogWatchOptions> options,
    ILogger<LogIncidentFiler>? logger = null)
{
    private readonly LogWatchOptions options = options.Value;

    /// <summary>
    /// True when this deployment can actually open issues: GitSync is wired AND the App identity is
    /// configured. Both are nullable dependencies on purpose — red-log ingest is useful on its own
    /// (incidents are recorded and visible), so a host that has not wired GitSync must still be able
    /// to register this module rather than failing to start.
    /// </summary>
    public bool IsConfigured => repoClient is not null && appTokens?.IsConfigured == true;

    // ══════════════════════════════════════════════════════════════════════════
    //  Open the ticket
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the issue for a triaged incident and emits the incident updated with the filed
    /// result. Refuses (throws) rather than guessing when there is no draft, no destination, or no
    /// App credential — a silently-unfiled incident is exactly the failure mode this system
    /// exists to prevent, so the error lands on the node instead.
    /// </summary>
    public IObservable<LogIncident> File(LogIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        if (Unconfigured(incident) is { } unconfigured)
            return unconfigured;
        if (incident.Draft is not { } draft || string.IsNullOrWhiteSpace(draft.Title))
            return Fail(incident, "Triage produced no ticket draft.");

        var route = RepositoryRouter.Resolve(incident, options);
        if (route.RejectedOverride is { } rejected)
            logger?.LogWarning(
                "Incident {Fingerprint}: triage proposed repository '{Proposed}' — refused (not an allowed "
                + "destination); filing to the routed '{Routed}' instead",
                incident.Fingerprint, rejected, route.Repository);
        if (RepositoryRouter.ToUrl(route.Repository) is not { } repositoryUrl)
            return Fail(incident,
                $"No repository is routed for category '{incident.Category}' and no default is configured.");

        var labels = draft.Labels.Union(options.Labels).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        return appTokens!.GetInstallationToken().SelectMany(token =>
            repoClient!.CreateIssue(new GitHubCreateIssueRequest
            {
                RepositoryUrl = repositoryUrl,
                Title = draft.Title!,
                Body = Body(incident, draft, route),
                Labels = labels,
                AccessToken = token,
            })).Select(issue =>
        {
            logger?.LogInformation(
                "Incident {Fingerprint} filed as {Repository}#{Number}",
                incident.Fingerprint, RepositoryRouter.Normalize(route.Repository), issue.Number);
            return incident with
            {
                Status = LogIncidentStatus.Filed,
                RequestedStatus = LogIncidentRequest.None,
                Repository = RepositoryRouter.Normalize(route.Repository),
                IssueNumber = issue.Number,
                IssueUrl = issue.Url,
                OccurrencesAtLastComment = incident.Occurrences,
                LastCommentedAt = null,
                Error = null,
            };
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Keep it honest as the fault recurs
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a recurrence on the existing issue: reopens it first when it had been closed and
    /// <see cref="LogWatchOptions.ReopenOnRecurrence"/> is on, then posts one comment summarising
    /// how much the fault has fired since the last one. Emits the incident with the comment
    /// bookkeeping updated.
    /// </summary>
    public IObservable<LogIncident> Comment(LogIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        if (Unconfigured(incident) is { } unconfigured)
            return unconfigured;
        if (incident.IssueNumber is not { } number)
            return Fail(incident, "Cannot comment: the incident has no filed issue.");
        if (RepositoryRouter.ToUrl(incident.Repository) is not { } repositoryUrl)
            return Fail(incident, "Cannot comment: the incident has no repository.");

        return appTokens!.GetInstallationToken().SelectMany(token =>
            Reopen(repositoryUrl, number, token)
                .SelectMany(reopened => repoClient!
                    .CommentIssue(repositoryUrl, number, RecurrenceComment(incident, reopened), token)
                    .Select(_ => reopened)))
            .Select(reopened =>
            {
                logger?.LogInformation(
                    "Incident {Fingerprint} recurrence commented on {Repository}#{Number}{Reopened}",
                    incident.Fingerprint, incident.Repository, number, reopened ? " (reopened)" : "");
                return incident with
                {
                    RequestedStatus = LogIncidentRequest.None,
                    LastCommentedAt = incident.LastSeen,
                    OccurrencesAtLastComment = incident.Occurrences,
                    Error = null,
                };
            });
    }

    /// <summary>
    /// Reopens the issue when it is closed and the policy allows it. Emits whether a reopen
    /// actually happened, so the comment can say so. Reading the issue first is deliberate: it is
    /// one cheap GET that keeps the system from "reopening" an already-open issue on every
    /// recurrence.
    /// </summary>
    private IObservable<bool> Reopen(string repositoryUrl, int number, string token)
    {
        if (!options.ReopenOnRecurrence)
            return Observable.Return(false);

        return repoClient!.GetIssue(repositoryUrl, number, token).SelectMany(issue =>
            issue.State == GitHubIssueState.Closed
                ? repoClient.SetIssueState(repositoryUrl, number, GitHubIssueState.Open, token)
                    .Select(_ => true)
                : Observable.Return(false));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Ticket text
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The issue body: the agent's write-up, then a machine-generated evidence footer. The footer
    /// is what makes the ticket actionable months later — the fingerprint (so a human can find the
    /// incident node), the counts, and the verbatim lines.
    /// </summary>
    private static string Body(LogIncident incident, LogIncidentDraft draft, RepositoryRouter.Route route)
    {
        var sb = new StringBuilder();
        sb.AppendLine(draft.Body ?? "_Triage produced no description._");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### Evidence");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Fingerprint | `{incident.Fingerprint}` |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Category | `{incident.Category}` |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Severity | {incident.Severity} |");
        if (incident.ExceptionType is { Length: > 0 } ex)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Exception | `{ex}` |");
        if (incident.TopFrame is { Length: > 0 } frame)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Top frame | `{frame}` |");
        if (incident.Namespace is { Length: > 0 } ns)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Namespace | `{ns}` |");
        if (!incident.Pods.IsEmpty)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Pods | {string.Join(", ", incident.Pods.Select(p => $"`{p}`"))} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Occurrences | {incident.Occurrences} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| First seen | {Format(incident.FirstSeen)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Last seen | {Format(incident.LastSeen)} |");
        if (route.Overridden)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| Routing | overridden by triage — {draft.RepositoryReason ?? "no reason given"} |");
        sb.AppendLine();
        AppendSamples(sb, incident.Samples);
        sb.AppendLine();
        var incidentPath = LogIncidentNodeType.IncidentPath(incident.Fingerprint);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"<sub>Opened automatically from `{incidentPath}`. Recurrences are folded into this issue rather than opening new ones.</sub>");
        return sb.ToString();
    }

    private static string RecurrenceComment(LogIncident incident, bool reopened)
    {
        var since = incident.Occurrences - incident.OccurrencesAtLastComment;
        var sb = new StringBuilder();
        if (reopened)
            sb.AppendLine("**Reopened — this fault is happening again.**").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Still occurring: **{since}** more since the last update (**{incident.Occurrences}** total).");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Last seen: {Format(incident.LastSeen)}");
        if (!incident.Pods.IsEmpty)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- Pods: {string.Join(", ", incident.Pods.Select(p => $"`{p}`"))}");
        sb.AppendLine();
        AppendSamples(sb, incident.Samples.Count > 3
            ? incident.Samples.RemoveRange(0, incident.Samples.Count - 3)
            : incident.Samples);
        return sb.ToString();
    }

    private static void AppendSamples(StringBuilder sb, ImmutableList<LogSample> samples)
    {
        if (samples.IsEmpty)
            return;
        sb.AppendLine("<details><summary>Recent log lines</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var sample in samples)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{Format(sample.Timestamp)} {sample.Pod ?? "-"} {sample.Line}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The precise reason filing is impossible on this deployment, or null when it is possible.</summary>
    private IObservable<LogIncident>? Unconfigured(LogIncident incident) => repoClient switch
    {
        null => Fail(incident, "GitHub integration is not wired into this deployment (MeshWeaver.GitSync)."),
        _ when appTokens?.IsConfigured != true =>
            Fail(incident, "GitHub App credentials are not configured (GitHub:App)."),
        _ => null,
    };

    private IObservable<LogIncident> Fail(LogIncident incident, string reason)
    {
        logger?.LogWarning("Incident {Fingerprint}: {Reason}", incident.Fingerprint, reason);
        return Observable.Throw<LogIncident>(new InvalidOperationException(reason));
    }
}
