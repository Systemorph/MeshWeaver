using System.Collections.Immutable;
namespace MeshWeaver.Observability;

/// <summary>One log line as the collector read it, before any grouping.</summary>
/// <param name="Timestamp">The line's timestamp.</param>
/// <param name="Namespace">The Kubernetes namespace the line came from.</param>
/// <param name="Pod">The pod the line came from.</param>
/// <param name="Line">The verbatim line.</param>
public record LogLine(DateTimeOffset Timestamp, string? Namespace, string? Pod, string Line);

/// <summary>
/// What one window of log lines amounted to.
///
/// <para>The three numbers are reported separately on purpose. Before #1787 the watcher logged
/// <c>"{Bursts} distinct fingerprint(s) from {Lines} red line(s)"</c> with <c>Lines</c> bound to the
/// TOTAL line count — the query is deliberately unfiltered — so "1 distinct fingerprint from 5000
/// red line(s)" read as a catastrophic collapse of 5000 errors when it was 5000 lines of every
/// severity. A verdict nobody can interpret is not a verdict.</para>
/// </summary>
/// <param name="Reports">One report per distinct fingerprint, oldest first.</param>
/// <param name="RedBursts">How many red (<c>fail:</c>/<c>crit:</c>) bursts were parsed — the number
/// the fingerprint count should be compared against.</param>
/// <param name="TotalLines">Every line the query returned, red or not.</param>
/// <param name="FoldedSites">How many log sites exceeded the per-site variant budget and were
/// collapsed onto a single site-level incident.</param>
/// <param name="HeaderOnly">The red bursts that arrived as a console header and NOTHING else. They
/// are deliberately NOT in <paramref name="Reports"/> — a burst with no message, no exception and no
/// frame can only fingerprint on its category and event id, which is a token that names a component
/// and no defect (#2222). They are surfaced here instead so the caller can recover the ones that are
/// recoverable and REPORT the ones that are not, rather than either fabricating an incident or
/// dropping them in silence.</param>
public record BurstAggregation(
    ImmutableList<LogIncidentReport> Reports,
    int RedBursts,
    int TotalLines,
    int FoldedSites,
    ImmutableList<HeaderOnlyBurst>? HeaderOnly = null)
{
    /// <summary>The bodyless bursts this window saw; never null.</summary>
    public ImmutableList<HeaderOnlyBurst> HeaderOnly { get; init; } =
        HeaderOnly ?? ImmutableList<HeaderOnlyBurst>.Empty;
}

/// <summary>
/// A red burst whose console header was all that reached the aggregator.
/// </summary>
/// <param name="Timestamp">The header line's timestamp.</param>
/// <param name="Pod">The pod that emitted it.</param>
/// <param name="Category">The log category from the header.</param>
/// <param name="AtWindowEdge">
/// True when this was the LAST burst its pod produced in the window — i.e. the body may simply not
/// have been read yet, because the window ended between the header and the lines that follow it. A
/// caller holding a cursor can recover that burst whole by resuming at <paramref name="Timestamp"/>
/// instead of at the window's end; anything else here genuinely had no body.
/// </param>
public record HeaderOnlyBurst(
    DateTimeOffset Timestamp,
    string? Pod,
    string Category,
    bool AtWindowEdge);

