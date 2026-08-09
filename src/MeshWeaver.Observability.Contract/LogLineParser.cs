using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshWeaver.Observability;

/// <summary>
/// Turns raw .NET console log lines into the (category, normalized message, top frame)
/// triple the incident fingerprint is computed over.
///
/// <para>This type lives in the framework — NOT in the watcher — on purpose: the watcher and the
/// portal must agree on what "the same error" means, and the portal's tests are where that
/// agreement is pinned. It is pure and allocation-light; no I/O, no hub, no state.</para>
///
/// <para>The shape it parses is the default <c>SimpleConsoleFormatter</c> output that Promtail
/// ships from pod stdout:</para>
/// <code>
/// fail: MeshWeaver.Data.MeshDataSource[0]
///       Update failed for node rbuergi/Foo/123
///       System.InvalidOperationException: Sequence contains no elements
///          at MeshWeaver.Data.MeshDataSource.Apply(MeshNode node) in /src/…:line 88
/// </code>
/// </summary>
public static partial class LogLineParser
{
    /// <summary>The console prefixes treated as red, mapped to their severity.</summary>
    private static readonly ImmutableDictionary<string, LogSeverity> RedPrefixes =
        ImmutableDictionary<string, LogSeverity>.Empty
            .Add("fail", LogSeverity.Error)
            .Add("crit", LogSeverity.Critical);

    /// <summary>How much of a normalized message survives into the fingerprint. Long messages
    /// (a serialized payload, a wall of SQL) differ in their tails far more often than in the
    /// part that identifies the fault, so the tail is deliberately not fingerprinted.</summary>
    private const int FingerprintMessageLength = 300;

    /// <summary>The parsed head of a red log burst.</summary>
    /// <param name="Severity">Error or Critical.</param>
    /// <param name="Category">The .NET log category.</param>
    /// <param name="Message">The message as logged (volatile parts intact).</param>
    /// <param name="NormalizedMessage">The message with volatile parts masked.</param>
    /// <param name="ExceptionType">The exception type name, when present.</param>
    /// <param name="TopFrame">The top application stack frame, when present.</param>
    public record ParsedBurst(
        LogSeverity Severity,
        string Category,
        int EventId,
        string Message,
        string NormalizedMessage,
        string? ExceptionType,
        string? TopFrame);

    /// <summary>
    /// True when <paramref name="line"/> opens a red burst (<c>fail:</c> / <c>crit:</c>), with the
    /// matching severity. Warning and below are not red — see AGENTS.md on why the level in code
    /// is a cost decision, not a debugging knob.
    /// </summary>
    public static bool IsRedHeader(string line, out LogSeverity severity)
    {
        severity = default;
        if (line.Length < 5 || line[4] != ':')
            return false;
        return RedPrefixes.TryGetValue(line[..4], out severity);
    }

