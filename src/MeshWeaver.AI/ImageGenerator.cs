using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Default <see cref="IImageGenerator"/> — the raster counterpart to <see cref="IconGenerator"/>.
/// Selects an image-capable <see cref="ModelDefinition"/> (<see cref="ModelCapability.Image"/>),
/// resolves its endpoint + (decrypted) key via <see cref="ChatClientCredentialResolver"/>, and
/// dispatches by the model's <see cref="ModelDefinition.Provider"/> to a backend:
/// <list type="bullet">
///   <item><c>AzureOpenAI</c> → Azure OpenAI Images (<c>/openai/deployments/{id}/images/generations</c>).</item>
///   <item><c>OpenAI</c> (or any OpenAI-compatible gateway) → <c>/v1/images/generations</c>.</item>
///   <item><c>Automatic1111</c> / <c>ComfyUI</c> / <c>StableDiffusion</c> / <c>Local</c> → A1111 <c>/sdapi/v1/txt2img</c>.</item>
///   <item><c>Ollama</c> → surfaced as unsupported (Ollama has no text-to-image endpoint).</item>
/// </list>
/// Every HTTP round runs through the shared <see cref="IoPoolNames.Http"/> I/O pool — never on the
/// hub scheduler, never <c>Observable.FromAsync</c> — and the public surface stays
/// <see cref="IObservable{T}"/> end to end.
/// </summary>
public sealed class ImageGenerator : IImageGenerator
{
    private readonly IMeshService meshService;
    private readonly ChatClientCredentialResolver resolver;
    private readonly IIoPool httpPool;
    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly ILogger<ImageGenerator>? logger;

