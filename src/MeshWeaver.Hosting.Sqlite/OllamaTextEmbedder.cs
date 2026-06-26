using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// <see cref="ITextEmbedder"/> backed by an OpenAI-compatible <c>/v1/embeddings</c> endpoint —
/// e.g. a local <b>Ollama</b> hosting <c>bge-m3</c> / <c>nomic-embed-text</c>. Keeps the on-device
/// mesh's embeddings fully local: no cloud round-trip, and it reuses the very model server the
/// chat client already talks to.
/// </summary>
/// <remarks>
/// A finite <see cref="HttpClient.Timeout"/> is mandatory — a hung embedding leaf would otherwise
/// pin an <c>IIoPool</c> slot (and, on a device with no model server reachable, slow every write).
/// Keep it short on mobile.
/// </remarks>
public sealed class OllamaTextEmbedder : ITextEmbedder
{
    /// <summary>Vector dimensions for the embedding models Ollama commonly serves.</summary>
    private static readonly FrozenDictionary<string, int> ModelDimensions =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["bge-m3"] = 1024,
            ["nomic-embed-text"] = 768,
            ["mxbai-embed-large"] = 1024,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Best-effort dimension lookup for a known model; 1024 (bge-m3) otherwise.</summary>
    public static int DimensionsFor(string model) => ModelDimensions.GetValueOrDefault(model, 1024);

    private readonly HttpClient _client;
    private readonly string _model;
    private readonly int _dimensions;

    public OllamaTextEmbedder(string endpoint, string model, int? dimensions = null,
        string? apiKey = null, TimeSpan? timeout = null)
    {
        // Endpoint is the OpenAI-compatible base (e.g. http://localhost:11434/v1); the request
        // resolves against "<base>/embeddings".
        var baseAddress = endpoint.TrimEnd('/') + "/";
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", string.IsNullOrEmpty(apiKey) ? "ollama" : apiKey);
        _model = model;
        _dimensions = dimensions ?? DimensionsFor(model);
    }

    public int Dimensions => _dimensions;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var response = await _client
            .PostAsJsonAsync("embeddings", new EmbeddingRequest(_model, text), ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<EmbeddingResponse>(ct).ConfigureAwait(false);
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
