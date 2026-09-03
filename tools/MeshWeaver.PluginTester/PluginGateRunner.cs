using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.PluginTester;

/// <summary>Configuration for one gate run.</summary>
public sealed record GateOptions
{
    /// <summary>The node-repo checkout root (the plugins repo working tree).</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Progress + summary sink (default: <see cref="Console.Out"/>).</summary>
    public TextWriter Output { get; init; } = Console.Out;

    /// <summary>Budget for one NodeType to reach a terminal compile status.</summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Budget for one layout-area render / Tests execution.</summary>
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Budget for one package fetch or install pass (each of the two installs — the gate proper
    /// and the idempotence re-install — gets its own). These phases were the LAST unbounded waits
    /// in the gate: compile, render and Tests are all time-bounded with a named verdict, but an
    /// install whose node write is stored and never announced (the Systemorph/MeshWeaver#817
    /// class — the row lands, no hub wakes, the observable never emits) threw nothing, so the
    /// stage-labelling <c>Catch</c> never fired and the whole gate went SILENT mid-package — a
    /// bake that runs for hours producing no further output, killed only by the CI runner. A
    /// timeout is an ANSWER here, not a cure: the run fails loudly, named
    /// <c>[install] TimeoutException</c> by the stage marker, instead of hanging unlabelled.
    /// Generous by default — installs write nodes but never wait on compiles (that budget is
    /// <see cref="CompileTimeout"/>, per type, in its own stage).
    /// </summary>
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When set, the gate PERSISTS what it compiled: one prebuilt-assembly bundle per package
    /// (every NodeType that reached <see cref="CompilationStatus.Ok"/>) is written into this
    /// directory, keyed to the run's framework MVID — the artifact half of issue #1660 WS1 (the
    /// gate's compile stops being verdict-only). Null (the default) keeps the gate verdict-only.
    /// </summary>
    public string? BakeOutputDirectory { get; init; }

    /// <summary>
    /// The commit the gated content was synced from, recorded in each bundle's manifest for
    /// provenance. Null falls back to the repo snapshot's own commit sha.
    /// </summary>
    public string? SourceSha { get; init; }

    /// <summary>
    /// The BAKE this gate run CONSUMES (#1763), read and address-checked before the mesh boots.
    /// Null (the default) keeps the gate self-producing — it compiles the content itself, which
    /// is the emergency/standalone shape and stays supported.
    ///
    /// <para>With a seed the gate stops being a producer: <c>PackageInstaller</c>'s existing
    /// adopt-before-compile step takes the bake's bytes for every type it installs, and what the
    /// gate then renders and executes <c>Tests</c> on is the assembly that will actually ship —
    /// a strictly stronger claim than judging a private compile of the same sources.</para>
    /// </summary>
    public BakeSeed? Seed { get; init; }

    /// <summary>
    /// Module entry assemblies to activate IN ADDITION to the ones this image ships — absolute
    /// paths, typically a node repo's own freshly-built modules mounted into the container.
    ///
    /// <para>🚨 <b>Why the gate needs this at all.</b> The gate judges a node repo's content, and
    /// that content may declare node types a MODULE provides (an <c>Agent</c> or <c>Skill</c> node
    /// needs the AI engine's types registered, or the install is refused "not registered"). While
    /// a module's SOURCE lives in the platform repo, the tester lands it from its own closure lane
    /// and nothing else is needed. Once the source moves to a node repo, the platform image can no
    /// longer build it — and the only build of those bytes is the one the node repo's own CI just
    /// produced. This is the seam that lets that build reach the gate, and it is what unblocks
    /// moving a module's source out of the platform repo at all.</para>
    ///
    /// <para>Absolute paths pass through <c>MeshBuilder.ResolveModulePath</c> untouched, so a
    /// mounted path is used exactly as given — no probing, no image fallback that could silently
    /// substitute a different copy.</para>
    /// </summary>
    public IReadOnlyList<string> ExternalModules { get; init; } = [];

    /// <summary>
    /// Which slice of the discovered packages this run gates (<c>--shard i/n</c>), or null for the
    /// whole set. See <see cref="GateShardPlan"/> for what a shard installs versus gates, and
    /// Doc/Architecture/NodeRepoGateSharding for why the lane fans out at all.
    /// </summary>
    public GateShard? Shard { get; init; }
}

/// <summary>
/// The <c>mw-plugin-test</c> pipeline: boots a fresh IN-PROCESS monolith mesh (in-memory
/// persistence — throwaway, no external services), imports every node-repo package of a
/// checkout dependency-first through the standard <see cref="PackageInstaller"/>, waits for
/// every NodeType to reach a terminal <see cref="CompilationStatus"/> (a compile error prints
/// the Roslyn diagnostics and fails the run), renders each type's default area, and EXECUTES
/// each type's <c>Tests</c> layout area — a red test fails the run. Reactive end-to-end; the
/// console <c>Main</c> bridges once at the boundary.
/// </summary>
public static class PluginGateRunner
{
    /// <summary>The gate's admin identity (the in-process analogue of DevLogin).</summary>
    private static readonly AccessContext GateAdmin = new()
    {
        ObjectId = "mw-plugin-test",
        Name = "Plugin Gate",
        Email = "mw-plugin-test@meshweaver.io",
        Roles = ["Admin"],
    };

