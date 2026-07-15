using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Export robustness: a node whose content type has NO bespoke (markdown / agent / C#) parser —
/// a <see cref="DeckContent"/> deck — must still round-trip. Export serializes it through the
/// universal JSON serializer (<c>JsonFileParser.CanSerialize =&gt; true</c> — any content type, the
/// exact inverse of the JSON import) as a <c>.json</c> file, and import reads it back typed with
/// its manifest intact. Pins that GitSync export serializes EVERY content type and can never
/// silently drop a node (the failure mode that lost live-only work before).
/// </summary>
public class TypedNodeExportRobustnessTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task DeckWithManifest_RoundTripsAsJson_NothingDropped()
    {
        await Connect();
        var a = "GhTk" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Deck Space");
        await NodeFactory.CreateNode(MeshNode.FromPath($"{a}/Show") with
        {
            NodeType = DeckNodeType.NodeType,   // "Deck" — no markdown/agent/C# parser exists for it
            Name = "Show",
            State = MeshNodeState.Active,
            Content = new DeckContent
            {
                Title = "The Show",
                Description = "A shared-pool presentation.",
                Slides = ["Slides/S01", "Slides/S02", "Slides/S03"],
            },
        }).Timeout(60.Seconds()).ToTask();

        var repo = "https://github.com/test/deck-json";
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        // Exported via the universal JSON serializer → Show.json, with the manifest preserved.
        var file = Fake.Tree(repo).FirstOrDefault(f => f.Path == "Show.json");
        Assert.NotNull(file);
        Assert.Contains("DeckContent", file!.Content);
        Assert.Contains("Slides/S02", file.Content);

        // Import into a fresh Space — the Deck round-trips TYPED with its ordered manifest intact.
        var b = "GhTl" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(repo, "main", b, "Deck B", null, UserId).Timeout(90.Seconds()).ToTask();

        var deckNode = await WaitForNode($"{b}/Show");
        Assert.Equal(DeckNodeType.NodeType, deckNode.NodeType);
        var deck = deckNode.ContentAs<DeckContent>(Mesh.JsonSerializerOptions);
        Assert.NotNull(deck);
        Assert.Equal(new[] { "Slides/S01", "Slides/S02", "Slides/S03" }, deck!.Slides);
    }

    /// <summary>
    /// A "broken" repo file whose content object omits the <c>$type</c> discriminator must still
    /// import and read TYPED: the content converter degrades an absent/unknown <c>$type</c> to raw
    /// JSON, and <c>ContentAs&lt;T&gt;</c> resolves it against the target type at the read site
    /// (<c>$type</c> is advisory, not load-bearing, for a typed read). Pins that a hand-authored or
    /// legacy <c>$type</c>-less deck file is not lost — it round-trips to a usable Deck.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ContentMissingDollarType_StillImportsAndReadsTyped()
    {
        await Connect();
        var a = "GhNt" + Guid.NewGuid().ToString("N")[..8];
        var repo = "https://github.com/test/missing-type";
        await CreateSpace(a, "Repair Space");
        await CreateMarkdown($"{a}/Seed", "Seed", "# seed");   // seeds a valid root + tree
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        // A BROKEN file: a Deck node whose content object has NO "$type".
        var brokenJson = """
            {"$type":"MeshNode","id":"Show","namespace":"__NS__","path":"__NS__/Show","nodeType":"Deck","name":"Show","state":"Active","content":{"title":"Show","slides":["Slides/S01","Slides/S02"]}}
            """.Replace("__NS__", a);
        var tree = Fake.Tree(repo).Append(new RepoFile("Show.json", brokenJson)).ToImmutableList();
        var commit = await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Subdirectory = null,
            Files = tree,
            CommitMessage = "add a $type-less deck file",
            AuthorName = "Git Author",
            AuthorEmail = "git-author@test",
            AccessToken = "ghp_test_token",
        }).Timeout(30.Seconds()).ToTask();

        await Sync.ReimportAtCommit(a, commit.CommitSha, UserId).Timeout(90.Seconds()).ToTask();

        var node = await WaitForNode($"{a}/Show");
        Assert.Equal("Deck", node.NodeType);
        var deck = node.ContentAs<DeckContent>(Mesh.JsonSerializerOptions);
        Assert.NotNull(deck);
        Assert.Equal(new[] { "Slides/S01", "Slides/S02" }, deck!.Slides);
    }
}
