using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.PluginTester;

/// <summary>
/// <c>mw-plugin-test build &lt;repo-root&gt; [&lt;package&gt;… | all]</c> — the new build process
/// for node repos (maintainer, 2026-08-30): <b>build always means compile AND run tests</b>, per
/// package, over the package dependency network, as a reactive cascade, inside the container the
/// platform build produced. Nothing here imports a node into a mesh. The sources are read from the
/// checkout on disk, composed into Roslyn compilations exactly as the portal composes them
/// (<see cref="NodeSetCompiler"/> — the same skeleton, the same options, the same
/// <c>/app</c> reference set), and each package compiles against the assemblies its dependency
/// packages just emitted.
///
/// <para><b>The cascade.</b> <see cref="Cascade"/> gives every package a result stream; a package
/// subscribes to its dependencies' streams and starts the moment the last one completes green.
/// Independent packages build in parallel up to <see cref="Options.MaxParallel"/>. A red package
/// (compile error, failed test, load failure) stops there: every package that requires it is
/// reported <c>blocked by &lt;it&gt;</c> and never starts — on red we break, on green we continue.</para>
///
/// <para><b>Selection.</b> Naming packages selects them AND their transitive requirements inside
/// this repo, so a single package builds on freshly built dependencies rather than on nothing.
/// <c>all</c> (the default) is the full rebuild — the platform-rebuild case, where every package
/// must be re-proven against the new image.</para>
///
/// <para><b>Timings.</b> Every package carries ready/started/finished, the compile and test
/// splits, and per-type compile times; the report prints them and the critical path — the chain
/// of packages whose serial length is the wall-clock floor.</para>
///
/// <para><b>What the mesh lane still owns.</b> Test cases that take a host (a layout-area
/// <c>Tests</c> aggregator, anything needing a hub) are counted and named as <c>needs-mesh</c>
/// rather than run; the gate (<see cref="PluginGateRunner"/>) runs them, seeded from this build's
/// <see cref="Options.OutputDirectory"/> so nothing is compiled twice. Reported, never hidden.</para>
///
/// <para><b>Parity flag.</b> The portal compiles a type against the framework and its modules and
/// reaches other packages' types by <c>shared=</c> source inclusion — never by referencing their
/// emitted assemblies. This build references them (the maintainer's instruction: use the
/// references the dependency packages produce). A type whose emitted assembly turns out to BIND a
/// dependency package's assembly is therefore green here on grounds the portal does not have, and
/// the report marks it <c>binds-dependency-assembly</c> so that difference is visible rather than
/// discovered as a CompileError in production.</para>
/// </summary>
public static class CascadeBuild
{
    /// <summary>The CLI verb.</summary>
    public const string Verb = "build";

    /// <summary>The <c>all</c> selector.</summary>
    public const string AllSelector = "all";

    /// <summary>Options for one cascade build.</summary>
    public sealed record Options
    {
        /// <summary>The checkout root holding the node-repo packages.</summary>
        public required string RepoRoot { get; init; }

        /// <summary>The packages to build; empty or <c>all</c> means every package.</summary>
        public IReadOnlyList<string> Packages { get; init; } = [];

        /// <summary>External module assemblies composed into the reference set (<c>--module</c>).</summary>
        public IReadOnlyList<string> ModuleAssemblyPaths { get; init; } = [];

        /// <summary>The platform host's application directory to compile against and address the
        /// bundles to (<c>--app</c>; see <see cref="TreeBake.Options.AppDirectory"/>). Null = this process.</summary>
        public string? AppDirectory { get; init; }

        /// <summary>The platform host's shared-frameworks root (<c>--shared-frameworks</c>); required
        /// with <see cref="AppDirectory"/>.</summary>
        public string? SharedFrameworksRoot { get; init; }

        /// <summary>Where per-package prebuilt bundles are written; null keeps the build verdict-only.</summary>
        public string? OutputDirectory { get; init; }

        /// <summary>Where the JSON report is written; null prints only the table.</summary>
        public string? ReportPath { get; init; }

        /// <summary>Concurrency cap for package work (compile + tests).</summary>
        public int MaxParallel { get; init; } = Math.Max(1, Environment.ProcessorCount);

        /// <summary>Hard cap per test case.</summary>
        public TimeSpan CaseTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>False compiles only — for a type-check lane; the default builds tests too.</summary>
        public bool RunTests { get; init; } = true;

