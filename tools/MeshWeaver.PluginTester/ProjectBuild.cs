using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// <c>mw-plugin-test build-project &lt;csproj|dir&gt;</c> — compile a .NET project with NO dotnet
/// SDK and NO NuGet restore, against the assemblies of the container this process runs in
/// (maintainer, 2026-08-30: <i>"the platform builds dll completely without any external dotnet kit
/// or nuget"</i>).
///
/// <para><b>Three parts, each with one job.</b> <see cref="ProjectFile"/> evaluates the
/// <c>.csproj</c> without MSBuild and fails loudly on anything it cannot reproduce;
/// <see cref="ContainerReferenceSet"/> reads <c>/app</c> and the image's own <c>.deps.json</c> and
/// fails closed on anything partial; this type sequences the <c>ProjectReference</c> graph, runs
/// Roslyn through the platform's own <see cref="EmitPipeline"/>, and streams every diagnostic into
/// an <see cref="ActivityLog"/> as it is produced.</para>
///
/// <para>🚨 <b>It configures its OWN Roslyn options and touches no shared ones.</b>
/// <c>EmitPipeline.CreateCompilationOptions</c> feeds
/// <c>GeneratedInputIdentity.OptionsFingerprint</c>, which is part of every cached NodeType
/// assembly's key: changing it would invalidate every cached assembly on every mesh. A project
/// build needs different options (the project's nullable setting, its NoWarn, its
/// warnings-as-errors), so it builds them here and shares only the EMIT.</para>
///
/// <para><b>The diagnostic standard is the SDK's.</b> Nullable reference analysis is on when the
/// project says so, and <see cref="DocumentationMode.Diagnose"/> is on ALWAYS — an unresolvable
/// <c>cref</c> (CS1574), an ambiguous one (CS0419) and malformed doc XML (CS1570) are real defects,
/// and a builder that cannot see them is not reproducing the build the SDK would have produced.
/// Warnings fail the run by default; <c>--allow-warnings</c> is the deliberate opt-out.</para>
///
/// <para><b>Reactive.</b> The graph is sequenced by <see cref="Cascade"/> — the same dependency
/// cascade <c>mw-plugin-test build</c> runs on: every project observes its dependencies' result
/// streams and starts when the last one lands green, a cycle is refused by name before anything
/// runs, and the compile itself executes on the cascade's scheduler rather than on the caller's
/// thread. The one <c>Task</c> bridge in this tool stays where it already is — the console
/// boundary in <c>Program.cs</c>.</para>
/// </summary>
public static class ProjectBuild
{
    /// <summary>The CLI verb.</summary>
    public const string Verb = "build-project";

    /// <summary>Options for one project build.</summary>
    public sealed record Options
    {
        /// <summary>The <c>.csproj</c> to build, or a directory holding exactly one.</summary>
        public required string EntryProject { get; init; }

        /// <summary>Where the emitted assemblies land; a temp directory when null.</summary>
        public string? OutputDirectory { get; init; }

        /// <summary>The container's assembly directory — <c>/app</c> in every MeshWeaver image.</summary>
        public string AppDirectory { get; init; } = ContainerReferenceSet.DefaultAppDirectory;

        /// <summary>Directories of ADDITIONAL libraries — additional to the platform — that the
        /// container does not carry. The only sanctioned way to satisfy a <c>PackageReference</c>
        /// the image does not supply.</summary>
        public IReadOnlyList<string> ExtraReferenceDirectories { get; init; } = [];

        /// <summary>False (the default) fails the build on any warning the project did not suppress.</summary>
        public bool AllowWarnings { get; init; }

        /// <summary>The <c>--accept</c> tokens acknowledging constructs the evaluator cannot reproduce.</summary>
        public IReadOnlyList<string> Accept { get; init; } = [];

        /// <summary>
        /// Assemblies (or directories of them) to load Roslyn <c>[Generator]</c> source generators
        /// from. 🚨 A MeshWeaver image ships the RUNTIME, and the SDK's built-in generators
        /// (<c>GeneratedRegex</c>, <c>LoggerMessage</c>, <c>JsonSerializable</c>) live in the SDK, so
        /// a project using one cannot build here unless its generator is supplied. Nothing is
        /// guessed: with no generator the compile reports the SDK error and this builder names the
        /// generator that did not run.
        /// </summary>
        public IReadOnlyList<string> GeneratorPaths { get; init; } = [];

