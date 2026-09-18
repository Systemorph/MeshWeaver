using System.Collections.Immutable;
using MeshWeaver.Compiler;

namespace MeshWeaver.PluginTester;

/// <summary>
/// WHICH ratchet a diagnostic belongs to. The two are kept apart on purpose (maintainer,
/// 2026-09-16, choosing this over one combined gate): they are different kinds of debt, they are
/// paid by different people at different times, and one must never hold the other hostage.
/// </summary>
public enum WarningClass
{
    /// <summary>
    /// A REAL warning — unused code (<c>CS0219</c>), an unresolvable <c>cref</c>
    /// (<c>CS1574</c>/<c>CS1584</c>), a <c>&lt;param&gt;</c> tag naming a parameter that does not
    /// exist (<c>CS1573</c>), an unreachable statement, a shadowed member. Every one of these is a
    /// latent bug or a doc comment that is actively WRONG, and <c>src/</c> would not compile with
    /// it under <c>-warnaserror</c>.
    /// </summary>
    Real,

    /// <summary>
    /// <c>CS1591</c> — a public member with no XML doc comment. Documentation debt: nothing is
    /// broken, the member simply is not described. It gets its OWN baseline so that paying down (or
    /// carrying) doc debt never sits in the same verdict as a latent bug, and so a missing doc
    /// comment can never be the reason an unrelated change cannot merge.
    /// </summary>
    DocComment,
}

/// <summary>Which class a diagnostic id falls in — one function, so the two ratchets cannot
/// disagree about where a code belongs.</summary>
public static class WarningClasses
{
    /// <summary>Classifies one warning.</summary>
    /// <param name="warning">The warning to classify.</param>
    public static WarningClass Of(CompileWarning warning) =>
        warning.IsMissingDocComment ? WarningClass.DocComment : WarningClass.Real;

    /// <summary>Classifies one diagnostic id.</summary>
    /// <param name="id">The diagnostic id, e.g. <c>CS1591</c>.</param>
    public static WarningClass Of(string id) =>
        string.Equals(id, CompileWarning.MissingDocComment, StringComparison.Ordinal)
            ? WarningClass.DocComment
            : WarningClass.Real;

    /// <summary>The ratchet's name as it appears in the log — the word a reader greps for.</summary>
    /// <param name="warningClass">The class to name.</param>
    public static string Name(WarningClass warningClass) =>
        warningClass == WarningClass.DocComment ? "doc-comments" : "warnings";
}

/// <summary>
/// One baseline entry: a NodeType path and a diagnostic id it is currently allowed to produce.
/// </summary>
/// <param name="Scope">The NodeType's mesh path, e.g. <c>ACME/Report</c>.</param>
/// <param name="Code">The diagnostic id, e.g. <c>CS1591</c>.</param>
public sealed record WarningBaselineEntry(string Scope, string Code)
{
    /// <summary>Which ratchet owns this entry.</summary>
    public WarningClass Class => WarningClasses.Of(Code);

    /// <summary>Case-insensitive on the path (mesh paths are), ordinal on the id (a diagnostic id
    /// is a wire identifier). Exact scope — no globs: an entry must name exactly the debt it
    /// tolerates, or the ratchet stops ratcheting.</summary>
    /// <param name="scope">The NodeType path observed.</param>
    /// <param name="code">The diagnostic id observed.</param>
    public bool Matches(string scope, string code) =>
        string.Equals(Scope, scope, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Code, code, StringComparison.Ordinal);

    /// <inheritdoc />
    /// <summary>The entry as it appears in the baseline file, so a reader can copy the line the
    /// gate names straight back out of (or into) the file.</summary>
    public override string ToString() => $"{Scope} {Code}";
}

