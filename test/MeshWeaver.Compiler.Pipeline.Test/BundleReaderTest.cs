#pragma warning disable CS1591

using System.Text;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the bundle round trip — what <see cref="NuGetPackageWriter"/> writes,
/// <see cref="BundleReader"/> must read back.
///
/// <para>The two live in one library precisely so this holds, and it is worth pinning because every
/// way it can break is silent: a consumer that recovers the wrong node path seeds correct bytes
/// against the wrong NodeType, and the mismatch does not surface until activation throws a
/// <c>TypeLoadException</c> inside a collectible ALC — no compile error, no overlay, nothing to
/// grep.</para>
/// </summary>
public class BundleReaderTest
{
    private static readonly PluginManifest Manifest =
        new("ThreeBody", "MeshWeaver.Plugin.ThreeBody", "1.3.2", "ThreeBody", null, []);

    private static byte[] WriteBundle(params (string NodePath, byte[] Bytes)[] assemblies)
    {
        var entries = assemblies.Select(a => new NuGetPackageWriter.Entry(
            NuGetPackageWriter.EntryPathFor(a.NodePath),
            () => new MemoryStream(a.Bytes))).ToArray();

        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            assemblies = assemblies
                .Select(a => new { nodePath = a.NodePath, assembly = $"{a.NodePath}.dll" })
                .ToArray(),
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", entries, manifestJson);
        return buffer.ToArray();
    }

    [Fact]
    public void APayloadComesBackAgainstItsOwnNodePath()
    {
        var bundle = WriteBundle(("ThreeBody/Physics/Source", "PHYSICS"u8.ToArray()));

        var (manifest, assemblies) = BundleReader.Read(bundle);

        Assert.Equal("ThreeBody", manifest!.Plugin);
        Assert.Equal("1.3.2", manifest.Version);
        Assert.Equal("33f2efb8aaaabbbbccccddddeeeeffff", manifest.FrameworkMvid);

        var only = Assert.Single(assemblies);
        Assert.Equal("ThreeBody/Physics/Source", only.NodePath);
        Assert.Equal("PHYSICS", Encoding.UTF8.GetString(only.Assembly));
        Assert.Null(only.Pdb);
    }

    [Theory]
    [InlineData("A/B/C", "meshweaver/assemblies/A/B/C.dll")]
    [InlineData("A_B/C", "meshweaver/assemblies/A_B/C.dll")]
    public void TheEntryPathIsTheNodePathVerbatim(string nodePath, string expected)
        // 🚨 The producer-side half of the collision guard: slash-replacing maps BOTH of these to
        // `A_B_C`. Pinned here rather than only in the round trip, because a writer that sanitises
        // produces a bundle the reader still parses happily — it just has one entry where two
        // belong, and the loser's NodeType silently adopts the winner's assembly.
        => Assert.Equal(expected, NuGetPackageWriter.EntryPathFor(nodePath));

    [Fact]
    public void PathsThatWouldCollideWhenSanitisedStaySeparate()
    {
        // 🚨 The regression this guards. Replacing '/' with '_' maps BOTH of these to
        // `ThreeBody_A_B` — one archive entry, one set of bytes, and the second NodeType silently
        // adopts the first one's assembly. Mesh paths do contain underscores, so this is reachable,
        // not theoretical.
        var bundle = WriteBundle(
            ("ThreeBody/A/B", "FIRST"u8.ToArray()),
            ("ThreeBody/A_B", "SECOND"u8.ToArray()));

        var (_, assemblies) = BundleReader.Read(bundle);

        Assert.Equal(2, assemblies.Count);
        Assert.Equal("FIRST", Encoding.UTF8.GetString(
            assemblies.Single(a => a.NodePath == "ThreeBody/A/B").Assembly));
        Assert.Equal("SECOND", Encoding.UTF8.GetString(
            assemblies.Single(a => a.NodePath == "ThreeBody/A_B").Assembly));
    }