        /// <summary>Concurrency cap across independent projects.</summary>
        public int MaxParallel { get; init; } = Math.Max(1, Environment.ProcessorCount);

        /// <summary>Where the secondary, human rendering goes.</summary>
        public TextWriter Output { get; init; } = Console.Out;

        /// <summary>
        /// The PRIMARY sink: every progress line and every diagnostic arrives here the moment it is
        /// produced. The console is a rendering of this stream, not the other way round.
        /// </summary>
        public IObserver<LogMessage>? Log { get; init; }

        /// <summary>Overrides the process's TPA list; for tests that have no container.</summary>
        public string? TrustedPlatformAssemblies { get; init; }
    }

    /// <summary>One project's build.</summary>
    /// <param name="ProjectPath">The <c>.csproj</c>.</param>
    /// <param name="AssemblyName">The assembly it emits.</param>
    /// <param name="Elapsed">Wall time for this project alone.</param>
    /// <param name="SourceCount">How many <c>.cs</c> files went in.</param>
    /// <param name="Errors">Error-severity diagnostics.</param>
    /// <param name="Warnings">Warning-severity diagnostics that survived <c>NoWarn</c>.</param>
    /// <param name="AssemblyPath">The emitted DLL, when the emit succeeded.</param>
    /// <param name="UnresolvedPackages">Packages neither the container nor <c>--extra-refs</c> supplies.</param>
    /// <param name="Failure">Why the project is red, when it is.</param>
    public sealed record ProjectResult(
        string ProjectPath,
        string AssemblyName,
        TimeSpan Elapsed,
        int SourceCount,
        int Errors,
        int Warnings,
        string? AssemblyPath,
        ImmutableArray<string> UnresolvedPackages,
        string? Failure)
    {
        /// <summary>
        /// How many managed resources were embedded in the emitted assembly.
        ///
        /// <para>🚨 An <c>init</c> PROPERTY, not a primary-constructor parameter — adding a
        /// parameter REPLACES the record's signature, and every assembly compiled against the old
        /// arity would call a constructor that no longer exists
        /// (<c>scripts/check-record-signatures.py</c>).</para>
        /// </summary>
        public int ResourceCount { get; init; }

        /// <summary>Compiled, emitted, and within the warning policy.</summary>
        public bool IsGreen => Failure is null && AssemblyPath is not null;
    }

    /// <summary>The whole run.</summary>
    /// <param name="AppDirectory">The container directory the reference set came from.</param>
    /// <param name="PlatformAssemblyVersion">The binding identity every platform assembly carries.</param>
    /// <param name="Projects">Every project's outcome, in id order.</param>
    /// <param name="Wall">Total wall time.</param>
    /// <param name="Activity">The activity log every diagnostic was streamed into.</param>
    /// <param name="FatalError">Set when the run could not be attempted at all.</param>
    public sealed record Report(
        string AppDirectory,
        string PlatformAssemblyVersion,
        ImmutableArray<Cascade.NodeResult<ProjectResult>> Projects,
        TimeSpan Wall,
        ActivityLog Activity,
        string? FatalError = null)
    {
        /// <summary>0 green, 1 any red or blocked, 70 fatal.</summary>
        public int ExitCode =>
            FatalError is not null ? 70
            : Projects.IsEmpty ? 70
            : Projects.All(p => p.IsGreen) ? 0
            : 1;
    }

    /// <summary>
    /// Runs the build and emits the report exactly once.
    /// </summary>
    /// <param name="options">What to build and how.</param>
    /// <returns>A cold stream of the single report.</returns>
    public static IObservable<Report> Run(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Observable.Defer(() => RunCore(options))
            .Catch((Exception ex) => Observable.Return(new Report(
                options.AppDirectory, "unknown", [], TimeSpan.Zero,
                new ActivityLog(ActivityCategory.Compilation).Fail($"{ex.GetType().Name}: {ex.Message}"),
                $"{ex.GetType().Name}: {ex.Message}")));
    }

