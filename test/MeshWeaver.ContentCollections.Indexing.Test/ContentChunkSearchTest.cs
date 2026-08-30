using System.Reactive.Linq;
using Xunit;
using MeshWeaver.Fixture;

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
        await ContentChunkSearch.SearchContent(_store, _embedder, query, 50, defaultNs).FirstAsync().Await();

    private async Task<ContentSearchResult> Anchored(string query, string anchor) =>
        await ContentChunkSearch.Search(_store, _embedder, query, anchor, 50).FirstAsync().Await();

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
            .FirstAsync().Await();

        AssertSet(hits.Select(h => h.CollectionPath), "ACME/content", "ACME/content/sub");
    }

    [Fact]
    public async Task SearchSubtree_ExactCollectionWithNoChildren_ReturnsOnlyThatCollection()
    {
        var hits = await _store.SearchSubtree("OTHER/content", _embedder.Embed("delta").Wait(), 50)
            .FirstAsync().Await();

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
            .FirstAsync().Await();

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

    [Fact]
    public async Task ToJson_IncludesPageAndBbox_WhenChunkCarriesProvenance()
    {
        var pos = new ChunkPosition(0.1, 0.2, 0.3, 0.05);
        _store.ReplaceFileChunks("PROV/content", "p.pdf", new[]
        {
            new ContentChunk("PROV/content", "p.pdf", 0, "provenance carrying chunk",
                ContentHash: "h", Embedding: _embedder.Embed("provenance carrying chunk").Wait(),
                Metadata: null, Page: 3, Position: pos),
        }).Wait();

        var result = await Search("namespace:PROV/content provenance");

        var hit = result.Hits.Should().ContainSingle().Subject;
        hit.Page.Should().Be(3);
        hit.Position.Should().Be(pos);

        var json = ContentChunkSearch.ToJson(result);
        json.Should().Contain("\"page\":3");
        json.Should().Contain("\"bbox\":");
        json.Should().Contain("\"x\":0.1");
    }

    /// <summary>
    /// 🚨 <b>#2741 — a sweep that cannot fail is not a sweep.</b>
    ///
    /// <para>AGENTS.md requires a live-mesh <c>search_chunks</c> sweep before deleting any public
    /// framework surface, because the mesh holds callers the repo has already dropped and no
    /// compiler can see them. On a deployment with no embedding provider that sweep answered
    /// <c>{"count":0,"results":[]}</c> — byte-identical to "I searched and found no callers" — so an
    /// agent following the prescribed procedure reads it as permission to delete. Measured on BOTH
    /// reachable portals on 2026-08-30.</para>
    ///
    /// <para>The envelope now carries no <c>count</c> at all when nothing was searched, which is the
    /// point: a consumer testing <c>count == 0</c> finds the field ABSENT rather than finding a zero
    /// that means the opposite of what it looks like.</para>
    /// </summary>
    [Fact]
    public async Task ToJson_WhenIndexingIsOff_CarriesNoCount_SoZeroCannotBeReadAsNoResults()
    {
        var off = "Content indexing is not active on this deployment.";
        var result = await ContentChunkSearch
            .Search(new InertChunkedContentVectorStore(off), new InertChunkEmbedder(off),
                "namespace:ACME/content alpha", anchorPath: null, limit: 50)
            .FirstAsync();

        result.Searched.Should().BeFalse("nothing was embedded and no collection was read");

        var json = ContentChunkSearch.ToJson(result);
        json.Should().NotContain("\"count\":",
            "a count of 0 for a search that never ran is the false pass this test exists to prevent");
        json.Should().NotContain("\"results\":");
        json.Should().Contain("\"searched\":false");
        json.Should().Contain(ContentChunkSearch.NotSearchedError);
        json.Should().Contain(off, "the envelope must still say WHAT to configure, not merely that it did not search");
    }

    /// <summary>
    /// The same rule for the other way a search fails to run: no query text. "You gave me nothing to
    /// search for" and "I searched and found nothing" are different answers and must not share an
    /// envelope either.
    /// </summary>
    [Fact]
    public async Task ToJson_WhenTheQueryCarriesNoText_CarriesNoCount()
    {
        var result = await Search("namespace:ACME/content");

        result.Searched.Should().BeFalse();
        ContentChunkSearch.ToJson(result).Should().NotContain("\"count\":");
    }

    /// <summary>
    /// The control, and the half that keeps the guard honest: a search that DID run still reports its
    /// count, including the genuine zero. Without this, "omit the count" could quietly become "omit
    /// the count whenever it is zero", which would hide a real empty result.
    /// </summary>
    [Fact]
    public async Task ToJson_WhenASearchRanAndMatchedNothing_StillReportsZero()
    {
        var result = await Search("namespace:EMPTY/collection alpha");

        result.Searched.Should().BeTrue();
        var json = ContentChunkSearch.ToJson(result);
        json.Should().Contain("\"searched\":true");
        json.Should().Contain("\"count\":0");
    }
}
