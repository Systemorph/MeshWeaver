using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Compiler;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// One line of the dynamic-edge ratchet's baseline: a dynamic edge INTO CORE that a source carries
/// today — <c>&lt;source&gt; &lt;kind&gt; &lt;target&gt;</c>, the target being the rest of the line
/// (an assembly-qualified pattern contains a comma and a space).
/// </summary>
/// <param name="Source">The edge set's source (a NodeType path, or a module's assembly name).</param>
/// <param name="Kind"><see cref="DependencyEdgeKinds.Dynamic"/> or <see cref="DependencyEdgeKinds.DynamicUnresolved"/>.</param>
/// <param name="Target">The edge's target, exactly as the edge file spells it.</param>
public sealed record DynamicEdgeBaselineEntry(string Source, string Kind, string Target)
{
    /// <summary>Whether this entry names <paramref name="key"/>.</summary>
    /// <param name="key">An observed (source, kind, target).</param>
    public bool Matches((string Source, string Kind, string Target) key) =>
        string.Equals(Source, key.Source, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Kind, key.Kind, StringComparison.Ordinal)
        && string.Equals(Target, key.Target, StringComparison.Ordinal);

    /// <inheritdoc />
    /// <summary>The line as it appears in the baseline file.</summary>
    public override string ToString() => $"{Source} {Kind} {Target}";
}

/// <summary>
/// The dynamic-edge ratchet's baseline (<c>Doc/Architecture/DependencyGraph</c> → <i>The ratchet</i>)
/// — the same one-way contract as <see cref="WarningBaseline"/>: a listed dynamic edge into core is
/// tolerated and reported, an UNLISTED one fails, and a listed one whose source compiled this run
/// without it is STALE and fails, so the list can only shrink. Absent (no
/// <c>--dynamic-baseline</c>) is OBSERVE-ONLY and says so by name in every run; a path that was
/// given and is not there is a hard refusal; an EMPTY file is "nothing tolerated".
/// </summary>
/// <param name="Entries">The parsed entries, in file order.</param>
/// <param name="Enforced">False when no baseline was named.</param>
public sealed record DynamicEdgeBaseline(IReadOnlyList<DynamicEdgeBaselineEntry> Entries, bool Enforced)
{
    /// <summary>No baseline was named: measure, report, enforce nothing — and say so.</summary>
    public static DynamicEdgeBaseline ObserveOnly { get; } = new([], Enforced: false);

    /// <summary>Parses the baseline, throwing on a malformed line.</summary>
    /// <param name="lines">The file's lines.</param>
    public static DynamicEdgeBaseline Parse(IEnumerable<string> lines)
    {
        var entries = new List<DynamicEdgeBaselineEntry>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var hash = raw.IndexOf('#');
            var line = (hash < 0 ? raw : raw[..hash]).Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                throw new FormatException(
                    $"dynamic baseline line {number}: expected '<source> <kind> <target>' "
                    + $"(e.g. 'Acme/Maps Dynamic MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph'), got '{line}'");
            if (parts[1] is not (DependencyEdgeKinds.Dynamic or DependencyEdgeKinds.DynamicUnresolved))
                throw new FormatException(
                    $"dynamic baseline line {number}: '{parts[1]}' is not a dynamic edge kind — the second "
                    + $"token is {DependencyEdgeKinds.Dynamic} or {DependencyEdgeKinds.DynamicUnresolved}. A line "
                    + "naming anything else tolerates nothing and hides the edge it was meant to record.");
            entries.Add(new DynamicEdgeBaselineEntry(parts[0], parts[1], parts[2].Trim()));
        }
        return new DynamicEdgeBaseline(entries, Enforced: true);
    }

    /// <summary>Loads the baseline; a missing path throws with <see cref="MissingFileMessage"/>.</summary>
    /// <param name="path">The path as given.</param>
    public static DynamicEdgeBaseline Load(string path) =>
        File.Exists(path)
            ? Parse(File.ReadLines(path))
            : throw new FileNotFoundException(MissingFileMessage(path), path);

    /// <summary>The account of a <c>--dynamic-baseline</c> path that is not there.</summary>
    /// <param name="path">The path as given.</param>
    public static string MissingFileMessage(string path) =>
        $"--dynamic-baseline named '{path}', which does not exist (resolved to "
        + $"'{GateAllowlist.Describe(path)}'). Spell an EMPTY baseline — no dynamic edge into core "
        + "tolerated — as an empty FILE, and 'measure but enforce nothing' by omitting the flag; never "
        + "as a path that is not there.";
}