/// <summary>
/// The in-mesh warning ratchet's baseline: the debt in-mesh C# carries TODAY, so that the debt can
/// only ever shrink.
///
/// <para>Same one-way contract as <see cref="GateAllowlist"/> and
/// <c>scripts/type-forwards.allow</c>: a (type, diagnostic id) pair listed here is reported and
/// tolerated; a pair NOT listed fails the bake; and a listed pair whose type compiled clean this
/// run is STALE and fails the bake, so the line has to go. Nothing can quietly grow back.</para>
///
/// <para><b>File format</b> — one entry per line, <c>#</c> comments, blank lines ignored:</para>
/// <code>
/// ACME/Report      CS1591   # 12 public members with no doc comment
/// Northwind/Order  CS0219   # an unused local in OrderView.cs
/// </code>
///
/// <para>🚨 <b>Granularity is (type, code), not (type, code, member).</b> Per-member would make the
/// baseline a transcript — 380 lines for the samples tree alone — and every doc comment written
/// would edit it. Per-TYPE would allow a NodeType with one tolerated <c>CS1591</c> to acquire a
/// <c>CS0219</c> in silence, which is the debt growing back under a green tick. (type, code) is the
/// coarsest key that still refuses a NEW KIND of defect anywhere, and the finest that a reader can
/// keep in their head.</para>
///
/// <para>🚨 <b>Absent is OBSERVE-ONLY, and it says so in every run.</b> There is no baseline to
/// capture for a repo this change has never run in, so a bake with no <c>--warning-baseline</c>
/// MEASURES and REPORTS and enforces nothing — and prints the policy by name, every time, so
/// "nothing was enforced here" and "everything passed" are two different printed sentences rather
/// than the same silence. That is the same rule <see cref="GateAllowlist.MissingFileMessage"/>
/// states from the other side: a gate that cannot read its input must not look like a gate that
/// passed. A path that was GIVEN and is not there is still a hard refusal.</para>
/// </summary>
/// <param name="Entries">The parsed entries, in file order.</param>
/// <param name="Enforced">False for <see cref="ObserveOnly"/> — no baseline was named, so the two
/// ratchets measure and report but fail nothing.</param>
public sealed record WarningBaseline(
    IReadOnlyList<WarningBaselineEntry> Entries, bool Enforced)
{
    /// <summary>No baseline was named: measure, report, enforce nothing — and say so.</summary>
    public static readonly WarningBaseline ObserveOnly = new([], Enforced: false);

    /// <summary>Parses the baseline, throwing on a malformed line — a typo that silently allowed
    /// nothing (or everything) would defeat the ratchet.</summary>
    /// <param name="lines">The file's lines.</param>
    public static WarningBaseline Parse(IEnumerable<string> lines)
    {
        var entries = new List<WarningBaselineEntry>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new FormatException(
                    $"warning baseline line {number}: expected '<NodeType path> <diagnostic id>' "
                    + $"(e.g. 'ACME/Report CS1591'), got '{line}'");
            if (!LooksLikeADiagnosticId(parts[1]))
                throw new FormatException(
                    $"warning baseline line {number}: '{parts[1]}' is not a diagnostic id — the "
                    + "second token is the compiler's own code (CS1591, CS0219, CS1574), never a "
                    + "check name. A line that names something else tolerates nothing and hides "
                    + "the debt it was meant to record.");
            entries.Add(new WarningBaselineEntry(parts[0], parts[1]));
        }
        return new WarningBaseline(entries, Enforced: true);
    }

    /// <summary>Loads and parses the baseline at <paramref name="path"/>. A path that was GIVEN and
    /// is not there throws with <see cref="MissingFileMessage"/> — the caller is a CLI whose job is
    /// to turn that into one actionable line and an exit code.</summary>
    /// <param name="path">The path as it was given on the command line.</param>
    public static WarningBaseline Load(string path) =>
        File.Exists(path)
            ? Parse(File.ReadLines(path))
            : throw new FileNotFoundException(MissingFileMessage(path), path);

    /// <summary>The one-line, actionable account of a <c>--warning-baseline</c> path that is not
    /// there. An EMPTY baseline (nothing tolerated, every warning fails) and a baseline the bake
    /// could not read must never render the same, which is why the empty case has its own honest
    /// spelling — an empty FILE — and a missing path is refused.</summary>
    /// <param name="path">The path as it was given on the command line.</param>
    public static string MissingFileMessage(string path) =>
        $"--warning-baseline named '{path}', which does not exist (resolved to "
        + $"'{GateAllowlist.Describe(path)}'). Spell an EMPTY baseline — nothing tolerated, every "
        + "in-mesh warning fails — as an empty FILE at that path, and 'measure but enforce "
        + "nothing' by omitting the flag; never as a path that is not there, or nothing can tell "
        + "'no known debt' from 'the bake never read the baseline'.";

    /// <summary>Whether <paramref name="scope"/> is allowed to produce <paramref name="code"/>.</summary>
    /// <param name="scope">The NodeType path.</param>
    /// <param name="code">The diagnostic id.</param>
    public bool Allows(string scope, string code) =>
        Entries.Any(e => e.Matches(scope, code));

    /// <summary>The entries belonging to one ratchet — <see cref="Inert"/> ones excluded.</summary>
    /// <param name="warningClass">Which ratchet.</param>
    public IEnumerable<WarningBaselineEntry> For(WarningClass warningClass) =>
        Entries.Where(e => e.Class == warningClass && !CompileWarning.IsNotReported(e.Code));

    /// <summary>
    /// Baseline entries naming a code the compile no longer REPORTS
    /// (<see cref="CompileWarning.NotReported"/>) — reported once, tolerated, and never failed in
    /// either direction.
    ///
    /// <para>🚨 This exists so that retiring a code cannot red the fleet. The moment
    /// <c>CS1701</c> stopped being reported, every one of MeshWeaver.Plugins' 95 <c>CS1701</c>
    /// entries would have become STALE — a hard bake failure on a file no pull request in flight
    /// had any reason to touch — and the repo would have been unable to go green until a trim
    /// landed, on a platform image it does not control the timing of. An entry here is simply
    /// INERT: not stale (nothing measured it), not known debt (nothing can produce it), just a line
    /// with nothing left to say. The report names them so they get deleted, rather than sitting
    /// unread for ever.</para>
    /// </summary>
    public IEnumerable<WarningBaselineEntry> Inert =>
        Entries.Where(e => CompileWarning.IsNotReported(e.Code));

    private static string StripComment(string line) =>
        line.IndexOf('#') is var i && i >= 0 ? line[..i] : line;

    // A diagnostic id is letters then digits — CS1591, CA1822, IDE0051. Deliberately shape-based
    // rather than a CS-only test: an analyzer id is a legitimate entry and refusing it would send
    // the reader to suppress the analyzer instead of recording the debt.
    private static bool LooksLikeADiagnosticId(string token) =>
        token.Length >= 3
        && char.IsAsciiLetterUpper(token[0])
        && token.TakeWhile(char.IsAsciiLetter).Count() is var letters and >= 2
        && letters < token.Length
        && token.Skip(letters).All(char.IsAsciiDigit);
}

