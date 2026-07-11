#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The node-native path (the shape <c>MeshWeaver.Plugins</c> ships): a <see cref="NodeRepoPackageSource"/>
/// lists <c>&lt;Plugin&gt;/index.json</c> Space roots (the partition root lives INSIDE the plugin
/// folder — the folder is the unit of import) and fetches a plugin's node tree; the installer
/// imports the nodes at their CANONICAL paths (no rebase) and the mesh compiles the NodeType live. A
/// re-install of the unchanged repo writes nothing (checksum).
/// </summary>
public class NodeRepoInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    // A minimal node-native plugin repo: a `Widget/index.json` Space root carrying a PluginManifest
    // (the partition root lives INSIDE the folder), one NodeType with a Configuration lambda, and
    // that type's Source. Plus a README (a display file, never a node), an unrelated nested json
    // that is no index.json, and a LEGACY top-level `<Plugin>.json` (the pre-index.json layout —
    // must NOT be listed as a plugin).
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Widget/Thing.json",
            """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>()","includeGlobalTypes":true}}"""),
        new("Widget/Thing/Source/Thing.cs",
            "public record Thing { public string Name { get; init; } = string.Empty; }"),
        new("README.md", "# Plugins"),
        new("Notes/scratch.json", """{"$type":"MeshNode","id":"scratch","nodeType":"Markdown"}"""),
        new("Legacy.json",
            """{"$type":"MeshNode","id":"Legacy","namespace":"","path":"Legacy","mainNode":"Legacy","name":"Legacy","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Old sibling-manifest layout — not a plugin root any more."}}"""),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-abc", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    [Fact(Timeout = 120_000)]
    public async Task ListsIndexJsonRoots_InstallsAtCanonicalPaths_Compiles_ReinstallIdempotent()
    {
        var source = Source();

        // List: exactly the one `<Plugin>/index.json` Space root (README, the nested non-index
        // json, and the legacy top-level `Legacy.json` are not plugins).
        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
        packages.Count.Should().Be(1);
        var widget = packages[0];
        widget.Id.Should().Be("Widget");
        widget.Name.Should().Be("Widget Plugin");
        widget.Kind.Should().Be(PackageKind.NodeRepo);
        widget.Version.Should().Be("commit-abc"); // the commit sha → update detection

        // Fetch: the plugin's own files only (everything under `Widget/`, root included) —
        // README/Notes/Legacy excluded.
        var files = await source.FetchPackageFiles(widget, "HEAD").FirstAsync().ToTask();
        files.Count.Should().Be(3);

        // Install: 3 nodes written at their CANONICAL paths.
        var result = await PackageInstaller.Install(Mesh, widget, files, "commit-abc").FirstAsync().ToTask();
        result.Total.Should().Be(3);
        result.Written.Should().Be(3);

        (await Read("Widget")).NodeType.Should().Be("Space");
        (await Read("Widget/Thing/Source/Thing")).NodeType.Should().Be("Code");

        // The installed NodeType compiles live.
        var nt = await Mesh.GetMeshNodeStream("Widget/Thing")
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        var def = (NodeTypeDefinition)nt.Content!;
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the installed node-repo NodeType must compile live; error: {def.CompilationError}");

        // Re-install the unchanged repo → nothing written (checksum), despite the compile having
        // enriched the NodeType node.
        var second = await PackageInstaller.Install(Mesh, widget, files, "commit-abc").FirstAsync().ToTask();
        second.Written.Should().Be(0, "an unchanged node-repo re-install must not rewrite any node");
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
}