/// <summary>The dynamic-edge ratchet's verdict.</summary>
/// <param name="New">Observed dynamic edges into core the baseline does not carry — fail.</param>
/// <param name="Stale">Baseline entries whose source compiled this run without the edge — fail.</param>
/// <param name="Unverifiable">Baseline entries whose source this run did not compile — kept, warned.</param>
/// <param name="Known">Observed edges the baseline carries — the debt, reported.</param>
/// <param name="Enforced">Whether a baseline was named.</param>
/// <param name="Measured">Whether the edges file MEASURED dynamic bindings at all
/// (<see cref="DependencyEdgesFile.Measures.DynamicBindings"/>). A file whose producer predates the
/// scan has no dynamic edges because nobody looked — unverifiable, never a pass.</param>
public sealed record DynamicEdgeRatchet(
    ImmutableArray<(string Source, string Kind, string Target)> New,
    ImmutableArray<DynamicEdgeBaselineEntry> Stale,
    ImmutableArray<DynamicEdgeBaselineEntry> Unverifiable,
    ImmutableArray<(string Source, string Kind, string Target)> Known,
    bool Enforced,
    bool Measured)
{
    /// <summary>The log prefix every ratchet and report line carries.</summary>
    public const string Prefix = "dependencies:";

    /// <summary>Green iff the input was MEASURED and nothing is new or stale. OBSERVE-ONLY is green
    /// and says so; an enforced ratchet over an unmeasured input is RED — it judged nothing.</summary>
    public bool Success => !Enforced || (Measured && New.IsEmpty && Stale.IsEmpty);

    /// <summary>
    /// Whether an edge is a dynamic edge INTO CORE: every <see cref="DependencyEdgeKinds.DynamicUnresolved"/>
    /// (its target is unknown, so it may be the platform), and every <see cref="DependencyEdgeKinds.Dynamic"/>
    /// whose target is the platform or could not be classified.
    /// </summary>
    /// <param name="edge">The edge.</param>
    public static bool IsIntoCore(DependencyEdge edge) =>
        edge.Kind == DependencyEdgeKinds.DynamicUnresolved
        || (edge.Kind == DependencyEdgeKinds.Dynamic
            && (edge.TargetKind is null || edge.TargetKind == DependencyEdgeKinds.Platform));

    /// <summary>Evaluates the ratchet over an edge document.</summary>
    /// <param name="document">The edges.</param>
    /// <param name="baseline">The baseline.</param>
    public static DynamicEdgeRatchet Evaluate(DependencyEdgesFile.Document document, DynamicEdgeBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baseline);
        // An input that never measured dynamic bindings proves nothing either way: every entry is
        // unverifiable (kept, never stale) and nothing is NEW — and an enforced ratchet is refused.
        if (!document.Has(DependencyEdgesFile.Measures.DynamicBindings))
            return new DynamicEdgeRatchet([], [], [.. baseline.Entries], [], baseline.Enforced, Measured: false);
        var observed = document.Sets
            .SelectMany(s => s.Edges.Where(IsIntoCore).Select(e => (s.Source, e.Kind, e.Target)))
            .ToList();
        var fresh = ImmutableArray.CreateBuilder<(string, string, string)>();
        var known = ImmutableArray.CreateBuilder<(string, string, string)>();
        foreach (var key in observed)
            (baseline.Entries.Any(e => e.Matches(key)) ? known : fresh).Add(key);
        var compiled = document.Sets.Select(s => s.Source).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = ImmutableArray.CreateBuilder<DynamicEdgeBaselineEntry>();
        var unverifiable = ImmutableArray.CreateBuilder<DynamicEdgeBaselineEntry>();
        foreach (var entry in baseline.Entries)
        {
            if (observed.Any(entry.Matches))
                continue;
            // STALE only when this run EXTRACTED the source's edges and the edge is gone. A source
            // that failed, or that this run did not attempt, proves nothing by its silence.
            (compiled.Contains(entry.Source) ? stale : unverifiable).Add(entry);
        }
        return new DynamicEdgeRatchet(fresh.ToImmutable(), stale.ToImmutable(), unverifiable.ToImmutable(),
            known.ToImmutable(), baseline.Enforced, Measured: true);
    }

    /// <summary>Prints the verdict — ENFORCED with its counts, or OBSERVE-ONLY naming who has not adopted it.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="baseline">The baseline evaluated.</param>
    /// <param name="adopter">The repository the run is for, named in the observe-only line.</param>
    public void Write(TextWriter output, DynamicEdgeBaseline baseline, string? adopter)
    {
        if (!Measured)
        {
            output.WriteLine(
                $"{Prefix} dynamic-edge ratchet — UNVERIFIABLE: the edges file does not record that its "
                + "producer scanned for dynamic bindings (written before the scan existed), so its silence "
                + "is unknown, not clean. "
                + (Enforced
                    ? $"Enforcement REFUSED — RED; {baseline.Entries.Count} baseline entr(ies) kept as unverifiable."
                    : "OBSERVE-ONLY, and nothing was measured either."));
            return;
        }
        if (!Enforced)
        {
            var who = string.IsNullOrWhiteSpace(adopter) ? "this repository" : adopter;
            output.WriteLine(
                $"{Prefix} dynamic-edge ratchet — OBSERVE-ONLY: {who} has not adopted it (no "
                + $"--dynamic-baseline), so NOTHING is enforced. {New.Length} dynamic edge(s) into core "
                + (New.IsEmpty
                    ? "measured; to adopt, pass an EMPTY baseline file (nothing tolerated)."
                    : "measured; to adopt, put these lines in a baseline file:"));
            foreach (var (source, kind, target) in New)
                output.WriteLine($"{Prefix} baseline {source} {kind} {target}");
            return;
        }
        output.WriteLine(
            $"{Prefix} dynamic-edge ratchet — ENFORCED against {baseline.Entries.Count} baseline entr(ies): "
            + $"{New.Length} NEW, {Stale.Length} stale, {Known.Length} known, {Unverifiable.Length} unverifiable");
        foreach (var (source, kind, target) in New)
            output.WriteLine(
                $"{Prefix} dynamic-edge NEW {source} {kind} {target} — a reflection binding into core the "
                + $"baseline does not carry. Bind it statically, or record it by adding the line '{source} {kind} {target}'.");
        foreach (var entry in Stale)
            output.WriteLine($"{Prefix} dynamic-edge STALE baseline entry (its source compiled without it — remove the line): {entry}");
        foreach (var entry in Unverifiable)
            output.WriteLine($"{Prefix} dynamic-edge unverifiable baseline entry ({entry.Source} was not extracted this run — kept): {entry}");
    }
}

