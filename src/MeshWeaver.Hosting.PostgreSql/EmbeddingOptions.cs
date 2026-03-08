using System.Collections.Frozen;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Configuration options for the Azure Foundry embedding provider.
/// Bind from "Embedding" config section.
/// </summary>
public class EmbeddingOptions
{
    private static readonly FrozenDictionary<string, int> ModelDimensions =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cohere-embed-v-4-0"] = 1024,
            ["text-embedding-3-small"] = 1536,
            ["text-embedding-3-large"] = 3072,
            ["text-embedding-ada-002"] = 1536,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private int? _dimensions;

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "cohere-embed-v-4-0";

    /// <summary>
    /// Vector dimensions. Auto-derived from <see cref="Model"/> for known models; defaults to 1024.
    /// </summary>
    public int Dimensions
    {
        get => _dimensions ?? GetDefaultDimensions(Model);
        set => _dimensions = value;
    }

    public static int GetDefaultDimensions(string model)
        => ModelDimensions.GetValueOrDefault(model, 1024);
}