        /// <summary>The source commit recorded in the bundles; defaults to the snapshot's.</summary>
        public string? SourceSha { get; init; }

        /// <summary>Where progress lines go.</summary>
        public TextWriter Output { get; init; } = Console.Out;

        /// <summary>Compiler logging.</summary>
        public ILogger Logger { get; init; } = NullLogger.Instance;

        /// <summary>Logger factory for the NuGet resolver.</summary>
        public ILoggerFactory? LoggerFactory { get; init; }
    }

    /// <summary>One NodeType's build inside a package.</summary>
    public sealed record TypeBuild(
        string NodePath,
        string Package,
        string? CompileError,
        TimeSpan CompileTime,
        int SourceCount,
        string? DllPath,
        StaticTestRunner.Run? Tests,
        ImmutableArray<string> BindsDependencyAssemblies)
    {
        /// <summary>Compiled and every test that ran passed.</summary>
        public bool IsGreen => CompileError is null && (Tests is null || Tests.IsGreen);
    }

    /// <summary>One package's build: its types, what it emitted, and the two time splits.</summary>
    public sealed record PackageBuild(
        string Id,
        ImmutableArray<TypeBuild> Types,
        ImmutableArray<string> EmittedAssemblies,
        TimeSpan Compile,
        TimeSpan Tests,
        ImmutableArray<string> ExternalRequirements)
    {
        /// <summary>Every type compiled and tested green.</summary>
        public bool IsGreen => Types.All(t => t.IsGreen);

        /// <summary>Cases that ran and passed, across the package.</summary>
        public int TestsPassed => Types.Sum(t => t.Tests?.Passed ?? 0);

        /// <summary>Cases that ran and failed.</summary>
        public int TestsFailed => Types.Sum(t => t.Tests?.Failed ?? 0);

        /// <summary>Cases left to the mesh lane.</summary>
        public int TestsNeedMesh => Types.Sum(t => t.Tests?.NeedsMesh ?? 0);

        /// <summary>
        /// Cases that ran and DECLINED. 🚨 Its own number, never added to
        /// <see cref="TestsPassed"/>: a package line reading "12 passed" over a suite where three
        /// threw <c>SkipException</c> asserts evidence that was never produced.
        /// </summary>
        public int TestsSkipped => Types.Sum(t => t.Tests?.Skipped ?? 0);
    }

    /// <summary>The whole build.</summary>
    public sealed record Report(
        string FrameworkIdentity,
        ImmutableArray<Cascade.NodeResult<PackageBuild>> Packages,
        ImmutableArray<string> CriticalPath,
        TimeSpan Wall,
        ImmutableArray<string> Bundles,
        string? FatalError = null)
    {
        /// <summary>0 green, 1 any red or blocked, 70 fatal.</summary>
        public int ExitCode =>
            FatalError is not null ? 70
            : Packages.All(p => p.IsGreen) ? 0
            : 1;
    }

    /// <summary>
    /// Every progress line starts with the UTC time and the managed thread — packages build in
    /// parallel, so a reader must be able to tell whose line this is (maintainer, 2026-08-30:
    /// "output the time and which package … and for multithreaded also some thread id").
    /// </summary>
    private static string Stamp() =>
        $"{DateTime.UtcNow:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:D3}]";

    /// <summary>Runs the build and emits the report once; the exit code is <see cref="Report.ExitCode"/>.</summary>
    public static IObservable<Report> Run(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Observable.Defer(() => RunCore(options))
            .Catch((Exception ex) => Observable.Return(
                new Report(PrebuiltAssemblySeeder.LiveFrameworkMvid, [], [], TimeSpan.Zero, [],
                    // The full exception, stack included — this string is the ONLY trace a fatal
                    // leaves, and "TypeName: message" was not enough to locate the first real one.
                    ex.ToString())))
            // 🚨 A FATAL is NEVER silent. Every early exit — LoadSync throwing, no packages, an
            // unknown package id, LoadExternalModules refusing, the Catch above — returns its
            // Report BEFORE the pipeline reaches Finish, so Print never sees it and Program only
            // turns it into an exit code. Measured on the staging-7f99ee5 image (2026-08-30):
            // `build /repo OgCard` died with exit 70 and ZERO bytes on stdout AND stderr — CI's
            // cascade step would render that as "fatal (exit 70) — see build.log" over an EMPTY
            // log, a signal that cannot distinguish "crashed before it could speak" from "ran and
            // produced nothing". Print at the one seam every report passes; Finish never sets
            // FatalError, so nothing prints twice. The report file is written too when asked —
            // a fatal must be machine-readable to the lane, not only human-readable in the log.
            .Do(report =>
            {
                if (report.FatalError is null)
                    return;
                options.Output.WriteLine($"FATAL: {report.FatalError}");
                if (options.ReportPath is { } reportPath)
                    WriteJson(reportPath, report);
            });
    }

