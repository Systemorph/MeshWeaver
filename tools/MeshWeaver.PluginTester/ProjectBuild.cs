using System.Collections.Concurrent;
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
/// <para>🔴 <b>The emitted assembly carries the SDK's IDENTITY.</b> <c>GenerateAssemblyInfo</c> is a
/// TARGET, and this builder runs no targets — so before <see cref="ProjectFile.AssemblyInfo"/>
/// existed every assembly it produced was <c>AssemblyVersion=0.0.0.0</c> while the fleet binds
/// <c>3.0.0.0</c>. Nothing went red: the compile was green and the failure would have surfaced in
/// another repo, at runtime, as a missing-file error. The identity is now EVALUATED from the
/// project (<c>AssemblyVersion</c>/<c>FileVersion</c>/<c>Version</c>/<c>InformationalVersion</c>
/// and the descriptive attributes, plus <c>InternalsVisibleTo</c>, <c>AssemblyMetadata</c> and
/// <c>AssemblyAttribute</c> items) and synthesized into the compilation as one more source
/// document, exactly as the SDK's generated <c>&lt;Project&gt;.AssemblyInfo.cs</c> is one more
/// Compile item.</para>
///
/// <para><b>Two things it deliberately does not reproduce, both named rather than hidden.</b>
/// (1) <c>$(SourceRevisionId)</c>: the SDK reads the commit from git and appends <c>+&lt;sha&gt;</c>
/// to <c>InformationalVersion</c>; there is no git here, so the suffix is absent and every build
/// SAYS so — pass <c>-p:SourceRevisionId=&lt;sha&gt;</c> for exact parity.
/// (2) <c>TargetFrameworkAttribute</c>: written by a DIFFERENT target
/// (<c>GenerateTargetFrameworkMonikerAttribute</c>), with its own switches and its own
/// <c>TargetPlatform</c>/<c>SupportedOSPlatform</c> companions for a platform-suffixed TFM that
/// this evaluator cannot compute. Emitting half of that set would be worse than the gap, so the gap
/// is written down here instead. Everything else was compared attribute-for-attribute against a
/// real <c>dotnet build</c> of <c>MeshWeaver.Plugins/src/MeshWeaver.Speech.Contract</c> and
/// matches.</para>
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
        /// Global properties — MSBuild's <c>-p:Name=Value</c>. They win over anything the project
        /// files set unconditionally, and they are how a caller supplies the identity inputs this
        /// builder cannot compute for itself: <c>PlatformVersion</c>, <c>Version</c>, and above all
        /// <c>SourceRevisionId</c> (the SDK gets it from git; there is no git here).
        /// </summary>
        public IReadOnlyDictionary<string, string> Properties { get; init; } =
            ImmutableDictionary<string, string>.Empty;

        /// <summary>
        /// Assemblies (or directories of them) to load Roslyn <c>[Generator]</c> source generators
        /// from. 🚨 A MeshWeaver image ships the RUNTIME, and the SDK's built-in generators
        /// (<c>GeneratedRegex</c>, <c>LoggerMessage</c>, <c>JsonSerializable</c>) live in the SDK, so
        /// a project using one cannot build here unless its generator is supplied. Nothing is
        /// guessed: with no generator the compile reports the SDK error and this builder names the
        /// generator that did not run.
        /// </summary>
        public IReadOnlyList<string> GeneratorPaths { get; init; } = [];

        /// <summary>
        /// Where the RAZOR source generator lives. Null means the standard search —
        /// <c>razor-generators/</c> beside this builder, then under <see cref="AppDirectory"/> —
        /// which is where the image build lays it. Named separately from
        /// <see cref="GeneratorPaths"/> because it is not optional in the same way: a project with
        /// <c>.razor</c> files and no Razor compiler is a FAILURE, never a build that quietly
        /// omits every component.
        /// </summary>
        public string? RazorGeneratorDirectory { get; init; }

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

        /// <summary>
        /// Directories of PREBUILT sibling-module assemblies (<c>--prebuilt</c>). An in-root
        /// <c>ProjectReference</c> whose assembly name is found here resolves to that DLL instead
        /// of being rebuilt from source — the maintainer's "we don't need to rebuild the mesh"
        /// (2026-08-31): a dependent module's job consumed ~3 minutes re-compiling MeshWeaver.AI
        /// that the SAME RUN's floor job had already built, per dependent. Prebuilt assemblies are
        /// references only: they never ride the dependent's bundle (they ship as their OWN
        /// bundles), and the platform binding-identity check covers them like every reference.
        /// </summary>
        public IReadOnlyList<string> PrebuiltDirectories { get; init; } = [];

        /// <summary>
        /// The PLATFORM image's <c>shared/</c> frameworks root — required whenever
        /// <see cref="AppDirectory"/> comes from a different image than the one running this
        /// builder (see <see cref="ContainerReferenceSet.Read"/>). Null = the running runtime's.
        /// </summary>
        public string? SharedFrameworksRoot { get; init; }
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
        /// How many <c>.razor</c>/<c>.cshtml</c> documents the Razor generator produced.
        ///
        /// <para>🚨 An <c>init</c> PROPERTY, not a primary-constructor parameter. Adding a
        /// parameter — even with a default — REPLACES the record's constructor signature, and
        /// <c>scripts/check-record-signatures.py</c> refuses that for exactly the reason it should:
        /// every assembly compiled against the old arity calls a constructor that no longer
        /// exists.</para>
        /// </summary>
        public int RazorCount { get; init; }

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
            container = ContainerReferenceSet.Read(
                options.AppDirectory, options.TrustedPlatformAssemblies, options.SharedFrameworksRoot);
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
        var shelf = ModuleLibrariesShelf.Locate(container.AppDirectory);
        if (shelf is not null)
            sink.Info($"module-libraries shelf: {shelf.PackageCount} package(s) from {shelf.Directory} "
                + "(an additional library resolves here and RIDES the bundle with its deps.json-derived closure)");

        return Cascade.Run<ProjectResult>(
                graph.Order,
                id => graph.DependenciesOf(id),
                (id, deps) =>
                {
                    var result = Compile(
                        graph.Models[id], graph, container, extraReferences, generators, shelf, options, sink, workRoot,
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
    /// The directory, beside the builder (then under the container's <c>/app</c>), that carries the
    /// SDK's BUILT-IN generators (<c>GeneratedRegex</c>, <c>JsonSerializable</c>,
    /// <c>LibraryImport</c>…). They live in the SDK's targeting pack, not the runtime the image
    /// ships, so the image build stages them here (<c>StageSdkSourceGenerators</c> in this
    /// project's <c>.csproj</c>) and every build picks them up without a flag — the same shipping
    /// pattern as <c>razor-generators/</c>, minus the per-RID split (these are plain AnyCPU IL).
    /// </summary>
    public const string SdkGeneratorDirectoryName = "sdk-generators";

    /// <summary>
    /// Expands <c>--generators</c> into assembly paths. A directory contributes its <c>*.dll</c>;
    /// a path that does not exist is a failure, because a generator directory that is not there
    /// would silently run no generator and produce a different assembly.
    ///
    /// <para>The image-shipped <see cref="SdkGeneratorDirectoryName"/> directory is then appended
    /// unconditionally when present — beside the builder first, then under the container's
    /// <c>/app</c>. Absence is NOT an error here: a from-source run on a dev machine has no staged
    /// copy, and a project that uses no SDK generator builds identically either way; a project
    /// that DOES need one still fails by name (the missing-partials diagnostic below names the
    /// generator that never ran), so nothing goes quietly.</para>
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
        var shipped = new[]
            {
                Path.Combine(AppContext.BaseDirectory, SdkGeneratorDirectoryName),
                Path.Combine(options.AppDirectory, SdkGeneratorDirectoryName),
            }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(Directory.Exists);
        if (shipped is not null)
        {
            var dlls = Directory.GetFiles(shipped, "*.dll");
            paths.AddRange(dlls);
            sink.Info($"sdk generators: {dlls.Length} from {shipped} (staged by the image build)");
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
        /// <summary>
        /// Assembly name → prebuilt DLL for in-root ProjectReferences resolved via
        /// <c>--prebuilt</c> — referenced by every compile in the graph, never rebuilt and never
        /// riding a bundle. (An <c>init</c> property, not a positional parameter — the record
        /// signature rule.)
        /// </summary>
        public ImmutableDictionary<string, string> PrebuiltReferences { get; init; } =
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One Roslyn reference universe for the whole run — "one workspace": every project in the
        /// graph shares the SAME <see cref="PortableExecutableReference"/> instance per path, so a
        /// reference assembly is opened and its metadata decoded from the filesystem ONCE, and
        /// Roslyn's metadata-keyed symbol caches carry across the graph instead of every project
        /// re-materializing 200+ assemblies (maintainer, 2026-09-01: "create *one* roslyn
        /// workspace with all projects at beginning … and you read from file system directly").
        /// Run-scoped instance state, deliberately not static.
        /// </summary>
        public ConcurrentDictionary<string, PortableExecutableReference> SharedReferences { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal PortableExecutableReference Reference(string path) =>
            SharedReferences.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p));

        internal IReadOnlyList<string> DependenciesOf(string id) =>
            Edges.TryGetValue(id, out var deps) ? deps : [];

        internal static Graph Discover(
            string entry, ContainerReferenceSet container, Options options, Sink sink)
        {
            var prebuilt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in options.PrebuiltDirectories)
            {
                var full = Path.GetFullPath(directory);
                if (!Directory.Exists(full))
                    throw new InvalidOperationException(
                        $"--prebuilt '{full}' does not exist. A prebuilt directory that is not there "
                        + "silently rebuilds every module it was supposed to supply — the exact "
                        + "wasted work the flag exists to remove, wearing a green log.");
                foreach (var dll in Directory.GetFiles(full, "*.dll"))
                    prebuilt[Path.GetFileNameWithoutExtension(dll)] = dll;
            }
            if (prebuilt.Count > 0)
                sink.Info($"prebuilt siblings: {prebuilt.Count} assembl(y|ies) — an in-root "
                    + "ProjectReference matching one resolves to it instead of rebuilding");
            var sourceRoot = ProjectFile.FindNearest(Path.GetDirectoryName(entry)!, "Directory.Build.props") is { } props
                ? Path.GetDirectoryName(props)!
                : Path.GetDirectoryName(entry)!;
            sink.Info($"source root: {sourceRoot} (a ProjectReference inside it is built; outside it comes from the container)");

            var models = ImmutableDictionary.CreateBuilder<string, ProjectFile.Model>(StringComparer.OrdinalIgnoreCase);
            var edges = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
            var prebuiltReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(entry);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (models.ContainsKey(current)) continue;
                var model = ProjectFile.Load(current, options.Accept, options.Properties);
                models[current] = model;
                foreach (var target in model.UnexecutedTargets)
                    sink.Warn($"{Path.GetFileName(current)}: target not executed — {target}");

                var dependencies = ImmutableArray.CreateBuilder<string>();
                foreach (var reference in model.ProjectReferences)
                {
                    var referenceAssembly = Path.GetFileNameWithoutExtension(reference);
                    if (prebuilt.TryGetValue(referenceAssembly, out var prebuiltDll))
                    {
                        prebuiltReferences[referenceAssembly] = prebuiltDll;
                        sink.Info(
                            $"{Path.GetFileNameWithoutExtension(current)}: ProjectReference {referenceAssembly} "
                            + "→ PREBUILT (this run already built it; not rebuilding the mesh)");
                        continue;
                    }
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

            var shadowed = models.Values.Select(m => m.AssemblyName)
                .Concat(prebuiltReferences.Keys)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            return new Graph(
                [.. models.Keys.OrderBy(k => k, StringComparer.Ordinal)],
                models.ToImmutable(), edges.ToImmutable(), shadowed)
            {
                PrebuiltReferences = prebuiltReferences.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    // ── the compile ────────────────────────────────────────────────────────────────────────────

    private static ProjectResult Compile(
        ProjectFile.Model model,
        Graph graph,
        ContainerReferenceSet container,
        ImmutableArray<string> extraReferences,
        ImmutableArray<string> generators,
        ModuleLibrariesShelf? shelf,
        Options options,
        Sink sink,
        string workRoot,
        IReadOnlyList<ProjectResult> dependencies)
    {
        var clock = Stopwatch.StartNew();
        var name = Path.GetFileNameWithoutExtension(model.ProjectPath);
        var razorGenerated = 0;
        sink.Info($"[{name}] start — {model.CompileItems.Length} source file(s)"
                  + (model.RazorItems.IsEmpty ? "" : $" + {model.RazorItems.Length} Razor file(s)") + ", "
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
        var shelfResolutions = new List<ModuleLibrariesShelf.Resolution>();
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
            if (shelf?.Resolve(package.Id, n => container.FindAssembly(n) is not null)
                is { } fromShelf)
            {
                shelfResolutions.Add(fromShelf);
                sink.Info($"[{name}] package {package.Id} → the module-libraries shelf "
                    + $"({fromShelf.Version}); {fromShelf.RideFiles.Length} file(s) ride the bundle");
                continue;
            }
            unresolved.Add(package.Id);
        }
        if (unresolved.Count > 0)
        {
            var message =
                $"{unresolved.Count} PackageReference(s) the container does not supply: "
                + string.Join(", ", unresolved)
                + ". This mode resolves references from the image, the module-libraries shelf and "
                + "--extra-refs only — there is no NuGet here. Only libraries ADDITIONAL to the platform "
                + "have to be specified, and these are they: add the package to the curated shelf "
                + "(tools/MeshWeaver.ModuleLibraries — its deps.json is the ride closure), pass "
                + "--extra-refs <dir>, or build against an image that carries them.";
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
            references.Add(graph.Reference(path));
        }
        foreach (var extra in extraReferences)
            references.Add(graph.Reference(extra));
        foreach (var prebuiltDll in graph.PrebuiltReferences.Values)
            references.Add(graph.Reference(prebuiltDll));
        // Two shelf packages can share a transitive; each names the full closure, so dedupe by path.
        var shelfReferencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fromShelf in shelfResolutions)
            foreach (var file in fromShelf.ReferenceFiles)
                if (shelfReferencePaths.Add(file))
                    references.Add(graph.Reference(file));
        foreach (var dependency in dependencies)
            if (dependency.AssemblyPath is { Length: > 0 } dll)
                references.Add(graph.Reference(dll));

        var parseOptions = new CSharpParseOptions(
                model.LanguageVersion,
                // 🚨 ALWAYS Diagnose, whatever GenerateDocumentationFile says. CS1574 (unresolvable
                // cref), CS0419 (ambiguous cref) and CS1570 (malformed doc XML) are real defects,
                // and a builder that cannot see them is not reproducing the SDK's build.
                DocumentationMode.Diagnose)
            .WithPreprocessorSymbols(model.DefineConstants
                .Concat(ProjectFile.FrameworkSymbols(model.TargetFramework))
                .Distinct(StringComparer.Ordinal));

        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(model.CompileItems.Length + 2);
        if (!model.GlobalUsings.IsEmpty)
            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(
                    string.Join('\n', model.GlobalUsings.Select(u => $"global using global::{u};")) + "\n",
                    Encoding.UTF8),
                parseOptions, path: GlobalUsingsPath));

        // 🔴 THE ASSEMBLY'S IDENTITY. Without this tree Roslyn emits its own default — 0.0.0.0 —
        // and NOTHING goes red: the compile is green, the DLL loads, and the failure arrives later
        // in a different process as `FileNotFoundException: … Version=3.0.0.0`. Synthesized here
        // rather than in ProjectFile because it is a compile INPUT, exactly as the SDK's generated
        // <Project>.AssemblyInfo.cs is a Compile item.
        var assemblyInfo = model.AssemblyInfo;
        if (assemblyInfo.Generate && !assemblyInfo.Attributes.IsEmpty)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(RenderAssemblyInfo(assemblyInfo.Attributes), Encoding.UTF8),
                parseOptions, path: AssemblyInfoPath));
            sink.Info(
                $"[{name}] assembly identity — AssemblyVersion {assemblyInfo.AssemblyVersion}, "
                + $"FileVersion {assemblyInfo.FileVersion}, "
                + $"InformationalVersion {assemblyInfo.InformationalVersion}"
                + (assemblyInfo.SourceRevisionApplied
                    ? string.Empty
                    : " (no SourceRevisionId: this builder runs no git, so the SDK's +<sha> suffix on "
                      + "InformationalVersion is absent — pass --property SourceRevisionId=<sha> to "
                      + "reproduce it)"));
        }
        else if (!assemblyInfo.Generate)
        {
            // GenerateAssemblyInfo=false means the project supplies its own attributes; synthesizing
            // a second set is CS0579, so this is a deliberate no-op — said out loud, because an
            // assembly with no identity attributes at all is otherwise indistinguishable from this
            // builder having forgotten them.
            sink.Info(
                $"[{name}] GenerateAssemblyInfo=false — no identity attributes synthesized; the "
                + "project's own sources supply them.");
        }
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

        // 🚨 RAZOR FIRST. `.razor` becomes C# through a Roslyn source generator, so a Blazor project
        // whose components have not been generated is not "a project with errors" — it is a project
        // that was never fully read. Running it before any other generator also means an ordinary
        // `--generators` generator sees the component partials, exactly as csc does.
        if (!model.RazorItems.IsEmpty)
        {
            try
            {
                var directory = RazorGenerators.Locate(options.RazorGeneratorDirectory, options.AppDirectory)
                    ?? throw new RazorGenerators.MissingRazorCompilerException(
                        RazorGenerators.MissingCompilerMessage(
                            name, model.RazorItems.Length,
                            RazorGenerators.SearchPath(options.RazorGeneratorDirectory, options.AppDirectory)));
                var razor = RazorGenerators.Load(directory, sink.Logger);
                sink.Info(
                    $"[{name}] Razor: {model.RazorItems.Length} file(s) through "
                    + $"{razor.Generators.Length} generator(s) from {razor.Directory} ({razor.Provenance})");
                var outcome = RazorGenerators.Run(compilation, model, razor, parseOptions, CancellationToken.None);
                compilation = outcome.Compilation;
                razorGenerated = outcome.GeneratedSources;
                foreach (var diagnostic in outcome.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
                    sink.Warn($"[{name}] razor: {Render(diagnostic)}");
                sink.Info($"[{name}] Razor: {razorGenerated} generated document(s)");
            }
            catch (RazorGenerators.MissingRazorCompilerException ex)
            {
                sink.Error($"[{name}] {ex.Message}");
                return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                    model.CompileItems.Length, 1, 0, null, [], ex.Message);
            }
        }

        // Phase timing: the CI receipt for MeshWeaver.AI read "OK … in 558956 ms" with nine silent
        // minutes between the identity line and the verdict — a duration with no phases is a
        // number nobody can act on. Each phase now reports itself.
        var phase = System.Diagnostics.Stopwatch.StartNew();
        if (!generators.IsEmpty)
        {
            compilation = GeneratorPipeline.RunSourceGenerators(
                compilation, generators, sink.Logger, CancellationToken.None);
            sink.Info($"[{name}] phase: source generators {phase.ElapsedMilliseconds} ms");
        }

        phase.Restart();
        var errors = 0;
        var warnings = 0;
        var missingGeneratorPartials = 0;
        var missingComponentOverrides = 0;
        // 🚨 ONE body pass, like csc. GetDiagnostics() binds and flow-analyzes every method body,
        // and the Emit below then lowers them ALL AGAIN — measured on MeshWeaver.AI (168
        // nullable-heavy files): 82s analysis + 79s emit locally, 559s inside the CI container,
        // for work csc does once. So the upfront pass reads only parse + declaration diagnostics
        // (imports, signatures, partials — including the CS8795/CS0115 generator signatures
        // classified below); body diagnostics surface from the emit itself, whose EmitResult
        // carries every one.
        foreach (var diagnostic in compilation.GetParseDiagnostics()
                     .Concat(compilation.GetDeclarationDiagnostics()))
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    if (diagnostic.Id is "CS8795" or "CS8785" or "CS9248")
                        missingGeneratorPartials++;
                    // CS0115 "no suitable method found to override" is what a Razor component looks
                    // like when its generated half is absent: the .razor.cs declares
                    // BuildRenderTree, the generator never produced the partial that has it.
                    if (diagnostic.Id is "CS0115")
                        missingComponentOverrides++;
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

        sink.Info($"[{name}] phase: declaration analysis {phase.ElapsedMilliseconds} ms");
        phase.Restart();

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
            if (model.RazorItems.IsEmpty && missingComponentOverrides > 0)
                // 🚨 The other half of the same rule. CS0115 in a project this builder found NO
                // Razor items in means the components were never offered to the generator at all —
                // the item glob, not the compile, is what went wrong. Say which.
                sink.Error(
                    $"[{name}] {missingComponentOverrides} of those errors are overrides with nothing "
                    + "to override — the signature of Razor components whose generated partial is "
                    + "missing. This build found NO .razor/.cshtml items in the project, so the "
                    + "generator was never asked to produce them: check the project's Sdk (Razor "
                    + "items are compiled only under Microsoft.NET.Sdk.Razor/.Web) and its Content "
                    + "items, rather than reading these as broken source.");
            sink.Error($"[{name}] RED — {errors} error(s), {warnings} warning(s) in {clock.Elapsed.TotalMilliseconds:F0} ms");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, errors, warnings, null, [],
                $"{errors} compile error(s)") { RazorCount = razorGenerated };
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
            sink.Info($"[{name}] phase: emit {phase.ElapsedMilliseconds} ms");
            if (!artifact.MatchesFileOnDisk(out var reason))
            {
                sink.Error($"[{name}] RED — the emitted assembly did not survive the write: {reason}");
                return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                    model.CompileItems.Length, 0, warnings, null, [], reason);
            }
            // Body-level warnings arrive from the emit (the single body pass); under
            // warnings-as-errors they were already escalated inside the Emit and threw. This is
            // the same no-warn policy gate as the declaration pass above, applied to the rest.
            foreach (var bodyWarning in artifact.Warnings)
                sink.Warn($"[{name}] {bodyWarning}");
            warnings += artifact.Warnings.Count;
            if (artifact.Warnings.Count > 0 && !options.AllowWarnings)
            {
                sink.Error(
                    $"[{name}] RED — {warnings} warning(s), and the no-warn policy is on. Fix them, add them "
                    + "to the project's NoWarn, or pass --allow-warnings to build anyway.");
                return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                    model.CompileItems.Length, 0, warnings, null, [],
                    $"{warnings} warning(s) under the no-warn policy");
            }
            NameTheDocumentationFile(outputDirectory, model);
            EmitShelfRides(outputDirectory, model, shelfResolutions, sink, name);
            var scopedCssCount = EmitStaticAssets(outputDirectory, model, sink, name);
            sink.Info(
                $"[{name}] OK — {model.CompileItems.Length} source file(s)"
                + (razorGenerated == 0 ? "" : $" + {model.RazorItems.Length} Razor file(s)")
                + (model.EmbeddedResources.IsEmpty ? "" : $" + {model.EmbeddedResources.Length} resource(s)")
                + (scopedCssCount == 0 ? "" : $" + {scopedCssCount} scoped stylesheet(s)")
                + $", {warnings} warning(s), "
                + $"{artifact.Length} bytes in {clock.Elapsed.TotalMilliseconds:F0} ms → {artifact.DllPath}");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 0, warnings, artifact.DllPath, [], null)
            {
                RazorCount = razorGenerated,
                ResourceCount = model.EmbeddedResources.Length,
            };
        }
        catch (CompilationException ex)
        {
            // Body-level compile errors surface HERE now (the emit is the single body pass); the
            // message already carries every formatted diagnostic.
            sink.Error($"[{name}] RED — {ex.Message}");
            return new ProjectResult(model.ProjectPath, model.AssemblyName, clock.Elapsed,
                model.CompileItems.Length, 1, warnings, null, [], ex.Message);
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

    /// <summary>
    /// The static-asset half of the module: the project's own <c>wwwroot/**</c> copied verbatim,
    /// plus the CSS-isolation aggregate <c>wwwroot/&lt;AssemblyName&gt;.styles.css</c> — each
    /// <c>*.razor.css</c> rewritten under the SAME scope the generator stamped into the markup
    /// (<see cref="ScopedCss"/>), concatenated in item order. The packer sweeps <c>wwwroot/</c>
    /// into the bundle's <c>staticAssets</c>, and the portal's module-asset host links the
    /// aggregate at runtime — without this a converted pack lands, loads, and renders UNSTYLED
    /// with nothing in any log (#2221's signature).
    /// </summary>
    /// <returns>How many scoped stylesheets went into the aggregate.</returns>
    private static int EmitStaticAssets(
        string outputDirectory, ProjectFile.Model model, Sink sink, string name)
    {
        var projectDirectory = Path.GetDirectoryName(model.ProjectPath)!;
        var wwwroot = Path.Combine(projectDirectory, "wwwroot");
        var target = Path.Combine(outputDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            foreach (var file in Directory.GetFiles(wwwroot, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(wwwroot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            sink.Info($"[{name}] static assets: wwwroot/ copied "
                + $"({Directory.GetFiles(wwwroot, "*", SearchOption.AllDirectories).Length} file(s))");
        }

        var scoped = model.RazorItems
            .Where(item => item.CssScope is { Length: > 0 })
            .Select(item => (
                RelativePath: item.TargetPath + ".css",
                Rewritten: ScopedCss.Rewrite(File.ReadAllText(item.Path + ".css"), item.CssScope!)))
            .ToImmutableArray();
        if (scoped.IsEmpty)
            return 0;
        Directory.CreateDirectory(target);
        var aggregatePath = Path.Combine(target, model.AssemblyName + ".styles.css");
        File.WriteAllText(aggregatePath, ScopedCss.Aggregate(scoped.Select(s => (s.RelativePath, s.Rewritten))));
        sink.Info($"[{name}] css isolation: {scoped.Length} scoped stylesheet(s) → "
            + $"wwwroot/{model.AssemblyName}.styles.css (scopes match the generated markup)");
        return scoped.Length;
    }

    /// <summary>
    /// The file, beside the built module, that names every shelf-supplied assembly riding the
    /// bundle. 🚨 It is the PROVENANCE the lane's inspection keys on: a container bundle may carry
    /// a non-MeshWeaver assembly ONLY when this manifest names it — anything else still means the
    /// closure cannot be accounted for and the pack must fail.
    /// </summary>
    public const string ShelfManifestName = "module-libs.txt";

    /// <summary>
    /// Copies every shelf ride beside the module and writes <see cref="ShelfManifestName"/>. The
    /// rides were derived from the shelf's deps.json minus everything the landing image supplies
    /// (<see cref="ModuleLibrariesShelf.Resolve"/>), so the bundle's closure stays complete BY
    /// RECORD — the property the lane's no-extra-refs stance protects.
    /// </summary>
    private static void EmitShelfRides(
        string outputDirectory, ProjectFile.Model model,
        List<ModuleLibrariesShelf.Resolution> shelfResolutions, Sink sink, string name)
    {
        if (shelfResolutions.Count == 0)
            return;
        var manifest = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolution in shelfResolutions)
            foreach (var file in resolution.RideFiles)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals(model.AssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(outputDirectory, fileName), overwrite: true);
                manifest.Add(fileName);
            }
        File.WriteAllLines(Path.Combine(outputDirectory, ShelfManifestName), manifest);
        sink.Info($"[{name}] shelf rides: {manifest.Count} assembl(y|ies) beside the module "
            + $"({ShelfManifestName} is the provenance the pack inspection keys on)");
    }

    /// <summary>How many resource names a build lists before it counts the rest instead.</summary>
    internal const int MaxListedResources = 25;

    /// <summary>Sentinel path for the synthesized implicit-usings document.</summary>
    internal const string GlobalUsingsPath = "__implicit_usings__.cs";

    /// <summary>Sentinel path for the synthesized assembly-info document.</summary>
    internal const string AssemblyInfoPath = "__assembly_info__.cs";

    /// <summary>
    /// The generated <c>&lt;Project&gt;.AssemblyInfo.cs</c>, as the SDK's <c>WriteCodeFragment</c>
    /// task writes it: one fully-qualified <c>[assembly: …]</c> per attribute, arguments as C#
    /// string literals.
    /// </summary>
    /// <param name="attributes">What <c>GetAssemblyAttributes</c> collected.</param>
    /// <returns>The source text.</returns>
    internal static string RenderAssemblyInfo(ImmutableArray<ProjectFile.AssemblyAttributeSpec> attributes)
    {
        var text = new StringBuilder("// <auto-generated/> — the SDK's GenerateAssemblyInfo, without MSBuild.\n");
        foreach (var attribute in attributes)
        {
            text.Append("[assembly: global::").Append(attribute.TypeName).Append('(');
            // 🚨 FormatLiteral, never string concatenation. A Description carrying a quote, a
            // backslash or a newline — and these repos' Description properties carry all three —
            // would otherwise produce source that does not parse, or worse, parses differently.
            text.AppendJoin(", ", attribute.Arguments.Select(a => SymbolDisplay.FormatLiteral(a, quote: true)));
            text.Append(")]\n");
        }
        return text.ToString();
    }

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
        var file = span.Path switch
        {
            GlobalUsingsPath => "(implicit usings)",
            AssemblyInfoPath => "(generated assembly info)",
            _ => span.Path,
        };
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
