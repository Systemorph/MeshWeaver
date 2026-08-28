using System.Collections.Immutable;

namespace MeshWeaver.Observability;

/// <summary>
/// The last line of defence against the degenerate incident of #2222/#2466: a report whose entire
/// identity is <c>(category, event id)</c> — no message, no exception type, no stack frame — or
/// whose "message" is nothing but an echo of the console header itself.
///
/// <para>🚨 <b>Why this exists when <c>BurstAggregator</c> already refuses to fingerprint bodyless
/// bursts</b> (#2466): the aggregator runs in the WATCHER, which is a separately shipped binary
/// with no delivery lane of its own. The exclusion merged on 2026-08-25; the <c>mw-log-watcher</c>
/// image running in the cluster was built on 2026-08-10 — its parser still fabricated a message
/// out of the console header (producing exactly
/// <c>MeshWeaver.Connection.Orleans.OrleansRoutingService[{n}]</c> and <c>Orleans.Messaging[{n}]</c>)
/// and kept minting and folding undiagnosable incidents two days after the "fix" was green on
/// main. A classification that only the sender enforces is not enforced: the ingest boundary — the
/// side that PERSISTS the incident and lives on the portal's continuous delivery — must refuse the
/// shape too, whatever vintage the watcher is.</para>
///
/// <para>This lives in the contract, beside the wire shape, so the watcher, the aggregator's tests
/// and the portal's ingest all share ONE definition of "carries no diagnostic". It deliberately
/// mirrors <c>LogLineParser.ParsedBurst.IsHeaderOnly</c> at the report level.</para>
/// </summary>
public static class LogIncidentReportSanity
{
    /// <summary>
    /// The category the log pipeline files its own findings under — capture gaps, lost windows,
    /// truncation. The watcher's <c>LogPipelineGap</c> reports use it too, so both sides fold onto
    /// the same incidents.
    /// </summary>
    public const string PipelineCategory = "MeshWeaver.LogWatcher.Pipeline";

    /// <summary>
    /// The per-namespace fingerprint for "red bursts arrived with no body". The watcher's
    /// <c>LogPipelineGap.HeaderOnlyReport</c> uses it as well, so a report the ingest refuses folds
    /// onto the very incident a current watcher would have filed for it.
    /// </summary>
    public static string HeaderOnlyFingerprint(string? ns) => $"log-burst-header-only-{ns ?? "unknown"}";

    /// <summary>
    /// True when the report carries no diagnostic an engineer could act on: no exception type, no
    /// application stack frame, and a message that is either empty or a mere echo of the console
    /// header (<c>Category[{n}]</c> / the bare category) — the shape the pre-#2222 parser
    /// fabricated for a bodyless burst.
    ///
    /// <para>Deliberately narrow. A report with a real logged message — however short — is
    /// diagnosable and always passes; so does anything carrying an exception type or a frame,
    /// and the pipeline's own findings (which have long prose messages). The failure direction of
    /// a false positive is a reroute into the per-namespace capture-gap incident, where the
    /// category is still named — recoverable and visible, never silent.</para>
    /// </summary>
    public static bool IsUndiagnosable(LogIncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.ExceptionType is { Length: > 0 } || report.TopFrame is { Length: > 0 })
            return false;

        // A detail that says more than the message is a diagnostic in its own right.
        if (report.NormalizedDetail is { Length: > 0 } detail
            && !string.Equals(detail, report.NormalizedMessage, StringComparison.Ordinal))
            return false;

        var message = report.NormalizedMessage;
        if (message.Length == 0)
            return true;

        // The header echo, compared through the same masking the watcher applied — so a category
        // containing digits ("memory-7") matches its own masked echo ("memory-{n}[{n}]") too.
        return string.Equals(message, LogLineParser.Normalize(report.Category), StringComparison.Ordinal)
               || string.Equals(message, LogLineParser.Normalize(report.Category + "[0]"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reroutes an undiagnosable report onto the per-namespace capture-gap incident — the same one
    /// a current watcher files via <c>LogPipelineGap.HeaderOnlyReport</c> — instead of letting it
    /// mint (or fold into) an incident that names a component and no defect. The original category
    /// and fingerprint survive as the first evidence sample, so the finding still says WHERE the
    /// bodyless capture came from.
    /// </summary>
    public static LogIncidentReport AsCaptureGap(LogIncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new LogIncidentReport
        {
            Fingerprint = HeaderOnlyFingerprint(report.Namespace),
            Category = PipelineCategory,
            // Never downgrade a crit: the merge rule keeps the max anyway.
            Severity = report.Severity > LogSeverity.Error ? report.Severity : LogSeverity.Error,
            NormalizedMessage =
                $"Red log line(s) in namespace '{report.Namespace ?? "unknown"}' arrived with NO "
                + "body: a 'fail:'/'crit:' header and no message, no exception and no stack frame. "
                + "They are deliberately not fingerprinted — an incident keyed on category+event id "
                + "alone names a component and no defect, and swallows every later bodyless capture "
                + "from the same site. Fix it at the category named in the samples: either the call "
                + "site logs an empty message with no exception (pass the exception, or give the "
                + "message a template), or its lines are being dropped between the pod and the log "
                + "store — or an out-of-date log watcher is still fingerprinting headers.",
            NormalizedDetail = "A red console header reached the watcher with no body.",
            Namespace = report.Namespace,
            Pods = report.Pods,
            Occurrences = report.Occurrences,
            FirstSeen = report.FirstSeen,
            LastSeen = report.LastSeen,
            Samples = ImmutableList
                .Create(new LogSample(
                    report.LastSeen,
                    null,
                    $"refused undiagnosable report {report.Fingerprint} for category "
                    + $"{report.Category} — no message, no exception, no stack frame"))
                .AddRange(report.Samples),
        };
    }
}
