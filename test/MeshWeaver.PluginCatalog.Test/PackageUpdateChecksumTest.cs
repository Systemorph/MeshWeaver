#pragma warning disable CS1591

using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Proves the install/update checksum: a re-install with identical files writes NOTHING and bumps no
/// versions, and changing one file writes ONLY that node — "update only on real change". Without the
/// guard every re-install would churn every node's version, because the upsert stamps
/// <c>LastModified = UtcNow</c> unconditionally.
/// </summary>
public class PackageUpdateChecksumTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private static PackageManifest Manifest => new()
    {
        Id = "notes-pack",
        Name = "Notes",
        Kind = PackageKind.Content,
        TargetPartition = "ChecksumTest",
        Version = "1.0.0",
        SourceFolder = "notes-pack",
    };

    private static IReadOnlyList<PackageFile> Files(string a, string b) => new List<PackageFile>
    {
        new("notes-pack/package.json", """{"id":"notes-pack"}"""), // the manifest — filtered out on install
        new("notes-pack/A.md", a),
        new("notes-pack/B.md", b),
    };

    [Fact(Timeout = 120000)]
    public async Task Reinstall_WritesOnlyRealChanges()
    {
        // 1) First install → both content nodes written.
        var first = await PackageInstaller.Install(Mesh, Manifest, Files("# A one", "# B one"), "HEAD")
            .FirstAsync().ToTask();
        first.Total.Should().Be(2);
        first.Written.Should().Be(2);
        first.Unchanged.Should().Be(0);

        // 🚨 Take the baseline off the SETTLED node, not off whatever snapshot the stream happens
        // to be replaying. GetMeshNodeStream replays its cached value, so a bare FirstAsync() right
        // after an install can hand back the node as it was BEFORE the install's write landed. That
        // baseline is then too low, and step 2's "must not bump" fails against the settled version —
        // the intermittent red on this test. Waiting for the installed body makes the baseline the
        // version the install actually produced.
        var aV1 = (await ReadWhen("ChecksumTest/A", "# A one")).Version;
        var bV1 = (await ReadWhen("ChecksumTest/B", "# B one")).Version;

        // 2) Re-install the IDENTICAL files → nothing written, no version churn.
        var second = await PackageInstaller.Install(Mesh, Manifest, Files("# A one", "# B one"), "HEAD")
            .FirstAsync().ToTask();
        second.Written.Should().Be(0, "an unchanged re-install must not write any node");
        second.Unchanged.Should().Be(2);
        (await Read("ChecksumTest/A")).Version.Should().Be(aV1, "the unchanged node must not bump its version");
        (await Read("ChecksumTest/B")).Version.Should().Be(bV1, "the unchanged node must not bump its version");

        // 3) Change ONLY A → exactly one node written; B is left untouched.
        var third = await PackageInstaller.Install(Mesh, Manifest, Files("# A CHANGED", "# B one"), "HEAD")
            .FirstAsync().ToTask();
        third.Written.Should().Be(1, "only the changed file should be written");
        third.Unchanged.Should().Be(1);

        // Wait for the CHANGED node to land before asserting on either version. Ordering matters:
        // once A's new body is observable the install has been applied, so B's "unchanged" is a
        // statement about a settled mesh rather than about a snapshot that has not moved YET.
        var aV2 = (await ReadWhen("ChecksumTest/A", "# A CHANGED")).Version;
        Assert.True(aV2 > aV1, "the changed node must bump its version");
        (await Read("ChecksumTest/B")).Version.Should().Be(bV1, "the untouched node must not bump its version");
    }

    [Fact(Timeout = 120000)]
    public async Task CodePackage_ReinstallUnchanged_WritesNothing_DespiteCompileEnrichment()
    {
        var manifest = new PackageManifest
        {
            Id = "widget2",
            Name = "Widget2",
            Kind = PackageKind.Code,
            TargetPartition = "type",
            Version = "1.0.0",
            SourceFolder = "widget2",
            NodeTypeConfiguration = "config => config.WithContentType<Widget2>()",
        };
        IReadOnlyList<PackageFile> files = new List<PackageFile>
        {
            new("widget2/package.json", """{"id":"widget2"}"""),
            new("widget2/Source/Widget2.cs", "public record Widget2 { public string T { get; init; } = string.Empty; }"),
        };

        // First install → NodeType node + one Source Code node written.
        var first = await PackageInstaller.Install(Mesh, manifest, files, "HEAD").FirstAsync().ToTask();
        first.Total.Should().Be(2);
        first.Written.Should().Be(2);

        // Wait for the mesh to compile the NodeType — this ENRICHES its stored content with
        // CompilationStatus etc., which a naive whole-content compare would mistake for a change.
        await Mesh.GetMeshNodeStream("type/widget2")
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        // Re-install the IDENTICAL package → nothing written: the NodeType compare looks only at the
        // authored Configuration (ignoring compile-derived state), and the Source is unchanged. A
        // redundant recompile is therefore not triggered either.
        var second = await PackageInstaller.Install(Mesh, manifest, files, "HEAD").FirstAsync().ToTask();
        second.Written.Should().Be(0, "an unchanged code re-install must not rewrite the NodeType or its Source");
        second.Unchanged.Should().Be(2);
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

    /// <summary>
    /// The node once its body IS <paramref name="body"/> — i.e. once the install that wrote it has
    /// actually landed.
    ///
    /// <para>🚨 <see cref="Read"/> is a bare <c>FirstAsync</c> on a REPLAYING stream: it returns the
    /// currently cached snapshot, which right after an install may still be the pre-write node. Any
    /// version captured from it is a version from before the write, and every later comparison
    /// against it is then wrong in a way that only shows up under load. Wait for the state, then
    /// read the version.</para>
    /// </summary>
    private async Task<MeshNode> ReadWhen(string path, string body) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is MeshWeaver.Markdown.MarkdownContent m
                        && m.Content.Contains(body, System.StringComparison.Ordinal))
            .FirstAsync().Timeout(30.Seconds()).ToTask();
}
