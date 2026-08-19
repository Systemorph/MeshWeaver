using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// The "content indexing is switched off" contract at the engine level (#1642). A deployment can have the
/// pipeline WIRED and still not be CONFIGURED for it; the services then resolve
/// <see cref="IInertContentIndex"/> stand-ins rather than the concrete store/embedder, and every search
/// surface must answer the documented <c>{count:0, message:…}</c> envelope carrying the stand-in's reason —
/// never an exception, and never a bare "no results" that reads as missing data.
/// </summary>
public class InertContentIndexTest
{
    private const string Reason = "Content indexing is not active on this deployment: configure Embedding:Endpoint.";

    private readonly InertChunkedContentVectorStore _store = new(Reason);
    private readonly InertChunkEmbedder _embedder = new(Reason);
    private readonly FakeEmbedder _liveEmbedder = new();
    private readonly InMemoryChunkedContentVectorStore _liveStore = new();

    private static async Task<ContentSearchResult> Anchored(
        IChunkedContentVectorStore? store, IChunkEmbedder? embedder) =>
        await ContentChunkSearch.Search(store, embedder, "benefit obligation", "ACME/content", 20)
            .FirstAsync().ToTask();

    private static async Task<ContentSearchResult> Grammar(
        IChunkedContentVectorStore? store, IChunkEmbedder? embedder) =>
        await ContentChunkSearch.Search(
                store, embedder, "namespace:ACME/content scope:subtree benefit obligation", null, 20)
            .FirstAsync().ToTask();

    [Fact]
    public void IsOff_TrueForNullAndForInert_FalseOnlyWhenBothAreLive()
    {
        Assert.True(ContentIndexAvailability.IsOff(null, null));
        Assert.True(ContentIndexAvailability.IsOff(_store, _liveEmbedder));
        Assert.True(ContentIndexAvailability.IsOff(_liveStore, _embedder));
        Assert.False(ContentIndexAvailability.IsOff(_liveStore, _liveEmbedder));
    }

    [Fact]
    public void ReasonOff_PrefersTheStandIns_Reason_OverTheGenericFallback()
    {
        Assert.Equal(Reason, ContentIndexAvailability.ReasonOff(_store, _liveEmbedder));
        Assert.Equal(Reason, ContentIndexAvailability.ReasonOff(_liveStore, _embedder));
        // Nothing registered at all (the pipeline was never wired) has no deployment-specific reason.
        Assert.Equal(ContentIndexAvailability.NotEnabledMessage,
            ContentIndexAvailability.ReasonOff(null, null));
    }

    [Fact]
    public async Task Search_WithInertServices_ReturnsTheReason_InBothQueryShapes()
    {
        foreach (var result in new[] { await Anchored(_store, _embedder), await Grammar(_store, _embedder) })
        {
            Assert.Empty(result.Hits);
            Assert.Equal(Reason, result.Message);
            // The tool envelope the MCP/agent surface actually returns.
            var json = ContentChunkSearch.ToJson(result);
            Assert.Contains("\"count\":0", json);
            Assert.Contains(Reason, json);
        }
    }

    [Fact]
    public async Task Search_WithInertServices_NeverEmbeds()
    {
        // The inert embedder faults if asked to embed — reaching it would mean the availability check was
        // skipped and a meaningless zero vector got ranked. Both query shapes must settle before that.
        Assert.NotNull((await Anchored(_liveStore, _embedder)).Message);
        Assert.NotNull((await Grammar(_liveStore, _embedder)).Message);
    }

    [Fact]
    public async Task InertStore_AnswersEveryReadEmptily_AndDropsWrites()
    {
        Assert.Null(await _store.GetFileHash("ACME/content", "a.txt").FirstAsync().ToTask());
        Assert.Equal(0, await _store.GetChunkCount("ACME/content", "a.txt").FirstAsync().ToTask());
        Assert.Null(await _store.GetChunk("ACME/content", "a.txt", 0).FirstAsync().ToTask());
        Assert.Empty(await _store.Search("ACME/content", [1f], 5).FirstAsync().ToTask());
        Assert.Empty(await _store.SearchSubtree("ACME", [1f], 5).FirstAsync().ToTask());
        // A write completes (uploads proceed) and stores nothing.
        await _store.ReplaceFileChunks("ACME/content", "a.txt", []).FirstAsync().ToTask();
        Assert.Equal(0, await _store.GetChunkCount("ACME/content", "a.txt").FirstAsync().ToTask());
    }
}