/// <summary>
/// Turns a flat batch of log lines into one <see cref="LogIncidentReport"/> per distinct
/// fingerprint — the step that collapses "ten thousand identical errors" into one ticketable fact,
/// without collapsing thirteen different errors along with them.
///
/// <para>Two groupings happen here, and they are different things:</para>
/// <list type="number">
/// <item><b>Line → burst.</b> A .NET console error is several lines: the <c>fail:</c> header, the
/// message, then an indented stack trace. Loki hands them over as separate entries, so consecutive
/// continuation lines are re-attached to their header before anything is parsed. Without this the
/// stack trace is invisible and every fingerprint is computed from the header alone.</item>
/// <item><b>Burst → report.</b> Bursts are then grouped by fingerprint, so one report carries the
/// count, the time span, the pods, and a bounded sample.</item>
/// </list>
///
/// <para>🚨 <b>The two real cases, and what this does with each</b> (both measured on memex-cloud,
/// 2026-08-17):</para>
/// <list type="bullet">
/// <item><b>3,894 lines of the SAME error ⇒ ONE report</b> with <c>Occurrences = 3894</c>. The
/// varying parts — node paths, guids, counts, durations — are masked before the identity is
/// computed, so repetition cannot fan out.</item>
/// <item><b>13 lines of 13 DIFFERENT errors ⇒ 13 reports.</b> Thirteen NodeTypes parked at
/// <c>CompileError</c> share a category, an exception type and a top frame; only the compiler
/// diagnostics differ, and those now reach the identity. Under the old rule they were one ticket,
/// which is why #1786 had to be filed by hand.</item>
/// </list>
///
/// <para>The bound on the second direction is <c>maxVariantsPerSite</c>: a log site that
/// produces more distinct shapes than that in ONE window is treated as a message the masking failed
/// to normalize, and its bursts fold onto a single site-level incident carrying
/// <see cref="LogIncidentReport.Variants"/>. 13 is under the default budget and 50 (the 2026-08-09
/// fan-out) is over it — deliberately, those are the two numbers production has actually produced.
/// The fold is per WINDOW, so the worst case is one folded incident plus at most the budget's worth
/// of fine-grained ones per site, all of them stably fingerprinted and therefore deduplicated across
/// windows.</para>
///
/// <para>Pure and static — no state, no I/O — so the grouping is testable directly on a fixture of
/// log lines.</para>
/// </summary>
public static class BurstAggregator
{
    /// <summary>A header line plus the continuation lines that belong to it.</summary>
    /// <param name="Timestamp">The header line's timestamp.</param>
    /// <param name="Namespace">The namespace label.</param>
    /// <param name="Pod">The pod label.</param>
    /// <param name="Lines">The header line and its continuation lines.</param>
    /// <param name="LastForPod">True when no further burst from this pod followed in the window —
    /// so a body that is missing here may simply be on the other side of the window's edge.</param>
    private record RawBurst(
        DateTimeOffset Timestamp,
        string? Namespace,
        string? Pod,
        ImmutableList<string> Lines,
        bool LastForPod = false);

    /// <summary>One parsed burst, with both identities it could be filed under.</summary>
    private record KeyedBurst(RawBurst Raw, LogLineParser.ParsedBurst Parsed, string Fingerprint, string SiteFold);

