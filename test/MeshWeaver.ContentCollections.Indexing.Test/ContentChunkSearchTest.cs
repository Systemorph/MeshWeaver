using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Unit tests for the content-chunk search engine (<see cref="ContentChunkSearch"/>) and the subtree-scoped
/// store query (<see cref="IChunkedContentVectorStore.SearchSubtree"/>) it builds on. These pin the
/// scope semantics the GUI explorer and the agent/MCP <c>search_chunks</c> grammar both rely on:
/// <c>subtree</c> (the default — "check only this collection [and nested]"), <c>exact</c>, and
/// <c>ancestorsandself</c> (the context walk). Pure unit test over the in-memory store + the deterministic
/// <see cref="FakeEmbedder"/> — no mesh. Each search uses <c>limit:50</c> so every in-scope chunk is
/// returned regardless of the fake embedder's (SHA-derived) ranking — the assertions are on WHICH
/// collections are searched, which is the scope contract.
/// </summary>
public class ContentChunkSearchTest
{
    private readonly FakeEmbedder _embedder = new();
    private readonly InMemoryChunkedContentVectorStore _store = new();

    public ContentChunkSearchTest()
    {
        // A small collection tree under the ACME partition, plus a sibling partition collection:
        //   ACME/content           (the collection)         — file a.txt
        //   ACME/content/sub       (nested under it)        — file b.txt
        //   ACME                   (an ancestor collection) — file root.txt
        //   OTHER/content          (a different partition)  — file c.txt
        Seed("ACME/content", "a.txt", "alpha apple annual report");
        Seed("ACME/content/sub", "b.txt", "beta banana benefit obligation");
        Seed("ACME", "root.txt", "gamma grape governance");
        Seed("OTHER/content", "c.txt", "delta date disclosure");
    }

    private void Seed(string collection, string file, string text) =>
        _store.ReplaceFileChunks(collection, file, new[]
        {
            new ContentChunk(
                CollectionPath: collection, FilePath: file, ChunkIndex: 0, Text: text,
                ContentHash: "h-" + collection + "/" + file, Embedding: _embedder.Embed(text).Wait()),
        }).Wait();

    private async Task<ContentSearchResult> Search(string query, string? defaultNs = null) =>
        await ContentChunkSearch.SearchContent(_store, _embedder, query, 50, defaultNs).FirstAsync().ToTask();

    private async Task<ContentSearchResult> Anchored(string query, string anchor) =>
        await ContentChunkSearch.Search(_store, _embedder, query, anchor, 50).FirstAsync().ToTask();

    private static IEnumerable<string> Collections(ContentSearchResult r) =>
        r.Hits.Select(h => h.CollectionPath);

    /// <summary>Order-insensitive set assertion — the custom <c>BeEquivalentTo</c> in this repo wants a serializer.</summary>
    private static void AssertSet(IEnumerable<string> actual, params string[] expected) =>
        actual.Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(expected.OrderBy(x => x, StringComparer.Ordinal));

    // ── Store-level subtree scoping ──────────────────────────────────────────

    [Fact]
    public async Task SearchSubtree_MatchesCollectionAndDescendants_NotAncestorsOrSiblings()
    {
        var hits = await _store.SearchSubtree("ACME/content", _embedder.Embed("alpha").Wait(), 50)
            .FirstAsync().ToTask();

        AssertSet(hits.Select(h => h.CollectionPath), "ACME/content", "ACME/content/sub");
    }

    [Fact]
    public async Task SearchSubtree_ExactCollectionWithNoChildren_ReturnsOnlyThatCollection()
    {
        var hits = await _store.SearchSubtree("OTHER/content", _embedder.Embed("delta").Wait(), 50)
            .FirstAsync().ToTask();

        AssertSet(hits.Select(h => h.CollectionPath), "OTHER/content");
    }

    // ── Grammar: scope resolution ────────────────────────────────────────────

