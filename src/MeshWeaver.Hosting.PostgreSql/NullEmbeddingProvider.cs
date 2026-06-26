namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// No-op embedding provider for scenarios where vector search is not needed.
/// </summary>
public class NullEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Shared singleton instance of the no-op provider.</summary>
    public static readonly NullEmbeddingProvider Instance = new();

    /// <inheritdoc />
    public Task<float[]?> GenerateEmbeddingAsync(string text) => Task.FromResult<float[]?>(null);

    /// <inheritdoc />
    public int Dimensions => 0;
}
