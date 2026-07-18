using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Configuration for the WebSearch plugin. Bound from the <c>WebSearch</c> section.
/// </summary>
public class WebSearchConfiguration
{
    /// <summary>
    /// Which web-search backend to use (<c>WebSearch:Provider</c>). Defaults to
    /// <see cref="WebSearchProviderType.None"/>, which auto-detects from whatever credentials are
    /// present (Google first, then the retired Bing path). When no backend is configured the
    /// <c>SearchWeb</c> tool is not advertised at all.
    /// </summary>
    public WebSearchProviderType Provider { get; set; } = WebSearchProviderType.None;

    /// <summary>
    /// Google Programmable Search (Custom Search JSON API) settings (<c>WebSearch:Google:*</c>).
    /// The deployment supplies <c>ApiKey</c> and <c>Cx</c>.
    /// </summary>
    public GoogleWebSearchConfiguration Google { get; set; } = new();

    /// <summary>
    /// Bing Search API subscription key. RETIRED (Bing Search was shut down 2025-08-11) — present
    /// only for back-compat; prefer <see cref="Google"/>.
    /// </summary>
    public string? BingApiKey { get; set; }

    /// <summary>
    /// Bing Search API endpoint (retired).
    /// </summary>
    public string Endpoint { get; set; } = "https://api.bing.microsoft.com/v7.0/search";

    /// <summary>
    /// Maximum content length (characters) when fetching web pages.
    /// </summary>
    public int MaxFetchContentLength { get; set; } = 50_000;
}

/// <summary>
/// Plugin providing web search and web page fetching tools for AI agents.
/// Register via <c>AIExtensions.AddWebSearchPlugin</c>.
/// </summary>
public class WebSearchPlugin : IAgentPlugin
{
    private readonly HttpClient httpClient;
    private readonly WebSearchConfiguration config;
    private readonly ILogger<WebSearchPlugin> logger;
    private readonly IIoPool ioPool;

    /// <summary>
    /// The selected search backend, or <c>null</c>/unconfigured when no provider is set up.
    /// When this is not configured the <c>SearchWeb</c> tool is not advertised.
    /// </summary>
    private readonly IWebSearchProvider? searchProvider;

    /// <summary>The plugin's stable identifier, <c>WebSearch</c>.</summary>
    public string Name => "WebSearch";

    /// <summary>True when a search backend is configured and <c>SearchWeb</c> should be advertised.</summary>
    private bool SearchConfigured => searchProvider is { IsConfigured: true };

    /// <summary>
    /// Creates the plugin, resolving the named <c>Http</c> I/O pool through which every
    /// HTTP leaf is bridged (falls back to <c>IoPool.Unbounded</c> when no registry is supplied),
    /// and selecting the web-search backend from configuration.
    /// </summary>
    /// <param name="httpClient">HTTP client used for search and page-fetch requests.</param>
    /// <param name="options">Web-search configuration (provider selection, keys, fetch limit).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="ioPoolRegistry">Optional registry supplying the bounded HTTP I/O pool.</param>
    public WebSearchPlugin(
        HttpClient httpClient,
        IOptions<WebSearchConfiguration> options,
        ILogger<WebSearchPlugin> logger,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        this.httpClient = httpClient;
        this.config = options.Value;
        this.logger = logger;
        ioPool = ioPoolRegistry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        searchProvider = ResolveProvider();
    }

    /// <summary>
    /// Selects the search backend from config. An explicit <c>WebSearch:Provider</c> forces that
    /// backend; the default (<see cref="WebSearchProviderType.None"/>) auto-detects — Google when
    /// its key + cx are present, else the retired Bing path when a legacy key is present, else none.
    /// </summary>
    private IWebSearchProvider? ResolveProvider()
    {
        var google = new GoogleWebSearchProvider(config.Google, httpClient, ioPool, logger);
        var bing = new BingWebSearchProvider(config.BingApiKey, config.Endpoint, httpClient, ioPool, logger);

        return config.Provider switch
        {
            WebSearchProviderType.Google => google,
            WebSearchProviderType.Bing => bing,
            _ => google.IsConfigured ? google : bing.IsConfigured ? bing : null,
        };
    }

