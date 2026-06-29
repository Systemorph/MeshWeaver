using System.Reactive.Linq;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql;

/// <summary>
/// Adapts the framework's <see cref="IEmbeddingProvider"/> (MeshWeaver.Hosting.PostgreSql) to the
/// indexing core's <see cref="IChunkEmbedder"/>. The genuine network/compute embedding leaf
/// (<see cref="IEmbeddingProvider.GenerateEmbeddingAsync"/>) is pushed onto the <c>Http</c>
/// <see cref="IIoPool"/> so it runs off the hub scheduler and is bounded — never a bare
/// <c>Observable.FromAsync</c> (forbidden — see ControlledIoPooling.md).
/// </summary>
public sealed class EmbeddingProviderChunkEmbedder : IChunkEmbedder
{
    private readonly IEmbeddingProvider _provider;
    private readonly IIoPool _httpPool;

    /// <param name="provider">The framework embedding provider to wrap.</param>
    /// <param name="ioPoolRegistry">
    /// Mesh-scoped pool resolver; the embedder runs through the shared <c>Http</c> pool (the
    /// embedding endpoint is an outbound HTTP leaf). Falls back to <see cref="IoPool.Unbounded"/>
    /// only when constructed outside DI (tests).
    /// </param>
    public EmbeddingProviderChunkEmbedder(IEmbeddingProvider provider, IoPoolRegistry? ioPoolRegistry)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _httpPool = ioPoolRegistry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    /// <inheritdoc/>
    public int Dimensions => _provider.Dimensions;

    /// <inheritdoc/>
    public IObservable<float[]> Embed(string text) =>
        _httpPool.Invoke(ct => _provider.GenerateEmbeddingAsync(text))
            .Select(vector => vector ?? Array.Empty<float>());
}