    [Fact]
    public async Task Grammar_NoScope_DefaultsToSubtree()
    {
        var result = await Search("namespace:ACME/content alpha apple");

        result.Scope.Should().Be(ContentSearchScope.Subtree);
        AssertSet(Collections(result), "ACME/content", "ACME/content/sub");
        result.ToolCall.Should().Contain("namespace:ACME/content scope:subtree");
    }

    [Fact]
    public async Task Grammar_ScopeExact_RestrictsToTheOneCollection()
    {
        var result = await Search("namespace:ACME/content scope:exact alpha");

        result.Scope.Should().Be(ContentSearchScope.Exact);
        AssertSet(Collections(result), "ACME/content");
        result.ToolCall.Should().Contain("scope:exact");
    }

    [Fact]
    public async Task Grammar_ScopeAncestorsAndSelf_WalksUp_NotDown()
    {
        var result = await Search("namespace:ACME/content scope:ancestorsandself alpha");

        result.Scope.Should().Be(ContentSearchScope.AncestorsAndSelf);
        AssertSet(Collections(result), "ACME/content", "ACME"); // up to ACME, never down into sub
    }

    [Fact]
    public async Task Grammar_NamespaceFromDefault_WhenNoNamespaceToken()
    {
        var result = await Search("alpha apple", defaultNs: "ACME/content");

        result.Namespace.Should().Be("ACME/content");
        result.Scope.Should().Be(ContentSearchScope.Subtree);
        AssertSet(Collections(result), "ACME/content", "ACME/content/sub");
    }

    [Fact]
    public async Task Grammar_NamespaceTokenWins_OverDefault()
    {
        var result = await Search("namespace:OTHER/content delta", defaultNs: "ACME/content");

        result.Namespace.Should().Be("OTHER/content");
        AssertSet(Collections(result), "OTHER/content");
    }

    // ── Grammar: hint envelopes (no throw) ───────────────────────────────────

    [Fact]
    public async Task Grammar_NoNamespaceAndNoDefault_ReturnsHint_NoHits()
    {
        var result = await Search("alpha apple");

        result.Hits.Should().BeEmpty();
        result.Message.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain("namespace:");
    }

    [Fact]
    public async Task Grammar_EmptyText_ReturnsHint_NoHits()
    {
        var result = await Search("namespace:ACME/content scope:subtree");

        result.Hits.Should().BeEmpty();
        result.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Grammar_NoStore_ReturnsNotEnabledMessage_NoThrow()
    {
        var result = await ContentChunkSearch.SearchContent(null, null, "namespace:ACME/content alpha")
            .FirstAsync().ToTask();

        result.Hits.Should().BeEmpty();
        result.Message.Should().Contain("not enabled");
    }

    // ── Dispatch: Search routes by query shape ───────────────────────────────

    [Fact]
    public async Task Search_NamespaceToken_RoutesToGrammar_IgnoringAnchor()
    {
        // The anchor is a bogus path; the namespace: token must win and drive the search.
        var result = await Anchored("namespace:ACME/content alpha", anchor: "ZZZ/nowhere");

        result.Scope.Should().Be(ContentSearchScope.Subtree);
        AssertSet(Collections(result), "ACME/content", "ACME/content/sub");
    }

    [Fact]
    public async Task Search_NoNamespace_AnchoredAncestorWalk()
    {
        var result = await Anchored("beta", anchor: "ACME/content/sub");

        result.Scope.Should().Be(ContentSearchScope.AncestorsAndSelf);
        // Ancestor walk of ACME/content/sub = {ACME/content/sub, ACME/content, ACME} — never OTHER.
        Collections(result).Should().NotContain("OTHER/content");
        Collections(result).Should().Contain("ACME/content/sub");
    }

    [Fact]
    public async Task ToJson_HasCountAndResults()
    {
        var result = await Search("namespace:ACME/content alpha");

        var json = ContentChunkSearch.ToJson(result);
        json.Should().Contain("\"count\":");
        json.Should().Contain("\"results\":");
        json.Should().Contain("\"chunkIndex\":");
    }
}