    // The AIFunction surface requires Task<string> — these are the sanctioned one-line
    // boundary adapters; the bodies are reactive with the HTTP leaf bridged through the
    // IIoPool (AsynchronousCalls.md, ControlledIoPooling.md).
    /// <summary>
    /// MCP/agent tool: searches the web via the configured provider and returns matching results
    /// (title, URL and snippet) as JSON.
    /// </summary>
    /// <param name="query">Search query string.</param>
    /// <param name="count">Number of results to return (default 5, clamped to 1..20).</param>
    /// <returns>A task resolving to the JSON result list, or a message when search is not configured or fails.</returns>
    [Description("Searches the web and returns relevant results with titles, URLs, and snippets. Use this to find current information, documentation, or any topic on the internet.")]
    public Task<string> SearchWeb(
        [Description("Search query string")] string query,
        [Description("Number of results to return (default 5, max 20)")] int count = 5)
        => SearchWebCore(query, count).FirstAsync().ToTask();

    /// <summary>
    /// MCP/agent tool: fetches a web page and extracts its readable text, truncated
    /// to the configured maximum length.
    /// </summary>
    /// <param name="url">URL of the web page to fetch.</param>
    /// <returns>A task resolving to the extracted text, or a message when the URL is empty or the fetch fails.</returns>
    [Description("Fetches a web page and extracts its text content. Use this to read articles, documentation, or any public web page after finding URLs via SearchWeb.")]
    public Task<string> FetchWebPage(
        [Description("URL of the web page to fetch")] string url)
        => FetchWebPageCore(url).FirstAsync().ToTask();

    internal IObservable<string> SearchWebCore(string query, int count)
    {
        logger.LogInformation("SearchWeb called with query={Query}, count={Count}", query, count);

        if (searchProvider is not { IsConfigured: true } provider)
            return Observable.Return("Web search is not configured.");

        var clamped = Math.Clamp(count, 1, 20);

        // The provider bridges its single HTTP round-trip through the IIoPool — async lives only
        // inside that bridge, never on the subscribing thread. Map the neutral results to the
        // stable JSON shape agents already consume: [{ title, url, snippet }].
        return provider.Search(query, clamped)
            .Select(results => results.Count == 0
                ? "No results found."
                : JsonSerializer.Serialize(results.Select(r => new
                {
                    title = r.Title,
                    url = r.Url,
                    snippet = r.Snippet
                })))
            .Catch((HttpRequestException ex) =>
            {
                logger.LogError(ex, "Web search failed for query={Query}", query);
                return Observable.Return($"Web search failed: {ex.Message}");
            });
    }

    internal IObservable<string> FetchWebPageCore(string url)
    {
        logger.LogInformation("FetchWebPage called with url={Url}", url);

        if (string.IsNullOrWhiteSpace(url))
            return Observable.Return("URL cannot be empty.");

        return ioPool.Invoke(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "MeshWeaver/1.0");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,text/plain");

                using var response = await httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var content = await response.Content.ReadAsStringAsync(ct);

                // Extract text from HTML
                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    content.TrimStart().StartsWith("<", StringComparison.Ordinal))
                {
                    content = ExtractTextFromHtml(content);
                }

                if (content.Length > config.MaxFetchContentLength)
                    content = content[..config.MaxFetchContentLength] + "\n\n[Content truncated]";

                return content;
            })
            .Catch((HttpRequestException ex) =>
            {
                logger.LogError(ex, "Failed to fetch web page url={Url}", url);
                return Observable.Return($"Failed to fetch web page: {ex.Message}");
            });
    }

    private static string ExtractTextFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove script and style elements
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var sb = new StringBuilder();
        ExtractText(doc.DocumentNode, sb);
        return sb.ToString().Trim();
    }

    private static void ExtractText(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text);
                sb.Append(' ');
            }
            return;
        }

        // Add line breaks for block elements
        var isBlock = node.Name is "p" or "div" or "br" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "li" or "tr" or "td" or "th" or "blockquote" or "pre" or "hr" or "section" or "article";

        if (isBlock && sb.Length > 0 && sb[^1] != '\n')
            sb.AppendLine();

        foreach (var child in node.ChildNodes)
            ExtractText(child, sb);

        if (isBlock && sb.Length > 0 && sb[^1] != '\n')
            sb.AppendLine();
    }

    /// <summary>
    /// Builds the <c>AITool</c> set this plugin exposes to agents. <c>FetchWebPage</c> is always
    /// available; <c>SearchWeb</c> is advertised ONLY when a search provider is configured — a
    /// deployment never advertises a search tool it cannot fulfil.
    /// </summary>
    /// <returns>The web-search tools.</returns>
    public IEnumerable<AITool> CreateTools()
    {
        var tools = ImmutableList<AITool>.Empty;

        if (SearchConfigured)
            tools = tools.Add(AIFunctionFactory.Create(SearchWeb));
        else
            logger.LogInformation("WebSearch: no search provider configured — SearchWeb tool not advertised.");

        tools = tools.Add(AIFunctionFactory.Create(FetchWebPage));
        return tools;
    }
}
