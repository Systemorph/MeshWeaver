using Azure;
using Azure.AI.Inference;

namespace MeshWeaver.Hosting.Embeddings;

/// <summary>
/// Embedding provider using Cohere embed-v4 via Azure AI Foundry.
/// </summary>
public class AzureFoundryEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingsClient _client;
    private readonly string _model;
    private readonly int _dimensions;

    /// <summary>
    /// Initializes the provider against an Azure AI Foundry inference endpoint.
    /// </summary>
    /// <param name="endpoint">Base URI of the Azure AI Foundry inference endpoint.</param>
    /// <param name="apiKey">API key credential for the endpoint.</param>
    /// <param name="model">Deployment/model name used for embedding requests.</param>
    /// <param name="dimensions">Dimensionality of the embedding vectors the model returns.</param>
    public AzureFoundryEmbeddingProvider(string endpoint, string apiKey,
        string model = "embed-v-4-0", int dimensions = 1536)
    {
        _client = new EmbeddingsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _model = model;
        _dimensions = dimensions;
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var options = new EmbeddingsOptions([text]) { Model = _model };
        var response = await _client.EmbedAsync(options).ConfigureAwait(false);
        return response.Value.Data[0].Embedding.ToObjectFromJson<float[]>();
    }
}
