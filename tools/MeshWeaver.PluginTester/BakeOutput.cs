using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The ARTIFACT half of the content gate (issue #1660 WS1): after the gate has compiled a
/// checkout's NodeTypes, persist the resulting assemblies as prebuilt-assembly bundles — one
/// <c>&lt;package&gt;.zip</c> per package, written with <see cref="BundleWriter"/>, keyed to the
/// run's framework MVID — so the SAME compile that proves the content also produces the bytes a
/// consumer (<c>PrebuiltAssemblySeeder</c> via the boot-time shipped-bundle seeder, the registry
/// bundle lane) can load instead of re-compiling.
///
/// <para><b>Only <see cref="CompilationStatus.Ok"/> types contribute.</b> A type that failed —
/// including known-debt failures the allowlist tolerates — has no usable build to ship, so it is
/// simply absent from the bundle and the consumer compiles it as it would have anyway. A type that
/// claims Ok but has no bytes in the run's assembly store is an INCONSISTENCY and faults the run:
/// an artifact stage that quietly ships less than it claims is the skip-trapdoor shape CI forbids.</para>
/// </summary>
public static class BakeOutput
{
    /// <summary>
    /// The package's own NODE DEFINITION files, as the bundle's content entries — every file under
    /// the package's source folder, path relative to that folder, bytes streamed from the snapshot.
    ///
    /// <para>🚨 <b>This is what turns an assemblies-only bundle into an installable upstream, and
    /// until now the bake never emitted it.</b> <c>BundleReader.Manifest.Content</c> has said all
    /// along that "a consumer that means to USE this package as an upstream needs these: the bytes
    /// only stamp nodes that already exist, so without the definitions there is nothing to stamp".
    /// The writer accepted them; both bake call sites passed nothing. So every published bundle
    /// was assemblies-only, which is exactly why <c>node-repo-publish-bake</c>'s <c>stage-repo</c>
    /// input carries the note "staging cannot be dropped until a published artifact carries the
    /// upstream's NODE DEFINITIONS and not just its compiled assemblies" — and why every plugin
    /// repo still stages its dependencies as SOURCE and recompiles them. Measured 2026-08-27:
    /// core passes <c>--seed</c> in 9 places, every plugin repo in 0.</para>
    ///
    /// <para>The whole tree ships — nodes, source and assets — because the consumer installs the
    /// package, and an install needs the package. <c>SourceIncluded</c> is declared <c>true</c> for
    /// the same reason; it is a declaration rather than an inference (see its remarks) and this
    /// producer withholds nothing. Binary files ride as bytes; the writer refuses any path that is
    /// not package-relative, so a snapshot path that escapes the folder is a producer error
    /// surfaced here, not a bundle that a consumer must refuse.</para>
    /// </summary>
    /// <param name="snapshot">The repo tree the bake ran over.</param>
    /// <param name="package">The package whose tree to collect.</param>
    /// <returns>Content entries, ordinal by relative path — empty when the snapshot holds no files
    /// under the package (the caller then writes an assemblies-only bundle exactly as before).</returns>
    public static IReadOnlyList<BundleWriter.ContentEntry> NodeDefinitionsOf(
        RepoSnapshot snapshot, PackageManifest? package, string packageId)
    {
        var prefix = (package?.SourceFolder ?? packageId) + "/";
        return snapshot.Files
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.Length > prefix.Length)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new BundleWriter.ContentEntry(
                f.Path[prefix.Length..],
                () => new MemoryStream(f.Bytes, writable: false)))
            .ToList();
    }

    /// <summary>Bound on reading one compiled type's record back — the compile has already settled,
    /// so this is a replay off the shared stream handle, not a wait for work.</summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>The file beside the bundles naming the framework identity every bundle in the
    /// directory is keyed to — what CI reads to name the MVID-keyed artifact.</summary>
    public const string FrameworkMvidFile = "framework-mvid.txt";

    /// <summary>
    /// Persists the run's compiled assemblies into <see cref="GateOptions.BakeOutputDirectory"/>
    /// (no-op when unset or when the run died before producing a verdict), then emits the report
    /// unchanged. Cold; a persist fault propagates so the caller's Catch turns it into a
    /// <see cref="GateReport.FatalError"/> — RED, never a silent partial artifact.
    /// </summary>
    public static IObservable<GateReport> Persist(
        IMessageHub mesh,
        GateOptions options,
        RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages,
        GateReport report)
    {
        if (options.BakeOutputDirectory is not { Length: > 0 } outputDirectory
            || report.FatalError is not null)
            return Observable.Return(report);

        var pool = mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get("plugin-test:files");
        var store = mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        var workspace = mesh.GetWorkspace();
        var frameworkMvid = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        var sourceSha = options.SourceSha ?? snapshot.CommitSha;
        var manifestsById = packages.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        var perPackage = report.Packages
            .Select(package => Observable.Defer(() =>
            {
                var compiled = package.NodeTypes
                    .Where(t => t.CompilationStatus == CompilationStatus.Ok)
                    .Select(t => t.Path)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (compiled.Count == 0)
                    return Observable.Return(0);

                manifestsById.TryGetValue(package.Id, out var manifest);
                return compiled
                    .Select(typePath => CollectOne(workspace, store, pool, typePath))
                    .Concat()
                    .ToList()
                    .SelectMany(entries => pool.InvokeBlocking(_ =>
                    {
                        Directory.CreateDirectory(outputDirectory);
                        var bundlePath = Path.Combine(outputDirectory, SafeFileName(package.Id) + ".zip");
                        var content = NodeDefinitionsOf(snapshot, manifest, package.Id);
                        using (var file = File.Create(bundlePath))
                            BundleWriter.Write(
                                file,
                                package.Id,
                                manifest?.ReleasedVersion ?? manifest?.ModuleVersion ?? manifest?.Version,
                                frameworkMvid,
                                entries.ToList(),
                                sourceSha,
                                content,
                                sourceIncluded: true);
                        options.Output.WriteLine(
                            $"bake: {package.Id} → {entries.Count} assembly(ies) + {content.Count} node file(s) → {bundlePath}");
                        return entries.Count;
                    }));
            }))
            .ToList();

        return perPackage
            .ToObservable()
            .Concat()
            .ToList()
            .SelectMany(counts => pool.InvokeBlocking(_ =>
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, FrameworkMvidFile), frameworkMvid);
                options.Output.WriteLine(
                    $"bake: framework={frameworkMvid} source={sourceSha} "
                    + $"packages={counts.Count(c => c > 0)} assemblies={counts.Sum()} → {outputDirectory}");
                return report;
            }));
    }

    /// <summary>
    /// One compiled type's bundle entry: read the stamped record back (a replay — the compile gate
    /// already saw it settle), resolve the bytes the compile put in the run's assembly store, and
    /// bind them to the node path plus the source snapshot the compile consumed.
    /// </summary>
    private static IObservable<BundleWriter.AssemblyEntry> CollectOne(
        IWorkspace workspace,
        IAssemblyStore store,
        IIoPool pool,
        string typePath)
        => workspace.GetMeshNodeStream(typePath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(ReadBudget)
            .SelectMany(node =>
            {
                var def = node!.ContentAs<NodeTypeDefinition>(workspace.Hub.JsonSerializerOptions);
                if (def?.LastCompiledVersion is not { } version)
                    throw new InvalidOperationException(
                        $"bake: '{typePath}' settled at CompilationStatus.Ok but records no "
                        + "LastCompiledVersion — the bake cannot name the bytes it would ship");
                return store
                    .TryGetAssemblyPath(typePath, version)
                    .Take(1)
                    .SelectMany(dllPath =>
                    {
                        if (string.IsNullOrEmpty(dllPath))
                            throw new InvalidOperationException(
                                $"bake: '{typePath}' claims a usable build at v{version} but the "
                                + "run's assembly store has NO bytes for it — refusing to write a "
                                + "bundle that ships less than the gate verdict claims");
                        // 🚨 #2813 — the CONTENT fingerprint of the sources these bytes came from,
                        // read through the SAME shared query the runtime's sources watcher reads
                        // (NodeSources.GetSources, System-scoped for the same reason: source-set
                        // discovery is framework infrastructure, and a per-user read routes a
                        // CheckPermission back into the grain being read). Computing it here rather
                        // than folding def.CompiledSources is the whole point — CompiledSources is
                        // path→ticks, and ticks are the consumer's install clock, not content.
                        return SourceFingerprintOf(workspace, def, typePath)
                            .SelectMany(fingerprint => pool.InvokeBlocking(_ =>
                            {
                                // STREAMED, never buffered: the entry carries open-stream
                                // FACTORIES, so the bytes flow disk → zip inside BundleWriter
                                // (which opens and disposes each stream per entry) instead of the
                                // whole bake's DLL set sitting in byte[]s at once — that is the
                                // entire point of AssemblyEntry's factory shape. The factories run
                                // inside the zip-writing InvokeBlocking, on the same files pool as
                                // this existence probe.
                                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                                var hasPdb = File.Exists(pdbPath);
                                return new BundleWriter.AssemblyEntry(
                                    typePath,
                                    () => File.OpenRead(dllPath),
                                    hasPdb ? () => File.OpenRead(pdbPath) : null,
                                    def.CompiledSources,
                                    def.CompiledDependencies)
                                {
                                    SourceFingerprint = fingerprint,
                                };
                            }));
                    });
            });

    /// <summary>
    /// The live source set's fingerprint for one NodeType (#2813) — the mesh-driven producer's half
    /// of the value a consumer compares against its own.
    ///
    /// <para>Reads the CANONICAL shared query rather than re-deriving a source set: this is the
    /// same <c>Replay(1).RefCount()</c> upstream the owning hub's sources watcher already holds
    /// open, so the value computed here is by construction the value THAT hub computes into
    /// <c>NodeTypeDefinition.CurrentSourceFingerprint</c>. <c>BakeEquivalenceTest</c> is what pins
    /// the compiler-driven bake to it.</para>
    /// </summary>
    private static IObservable<string> SourceFingerprintOf(
        IWorkspace workspace, NodeTypeDefinition def, string typePath)
    {
        var access = workspace.Hub.ServiceProvider.GetService<AccessService>();
        // 🚨 RunAsSystem, NEVER Observable.Using(access.ImpersonateAsSystem, …). Impersonation is
        // an AsyncLocal store/restore pair, and Rx runs Using's resource factory on the SUBSCRIBING
        // thread while disposing it when the inner observable TERMINATES — a different thread for a
        // cross-hub read like this one. The subscriber is then left running as System (#1790).
        // RunAsSystem seals both ends inside the one Subscribe, so the cold read below still issues
        // impersonated while nothing downstream inherits the identity.
        // ImpersonationScopeSiteRatchetGuard fails a NEW site carrying the old shape, and this file
        // is not on its allow list — deliberately, because it is new code.
        return access
            .RunAsSystem(() => NodeSources.GetSources(workspace, def, typePath))
            .Take(1)
            .Timeout(ReadBudget)
            .Select(sources => NodeTypeSourceFingerprint.Compute(sources, typePath));
    }

    /// <summary>Package ids are top-level folder names, but the bundle FILE name must be safe on
    /// every filesystem the artifact travels through.</summary>
    private static string SafeFileName(string packageId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(packageId.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