    /// <summary>
    /// Runs the gate over <paramref name="options"/>' repo root. Cold: the mesh boots on
    /// subscribe and is torn down when the report emits (or the pipeline faults).
    /// </summary>
    public static IObservable<GateReport> Run(GateOptions options) =>
        Observable.Defer(() =>
        {
            var harness = GateMesh.Create(options.Output, options.Seed, options.ExternalModules);
            var pool = harness.ServiceProvider.GetRequiredService<IoPoolRegistry>()
                .Get("plugin-test:files");
            return LocalNodeRepo.Load(options.RepoRoot, pool)
                // The seed's UPSTREAM packages are materialized into the snapshot so the gate
                // INSTALLS its dependencies (node definitions register their types; the seed's
                // assemblies stamp their compiles) - see SeedPackages for the failure this ends.
                .Select(snapshot => SeedPackages.Materialize(snapshot, options.Seed, options.Output))
                .SelectMany(m => LocalNodeRepo.DiscoverPackages(m.Snapshot)
                    .SelectMany(packages => RunPackages(harness, options, m.Snapshot, packages, m.UpstreamIds)
                        // The bake artifact rides the compile the gate just performed (#1660 WS1):
                        // persisting is part of the run, INSIDE the outer Catch, so a bake fault is
                        // a FatalError and the run exits RED — an artifact stage must never fail
                        // into a green gate.
                        // The upstream seed is CONSUMED, never re-emitted: persisting an
                        // upstream's bundle as this repo's own would let one repo publish
                        // another's bytes under its own name (the #1814 identity class).
                        .SelectMany(report => BakeOutput.Persist(
                            harness.Mesh, options, m.Snapshot,
                            packages.Where(p => !m.UpstreamIds.Contains(p.Id)).ToImmutableList(),
                            report))))
                // The CONSUMPTION postcondition (#1763), applied to every report the run PRODUCES
                // — a red one included, so a shortfall neither masks an unrelated failure nor is
                // masked by one. Adoption leaves no trace in a gate verdict (an adopted type
                // renders and tests exactly like a compiled one), so without this the consuming
                // half could stop working with every run still green.
                //
                // Placed BEFORE the Catch on purpose, in both directions: a fault in this fold is
                // contained as a FatalError like any other, and a run that faulted upstream is
                // reported by its fault rather than by a consumption verdict derived from a run
                // that never happened.
                .Select(report => WithSeedVerdict(harness, options, report))
                .Catch((Exception ex) => Observable.Return(
                    new GateReport([]) { FatalError = $"{ex.GetType().Name}: {ex.Message}" }))
                .Finally(harness.Dispose);
        });

    /// <summary>
    /// Folds the bake-consumption verdict into the report: prints what was adopted, and turns a
    /// shortfall into a fatal error. A run with no <see cref="GateOptions.Seed"/> is unchanged.
    /// </summary>
    private static GateReport WithSeedVerdict(
        GateMesh harness, GateOptions options, GateReport report)
    {
        if (harness.SeedConsumer is not { } consumer)
            return report;
        var expected = consumer.Seed.DeclaredTypePaths.Intersect(consumer.Requested).Count;
        options.Output.WriteLine(
            $"seed: adopted {consumer.AdoptedPaths} of {expected} baked assembly(ies) for the "
            + $"{consumer.Requested.Count} installed NodeType(s) "
            + $"(bake: {consumer.Seed.Describe()})");
        if (consumer.Shortfall() is not { } shortfall)
            return report;
        // Appended, never replacing: a run that failed for its own reasons keeps that reason.
        return report with
        {
            FatalError = report.FatalError is null
                ? $"bake consumption: {shortfall}"
                : $"{report.FatalError}\nbake consumption: {shortfall}",
        };
    }

    private static IObservable<GateReport> RunPackages(
        GateMesh harness, GateOptions options, RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages, ImmutableHashSet<string> upstreamIds)
    {
        if (packages.Count == 0)
            return Observable.Return(new GateReport([])
            {
                FatalError = $"No node-repo packages (top-level folders with an index.json root) " +
                             $"found under '{options.RepoRoot}'.",
            });

        var ordered = LocalNodeRepo.OrderByDependencies(packages, snapshot);
        options.Output.WriteLine(
            $"Discovered {ordered.Count} package(s), install order: " +
            string.Join(" → ", ordered.Select(p => p.Id)));

        // 🚨 The shard's plan is printed BEFORE anything installs, and it names the discovered
        // total as well as this shard's slice. That line is the receipt the aggregate job folds:
        // one shard cannot know whether its siblings covered the rest, so the check that the
        // slices are a disjoint COVER happens where all of them are visible.
        var support = ImmutableHashSet<string>.Empty;
        if (options.Shard is { } shard)
        {
            var assignment = GateShardPlan.Assign(
                ordered, LocalNodeRepo.DependencyMap(packages, snapshot), shard);
            options.Output.WriteLine(GateShardPlan.Describe(shard, ordered.Count, assignment));
            support = assignment.Support.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
            ordered = assignment.Installed;
        }

        // Sequential (Concat): installs respect the dependency order; compiles keep running in
        // the background while later packages install.
        return ordered
            .Select(package => TestPackage(
                harness, options, snapshot, package,
                upstreamIds.Contains(package.Id), support.Contains(package.Id)))
            .ToObservable().Concat().ToList()
            .Select(results => new GateReport(results.ToImmutableList()));
    }