    /// <summary>Creates the generator, resolving its collaborators from <paramref name="services"/>.</summary>
    public ImageGenerator(IServiceProvider services)
    {
        meshService = services.GetRequiredService<IMeshService>();
        resolver = services.GetRequiredService<ChatClientCredentialResolver>();
        httpPool = services.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Http);
        // Named, factory-pooled client — never a raw `new HttpClient()` per transient instance
        // (handler/socket exhaustion). AddAgentChatServices registers the named client + the factory.
        httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ImageGenerator));
        jsonOptions = services.GetRequiredService<IMessageHub>().JsonSerializerOptions;
        logger = services.GetService<ILogger<ImageGenerator>>();
    }

    /// <inheritdoc />
    public IObservable<GeneratedImage> GenerateImageAsync(
        string prompt, string? size = null, string? modelId = null, CancellationToken ct = default)
        => ResolveImageModel(modelId)
            .SelectMany(def =>
            {
                // Endpoint + key from the ModelProvider node (ApiKey already decrypted), falling back
                // to any endpoint stamped directly on the model.
                var creds = resolver.Resolve(def.Id);
                var endpoint = FirstNonEmpty(creds.Endpoint, def.Endpoint);
                // Bound + off-hub: the whole HTTP round runs on the shared Http I/O pool. Link the
                // caller's token with the pool's so an explicit cancellation is honoured too.
                return httpPool.Invoke(async token =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                    return await GenerateCore(def, endpoint, creds.ApiKey, prompt, size, linked.Token).ConfigureAwait(false);
                });
            });

    /// <summary>
    /// Picks the image model: the one whose id matches <paramref name="modelId"/>, or — when none is
    /// given — the first <see cref="ModelCapability.Image"/> model by <see cref="ModelDefinition.Order"/>.
    /// Listing models is a legitimate query use (not a single-node content read).
    /// </summary>
    private IObservable<ModelDefinition> ResolveImageModel(string? modelId)
        => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{LanguageModelNodeType.NodeType}"))
            .Take(1)
            .Select(change =>
            {
                var defs = change.Items
                    .Select(n => n.ContentAs<ModelDefinition>(jsonOptions, logger))
                    .Where(d => d is not null)
                    .Select(d => d!)
                    .ToList();
                var chosen = !string.IsNullOrWhiteSpace(modelId)
                    ? defs.FirstOrDefault(d => string.Equals(d.Id, modelId, StringComparison.OrdinalIgnoreCase))
                    : defs.Where(d => d.Capability == ModelCapability.Image)
                          .OrderBy(d => d.Order).ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault();
                return chosen ?? throw new InvalidOperationException(
                    "No image-generation model is configured. Create a Model with Capability = Image under a Provider " +
                    "(Azure OpenAI Images, OpenAI, or a local Stable-Diffusion endpoint such as Automatic1111 / ComfyUI).");
            });

    private async Task<GeneratedImage> GenerateCore(
        ModelDefinition def, string? endpoint, string? apiKey, string prompt, string? size, CancellationToken ct)
    {
        var provider = (def.Provider ?? string.Empty).Trim();
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Ollama does not support image generation. Use Azure OpenAI Images, OpenAI, or a local " +
                "Stable-Diffusion endpoint (Automatic1111 / ComfyUI).");
        if (IsLocalStableDiffusion(provider))
            return await GenerateStableDiffusion(endpoint, apiKey, prompt, size, ct).ConfigureAwait(false);
        // OpenAI + Azure OpenAI + any OpenAI-compatible gateway share the /images/generations shape.
        return await GenerateOpenAiImages(
            azure: provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase),
            def, endpoint, apiKey, prompt, size, ct).ConfigureAwait(false);
    }

    private async Task<GeneratedImage> GenerateOpenAiImages(
        bool azure, ModelDefinition def, string? endpoint, string? apiKey, string prompt, string? size, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["prompt"] = prompt, ["n"] = 1 };
        if (!string.IsNullOrWhiteSpace(size)) body["size"] = size;

        string url;
        HttpRequestMessage request;
        if (azure)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("The Azure OpenAI image model needs an Endpoint on its Provider node.");
            url = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(def.Id)}/images/generations?api-version=2024-10-21";
            request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("api-key", apiKey);
        }
        else
        {
            var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com" : endpoint.TrimEnd('/');
            url = $"{baseUrl}/v1/images/generations";
            body["model"] = def.Id;
            request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Content = JsonBody(body);

        using (request)
        using (var resp = await httpClient.SendAsync(request, ct).ConfigureAwait(false))
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Image API returned {(int)resp.StatusCode}: {Truncate(text)}");

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                throw new InvalidOperationException("Image API response contained no data.");
            var first = data[0];
            if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                return new GeneratedImage(Convert.FromBase64String(b64.GetString()!), "image/png");
            if (first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                using var imgResp = await httpClient.GetAsync(urlEl.GetString()!, ct).ConfigureAwait(false);
                imgResp.EnsureSuccessStatusCode();
                var mediaType = imgResp.Content.Headers.ContentType?.MediaType ?? "image/png";
                var bytes = await imgResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return new GeneratedImage(bytes, mediaType);
            }
            throw new InvalidOperationException("Image API response contained no image bytes.");
        }
    }

    private async Task<GeneratedImage> GenerateStableDiffusion(
        string? endpoint, string? apiKey, string prompt, string? size, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:7860" : endpoint).TrimEnd('/');
        var (width, height) = ParseSize(size, 512);
        var body = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["steps"] = 20,
            ["width"] = width,
            ["height"] = height,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sdapi/v1/txt2img") { Content = JsonBody(body) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stable-Diffusion endpoint returned {(int)resp.StatusCode}: {Truncate(text)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new InvalidOperationException("Stable-Diffusion response contained no images.");
        var b64 = images[0].GetString() ?? throw new InvalidOperationException("Stable-Diffusion image was empty.");
        // Strip an optional data: URI prefix ("data:image/png;base64,....").
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = b64.IndexOf(',');
            if (comma >= 0) b64 = b64[(comma + 1)..];
        }
        return new GeneratedImage(Convert.FromBase64String(b64), "image/png");
    }

    private static HttpContent JsonBody(object body)
        => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static bool IsLocalStableDiffusion(string provider)
        => provider.Equals("Automatic1111", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("A1111", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("StableDiffusion", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("ComfyUI", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("Local", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static (int width, int height) ParseSize(string? size, int fallback)
    {
        if (!string.IsNullOrWhiteSpace(size))
        {
            var parts = size.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                return (w, h);
        }
        return (fallback, fallback);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
