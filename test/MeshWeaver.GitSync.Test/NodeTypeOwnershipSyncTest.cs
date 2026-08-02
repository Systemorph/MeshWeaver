using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// NodeType content ownership through the FULL sync loop: the repo owns a type's AUTHORED
/// definition; the mesh owns its compile bookkeeping. Export must strip the bookkeeping from the
/// committed file, and an import of a legacy file that still embeds a stale copy must take the
/// authored change while keeping the LIVE compile state — never stamping the file's stale verdict
/// back onto the mesh (the "stale green" that made every plugin-source deploy on memex a
/// force-sync + manual-recompile ritual, 2026-08-02).
/// </summary>
public class NodeTypeOwnershipSyncTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task Export_StripsBookkeeping_And_Import_KeepsLiveCompileState()
    {
        await Connect();
        var space = "GhN" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Types");

        // A live NodeType whose content carries authored members AND current compile state.
        var content = JsonNode.Parse(
            """
            {"$type":"NodeTypeDefinition","description":"widget type",
             "configuration":"config => config","includeGlobalTypes":true,
             "lastCompiledVersion":1082,"latestAssemblyPath":"Widget/v1082.dll"}
            """)!.AsObject();
        await NodeFactory.CreateNode(new MeshNode("Widget", space)
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Widget",
            State = MeshNodeState.Active,
            Content = content,
        }).Timeout(60.Seconds()).ToTask();

        var repo = "https://github.com/test/space-types";
        await Sync.SaveConfig(space, repo, "main", subdirectory: null,
                createBranchIfMissing: true, createRepoIfMissing: true,
                direction: SyncDirection.Bidirectional, sourceId: null, twoWay: false)
            .Timeout(30.Seconds()).ToTask();
        var commit = await Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).ToTask();
        await WaitForConfig(space, c => c.LastSyncCommitSha == commit.CommitSha);

        // EXPORT: the committed file carries the authored definition only — no compile verdict.
        var tree = Fake.Tree(repo);
        var file = tree.Single(f => f.Path.EndsWith("Widget.json", StringComparison.Ordinal));
        var fileContent = JsonNode.Parse(file.Content)!.AsObject()["content"]!.AsObject();
        Assert.Equal("config => config", (string?)fileContent["configuration"]);
        Assert.False(fileContent.ContainsKey("lastCompiledVersion"),
            "the exported file must not carry the compile verdict");
        Assert.False(fileContent.ContainsKey("latestAssemblyPath"),
            "the exported file must not carry the assembly pointer");

        // A LEGACY repo file (committed before the strip existed) embeds a STALE verdict next to
        // a genuine authored change. Seed it as the new HEAD (the fake replaces the whole tree,
        // so every other exported file rides along unchanged).
        var edited = JsonNode.Parse(file.Content)!.AsObject();
        var editedContent = edited["content"]!.AsObject();
        editedContent["configuration"] = "config => CHANGED";
        editedContent["lastCompiledVersion"] = 202;
        editedContent["latestAssemblyPath"] = "Widget/v202.dll";
        var seeded = tree
            .Select(f => f.Path == file.Path
                ? f with { Content = edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }
                : f)
            .ToImmutableList();
        await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = seeded,
            CommitMessage = "legacy file with stale bookkeeping",
            AuthorName = "test",
            AuthorEmail = "test@test",
            AccessToken = "x",
        }).Timeout(30.Seconds()).ToTask();

        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(90.Seconds()).ToTask();

        // IMPORT: the authored change landed; the live compile state survived; the file's stale
        // verdict did not.
        var options = Mesh.JsonSerializerOptions;
        var updated = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode($"{space}/Widget"))
            .Select(n => n?.ContentAs<NodeTypeDefinition>(options))
            .Where(d => d?.Configuration == "config => CHANGED")
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();
        Assert.NotNull(updated);
        Assert.Equal(1082, updated!.LastCompiledVersion);
        Assert.Equal("Widget/v1082.dll", updated.LatestAssemblyPath);
    }
}