    private static IObservable<PackageResult> TestPackage(
        GateMesh harness, GateOptions options, RepoSnapshot snapshot, PackageManifest package,
        bool upstream, bool support)
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");
        // 🚨 WHICH STAGE FAILED is part of the verdict. The catch at the bottom wraps the WHOLE
        // pipeline — fetch, install, every NodeType compile/render/Tests gate, the idempotence
        // re-install — and used to label every one of them `install:` on a PackageResult built
        // from scratch, so `NodeCount` defaulted to 0 and `NodeTypes` to empty. That is how
        // Systemorph/MeshWeaver#1360 read as harness noise: `[FAIL] Essentials (0 node(s), 0
        // type(s)) / install: TimeoutException` was produced by a wait that did not complete
        // AFTER the nodes had been written (the same snapshot wrote 34 nodes on the re-run), yet
        // the line is indistinguishable from a package that genuinely installed nothing. The
        // counts were never measured — printing them as zeros asserts a measurement that was
        // never taken. Packages run strictly sequentially (Concat in RunPackages), so a captured
        // stage marker is exact.
        var stage = "fetch";
        // Every phase below is TIME-BOUNDED. The Catch at the bottom names the stage that THREW —
        // but a wait that never completes throws nothing, and fetch/install were the last phases
        // without a bound: a stored-but-never-announced node write (#817) left the gate silent
        // mid-package for hours. The Timeout turns that silence into `[<stage>] TimeoutException`.
        return source.FetchPackageFiles(package, "HEAD")
            .Timeout(options.InstallTimeout)
            .SelectMany(files =>
            {
                stage = "install";
                options.Output.WriteLine($"── {package.Id}: installing {files.Count} file(s)…");
                // An upstream package is INSTALLED, not gated: its types register and its
                // assemblies adopt from the seed, but compile/render/Tests verdicts belong to
                // the repo that owns it - running them here would double-judge every upstream
                // on every satellite and let an upstream flake red a repo that changed nothing.
                // A SUPPORT package is the same shape one level in: it is mounted so this shard's
                // slice can install, and the shard that owns it holds its verdict (GateShardPlan).
                var types = upstream || support
                    ? (IReadOnlyList<NodeTypeUnderTest>)[]
                    : DiscoverNodeTypes(package, files);
                // The authorizing principal is EXPLICIT — see GateMesh.AuthorizingUserId. Passing
                // nothing means "nobody authorized this", which PackageEntitlement refuses for any
                // priced package; the gate would then report a commercial package as failed
                // without ever having compiled a line of it.
                return PackageInstaller
                    .Install(harness.Mesh, package, files, snapshot.CommitSha,
                        authorizingUserId: GateMesh.AuthorizingUserId)
                    .Timeout(options.InstallTimeout)
                    .SelectMany(install =>
                    {
                        stage = "checks";
                        options.Output.WriteLine(
                            $"── {package.Id}: installed ({install.Written} written, " +
                            $"{install.Unchanged} unchanged); checking {types.Count} NodeType(s)…");
                        return types
                            .Select(type => TestNodeType(harness, options, type))
                            .ToObservable().Concat().ToList()
                            // The idempotence pin: a SECOND install of the same snapshot must write
                            // ZERO nodes — otherwise every re-sync would churn versions, re-broadcast
                            // nodes and recompile untouched NodeTypes (the deploy-flicker source).
                            // Runs after the compile gates so an enriched NodeType (compile stamps)
                            // is the realistic re-install input.
                            .SelectMany(typeResults => PackageInstaller
                                .Install(harness.Mesh, package, files, snapshot.CommitSha,
                                    authorizingUserId: GateMesh.AuthorizingUserId)
                                // Same bound as the first install; its own Catch below already
                                // folds a TimeoutException into IdempotenceError by name.
                                .Timeout(options.InstallTimeout)
                                .Select(second => new PackageResult(package.Id)
                                {
                                    Upstream = upstream,
                                    Support = support,
                                    NodeCount = install.Total,
                                    IdempotenceError = second.Written == 0
                                        ? null
                                        : $"re-install of the unchanged snapshot wrote {second.Written} node(s) " +
                                          "(expected 0 — the unchanged-skip regressed): " +
                                          // NAME the paths — a bare count is undiagnosable, and an
                                          // unnamed regression is how the placeholder-root churn
                                          // shipped: every run said "wrote 1" and nothing said WHICH.
                                          string.Join(", ", second.WrittenPaths.DefaultIfEmpty("(untracked)")),
                                    NodeTypes = typeResults.ToImmutableList(),
                                })
                                .Catch((Exception ex) => Observable.Return(new PackageResult(package.Id)
                                {
                                    Upstream = upstream,
                                    Support = support,
                                    NodeCount = install.Total,
                                    IdempotenceError = $"re-install failed: {ex.GetType().Name}: {ex.Message}",
                                    NodeTypes = typeResults.ToImmutableList(),
                                })));
                    });
            })
            .Catch((Exception ex) => Observable.Return(new PackageResult(package.Id)
            {
                Upstream = upstream,
                                    Support = support,
                // Named for the stage that actually threw, and marked as NOT a measurement so
                // WriteSummary prints "counts unavailable" instead of a fabricated "0 node(s)".
                CountsMeasured = false,
                InstallError = $"[{stage}] {ex.GetType().Name}: {ex.Message}",
            }));
    }

