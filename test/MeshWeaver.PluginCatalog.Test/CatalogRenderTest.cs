#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Renders the catalog browse view on a <c>PluginCatalog</c> node pointed at a throwaway local git
/// repo, and asserts it lists the repo's packages (under a <c>catalog/</c> subdir) as cards — the
/// GUI-side proof that the git source → list → render pipeline works, using the same
/// <c>GetRemoteStream</c>/<c>GetControlStream</c> binding the portal uses.
/// </summary>
public class CatalogRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddPluginCatalogTypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact(Timeout = 120000)]
    public async Task Catalog_ListsPackagesFromGitSource_AsCards()
    {
        var repo = CreateTempRepo();
        try
        {
            var git = new GitCli(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());
            WriteFile(repo, "catalog/pack-a/package.json",
                """{"id":"pack-a","name":"Package A","description":"first","kind":"content","targetPartition":"PartA","version":"1.0.0"}""");
            WriteFile(repo, "catalog/pack-a/A.md", "# A");
            WriteFile(repo, "catalog/pack-b/package.json",
                """{"id":"pack-b","name":"Package B","description":"second","kind":"content","targetPartition":"PartB","version":"2.0.0"}""");
            WriteFile(repo, "catalog/pack-b/B.md", "# B");
            await git.Run(repo, ["init"]).FirstAsync().ToTask();
            await git.Run(repo, ["add", "-A"]).FirstAsync().ToTask();
            await git.Run(repo, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init"])
                .FirstAsync().ToTask();

            // A catalog node pointed at the repo's catalog/ subdir. Created as a child (a partition
            // ROOT must be a Space; a custom node type can't be a root), under the admin partition.
            const string catalogPath = "rbuergi/catalog";
            await NodeFactory.CreateNode(MeshNode.FromPath(catalogPath) with
            {
                Name = "Catalog",
                NodeType = "PluginCatalog",
                Content = new PluginCatalogContent
                {
                    SourceRepoPath = repo,
                    SourceRef = "HEAD",
                    SourceSubdir = "catalog",
                },
            }).Should().Emit();

            var workspace = GetClient(c => c.AddData()).GetWorkspace();
            var reference = new LayoutAreaReference(CatalogLayoutAreas.CatalogArea);
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(catalogPath), reference);

            // The catalog resolves to a stack that lists both packages as "pkg-*" card areas.
            var stack = (StackControl)(await stream.GetControlStream(reference.Area!)
                .Should().Within(60.Seconds()).Match(c =>
                    c is StackControl s
                    && s.Areas.Count(a => a.Area?.ToString()?.Contains("/pkg-") == true) == 2))!;

            var cardAreas = stack.Areas
                .Select(a => a.Area?.ToString())
                .Where(p => p is not null && p.Contains("/pkg-"))
                .ToList();
            cardAreas.Count.Should().Be(2);

            // Each card renders (a StackControl with content).
            foreach (var area in cardAreas)
                await stream.GetControlStream(area!)
                    .Should().Within(15.Seconds()).Match(c => c is StackControl);

            await NodeFactory.DeleteNode(catalogPath).Should().Emit();
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private static string CreateTempRepo()
    {
        var p = Path.Combine(Path.GetTempPath(), "pkgcat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
