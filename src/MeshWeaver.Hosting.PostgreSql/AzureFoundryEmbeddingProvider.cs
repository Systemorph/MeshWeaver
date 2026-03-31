using Azure;
using Azure.AI.Inference;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Embedding provider using Cohere embed-v4 via Azure AI Foundry.
/// </summary>
public class AzureFoundryEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingsClient _client;
    private readonly string _model;
    private readonly int _dimensions;

    public AzureFoundryEmbeddingProvider(string endpoint, string apiKey,
        string model = "cohere-embed-v-4-0", int dimensions = 1536)
    {
        _client = new EmbeddingsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _model = model;
        _dimensions = dimensions;
    }

    public int Dimensions => _dimensions;

    public async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var options = new EmbeddingsOptions([text]) { Model = _model };
        var response = await _client.EmbedAsync(options);
        return response.Value.Data[0].Embedding.ToObjectFromJson<float[]>();
    }
}
