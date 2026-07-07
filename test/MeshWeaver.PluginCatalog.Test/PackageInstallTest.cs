#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// End-to-end proof of the git-based install loop: a throwaway local git repo holds one content
/// package (a folder with a <c>package.json</c> manifest + a markdown file); the catalog lists it
/// by commit, fetches its files, and installs it INCREMENTALLY into the target partition — then an
/// install record is written to the <c>Plugins</c> registry. No NuGet, no network.
/// </summary>
public class PackageInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 120000)]
    public async Task GitPackage_ListsInstalls_ContentIntoPartition_AndRecordsIt()
    {
        var repo = CreateTempDir();
        try
        {
            var git = new GitCli(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());

            // A single content package: manifest + one markdown file.
            WriteFile(repo, "hello-pack/package.json",
                """{"id":"hello-pack","name":"Hello Pack","description":"A greeting.","kind":"content","targetPartition":"CatalogTest","version":"1.0.0"}""");
            WriteFile(repo, "hello-pack/Greeting.md", "# Hello\n\nInstalled from a git package.");
            // A non-package folder (no manifest) must be ignored by the catalog.
            WriteFile(repo, "notes/scratch.md", "not a package");

            await git.Run(repo, ["init"]).FirstAsync().ToTask();
            await git.Run(repo, ["add", "-A"]).FirstAsync().ToTask();
            await git.Run(repo, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init"])
                .FirstAsync().ToTask();

            var source = new GitPackageSource(git, repo);

            // List: exactly the one folder that carries a manifest.
            var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
            packages.Count.Should().Be(1);
            var pkg = packages[0];
            pkg.Id.Should().Be("hello-pack");
            pkg.Name.Should().Be("Hello Pack");
            pkg.TargetPartition.Should().Be("CatalogTest");
            pkg.SourceFolder.Should().Be("hello-pack");
            pkg.Kind.Should().Be(PackageKind.Content);

            // Fetch + install.
            var files = await source.FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();
            var count = await PackageInstaller.Install(Mesh, pkg, files, "HEAD").FirstAsync().ToTask();
            count.Should().Be(1);

            // The content landed under the target partition, rebased off the package folder.
            var installed = await Mesh.GetWorkspace().GetMeshNodeStream("CatalogTest/Greeting")
                .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask();
            installed.NodeType.Should().Be("Markdown");

            // And the install was recorded in the Plugins registry with the source ref + version.
            var record = await Mesh.GetWorkspace().GetMeshNodeStream("Plugins/hello-pack")
                .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask();
            record.NodeType.Should().Be("Package");
            var manifest = record.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
            manifest.Should().NotBeNull();
            manifest!.Version.Should().Be("1.0.0");
            manifest.InstalledFromRef.Should().Be("HEAD");
            manifest.InstalledNodeCount.Should().Be(1);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task AgentPackage_InstallsAsAgentNode()
    {
        var repo = CreateTempDir();
        try
        {
            var git = new GitCli(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());

            // A real AI-content package: a valid Agent .md (frontmatter nodeType: Agent), under a
            // catalog/ subdir — exactly how the plugins repo ships it.
            WriteFile(repo, "catalog/echo-agent/package.json",
                """{"id":"echo-agent","name":"Echo Agent","kind":"content","targetPartition":"Agent","version":"1.0.0"}""");
            WriteFile(repo, "catalog/echo-agent/Echo.md",
                "---\nnodeType: Agent\nname: Echo\ndescription: A sample agent.\ncategory: Agents\n---\n\nYou are Echo, a sample agent.");

            await git.Run(repo, ["init"]).FirstAsync().ToTask();
            await git.Run(repo, ["add", "-A"]).FirstAsync().ToTask();
            await git.Run(repo, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init"])
                .FirstAsync().ToTask();

            var source = new GitPackageSource(git, repo, "catalog");
            var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
            var pkg = packages.First(p => p.Id == "echo-agent");
            var files = await source.FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();

            await PackageInstaller.Install(Mesh, pkg, files, "HEAD").FirstAsync().ToTask();

            // The Agent .md installs as a proper Agent node in the Agent partition — proving the
            // catalog installs real AI content, not just plain markdown.
            var agent = await Mesh.GetWorkspace().GetMeshNodeStream("Agent/Echo")
                .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask();
            agent.NodeType.Should().Be("Agent");
            agent.Name.Should().Be("Echo");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task GitHubSource_ListsAndInstalls_FromFetchedSnapshot()
    {
        // The GitHub-fetch source reuses GitSync's fetch; here a stub delegate stands in for the
        // repo client (offline, no network, no full IGitHubRepoClient double). It returns the repo
        // tree filtered to the requested subdirectory — exactly like Octokit's client.
        var repoFiles = new List<RepoFile>
        {
            new("catalog/gh-pack/package.json",
                """{"id":"gh-pack","name":"GH Pack","kind":"content","targetPartition":"GhPart","version":"1.0.0"}"""),
            new("catalog/gh-pack/Doc.md", "# From GitHub"),
        };
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch = (url, gitRef, sub, token) =>
            Observable.Return(new RepoSnapshot("sha1", repoFiles
                .Where(f => sub is null || f.Path.StartsWith(sub, StringComparison.Ordinal))
                .ToList()));

        var source = new GitHubPackageSource(fetch, "https://github.com/acme/plugins", subdir: "catalog");

        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
        packages.Count.Should().Be(1);
        var pkg = packages[0];
        pkg.Id.Should().Be("gh-pack");
        pkg.SourceFolder.Should().Be("catalog/gh-pack");

        var files = await source.FetchPackageFiles(pkg, "HEAD").FirstAsync().ToTask();
        var count = await PackageInstaller.Install(Mesh, pkg, files, "main").FirstAsync().ToTask();
        count.Should().Be(1);

        var node = await Mesh.GetWorkspace().GetMeshNodeStream("GhPart/Doc")
            .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask();
        node.NodeType.Should().Be("Markdown");
    }

    private static string CreateTempDir()
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
