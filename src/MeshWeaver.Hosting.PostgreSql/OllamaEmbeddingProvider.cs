using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Embedding provider backed by an OpenAI-compatible <c>/v1/embeddings</c> endpoint —
/// e.g. a local Ollama server hosting <c>bge-m3</c> / <c>nomic-embed-text</c>. Keeps
/// embeddings fully on-host (no cloud round-trip) and reuses the very model server the
/// chat provider already talks to (the in-cluster <c>ollama</c> Service).
/// </summary>
/// <remarks>
/// <see cref="GenerateEmbeddingAsync"/> is an I/O leaf: it is always invoked from inside
/// the PostgreSQL adapter's <c>IIoPool</c> (write path and query path both wrap it in
/// <c>pool.Invoke(...)</c>), never directly on a hub/grain turn — the same boundary the
/// async embedding call has always lived on. A finite <see cref="HttpClient.Timeout"/>
/// is mandatory: a hung leaf would otherwise pin a pool slot indefinitely.
/// </remarks>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _client;
    private readonly string _model;
    private readonly int _dimensions;

    public OllamaEmbeddingProvider(string endpoint, string model, int dimensions,
        string? apiKey = null, TimeSpan? timeout = null)
    {
        // Endpoint is the OpenAI-compatible base (e.g. http://ollama:11434/v1); the
        // request resolves against "<base>/embeddings".
        var baseAddress = endpoint.TrimEnd('/') + "/";
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        // Ollama ignores the bearer; other OpenAI-compatible servers may require one.
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", string.IsNullOrEmpty(apiKey) ? "ollama" : apiKey);
        _model = model;
        _dimensions = dimensions;
    }

    public int Dimensions => _dimensions;

    public async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var response = await _client
            .PostAsJsonAsync("embeddings", new EmbeddingRequest(_model, text))
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<EmbeddingResponse>().ConfigureAwait(false);
        return payload?.Data is { Count: > 0 } data ? data[0].Embedding : null;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingData> Data);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