    private static IObservable<Report> RunCore(Options options)
    {
        var wall = Stopwatch.StartNew();
        // This process's identity — what a FATAL before the host is resolved is reported under.
        // Every bundle is keyed to the HOST's identity, which Build() resolves (BakeHost).
        var frameworkIdentity = PrebuiltAssemblySeeder.LiveFrameworkMvid;

        RepoSnapshot snapshot;
        try
        {
            snapshot = LocalNodeRepo.LoadSync(options.RepoRoot);
        }
        catch (Exception ex)
        {
            // Full exception — the fatal string is the only trace this run leaves (see Run's Do).
            return Observable.Return(Fatal(frameworkIdentity, wall, ex.ToString()));
        }
        return LocalNodeRepo.DiscoverPackages(snapshot)
            .Take(1)
            .SelectMany(packages => Build(options, wall, frameworkIdentity, snapshot, packages));
    }

    private static IObservable<Report> Build(
        Options options, Stopwatch wall, string frameworkIdentity, RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages)
    {
        if (packages.Count == 0)
            return Observable.Return(Fatal(frameworkIdentity, wall,
                $"No node-repo packages (top-level folders with an index.json root) found under "
                + $"'{Path.GetFullPath(options.RepoRoot)}'."));

        var byId = packages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var selection = Select(options.Packages, byId, out var unknown);
        if (unknown.Length > 0)
            return Observable.Return(Fatal(frameworkIdentity, wall,
                $"unknown package(s): {string.Join(", ", unknown)} — known: "
                + string.Join(", ", packages.Select(p => p.Id))));

        var skipped = new List<string>();
        var treeNodes = TreeNodeLoader.Load(
            snapshot, packages, (path, reason) => skipped.Add($"{path}: {reason}"));
        foreach (var skip in skipped)
            options.Output.WriteLine($"build: skipped (not a materialisable node) {skip}");
        var nodeSet = NodeSet.Create(treeNodes.Select(t => t.Node));
        var typesByPackage = treeNodes
            .Where(t => t.Node.Content is NodeTypeDefinition)
            .GroupBy(t => t.Package, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Node.Path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        IReadOnlyList<InstalledModuleAssembly> modules;
        try
        {
            modules = TreeBake.LoadExternalModules(new TreeBake.Options
            {
                RepoRoot = options.RepoRoot,
                OutputDirectory = options.OutputDirectory ?? Path.GetTempPath(),
                ModuleAssemblyPaths = options.ModuleAssemblyPaths,
                Output = options.Output,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Observable.Return(Fatal(frameworkIdentity, wall, ex.Message));
        }
        // The PLATFORM HOST (#3022) — one directory for the reference set, the identity and the
        // dependency records, refused by name when this process cannot honestly bake for it. See
        // BakeHost; the same seam TreeBake.Run uses, so the two verbs cannot disagree about it.
        if (options.AppDirectory is not null && options.SharedFrameworksRoot is null)
            return Observable.Return(Fatal(frameworkIdentity, wall,
                "AppDirectory was given without SharedFrameworksRoot — the platform host's shared "
                + "frameworks are part of its reference set and are never inferred from the running "
                + "runtime (pass --shared-frameworks <dotnet root>/shared)."));
        var (host, hostProblem) = options.AppDirectory is null
            ? ((BakeHost?)BakeHost.InProcess(modules), (string?)null)
            : BakeHost.ResolveDirectory(options.AppDirectory, options.SharedFrameworksRoot!, modules);
        if (host is null)
            return Observable.Return(Fatal(frameworkIdentity, wall, hostProblem!));
        frameworkIdentity = host.FrameworkIdentity;
        options.Output.WriteLine($"{Stamp()} build: {host.Description}");
        if (host.Note is { } hostNote)
            options.Output.WriteLine($"{Stamp()} build: ⚠ {hostNote}");
        var idOf = host.IdOf;
        var toolchainId = host.ToolchainId;
        var baseReferences = host.References;
        // Built on the first reference-shaped failure only; shared by every package's build.
        var attribution = new Lazy<ReferenceGapAttribution>(
            () => ReferenceGapAttribution.Create(baseReferences, host.AppDirectory),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var nugetResolver = new NuGetAssemblyResolver(
            options.LoggerFactory?.CreateLogger<NuGetAssemblyResolver>()
            ?? NullLogger<NuGetAssemblyResolver>.Instance);
        var workDirectory = Path.Combine(
            Path.GetTempPath(), $"mw-cascade-build-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        IReadOnlyList<string> DependenciesOf(string id) =>
            byId.TryGetValue(id, out var m)
                ? m.Requires.Select(PackageDependencyGraph.DependencyId).Where(d => d.Length > 0).ToArray()
                : [];

        options.Output.WriteLine(
            $"{Stamp()} build: {selection.Length} of {packages.Count} package(s) selected "
            + $"({(options.Packages.Count == 0 || options.Packages.Contains(AllSelector) ? "all — full rebuild" : string.Join(", ", options.Packages))}), "
            + $"{treeNodes.Length} node(s), max-parallel={options.MaxParallel}, tests={(options.RunTests ? "on" : "off")}, "
            + $"framework={frameworkIdentity}");

        var entriesByPackage = new Dictionary<string, List<BundleWriter.AssemblyEntry>>(StringComparer.Ordinal);
        var entriesLock = new object();

        return Cascade.Run<PackageBuild>(
                selection,
                DependenciesOf,
                (id, deps) =>
                {
                    var build = BuildPackage(
                        options, id, typesByPackage.TryGetValue(id, out var types) ? types : [],
                        deps, byId, nodeSet, baseReferences, idOf, toolchainId, attribution, workDirectory, nugetResolver,
                        entriesByPackage, entriesLock);
                    return (build, build.IsGreen);
                },
                options.MaxParallel)
            .Select(results => Finish(options, wall, frameworkIdentity, snapshot, packages, results, entriesByPackage, workDirectory, DependenciesOf));
    }

    private static Report Finish(
        Options options, Stopwatch wall, string frameworkIdentity, RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages, ImmutableArray<Cascade.NodeResult<PackageBuild>> results,
        Dictionary<string, List<BundleWriter.AssemblyEntry>> entriesByPackage, string workDirectory,
        Func<string, IReadOnlyList<string>> DependenciesOf)
    {
        var bundles = ImmutableArray<string>.Empty;
        if (options.OutputDirectory is { } outDir)
        {
            bundles = TreeBake.WriteBundles(
                new TreeBake.Options
                {
                    RepoRoot = options.RepoRoot,
                    OutputDirectory = outDir,
                    SourceSha = options.SourceSha,
                    Output = options.Output,
                },
                packages, snapshot, frameworkIdentity, entriesByPackage);
        }

        try
        {
            if (options.OutputDirectory is null && Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
        catch (IOException)
        {
        }

        var report = new Report(
            frameworkIdentity, results, Cascade.CriticalPath(results, DependenciesOf), wall.Elapsed, bundles);
        Print(options.Output, report, DependenciesOf);
        if (options.ReportPath is { } reportPath)
            WriteJson(reportPath, report);
        return report;
    }

    /// <summary>
    /// The requested ids plus their transitive requirements inside the repo. External
    /// requirements (a package this repo does not hold) are not an error here — they are named on
    /// the package that needs them and satisfied by <c>--module</c> or by the image itself.
    /// </summary>
    internal static ImmutableArray<string> Select(
        IReadOnlyList<string> requested, IReadOnlyDictionary<string, PackageManifest> byId,
        out ImmutableArray<string> unknown)
    {
        if (requested.Count == 0 || requested.Any(r => string.Equals(r, AllSelector, StringComparison.OrdinalIgnoreCase)))
        {
            unknown = [];
            return [.. byId.Keys.OrderBy(k => k, StringComparer.Ordinal)];
        }
        unknown = [.. requested.Where(r => !byId.ContainsKey(r)).Distinct(StringComparer.Ordinal)];
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(requested.Where(byId.ContainsKey));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!selected.Add(id))
                continue;
            foreach (var dep in byId[id].Requires.Select(PackageDependencyGraph.DependencyId))
            {
                if (dep.Length > 0 && byId.ContainsKey(dep))
                    stack.Push(dep);
            }
        }
        return [.. selected.OrderBy(k => k, StringComparer.Ordinal)];
    }

    private static PackageBuild BuildPackage(
        Options options,
        string id,
        TreeNodeLoader.TreeNode[] types,
        IReadOnlyList<Cascade.NodeResult<PackageBuild>> deps,
        IReadOnlyDictionary<string, PackageManifest> byId,
        NodeSet nodeSet,
        IReadOnlyList<MetadataReference> baseReferences,
        Func<string, string?> idOf,
        string toolchainId,
        Lazy<ReferenceGapAttribution> attribution,
        string workDirectory,
        INuGetAssemblyResolver nugetResolver,
        Dictionary<string, List<BundleWriter.AssemblyEntry>> entriesByPackage,
        object entriesLock)
    {
        var external = byId[id].Requires
            .Select(PackageDependencyGraph.DependencyId)
            .Where(d => d.Length > 0 && !byId.ContainsKey(d))
            .ToImmutableArray();
        var dependencyAssemblies = deps
            .Where(d => d.Result is not null)
            .SelectMany(d => d.Result!.EmittedAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dependencyNames = dependencyAssemblies
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var references = dependencyAssemblies.Length == 0
            ? baseReferences
            : baseReferences.Concat(dependencyAssemblies.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))).ToArray();

        options.Output.WriteLine(
            $"{Stamp()} [{id}] start — {types.Length} type(s), depends on "
            + (deps.Count == 0 ? "nothing" : string.Join(", ", deps.Select(d => d.Id)))
            + (external.Length == 0 ? "" : $" (+ external: {string.Join(", ", external)})"));

        var compileClock = Stopwatch.StartNew();
        var testClock = new Stopwatch();
        var built = ImmutableArray.CreateBuilder<TypeBuild>();
        var emitted = ImmutableArray.CreateBuilder<string>();
        var packageDir = Path.Combine(workDirectory, CodeConventions.SanitizeNodeName(id));

        foreach (var candidate in types)
        {
            var definition = (NodeTypeDefinition)candidate.Node.Content!;
            var resolution = nodeSet.ResolveSources(definition.Sources, definition.Tests, candidate.Node.Path);
            if (resolution.IsEstablished
                && resolution.Sources.IsEmpty
                && string.IsNullOrWhiteSpace(definition.Configuration))
                continue;

            var typeClock = Stopwatch.StartNew();
            NodeSetCompiler.CompiledNode compiled;
            try
            {
                compiled = NodeSetCompiler.Compile(
                    nodeSet, candidate.Node, definition.Sources, definition.Tests,
                    definition.Configuration, definition.ContentCollections,
                    references, idOf, toolchainId,
                    Path.Combine(packageDir, CodeConventions.SanitizeNodeName(candidate.Node.Path)),
                    resolveNuGet: TreeBake.BlockingNuGetResolution(nugetResolver),
                    logger: options.Logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                typeClock.Stop();
                // A reference-set gap is named in the verdict, never left reading as a content
                // error — the same attribution TreeBake appends (#3022).
                var gap = ReferenceGapAttribution.MayExplain(ex.Message)
                    ? attribution.Value.Explain(ex.Message)
                    : null;
                built.Add(new TypeBuild(
                    candidate.Node.Path, id,
                    $"{ex.GetType().Name}: {ex.Message}" + (gap is null ? string.Empty : $"\n   {gap}"),
                    typeClock.Elapsed, resolution.Sources.Length, null, null, []));
                options.Output.WriteLine($"{Stamp()} [{id}]   RED {candidate.Node.Path} ({typeClock.Elapsed.TotalMilliseconds:F0} ms)");
                options.Output.WriteLine(ex.Message);
                if (gap is not null)
                    options.Output.WriteLine($"   {gap}");
                continue;
            }
            typeClock.Stop();
            emitted.Add(compiled.DllPath);

            var binds = compiled.Dependencies.Keys
                .Where(dependencyNames.Contains)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToImmutableArray();

            var sourceVersions = resolution.Sources
                .Select(n => n.Path)
                .ToImmutableSortedDictionary(p => p, _ => 0L, StringComparer.Ordinal);
            lock (entriesLock)
            {
                if (!entriesByPackage.TryGetValue(id, out var entries))
                    entriesByPackage[id] = entries = [];
                entries.Add(new BundleWriter.AssemblyEntry(
                    compiled.NodePath,
                    () => File.OpenRead(compiled.DllPath),
                    compiled.PdbPath is null ? null : () => File.OpenRead(compiled.PdbPath),
                    sourceVersions,
                    compiled.Dependencies)
                {
                    // 🚨 #2813 — the CONTENT fingerprint of the sources this compile consumed, so
                    // the consumer can prove the bytes match the source IT holds instead of taking
                    // the bundle's word for it. Over the RAW resolved set, exactly as the runtime's
                    // sources watcher does: NodeTypeSourceFingerprint applies the shaping fold
                    // itself so the two callers cannot apply different ones — plus the
                    // `@@`-include closure the compile just resolved (#2948), which is where the
                    // Code nodes no source query matched are accounted for.
                    SourceFingerprint = NodeTypeSourceFingerprint.Compute(
                        resolution.Sources, candidate.Node.Path,
                        compiled.Inputs.ResolvedIncludes, options.Logger),
                });
            }

            StaticTestRunner.Run? tests = null;
            if (options.RunTests)
            {
                // Run whatever test classes the emitted assembly actually contains. A type's
                // Test/*.cs reaches the compile through the compiler's DEFAULT query — the node
                // declares `sources`, rarely `tests` — so gating on the declaration reported zero
                // tests for every package on the first real run (33319413459). The assembly is
                // the truth: no test classes, no cases, and the report says so.
                testClock.Start();
                tests = StaticTestRunner.Execute(
                    compiled.DllPath,
                    [.. options.ModuleAssemblyPaths, .. dependencyAssemblies, .. emitted],
                    options.CaseTimeout, options.Output);
                testClock.Stop();
            }

            built.Add(new TypeBuild(
                compiled.NodePath, id, null, typeClock.Elapsed, compiled.Inputs.MatchedSourcePaths.Length,
                compiled.DllPath, tests, binds));
            options.Output.WriteLine(
                $"{Stamp()} [{id}]   ok  {compiled.NodePath} ({typeClock.Elapsed.TotalMilliseconds:F0} ms, "
                + $"{compiled.Inputs.MatchedSourcePaths.Length} source(s))"
                + (tests is null ? "" : tests.Cases.IsEmpty
                    ? " tests: no test classes in the assembly"
                    : $" tests: {tests.Cases.Length} case(s) — {tests.Passed} passed, {tests.Failed} failed, "
                      + $"{tests.Skipped} skipped, {tests.NeedsMesh} needs-mesh")
                + (binds.IsEmpty ? "" : $" binds-dependency-assembly: {string.Join(", ", binds)}"));
        }
        compileClock.Stop();

        var result = new PackageBuild(
            id, built.ToImmutable(), emitted.ToImmutable(),
            compileClock.Elapsed - testClock.Elapsed, testClock.Elapsed, external);
        options.Output.WriteLine(
            $"{Stamp()} [{id}] {(result.IsGreen ? "GREEN" : "RED")} — compile {result.Compile.TotalSeconds:F1}s, "
            + $"tests {result.Tests.TotalSeconds:F1}s ({result.TestsPassed} passed, {result.TestsFailed} failed, "
            + $"{result.TestsSkipped} skipped, {result.TestsNeedMesh} needs-mesh)");
        return result;
    }

    private static Report Fatal(string frameworkIdentity, Stopwatch wall, string error) =>
        new(frameworkIdentity, [], [], wall.Elapsed, [], error);

    /// <summary>
    /// The summary table and the build line. 🚨 <c>internal</c> so the columns can be PINNED by a
    /// test: the header drifted out of alignment with the row format once already, and a column
    /// that silently disappears is how a skip gets read as a pass.
    /// </summary>
    internal static void Print(
        TextWriter output, Report report, Func<string, IReadOnlyList<string>> dependenciesOf)
    {
        output.WriteLine();
        if (report.FatalError is not null)
        {
            output.WriteLine($"FATAL: {report.FatalError}");
            return;
        }
        // 🚨 `skip` is a column of its own. It used to have nowhere to go, which meant a
        // declining case was invisible in the one table a reader actually looks at.
        output.WriteLine("package                        verdict    ready   queued    work  compile   tests  types passed failed  skip  mesh  note");
        foreach (var p in report.Packages.OrderBy(p => p.Finished))
        {
            var b = p.Result;
            var note = p.Outcome switch
            {
                Cascade.NodeOutcome.Blocked => $"blocked by {p.BlockedBy}",
                Cascade.NodeOutcome.Faulted => p.Error ?? "faulted",
                Cascade.NodeOutcome.Red when b is not null => string.Join("; ",
                    b.Types.Where(t => !t.IsGreen).Select(t =>
                        t.CompileError is not null ? $"{t.NodePath}: compile" : $"{t.NodePath}: {t.Tests?.Failed} failed")),
                _ => b is not null && b.Types.Any(t => !t.BindsDependencyAssemblies.IsEmpty)
                    ? "binds-dependency-assembly"
                    : "",
            };
            output.WriteLine(
                $"{p.Id,-30} {Verdict(p.Outcome),-8} {p.Ready.TotalSeconds,7:F1} {p.Queued.TotalSeconds,8:F1} "
                + $"{p.Work.TotalSeconds,7:F1} {(b?.Compile.TotalSeconds ?? 0),8:F1} {(b?.Tests.TotalSeconds ?? 0),7:F1} "
                + $"{(b?.Types.Length ?? 0),6} {(b?.TestsPassed ?? 0),6} {(b?.TestsFailed ?? 0),6} "
                + $"{(b?.TestsSkipped ?? 0),5} {(b?.TestsNeedMesh ?? 0),5}  {note}");
        }
        output.WriteLine();
        var green = report.Packages.Count(p => p.IsGreen);
        var red = report.Packages.Count(p => p.Outcome is Cascade.NodeOutcome.Red or Cascade.NodeOutcome.Faulted);
        var blocked = report.Packages.Count(p => p.Outcome == Cascade.NodeOutcome.Blocked);
        output.WriteLine(
            $"build: {green} green, {red} red, {blocked} blocked of {report.Packages.Length} in {report.Wall.TotalSeconds:F1}s wall; "
            + $"critical path ({report.CriticalPath.Length}): {string.Join(" → ", report.CriticalPath)}"
            + (report.Bundles.IsEmpty ? "" : $"; {report.Bundles.Length} bundle(s) written"));
        var serial = report.Packages.Sum(p => p.Work.TotalSeconds);
        if (serial > 0)
            output.WriteLine($"build: {serial:F1}s of work in {report.Wall.TotalSeconds:F1}s wall — parallel speed-up {serial / Math.Max(0.001, report.Wall.TotalSeconds):F1}×");
    }

    private static string Verdict(Cascade.NodeOutcome outcome) => outcome switch
    {
        Cascade.NodeOutcome.Green => "green",
        Cascade.NodeOutcome.Red => "RED",
        Cascade.NodeOutcome.Blocked => "blocked",
        _ => "FAULT",
    };

    private static void WriteJson(string path, Report report)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var shape = new
        {
            framework = report.FrameworkIdentity,
            wallSeconds = report.Wall.TotalSeconds,
            exitCode = report.ExitCode,
            criticalPath = report.CriticalPath,
            fatal = report.FatalError,
            packages = report.Packages.Select(p => new
            {
                id = p.Id,
                outcome = p.Outcome.ToString(),
                blockedBy = p.BlockedBy,
                error = p.Error,
                readySeconds = p.Ready.TotalSeconds,
                startedSeconds = p.Started.TotalSeconds,
                finishedSeconds = p.Finished.TotalSeconds,
                compileSeconds = p.Result?.Compile.TotalSeconds,
                testSeconds = p.Result?.Tests.TotalSeconds,
                external = p.Result?.ExternalRequirements,
                types = p.Result?.Types.Select(t => new
                {
                    path = t.NodePath,
                    compileMs = t.CompileTime.TotalMilliseconds,
                    sources = t.SourceCount,
                    compileError = t.CompileError,
                    bindsDependencyAssemblies = t.BindsDependencyAssemblies,
                    tests = t.Tests is null ? null : new
                    {
                        loadError = t.Tests.LoadError,
                        passed = t.Tests.Passed,
                        failed = t.Tests.Failed,
                        skipped = t.Tests.Skipped,
                        needsMesh = t.Tests.NeedsMesh,
                        cases = t.Tests.Cases.Select(c => new
                        {
                            name = c.Name,
                            outcome = c.Outcome.ToString(),
                            ms = c.Elapsed.TotalMilliseconds,
                            error = c.Error,
                            log = c.Log,
                        }),
                    },
                }),
            }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
    }
}