    /// <summary>
    /// Groups a batch into one report per fingerprint.
    /// </summary>
    /// <param name="entries">The window's lines, oldest first.</param>
    /// <param name="maxSamples">Evidence lines kept per report.</param>
    /// <param name="maxSampleLength">Each evidence line is truncated to this many characters.</param>
    /// <param name="ignoreCategories">Drops bursts whose category starts with any of these prefixes.</param>
    /// <param name="maxVariantsPerSite">How many distinct fingerprints ONE log site may open in one
    /// window before they are folded onto a single site-level incident. Zero or less disables the
    /// fold.</param>
    /// <returns>The reports plus the counts needed to read the verdict.</returns>
    public static BurstAggregation Aggregate(
        IReadOnlyList<LogLine> entries,
        int maxSamples,
        int maxSampleLength,
        IReadOnlyList<string>? ignoreCategories = null,
        int maxVariantsPerSite = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var keyed = new List<KeyedBurst>();
        var headerOnly = ImmutableList.CreateBuilder<HeaderOnlyBurst>();
        foreach (var raw in SplitBursts(entries))
        {
            if (LogLineParser.Parse(raw.Lines) is not { } parsed)
                continue;
            if (ignoreCategories?.Any(prefix =>
                    parsed.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true)
                continue;

            // 🚨 A burst that is nothing but its console header carries no diagnostic at all, and
            // hashing it would key an incident on the category and event id alone — the degenerate
            // fingerprint of #2222, into which every later bodyless capture from that site would
            // fold. It is reported as what it is (a capture with no body) rather than as a fault.
            if (parsed.IsHeaderOnly)
            {
                headerOnly.Add(new HeaderOnlyBurst(
                    raw.Timestamp, raw.Pod, parsed.Category, raw.LastForPod));
                continue;
            }

            var burst = LogLineParser.ToBurst(parsed);
            keyed.Add(new KeyedBurst(raw, parsed,
                StructuralLogIncidentIdentity.Compute(burst),
                StructuralLogIncidentIdentity.ComputeSiteFold(burst)));
        }

        var folded = FoldedSites(keyed, maxVariantsPerSite);
        var variantsPerSite = folded.IsEmpty
            ? ImmutableDictionary<string, int>.Empty
            : keyed
                .Where(b => folded.Contains(b.SiteFold))
                .GroupBy(b => b.SiteFold, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.Select(b => b.Fingerprint).Distinct(StringComparer.Ordinal).Count(),
                    StringComparer.Ordinal);

        var reports = new Dictionary<string, LogIncidentReport>(StringComparer.Ordinal);
        foreach (var burst in keyed)
        {
            var isFolded = folded.Contains(burst.SiteFold);
            var fingerprint = isFolded ? burst.SiteFold : burst.Fingerprint;
            var sample = new LogSample(burst.Raw.Timestamp, burst.Raw.Pod, Truncate(
                string.Join('\n', burst.Raw.Lines), maxSampleLength));

            reports[fingerprint] = reports.TryGetValue(fingerprint, out var existing)
                ? existing with
                {
                    Occurrences = existing.Occurrences + 1,
                    FirstSeen = burst.Raw.Timestamp < existing.FirstSeen ? burst.Raw.Timestamp : existing.FirstSeen,
                    LastSeen = burst.Raw.Timestamp > existing.LastSeen ? burst.Raw.Timestamp : existing.LastSeen,
                    Pods = burst.Raw.Pod is { Length: > 0 } pod && !existing.Pods.Contains(pod)
                        ? existing.Pods.Add(pod)
                        : existing.Pods,
                    // Keep the LAST few lines: the most recent occurrence is the one a responder
                    // can still correlate with everything else in the logs.
                    Samples = Cap(existing.Samples.Add(sample), maxSamples),
                }
                : new LogIncidentReport
                {
                    Fingerprint = fingerprint,
                    Category = burst.Parsed.Category,
                    Severity = burst.Parsed.Severity,
                    ExceptionType = burst.Parsed.ExceptionType,
                    NormalizedMessage = burst.Parsed.NormalizedMessage,
                    NormalizedDetail = burst.Parsed.NormalizedDetail,
                    TopFrame = burst.Parsed.TopFrame,
                    Namespace = burst.Raw.Namespace,
                    Pods = burst.Raw.Pod is { Length: > 0 } p ? ImmutableList.Create(p) : ImmutableList<string>.Empty,
                    Occurrences = 1,
                    Variants = isFolded ? variantsPerSite[burst.SiteFold] : 1,
                    FirstSeen = burst.Raw.Timestamp,
                    LastSeen = burst.Raw.Timestamp,
                    Samples = ImmutableList.Create(sample),
                };
        }

        return new BurstAggregation(
            reports.Values.OrderBy(r => r.FirstSeen).ToImmutableList(),
            // Bodyless bursts WERE red bursts — they are counted here even though they open no
            // incident, so "N fingerprints from M red bursts" stays an honest ratio.
            keyed.Count + headerOnly.Count,
            entries.Count,
            folded.Count,
            headerOnly.ToImmutable());
    }

    /// <summary>The log sites whose distinct-fingerprint count exceeds the budget.</summary>
    private static ImmutableHashSet<string> FoldedSites(IReadOnlyList<KeyedBurst> keyed, int maxVariantsPerSite) =>
        maxVariantsPerSite <= 0
            ? ImmutableHashSet<string>.Empty
            : keyed
                .GroupBy(b => b.SiteFold, StringComparer.Ordinal)
                .Where(g => g.Select(b => b.Fingerprint).Distinct(StringComparer.Ordinal).Count() > maxVariantsPerSite)
                .Select(g => g.Key)
                .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Re-attaches each red header to the continuation lines that followed it — <b>per pod</b>.
    ///
    /// <para>🚨 <b>The query is namespace-wide, so the lines of one burst are NOT adjacent</b>
    /// (#2153, #2222). Every replica writes to its own stream, the CRI log format stamps each LINE
    /// with its own timestamp, and the collector merges those streams by timestamp — so a burst
    /// whose header and stack trace fall in different milliseconds has any other pod's line from the
    /// millisecond in between sorted right into the middle of it. Reconstructing over the merged
    /// sequence therefore CUT bursts at an arbitrary point and threw the rest away: the header alone
    /// ("no message, no exception, no stack" — #2222), or the header plus the message with the
    /// exception line lost ("a bare Unexpected error with no exception attached" — #2153, whose call
    /// site does pass its exception to <c>LogError</c> and always did). Both filed as incidents that
    /// named a component and no defect.</para>
    ///
    /// <para>A pod's own lines ARE in order within its stream, so grouping by pod first makes the
    /// reconstruction immune to whatever else the namespace was writing at the same moment. Bursts
    /// come back ordered by their header timestamp, as the callers downstream expect.</para>
    ///
    /// <para>Within one pod a line still starts a new burst when it is itself a red header, or when
    /// it carries any other console level prefix (<c>info:</c>, <c>warn:</c>, …) — that prefix means
    /// the previous burst has ended, and the line is dropped because it is not red.</para>
    /// </summary>
    private static IEnumerable<RawBurst> SplitBursts(IReadOnlyList<LogLine> entries) =>
        entries
            .GroupBy(entry => entry.Pod ?? string.Empty, StringComparer.Ordinal)
            .SelectMany(SplitPodBursts)
            .OrderBy(burst => burst.Timestamp);

    /// <summary>Reconstructs the bursts of ONE pod, whose lines are already in emission order.</summary>
    private static IEnumerable<RawBurst> SplitPodBursts(IEnumerable<LogLine> podEntries)
    {
        RawBurst? current = null;

        foreach (var entry in podEntries)
        {
            var line = entry.Line.TrimEnd('\r', '\n');

            if (LogLineParser.IsRedHeader(line, out _))
            {
                if (current is not null)
                    yield return current;
                current = new RawBurst(entry.Timestamp, entry.Namespace, entry.Pod,
                    ImmutableList.Create(line));
                continue;
            }

            if (current is null)
                continue;

            // A non-red level header from this pod ends the burst.
            if (IsLevelHeader(line))
            {
                yield return current;
                current = null;
                continue;
            }

            current = current with { Lines = current.Lines.Add(line) };
        }

        // The last burst this pod opened in the window: its body may simply be on the other side of
        // the window's edge, which is what LastForPod tells the caller.
        if (current is not null)
            yield return current with { LastForPod = true };
    }

    /// <summary>True for any <c>xxxx: </c> console level prefix — the marker that a new log event began.</summary>
    private static bool IsLevelHeader(string line) =>
        line.Length > 5 && line[4] == ':' && line[5] == ' ' && !char.IsWhiteSpace(line[0]);

    private static ImmutableList<LogSample> Cap(ImmutableList<LogSample> samples, int max) =>
        max > 0 && samples.Count > max ? samples.RemoveRange(0, samples.Count - max) : samples;

    private static string Truncate(string value, int max) =>
        max > 0 && value.Length > max ? value[..max] + "…[truncated]" : value;
}