    private static IObservable<Report> RunCore(Options options)
    {
        var wall = Stopwatch.StartNew();
        var sink = new Sink(options);

        string entry;
        try
        {
            entry = ResolveEntryProject(options.EntryProject);
        }
        catch (Exception ex)
        {
            return Observable.Return(Fatal(options, sink, wall, "unknown", ex.Message));
        }

        ContainerReferenceSet container;
        try
        {
            container = ContainerReferenceSet.Read(options.AppDirectory, options.TrustedPlatformAssemblies);
        }
        catch (ContainerReferenceSet.UnreadableContainerException ex)
        {
            return Observable.Return(Fatal(options, sink, wall, "unknown", ex.Message));
        }

        sink.Info(
            $"reference set: {container.AssembliesByName.Count} assembl(y|ies) from {container.AppDirectory} "
            + $"+ the shared framework; {container.PackageVersions.Count} package version(s) from the image's "
            + $"deps.json; platform AssemblyVersion {container.PlatformAssemblyVersion}");

        Graph graph;
        try
        {
            graph = Graph.Discover(entry, container, options, sink);
        }
        catch (Exception ex) when (ex is ProjectFile.UnsupportedConstructException or InvalidOperationException)
        {
            return Observable.Return(Fatal(options, sink, wall, container.PlatformAssemblyVersion, ex.Message));
        }

        if (graph.Order.IsEmpty)
            return Observable.Return(Fatal(options, sink, wall, container.PlatformAssemblyVersion,
                "nothing to build. A run that compiles nothing is a failure, never a silent success."));

        var workRoot = options.OutputDirectory is { Length: > 0 } outDir
            ? Path.GetFullPath(outDir)
            : Path.Combine(Path.GetTempPath(), $"mw-build-project-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        sink.Info(
            $"building {graph.Order.Length} project(s) — "
            + string.Join(", ", graph.Order.Select(Path.GetFileNameWithoutExtension))
            + $"; output {workRoot}; warnings {(options.AllowWarnings ? "allowed" : "fail the build")}");

        var extraReferences = ResolveExtraReferences(options, sink);
        var generators = ResolveGenerators(options, sink);

        return Cascade.Run<ProjectResult>(
                graph.Order,
                id => graph.DependenciesOf(id),
                (id, deps) =>
                {
                    var result = Compile(
                        graph.Models[id], graph, container, extraReferences, generators, options, sink, workRoot,
                        deps.Where(d => d.Result is not null).Select(d => d.Result!).ToArray());
                    return (result, result.IsGreen);
                },
                options.MaxParallel)
            .Select(results =>
            {
                var report = new Report(
                    container.AppDirectory, container.PlatformAssemblyVersion, results, wall.Elapsed,
                    sink.Seal(results.All(r => r.IsGreen)));
                Print(options.Output, report);
                return report;
            })
            // A cycle is thrown by the cascade BEFORE any work runs, naming the cycle. Turn it into
            // the same fatal report every other refusal produces rather than a stack trace.
            .Catch((InvalidOperationException ex) => Observable.Return(
                Fatal(options, sink, wall, container.PlatformAssemblyVersion, ex.Message)));
    }

    private static Report Fatal(Options options, Sink sink, Stopwatch wall, string platformVersion, string message)
    {
        sink.Error(message);
        options.Output.WriteLine($"build-project: FATAL — {message}");
        return new Report(options.AppDirectory, platformVersion, [], wall.Elapsed, sink.Seal(false), message);
    }

