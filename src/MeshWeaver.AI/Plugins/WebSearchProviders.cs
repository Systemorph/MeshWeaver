using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Selects which web-search backend <see cref="WebSearchPlugin"/> uses. Bound from
/// <c>WebSearch:Provider</c>. <see cref="None"/> (the default) auto-detects from whatever
/// credentials are present (Google first, then the retired Bing path); an explicit value
/// forces that backend.
/// </summary>
public enum WebSearchProviderType
{
    /// <summary>No explicit selection — auto-detect from configured credentials.</summary>
    None = 0,

    /// <summary>Google Programmable Search (Custom Search JSON API).</summary>
    Google,

    /// <summary>Bing Search v7 — RETIRED by Microsoft on 2025-08-11; kept only for back-compat.</summary>
    Bing,
}

/// <summary>
/// One web-search hit: title, URL and snippet. The provider-neutral result shape the
/// <see cref="IWebSearchProvider"/> seam maps every backend's payload into.
/// </summary>
/// <param name="Title">Result title / heading.</param>
/// <param name="Url">Result URL.</param>
/// <param name="Snippet">Short text excerpt.</param>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// The swappable web-search backend seam. Implementations bridge their single HTTP round-trip
/// through the <see cref="IoPoolNames.Http"/> <see cref="IIoPool"/> and return results reactively.
/// A backend that lacks credentials reports <see cref="IsConfigured"/> = <c>false</c> so the
/// plugin can decline to advertise the <c>SearchWeb</c> tool (never advertise what it can't fulfil).
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>Provider identity (for logs/diagnostics).</summary>
    WebSearchProviderType Kind { get; }

    /// <summary>True when the backend has the credentials it needs to actually run a search.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Searches the web. Returns a cold observable that runs the HTTP leaf on the I/O pool when
    /// subscribed and emits the mapped results once. May surface <see cref="HttpRequestException"/>
    /// via <c>OnError</c> — the caller formats that into a user-facing message.
    /// </summary>
    /// <param name="query">Search query string.</param>
    /// <param name="count">Requested result count (each provider clamps to its own maximum).</param>
    IObservable<IReadOnlyList<WebSearchResult>> Search(string query, int count);
}

/// <summary>
/// Google Programmable Search (Custom Search JSON API) backend configuration. The deployment
/// supplies both keys (see the Helm ConfigMap <c>WebSearch__Google__*</c>); with either missing
/// the provider is inert and <c>SearchWeb</c> is not advertised.
/// </summary>
public class GoogleWebSearchConfiguration
{
    /// <summary>Google API key (<c>WebSearch:Google:ApiKey</c>). Supplied by the deployment.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Programmable Search Engine id / <c>cx</c> (<c>WebSearch:Google:Cx</c>). Supplied by the deployment.</summary>
    public string? Cx { get; set; }

    /// <summary>Custom Search JSON API endpoint.</summary>
    public string Endpoint { get; set; } = "https://www.googleapis.com/customsearch/v1";
}

/// <summary>
/// Google Custom Search JSON API provider. Calls
/// <c>GET {endpoint}?key={key}&amp;cx={cx}&amp;q={query}&amp;num={count}</c> (num capped at 10 by
/// Google) and maps each <c>items[]</c> entry's <c>title</c>/<c>link</c>/<c>snippet</c> to a
/// <see cref="WebSearchResult"/>. The single HTTP round-trip is one pooled async leaf.
/// </summary>
internal sealed class GoogleWebSearchProvider(
    GoogleWebSearchConfiguration config,
    HttpClient httpClient,
    IIoPool ioPool,
    ILogger logger) : IWebSearchProvider
{
    /// <summary>Google Custom Search caps <c>num</c> at 10 per request.</summary>
    private const int MaxResults = 10;

    public WebSearchProviderType Kind => WebSearchProviderType.Google;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.ApiKey) && !string.IsNullOrWhiteSpace(config.Cx);

    public IObservable<IReadOnlyList<WebSearchResult>> Search(string query, int count)
    {
        var num = Math.Clamp(count, 1, MaxResults);

        // The HTTP round-trip is ONE pooled async leaf — async lives only inside the
        // IIoPool bridge, never on the subscribing thread (AsynchronousCalls.md).
        return ioPool.Invoke(async ct =>
        {
            var uri = $"{config.Endpoint}?key={Uri.EscapeDataString(config.ApiKey!)}" +
                      $"&cx={Uri.EscapeDataString(config.Cx!)}" +
                      $"&q={Uri.EscapeDataString(query)}&num={num}";

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = ImmutableList<WebSearchResult>.Empty;
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    results = results.Add(new WebSearchResult(
                        GetString(item, "title"),
                        GetString(item, "link"),
                        GetString(item, "snippet")));
                }
            }

            logger.LogInformation("Google Custom Search returned {Count} result(s) for query={Query}", results.Count, query);
            return (IReadOnlyList<WebSearchResult>)results;
        });
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// Bing Search v7 provider. RETIRED — Microsoft shut the Bing Search API down on 2025-08-11, so
/// this path no longer resolves against a live service. Kept behind <c>WebSearch:Provider = Bing</c>
/// (and only advertised when a legacy <c>BingApiKey</c> is present) purely for back-compat; new
/// deployments use <see cref="GoogleWebSearchProvider"/>.
/// </summary>
internal sealed class BingWebSearchProvider(
    string? apiKey,
    string endpoint,
    HttpClient httpClient,
    IIoPool ioPool,
    ILogger logger) : IWebSearchProvider
{
    public WebSearchProviderType Kind => WebSearchProviderType.Bing;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

    public IObservable<IReadOnlyList<WebSearchResult>> Search(string query, int count)
    {
        var clamped = Math.Clamp(count, 1, 20);

        return ioPool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{endpoint}?q={Uri.EscapeDataString(query)}&count={clamped}&textFormat=Plain");
            request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = ImmutableList<WebSearchResult>.Empty;
            if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                webPages.TryGetProperty("value", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    results = results.Add(new WebSearchResult(
                        page.GetProperty("name").GetString() ?? string.Empty,
                        page.GetProperty("url").GetString() ?? string.Empty,
                        page.GetProperty("snippet").GetString() ?? string.Empty));
                }
            }

            logger.LogInformation("Bing search returned {Count} result(s) for query={Query}", results.Count, query);
            return (IReadOnlyList<WebSearchResult>)results;
        });
    }
}