    /// <summary>
    /// Parses a burst — a red header line plus its indented continuation lines. Returns null when
    /// <paramref name="lines"/> does not open with a red header.
    /// </summary>
    public static ParsedBurst? Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || !IsRedHeader(lines[0], out var severity))
            return null;

        // "fail: Category.Name[42]" → the category AND the event id. The id was previously thrown
        // away; it is the identity the code assigns to a log SITE, which is what the incident key
        // now leans on instead of the message text.
        var header = lines[0][5..].Trim();
        var bracket = header.LastIndexOf('[');
        var category = (bracket > 0 ? header[..bracket] : header).Trim();
        var eventId = 0;
        if (bracket > 0 && header.EndsWith(']'))
            _ = int.TryParse(header[(bracket + 1)..^1], out eventId);

        var body = lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // The exception line is the first "Some.Namespace.SomeException: message" continuation.
        var exceptionIndex = body.FindIndex(l => ExceptionLine().IsMatch(l));
        var exceptionType = exceptionIndex < 0
            ? null
            : ExceptionLine().Match(body[exceptionIndex]).Groups["type"].Value;

        // The message is everything before the exception line (the logged text); a burst that is
        // nothing but an exception falls back to the exception line itself.
        var message = string.Join(' ', exceptionIndex < 0 ? body.Take(1) : body.Take(exceptionIndex))
            .Trim();
        if (message.Length == 0)
            message = exceptionIndex >= 0 ? body[exceptionIndex] : header;

        return new ParsedBurst(
            severity,
            category,
            eventId,
            message,
            Normalize(message),
            exceptionType,
            TopApplicationFrame(body));
    }

    /// <summary>
    /// The first <c>at …</c> stack frame belonging to application code. Framework and BCL frames
    /// are skipped — they are the same for every fault and would collapse unrelated errors onto
    /// one fingerprint.
    /// </summary>
    public static string? TopApplicationFrame(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = StackFrame().Match(line);
            if (!match.Success)
                continue;
            var frame = match.Groups["frame"].Value.Trim();
            if (IsFrameworkFrame(frame))
                continue;
            // Drop the " in /path/file.cs:line 88" suffix: the line number moves with every edit,
            // so keeping it would fork the fingerprint on unrelated commits.
            var inIndex = frame.IndexOf(" in ", StringComparison.Ordinal);
            return (inIndex > 0 ? frame[..inIndex] : frame).Trim();
        }
        return null;
    }

    private static bool IsFrameworkFrame(string frame) =>
        frame.StartsWith("System.", StringComparison.Ordinal)
        || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
        || frame.StartsWith("Npgsql.", StringComparison.Ordinal)
        || frame.StartsWith("Orleans.", StringComparison.Ordinal);

    /// <summary>
    /// Masks the volatile parts of a message so two occurrences of the same fault normalize to the
    /// same text: guids, hex blobs, timestamps, quoted literals, paths, labelled identifiers, and
    /// bare numbers.
    /// This is what keeps "node 7a2f… not found" and "node 91bc… not found" one incident.
    ///
    /// <para>Order matters. Guids and timestamps are masked FIRST so the path rule can then swallow
    /// a whole mesh-node path — <c>rbuergi/Foo/{guid}</c> — in one go. Without that, the partition
    /// segment survives and every user's occurrence of the same defect becomes its own ticket,
    /// which is exactly the flood this system exists to prevent.</para>
    /// </summary>
    public static string Normalize(string message)
    {
        var masked = Guid().Replace(message, "{guid}");
        masked = Timestamp().Replace(masked, "{time}");
        masked = Path().Replace(masked, "{path}");
        masked = Quoted().Replace(masked, "'{value}'");
        masked = HexBlob().Replace(masked, "{hex}");
        masked = LabelledIdentifier().Replace(masked, "${label}: {id}");
        masked = Number().Replace(masked, "{n}");
        return WhitespaceRun().Replace(masked, " ").Trim();
    }

    /// <summary>
    /// The stable identity of a fault. Delegates to <see cref="StructuralLogIncidentIdentity"/> —
    /// there is ONE definition of "the same incident", and it lives behind
    /// <see cref="ILogIncidentIdentity"/> so a Code node can replace it without a redeploy.
    /// </summary>
    public static string Fingerprint(ParsedBurst burst) => StructuralLogIncidentIdentity.Compute(
        new LogBurst(burst.Category, burst.EventId, burst.Severity, burst.Message,
            burst.NormalizedMessage, burst.ExceptionType, burst.TopFrame));

    [GeneratedRegex(@"^(?<type>[A-Z][\w.]*(?:Exception|Error))(?::|\s*$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();

    [GeneratedRegex(@"^\s*at\s+(?<frame>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrame();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    // Any multi-segment slash path — a filesystem path AND a mesh node path (rbuergi/Foo/{guid}).
    // `{}` is in the segment class on purpose so an already-masked guid still counts as a segment
    // and the whole path collapses, instead of leaving a per-user prefix behind.
    [GeneratedRegex(@"[\w.\-{}]+(?:[/\\][\w.\-{}]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex Path();

    [GeneratedRegex(@"'[^']{1,120}'|""[^""]{1,120}""", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\b(?:0x)?[0-9a-fA-F]{16,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexBlob();

    // A bare identifier introduced by a known label — `target: Claims`, `sender: Edu`, `for: Foo`.
    // These are the SUBJECT of a message, not the fault in it: one defect reported once per target
    // produced one incident per target name until this masked them (66 incidents for ~3 real bugs,
    // 2026-08-08). Only single tokens are masked, and only after these labels, so a genuine message
    // like "Sequence contains no elements" is untouched.
    [GeneratedRegex(@"\b(?<label>target|sender|for|from|to|node|hub|partition|user|area|space|type)\s*:\s*(?![{'""])[A-Za-z0-9_.\-/]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LabelledIdentifier();

    [GeneratedRegex(@"(?<![A-Za-z_])\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