    /// <summary>One NodeType of one package, as parsed from its file (pre-install).</summary>
    internal sealed record NodeTypeUnderTest(
        string Path, string Package, string? Configuration, bool HasSources)
    {
        /// <summary>The type compiles when it carries a configuration lambda or source files.</summary>
        public bool Compiles => !string.IsNullOrWhiteSpace(Configuration) || HasSources;

        /// <summary>The type declares an executable <c>Tests</c> layout area in its configuration.</summary>
        public bool DeclaresTestsArea =>
            Configuration?.Contains("WithView(\"Tests\"", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Document-parse tolerances for a node file — the same copy <c>JsonFileParser.Parse</c> and
    /// <c>TreeNodeLoader.ContentJsonDocument</c> both make, off the hub's own options.
    ///
    /// <para>🚨 <b>The defaults are a drift, not a neutral choice.</b> <see cref="JsonDocumentOptions"/>
    /// defaults REJECT comments and trailing commas. A node file carrying either parses under the
    /// installer and under the bake — both opt in — and would have thrown here, landing in the
    /// <c>catch (JsonException)</c> and vanishing from the gate's view: the identical
    /// discovered-by-one-half defect as the BOM, one parser tolerance later (Copilot review on
    /// #2063). Every tolerance this parse does NOT share with the installer is a future #2063.</para>
    /// </summary>
    private static readonly JsonDocumentOptions NodeJsonDocument = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The package's NodeType nodes (content.$type == NodeTypeDefinition), from the raw files.
    //
    // 🚨 EVERY file-shaped decision here is DELEGATED, never re-implemented — this half of CI has
    // to see exactly the tree the other half bakes and the runtime installs, and the two ways it
    // silently failed to (#2063) were both a local copy of a rule that lives elsewhere:
    //
    //  1. **The BOM.** This parsed `file.Content` raw. Package content arrives as BYTES and is
    //     decoded with `Encoding.UTF8.GetString`, which PRESERVES U+FEFF (unlike File.ReadAllText),
    //     so a BOM'd `.json` threw here and the `catch (JsonException)` dropped it — labelled
    //     "malformed json is surfaced by the install itself", which stopped being true when #1767
    //     taught the installer to strip the BOM. The install then wrote the node, this discovery
    //     did not see it, and the type compiled UNGATED. `samples/Graph/Data/PensionFund` ships 5
    //     BOM'd NodeTypes and the gate printed `(72 node(s), 0 type(s))` — a green run over nothing.
    //  2. **The node path.** `NodeFileMapper.FromRelativePath` is only half the installer's rule;
    //     `PackageInstaller.NodePathForFile` is the whole of it, and it also EXCLUDES README.md,
    //     `manifest.lock` and `content/**` assets. A NodeType-shaped `.json` under `content/**`
    //     was therefore discovered here but never installed — so the gate would wait out its full
    //     compile timeout for a node that does not exist, and report a TimeoutException.
    //
    // Both rules now come from the installer itself, and `GateDiscoveryEqualsBakeDiscoveryTest`
    // pins this set equal to the compiler-driven bake's — the drift, not either symptom, is the bug.
    internal static IReadOnlyList<NodeTypeUnderTest> DiscoverNodeTypes(
        PackageManifest package, IReadOnlyList<PackageFile> files)
    {
        var sourceFolders = files
            .Where(f => f.RelativePath.Contains("/Source/", StringComparison.Ordinal))
            .Select(f => f.RelativePath[..f.RelativePath.IndexOf("/Source/", StringComparison.Ordinal)])
            .ToImmutableHashSet(StringComparer.Ordinal);

        var types = new List<NodeTypeUnderTest>();
        foreach (var file in files)
        {
            if (!file.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            // The installer's own rule — null means "this file is not a node at all".
            if (PackageInstaller.NodePathForFile(file.RelativePath) is not { Length: > 0 } path)
                continue;
            string? configuration;
            try
            {
                // The SAME read TreeNodeLoader and JsonFileParser make — BOM-stripped AND
                // comment/trailing-comma tolerant.
                using var doc = JsonDocument.Parse(
                    FileFormatParserRegistry.WithoutBom(file.Content), NodeJsonDocument);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("$type", out var type)
                    || type.GetString() != nameof(NodeTypeDefinition))
                    continue;
                configuration = content.TryGetProperty("configuration", out var config)
                    && config.ValueKind == JsonValueKind.String
                        ? config.GetString()
                        : null;
            }
            catch (JsonException)
            {
                continue; // malformed json is surfaced by the install itself
            }
            types.Add(new NodeTypeUnderTest(path, package.Id, configuration,
                HasSources: sourceFolders.Contains(path)));
        }
        return types.OrderBy(t => t.Path, StringComparer.Ordinal).ToImmutableList();
    }

    private static IObservable<NodeTypeResult> TestNodeType(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type)
    {
        var result = new NodeTypeResult(type.Path, type.Package);
        return AwaitCompile(harness, options, type, result)
            // 🚨 .Fails(), never `== Failed`: a type whose compile produced NO verdict
            // (Inconclusive/Unrecorded) has no settled build either, so rendering it would add a
            // second, more confusing failure on top of the first. An equality test against one
            // member silently admits every member added after it.
            .SelectMany(afterCompile => afterCompile.Compile.Fails()
                // A compile that did not pass already fails the gate — rendering it would only
                // add noise.
                ? Observable.Return(afterCompile with
                {
                    Render = CheckOutcome.Skipped,
                    Tests = type.DeclaresTestsArea ? CheckOutcome.Skipped : afterCompile.Tests,
                })
                : RenderGate(harness, options, type, afterCompile))
            .Do(r => options.Output.WriteLine(
                $"   {(r.Success ? "ok " : "RED")} {r.Path} " +
                $"[compile:{r.Compile} render:{r.Render} tests:{r.Tests}]"));
    }

    private static IObservable<NodeTypeResult> AwaitCompile(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type, NodeTypeResult result)
    {
        if (!type.Compiles)
            return Observable.Return(result with { Compile = CheckOutcome.Skipped });

        return harness.Mesh.GetWorkspace().GetMeshNodeStream(type.Path)
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1)
            .Timeout(options.CompileTimeout)
            .Select(node =>
            {
                var def = (NodeTypeDefinition)node!.Content!;
                if (def.CompilationStatus == CompilationStatus.Ok)
                    return result with
                    {
                        CompilationStatus = def.CompilationStatus,
                        Compile = CheckOutcome.Passed,
                    };
                return result with
                {
                    CompilationStatus = def.CompilationStatus,
                    Compile = CheckOutcome.Failed,
                    CompileDetail = string.IsNullOrWhiteSpace(def.CompilationError)
                        ? "compilation failed without diagnostics"
                        : def.CompilationError,
                };
            })
            // 🚨 A TIMEOUT IS NOT A COMPILE FAILURE. It used to be scored one, and the cost was
            // paid twice: #2454 (a PR of one markdown line, a test ledger and a test-list row,
            // annotated as a public-API break) and #2463 (main red and a production rollout held
            // ~11 hours on a compile the same run's own log had recorded as `ok`).
            .Catch((TimeoutException _) => Observable.Return(
                NoTerminalCompileStatus(result, type.Path, options.Seed, options.CompileTimeout)));
    }

    /// <summary>
    /// Classifies "the mesh never wrote a terminal compile status within the budget" — the ONE
    /// observation behind both #2454 and #2463 — into the outcome whose reader can act on it.
    /// Pure, so the classification is unit-testable without a mesh.
    ///
    /// <para>The discriminator is EVIDENCE, not a guess: on a run that CONSUMES a bake
    /// (<see cref="GateOptions.Seed"/>), a bundle carrying an assembly for this exact NodeType is
    /// proof the compile SUCCEEDED — the compiler stage emitted those bytes with no mesh involved
    /// at all ("<c>mw-compiler compile … (no mesh)</c>"). So when the bake declares the type and
    /// the mesh still reported nothing, the compile is not in question and the STATUS WRITE is
    /// what was lost: <see cref="CheckOutcome.Unrecorded"/>. Without that evidence the honest
    /// answer is narrower — nothing answered, and the gate does not know why:
    /// <see cref="CheckOutcome.Inconclusive"/>.</para>
    ///
    /// <para>🚨 <b>Both still fail the run</b>, and the detail says why in the reader's own terms.
    /// This is deliberately NOT "the bake said ok, so pass": the type never settled, so its render
    /// and <c>Tests</c> checks never ran, and reporting green would assert checks that did not
    /// happen — the failure shape the whole gate exists to refuse.</para>
    /// </summary>
    /// <param name="result">The result so far for this type.</param>
    /// <param name="typePath">The NodeType path, matched against the bake's declared assemblies
    /// with the seeder's own OrdinalIgnoreCase comparer (<see cref="BakeSeed.DeclaredTypePaths"/>)
    /// — a stricter comparer here would report "no evidence" for bytes the seeder had happily
    /// adopted under a case difference.</param>
    /// <param name="seed">The bake this run consumed, or null on a self-producing run.</param>
    /// <param name="budget">The elapsed per-type compile budget, named in the detail.</param>
    internal static NodeTypeResult NoTerminalCompileStatus(
        NodeTypeResult result, string typePath, BakeSeed? seed, TimeSpan budget)
    {
        var seconds = $"{budget.TotalSeconds:F0}s";
        if (seed is not null && seed.DeclaredTypePaths.Contains(typePath))
            return result with
            {
                Compile = CheckOutcome.Unrecorded,
                CompileDetail =
                    $"the COMPILE SUCCEEDED — the bake this run consumed ('{seed.Directory}') "
                    + "carries an assembly for this type, produced by the compiler stage with no "
                    + $"mesh involved — but no terminal compile status was written within {seconds}. "
                    + "The mesh lost the STATUS WRITE (a MergeGuard stale/reordered refusal on the "
                    + "cross-hub compile-state fields, or the owning hub disposed with its "
                    + "CreateOrUpdateNodeRequest still in flight). This is INFRASTRUCTURE, not the "
                    + "plugin source: do not diff the content or the framework's public API. "
                    + "Systemorph/MeshWeaver#2463, #2454.",
            };
        return result with
        {
            Compile = CheckOutcome.Inconclusive,
            CompileDetail =
                $"no terminal compile status within {seconds} — the gate observed NO compile "
                + "result. This is a TIMEOUT, not a compiler diagnostic: nothing reported an "
                + "error and no source was judged. Investigate the mesh (a wedged hub, a lost or "
                + "refused status write, an install racing its own teardown), not the plugin "
                + "source. Systemorph/MeshWeaver#2454."
                + (seed is null
                    ? ""
                    : $" (The bake in '{seed.Directory}' declares no assembly for this type, so "
                      + "there is no evidence here that the compile itself succeeded.)"),
        };
    }

    private static IObservable<NodeTypeResult> RenderGate(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type, NodeTypeResult result)
        => AreaProbe.RenderDefaultArea(harness.Client, type.Path, options.RenderTimeout)
            .SelectMany(render =>
            {
                var afterRender = result with
                {
                    Render = render.Outcome,
                    RenderDetail = render.Detail,
                };
                if (!type.DeclaresTestsArea)
                    return Observable.Return(afterRender with { Tests = CheckOutcome.Skipped });
                return CreateTestsProbe(harness, type.Path)
                    .Select(hostPath => new TestsHost(
                        hostPath,
                        $"{hostPath} — the probe instance the gate created for this check"))
                    // Bounded: creating the probe is a CreateNode round-trip with no budget of its
                    // own, so an unbounded wait here would spend the whole JOB timeout and report
                    // nothing. Same budget as the render it feeds; the create is sub-second when it
                    // works.
                    .Timeout(options.RenderTimeout)
                    .Catch((TimeoutException _) => Observable.Return(new TestsHost(
                        Path: null,
                        Description: $"unresolved — no probe instance under {type.Path} within " +
                                     $"{options.RenderTimeout.TotalSeconds:F0}s")))
                    .SelectMany(host => (host.Path is null
                            ? Observable.Return(new AreaVerdict(
                                CheckOutcome.Failed, "no host to execute the Tests area on"))
                            : AreaProbe.ExecuteTestsArea(
                                harness.Client, host.Path, options.RenderTimeout))
                        .Catch((Exception ex) => Observable.Return(new AreaVerdict(
                            CheckOutcome.Failed,
                            $"could not execute Tests area: {ex.GetType().Name}: {ex.Message}")))
                        .Select(tests => afterRender with
                        {
                            Tests = tests.Outcome,
                            TestsDetail = tests.Detail,
                            TestsHost = host.Description,
                        }))
                    .Catch((Exception ex) => Observable.Return(afterRender with
                    {
                        Tests = CheckOutcome.Failed,
                        TestsDetail = $"could not create the Tests probe: " +
                                      $"{ex.GetType().Name}: {ex.Message}",
                    }));
            });

    /// <summary>The node whose hub ran a type's <c>Tests</c> area.</summary>
    /// <param name="Path">The host node's path; null when no host could be created.</param>
    /// <param name="Description">The human-readable account printed in the report — so a
    /// <c>No renderer is registered for area `Tests` on hub X</c> can be read without guessing
    /// which node X was (issue #1077).</param>
    private sealed record TestsHost(string? Path, string Description);

    /// <summary>
    /// The Tests probe ALWAYS runs on a freshly created instance, never on a shipped one.
    ///
    /// <para>🚨 This is a correctness requirement, not tidiness. A shipped instance (e.g. the
    /// Store root) is installed early in the run, so its hub activates mid-import — BEFORE this
    /// type's compile produced its release — and a hub never rebinds on its own: it keeps serving
    /// only the framework areas, and the type's Tests view is absent for the rest of the run.
    /// That surfaced as <c>No renderer is registered for area `Tests`</c> (latched as an instant
    /// red before AreaProbe treated not-found as transient, and as
    /// <c>Tests area never became available within 120s</c> after). Recycling the shipped host
    /// instead (DisposeRequest, then probe) trades this for a subscribe-vs-teardown race the raw
    /// probe stream has no recovery for — the platform's stream cache absorbs exactly that race,
    /// but the tester's <c>GetRemoteStream</c> path deliberately bypasses the cache.</para>
    ///
    /// <para>A fresh node's hub activates on FIRST ACCESS — the probe's own subscription — which
    /// is after the compile gate passed, so it binds the release just verified, deterministically.
    /// The Tests-area convention (self-contained static suites that throw on failure) is what
    /// makes the host instance interchangeable.</para>
    /// </summary>
    private static IObservable<string> CreateTestsProbe(GateMesh harness, string typePath)
    {
        var meshService = harness.ServiceProvider.GetRequiredService<IMeshService>();
        var probePath = $"{typePath}/GateProbe";
        var probe = new MeshNode("GateProbe", typePath)
        {
            Name = "Gate Probe",
            NodeType = typePath,
            MainNode = probePath,
            State = MeshNodeState.Active,
        };
        var access = harness.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => meshService.CreateNode(probe))
            .Select(created => created.Path);
    }

