namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Small embedding abstraction owned by the indexing core, so it does not take a heavy
/// dependency on any concrete embedding stack. A thin adapter from the framework's
/// <c>IEmbeddingProvider</c> (MeshWeaver.Hosting.PostgreSql) can layer on top later without
/// changing this contract; tests supply a deterministic fake.
/// </summary>
public interface IChunkEmbedder
{
    /// <summary>
    /// Embeds <paramref name="text"/> into a vector of length <see cref="Dimensions"/>. Reactive:
    /// the genuine network/compute leaf is expected to run through an <c>IIoPool</c> internally and
    /// emit a single vector. Composed (never awaited) by <see cref="ContentIndexingService"/>.
    /// </summary>
    IObservable<float[]> Embed(string text);

    /// <summary>Dimensionality of every vector this embedder produces.</summary>
    int Dimensions { get; }
}