/// <summary>
/// The RECONCILIATION of what a tree's code USED (the static and dynamic edges of
/// <c>dependency-edges.json</c>) with what it DECLARED (<c>Doc/Architecture/DependencyGraph</c> →
/// <i>The reconciliation report</i>). Per package (a node-repo top-level folder) or module (a
/// project), three flags: used-but-undeclared, declared-but-unused, and every dynamic edge into
/// core. A report, never a gate — only the dynamic-edge ratchet decides anything.
/// </summary>
public static class DependencyReconciliation
{
    /// <summary>The report's file name, beside <see cref="DependencyEdgesFile.FileName"/>.</summary>
    public const string FileName = "dependency-report.json";

    /// <summary>The verb: <c>mw-plugin-test dependency-report &lt;edges.json&gt; …</c>.</summary>
    public const string Verb = "dependency-report";

    /// <summary>One flagged dependency.</summary>
    /// <param name="Kind">The edge kind, or the declaration kind (<c>requires</c>, <c>module</c>,
    /// <c>nuget</c>, <c>ProjectReference</c>, <c>PackageReference</c>).</param>
    /// <param name="Target">What is used or declared.</param>
    /// <param name="Sources">The sources that use it (empty for a declaration).</param>
    public sealed record Finding(string Kind, string Target, ImmutableArray<string> Sources);

    /// <summary>One dynamic edge, with its source.</summary>
    /// <param name="Source">The edge set's source.</param>
    /// <param name="Kind">The edge kind.</param>
    /// <param name="Target">The edge target.</param>
    /// <param name="TargetKind">What the target is, when known.</param>
    /// <param name="Evidence">The call sites.</param>
    public sealed record DynamicFinding(
        string Source, string Kind, string Target, string? TargetKind, ImmutableList<string> Evidence);

    /// <summary>One package's or module's reconciliation.</summary>
    /// <param name="Unit">The package id or module assembly name.</param>
    /// <param name="Shape"><c>package</c> (a node-repo folder) or <c>module</c> (a project).</param>
    /// <param name="Sources">The edge sets the unit owns.</param>
    /// <param name="UsedButUndeclared">Used, and no declaration covers it.</param>
    /// <param name="DeclaredButUnused">Declared, and no edge uses it.</param>
    /// <param name="DynamicIntoCore">Every dynamic edge into core — the ratchet's subject.</param>
    /// <param name="DynamicElsewhere">Dynamic edges into modules, packages, its own code or the framework.</param>
    /// <param name="Platform">Platform assembly → how many of the unit's sources bind it statically.</param>
    /// <param name="Unverifiable">Declarations this reconciliation could not check (a <c>requires</c>
    /// outside the tree, a project file it could not find).</param>
    public sealed record UnitReport(
        string Unit,
        string Shape,
        ImmutableArray<string> Sources,
        ImmutableArray<Finding> UsedButUndeclared,
        ImmutableArray<Finding> DeclaredButUnused,
        ImmutableArray<DynamicFinding> DynamicIntoCore,
        ImmutableArray<DynamicFinding> DynamicElsewhere,
        ImmutableSortedDictionary<string, int> Platform,
        ImmutableArray<string> Unverifiable)
    {
        /// <summary>The unit's sources the producer attempted and could not extract — whatever
        /// depends on them is unknown.</summary>
        public ImmutableArray<string> NotExtracted { get; init; } = [];
    }

