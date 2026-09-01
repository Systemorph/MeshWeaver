using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Compiler;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The COMPILER-DRIVEN bake (#1763): read a checkout's node trees, compile every dynamic NodeType
/// with <see cref="MeshWeaver.Compiler"/>, emit DLL + PDB, and write the SAME prebuilt-assembly
/// bundles <c>BakeOutput</c> writes — <c>&lt;package&gt;.zip</c> per package plus
/// <c>framework-mvid.txt</c>, keyed to this process's framework build identity, carrying the
/// per-type dependency records (#1719).
///
/// <para><b>No mesh anywhere in this path.</b> No <c>MeshBuilder</c>, no <c>AddGraph()</c>, no
/// import, no hub scheduler, no per-type activation. That is the whole point of the issue: bake is
/// a build step, and the mesh's job is to CONSUME a bake, not to produce one. The gate
/// (<see cref="PluginGateRunner"/>) still stands up a mesh, because rendering a layout area and
/// executing a <c>Tests</c> area are genuine runtime behaviours — producing an assembly is not.</para>
///
/// <para><b>The output format is frozen.</b> <see cref="BundleWriter"/> is the same writer, the
/// framework identity is the same <c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c> reading, and the
/// dependency record is the same <see cref="CompiledDependencies"/> computation over the emitted
/// assembly's AssemblyRef table. <c>ShippedPrebuiltBundles</c>, <c>PluginBundleClient.Adopt</c> and
/// <c>PrebuiltAssemblySeeder</c> cannot tell which producer wrote a bundle — and must not.</para>
///
/// <para><b>The emergency path is untouched.</b> A live instance with no usable artifact still
/// compiles its own; #1707 requires it ("there will always be code without any CI"). This removes
/// the mesh from the BUILD lane, not the recovery lane.</para>
/// </summary>
public static class TreeBake
{
    /// <summary>Options for one compiler-driven bake.</summary>
    public sealed record Options
    {
        /// <summary>The checkout root holding the node-repo packages.</summary>
        public required string RepoRoot { get; init; }

        /// <summary>Directory the bundles + <c>framework-mvid.txt</c> are written into.</summary>
        public required string OutputDirectory { get; init; }

        /// <summary>Commit recorded in each bundle manifest for provenance.</summary>
        public string? SourceSha { get; init; }

        /// <summary>Progress sink.</summary>
        public TextWriter Output { get; init; } = Console.Out;

        /// <summary>Diagnostics.</summary>
        public ILogger Logger { get; init; } = NullLogger.Instance;

        /// <summary>Factory for the leaf loggers the toolchain's own services want (the NuGet
        /// resolver). Null keeps them silent.</summary>
        public ILoggerFactory? LoggerFactory { get; init; }

        /// <summary>
        /// The module assemblies this bake compiles against, as RESOLVED file paths — build them
        /// with <see cref="TesterModules.ResolvedPaths"/>, which is the one list the gate reads too.
        ///
        /// <para>A module lives under <c>modules/&lt;name&gt;/</c> and is therefore NOT in
        /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so it reaches the compile only by being composed in
        /// — see <see cref="InstalledModuleAssembly"/>, which names this as its first purpose.</para>
        ///
        /// <para>🚨 Already-resolved PATHS, not entry names, and deliberately so: resolving means
        /// calling <c>MeshBuilder.ResolveModulePath</c>, and <c>MeshFreeBakePathTest</c> fails the
        /// build if anything reachable from <see cref="Run"/> so much as NAMES a mesh type. That
        /// guard is right — a bake is a build step and must not touch a mesh — so resolution
        /// happens at the CLI boundary and the bake receives the answer.</para>
        /// </summary>
        public IReadOnlyList<string> ModuleAssemblyPaths { get; init; } = [];
    }

    /// <summary>One NodeType's outcome.</summary>
    /// <param name="NodePath">The NodeType's mesh path.</param>
    /// <param name="Package">The package it belongs to.</param>
    /// <param name="Error">The failure, or null when it compiled.</param>
    /// <param name="MatchedSourcePaths">The Code nodes the compile consumed (empty on failure).</param>
    public sealed record TypeResult(
        string NodePath,
        string Package,
        string? Error,
        ImmutableArray<string> MatchedSourcePaths)
    {
        /// <summary>
        /// The <c>@@</c>-include targets the compile pulled in — Code nodes NO source query
        /// matched, so they are deliberately absent from <see cref="MatchedSourcePaths"/> and from
        /// the bundle's <c>sourceVersions</c>, while being inside both the emitted bytes and the
        /// source fingerprint (#2948). Reported so a reader (and
        /// <c>BakeEquivalenceTest</c>) can see that the include actually resolved rather than
        /// assuming it from an equal hash.
        /// </summary>
        public ImmutableArray<string> ResolvedIncludePaths { get; init; } = [];

        /// <summary>True when the type produced bytes.</summary>
        public bool Success => Error is null;
    }

    /// <summary>The whole bake's outcome.</summary>
    /// <param name="FrameworkIdentity">The identity every bundle is keyed to.</param>
    /// <param name="Types">Per-NodeType results, ordinal by path.</param>
    /// <param name="Bundles">The bundle files written, ordinal by path.</param>
    /// <param name="FatalError">A failure that stopped the bake before it produced a verdict.</param>
    public sealed record Report(
        string FrameworkIdentity,
        ImmutableArray<TypeResult> Types,
        ImmutableArray<string> Bundles,
        string? FatalError = null)
    {
        /// <summary>Process exit code: 0 only when nothing failed.</summary>
        public int ExitCode =>
            FatalError is null && Types.All(t => t.Success) ? 0 : 1;
    }

    /// <summary>The file beside the bundles naming the framework identity — same name and same
    /// contract as the mesh-driven bake's, because CI reads it either way.</summary>
    public const string FrameworkMvidFile = BakeOutput.FrameworkMvidFile;

    /// <summary>Runs the bake. Synchronous — this is a build step at a console boundary.</summary>
    /// <param name="options">What to bake, and where to.</param>
    public static Report Run(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var frameworkIdentity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        if (PrebuiltAssemblySeeder.LiveFrameworkIdentityWarning is { } warning)
            options.Output.WriteLine($"bake: ⚠ framework identity degraded — {warning}");

        RepoSnapshot snapshot;
        IReadOnlyList<PackageManifest> packages;
        try
        {
            snapshot = LocalNodeRepo.LoadSync(options.RepoRoot);
            packages = LocalNodeRepo.DiscoverPackages(snapshot).Wait();
        }
        catch (Exception ex)
        {
            return new Report(frameworkIdentity, [], [],
                $"{ex.GetType().Name}: {ex.Message}");
        }
        if (packages.Count == 0)
            return new Report(frameworkIdentity, [], [],
                $"No node-repo packages (top-level folders with an index.json root) found under "
                + $"'{Path.GetFullPath(options.RepoRoot)}'.");

        // 🚨 ONE node set across EVERY package. A `shared=@Other/Lib/Source` query crosses package
        // boundaries and the runtime resolves it against the whole mesh; a per-package set would
        // resolve it to nothing and compile a short set that looks like the author's bug (#1218).
        // 🚨 A file that will not materialise is REPORTED, not fatal — because the RUNTIME skips it
        // too. `PackageInstaller` logs "No parser for node-repo file X; skipped" and installs the
        // rest, so a bake that refused here would resolve a SMALLER tree than the mesh and the two
        // producers would disagree — the equivalence break this change exists to prevent, just in
        // the opposite direction. (Measured on samples/Graph/Data: the mesh drops 62 of PensionFund's
        // 72 files and gates 0 NodeTypes there, because those files carry a UTF-8 BOM. This bake
        // drops exactly the same ones — it just says so.)
        //
        // Loud is the point. Every skip is printed and counted, so "the bake shipped less than the
        // tree holds" is visible in the log instead of being a silent shortfall.
        var skipped = new List<string>();
        var treeNodes = TreeNodeLoader.Load(
            snapshot, packages, (path, reason) => skipped.Add($"{path}: {reason}"));
        foreach (var skip in skipped)
            options.Output.WriteLine($"bake: skipped (not a materialisable node) {skip}");
        if (skipped.Count > 0)
            options.Output.WriteLine(
                $"bake: {skipped.Count} file(s) skipped — the runtime's importer skips these too; "
                + "a NodeType among them is a CONTENT defect, not a bake failure");

        var nodeSet = NodeSet.Create(treeNodes.Select(t => t.Node));
        options.Output.WriteLine(
            $"bake: {treeNodes.Length} node(s) from {packages.Count} package(s); "
            + $"framework={frameworkIdentity}");

        // 🚨 The bake compiles against the SAME reference set a portal does — the TPA baseline PLUS
        // this deployment's installed modules. No mesh here, but "no mesh" never meant "no modules":
        // a module published into modules/<name>/ is by construction absent from
        // TRUSTED_PLATFORM_ASSEMBLIES, so a bake using the bare Default set cannot see ONE module
        // type, while a portal — which composes them — compiles the very same content fine.
        //
        // That asymmetry hides itself. The gate's compile-check stands up a mesh and therefore
        // composes modules, so it goes GREEN; only publish-bake goes red, which reads as a bake
        // infrastructure failure rather than as a missing reference. It shipped exactly that way
        // when the AI engine left the content surface (#2276): Store/Installer's `AiSettings` calls
        // stopped resolving HERE and nowhere else, five Store NodeTypes went RED, no bundle was
        // sealed for the new framework identity, and every install then correctly declined to
        // self-update onto an image whose content had no bake (#2563) — a fleet held back by a
        // reference list, with nothing in the failure naming a reference.
        var modules = LoadExternalModules(options);
        var idOf = CompiledDependencies.CreateIdResolver(
            FrameworkBuildIdentity.ProcessSurfacePairs,
            // Modules resolve FIRST and by exact-build MVID, so the record a consumer checks names
            // the same build it will bind. Empty here (the old value) made a module-bound type
            // fall through to the platform branch and record `ref:`/absent, which cannot match the
            // `mvid:` a module-installing portal computes — the second half of #2563.
            ModuleMvidsOf(modules),
            FrameworkBuildIdentity.ProcessImplMvidOf);
        var toolchainId = CompiledDependencies.ComputeToolchainId(FrameworkBuildIdentity.ProcessImplMvidOf);
        var references = CompileReferences.ComposeWithModules(modules);

        // 🚨 The NuGet resolver is WIRED, not omitted. `#r "nuget:…"` is a compile input like any
        // other — samples/Graph/Data/MathDemo/Matrix declares `#r "nuget:MathNet.Numerics, 5.0.0"` —
        // and a bake without a resolver simply cannot build those types. That showed up as the ONE
        // divergence across the whole samples tree (23 of 24 NodeTypes agreed; MathDemo/Matrix was
        // the miss), which is exactly the class of shortfall this issue is about: a bake that
        // quietly produces less than the runtime would. NuGetAssemblyResolver needs nothing from a
        // mesh — a logger and an optional cache — so there was never a reason to leave it out.
        var nugetResolver = new NuGetAssemblyResolver(
            options.LoggerFactory?.CreateLogger<NuGetAssemblyResolver>()
            ?? NullLogger<NuGetAssemblyResolver>.Instance);

        var workDirectory = Path.Combine(
            Path.GetTempPath(), $"mw-compiler-bake-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            return BakeAll(
                options, treeNodes, nodeSet, packages, snapshot, frameworkIdentity,
                idOf, toolchainId, references, workDirectory, nugetResolver);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory costs disk, never correctness.
            }
        }
    }

    /// <summary>
    /// Loads the module entry assemblies the same way a mesh installs them —
    /// <see cref="Assembly.LoadFrom(string)"/>, so they land in the Default ALC, file-backed, and
    /// expose a <c>Location</c> the compiler can turn into a Roslyn <c>MetadataReference</c>.
    ///
    /// <para>🚨 A module that will not load is FATAL, never skipped. Skipping would produce the
    /// precise failure this whole change exists to remove: a bake that silently compiled without a
    /// module's types, reported the resulting misses as CONTENT errors, and named nothing about a
    /// reference. Loud here, once, beats five red NodeTypes and a fleet that will not roll.</para>
    /// </summary>
    /// <summary>
    /// The compiler's NuGet hook is synchronous by contract (<see cref="NodeSetCompiler.Compile"/>
    /// takes a plain delegate), and the resolver is genuinely async, so this is the ONE place the
    /// bake lane blocks on it. Shared with the build verb so the production blocking-bridge
    /// inventory keeps counting exactly one site, here.
    /// </summary>
    internal static Func<IReadOnlyList<NuGetPackageReference>, CancellationToken, IReadOnlyList<string>>
        BlockingNuGetResolution(INuGetAssemblyResolver resolver) =>
        (refs, ct) => resolver
            .ResolveAsync(refs, targetFramework: null, ct)
            .GetAwaiter().GetResult()
            .AssemblyPaths;

    internal static IReadOnlyList<InstalledModuleAssembly> LoadExternalModules(Options options)
    {
        var paths = options.ModuleAssemblyPaths;
        if (paths.Count == 0)
            return [];
        var loaded = new List<InstalledModuleAssembly>(paths.Count);
        foreach (var path in paths)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                // 🚨 Name BOTH provenances, because this list carries both and they have opposite
                // fixes: an image-shipped module missing is a broken image build, while a mounted
                // one missing is a bad path. Blaming `--module` for the first would be this PR's
                // own bug in miniature — an error naming the wrong cause. Mirrors the gate's
                // message (PluginGateRunner) so the two lanes explain a missing module alike.
                throw new InvalidOperationException(
                    $"bake: module '{path}' could not be loaded — {ex.Message}. Modules this image "
                    + "ships come from the MeshModulesPublish closure lane in "
                    + "MeshWeaver.PluginTester.csproj; modules passed with --module must exist at "
                    + "the absolute path given (mount them into the container). A bake whose "
                    + "modules are missing would resolve fewer types than the portal that consumes "
                    + "its bundles.", ex);
            }
            loaded.Add(new InstalledModuleAssembly(assembly));
            options.Output.WriteLine(
                $"bake: module {assembly.GetName().Name} "
                + $"mvid={assembly.ManifestModule.ModuleVersionId:N} — composed into the reference set");
        }
        return loaded;
    }

    /// <summary>Installed module simple name → implementation MVID ("N"). Deliberately the same
    /// projection the mesh makes (<c>NodeTypeCompilationHelpers.ModuleMvidsOf</c>): producer and
    /// consumer have to compute one id for one build, or every bundle is declined.</summary>
    internal static IReadOnlyDictionary<string, string> ModuleMvidsOf(
        IReadOnlyList<InstalledModuleAssembly> modules)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var name = module.Assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                map[name] = module.Mvid.ToString("N");
        }
        return map;
    }

    private static Report BakeAll(
        Options options,
        ImmutableArray<TreeNodeLoader.TreeNode> treeNodes,
        NodeSet nodeSet,
        IReadOnlyList<PackageManifest> packages,
        RepoSnapshot snapshot,
        string frameworkIdentity,
        Func<string, string?> idOf,
        string toolchainId,
        IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference> references,
        string workDirectory,
        INuGetAssemblyResolver nugetResolver)
    {
        var results = ImmutableArray.CreateBuilder<TypeResult>();
        var entriesByPackage = new Dictionary<string, List<BundleWriter.AssemblyEntry>>(StringComparer.Ordinal);

        var compilable = treeNodes
            .Where(t => t.Node.Content is NodeTypeDefinition)
            .OrderBy(t => t.Node.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in compilable)
        {
            var definition = (NodeTypeDefinition)candidate.Node.Content!;
            var resolution = nodeSet.ResolveSources(
                definition.Sources, definition.Tests, candidate.Node.Path);

            // A type with neither a configuration lambda nor any source has nothing to compile —
            // the same "Compiles" predicate the gate applies.
            //
            // 🚨 ESTABLISHED is a precondition of that judgement, not part of it. An unestablished
            // resolution means the bake could not evaluate one of the type's source queries — it
            // does NOT mean the type has no sources, and collapsing the two would be a
            // skip-trapdoor of exactly the shape CI forbids: a source-only type whose query this
            // evaluator cannot answer would vanish from the bake with no bundle entry, no RED and
            // no line anywhere saying a type was dropped. So an unestablished resolution falls
            // THROUGH to Compile, which throws SourceDiscoveryUnavailableException and lands in the
            // catch below as a failed type — loud, named, and non-zero exit.
            if (resolution.IsEstablished
                && resolution.Sources.IsEmpty
                && string.IsNullOrWhiteSpace(definition.Configuration))
                continue;

            try
            {
                var compiled = NodeSetCompiler.Compile(
                    nodeSet, candidate.Node, definition.Sources, definition.Tests,
                    definition.Configuration, definition.ContentCollections,
                    references, idOf, toolchainId,
                    Path.Combine(workDirectory, CodeConventions.SanitizeNodeName(candidate.Node.Path)),
                    // The ONE Task bridge in the bake, at a console build step — never on a hub
                    // scheduler. NuGet restore is genuine network IO with no reactive surface;
                    // the runtime bridges it through the IoPool for the same reason.
                    resolveNuGet: BlockingNuGetResolution(nugetResolver),
                    logger: options.Logger);

                // 🚨 The RAW resolved set, not the post-filter compile set — the mesh-driven bake
                // writes `NodeTypeDefinition.CompiledSources`, which DiscoverSourceVersionSnapshot
                // folds over the snapshot BEFORE CollectCompileSources drops executable cells and
                // blank files. Keeping the same semantics is what makes the two producers'
                // manifests comparable, and it is what the bake-equivalence test asserts on.
                //
                // The VALUES are 0, deliberately. The mesh stamps MeshNode.LastModified.UtcTicks,
                // which for a freshly imported tree is the INSTALL clock — not reproducible, not
                // derivable from git, and read by nobody: BundleReader skips the property and
                // PrebuiltAssemblySeeder re-stamps CompiledSources from the CONSUMER's own
                // CurrentSourceVersions on adopt. The key set is the provenance; the ticks were
                // never information.
                var sourceVersions = resolution.Sources
                    .Select(n => n.Path)
                    .ToImmutableSortedDictionary(p => p, _ => 0L, StringComparer.Ordinal);
                if (!entriesByPackage.TryGetValue(candidate.Package, out var entries))
                    entriesByPackage[candidate.Package] = entries = [];
                entries.Add(new BundleWriter.AssemblyEntry(
                    compiled.NodePath,
                    () => File.OpenRead(compiled.DllPath),
                    compiled.PdbPath is null ? null : () => File.OpenRead(compiled.PdbPath),
                    sourceVersions,
                    compiled.Dependencies)
                {
                    // 🚨 #2813 — the CONTENT fingerprint of the sources this compile consumed.
                    // `sourceVersions` above is provenance a reader SKIPS (its values are zeros);
                    // this is the value the consumer's adoption is decided on, and it is taken over
                    // the same RAW resolved set for the same reason: NodeTypeSourceFingerprint
                    // applies the shaping fold, so the bake and the runtime cannot fork on which
                    // files count.
                    //
                    // 🚨 #2948 — plus the `@@`-include closure, handed straight back by the compile
                    // that just substituted it (CompileInputs.ResolvedIncludes). No second walk, no
                    // second read, and no `.Wait()` at this synchronous build-step boundary.
                    SourceFingerprint = NodeTypeSourceFingerprint.Compute(
                        resolution.Sources, candidate.Node.Path,
                        compiled.Inputs.ResolvedIncludes, options.Logger),
                });

                results.Add(new TypeResult(
                    compiled.NodePath, candidate.Package, null, compiled.Inputs.MatchedSourcePaths)
                {
                    ResolvedIncludePaths = [.. compiled.Inputs.ResolvedIncludes.Keys],
                });
                options.Output.WriteLine(
                    $"   ok  {compiled.NodePath} "
                    + $"[{compiled.Inputs.MatchedSourcePaths.Length} source(s), "
                    + $"{compiled.Dependencies.Count} dependency record entr(ies)]");
            }
            // 🚨 A FAILING TYPE FAILS THAT TYPE — never the whole bake. The catch used to name
            // exactly two exception types, and anything else escaped BakeAll, unwound past the
            // bundle writer and killed the run: `mw-plugin-test: FATAL — …`, exit 70, zero bundles
            // written for the packages that compiled perfectly.
            //
            // Measured on samples/Graph/Data: `FatalProtocolException: The local source
            // 'dist/packages' doesn't exist` out of the NuGet resolver — a host-configuration
            // fault on ONE type with a `#r "nuget:"` directive — discarded a bake in which 23 of
            // 24 NodeTypes had already compiled. The mesh-driven producer contains exactly this
            // per type (the type settles at CompilationStatus.Error and the gate's ratchet decides
            // what that is worth), so an escaping exception here is also an EQUIVALENCE break: the
            // two producers must fail the same way, or a known-debt entry that the gate tolerates
            // becomes a total bake failure.
            //
            // Cancellation still propagates: it is the caller's decision to stop, not a verdict
            // about this type.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new TypeResult(
                    candidate.Node.Path, candidate.Package,
                    $"{ex.GetType().Name}: {ex.Message}", []));
                options.Output.WriteLine($"   RED {candidate.Node.Path}");
                options.Output.WriteLine(ex.Message);
            }
        }

        var bundles = WriteBundles(
            options, packages, snapshot, frameworkIdentity, entriesByPackage);
        return new Report(frameworkIdentity, results.ToImmutable(), bundles);
    }

    internal static ImmutableArray<string> WriteBundles(
        Options options,
        IReadOnlyList<PackageManifest> packages,
        RepoSnapshot snapshot,
        string frameworkIdentity,
        Dictionary<string, List<BundleWriter.AssemblyEntry>> entriesByPackage)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var manifestsById = packages.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var sourceSha = options.SourceSha ?? snapshot.CommitSha;
        var written = ImmutableArray.CreateBuilder<string>();

        foreach (var (packageId, entries) in entriesByPackage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (entries.Count == 0)
                continue;
            manifestsById.TryGetValue(packageId, out var manifest);
            var bundlePath = Path.Combine(options.OutputDirectory, SafeFileName(packageId) + ".zip");
            // Same producer rule as BakeOutput: a bundle ships the package it was compiled from,
            // or it cannot be installed as an upstream — see BakeOutput.NodeDefinitionsOf.
            var content = BakeOutput.NodeDefinitionsOf(snapshot, manifest, packageId);
            using (var file = File.Create(bundlePath))
                BundleWriter.Write(
                    file,
                    packageId,
                    manifest?.ReleasedVersion ?? manifest?.ModuleVersion ?? manifest?.Version,
                    frameworkIdentity,
                    [.. entries.OrderBy(e => e.NodePath, StringComparer.Ordinal)],
                    sourceSha,
                    content,
                    sourceIncluded: true);
            written.Add(bundlePath);
            options.Output.WriteLine(
                $"bake: {packageId} → {entries.Count} assembly(ies) + {content.Count} node file(s) → {bundlePath}");
        }

        File.WriteAllText(
            Path.Combine(options.OutputDirectory, FrameworkMvidFile), frameworkIdentity);
        options.Output.WriteLine(
            $"bake: framework={frameworkIdentity} source={sourceSha} "
            + $"packages={written.Count} assemblies={entriesByPackage.Values.Sum(e => e.Count)} "
            + $"→ {options.OutputDirectory}");
        return written.ToImmutable();
    }

    /// <summary>Identical to the mesh-driven bake's: package ids are folder names, bundle FILE
    /// names must be safe on every filesystem the artifact travels through.</summary>
    private static string SafeFileName(string packageId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(packageId.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
