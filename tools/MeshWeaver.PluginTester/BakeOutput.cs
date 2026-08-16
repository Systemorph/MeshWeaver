using System.Reactive.Linq;
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
                        using (var file = File.Create(bundlePath))
                            BundleWriter.Write(
                                file,
                                package.Id,
                                manifest?.ReleasedVersion ?? manifest?.ModuleVersion ?? manifest?.Version,
                                frameworkMvid,
                                entries.ToList(),
                                sourceSha);
                        options.Output.WriteLine(
                            $"bake: {package.Id} → {entries.Count} assembly(ies) → {bundlePath}");
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
                        // STREAMED, never buffered: the entry carries open-stream FACTORIES, so the
                        // bytes flow disk → zip inside BundleWriter (which opens and disposes each
                        // stream per entry) instead of the whole bake's DLL set sitting in byte[]s
                        // at once — that is the entire point of AssemblyEntry's factory shape.
                        // The factories run inside the zip-writing InvokeBlocking, on the same
                        // files pool as this existence probe.
                        return pool.InvokeBlocking(_ =>
                        {
                            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                            var hasPdb = File.Exists(pdbPath);
                            return new BundleWriter.AssemblyEntry(
                                typePath,
                                () => File.OpenRead(dllPath),
                                hasPdb ? () => File.OpenRead(pdbPath) : null,
                                def.CompiledSources);
                        });
                    });
            });

    /// <summary>Package ids are top-level folder names, but the bundle FILE name must be safe on
    /// every filesystem the artifact travels through.</summary>
    private static string SafeFileName(string packageId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(packageId.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
