namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Pluggable provider for generating text embeddings.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Generates an embedding vector from the given text.
    /// Returns null if embedding is not available.
    /// </summary>
    Task<float[]?> GenerateEmbeddingAsync(string text);

    /// <summary>
    /// The dimensionality of embedding vectors produced by this provider.
    /// </summary>
    int Dimensions { get; }
}
