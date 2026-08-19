using System.Diagnostics.CodeAnalysis;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Marks the stand-ins the content-indexing pipeline registers when the deployment is NOT configured
/// for indexing (no vector store connection, no embedding provider). Every consumer of the indexing
/// services tests for this state and answers "the capability is off" — it must never surface as an
/// exception.
///
/// <para><b>Why a stand-in and not "no registration".</b> Whether the deployment is configured can
/// only be decided at RESOLVE time (a boot-loaded module has no <c>IConfiguration</c> when it folds
/// in), so the services ARE registered and the gate lives in the factory. The obvious shape — a
/// factory returning <c>null</c> so <c>GetService</c> answers null — does not work on the container
/// MeshWeaver actually runs: Autofac REFUSES a delegate registration that returns null and throws
/// <c>"An exception was thrown while activating λ:T"</c> out of <c>GetService</c>. An inert INSTANCE
/// is therefore the only shape in which "registered but switched off" can be resolved at all.</para>
/// </summary>
public interface IInertContentIndex
{
    /// <summary>
    /// Why content indexing is off on this deployment, phrased for a tool caller or a page — it is
    /// carried into the <c>search_chunks</c> envelope's <c>message</c>, so it must name what to
    /// configure rather than merely stating that nothing was found.
    /// </summary>
    string Reason { get; }
}

/// <summary>
/// The switched-off <see cref="IChunkedContentVectorStore"/>: every read answers the empty result and
/// every write is dropped, so nothing throws and nothing is silently half-indexed. Resolved only via
/// <see cref="ContentIndexAvailability.GetActiveChunkStore"/>, which maps it back to null for the
/// callers that branch on "no store".
/// </summary>
public sealed class InertChunkedContentVectorStore(string reason)
    : IChunkedContentVectorStore, IInertContentIndex
{
    /// <inheritdoc />
    public string Reason { get; } = reason;

    /// <inheritdoc />
    public IObservable<string?> GetFileHash(string collectionPath, string filePath) =>
        Observable.Return<string?>(null);

    /// <inheritdoc />
    public IObservable<Unit> ReplaceFileChunks(
        string collectionPath, string filePath, IReadOnlyList<ContentChunk> chunks) =>
        Observable.Return(Unit.Default);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<ContentChunk>> Search(string collectionPath, float[] query, int topK) =>
        Observable.Return<IReadOnlyList<ContentChunk>>(Array.Empty<ContentChunk>());

    /// <inheritdoc />
    public IObservable<IReadOnlyList<ContentChunk>> SearchSubtree(
        string collectionPathPrefix, float[] query, int topK) =>
        Observable.Return<IReadOnlyList<ContentChunk>>(Array.Empty<ContentChunk>());

    /// <inheritdoc />
    public IObservable<ContentChunk?> GetChunk(string collectionPath, string filePath, int chunkIndex) =>
        Observable.Return<ContentChunk?>(null);

    /// <inheritdoc />
    public IObservable<int> GetChunkCount(string collectionPath, string filePath) =>
        Observable.Return(0);
}

/// <summary>
/// The switched-off <see cref="IChunkEmbedder"/>. It never embeds: every caller resolves it through
/// <see cref="ContentIndexAvailability.GetActiveChunkEmbedder"/> (null when inert) or tests for
/// <see cref="IInertContentIndex"/> first, so reaching <see cref="Embed"/> would mean a caller skipped
/// the availability check — which must fail loudly rather than return a zero vector that ranks as a
/// silent, meaningless similarity.
/// </summary>
public sealed class InertChunkEmbedder(string reason) : IChunkEmbedder, IInertContentIndex
{
    /// <inheritdoc />
    public string Reason { get; } = reason;

    /// <inheritdoc />
    public IObservable<float[]> Embed(string text) =>
        Observable.Throw<float[]>(new InvalidOperationException(Reason));

    /// <inheritdoc />
    public int Dimensions => 0;
}

/// <summary>
/// Resolves the content-indexing services in the ONE shape every consumer already branches on:
/// a live service, or <c>null</c> when the deployment is not configured for content indexing.
/// </summary>
public static class ContentIndexAvailability
{
    /// <summary>
    /// The fallback message for the "indexing is off" envelope, used when no inert stand-in carried a
    /// deployment-specific <see cref="IInertContentIndex.Reason"/> (e.g. the pipeline was never wired
    /// into this host at all).
    /// </summary>
    public const string NotEnabledMessage =
        "Content chunk indexing is not enabled in this host — no chunk store is configured.";

    /// <summary>
    /// The live chunk store, or null when content indexing is off (never wired, or wired but not
    /// configured — see <see cref="IInertContentIndex"/>).
    /// </summary>
    public static IChunkedContentVectorStore? GetActiveChunkStore(this IServiceProvider services) =>
        Active(services.GetService<IChunkedContentVectorStore>());

    /// <summary>
    /// The live chunk embedder, or null when content indexing is off — the counterpart to
    /// <see cref="GetActiveChunkStore"/>.
    /// </summary>
    public static IChunkEmbedder? GetActiveChunkEmbedder(this IServiceProvider services) =>
        Active(services.GetService<IChunkEmbedder>());

    /// <summary>
    /// The reason indexing is off on this host, read from whichever registered service is an inert
    /// stand-in — the line a tool envelope or a page shows instead of "no results".
    /// </summary>
    public static string ReasonOff(this IServiceProvider services) =>
        ReasonOff(services.GetService<IChunkedContentVectorStore>(), services.GetService<IChunkEmbedder>());

    /// <summary>The reason indexing is off, from whichever of the two services is an inert stand-in.</summary>
    public static string ReasonOff(IChunkedContentVectorStore? store, IChunkEmbedder? embedder) =>
        (store as IInertContentIndex)?.Reason
        ?? (embedder as IInertContentIndex)?.Reason
        ?? NotEnabledMessage;

    /// <summary>
    /// Whether the pair can actually answer a search: false only when both are present and neither is an
    /// inert stand-in — which is exactly when the caller may dereference them, hence the
    /// <see cref="NotNullWhenAttribute"/> pair.
    /// </summary>
    public static bool IsOff(
        [NotNullWhen(false)] IChunkedContentVectorStore? store,
        [NotNullWhen(false)] IChunkEmbedder? embedder) =>
        store is null or IInertContentIndex || embedder is null or IInertContentIndex;

    private static T? Active<T>(T? service) where T : class =>
        service is IInertContentIndex ? null : service;
}
