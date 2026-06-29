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
    public IObservable<IReadOnlyList<string>> ListModels(string? endpoint, string apiKey, string? providerName = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
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
        else
        {
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
}
