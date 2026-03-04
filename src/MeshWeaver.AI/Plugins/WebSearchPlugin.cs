using System.ComponentModel;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
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
/// Register via <see cref="WebSearchPluginExtensions.AddWebSearchPlugin"/>.
/// </summary>
public class WebSearchPlugin : IAgentPlugin
{
    private readonly HttpClient httpClient;
    private readonly WebSearchConfiguration config;
    private readonly ILogger<WebSearchPlugin> logger;

    public string Name => "WebSearch";

    public WebSearchPlugin(
        HttpClient httpClient,
        IOptions<WebSearchConfiguration> options,
        ILogger<WebSearchPlugin> logger)
    {
        this.httpClient = httpClient;
        this.config = options.Value;
        this.logger = logger;
    }

    [Description("Searches the web using Bing and returns relevant results with titles, URLs, and snippets. Use this to find current information, documentation, or any topic on the internet.")]
    public async Task<string> SearchWeb(
        [Description("Search query string")] string query,
        [Description("Number of results to return (default 5, max 20)")] int count = 5)
    {
        logger.LogInformation("SearchWeb called with query={Query}, count={Count}", query, count);

        if (string.IsNullOrWhiteSpace(config.BingApiKey))
            return "Web search is not configured. A Bing Search API key is required.";

        count = Math.Clamp(count, 1, 20);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{config.Endpoint}?q={Uri.EscapeDataString(query)}&count={count}&textFormat=Plain");
            request.Headers.Add("Ocp-Apim-Subscription-Key", config.BingApiKey);

            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var results = new List<object>();
            if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                webPages.TryGetProperty("value", out var pages))
            {
                foreach (var page in pages.EnumerateArray())
                {
                    results.Add(new
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
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Web search failed for query={Query}", query);
            return $"Web search failed: {ex.Message}";
        }
    }

    [Description("Fetches a web page and extracts its text content. Use this to read articles, documentation, or any public web page after finding URLs via SearchWeb.")]
    public async Task<string> FetchWebPage(
        [Description("URL of the web page to fetch")] string url)
    {
        logger.LogInformation("FetchWebPage called with url={Url}", url);

        if (string.IsNullOrWhiteSpace(url))
            return "URL cannot be empty.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MeshWeaver/1.0");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,text/plain");

            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var content = await response.Content.ReadAsStringAsync();

            // Extract text from HTML
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                content.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                content = ExtractTextFromHtml(content);
            }

            if (content.Length > config.MaxFetchContentLength)
                content = content[..config.MaxFetchContentLength] + "\n\n[Content truncated]";

            return content;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to fetch web page url={Url}", url);
            return $"Failed to fetch web page: {ex.Message}";
        }
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

    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(SearchWeb),
            AIFunctionFactory.Create(FetchWebPage)
        ];
    }
}
