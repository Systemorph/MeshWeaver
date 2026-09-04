using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// ONE PRODUCER IN TIME (#3244): a bundle download must carry the build of the module its own
/// assemblies were compiled against.
///
/// <para>🚨 <b>Why this is pinned as a pure decision.</b> Every branch of it fails SILENTLY. Serving
/// the shelf's build beside assemblies sealed against another one returns 200, writes a
/// well-formed archive, lands cleanly on the consumer — and is then declined type by type at the
/// consumer's boot ("dependency record mismatch — built against mvid:…, live is mvid:…"), which is
/// how memex.meshweaver.cloud adopted 0 of 4 SocialMedia bundles on ci.7621 with no diff in any
/// repo. Nothing upstream of the consumer's log can see it: <c>ReleaseAvailability</c> compares the
/// SEALED module set against the sealed bundles' records and passes, because both halves it reads
/// come from the same publication while the instance runs the registry's content-versioned shelf.
/// Same reasoning, same shape, as <see cref="PluginBundleLaneResolutionTest"/> for the NodeType half
/// of the very same handler.</para>
///
/// <para>The MVIDs are REAL: each fixture's module bytes are a genuine assembly this test process
/// has loaded, so the PE reads under test read what they would read in production. Two different
/// assemblies give two different builds without having to fabricate metadata.</para>
/// </summary>
public class ServedModuleBytesTest : IDisposable
{
    private const string Module = "MeshWeaver.Widget";
    private const string Package = "Widget";
    private const string Identity = "s0123456789abcdef0123456789abcdef";
    private const string Source = "plugins";

    /// <summary>The build the SEAL carries — what the served assemblies will record.</summary>
    private static byte[] SealedBytes => File.ReadAllBytes(typeof(ReleaseAvailability).Assembly.Location);

    private static string SealedMvid => MvidOf(typeof(ReleaseAvailability).Assembly);

    /// <summary>A DIFFERENT build — what the registry's own content-versioned shelf holds.</summary>
    private static byte[] ShelfBytes => File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

    private static string ShelfMvid => MvidOf(typeof(BundleReader).Assembly);

    private static string MvidOf(System.Reflection.Assembly assembly) =>
        CompiledDependencies.MvidScheme + assembly.ManifestModule.ModuleVersionId.ToString("N");

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-served-module-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// THE defect. The shelf holds one build, the assemblies about to be shipped record another, and
    /// the publication sealed for the caller's identity carries the recorded one — so the recorded
    /// one is what goes in the archive.
    /// </summary>
    [Fact]
    public void WhenTheShelfIsNotTheRecordedBuild_TheSealedBuildIsServed()
    {
        var shelf = Shelf(ShelfBytes);
        PublishedRoot(bundleName: ServedModuleBytes.BundleNameFor(Package), moduleBytes: SealedBytes);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(SealedMvid, null),
            PublishedRootPath, Identity);

        Assert.Null(served.Divergence);
        Assert.Equal(Module + ".dll", served.Files[0].FileName);
        Assert.Equal(SealedBytes, Read(served.Files[0]));
        Assert.Contains(Source, served.Provenance, StringComparison.Ordinal);
        Assert.Contains(Identity, served.Provenance, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seal is found even when the module bundle is NOT named by the pack lane's convention —
    /// the naming is a contract of the producing lane, and a control that could only see
    /// <c>&lt;lower(package)&gt;.module.nupkg</c> would report "no matching build" for a seal that
    /// carries exactly the right bytes under another name.
    /// </summary>
    [Fact]
    public void TheSealedBuildIsFound_EvenUnderAnUnconventionalBundleName()
    {
        var shelf = Shelf(ShelfBytes);
        PublishedRoot(bundleName: "essentials.module.nupkg", moduleBytes: SealedBytes);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(SealedMvid, null),
            PublishedRootPath, Identity);

        Assert.Null(served.Divergence);
        Assert.Equal(SealedBytes, Read(served.Files[0]));
    }

    /// <summary>
    /// The healthy case, and the one that must not change: the shelf already IS the recorded build,
    /// so the shelf's own bytes are served and no publication is consulted.
    /// </summary>
    [Fact]
    public void WhenTheShelfIsTheRecordedBuild_TheShelfIsServed()
    {
        var shelf = Shelf(ShelfBytes);
        PublishedRoot(bundleName: ServedModuleBytes.BundleNameFor(Package), moduleBytes: SealedBytes);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(ShelfMvid, null),
            PublishedRootPath, Identity);

        Assert.Null(served.Divergence);
        Assert.Equal(ShelfBytes, Read(served.Files[0]));
        Assert.Contains("shelf", served.Provenance, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the bundle binds the module, so nothing constrains which build ships: the shelf
    /// answers exactly as it did before this decision existed. A module-only package has no NodeType
    /// records at all, and re-pointing it at a seal would be a change with no evidence behind it.
    /// </summary>
    [Fact]
    public void WhenNothingRecordsTheModule_TheShelfIsServedUnchanged()
    {
        var shelf = Shelf(ShelfBytes);
        PublishedRoot(bundleName: ServedModuleBytes.BundleNameFor(Package), moduleBytes: SealedBytes);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(null, null),
            PublishedRootPath, Identity);

        Assert.Null(served.Divergence);
        Assert.Equal(ShelfBytes, Read(served.Files[0]));
    }