    /// <summary>
    /// The in-process mesh + render client for one gate run — the console analogue of the
    /// monolith test base's mesh: monolith hosting, in-memory persistence, row-level security,
    /// Graph + Space types, the plugin catalog, an isolated assembly store / compilation cache,
    /// and an admin circuit identity.
    /// </summary>
    private sealed class GateMesh : IDisposable
    {
        /// <summary>The mesh hub.</summary>
        public required IMessageHub Mesh { get; init; }

        /// <summary>The client hub used for layout-area sync streams.</summary>
        public required IMessageHub Client { get; init; }

        /// <summary>The mesh's root service provider.</summary>
        public required IServiceProvider ServiceProvider { get; init; }

        /// <summary>
        /// The bake-consuming <see cref="IPrebuiltAssemblyConsumer"/> when this run was given a
        /// seed, else null. Held so the run can read its accounting for the postcondition.
        /// </summary>
        public BakeSeedConsumer? SeedConsumer { get; init; }

        private readonly List<IHostedService> startedHostedServices = [];
        private readonly TextWriter output;

        private GateMesh(TextWriter output) => this.output = output;

        /// <summary>Boots the gate mesh (blocking — runs once at the console boundary).</summary>
        /// <param name="output">Progress sink.</param>
        /// <param name="seed">The bake this run consumes, or null to compile the content itself.</param>
        public static GateMesh Create(
            TextWriter output, BakeSeed? seed = null,
            IReadOnlyList<string>? externalModules = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            // Warning by default — the gate's own output is the product, and a bulk run at Trace
            // is ~100k lines. But a message-flow hang reproduces ONLY in bulk (see the /debug
            // skill), and this tool IS the bulk shape, so the trace has to be reachable without
            // editing the tool. MW_LOG_LEVEL=Trace turns the whole run into the trace the skill
            // greps; MW_LOG_CATEGORIES narrows it to the categories that carry MESSAGE_FLOW.
            var minLevel = Enum.TryParse<LogLevel>(
                Environment.GetEnvironmentVariable("MW_LOG_LEVEL"), ignoreCase: true, out var lvl)
                ? lvl
                : LogLevel.Warning;
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(minLevel);
                // 🚨 ALWAYS attach the provider, including at the default Warning level. It used to
                // be attached only when the level was turned DOWN (`minLevel < Warning`), so a gate
                // run at its default verbosity emitted the report and NOTHING else — every
                // framework Warning was written to a logger with no sinks. When the gate went RED
                // on CI ("No renderer is registered for area `Tests` on hub `Store`", 2026-08-10)
                // the run therefore carried zero evidence of WHY the instance was bound to the
                // fallback config, and the failure could not be diagnosed from the job log at all.
                // Warning volume is a handful of lines per run — the trace levels are still opt-in
                // through MW_LOG_LEVEL.
                logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
                // 🚨 The bake-consumption category is raised to Information whenever a bake is
                // being consumed — the same argument as the always-attach fix above, one seam
                // over. A bake shortfall is a RED verdict, and every reason an assembly is
                // declined (framework identity, the per-type dependency record, a payload the
                // bundle's manifest names but does not carry) is logged at Information by
                // ShippedPrebuiltBundles / PrebuiltAssemblySeeder. At the gate's default Warning
                // those lines are never written, so the verdict pointed at evidence that did not
                // exist: MeshWeaver.Crm main sat red from 2026-08-28 21:54 for ~12 hours on
                // "adopted 86 of 87 … 1 were DECLINED" with every per-type verdict green and no
                // way to tell which assembly, or why, from the job log. The volume is one line
                // per bundle (34 on that run) — the trace levels stay opt-in via MW_LOG_LEVEL.
                if (seed is not null)
                    logging.AddFilter(BakeSeedConsumer.LogCategory, LogLevel.Information);
                foreach (var category in (Environment.GetEnvironmentVariable("MW_LOG_CATEGORIES") ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    logging.AddFilter(category, minLevel);
            });
            services.AddOptions();

            var runRoot = Path.Combine(Path.GetTempPath(),
                $"mw-plugin-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            // 🚨 THE CONSUMING SEAM (#1763). Registering an IPrebuiltAssemblyConsumer is the WHOLE
            // wiring: `PackageInstaller` already asks for one before it issues any release request
            // (#1707 slice 3), so a gate handed a bake adopts its bytes and a gate handed none
            // resolves nothing and compiles, exactly as before. No new call site, no second
            // ordering to get wrong, and the adopt-before-compile step the gate exercises is the
            // one a portal runs. Registered as an INSTANCE (not a factory) so the accounting the
            // postcondition reads is one object, whichever container resolves the interface.
            IMessageHub? meshHub = null;
            var seedConsumer = seed is null
                ? null
                : new BakeSeedConsumer(
                    () => meshHub ?? throw new InvalidOperationException(
                        "the gate mesh was asked for prebuilt assemblies before it finished booting"),
                    seed);
            // 🚨 The AI engine is a MODULE (#2276), and the gate activates it the way a portal
            // does: the csproj's closure lane laid modules/MeshWeaver.AI/ beside this binary, and
            // this in-memory configuration is the gate's Modules:Assemblies. The engine matters
            // here because plugin packages ship Agent and Skill nodes — without the AI node types
            // those installs are refused "not registered". Features:StaticRepoSync:Partitions
            // names ONE AI partition; the module's served-as-a-unit rule
            // (AiMeshModuleAttribute.ServeFromPartitions) unions in the rest, so this cannot go
            // stale when the engine gains a partition. The DB-served shape is load-bearing, not a
            // preference: a statically-served AI partition refuses Agent/Skill package installs
            // (StaticShadowedReason — the 2026-08-11 30s-hang-per-package incident).
            //
            // 🚨 EXTERNAL modules (--module) are activated the SAME way, and are REQUIRED too. They
            // are how a module whose source has left the platform repo still reaches the gate: the
            // node repo's own CI built the bytes, mounts them, and names them here. Absolute paths
            // pass through ResolveModulePath untouched, so a mounted module is used exactly as
            // given — an image copy can never silently substitute for it.
            // 🚨 The entry list is TesterModules' — shared with the bake, which compiles against
            // exactly these. Both lanes reading one list is the fix for the two having disagreed
            // (see TesterModules): a module added for the gate can no longer go missing from the
            // bake's reference set, which is how five Store NodeTypes came to read as content
            // errors while the gate's own compile-check stayed green.
            var moduleEntries = new Dictionary<string, string?>
            {
                ["Features:StaticRepoSync:Partitions:0"] = "Agent",
            };
            var moduleIndex = 0;
            foreach (var module in TesterModules.Entries(externalModules))
            {
                moduleEntries[$"Modules:Assemblies:{moduleIndex}"] = module;
                moduleEntries[$"Modules:Required:{moduleIndex}"] = module;
                moduleIndex++;
            }
            var gateModules = new ConfigurationBuilder()
                .AddInMemoryCollection(moduleEntries)
                .Build();
            // 🚨 A gate never tests its own inputs (AGENTS.md): a missing engine module fails RED
            // here, never a booted mesh without the AI node types that then refuses every install
            // for a reason naming the wrong cause.
            var missingModules = MeshBuilderModuleActivation.MissingRequired(
                gateModules, MeshBuilder.ResolveModulePath, File.Exists);
            if (missingModules.Length > 0)
                throw new InvalidOperationException(
                    "the gate's required module(s) did not resolve beside the binary: "
                    + string.Join(", ", missingModules)
                    + ". Modules this image ships come from the MeshModulesPublish closure lane "
                    + "in MeshWeaver.PluginTester.csproj; modules passed with --module must exist "
                    + "at the absolute path given (mount them into the container). A run whose "
                    + "modules are missing cannot gate.");
            var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())
                .UseMonolithMesh()
                .AddInMemoryPersistence()
                .AddRowLevelSecurity()
                .AddGraph()
                .AddSpaceType()
                // The gate runs NO boot static-repo import (its content arrives through package
                // installs), so the import-settled signal is registered PRE-SETTLED — the
                // documented "nothing to import" state. The AI module's provider-credential seed
                // resolves it, and anything sequencing on it proceeds immediately instead of
                // waiting for an import that will never run here.
                .ConfigureServices(s =>
                {
                    var settled = new StaticRepoImportSettled();
                    settled.MarkSettled();
                    return s.AddSingleton(settled);
                })
                .InstallConfiguredModules(gateModules,
                    msg => output.WriteLine($"[gate modules] {msg}"))
                .AddPluginCatalog()
                .AddMeshNodes(RootAdminAccess())
                // Per-run isolated assembly store + compilation cache (AddInMemoryPersistence
                // TryAdds a process-pid-scoped store — REPLACE it, mirroring the test base).
                .ConfigureServices(s =>
                {
                    s.RemoveAll<IAssemblyStore>();
                    return s.AddFileSystemAssemblyStore(Path.Combine(runRoot, "assembly-store"));
                })
                .ConfigureServices(s => s.Configure<CompilationCacheOptions>(o =>
                    o.CacheDirectory = Path.Combine(runRoot, "compilation-cache")))
                .ConfigureServices(s => seedConsumer is null
                    ? s
                    : s.AddSingleton<IPrebuiltAssemblyConsumer>(seedConsumer))
                .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(120)));
            services.AddSingleton(builder.BuildHub);