    [Fact]
    public void AnAssemblyTheManifestNamesButTheArchiveLacksIsSkipped()
    {
        // Truncated or partially-assembled bundle: the rest must stay usable, because the NodeType
        // whose bytes are missing simply compiles — which is the behaviour without any bundle at all.
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[]
            {
                new { nodePath = "ThreeBody/Present", assembly = "ThreeBody/Present.dll" },
                new { nodePath = "ThreeBody/Absent", assembly = "ThreeBody/Absent.dll" },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/Present.dll",
                    () => new MemoryStream("HERE"u8.ToArray())),
            ],
            manifestJson);

        var (_, assemblies) = BundleReader.Read(buffer.ToArray());

        var only = Assert.Single(assemblies);
        Assert.Equal("ThreeBody/Present", only.NodePath);
    }

    [Fact]
    public void SymbolsRideAlongWhenPresent()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "ThreeBody",
            version = "1.3.2",
            frameworkMvid = "33f2efb8",
            assemblies = new[] { new { nodePath = "ThreeBody/X", assembly = "ThreeBody/X.dll" } },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.dll",
                    () => new MemoryStream("DLL"u8.ToArray())),
                new NuGetPackageWriter.Entry(
                    $"{NuGetPackageWriter.AssemblyFolder}/ThreeBody/X.pdb",
                    () => new MemoryStream("PDB"u8.ToArray())),
            ],
            manifestJson);

        var only = Assert.Single(BundleReader.Read(buffer.ToArray()).Assemblies);

        Assert.Equal("PDB", Encoding.UTF8.GetString(only.Pdb!));
    }

    // ── the MODULE variant (#1664): one bundle, one reader, a second lane ──

    private static byte[] WriteModuleBundle(
        object module, params (string FileName, byte[] Bytes)[] moduleFiles)
    {
        var entries = moduleFiles.Select(f => new NuGetPackageWriter.Entry(
            NuGetPackageWriter.ModuleEntryPathFor(f.FileName),
            () => new MemoryStream(f.Bytes))).ToArray();

        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SocialMedia",
            version = "1.2.0",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            module,
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", entries, manifestJson);
        return buffer.ToArray();
    }

    [Fact]
    public void AModuleBundleRoundTrips()
    {
        var bundle = WriteModuleBundle(
            new
            {
                assemblyName = "MeshWeaver.Social",
                assemblies = new[] { "MeshWeaver.Social.dll" },
                minMeshVersion = "3.0.0",
            },
            ("MeshWeaver.Social.dll", "SOCIAL"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.Equal("MeshWeaver.Social", manifest!.Module!.AssemblyName);
        // The consumer's landing gate — a platform FLOOR, not the manifest-level frameworkMvid
        // (which stays the NodeType lane's strict gate and, for the module, diagnostics).
        Assert.Equal("3.0.0", manifest.Module.MinMeshVersion);
        var only = Assert.Single(files);
        Assert.Equal("MeshWeaver.Social.dll", only.FileName);
        Assert.Equal("SOCIAL", Encoding.UTF8.GetString(only.Bytes));
    }

    [Fact]
    public void AMixedBundleServesBothLanes()
    {
        // The MeshWeaver.SocialMedia shape: content nodes + NodeType assemblies + a compiled module
        // in ONE Store product — so one bundle must serve both readers without either seeing the
        // other's payload.
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SocialMedia",
            version = "1.2.0",
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            assemblies = new[] { new { nodePath = "SocialMedia/Post", assembly = "SocialMedia/Post.dll" } },
            module = new { assemblyName = "MeshWeaver.Social", assemblies = new[] { "MeshWeaver.Social.dll" } },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.EntryPathFor("SocialMedia/Post"),
                    () => new MemoryStream("NODETYPE"u8.ToArray())),
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Social.dll"),
                    () => new MemoryStream("MODULE"u8.ToArray())),
            ],
            manifestJson);
        var bundle = buffer.ToArray();

        var nodeTypes = Assert.Single(BundleReader.Read(bundle).Assemblies);
        Assert.Equal("SocialMedia/Post", nodeTypes.NodePath);
        Assert.Equal("NODETYPE", Encoding.UTF8.GetString(nodeTypes.Assembly));

        var moduleFile = Assert.Single(BundleReader.ReadModule(bundle).Files);
        Assert.Equal("MODULE", Encoding.UTF8.GetString(moduleFile.Bytes));
    }

    [Fact]
    public void AnIncompleteModuleClosureYieldsNoFilesAtAll()
    {
        // All-or-nothing, deliberately UNLIKE the NodeType lane's skip: a NodeType with missing
        // bytes simply compiles, but a module folder missing part of its closure loads and then
        // faults at first use — a subset must never land.
        var bundle = WriteModuleBundle(
            new
            {
                assemblyName = "MeshWeaver.Social",
                assemblies = new[] { "MeshWeaver.Social.dll", "MeshWeaver.Social.Support.dll" },
            },
            ("MeshWeaver.Social.dll", "SOCIAL"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.NotNull(manifest!.Module);
        Assert.Empty(files);
    }

    [Fact]
    public void ANodeTypeOnlyBundleDeclaresNoModule()
    {
        var bundle = WriteBundle(("ThreeBody/Physics/Source", "PHYSICS"u8.ToArray()));

        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.Null(manifest!.Module);
        Assert.Empty(files);
    }

    [Fact]
    public void AnArchiveWithoutAManifestYieldsNothing()
    {
        // Not an exception: a consumer must treat "cannot read this" the same as "no bundle" and
        // compile. Throwing here would turn a distribution problem into a failed install.
        var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("readme.txt").Open();
            stream.Write("not a bundle"u8);
        }

        var (manifest, assemblies) = BundleReader.Read(buffer.ToArray());

        Assert.Null(manifest);
        Assert.Empty(assemblies);
    }

    [Fact]
    public void AManifestWithNoFrameworkIdentityReadsAsNull()
    {
        // The seeder DECLINES on a null MVID, so this value must survive as null rather than
        // becoming an empty string that a careless comparison could treat as a match.
        var manifestJson = JsonSerializer.Serialize(new { plugin = "ThreeBody", version = "1.3.2" });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, Manifest, "3.0.0", [], manifestJson);

        Assert.Null(BundleReader.Read(buffer.ToArray()).Manifest!.FrameworkMvid);
    }
}