/// <summary>One distinct warning SITE, folded across every NodeType that produced it.</summary>
/// <param name="Code">The diagnostic id.</param>
/// <param name="Message">The compiler's message — it names the member, so it identifies the site.</param>
/// <param name="Types">The NodeType paths whose compile produced it, ordinal.</param>
public sealed record WarningSite(string Code, string Message, ImmutableSortedSet<string> Types)
{
    /// <summary>The site as one line: the code, the first type, how many others, and the message.
    /// 🚨 A site shared by eight NodeTypes is ONE line naming eight, never eight lines — a
    /// <c>Source/*.cs</c> pulled in by <c>shared=@Lib/Source</c> is concatenated into every one of
    /// their generated trees, so the multiplier is the sharing, not the defect count.</summary>
    public string Describe() =>
        $"{Code} {Types[0]}"
        + (Types.Count > 1 ? $" (+{Types.Count - 1} more type(s))" : string.Empty)
        + $": {Message}";
}

/// <summary>
/// What a bake MEASURED: every warning every NodeType that compiled produced, folded by diagnostic
/// id and by site.
///
/// <para>🚨 This is the whole of the "quieten the log" half. Raw, a bake of the samples tree emits
/// hundreds of warning lines, most of them the same diagnostic on the same shared source reported
/// once per NodeType that includes it. Folded, the reader gets the totals, one line per diagnostic
/// id, and each distinct site named ONCE — the shape without the scrolling.</para>
/// </summary>
/// <param name="Sites">Every distinct (code, message) site, ordered by code then message.</param>
/// <param name="Pairs">Every distinct (NodeType path, code) pair observed — the ratchet's key set.</param>
/// <param name="Occurrences">Raw occurrences, before any folding: what the log WOULD have carried.</param>
/// <param name="CompiledTypes">The NodeType paths this run actually compiled. 🚨 The ratchet's
/// DENOMINATOR: a baseline entry for a type that did NOT compile is unverifiable, never stale —
/// a type that failed produced no warnings, and reading that silence as "the debt is paid" is the
/// same defect as a sweep whose zero has two causes.</param>
public sealed record WarningInventory(
    ImmutableArray<WarningSite> Sites,
    ImmutableSortedSet<(string Scope, string Code)> Pairs,
    int Occurrences,
    ImmutableSortedSet<string> CompiledTypes)
{
    /// <summary>Nothing measured — the shape a bake that compiled nothing reports.</summary>
    public static readonly WarningInventory Empty = new(
        [],
        ImmutableSortedSet<(string, string)>.Empty,
        0,
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));

    /// <summary>Folds the per-type warning lists of one bake.</summary>
    /// <param name="compiled">One entry per NodeType that COMPILED, with the warnings it produced —
    /// a type with none belongs here too, because "compiled clean" is what makes a baseline entry
    /// stale.</param>
    public static WarningInventory Of(
        IEnumerable<(string NodePath, ImmutableArray<CompileWarning> Warnings)> compiled)
    {
        var sites = new Dictionary<(string Code, string Message), ImmutableSortedSet<string>.Builder>();
        var pairs = ImmutableSortedSet.CreateBuilder<(string Scope, string Code)>();
        var types = ImmutableSortedSet<string>.Empty
            .WithComparer(StringComparer.OrdinalIgnoreCase).ToBuilder();
        // 🚨 Counted HERE, off the same pass as the total — never re-derived from the folded sites.
        // Summing each site's TYPE count would be the raw count only while no single type produces
        // one (id, message) at two different LINES, which `EmitPipeline.Collect` does keep as two
        // entries (two unused locals of the same name in two methods is the shape). The totals line
        // and the per-code line are then two numbers for one quantity that can silently disagree,
        // and a reader has no way to tell which is the measurement.
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = 0;
        foreach (var (nodePath, warnings) in compiled)
        {
            types.Add(nodePath);
            foreach (var warning in warnings)
            {
                occurrences++;
                byCode[warning.Id] = byCode.GetValueOrDefault(warning.Id) + 1;
                pairs.Add((nodePath, warning.Id));
                if (!sites.TryGetValue(warning.Site, out var hosts))
                    sites[warning.Site] = hosts = ImmutableSortedSet<string>.Empty
                        .WithComparer(StringComparer.Ordinal).ToBuilder();
                hosts.Add(nodePath);
            }
        }
        return new WarningInventory(
            [.. sites
                .OrderBy(s => s.Key.Code, StringComparer.Ordinal)
                .ThenBy(s => s.Key.Message, StringComparer.Ordinal)
                .Select(s => new WarningSite(s.Key.Code, s.Key.Message, s.Value.ToImmutable()))],
            pairs.ToImmutable(),
            occurrences,
            types.ToImmutable())
        {
            OccurrencesByCode = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, byCode),
        };
    }

    /// <summary>
    /// Raw occurrences per diagnostic id, counted on the SAME pass as <see cref="Occurrences"/> so
    /// the totals line and the per-code table cannot disagree. Init-only property for the
    /// record-signature rule.
    /// </summary>
    public ImmutableSortedDictionary<string, int> OccurrencesByCode { get; init; } =
        ImmutableSortedDictionary<string, int>.Empty;

    /// <summary>The sites belonging to one ratchet.</summary>
    /// <param name="warningClass">Which ratchet.</param>
    public IEnumerable<WarningSite> For(WarningClass warningClass) =>
        Sites.Where(s => WarningClasses.Of(s.Code) == warningClass);

    /// <summary>
    /// Distinct diagnostic ids, with their raw occurrence count, site count and type count — the
    /// per-code table, ordered most-occurrences first so the shape reads at a glance.
    ///
    /// <para>The occurrence column comes from <see cref="OccurrencesByCode"/>, counted on the same
    /// pass as <see cref="Occurrences"/>, so this table SUMS to the totals line by construction.</para>
    /// </summary>
    public IReadOnlyList<(string Code, int Occurrences, int Sites, int Types)> ByCode() =>
        [.. Sites
            .GroupBy(s => s.Code, StringComparer.Ordinal)
            .Select(g => (
                Code: g.Key,
                Occurrences: OccurrencesByCode.GetValueOrDefault(g.Key),
                Sites: g.Count(),
                Types: g.SelectMany(s => s.Types).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(x => x.Occurrences)
            .ThenBy(x => x.Code, StringComparer.Ordinal)];

    /// <summary>How many of the compiled types produced at least one warning of this class.</summary>
    /// <param name="warningClass">Which ratchet.</param>
    public int TypesAffected(WarningClass warningClass) =>
        Pairs.Where(p => WarningClasses.Of(p.Code) == warningClass)
            .Select(p => p.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
}

/// <summary>
/// ONE ratchet's verdict — which (type, code) pairs are NEW (fail), which baseline entries are
/// STALE (fail, so the list shrinks) and which are UNVERIFIABLE (warned, never failed: the type did
/// not compile or is not in this run's tree, and silence is not evidence the debt is paid).
/// </summary>
/// <param name="Class">Which ratchet this is.</param>
/// <param name="New">Observed pairs the baseline does not carry.</param>
/// <param name="Stale">Baseline entries whose type compiled clean of that code this run.</param>
/// <param name="Unverifiable">Baseline entries whose type this run could not judge.</param>
/// <param name="Known">Observed pairs the baseline carries — the debt, reported not failed.</param>
/// <param name="Enforced">Whether a baseline was named at all.</param>
public sealed record WarningRatchet(
    WarningClass Class,
    ImmutableArray<(string Scope, string Code)> New,
    ImmutableArray<WarningBaselineEntry> Stale,
    ImmutableArray<WarningBaselineEntry> Unverifiable,
    ImmutableArray<(string Scope, string Code)> Known,
    bool Enforced)
{
    /// <summary>Green iff nothing new and nothing stale. An OBSERVE-ONLY ratchet is always green —
    /// and always says it enforced nothing.</summary>
    public bool Success => !Enforced || (New.IsEmpty && Stale.IsEmpty);

    /// <summary>The ratchet's name in the log.</summary>
    public string Name => WarningClasses.Name(Class);

    /// <summary>Evaluates one ratchet.</summary>
    /// <param name="warningClass">Which ratchet.</param>
    /// <param name="inventory">What the bake measured.</param>
    /// <param name="baseline">The debt this repo currently carries.</param>
    public static WarningRatchet Evaluate(
        WarningClass warningClass, WarningInventory inventory, WarningBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(baseline);

        var observed = inventory.Pairs
            .Where(p => WarningClasses.Of(p.Code) == warningClass)
            .ToList();
        var fresh = ImmutableArray.CreateBuilder<(string, string)>();
        var known = ImmutableArray.CreateBuilder<(string, string)>();
        foreach (var pair in observed)
            (baseline.Allows(pair.Scope, pair.Code) ? known : fresh).Add(pair);

        var stale = ImmutableArray.CreateBuilder<WarningBaselineEntry>();
        var unverifiable = ImmutableArray.CreateBuilder<WarningBaselineEntry>();
        foreach (var entry in baseline.For(warningClass))
        {
            if (observed.Any(p => entry.Matches(p.Scope, p.Code)))
                continue;
            // 🚨 STALE only when this run COMPILED the type and it produced no such warning. A type
            // that failed to compile, or that this run's tree does not carry (a shard, a narrowed
            // stage), emitted no warnings at all — calling its entry stale would delete a line
            // recording real debt on the strength of a measurement nobody took. Same rule as
            // GateVerdict's Unverifiable, and the same reason.
            (inventory.CompiledTypes.Contains(entry.Scope) ? stale : unverifiable).Add(entry);
        }

        return new WarningRatchet(
            warningClass, fresh.ToImmutable(), stale.ToImmutable(), unverifiable.ToImmutable(),
            known.ToImmutable(), baseline.Enforced);
    }
}
