namespace MeshWeaver.PluginTester;

/// <summary>
/// The gate's known-debt ratchet — the same one-way contract as the plugins repo's
/// <c>compile-check.allow</c>: a failure listed here is reported as debt but does not fail the
/// run; a NEW failure (not listed) fails; and an entry whose check now PASSES is STALE and fails
/// the run, so the list can only shrink. Without this, arming the gate on a repo with
/// pre-existing debt means either merging red or silencing the whole job — both worse than a
/// ratchet.
///
/// <para>File format: one entry per line, <c>#</c> comments, blank lines ignored:</para>
/// <code>
/// &lt;scope&gt; &lt;check&gt;
/// Claims idempotence          # package-level checks: install, idempotence
/// Edu/Exercise tests          # type-level checks: compile, render, tests
/// </code>
/// </summary>
public sealed record GateAllowlist(IReadOnlyList<AllowEntry> Entries)
{
    /// <summary>The empty list — every failure is a new failure.</summary>
    public static readonly GateAllowlist Empty = new([]);

    /// <summary>The valid check names, package-level and type-level.</summary>
    public static readonly IReadOnlySet<string> Checks =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "install", "idempotence", "compile", "render", "tests" };

    /// <summary>Parses the allow file, throwing on a malformed line — a typo that silently
    /// allowed nothing (or everything) would defeat the ratchet.</summary>
    public static GateAllowlist Parse(IEnumerable<string> lines)
    {
        var entries = new List<AllowEntry>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !Checks.Contains(parts[1]))
                throw new FormatException(
                    $"allow file line {number}: expected '<scope> <check>' with check one of " +
                    $"[{string.Join(", ", Checks)}], got '{line}'");
            entries.Add(new AllowEntry(parts[0], parts[1].ToLowerInvariant()));
        }
        return new GateAllowlist(entries);
    }

    /// <summary>Loads and parses the allow file at <paramref name="path"/>.</summary>
    public static GateAllowlist Load(string path) => Parse(File.ReadLines(path));

    private static string StripComment(string line) =>
        line.IndexOf('#') is var i && i >= 0 ? line[..i] : line;

    /// <summary>Whether a failure at <paramref name="scope"/>/<paramref name="check"/> is known debt.</summary>
    public bool Allows(string scope, string check) => Entries.Any(e => e.Matches(scope, check));
}

/// <summary>One known-debt entry: a scope (package id or NodeType path) and a check name.</summary>
public sealed record AllowEntry(string Scope, string Check)
{
    /// <summary>Case-insensitive match, exact scope — no globs: an entry must name exactly the
    /// debt it tolerates, or the ratchet stops ratcheting.</summary>
    public bool Matches(string scope, string check) =>
        string.Equals(Scope, scope, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Check, check, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => $"{Scope} {Check}";
}

/// <summary>One observed failure: where, which check, and the detail text.</summary>
public sealed record GateFailure(string Scope, string Check, string Detail);

/// <summary>
/// The run's verdict once the allowlist is applied: which failures are NEW (fail the run), which
/// are known debt (reported, tolerated), which allow entries are STALE (their check now passes —
/// fail the run so the list shrinks), and which are unverifiable (their scope/check was skipped
/// or absent this run — warned, never failed: a skipped Tests area proves nothing either way).
/// </summary>
public sealed record GateVerdict(
    IReadOnlyList<GateFailure> NewFailures,
    IReadOnlyList<GateFailure> KnownDebt,
    IReadOnlyList<AllowEntry> Stale,
    IReadOnlyList<AllowEntry> Unverifiable)
{
    /// <summary>Applies <paramref name="allow"/> to <paramref name="report"/>.</summary>
    public static GateVerdict Evaluate(GateReport report, GateAllowlist allow)
    {
        var failures = Failures(report).ToList();
        var newFailures = new List<GateFailure>();
        var knownDebt = new List<GateFailure>();
        foreach (var failure in failures)
            (allow.Allows(failure.Scope, failure.Check) ? knownDebt : newFailures).Add(failure);

        var stale = new List<AllowEntry>();
        var unverifiable = new List<AllowEntry>();
        foreach (var entry in allow.Entries)
        {
            if (failures.Any(f => entry.Matches(f.Scope, f.Check)))
                continue;
            (Passed(report, entry) ? stale : unverifiable).Add(entry);
        }
        return new GateVerdict(newFailures, knownDebt, stale, unverifiable);
    }

    /// <summary>Whether the failure at <paramref name="scope"/>/<paramref name="check"/> is
    /// tolerated debt in this verdict. Pure.</summary>
    public bool IsKnownDebt(string scope, string check) =>
        KnownDebt.Any(f =>
            string.Equals(f.Scope, scope, StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.Check, check, StringComparison.OrdinalIgnoreCase));

    /// <summary>Green iff no new failure and no stale entry (and the run itself was not fatal —
    /// the report carries that separately).</summary>
    public bool Success => NewFailures.Count == 0 && Stale.Count == 0;

    private static IEnumerable<GateFailure> Failures(GateReport report)
    {
        foreach (var package in report.Packages)
        {
            if (package.InstallError is not null)
                yield return new GateFailure(package.Id, "install", package.InstallError);
            if (package.IdempotenceError is not null)
                yield return new GateFailure(package.Id, "idempotence", package.IdempotenceError);
            foreach (var type in package.NodeTypes)
            {
                if (type.Compile == CheckOutcome.Failed)
                    yield return new GateFailure(type.Path, "compile", type.CompileDetail ?? "");
                if (type.Render == CheckOutcome.Failed)
                    yield return new GateFailure(type.Path, "render", type.RenderDetail ?? "");
                if (type.Tests == CheckOutcome.Failed)
                    yield return new GateFailure(type.Path, "tests", type.TestsDetail ?? "");
            }
        }
    }

    // An entry is stale only when its check AFFIRMATIVELY passed this run; a skipped check or an
    // absent scope proves nothing, so those entries are kept (and listed as unverifiable).
    private static bool Passed(GateReport report, AllowEntry entry)
    {
        foreach (var package in report.Packages)
        {
            if (string.Equals(package.Id, entry.Scope, StringComparison.OrdinalIgnoreCase))
                return entry.Check.ToLowerInvariant() switch
                {
                    "install" => package.InstallError is null,
                    "idempotence" => package.InstallError is null && package.IdempotenceError is null,
                    _ => false,
                };
            foreach (var type in package.NodeTypes)
                if (string.Equals(type.Path, entry.Scope, StringComparison.OrdinalIgnoreCase))
                    return entry.Check.ToLowerInvariant() switch
                    {
                        "compile" => type.Compile == CheckOutcome.Passed,
                        "render" => type.Render == CheckOutcome.Passed,
                        "tests" => type.Tests == CheckOutcome.Passed,
                        _ => false,
                    };
        }
        return false;
    }
}