    /// <summary>The whole report.</summary>
    /// <param name="FrameworkIdentity">The edges file's host identity.</param>
    /// <param name="Commit">The tree the edges were read from.</param>
    /// <param name="Units">One entry per package or module, ordinal.</param>
    /// <param name="NotExtracted">Sources the edges file lists as failed — unknown, never "depends on nothing".</param>
    public sealed record Report(
        string FrameworkIdentity,
        string? Commit,
        ImmutableArray<UnitReport> Units,
        ImmutableArray<string> NotExtracted);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };


    /// <summary>
    /// Reconciles <paramref name="document"/> with the declarations under <paramref name="treeRoot"/>.
    /// A source with a <c>/</c> is a NodeType of a node-repo package (declarations: the package
    /// manifest's <c>requires</c> and <c>module</c>, its source queries and node types, its
    /// <c>#r "nuget:"</c> directives); any other source is a module's assembly name (declarations:
    /// the producing build's EVALUATED <c>ProjectReference</c>s and <c>PackageReference</c>s, carried in the file).
    /// </summary>
    /// <param name="document">The edges.</param>
    /// <param name="treeRoot">The checkout the edges were read from, or null for edges-only flags.</param>
    public static Report Reconcile(DependencyEdgesFile.Document document, string? treeRoot)
    {
        ArgumentNullException.ThrowIfNull(document);
        static bool IsNodeType(string source) => source.Contains('/');
        var units = ImmutableArray.CreateBuilder<UnitReport>();
        var nodeTypeSets = document.Sets.Where(s => IsNodeType(s.Source)).ToList();
        var nodeTypeFailed = document.Failed.Where(IsNodeType).ToList();
        if (nodeTypeSets.Count > 0 || nodeTypeFailed.Count > 0)
            units.AddRange(ReconcilePackages(nodeTypeSets, nodeTypeFailed, document, treeRoot));
        var moduleSets = document.Sets.Where(s => !IsNodeType(s.Source)).ToList();
        var moduleFailed = document.Failed.Where(f => !IsNodeType(f)).ToList();
        if (moduleSets.Count > 0 || moduleFailed.Count > 0)
            units.AddRange(ReconcileModules(moduleSets, moduleFailed, document));
        return new Report(document.FrameworkIdentity, document.Commit,
            [.. units.OrderBy(u => u.Unit, StringComparer.Ordinal)], document.Failed);
    }

    /// <summary>The unverifiable notes every unit carries for what the PRODUCER did not measure —
    /// an unmeasured kind is unknown, never clean.</summary>
    private static IEnumerable<string> Unmeasured(DependencyEdgesFile.Document document)
    {
        if (!document.Has(DependencyEdgesFile.Measures.DynamicBindings))
            yield return "dynamic bindings were not measured by the producer — dynamic edges unknown";
        if (!document.Has(DependencyEdgesFile.Measures.ReferencedAssemblies))
            yield return "platform and package references were not measured by the producer — those edges unknown";
    }

    // ── node-repo packages ──────────────────────────────────────────────────────────────────────

    private sealed record PackageDeclarations(
        string Id,
        string? Module,
        ImmutableHashSet<string> DeclaredPackages,
        ImmutableArray<string> RawRequires,
        ImmutableHashSet<string> NuGet);

    private static IEnumerable<UnitReport> ReconcilePackages(
        List<DependencyEdgeSet> sets, List<string> failed, DependencyEdgesFile.Document document, string? treeRoot)
    {
        var declarations = ImmutableDictionary<string, PackageDeclarations>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        if (treeRoot is not null)
            declarations = ReadPackageDeclarations(treeRoot);
        var referencesMeasured = document.Has(DependencyEdgesFile.Measures.ReferencedAssemblies);

        string PackageOf(string source)
        {
            var first = source[..source.IndexOf('/')];
            return declarations.Keys.FirstOrDefault(k => string.Equals(k, first, StringComparison.OrdinalIgnoreCase)) ?? first;
        }

        ImmutableHashSet<string> Closure(string id)
        {
            var seen = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>([id]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (!seen.Add(next) || !declarations.TryGetValue(next, out var d))
                    continue;
                foreach (var dep in d.DeclaredPackages)
                    stack.Push(dep);
            }
            return seen.ToImmutable();
        }

        var setsByPackage = sets.ToLookup(s => PackageOf(s.Source), StringComparer.OrdinalIgnoreCase);
        var failedByPackage = failed.ToLookup(PackageOf, StringComparer.OrdinalIgnoreCase);
        var ids = setsByPackage.Select(g => g.Key).Concat(failedByPackage.Select(g => g.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var group = setsByPackage[id].ToList();
            var notExtracted = failedByPackage[id].ToList();
            declarations.TryGetValue(id, out var declared);
            var closure = Closure(id);
            var declaredModules = closure
                .Select(p => declarations.TryGetValue(p, out var d) ? d.Module : null)
                .OfType<string>()
                .ToImmutableHashSet(StringComparer.Ordinal);
            var edges = group.SelectMany(s => s.Edges.Select(e => (s.Source, Edge: e))).ToList();

            var undeclared = new List<Finding>();
            foreach (var byTarget in edges.Where(x => x.Edge.EvidenceClass == DependencyEvidenceClasses.Static)
                         .GroupBy(x => (x.Edge.Kind, x.Edge.Target)))
            {
                var (kind, target) = byTarget.Key;
                var covered = kind switch
                {
                    DependencyEdgeKinds.NodeType => target.Contains('/') && closure.Contains(PackageOf(target)),
                    DependencyEdgeKinds.Module => declaredModules.Contains(target),
                    DependencyEdgeKinds.Package => declared?.NuGet.Contains(DependencyEdgeTargets.PackageIdOf(target)) == true,
                    // The platform is on every host; a Platform edge needs no declaration.
                    _ => true,
                };
                if (!covered && treeRoot is not null)
                    undeclared.Add(new Finding(kind, target, Sources(byTarget.Select(x => x.Source))));
            }

            // "Declared but unused" is a claim about EVERY source of the package: one that was not
            // extracted could be the user, so the package's unmatched declarations are UNVERIFIABLE
            // until all of its sources were measured — never reported unused on partial evidence.
            var unused = new List<Finding>();
            var unverifiable = new List<string>(Unmeasured(document));
            void Unused(string kind, string target, bool measured)
            {
                if (!measured)
                    unverifiable.Add($"{kind} {target} — not judged: the producer did not measure the edges that would use it");
                else if (notExtracted.Count > 0)
                    unverifiable.Add($"{kind} {target} — not judged: {notExtracted.Count} source(s) of this package were not extracted ({string.Join(", ", notExtracted)})");
                else
                    unused.Add(new Finding(kind, target, []));
            }
            if (declared is not null)
            {
                foreach (var requirement in declared.RawRequires)
                {
                    if (!declarations.TryGetValue(requirement, out var required))
                    {
                        unverifiable.Add($"requires {requirement} — not a package of this tree");
                        continue;
                    }
                    var used = edges.Any(x =>
                        (x.Edge.Kind == DependencyEdgeKinds.NodeType && x.Edge.Target.Contains('/')
                         && string.Equals(PackageOf(x.Edge.Target), required.Id, StringComparison.OrdinalIgnoreCase))
                        || (x.Edge.Kind == DependencyEdgeKinds.Module && required.Module is not null
                            && string.Equals(x.Edge.Target, required.Module, StringComparison.Ordinal)));
                    if (!used)
                        Unused("requires", requirement, measured: true);
                }
                if (declared.Module is { } module
                    && !edges.Any(x => x.Edge.Kind == DependencyEdgeKinds.Module && x.Edge.Target == module))
                    Unused("module", module, measured: true);
                foreach (var nuget in declared.NuGet.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    if (!edges.Any(x => x.Edge.Kind == DependencyEdgeKinds.Package
                                        && string.Equals(DependencyEdgeTargets.PackageIdOf(x.Edge.Target), nuget, StringComparison.OrdinalIgnoreCase)))
                        Unused("nuget", nuget, referencesMeasured);
            }
            else if (treeRoot is not null)
                unverifiable.Add($"no package manifest for '{id}' under the tree");

            yield return Unit(id, "package", group, undeclared, unused, edges, unverifiable, notExtracted);
        }
    }

    private static ImmutableDictionary<string, PackageDeclarations> ReadPackageDeclarations(string treeRoot)
    {
        var snapshot = LocalNodeRepo.LoadSync(treeRoot);
        // DiscoverPackages composes over an in-memory snapshot and emits synchronously on Subscribe;
        // this reads it without a blocking bridge and says so if that ever stops being true.
        IReadOnlyList<PackageManifest>? packages = null;
        Exception? failure = null;
        using (LocalNodeRepo.DiscoverPackages(snapshot).Subscribe(p => packages = p, ex => failure = ex))
        {
        }
        if (failure is not null)
            throw new InvalidOperationException($"package discovery under '{treeRoot}' failed: {failure.Message}", failure);
        if (packages is null)
            throw new InvalidOperationException(
                $"package discovery under '{treeRoot}' did not complete synchronously over an in-memory snapshot — "
                + "the reconciliation refuses to report on a package set it did not read.");
        var map = LocalNodeRepo.DependencyMap(packages, snapshot);
        var builder = ImmutableDictionary.CreateBuilder<string, PackageDeclarations>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var prefix = (package.SourceFolder ?? package.Id) + "/";
            var nuget = snapshot.Files
                .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(f => NuGetDirectiveParser.Extract(CodeOf(f.Path, f.Content)).References.Select(r => r.Id))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            builder[package.Id] = new PackageDeclarations(
                package.Id,
                string.IsNullOrWhiteSpace(package.Module) ? null : package.Module,
                map.TryGetValue(package.Id, out var deps) ? deps.WithComparer(StringComparer.OrdinalIgnoreCase) : [],
                [.. package.Requires.Select(PackageDependencyGraph.DependencyId).Where(r => r.Length > 0 && r != package.Id).Distinct()],
                nuget);
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The C# a file contributes to a compile, DECODED: a <c>.cs</c> Code node is its own text; a
    /// <c>.json</c> node carries its code as the string <c>content.code</c>, which must be read as
    /// JSON — scanned raw, its escaped quotes (<c>#r \"nuget:…\"</c>) match no directive. Anything
    /// else contributes no code.
    /// </summary>
    /// <param name="path">The file's tree path.</param>
    /// <param name="content">The file's text.</param>
    internal static string CodeOf(string path, string content)
    {
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return content;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("content", out var body)
                   && body.ValueKind == JsonValueKind.Object
                   && body.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // ── module projects ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A module's declarations are the PRODUCING build's evaluated ones
    /// (<see cref="DependencyEdgesFile.Document.Declarations"/> — imports, conditions and property
    /// expansion already applied by <c>ProjectFile.Load</c>), never raw project XML re-read here. A
    /// file that does not carry them leaves every declaration-level flag UNVERIFIABLE.
    /// </summary>
    private static IEnumerable<UnitReport> ReconcileModules(
        List<DependencyEdgeSet> sets, List<string> failed, DependencyEdgesFile.Document document)
    {
        var declarationsMeasured = document.Has(DependencyEdgesFile.Measures.Declarations);
        var referencesMeasured = document.Has(DependencyEdgesFile.Measures.ReferencedAssemblies);
        foreach (var set in sets.OrderBy(s => s.Source, StringComparer.Ordinal))
        {
            var edges = set.Edges.Select(e => (set.Source, Edge: e)).ToList();
            var undeclared = new List<Finding>();
            var unused = new List<Finding>();
            var unverifiable = new List<string>(Unmeasured(document));
            if (!declarationsMeasured || !document.Declarations.TryGetValue(set.Source, out var declared))
                unverifiable.Add("declarations not recorded by the producing build — used-but-undeclared and declared-but-unused not judged");
            else
            {
                var projectRefs = declared.ProjectReferences.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                var packageRefs = declared.PackageReferences.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var edge in set.Edges.Where(e => e.EvidenceClass == DependencyEvidenceClasses.Static))
                {
                    var covered = edge.Kind switch
                    {
                        DependencyEdgeKinds.Module => projectRefs.Contains(edge.Target),
                        DependencyEdgeKinds.Platform => packageRefs.Contains(edge.Target) || projectRefs.Contains(edge.Target),
                        DependencyEdgeKinds.Package => packageRefs.Contains(DependencyEdgeTargets.PackageIdOf(edge.Target)),
                        _ => true,
                    };
                    if (!covered)
                        undeclared.Add(new Finding(edge.Kind, edge.Target, [set.Source]));
                }
                // A ProjectReference OUTSIDE the source root resolves from the host and shows up as a
                // Platform edge, not a Module edge — either one is a use.
                foreach (var reference in projectRefs.OrderBy(r => r, StringComparer.Ordinal))
                    if (!set.Edges.Any(e => e.Kind is DependencyEdgeKinds.Module or DependencyEdgeKinds.Platform
                                            && string.Equals(e.Target, reference, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (referencesMeasured)
                            unused.Add(new Finding("ProjectReference", reference, []));
                        else
                            unverifiable.Add($"ProjectReference {reference} — not judged: platform references were not measured");
                    }
                // Only the CORE package references are judged: a third-party package the image also
                // carries resolves to the host's copy and is no Package edge, so "unused" would lie.
                foreach (var package in packageRefs
                             .Where(DependencyReferenceClasses.IsPlatformName).OrderBy(r => r, StringComparer.Ordinal))
                    if (!set.Edges.Any(e => e.Kind == DependencyEdgeKinds.Platform
                                            && string.Equals(e.Target, package, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (referencesMeasured)
                            unused.Add(new Finding("PackageReference", package, []));
                        else
                            unverifiable.Add($"PackageReference {package} — not judged: platform references were not measured");
                    }
            }
            yield return Unit(set.Source, "module", [set], undeclared, unused, edges, unverifiable, []);
        }
        foreach (var source in failed.OrderBy(f => f, StringComparer.Ordinal))
            yield return Unit(source, "module", [], [], [], [],
                ["not extracted (the build of this module failed) — nothing about it was measured"], [source]);
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────

    private static UnitReport Unit(
        string unit, string shape, IEnumerable<DependencyEdgeSet> sets,
        List<Finding> undeclared, List<Finding> unused,
        List<(string Source, DependencyEdge Edge)> edges, IEnumerable<string> unverifiable,
        IEnumerable<string> notExtracted)
    {
        DynamicFinding Dyn((string Source, DependencyEdge Edge) x) =>
            new(x.Source, x.Edge.Kind, x.Edge.Target, x.Edge.TargetKind, x.Edge.Evidence);
        var dynamic = edges.Where(x => x.Edge.EvidenceClass == DependencyEvidenceClasses.Dynamic).ToList();
        return new UnitReport(
            unit, shape,
            Sources(sets.Select(s => s.Source)),
            [.. undeclared.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Target, StringComparer.Ordinal)],
            [.. unused.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Target, StringComparer.Ordinal)],
            [.. dynamic.Where(x => DynamicEdgeRatchet.IsIntoCore(x.Edge)).Select(Dyn)],
            [.. dynamic.Where(x => !DynamicEdgeRatchet.IsIntoCore(x.Edge)).Select(Dyn)],
            edges.Where(x => x.Edge.Kind == DependencyEdgeKinds.Platform)
                .GroupBy(x => x.Edge.Target, StringComparer.Ordinal)
                .ToImmutableSortedDictionary(g => g.Key, g => g.Select(x => x.Source).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal),
            [.. unverifiable.Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal)])
        {
            NotExtracted = Sources(notExtracted),
        };
    }

    private static ImmutableArray<string> Sources(IEnumerable<string> sources) =>
        [.. sources.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)];

    /// <summary>The report's JSON text.</summary>
    /// <param name="report">The report.</param>
    public static string Serialize(Report report) => JsonSerializer.Serialize(report, Json);

    /// <summary>Writes <see cref="FileName"/> into <paramref name="directory"/>.</summary>
    /// <param name="directory">The output directory.</param>
    /// <param name="report">The report.</param>
    public static string Write(string directory, Report report)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path, Serialize(report));
        return path;
    }

    /// <summary>The console summary: one line per unit with a flag, plus the totals.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="report">The report.</param>
    public static void Summarize(TextWriter output, Report report)
    {
        var p = DynamicEdgeRatchet.Prefix;
        output.WriteLine(
            $"{p} reconciled {report.Units.Length} unit(s): "
            + $"{report.Units.Sum(u => u.UsedButUndeclared.Length)} used-but-undeclared, "
            + $"{report.Units.Sum(u => u.DeclaredButUnused.Length)} declared-but-unused, "
            + $"{report.Units.Sum(u => u.DynamicIntoCore.Length)} dynamic into core, "
            + $"{report.Units.Sum(u => u.DynamicElsewhere.Length)} dynamic elsewhere, "
            + $"{report.NotExtracted.Length} not extracted (unknown), "
            + $"{report.Units.Sum(u => u.Unverifiable.Length)} unverifiable");
        foreach (var unit in report.Units)
        {
            foreach (var f in unit.UsedButUndeclared)
                output.WriteLine($"{p} {unit.Unit} used-but-undeclared {f.Kind} {f.Target} ({string.Join(", ", f.Sources)})");
            foreach (var f in unit.DeclaredButUnused)
                output.WriteLine($"{p} {unit.Unit} declared-but-unused {f.Kind} {f.Target}");
            foreach (var d in unit.DynamicIntoCore)
                output.WriteLine($"{p} {unit.Unit} dynamic-into-core {d.Source} {d.Kind} {d.Target} [{string.Join("; ", d.Evidence)}]");
            foreach (var u in unit.Unverifiable)
                output.WriteLine($"{p} {unit.Unit} UNVERIFIABLE {u}");
        }
    }

    /// <summary>
    /// The <c>dependency-report</c> verb:
    /// <c>dependency-report &lt;edges.json&gt; [--tree &lt;dir&gt;] [--output &lt;dir&gt;] [--dynamic-baseline &lt;file&gt;] [--repo-name &lt;name&gt;]</c>.
    /// Exit 0 green (or observe-only), 1 the ratchet failed, 2 bad usage.
    /// </summary>
    /// <param name="args">The verb's arguments.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? edgesPath = null, tree = null, outputDirectory = null, repoName = null;
        var baseline = DynamicEdgeBaseline.ObserveOnly;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tree" when i + 1 < args.Length: tree = args[++i]; break;
                case "--output" when i + 1 < args.Length: outputDirectory = args[++i]; break;
                case "--repo-name" when i + 1 < args.Length: repoName = args[++i]; break;
                case "--dynamic-baseline" when i + 1 < args.Length:
                    if (LoadBaseline(args[++i], error, Verb) is not { } loaded)
                        return 2;
                    baseline = loaded;
                    break;
                case "--tree" or "--output" or "--repo-name" or "--dynamic-baseline":
                    error.WriteLine($"{Verb}: option '{args[i]}' requires a value.");
                    return 2;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith('-') || edgesPath is not null)
                    {
                        error.WriteLine($"{Verb}: unknown argument '{args[i]}'. Try --help.");
                        return 2;
                    }
                    edgesPath = args[i];
                    break;
            }
        }
        if (edgesPath is null)
        {
            error.WriteLine($"{Verb}: the edges file is required. {Usage}");
            return 2;
        }
        if (Directory.Exists(edgesPath))
            edgesPath = Path.Combine(edgesPath, DependencyEdgesFile.FileName);
        if (!File.Exists(edgesPath))
        {
            error.WriteLine($"{Verb}: '{Path.GetFullPath(edgesPath)}' does not exist — nothing was extracted, so nothing can be reconciled.");
            return 2;
        }
        if (tree is not null && !Directory.Exists(tree))
        {
            error.WriteLine($"{Verb}: --tree '{Path.GetFullPath(tree)}' does not exist.");
            return 2;
        }
        var document = DependencyEdgesFile.Deserialize(File.ReadAllText(edgesPath));
        if (document is null)
        {
            error.WriteLine($"{Verb}: '{edgesPath}' has a schema this reader does not know (expected {DependencyEdgesFile.Schema}).");
            return 2;
        }
        return Apply(document, tree, outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(edgesPath))!,
            baseline, repoName, output) ? 0 : 1;
    }

    /// <summary>Loads a <c>--dynamic-baseline</c>, printing the refusal and returning null on a
    /// missing or malformed file.</summary>
    /// <param name="path">The path as given.</param>
    /// <param name="error">Where to print a refusal.</param>
    /// <param name="verb">The verb, for the message.</param>
    public static DynamicEdgeBaseline? LoadBaseline(string path, TextWriter error, string verb)
    {
        if (!File.Exists(path))
        {
            error.WriteLine($"{verb}: {DynamicEdgeBaseline.MissingFileMessage(path)}");
            return null;
        }
        try
        {
            return DynamicEdgeBaseline.Load(path);
        }
        catch (FormatException ex)
        {
            error.WriteLine($"{verb}: --dynamic-baseline file '{GateAllowlist.Describe(path)}' is malformed — {ex.Message}");
            return null;
        }
    }

    /// <summary>Reconciles, writes the report beside the edges, prints the summary and the ratchet,
    /// and returns the ratchet's verdict.</summary>
    /// <param name="document">The edges.</param>
    /// <param name="tree">The checkout, or null.</param>
    /// <param name="outputDirectory">Where the report goes.</param>
    /// <param name="baseline">The dynamic-edge baseline.</param>
    /// <param name="repoName">The repository, named in the observe-only line.</param>
    /// <param name="output">Where to print.</param>
    public static bool Apply(
        DependencyEdgesFile.Document document, string? tree, string outputDirectory,
        DynamicEdgeBaseline baseline, string? repoName, TextWriter output)
    {
        var report = Reconcile(document, tree);
        var path = Write(outputDirectory, report);
        Summarize(output, report);
        output.WriteLine($"{DynamicEdgeRatchet.Prefix} report → {path}");
        var ratchet = DynamicEdgeRatchet.Evaluate(document, baseline);
        ratchet.Write(output, baseline, repoName);
        return ratchet.Success;
    }

    /// <summary>The verb's usage.</summary>
    public const string Usage =
        "usage: mw-plugin-test dependency-report <dependency-edges.json | dir> [--tree <checkout>] "
        + "[--output <dir>] [--dynamic-baseline <file>] [--repo-name <owner/repo>]\n"
        + "  Reconciles the edges a bake or a module workspace build wrote with what the tree DECLARES "
        + "(package manifests' requires/module, #r \"nuget:\" directives; the producing build's evaluated ProjectReference/"
        + "PackageReference) and writes dependency-report.json: per package or module, used-but-undeclared, "
        + "declared-but-unused, and every dynamic edge into core.\n"
        + "  --dynamic-baseline arms the shrink-only dynamic-edge ratchet: lines are "
        + "'<source> <Dynamic|DynamicUnresolved> <target>'; an unlisted dynamic edge into core fails, a "
        + "listed one whose source compiled without it is STALE and fails. Without it the run is "
        + "OBSERVE-ONLY and says so by name.";
}
