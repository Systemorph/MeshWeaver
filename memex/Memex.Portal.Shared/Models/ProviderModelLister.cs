using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Models;

/// <summary>
/// Fetches the <b>live model list</b> from a provider's HTTP API so the
/// Settings → Language Models tab can let the user pick which models to bring,
/// instead of relying on a static baked-in id list. Powers the generic
/// "add provider → URL + key → fetch models → select" flow (the OpenRouter /
/// Groq / Together / vLLM path, and direct OpenAI).
///
/// <para>🚨 Reactive end-to-end: the HTTP leaf runs inside the
/// <see cref="IoPoolNames.Http"/> <see cref="IIoPool"/> and the public surface is
/// <see cref="IObservable{T}"/> — no <c>async</c>/<c>await</c>/<c>Task</c> escapes a
/// signature (mirrors <c>GitHubOAuthService</c>; see
/// <c>Doc/Architecture/ControlledIoPooling.md</c>). The pool is resolved from the
/// hub's service provider so it shares the mesh's I/O bound.</para>
///
/// <para>Two wire shapes are handled, both returning <c>{ "data": [ { "id" } ] }</c>:
/// the OpenAI family (Bearer auth, <c>{baseUrl}/models</c> — covers OpenAI and every
/// OpenAI-compatible gateway) and Anthropic (<c>x-api-key</c> + <c>anthropic-version</c>,
/// <c>https://api.anthropic.com/v1/models</c>). Providers without a supported list
/// endpoint surface their error to the UI, which falls back to the catalog defaults +
/// manual entry.</para>
/// </summary>
public sealed class ProviderModelLister
{
    private readonly IMessageHub hub;
    private readonly HttpClient http;
    private readonly ILogger<ProviderModelLister>? logger;

    public ProviderModelLister(IMessageHub hub, ILogger<ProviderModelLister>? logger = null, HttpClient? httpClient = null)
    {
        this.hub = hub;
        this.logger = logger;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-ModelLister");
    }

    private IIoPool Http => hub.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Http);

    /// <summary>
    /// Live model ids offered by the provider at <paramref name="endpoint"/> for the
    /// given <paramref name="apiKey"/>. <paramref name="providerName"/> selects the wire
    /// shape (Anthropic vs the OpenAI family). Sorted, de-duplicated. Throws (via
    /// OnError) on a non-success response so the UI can show the reason and fall back.
    /// </summary>
    /// <param name="allowKeyless">
    /// When <c>true</c>, a blank <paramref name="apiKey"/> is permitted and the request is sent
    /// without an <c>Authorization</c> header — for keyless local OpenAI-compatible endpoints
    /// (Ollama, a bare vLLM/LM Studio) whose <c>/v1/models</c> needs no credential. The interactive
    /// add-provider flow keeps the default (<c>false</c>): a user pasting a remote provider URL still
    /// gets the "API key required" error rather than a silent 401.
    /// </param>
    public IObservable<IReadOnlyList<string>> ListModels(string? endpoint, string apiKey, string? providerName = null, bool allowKeyless = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey) && !allowKeyless)
            return Observable.Throw<IReadOnlyList<string>>(
                new InvalidOperationException("An API key is required to fetch the model list."));
        return Http.Invoke(ct => ListAsync(endpoint, apiKey, providerName, ct));
    }

    // ── HTTP leaf (runs inside the I/O pool) ──────────────────────────────────
    private async Task<IReadOnlyList<string>> ListAsync(
        string? endpoint, string apiKey, string? providerName, CancellationToken ct)
    {
        var isAnthropic = string.Equals(providerName, "Anthropic", StringComparison.OrdinalIgnoreCase);

        // OpenAI family: {baseUrl}/models. Blank endpoint → the OpenAI default. The
        // base URL must include its version segment (e.g. .../v1), which is the
        // OpenAI-compatible convention; OpenRouter's "https://openrouter.ai/api/v1"
        // → "https://openrouter.ai/api/v1/models".
        var url = isAnthropic
            ? "https://api.anthropic.com/v1/models"
            : (string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint.Trim())
                .TrimEnd('/') + "/models";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (isAnthropic)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Keyless endpoints (Ollama) send no Authorization header; a remote OpenAI-family
            // endpoint always has a non-blank key here (the ListModels guard enforces it).
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger?.LogInformation("Model list fetch from {Url} failed: {Status}", url, (int)resp.StatusCode);
            throw new InvalidOperationException(
                $"The provider rejected the model-list request ({(int)resp.StatusCode} {resp.StatusCode}). {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
        }

        return ids.Where(s => !string.IsNullOrWhiteSpace(s))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200] + "…");

    /// <summary>
    /// Whether <paramref name="modelId"/> supports tool/function calling, per the endpoint's declared
    /// capabilities. Ollama-specific: the OpenAI-wire base has no capabilities endpoint, so this hits
    /// <c>{base}/api/show</c> (dropping a trailing <c>/v1</c>) and checks whether <c>capabilities</c>
    /// contains <c>"tools"</c>. Returns:
    /// <list type="bullet">
    ///   <item><c>true</c> — capabilities include <c>tools</c>.</item>
    ///   <item><c>false</c> — capabilities were returned and do NOT include <c>tools</c> (a definitively
    ///     tool-less model, e.g. a roleplay model reporting <c>[completion]</c>).</item>
    ///   <item><c>null</c> — indeterminate (not Ollama / no <c>/api/show</c> / probe failed): the caller
    ///     assumes tools ARE supported (historical behaviour).</item>
    /// </list>
    /// Runs on the <see cref="IoPoolNames.Http"/> pool; never throws (probe failures surface as null).
    /// </summary>
    public IObservable<bool?> SupportsTools(string? endpoint, string modelId) =>
        Http.Invoke(ct => SupportsToolsAsync(endpoint, modelId, ct));

    private async Task<bool?> SupportsToolsAsync(string? endpoint, string modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelId))
            return null;
        var baseUrl = endpoint.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3].TrimEnd('/');
        var url = baseUrl + "/api/show";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    $"{{\"model\":{JsonSerializer.Serialize(modelId)}}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null; // not an Ollama endpoint, or the model is unknown → indeterminate
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("capabilities", out var caps)
                || caps.ValueKind != JsonValueKind.Array)
                return null; // no capabilities array → indeterminate
            foreach (var c in caps.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String
                    && string.Equals(c.GetString(), "tools", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false; // capabilities present but no "tools" → definitively unsupported
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Capability probe (/api/show) failed for {Model} at {Url}", modelId, url);
            return null;
        }
    }
}
