using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Configuration for the WebSearch plugin.
/// </summary>
public class WebSearchConfiguration
{
    /// <summary>
    /// Bing Search API subscription key.
    /// If not set, SearchWeb will return an error prompting configuration.
    /// </summary>
    public string? BingApiKey { get; set; }

    /// <summary>
    /// Bing Search API endpoint.
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

    /// <summary>The plugin's stable identifier, <c>WebSearch</c>.</summary>
    public string Name => "WebSearch";

    /// <summary>
    /// Creates the plugin, resolving the named <c>Http</c> I/O pool through which every
    /// HTTP leaf is bridged (falls back to <c>IoPool.Unbounded</c> when no registry is supplied).
    /// </summary>
    /// <param name="httpClient">HTTP client used for search and page-fetch requests.</param>
    /// <param name="options">Web-search configuration (Bing key, endpoint, fetch limit).</param>
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
    }

    // The AIFunction surface requires Task<string> — these are the sanctioned one-line
    // boundary adapters; the bodies are reactive with the HTTP leaf bridged through the
    // IIoPool (AsynchronousCalls.md, ControlledIoPooling.md).
    /// <summary>
    /// MCP/agent tool: searches the web via Bing and returns matching results
    /// (title, URL and snippet) as JSON.
    /// </summary>
    /// <param name="query">Search query string.</param>
    /// <param name="count">Number of results to return (default 5, clamped to 1..20).</param>
    /// <returns>A task resolving to the JSON result list, or a message when search is not configured or fails.</returns>
    [Description("Searches the web using Bing and returns relevant results with titles, URLs, and snippets. Use this to find current information, documentation, or any topic on the internet.")]
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

        if (string.IsNullOrWhiteSpace(config.BingApiKey))
            return Observable.Return("Web search is not configured. A Bing Search API key is required.");

        var clamped = Math.Clamp(count, 1, 20);

        // The HTTP round-trip is ONE pooled async leaf — async lives only inside
        // the IIoPool bridge, never on the subscribing thread.
        return ioPool.Invoke(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{config.Endpoint}?q={Uri.EscapeDataString(query)}&count={clamped}&textFormat=Plain");
                request.Headers.Add("Ocp-Apim-Subscription-Key", config.BingApiKey);

                using var response = await httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var results = ImmutableList<object>.Empty;
                if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                    webPages.TryGetProperty("value", out var pages))
                {
                    foreach (var page in pages.EnumerateArray())
                    {
                        results = results.Add(new
                        {
                            title = page.GetProperty("name").GetString(),
                            url = page.GetProperty("url").GetString(),
                            snippet = page.GetProperty("snippet").GetString()
                        });
                    }
                }

                if (results.Count == 0)
                    return "No results found.";

                return JsonSerializer.Serialize(results);
            })
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
                request.Headers.Add("Accept",
                    "text/html,application/xhtml+xml,application/rss+xml,application/atom+xml,application/xml,text/plain");

                using var response = await httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var content = await response.Content.ReadAsStringAsync(ct);

                // An RSS/Atom feed must be parsed as a feed BEFORE the HTML branch: running it
                // through ExtractTextFromHtml strips all element structure, collapsing every
                // item's title/link/description into one blob and losing the title↔link pairing
                // (issue #485). Only genuine HTML falls through to the HTML text-extractor.
                if (IsFeedContent(contentType, content))
                {
                    content = ExtractFeedItems(content);
                }
                else if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
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
    /// Decides whether fetched content is an RSS/Atom/XML feed that must be parsed as a feed
    /// rather than run through the HTML text-extractor. Recognizes feeds by media type
    /// (<c>application/rss+xml</c>, <c>application/atom+xml</c>, <c>application/xml</c>,
    /// <c>text/xml</c>) or, when the content-type is generic/absent, by an XML/feed body prefix.
    /// XHTML (media type ends in <c>+xml</c> but is HTML) is deliberately excluded.
    /// </summary>
    private static bool IsFeedContent(string contentType, string content)
    {
        // XHTML is HTML, not a feed, even though its media type ends in "+xml".
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return false;

        if (contentType.Contains("rss", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("atom", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return true;

        // Content-type is generic (text/plain, octet-stream) or missing — sniff the body prefix.
        var trimmed = content.AsSpan().TrimStart();
        return trimmed.StartsWith("<?xml") || trimmed.StartsWith("<rss") ||
               trimmed.StartsWith("<feed") || trimmed.StartsWith("<rdf:RDF");
    }

    /// <summary>
    /// Parses an RSS 2.0 / RSS 1.0 (RDF) / Atom feed into a compact JSON list of items so each
    /// item's <c>title</c>, <c>link</c>, <c>description</c> and <c>pubDate</c> survive as a unit
    /// (the HTML text-extractor would flatten them into a single blob and lose the title↔link
    /// pairing — issue #485). Falls back to the raw XML when the document is not well-formed
    /// or is valid XML but not a recognizable feed (e.g. a sitemap), which still preserves more
    /// structure than the HTML text-extractor would.
    /// </summary>
    internal static string ExtractFeedItems(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // Detected as XML by content-type but not well-formed — return the raw content
            // rather than destroying it through the HTML text-extractor.
            return xml;
        }

        var root = doc.Root;
        if (root is null)
            return xml;

        var items = ImmutableList<FeedItem>.Empty;

        // RSS 2.0 (<rss><channel><item>) and RSS 1.0/RDF (<item> under root) — RSS is
        // typically namespace-less, so match by local name to be namespace-robust.
        foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "item"))
            items = items.Add(ReadRssItem(item));

        // Atom (<feed><entry>).
        foreach (var entry in root.Descendants().Where(e => e.Name.LocalName == "entry"))
            items = items.Add(ReadAtomEntry(entry));

        if (items.Count == 0)
            // Valid XML but no feed items — hand back the raw XML so structure is preserved.
            return xml;

        return JsonSerializer.Serialize(items,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static FeedItem ReadRssItem(XElement item) => new(
        Title: GetChildText(item, "title"),
        Link: GetChildText(item, "link"),
        Description: GetChildText(item, "description"),
        PubDate: GetChildText(item, "pubDate"));

    private static FeedItem ReadAtomEntry(XElement entry)
    {
        // Atom <link> is an element carrying the URL in its href attribute; there can be
        // several (alternate/self/enclosure). Prefer rel="alternate", then a rel-less link,
        // then whatever link is present.
        var links = entry.Elements().Where(e => e.Name.LocalName == "link").ToImmutableList();
        var link = links.FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate")
                   ?? links.FirstOrDefault(l => l.Attribute("rel") is null)
                   ?? links.FirstOrDefault();
        var href = link?.Attribute("href")?.Value?.Trim();

        return new FeedItem(
            Title: GetChildText(entry, "title"),
            Link: string.IsNullOrEmpty(href) ? null : href,
            Description: GetChildText(entry, "summary") ?? GetChildText(entry, "content"),
            PubDate: GetChildText(entry, "updated") ?? GetChildText(entry, "published"));
    }

    private static string? GetChildText(XElement parent, string localName)
    {
        var element = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>A single parsed feed item; serialized to camelCase JSON for the agent.</summary>
    private sealed record FeedItem(string? Title, string? Link, string? Description, string? PubDate);

    /// <summary>
    /// Builds the <c>AITool</c> set this plugin exposes to agents — SearchWeb and FetchWebPage.
    /// </summary>
    /// <returns>The web-search tools.</returns>
    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(SearchWeb),
            AIFunctionFactory.Create(FetchWebPage)
        ];
    }
}
