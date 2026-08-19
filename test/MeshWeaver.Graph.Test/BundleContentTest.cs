#pragma warning disable CS1591

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the NODE-DEFINITION half of a bundle — the half that makes a published package usable as
/// an upstream instead of merely fast.
///
/// <para>Why this is worth its own suite: a bundle carrying assemblies alone LOOKS complete. It
/// reads back, its manifest validates, its bytes seed. What it cannot do is stand in for a source
/// checkout, because seeding an assembly only stamps a node that already exists — so a consumer
/// that trusted it would find its roots unbound at activation, far from here. Every assertion below
/// is about the difference between "the bundle read back" and "the package is actually there".</para>
/// </summary>
public class BundleContentTest
{
    private const string Mvid = "33f2efb8aaaabbbbccccddddeeeeffff";

    private static BundleWriter.ContentEntry File(string path, string body) =>
        new(path, () => new MemoryStream(Encoding.UTF8.GetBytes(body)));

    private static byte[] WriteBundle(params BundleWriter.ContentEntry[] content)
    {
        var buffer = new MemoryStream();
        BundleWriter.Write(
            buffer, "Edu", "1.4.0", Mvid,
            [new BundleWriter.AssemblyEntry("Edu/Module", () => new MemoryStream("MODULE"u8.ToArray()))],
            sourceSha: "0eaeb06",
            content: content);
        return buffer.ToArray();
    }

    [Fact]
    public void TheTreeComesBackVerbatim_PathsAndBytes()
    {
        var bundle = WriteBundle(
            File("index.json", """{"nodeType":"Store/Plugin"}"""),
            File("Module.json", """{"nodeType":"NodeType"}"""),
            File("Module/Source/ModuleContent.cs", "public record ModuleContent;"));

        var (manifest, files) = BundleReader.ReadContent(bundle);

        Assert.Equal("Edu", manifest!.Plugin);
        Assert.Equal(3, files.Count);
        // Nested paths survive: the consumer recreates a TREE, not a flat folder. A producer that
        // sanitised the separator would collide Module/Source/X.cs with Module.Source.X.cs.
        var source = Assert.Single(files, f => f.RelativePath == "Module/Source/ModuleContent.cs");
        Assert.Equal("public record ModuleContent;", Encoding.UTF8.GetString(source.Bytes));
        Assert.Equal("""{"nodeType":"Store/Plugin"}""",
            Encoding.UTF8.GetString(Assert.Single(files, f => f.RelativePath == "index.json").Bytes));
    }

    [Fact]
    public void CarryingContentDoesNotDisturbTheAssemblyLane()
    {
        var bundle = WriteBundle(File("index.json", "{}"));

        var (manifest, assemblies) = BundleReader.Read(bundle);

        // The two lanes share one manifest; adding content must not cost the bytes their identity.
        Assert.Equal(Mvid, manifest!.FrameworkMvid);
        var payload = Assert.Single(assemblies);
        Assert.Equal("Edu/Module", payload.NodePath);
        Assert.Equal("MODULE", Encoding.UTF8.GetString(payload.Assembly));
    }

    [Fact]
    public void AnAssembliesOnlyBundleReportsNoContent_NotAnError()
    {
        // The format stays valid without a tree: every bundle written before this existed, and
        // every NodeType-only bundle written after it, must still read.
        var buffer = new MemoryStream();
        BundleWriter.Write(
            buffer, "ThreeBody", "1.0.0", Mvid,
            [new BundleWriter.AssemblyEntry("ThreeBody/Physics", () => new MemoryStream("P"u8.ToArray()))]);

        var (manifest, files) = BundleReader.ReadContent(buffer.ToArray());

        Assert.Null(manifest!.Content);
        Assert.Empty(files);
    }

    [Fact]
    public void ADeclaredFileMissingFromTheArchiveYieldsNothing_NeverAPartialTree()
    {
        // A half-materialised package is worse than none: its roots reference nodes that are not
        // there, and the consumer fails at bind time naming the wrong thing. Strip one declared
        // entry out of an otherwise-good bundle and the whole read must refuse.
        var bundle = WriteBundle(File("index.json", "{}"), File("Module.json", "{}"));

        var tampered = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read))
        using (var target = new ZipArchive(tampered, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in source.Entries)
            {
                if (entry.FullName.EndsWith("/Module.json", StringComparison.Ordinal))
                    continue;
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }

        var (manifest, files) = BundleReader.ReadContent(tampered.ToArray());

        Assert.Equal(2, manifest!.Content!.Count);   // still DECLARED — the manifest is intact
        Assert.Empty(files);                          // …and refused wholesale
    }

    [Fact]
    public void OnlyDeclaredFilesAreExtracted()
    {
        // Manifest-driven, not glob-driven. These bytes land in a consumer's working tree, so an
        // undeclared entry a future producer adds must never be recreated there.
        var bundle = WriteBundle(File("index.json", "{}"));

        var tampered = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read))
        using (var target = new ZipArchive(tampered, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }
            using var stowaway = target
                .CreateEntry($"{NuGetPackageWriter.ContentFolder}/not-declared.json").Open();
            stowaway.Write("{}"u8);
        }

        var (_, files) = BundleReader.ReadContent(tampered.ToArray());

        Assert.Equal("index.json", Assert.Single(files).RelativePath);
    }

    [Theory]
    [InlineData("../escaped.json")]                    // parent traversal
    [InlineData("Module/../../escaped.json")]          // traversal mid-path
    [InlineData("/etc/passwd")]                        // rooted
    [InlineData("C:/Windows/system.ini")]              // drive-qualified
    [InlineData("Module\\Source\\X.cs")]                 // Windows separator
    public void AnUnsafeDeclaredPathIsRefused_Loudly(string hostile)
    {
        // These bytes are written into a BUILD AGENT's working tree, which then compiles what it
        // finds — so a producer-declared path that escapes the extraction directory must never be
        // materialised. It THROWS rather than degrading to "no content": an incomplete archive is a
        // benign producer bug, this is hostile or corrupt, and a caller about to write files must
        // not be able to confuse the two.
        var bundle = WriteBundle(File("index.json", "{}"));
        var tampered = Retarget(bundle, hostile);

        var error = Assert.Throws<InvalidOperationException>(() => BundleReader.ReadContent(tampered));
        Assert.Contains(hostile, error.Message);
    }

    [Fact]
    public void TheWriterRefusesToProduceOne()
    {
        // Defence at the producing end too, so a packaging mistake errors next to the code that
        // made it rather than on someone else's agent.
        var buffer = new MemoryStream();
        Assert.Throws<ArgumentException>(() => BundleWriter.Write(
            buffer, "Edu", "1.4.0", Mvid,
            [new BundleWriter.AssemblyEntry("Edu/Module", () => new MemoryStream("M"u8.ToArray()))],
            content: [File("../escaped.json", "{}")]));
    }

    /// <summary>
    /// Rewrites a good bundle's manifest so it DECLARES <paramref name="path"/> — the shape a
    /// hostile or corrupt producer would emit. The archive itself is left alone: the guard must
    /// fire on the declaration, before anything is looked up or written.
    /// </summary>
    private static byte[] Retarget(byte[] bundle, string path)
    {
        var rewritten = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read))
        using (var target = new ZipArchive(rewritten, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in source.Entries)
            {
                using var to = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName != NuGetPackageWriter.ManifestEntry)
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                var json = reader.ReadToEnd()
                    .Replace("\"index.json\"", JsonSerializer.Serialize(path));
                using var writer = new StreamWriter(to, Encoding.UTF8);
                writer.Write(json);
            }

        return rewritten.ToArray();
    }
}
