using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshWeaver.Observability;

/// <summary>
/// Turns raw .NET console log lines into the <see cref="LogBurst"/> the incident fingerprint is
/// computed over — log site (category, event id), fault (exception type, top application frame),
/// and the message, which is carried for the report but never hashed.
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

    /// <summary>The parsed head of a red log burst.</summary>
    /// <param name="Severity">Error or Critical.</param>
    /// <param name="Category">The .NET log category.</param>
    /// <param name="EventId">The event id from the console header (<c>Category[0]</c>) — the log
    /// SITE the code assigns. The incident identity uses it, together with the category and the
    /// exception type, for bursts that carry no application stack frame; when a frame IS present
    /// the frame locates the fault precisely and neither category nor event id participates.</param>
    /// <param name="Message">The message as logged (volatile parts intact).</param>
    /// <param name="NormalizedMessage">The message with volatile parts masked.</param>
    /// <param name="ExceptionType">The exception type name — read from the burst's own exception
    /// line, or recovered from the message when the call site formatted the exception into it.</param>
    /// <param name="TopFrame">The top application stack frame, when present — where the fault IS,
    /// and therefore the incident's locator in preference to the reporting category.</param>
    /// <param name="ExceptionMessage">The exception's OWN message — everything after
    /// <c>Some.Type:</c> up to the first stack frame — read from the exception line, or from the
    /// message when the call site formatted the exception into it. Null when the burst carries no
    /// exception.</param>
    /// <param name="NormalizedDetail">What the fingerprint discriminates on WITHIN a fault site:
    /// the normalized <see cref="ExceptionMessage"/> when the burst carries an exception, otherwise
    /// the normalized logged message. See <see cref="StructuralLogIncidentIdentity"/> for why the
    /// exception's own text wins over the reporter's prose.</param>
    public record ParsedBurst(
        LogSeverity Severity,
        string Category,
        int EventId,
        string Message,
        string NormalizedMessage,
        string? ExceptionType,
        string? TopFrame,
        string? ExceptionMessage = null,
        string NormalizedDetail = "");

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

        // 🚨 A call site that interpolates the exception INTO its message — `LogError("… after {Ms}ms:
        // {Exception}", …)` — leaves no own-line exception header, so the rule above finds nothing,
        // while the SAME fault logged as `LogError(ex, "…")` yields the type. That asymmetry is a
        // property of the caller's formatting, not of the fault, and it is half of why #1170 and
        // #1171 became two tickets for one ObjectDisposedException. Recover the type from the message
        // when no exception line exists; an own-line header always wins.
        string? exceptionMessage = null;
        if (exceptionIndex >= 0)
        {
            exceptionMessage = ExceptionText(body, exceptionIndex);
        }
        else if (InlineException().Match(message) is { Success: true } inline)
        {
            exceptionType = inline.Groups["type"].Value;
            // The exception's own words start right after the `Type: ` the regex consumed — the same
            // text an own-line header would have carried, so both shapes yield the same detail.
            exceptionMessage = message[(inline.Index + inline.Length)..].Trim();
        }

        var normalizedMessage = Normalize(message);

        return new ParsedBurst(
            severity,
            category,
            eventId,
            message,
            normalizedMessage,
            exceptionType,
            TopApplicationFrame(body),
            exceptionMessage,
            // 🚨 The fault's OWN text wins over the reporter's prose. Two catch sites printing one
            // unwinding exception word it differently ("Error during shutdown of hub …" vs "Hub …
            // disposal faulted") but quote the SAME exception message, so keying on the exception
            // text folds them — the property that stopped #1170/#1171 from being two tickets —
            // while still telling thirteen different compiler errors apart. Only a burst with no
            // exception at all falls back to the logged message, because then it is all there is.
            exceptionMessage is { Length: > 0 } detail ? Normalize(detail) : normalizedMessage);
    }

    /// <summary>
    /// The exception's own message: the remainder of the exception line after <c>Some.Type:</c>,
    /// plus the continuation lines up to the first stack frame.
    ///
    /// <para>Multi-line on purpose. A <c>CompilationException</c> puts every Roslyn diagnostic in
    /// its message, and its FIRST line ("Compilation failed for '…'") normalizes to the same text
    /// for every node — so a first-line-only rule would collapse thirteen parked NodeTypes back
    /// onto one ticket, which is the defect this exists to fix (#1787). Bounded by
    /// <see cref="MaxDetailLength"/> so one pathological exception cannot make every window's
    /// hashing quadratic.</para>
    /// </summary>
    private static string ExceptionText(IReadOnlyList<string> body, int exceptionIndex)
    {
        var match = ExceptionLine().Match(body[exceptionIndex]);
        var builder = new StringBuilder(body[exceptionIndex][match.Length..].TrimStart(' ', ':'));

        for (var i = exceptionIndex + 1; i < body.Count && builder.Length < MaxDetailLength; i++)
        {
            if (StackFrame().IsMatch(body[i]))
                break;
            builder.Append('\n').Append(body[i]);
        }

        var text = builder.ToString().Trim();
        return text.Length > MaxDetailLength ? text[..MaxDetailLength] : text;
    }

    /// <summary>
    /// How much exception text may reach the fingerprint. Generous enough that a compiler's whole
    /// diagnostic list fits (the ERRORS come first, before the warning tail), small enough that the
    /// masking regexes stay cheap over thousands of lines per poll.
    /// </summary>
    private const int MaxDetailLength = 8000;

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
        // Collected BEFORE the path rule eats them — see MaskSubjects.
        var subjects = PathSegments(masked);
        masked = Path().Replace(masked, "{path}");
        masked = Quoted().Replace(masked, "'{value}'");
        masked = HexBlob().Replace(masked, "{hex}");
        masked = LabelledIdentifier().Replace(masked, "${label}: {id}");
        masked = MaskSubjects(masked, subjects);
        masked = Number().Replace(masked, "{n}");
        return WhitespaceRun().Replace(masked, " ").Trim();
    }

    /// <summary>
    /// The distinct segments of every slash path in <paramref name="message"/> — the tokens the
    /// message itself has told us are identifiers rather than prose.
    /// </summary>
    private static ImmutableHashSet<string> PathSegments(string message)
    {
        var segments = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (Match match in Path().Matches(message))
        {
            foreach (var segment in match.Value.Split('/', '\\'))
            {
                // Placeholder names are excluded or a segment called "path" would rewrite the
                // `{path}` this very method is about to produce.
                if (segment.Length < 2 || !char.IsLetter(segment[0]) || segment.Contains('{')
                    || PlaceholderNames.Contains(segment))
                    continue;
                segments.Add(segment);
                if (segments.Count >= MaxSubjects)
                    return segments.ToImmutable();
            }
        }
        return segments.ToImmutable();
    }

    /// <summary>
    /// Masks a token elsewhere in the message when that same token appears as a segment of a slash
    /// path in it — <b>the message's own evidence that the token names an instance</b>.
    ///
    /// <para>🚨 This is what makes the message safe to hash at all. Round 4 of the fingerprint
    /// (2026-08-09) produced ~22 incidents for ONE reconcile defect because the subject sat BEFORE
    /// the colon — <c>[PluginGating] Chess: reconcile is NOT CONVERGING — rewrote
    /// Chess/_Access/Public_Access …</c> — where a <c>label: value</c> rule cannot see it. No masking
    /// rule can anticipate where the next message will put its subject, so this one does not try:
    /// it reads the subject OUT of the message, from the paths the message already spells out.
    /// <c>Chess</c> is a path segment, therefore <c>Chess</c> anywhere in that message is an
    /// identifier, whatever position it occupies.</para>
    ///
    /// <para>Word-boundary and case-sensitive, so <c>Cession</c> does not swallow
    /// <c>CessionData</c>, and bounded at <see cref="MaxSubjects"/> tokens. The failure direction is
    /// over-collapsing (a prose word that happens to be a path segment), which costs one ticket a
    /// human can split rather than fifty nobody reads.</para>
    /// </summary>
    private static string MaskSubjects(string message, ImmutableHashSet<string> subjects)
    {
        if (subjects.IsEmpty)
            return message;

        // Longest first, so a subject that is a prefix of another cannot shadow it.
        var pattern = @"\b(?:"
                      + string.Join('|', subjects.OrderByDescending(s => s.Length).Select(Regex.Escape))
                      + @")\b";
        return Regex.Replace(message, pattern, "{id}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>How many path segments may become subjects. Bounds the constructed regex.</summary>
    private const int MaxSubjects = 32;

    /// <summary>The masks <see cref="Normalize"/> itself emits — never re-masked.</summary>
    private static readonly ImmutableHashSet<string> PlaceholderNames =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal,
            "path", "value", "guid", "time", "id", "hex", "n");

    /// <summary>
    /// The stable identity of a fault. Delegates to <see cref="StructuralLogIncidentIdentity"/> —
    /// there is ONE definition of "the same incident", and it lives behind
    /// <see cref="ILogIncidentIdentity"/> so a Code node can replace it without a redeploy.
    /// </summary>
    public static string Fingerprint(ParsedBurst burst) =>
        StructuralLogIncidentIdentity.Compute(ToBurst(burst));

    /// <summary>The identity function's input, as this parser saw the burst.</summary>
    /// <param name="burst">The parsed burst.</param>
    /// <returns>The <see cref="LogBurst"/> handed to <see cref="ILogIncidentIdentity"/>.</returns>
    public static LogBurst ToBurst(ParsedBurst burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        return new LogBurst(burst.Category, burst.EventId, burst.Severity, burst.Message,
            burst.NormalizedMessage, burst.ExceptionType, burst.TopFrame, burst.NormalizedDetail);
    }

    [GeneratedRegex(@"^(?<type>[A-Z][\w.]*(?:Exception|Error))(?::|\s*$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();

    // The same type name, but anywhere in a line rather than anchored at its start — the shape a
    // message gets when the caller formatted the exception into it. `(?<![\w.])` pins the match to
    // the START of the qualified name so `System.ObjectDisposedException` is never read as the bare
    // `ObjectDisposedException`, and the trailing `:` keeps prose out ("Error during shutdown …" has
    // no colon after "Error", so it does not match — which is why this is a fallback, not a rewrite).
    [GeneratedRegex(@"(?<![\w.])(?<type>(?:[A-Za-z_]\w*\.)*[A-Z]\w*(?:Exception|Error))\s*:\s",
        RegexOptions.CultureInvariant)]
    private static partial Regex InlineException();

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
