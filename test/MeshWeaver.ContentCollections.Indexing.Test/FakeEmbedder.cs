using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.ContentCollections.Indexing;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Deterministic in-process <see cref="IChunkEmbedder"/> for tests. The vector is derived
/// from the SHA-256 of the text, so it is stable across runs and requires no network — yet
/// distinct texts get distinct (and L2-comparable) vectors, which is enough for the cosine
/// search assertions.
/// </summary>
public sealed class FakeEmbedder : IChunkEmbedder
{
    public int Dimensions => 8;

    public IObservable<float[]> Embed(string text) =>
        Observable.Defer(() => Observable.Return(Vectorize(text)));

    private float[] Vectorize(string text)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
            // Map each of the first Dimensions hash bytes into [-1, 1].
            vector[i] = (digest[i] / 255f) * 2f - 1f;
        return vector;
    }
}