            if (seed is not null)
                output.WriteLine($"consuming bake '{seed.Directory}' — {seed.Describe()}");

            var provider = services.CreateMeshWeaverServiceProvider();
            var mesh = provider.GetRequiredService<IMessageHub>();
            meshHub = mesh;
            var harness = new GateMesh(output)
            {
                Mesh = mesh,
                Client = CreateClient(mesh),
                ServiceProvider = provider,
                SeedConsumer = seedConsumer,
            };

            // Pre-warm the NodeType hubs a runtime CreateNode would otherwise recurse on
            // (the same chicken-and-egg the monolith test base pre-warms).
            foreach (var nodeTypePath in new[] { "AccessAssignment", "PartitionAccessPolicy" })
            {
                var typeNode = provider.FindStaticNode(nodeTypePath);
                if (typeNode?.HubConfiguration is { } config)
                    _ = mesh.GetHostedHub(new Address(nodeTypePath), config);
            }

            // The gate's admin identity (DevLogin analogue). `mw-plugin-test` is a
            // SINGLE-IDENTITY host — one gate admin for the whole run, no Blazor circuit — so this
            // is SetHostIdentity, not SetCircuitContext: the identity must survive every Rx /
            // scheduler hop, including the layout-area sync-stream subscribe the AreaProbe drives.
            // (SetCircuitContext writes only the calling flow's AsyncLocal; a gate identity set
            // there is gone by the time the render probe subscribes, and the area never
            // materialises because RLS denies the context-less read.)
            provider.GetRequiredService<AccessService>().SetHostIdentity(GateAdmin);

