namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// No-op embedding provider for scenarios where vector search is not needed.
/// </summary>
public class NullEmbeddingProvider : IEmbeddingProvider
{
    public static readonly NullEmbeddingProvider Instance = new();

    public Task<float[]?> GenerateEmbeddingAsync(string text) => Task.FromResult<float[]?>(null);

    public int Dimensions => 0;
}
