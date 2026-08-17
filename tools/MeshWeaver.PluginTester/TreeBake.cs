using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
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
        var skipped = new List<string>();
        var treeNodes = TreeNodeLoader.Load(
            snapshot, packages, (path, reason) => skipped.Add($"{path}: {reason}"));
        if (skipped.Count > 0)
            return new Report(frameworkIdentity, [], [],
                "the checkout holds node file(s) that could not be materialised, so the source set "
                + "this bake would compile against is INCOMPLETE — refusing to emit bytes built "
                + "from a short set: " + string.Join("; ", skipped));

        var nodeSet = NodeSet.Create(treeNodes.Select(t => t.Node));
        options.Output.WriteLine(
            $"bake: {treeNodes.Length} node(s) from {packages.Count} package(s); "
            + $"framework={frameworkIdentity}");

        var idOf = CompiledDependencies.CreateIdResolver(
            FrameworkBuildIdentity.ProcessSurfacePairs,
            // No mesh ⇒ no installed modules. A deployment that installs modules keys its own
            // record on them; a type that binds one is DECLINED by the consumer's dependency check
            // rather than silently adopted, which is the safe direction.
            ImmutableDictionary<string, string>.Empty,
            FrameworkBuildIdentity.ProcessImplMvidOf);
        var toolchainId = CompiledDependencies.ComputeToolchainId(FrameworkBuildIdentity.ProcessImplMvidOf);
        var references = CompileReferences.Default;

        var workDirectory = Path.Combine(
            Path.GetTempPath(), $"mw-compiler-bake-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            return BakeAll(
                options, treeNodes, nodeSet, packages, snapshot, frameworkIdentity,
                idOf, toolchainId, references, workDirectory);
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
        string workDirectory)
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
                    resolveNuGet: null, logger: options.Logger);

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
                    compiled.Dependencies));

                results.Add(new TypeResult(
                    compiled.NodePath, candidate.Package, null, compiled.Inputs.MatchedSourcePaths));
                options.Output.WriteLine(
                    $"   ok  {compiled.NodePath} "
                    + $"[{compiled.Inputs.MatchedSourcePaths.Length} source(s), "
                    + $"{compiled.Dependencies.Count} dependency record entr(ies)]");
            }
            catch (Exception ex) when (ex is CompilationException or SourceDiscoveryUnavailableException)
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

    private static ImmutableArray<string> WriteBundles(
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
            using (var file = File.Create(bundlePath))
                BundleWriter.Write(
                    file,
                    packageId,
                    manifest?.ReleasedVersion ?? manifest?.ModuleVersion ?? manifest?.Version,
                    frameworkIdentity,
                    [.. entries.OrderBy(e => e.NodePath, StringComparer.Ordinal)],
                    sourceSha);
            written.Add(bundlePath);
            options.Output.WriteLine(
                $"bake: {packageId} → {entries.Count} assembly(ies) → {bundlePath}");
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
