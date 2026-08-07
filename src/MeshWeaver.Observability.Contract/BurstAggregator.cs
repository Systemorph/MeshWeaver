using System.Collections.Immutable;
namespace MeshWeaver.Observability;

/// <summary>One log line as the collector read it, before any grouping.</summary>
/// <param name="Timestamp">The line's timestamp.</param>
/// <param name="Namespace">The Kubernetes namespace the line came from.</param>
/// <param name="Pod">The pod the line came from.</param>
/// <param name="Line">The verbatim line.</param>
public record LogLine(DateTimeOffset Timestamp, string? Namespace, string? Pod, string Line);

/// <summary>
/// Turns a flat batch of log lines into one <see cref="LogIncidentReport"/> per distinct
/// fingerprint — the step that collapses "ten thousand identical errors" into one ticketable fact.
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
/// <para>Pure and static — no state, no I/O — so the grouping is testable directly on a fixture of
/// log lines.</para>
/// </summary>
public static class BurstAggregator
{
    /// <summary>A header line plus the continuation lines that belong to it.</summary>
    private record RawBurst(DateTimeOffset Timestamp, string? Namespace, string? Pod, ImmutableList<string> Lines);

    /// <summary>
    /// Groups a batch into one report per fingerprint. <paramref name="ignoreCategories"/> drops
    /// bursts whose category starts with any of the listed prefixes.
    /// </summary>
    public static ImmutableList<LogIncidentReport> Aggregate(
        IReadOnlyList<LogLine> entries,
        int maxSamples,
        int maxSampleLength,
        IReadOnlyList<string>? ignoreCategories = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var reports = new Dictionary<string, LogIncidentReport>(StringComparer.Ordinal);

        foreach (var raw in SplitBursts(entries))
        {
            if (LogLineParser.Parse(raw.Lines) is not { } parsed)
                continue;
            if (ignoreCategories?.Any(prefix =>
                    parsed.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true)
                continue;

            var fingerprint = LogLineParser.Fingerprint(parsed);
            var sample = new LogSample(raw.Timestamp, raw.Pod, Truncate(
                string.Join('\n', raw.Lines), maxSampleLength));

            reports[fingerprint] = reports.TryGetValue(fingerprint, out var existing)
                ? existing with
                {
                    Occurrences = existing.Occurrences + 1,
                    FirstSeen = raw.Timestamp < existing.FirstSeen ? raw.Timestamp : existing.FirstSeen,
                    LastSeen = raw.Timestamp > existing.LastSeen ? raw.Timestamp : existing.LastSeen,
                    Pods = raw.Pod is { Length: > 0 } pod && !existing.Pods.Contains(pod)
                        ? existing.Pods.Add(pod)
                        : existing.Pods,
                    // Keep the LAST few lines: the most recent occurrence is the one a responder
                    // can still correlate with everything else in the logs.
                    Samples = Cap(existing.Samples.Add(sample), maxSamples),
                }
                : new LogIncidentReport
                {
                    Fingerprint = fingerprint,
                    Category = parsed.Category,
                    Severity = parsed.Severity,
                    ExceptionType = parsed.ExceptionType,
                    NormalizedMessage = parsed.NormalizedMessage,
                    TopFrame = parsed.TopFrame,
                    Namespace = raw.Namespace,
                    Pods = raw.Pod is { Length: > 0 } p ? ImmutableList.Create(p) : ImmutableList<string>.Empty,
                    Occurrences = 1,
                    FirstSeen = raw.Timestamp,
                    LastSeen = raw.Timestamp,
                    Samples = ImmutableList.Create(sample),
                };
        }

        return reports.Values.OrderBy(r => r.FirstSeen).ToImmutableList();
    }

    /// <summary>
    /// Re-attaches each red header to the continuation lines that followed it. A line starts a new
    /// burst when it is itself a red header, or when it carries any other console level prefix
    /// (<c>info:</c>, <c>warn:</c>, …) — that prefix means the previous burst has ended, and the
    /// line is dropped because it is not red.
    /// </summary>
    private static IEnumerable<RawBurst> SplitBursts(IReadOnlyList<LogLine> entries)
    {
        RawBurst? current = null;

        foreach (var entry in entries)
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

            // A different pod's line, or a non-red level header, ends the burst.
            if (!string.Equals(current.Pod, entry.Pod, StringComparison.Ordinal) || IsLevelHeader(line))
            {
                yield return current;
                current = null;
                continue;
            }

            current = current with { Lines = current.Lines.Add(line) };
        }

        if (current is not null)
            yield return current;
    }

    /// <summary>True for any <c>xxxx: </c> console level prefix — the marker that a new log event began.</summary>
    private static bool IsLevelHeader(string line) =>
        line.Length > 5 && line[4] == ':' && line[5] == ' ' && !char.IsWhiteSpace(line[0]);

    private static ImmutableList<LogSample> Cap(ImmutableList<LogSample> samples, int max) =>
        max > 0 && samples.Count > max ? samples.RemoveRange(0, samples.Count - max) : samples;

    private static string Truncate(string value, int max) =>
        max > 0 && value.Length > max ? value[..max] + "…[truncated]" : value;
}
