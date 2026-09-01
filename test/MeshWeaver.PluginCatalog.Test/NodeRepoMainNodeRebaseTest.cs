#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 MeshWeaver#2939 — an install must land every node as its OWN main node.
///
/// <para><see cref="MeshNode.MainNode"/> is a STORED, non-nullable init property whose default is
/// evaluated ONCE at construction. A file-format parser that mints a node in one namespace and lets
/// the installer rebase it therefore hands over a node whose computed <see cref="MeshNode.Path"/>
/// has moved and whose <c>MainNode</c> has NOT. <c>ParseCanonical</c> used to keep that value
/// verbatim — its <c>parsed.MainNode ?? (…)</c> was dead code, because the field can never be
/// null — so the node installed <c>Active</c>, fully formed, and INVISIBLE to every search: the
/// catalog's <c>is:main</c> projection is SQL <c>n.main_node = n.path</c>. Nothing errors, nothing
/// logs, no status flips. Six live Skill nodes on memex.meshweaver.cloud were in this state.</para>
///
/// <para>The file below reproduces it with the only lever a core test has: a node file that
/// DECLARES a namespace other than the one its path implies — which is exactly what
/// <c>MeshWeaver.Plugins</c>' <c>SkillFileParser</c> did (it minted every skill in the platform
/// <c>Skill</c> partition and rebased afterwards).</para>
/// </summary>
public class NodeRepoMainNodeRebaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Gadget/index.json",
            """{"$type":"MeshNode","id":"Gadget","namespace":"","path":"Gadget","name":"Gadget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A gadget plugin.","minMeshVersion":"1.0.0"}}"""),

        // The defect, authored: the file lives at `Gadget/Skill/deployment.json`, so its node path is
        // `Gadget/Skill/deployment` — but it declares `namespace: "Skill"`, so the parser mints it
        // with `MainNode = "Skill/deployment"` and never revisits it.
        new("Gadget/Skill/deployment.json",
            """{"$type":"MeshNode","id":"deployment","namespace":"Skill","path":"Skill/deployment","name":"/deployment","nodeType":"Markdown","state":"Active","content":{"$type":"MarkdownContent","markdown":"# deployment"}}"""),

        // The control: an AUTHORED mainNode, which the install must PRESERVE. An _Access grant's
        // mainNode IS its scope — the permission evaluator silently ignores a grant whose mainNode
        // is wrong — so the fix must not become "always stamp the path".
        new("Gadget/Pinned/tile.json",
            """{"$type":"MeshNode","id":"tile","namespace":"Gadget/Pinned","path":"Gadget/Pinned/tile","mainNode":"Gadget","name":"Tile","nodeType":"Markdown","state":"Active","content":{"$type":"MarkdownContent","markdown":"# tile"}}"""),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-gadget", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/gadgets");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnInstalledNode_IsItsOwnMainNode_UnlessTheFileAuthoredOne()
    {
        var source = Source();
        var packages = await source.ListPackages("HEAD").FirstAsync().Await();
        packages.Count.Should().Be(1);
        var gadget = packages[0];

        var files = await source.FetchPackageFiles(gadget, "HEAD").FirstAsync().Await();
        var result = await PackageInstaller.Install(Mesh, gadget, files, "commit-gadget")
            .FirstAsync().Await();
        result.Written.Should().Be(files.Count);

        var skill = await Read("Gadget/Skill/deployment");
        skill.MainNode.Should().Be("Gadget/Skill/deployment",
            "a node whose MainNode was never authored is a MAIN node — keeping the namespace the "
            + "parser minted it in makes it invisible to `is:main` (SQL n.main_node = n.path) "
            + "while `get` still returns it, Active and fully formed");
        skill.MainNode.Should().Be(skill.Path);

        var pinned = await Read("Gadget/Pinned/tile");
        pinned.MainNode.Should().Be("Gadget",
            "an AUTHORED mainNode is the writer's deliberate choice and must survive the rebase — "
            + "an _Access grant's mainNode IS its scope, and clobbering it with the path default "
            + "would break every access file a package ships");
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(30.Seconds()).Await();
}
