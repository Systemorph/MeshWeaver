#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// A registry can serve a node-native plugin repo straight from a LOCAL checkout — no GitHub App,
/// no network. This is what makes a local-dev (or air-gapped) registry possible at all: before it,
/// a local path silently fell through to the <c>package.json</c> source and listed nothing, so the
/// only way to serve the node-native repo MeshWeaver.Plugins ships was a GitHub credential.
/// </summary>
public class LocalNodeRepoSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private static string WriteRepo()
    {
        // A node-native repo on disk, in the shape MeshWeaver.Plugins ships: <Plugin>/index.json
        // Space roots. Includes a dot-directory that MUST be skipped (a real checkout's .git is
        // enormous and is not repo content) and a binary icon that must survive as bytes.
        var root = Path.Combine(Path.GetTempPath(), "mw-local-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Widget"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "Widget", "index.json"),
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget.","minMeshVersion":"1.0.0"}}""");
        File.WriteAllText(Path.Combine(root, "Widget", "Page.json"),
            """{"$type":"MeshNode","id":"Page","namespace":"Widget","path":"Widget/Page","mainNode":"Widget/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Widget"}""");
        File.WriteAllBytes(Path.Combine(root, "Widget", "icon.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
        return root;
    }

    [Fact(Timeout = 120_000)]
    public async Task LocalPath_ServesNodeRepo_SkippingDotDirs_WithAStableRef()
    {
        var root = WriteRepo();
        try
        {
            var source = PackageSources.FromRepo(Mesh, root, sourceSubdir: "", logger: null, nodeRepo: true);
            Assert.NotNull(source);

            var packages = await source!.ListPackages("HEAD").Should().Within(60.Seconds()).Emit();
            packages.Select(p => p.Id).Should().Contain("Widget");

            var files = await source.FetchPackageFiles(
                packages.First(p => p.Id == "Widget"), "HEAD").Should().Within(60.Seconds()).Emit();
            files.Select(f => f.RelativePath).Should().Contain("Widget/index.json");
            // The .git directory is not repo content and must never be served.
            files.Should().NotContain(f => f.RelativePath.StartsWith(".git"));

            // An unchanged directory yields the SAME ref, so the installer's checksum gate can
            // report "nothing to do" instead of rewriting every node on each listing.
            var again = await source.ListPackages("HEAD").Should().Within(60.Seconds()).Emit();
            again.Single(p => p.Id == "Widget").Version
                .Should().Be(packages.Single(p => p.Id == "Widget").Version);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The real repo, when this machine has it checked out — the case the local registry actually
    /// serves. Skips cleanly elsewhere (CI has no such checkout) rather than failing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RealPluginsCheckout_ListsTheStore()
    {
        const string repo = "/Users/roland/code/MeshWeaver.Plugins";
        if (!Directory.Exists(Path.Combine(repo, "Store")))
        {
            Output.WriteLine($"SKIP: no MeshWeaver.Plugins checkout at {repo}");
            return;
        }

        var source = PackageSources.FromRepo(Mesh, repo, sourceSubdir: "", logger: null, nodeRepo: true);
        var packages = await source!.ListPackages("HEAD").Should().Within(90.Seconds()).Emit();

        Output.WriteLine($"Local plugins repo serves: {string.Join(", ", packages.Select(p => p.Id))}");
        packages.Select(p => p.Id).Should().Contain("Store",
            "the Store is what a fresh installation must come up with");
    }
}
