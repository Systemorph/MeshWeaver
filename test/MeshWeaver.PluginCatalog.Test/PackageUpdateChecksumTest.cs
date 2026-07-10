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

        var aV1 = (await Read("ChecksumTest/A")).Version;
        var bV1 = (await Read("ChecksumTest/B")).Version;

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
        (await Read("ChecksumTest/B")).Version.Should().Be(bV1, "the untouched node must not bump its version");
        Assert.True((await Read("ChecksumTest/A")).Version > aV1, "the changed node must bump its version");
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
}
