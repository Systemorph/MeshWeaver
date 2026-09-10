#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

public class LocalSourceContentVersionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder).AddPluginCatalog();

    [Theory(Timeout = 120_000)]
    [InlineData("Page.md")]
    [InlineData("icon.png")]
    public async Task EqualLengthContentEdits_ChangeTheListedVersion(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-local-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Widget"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Widget", "index.json"),
                """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget."}}""");
            var path = Path.Combine(root, "Widget", fileName);
            File.WriteAllBytes(path, [65, 66, 67]);
            var source = PackageSources.FromRepo(Mesh, root, "", nodeRepo: PackageSources.IsNodeRepoFormat("node-repo"));
            Assert.NotNull(source);

            var first = (await source.ListPackages("HEAD").Should().Within(TestTimeouts.Quick).Emit()).Single();
            var unchanged = (await source.ListPackages("HEAD").Should().Within(TestTimeouts.Quick).Emit()).Single();
            unchanged.Version.Should().Be(first.Version, "an unchanged source must keep its version");

            // Keep both the byte count and timestamp: neither is evidence of equal content.
            var modified = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, [68, 69, 70]);
            File.SetLastWriteTimeUtc(path, modified);
            var edited = (await source.ListPackages("HEAD").Should().Within(TestTimeouts.Quick).Emit()).Single();
            edited.Version.Should().NotBe(first.Version, "a mounted source version must identify its content, including equal-length edits");

            // Infrastructure outside the package payload must not move the advertised version.
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/elsewhere");
            var infrastructureOnly = (await source.ListPackages("HEAD").Should().Within(TestTimeouts.Quick).Emit()).Single();
            infrastructureOnly.Version.Should().Be(edited.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