    /// <summary>
    /// No publication for this identity carries the recorded build. The bytes served are unchanged —
    /// the registry cannot conjure a build it does not hold — but the answer is NAMED, quoting both
    /// MVIDs, so "this lane cannot be satisfied" is stated by the producer instead of being
    /// discovered as a per-type decline in a consumer's log.
    /// </summary>
    [Fact]
    public void WhenNoSealCarriesTheRecordedBuild_TheDivergenceIsNamed()
    {
        var shelf = Shelf(ShelfBytes);
        PublishedRoot(
            bundleName: ServedModuleBytes.BundleNameFor(Package),
            moduleBytes: ShelfBytes);
        var absent = CompiledDependencies.MvidScheme + new string('a', 32);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(absent, null),
            PublishedRootPath, Identity);

        Assert.NotNull(served.Divergence);
        Assert.Contains(absent, served.Divergence!, StringComparison.Ordinal);
        Assert.Contains(ShelfMvid, served.Divergence!, StringComparison.Ordinal);
        Assert.Equal(ShelfBytes, Read(served.Files[0]));
    }

    /// <summary>
    /// A deployment that mounts no published bundle root has one producer by construction — the
    /// decision must not fault there, it must answer the shelf.
    /// </summary>
    [Fact]
    public void WithNoPublishedRoot_TheShelfIsServed()
    {
        var shelf = Shelf(ShelfBytes);

        var served = ServedModuleBytes.Resolve(
            Module, Package, shelf.Files, shelf.Assets,
            new RecordedModuleId(SealedMvid, null),
            publishedRoot: null, Identity);

        Assert.NotNull(served.Divergence);
        Assert.Equal(ShelfBytes, Read(served.Files[0]));
    }

    /// <summary>
    /// Two assemblies in one bundle recording two builds of one module cannot both be satisfied, so
    /// the answer is the named disagreement rather than a silent pick — choosing either would
    /// decline the other's NodeTypes.
    /// </summary>
    [Fact]
    public void AssembliesRecordingTwoBuilds_AreNamedRatherThanResolved()
    {
        var recorded = ServedModuleBytes.RecordedFor(
            [
                new Dictionary<string, string>(StringComparer.Ordinal) { [Module] = SealedMvid },
                new Dictionary<string, string>(StringComparer.Ordinal) { [Module] = ShelfMvid },
            ],
            Module);

        Assert.Null(recorded.Mvid);
        Assert.NotNull(recorded.Disagreement);
        Assert.Contains(SealedMvid, recorded.Disagreement!, StringComparison.Ordinal);
        Assert.Contains(ShelfMvid, recorded.Disagreement!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reading that drives everything above: the single <c>mvid:</c> the served assemblies agree
    /// on. Reserved '!' keys and <c>ref:</c> ids are not module builds and must not be read as one.
    /// </summary>
    [Fact]
    public void RecordedFor_ReadsTheAgreedModuleBuildAndNothingElse()
    {
        var recorded = ServedModuleBytes.RecordedFor(
            [
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Module] = SealedMvid,
                    ["MeshWeaver.Graph"] = CompiledDependencies.RefAsmScheme + "surface",
                    [CompiledDependencies.ToolchainKey] = CompiledDependencies.MvidScheme + "toolchain",
                },
                new Dictionary<string, string>(StringComparer.Ordinal) { [Module] = SealedMvid },
                null,
            ],
            Module);

        Assert.Equal(SealedMvid, recorded.Mvid);
        Assert.Null(recorded.Disagreement);

        var refOnly = ServedModuleBytes.RecordedFor(
            [
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Module] = CompiledDependencies.RefAsmScheme + "surface",
                },
            ],
            Module);
        Assert.Null(refOnly.Mvid);
        Assert.Null(refOnly.Disagreement);
    }

    private string PublishedRootPath => Path.Combine(root, "published");

    /// <summary>This registry's own <c>modules/</c> shelf, holding one build of the module.</summary>
    private (IReadOnlyList<string> Files, IReadOnlyList<(string RelativePath, string FullPath)> Assets)
        Shelf(byte[] bytes)
    {
        var folder = Path.Combine(root, "shelf", "modules", Module);
        Directory.CreateDirectory(folder);
        var entry = Path.Combine(folder, Module + ".dll");
        File.WriteAllBytes(entry, bytes);
        return ([entry], []);
    }

    /// <summary>A SEALED publication for <see cref="Identity"/> whose module set carries one bundle
    /// declaring <see cref="Module"/> at the given build.</summary>
    private void PublishedRoot(string bundleName, byte[] moduleBytes)
    {
        var source = Path.Combine(PublishedRootPath, Identity, Source);
        var modules = Path.Combine(source, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);

        WriteZip(
            Path.Combine(modules, bundleName),
            (NuGetPackageWriter.ManifestEntry,
                Encoding.UTF8.GetBytes($$$"""{"plugin":"{{{Package}}}","module":{"assemblyName":"{{{Module}}}"}}""")),
            ($"{NuGetPackageWriter.ModuleFolder}/{Module}.dll", moduleBytes));
        File.WriteAllLines(
            Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName), [bundleName]);

        // The seal itself — SealedModulesOf refuses a module set whose publication is torn.
        WriteZip(
            Path.Combine(source, Package + ".zip"),
            (NuGetPackageWriter.ManifestEntry, Encoding.UTF8.GetBytes("{}")));
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName), Package + ".zip\n");
    }

    private static byte[] Read(ServedModuleFile file)
    {
        using var stream = file.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteZip(string path, params (string Entry, byte[] Bytes)[] entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
    }
}
