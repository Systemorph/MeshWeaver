using System.Collections.Frozen;

namespace MeshWeaver.Hosting.Embeddings;

/// <summary>
/// Configuration options for the embedding provider. Bind from the "Embedding" config section.
/// <see cref="Provider"/> selects the backend: Azure AI Foundry (cloud) or an
/// OpenAI-compatible <c>/v1/embeddings</c> endpoint such as a local Ollama (on-host).
/// </summary>
public class EmbeddingOptions
{
    private static readonly FrozenDictionary<string, int> ModelDimensions =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["embed-v-4-0"] = 1536,
            ["text-embedding-3-small"] = 1536,
            ["text-embedding-3-large"] = 3072,
            ["text-embedding-ada-002"] = 1536,
            // Local Ollama embedding models (OpenAI-compatible /v1/embeddings).
            ["bge-m3"] = 1024,
            ["nomic-embed-text"] = 768,
            ["mxbai-embed-large"] = 1024,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private int? _dimensions;

    /// <summary>
    /// Backend selector. "AzureFoundry" (default) → <see cref="AzureFoundryEmbeddingProvider"/>;
    /// "Ollama" / "OpenAICompatible" → <see cref="OllamaEmbeddingProvider"/> against
    /// <see cref="Endpoint"/>'s <c>/v1/embeddings</c>.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Base URI of the embedding endpoint (Azure AI Foundry, or an OpenAI-compatible base).</summary>
    public string? Endpoint { get; set; }

    /// <summary>API key / bearer credential for the embedding endpoint, when required.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding model / deployment name. Defaults to <c>embed-v-4-0</c>.</summary>
    public string Model { get; set; } = "embed-v-4-0";

    /// <summary>
    /// Request timeout (seconds) for the OpenAI-compatible provider. A finite bound is
    /// required so a hung embedding leaf never pins an <c>IIoPool</c> slot.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Vector dimensions. Auto-derived from <see cref="Model"/> for known models; defaults to 1536.
    /// </summary>
    public int Dimensions
    {
        get => _dimensions ?? GetDefaultDimensions(Model);
        set => _dimensions = value;
    }

    /// <summary>
    /// Returns the default vector dimensionality for a known <paramref name="model"/>,
    /// or 1536 when the model is not in the built-in lookup.
    /// </summary>
    /// <param name="model">The embedding model name to resolve dimensions for.</param>
    /// <returns>The model's known dimension count, otherwise 1536.</returns>
    public static int GetDefaultDimensions(string model)
        => ModelDimensions.GetValueOrDefault(model, 1536);
}