    /// <summary>Resolves a directory to the single <c>.csproj</c> it holds, or takes the file given.</summary>
    internal static string ResolveEntryProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            return full;
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"'{full}' is neither a project file nor a directory.");
        var projects = Directory.GetFiles(full, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new InvalidOperationException($"'{full}' holds no .csproj."),
            _ => throw new InvalidOperationException(
                $"'{full}' holds {projects.Length} .csproj files ({string.Join(", ", projects.Select(Path.GetFileName))}) "
                + "— name the one to build rather than letting this builder pick."),
        };
    }

    private static ImmutableArray<string> ResolveExtraReferences(Options options, Sink sink)
    {
        var files = ImmutableArray.CreateBuilder<string>();
        foreach (var dir in options.ExtraReferenceDirectories)
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
                throw new InvalidOperationException(
                    $"--extra-refs '{full}' does not exist. An additional-library directory that is not "
                    + "there would silently supply nothing.");
            foreach (var dll in Directory.GetFiles(full, "*.dll"))
                files.Add(dll);
            sink.Info($"additional libraries: {Directory.GetFiles(full, "*.dll").Length} from {full}");
        }
        return files.ToImmutable();
    }

    /// <summary>
    /// Expands <c>--generators</c> into assembly paths. A directory contributes its <c>*.dll</c>;
    /// a path that does not exist is a failure, because a generator directory that is not there
    /// would silently run no generator and produce a different assembly.
    /// </summary>
    private static ImmutableArray<string> ResolveGenerators(Options options, Sink sink)
    {
        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in options.GeneratorPaths)
        {
            var full = Path.GetFullPath(entry);
            if (Directory.Exists(full))
                paths.AddRange(Directory.GetFiles(full, "*.dll"));
            else if (File.Exists(full))
                paths.Add(full);
            else
                throw new InvalidOperationException(
                    $"--generators '{full}' does not exist. A generator path that is not there runs "
                    + "no generator and silently produces a different assembly.");
        }
        if (paths.Count > 0)
            sink.Info($"source generators: {paths.Count} candidate assembl(y|ies)");
        return paths.ToImmutable();
    }

    // ── the project graph ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The set of projects this run compiles from SOURCE, and the edges between them.
    ///
    /// <para>The rule for a <c>ProjectReference</c> is one decision: <b>a project under the source
    /// root is ours to build; anything else is the container's to supply.</b> The source root is
    /// the directory of the nearest <c>Directory.Build.props</c> above the entry project — the same
    /// boundary MSBuild uses to decide which repository a project belongs to — so a reference into
    /// a sibling module builds, and a reference into the platform's own <c>src/</c> (which exists in
    /// the image as an assembly and not on disk at all) resolves to that assembly.</para>
    /// </summary>
    private sealed record Graph(
        ImmutableArray<string> Order,
        ImmutableDictionary<string, ProjectFile.Model> Models,
        ImmutableDictionary<string, ImmutableArray<string>> Edges,
        ImmutableHashSet<string> ShadowedAssemblyNames)
    {
        internal IReadOnlyList<string> DependenciesOf(string id) =>
            Edges.TryGetValue(id, out var deps) ? deps : [];

        internal static Graph Discover(
            string entry, ContainerReferenceSet container, Options options, Sink sink)
        {
            var sourceRoot = ProjectFile.FindNearest(Path.GetDirectoryName(entry)!, "Directory.Build.props") is { } props
                ? Path.GetDirectoryName(props)!
                : Path.GetDirectoryName(entry)!;
            sink.Info($"source root: {sourceRoot} (a ProjectReference inside it is built; outside it comes from the container)");

            var models = ImmutableDictionary.CreateBuilder<string, ProjectFile.Model>(StringComparer.OrdinalIgnoreCase);
            var edges = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(entry);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (models.ContainsKey(current)) continue;
                var model = ProjectFile.Load(current, options.Accept);
                models[current] = model;
                foreach (var target in model.UnexecutedTargets)
                    sink.Warn($"{Path.GetFileName(current)}: target not executed — {target}");

                var dependencies = ImmutableArray.CreateBuilder<string>();
                foreach (var reference in model.ProjectReferences)
                {
                    var inSourceTree = File.Exists(reference)
                        && reference.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (inSourceTree)
                    {
                        dependencies.Add(reference);
                        pending.Push(reference);
                        continue;
                    }
                    var assemblyName = Path.GetFileNameWithoutExtension(reference);
                    if (container.FindAssembly(assemblyName) is not null)
                    {
                        sink.Info(
                            $"{Path.GetFileNameWithoutExtension(current)}: ProjectReference {assemblyName} "
                            + "→ the container's assembly");
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"{current}: ProjectReference '{reference}' is outside the source root and the "
                        + $"container carries no '{assemblyName}.dll'. This builder resolves a reference "
                        + "from the source tree or from the image, and there is no third place — supply it "
                        + "with --extra-refs, or build against an image that has it.");
                }
                edges[current] = dependencies.ToImmutable();
            }

            var shadowed = models.Values.Select(m => m.AssemblyName).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            return new Graph(
                [.. models.Keys.OrderBy(k => k, StringComparer.Ordinal)],
                models.ToImmutable(), edges.ToImmutable(), shadowed);
        }
    }

    // ── the compile ────────────────────────────────────────────────────────────────────────────

    private static ProjectResult Compile(
        ProjectFile.Model model,
        Graph graph,
        ContainerReferenceSet container,
        ImmutableArray<string> extraReferences,
        ImmutableArray<string> generators,
        Options options,
        Sink sink,
        string workRoot,
        IReadOnlyList<ProjectResult> dependencies)
    {
        var clock = Stopwatch.StartNew();
        var name = Path.GetFileNameWithoutExtension(model.ProjectPath);
        sink.Info($"[{name}] start — {model.CompileItems.Length} source file(s), "
                  + $"target {(model.TargetFramework.Length > 0 ? model.TargetFramework : "(unset)")}, "
                  + $"nullable {model.NullableOptions}, "
                  + $"warnings-as-errors {(model.TreatWarningsAsErrors ? "on" : "off")}");

        // 🚨 Say the NAMES, not the count. A manifest resource is asked for BY NAME in some other
        // process, months later, and a wrong name is a null stream rather than an error — so the
        // names this build committed to belong in the log, where a reader can compare them against
        // what the code asks for. Capped: MeshWeaver.Documentation embeds hundreds.
        if (!model.EmbeddedResources.IsEmpty)
        {
            sink.Info($"[{name}] embedded resources: {model.EmbeddedResources.Length}");
            foreach (var resource in model.EmbeddedResources.Take(MaxListedResources))
                sink.Info($"[{name}]   {resource.ManifestName}  ({resource.Origin})");
            if (model.EmbeddedResources.Length > MaxListedResources)
                sink.Info($"[{name}]   … and {model.EmbeddedResources.Length - MaxListedResources} more");
        }
        // A resource the operator ACCEPTED away is still missing from the assembly, so it is a
        // WARNING every time — never a footnote on the accept token.
        foreach (var skipped in model.SkippedResources)
            sink.Warn($"[{name}] resource NOT embedded — {skipped}");

        // Packages: the container is authoritative for what the container HAS. Anything it does not
        // have is an ADDITIONAL library, and this mode cannot invent one.
        var unresolved = ImmutableArray.CreateBuilder<string>();
        var extraByName = extraReferences.ToDictionary(
            p => Path.GetFileNameWithoutExtension(p)!, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var package in model.PackageReferences)
        {
            var resolution = container.Resolve(package.Id);
            if (resolution.Supplied)
            {
                sink.Info($"[{name}] package {package.Id} → the container "
                          + $"({resolution.Version ?? package.Version ?? "version unknown"})");
                continue;
            }
            if (extraByName.ContainsKey(package.Id))
            {
                sink.Info($"[{name}] package {package.Id} → --extra-refs");
                continue;
            }
            unresolved.Add(package.Id);
        }
        if (unresolved.Count > 0)
        {
            var message =
                $"{unresolved.Count} PackageReference(s) the container does not supply: "
                + string.Join(", ", unresolved)
                + ". This mode resolves references from the image and from --extra-refs only — there is "
                + "no NuGet here. Only libraries ADDITIONAL to the platform have to be specified, and "
                + "these are they: pass --extra-refs <dir> holding their assemblies, or build against an "
                + "image that carries them.";
            sink.Error($"[{name}] {message}");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 0, 0, null, unresolved.ToImmutable(), message);
        }

        // The reference set: the whole container, MINUS every assembly this run builds from source
        // (its own included) — two definitions of one type is the CS0433 family, and the local build
        // is the one under test. Plus the dependency projects' fresh outputs and any additional
        // libraries.
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var (assemblyName, path) in container.AssembliesByName)
        {
            if (graph.ShadowedAssemblyNames.Contains(assemblyName)) continue;
            if (extraByName.ContainsKey(assemblyName)) continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }
        foreach (var extra in extraReferences)
            references.Add(MetadataReference.CreateFromFile(extra));
        foreach (var dependency in dependencies)
            if (dependency.AssemblyPath is { Length: > 0 } dll)
                references.Add(MetadataReference.CreateFromFile(dll));

        var parseOptions = new CSharpParseOptions(
                model.LanguageVersion,
                // 🚨 ALWAYS Diagnose, whatever GenerateDocumentationFile says. CS1574 (unresolvable
                // cref), CS0419 (ambiguous cref) and CS1570 (malformed doc XML) are real defects,
                // and a builder that cannot see them is not reproducing the SDK's build.
                DocumentationMode.Diagnose)
            .WithPreprocessorSymbols(model.DefineConstants
                .Concat(ProjectFile.FrameworkSymbols(model.TargetFramework))
                .Distinct(StringComparer.Ordinal));

        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(model.CompileItems.Length + 1);
        if (!model.GlobalUsings.IsEmpty)
            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(
                    string.Join('\n', model.GlobalUsings.Select(u => $"global using global::{u};")) + "\n",
                    Encoding.UTF8),
                parseOptions, path: GlobalUsingsPath));
        foreach (var file in model.CompileItems)
        {
            try
            {
                using var stream = File.OpenRead(file);
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(stream, Encoding.UTF8), parseOptions, path: file));
            }
            catch (IOException ex)
            {
                var message = $"could not read source file '{file}' — {ex.Message}";
                sink.Error($"[{name}] {message}");
                return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                    model.CompileItems.Length, 1, 0, null, [], message);
            }
        }

        var specific = ImmutableDictionary.CreateBuilder<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);
        // 🚨 DOC-QUALITY vs DOC-COMPLETENESS, and the split is the whole reason DocumentationMode is
        // pinned to Diagnose above. CS1574 (unresolved cref), CS0419 (ambiguous cref) and CS1570
        // (malformed XML) are real defects and must surface whatever the project asked for. The
        // COMPLETENESS family — CS1591 missing member doc, CS1573 missing param, CS1712 missing
        // typeparam — is emitted by csc only when /doc is requested, so a project with
        // GenerateDocumentationFile=false would see warnings the SDK never produces. Suppressed
        // FIRST, so a project that names one of them itself still wins.
        if (!model.GenerateDocumentationFile)
            foreach (var id in DocCompletenessDiagnostics)
                specific[id] = ReportDiagnostic.Suppress;
        foreach (var id in model.NoWarn)
            specific[id] = ReportDiagnostic.Suppress;
        foreach (var id in model.WarningsNotAsErrors)
            specific[id] = ReportDiagnostic.Warn;
        foreach (var id in model.WarningsAsErrors)
            specific[id] = ReportDiagnostic.Error;

        // 🚨 OUR OWN options object. EmitPipeline.CreateCompilationOptions feeds
        // GeneratedInputIdentity.OptionsFingerprint — the key every cached NodeType assembly is
        // filed under — so it must not acquire a project's settings.
        var compilationOptions = new CSharpCompilationOptions(model.OutputKind)
            .WithNullableContextOptions(model.NullableOptions)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithPlatform(Platform.AnyCpu)
            .WithAllowUnsafe(model.AllowUnsafe)
            .WithDeterministic(true)
            .WithConcurrentBuild(true)
            .WithGeneralDiagnosticOption(model.TreatWarningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default)
            .WithSpecificDiagnosticOptions(specific.ToImmutable());

        var compilation = CSharpCompilation.Create(
            model.AssemblyName, trees, references, compilationOptions);
        if (!generators.IsEmpty)
            compilation = GeneratorPipeline.RunSourceGenerators(
                compilation, generators, sink.Logger, CancellationToken.None);

        var errors = 0;
        var warnings = 0;
        var missingGeneratorPartials = 0;
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    if (diagnostic.Id is "CS8795" or "CS8785" or "CS9248")
                        missingGeneratorPartials++;
                    sink.Error($"[{name}] {Render(diagnostic)}");
                    break;
                case DiagnosticSeverity.Warning:
                    warnings++;
                    sink.Warn($"[{name}] {Render(diagnostic)}");
                    break;
                default:
                    break;
            }
        }

        if (errors > 0)
        {
            if (generators.IsEmpty && missingGeneratorPartials > 0)
                // 🚨 Name the CAUSE, not the symptom. A project using [GeneratedRegex],
                // [LoggerMessage] or [JsonSerializable] produces a WALL of CS8795 "partial method
                // must have an implementation part" when its generator did not run — which reads
                // like broken source and is not. The generator lives in the .NET SDK; a MeshWeaver
                // image ships the RUNTIME, so it is genuinely absent here.
                sink.Error(
                    $"[{name}] {missingGeneratorPartials} of those errors are unimplemented partial "
                    + "members — the signature of a SOURCE GENERATOR that did not run. The SDK's "
                    + "built-in generators (GeneratedRegex, LoggerMessage, JsonSerializable) ship in "
                    + "the .NET SDK, not in a runtime image, so this container has none. Supply them "
                    + "with --generators <dir>, or the project cannot be built in this mode.");
            sink.Error($"[{name}] RED — {errors} error(s), {warnings} warning(s) in {clock.Elapsed.TotalMilliseconds:F0} ms");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, errors, warnings, null, [],
                $"{errors} compile error(s)");
        }
        if (warnings > 0 && !options.AllowWarnings)
        {
            sink.Error(
                $"[{name}] RED — {warnings} warning(s), and the no-warn policy is on. Fix them, add them "
                + "to the project's NoWarn, or pass --allow-warnings to build anyway.");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 0, warnings, null, [],
                $"{warnings} warning(s) under the no-warn policy");
        }

        var outputDirectory = Path.Combine(workRoot, model.AssemblyName);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            // The platform's own verified emit: emit to memory, write, and prove the file on disk IS
            // the image that was emitted (EmittedArtifact). Nothing here re-implements it.
            var artifact = EmitPipeline.EmitCompilationToDirectory(
                compilation, model.AssemblyName, model.ProjectPath, outputDirectory,
                ManifestResources(model), CancellationToken.None);
            if (!artifact.MatchesFileOnDisk(out var reason))
            {
                sink.Error($"[{name}] RED — the emitted assembly did not survive the write: {reason}");
                return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                    model.CompileItems.Length, 0, warnings, null, [], reason);
            }
            NameTheDocumentationFile(outputDirectory, model);
            sink.Info(
                $"[{name}] OK — {model.CompileItems.Length} source file(s), "
                + (model.EmbeddedResources.IsEmpty ? "" : $"{model.EmbeddedResources.Length} resource(s), ")
                + $"{warnings} warning(s), "
                + $"{artifact.Length} bytes in {clock.Elapsed.TotalMilliseconds:F0} ms → {artifact.DllPath}");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 0, warnings, artifact.DllPath, [], null)
            {
                ResourceCount = model.EmbeddedResources.Length,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sink.Error($"[{name}] RED — emit failed: {ex.Message}");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 1, warnings, null, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The project's <c>&lt;EmbeddedResource&gt;</c> items as Roslyn resource descriptions.
    ///
    /// <para>🚨 <c>isPublic: true</c>, and it is not a preference. Every resource the real SDK
    /// emits carries <c>ManifestResourceAttributes.Public</c> — measured by reading the
    /// <c>ManifestResource</c> table out of a probe assembly the SDK built. A private resource is
    /// invisible to <c>Assembly.GetManifestResourceStream</c> from outside the assembly, which is
    /// the same silent null the manifest NAME rules exist to prevent.</para>
    ///
    /// <para>The stream is opened lazily, by Roslyn, during the emit — so a file that vanishes
    /// between evaluation and emit fails the emit rather than embedding zero bytes.</para>
    /// </summary>
    /// <param name="model">The evaluated project.</param>
    /// <returns>One description per resource, in declaration order.</returns>
    private static ImmutableArray<ResourceDescription> ManifestResources(ProjectFile.Model model) =>
    [
        .. model.EmbeddedResources.Select(resource => new ResourceDescription(
            resource.ManifestName, () => File.OpenRead(resource.Path), isPublic: true)),
    ];

    /// <summary>How many resource names a build lists before it counts the rest instead.</summary>
    internal const int MaxListedResources = 25;

    /// <summary>Sentinel path for the synthesized implicit-usings document.</summary>
    internal const string GlobalUsingsPath = "__implicit_usings__.cs";

    /// <summary>
    /// The doc-COMPLETENESS warning family — the one csc raises only when <c>/doc</c> is on. Kept
    /// as a named list beside the compile so the distinction from doc-QUALITY is legible rather
    /// than three magic ids in a condition.
    /// </summary>
    internal static readonly ImmutableArray<string> DocCompletenessDiagnostics =
        ["CS1591", "CS1573", "CS1712"];

    /// <summary>
    /// <see cref="EmitPipeline"/>'s emit names the XML doc for a dynamic NodeType
    /// (<c>DynamicNode_&lt;name&gt;.xml</c>). A project's doc file is <c>&lt;AssemblyName&gt;.xml</c>
    /// beside the DLL, and a project that did not ask for one gets none.
    /// </summary>
    private static void NameTheDocumentationFile(string outputDirectory, ProjectFile.Model model)
    {
        var emitted = Path.Combine(outputDirectory, $"DynamicNode_{model.AssemblyName}.xml");
        if (!File.Exists(emitted))
            return;
        if (!model.GenerateDocumentationFile)
        {
            File.Delete(emitted);
            return;
        }
        var target = Path.Combine(outputDirectory, $"{model.AssemblyName}.xml");
        File.Move(emitted, target, overwrite: true);
    }

    private static string Render(Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        if (!location.IsInSource)
            return $"{diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}";
        var span = location.GetLineSpan();
        var file = span.Path == GlobalUsingsPath ? "(implicit usings)" : span.Path;
        return $"{file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): "
               + $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: {diagnostic.GetMessage()}";
    }

    private static void Print(TextWriter output, Report report)
    {
        output.WriteLine();
        output.WriteLine($"build-project — {report.Projects.Length} project(s) in {report.Wall.TotalSeconds:F1}s "
                         + $"against {report.AppDirectory} (platform {report.PlatformAssemblyVersion})");
        foreach (var project in report.Projects)
        {
            var result = project.Result;
            var verdict = project.Outcome switch
            {
                Cascade.NodeOutcome.Green => "ok  ",
                Cascade.NodeOutcome.Blocked => "skip",
                _ => "FAIL",
            };
            output.WriteLine(
                $"  {verdict} {Path.GetFileNameWithoutExtension(project.Id)}"
                + (result is null
                    ? $" — {(project.BlockedBy is { } b ? $"blocked by {Path.GetFileNameWithoutExtension(b)}" : project.Error)}"
                    : $" — {result.SourceCount} source(s), {result.Errors} error(s), {result.Warnings} warning(s), "
                      + $"{result.Elapsed.TotalMilliseconds:F0} ms"
                      + (result.Failure is { } f ? $" — {f}" : "")));
        }
        output.WriteLine(report.ExitCode == 0 ? "build-project: GREEN" : "build-project: RED");
    }

    // ── the streaming sink ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every line this build produces, in the order it was produced: appended to an
    /// <see cref="ActivityLog"/> (the primary record — <see cref="ActivityCategory.Compilation"/>,
    /// <see cref="LogMessage"/>) and pushed to the caller's observer the same instant. The console
    /// writer is a rendering of that stream, which is why nothing is batched to the end.
    /// </summary>
    private sealed class Sink
    {
        private readonly Options _options;
        private readonly Lock _gate = new();
        private ActivityLog _log = new(ActivityCategory.Compilation);

        internal Sink(Options options)
        {
            _options = options;
            Logger = new SinkLogger(this);
        }

        /// <summary>The same stream, as an <see cref="ILogger"/> — what the platform's generator
        /// pipeline logs to, so a generator that fails to load says so in this build's log rather
        /// than nowhere.</summary>
        internal ILogger Logger { get; }

        internal void Info(string text) => Append(text, LogLevel.Information);

        internal void Warn(string text) => Append(text, LogLevel.Warning);

        internal void Error(string text) => Append(text, LogLevel.Error);

        private void Append(string text, LogLevel level)
        {
            var message = new LogMessage(text, level) { CategoryName = nameof(ProjectBuild) };
            lock (_gate)
            {
                _log = _log.Append(message);
            }
            _options.Log?.OnNext(message);
            _options.Output.WriteLine(
                $"{message.Timestamp:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:D3}] "
                + $"{(level == LogLevel.Information ? "" : level.ToString().ToLowerInvariant() + ": ")}{text}");
        }

        /// <summary>
        /// The <see cref="ILogger"/> face of the sink. Everything the platform's generator pipeline
        /// logs lands in the same ordered stream as the diagnostics, which is the point: a
        /// generator that could not be loaded is a build fact, not a debug detail.
        /// </summary>
        private sealed class SinkLogger(Sink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var text = formatter(state, exception);
                if (exception is not null) text += $" — {exception.GetType().Name}: {exception.Message}";
                sink.Append(text, logLevel);
            }
        }

        internal ActivityLog Seal(bool green)
        {
            lock (_gate)
            {
                _log = green
                    ? _log with { Status = ActivityStatus.Succeeded }
                    : _log with { Status = ActivityStatus.Failed };
                _options.Log?.OnCompleted();
                return _log;
            }
        }
    }
}