            // Activate hosted services DI registered but nothing started (no generic host here).
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                harness.startedHostedServices.Add(hosted);
            }
            return harness;
        }

        // Root-scope Admin for everyone: the gate is a throwaway single-user mesh; the install
        // itself runs system-impersonated, this grant is what lets the render client READ.
        //
        // 🚨 …AND the same grant on the Admin PARTITION, which is a different thing and is what
        // makes the gate's principal a GLOBAL ADMIN. `hub.IsGlobalAdmin(userId)` reads
        // `Permission.All` at scope `Admin` — an AccessAssignment in `Admin/_Access` — and a ROOT
        // grant is deliberately not that shape (AccessControl.md → "The Admin partition"). Without
        // it the gate could install nothing commercial: PackageEntitlement (#830) refuses a priced
        // package unless the authorizing principal is a global admin, so `Manufacturing`
        // (price -1 CHF, coupon-only) failed the MeshWeaver.Plugins gate with
        // `PackageAuthorizationException` the moment that repo's image pin moved onto eb330e1a2 —
        // not because anything about the package was broken, but because the harness had no
        // admin to offer. Mirrors TestUsers.PublicAdminAccess, which seeds both scopes.
        private static MeshNode[] RootAdminAccess() =>
        [
            GateAdminAccess(""),
            GateAdminAccess(AdminPartition),
        ];

        /// <summary>The Admin partition — the scope <c>IsGlobalAdmin</c> evaluates.</summary>
        private const string AdminPartition = "Admin";

        /// <summary>
        /// The gate principal (<see cref="WellKnownUsers.Public"/>) — the identity that AUTHORIZES
        /// every install in a gate run. A CI gate is an attended, operator-run check, so it presents
        /// an admin rather than installing as "nobody" (which
        /// <see cref="PackageEntitlement.Authorize"/> correctly refuses for priced packages).
        /// </summary>
        public static string AuthorizingUserId => WellKnownUsers.Public;

        private static MeshNode GateAdminAccess(string ns) =>
            // Root-scope assignments live at "_Access" (not ""), so the security service maps them
            // to scope "" — an empty namespace would land them at "Public_Access" with no scope.
            new(WellKnownUsers.Public + "_Access", ns.Length > 0 ? ns + "/_Access" : "_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Public Access",
                MainNode = ns,
                Content = new AccessAssignment
                {
                    AccessObject = WellKnownUsers.Public,
                    DisplayName = "Public",
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
            };

        private static IMessageHub CreateClient(IMessageHub mesh)
        {
            var routing = mesh.ServiceProvider.GetRequiredService<IRoutingService>();
            return mesh.ServiceProvider.CreateMessageHub(
                new Address("client", Guid.NewGuid().ToString("N")[..12]),
                configuration =>
                {
                    configuration.TypeRegistry.WithType(
                        typeof(MeshNodeReference), nameof(MeshNodeReference));
                    return configuration
                        .AddMeshTypes()
                        .AddData()
                        .WithRequestTimeout(TimeSpan.FromSeconds(120))
                        .WithInitialization(h => h.RegisterForDisposal(routing.RegisterStream(h)));
                })!;
        }

        /// <summary>
        /// Synchronous teardown at the run boundary: stop hosted services, dispose the hubs and
        /// JOIN their disposal, cancel+join the I/O pools, then tear down the container.
        /// </summary>
        public void Dispose()
        {
            foreach (var hosted in Enumerable.Reverse(startedHostedServices))
            {
                try
                {
                    hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    output.WriteLine($"[teardown] hosted service stop failed: {ex.Message}");
                }
            }
            // 🚨 The CLIENT is block-joined too, not merely disposed. It was the one hub in this
            // teardown that only had its disposal STARTED: the next statements dispose the Mesh and
            // then the whole container, so the client's still-draining action block, its
            // routing-stream unregistration and its sync-stream registrants ran against services
            // being torn down underneath them. That is the burst of
            // `[SYNC_STREAM] Not setting … — stream is disposed` + `resubscribe failed …
            // TargetInvocationException` immediately before the gate died with exit 139 mid-run
            // (MeshWeaver.Plugins run 33236823482, job 99060770662) — a use-after-dispose, not the
            // final teardown, which already joined.
            Client.DisposeAndJoin(
                message => output.WriteLine($"[teardown] {message}"),
                TimeSpan.FromSeconds(30));
            // Block-join at the run boundary: teardown = synchronous dispose that joins.
            //
            // This used to splice `.Timeout(30s).Catch(...)` INTO the signal, which races the thing
            // being waited for and leaves a fault arriving afterwards with no observer — the
            // unobserved exception ReactiveCompletion's remarks describe (#2301/#2488). The bound is
            // now outside the signal and the subscription outlives it, so a late disposal fault is
            // still SAID.
            Mesh.DisposeAndJoin(
                message => output.WriteLine($"[teardown] {message}"),
                TimeSpan.FromSeconds(30));
            var leakedIoLeaves = 0;
            try
            {
                leakedIoLeaves = ServiceProvider.GetRequiredService<IoPoolRegistry>()
                    .DrainAll(out var residualByPool);
                if (residualByPool.Count > 0)
                    output.WriteLine(
                        $"[teardown] pooled I/O leaves survived the drain: {string.Join(", ", residualByPool)}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"[teardown] pool drain failed: {ex.Message}");
            }
            // The async half of teardown, then the TERMINAL SIGNAL — the phase everything that
            // deferred itself to "after the drains" is waiting on: the collectible NodeType ALC
            // unloads (MeshDataSource.UnloadNodeAssemblyContexts) and the hosted hubs' lifetime
            // scopes (TeardownOrderedScopeDisposal). Without it those run only when the container
            // below disposes them mid-teardown — the unordered shape whose stragglers used to kill
            // this very tool with exit 139. Block-joins are the sanctioned run-boundary exception,
            // and both are bounded and loud.
            var asyncDisposeClean = true;
            try
            {
                var queue = ServiceProvider.GetService<AsyncDisposeQueue>();
                asyncDisposeClean = queue is null
                    || queue.DrainAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                if (!asyncDisposeClean)
                    output.WriteLine("[teardown] async dispose queue did not quiesce within 30s");
            }
            catch (Exception ex)
            {
                asyncDisposeClean = false;
                output.WriteLine($"[teardown] async dispose queue drain failed: {ex.Message}");
            }
            try
            {
                ServiceProvider.GetService<MeshTeardownSignal>()
                    ?.SignalCompleted(new TeardownReport(leakedIoLeaves, asyncDisposeClean));
            }
            catch (Exception ex)
            {
                output.WriteLine($"[teardown] teardown signal failed: {ex.Message}");
            }
            (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}
