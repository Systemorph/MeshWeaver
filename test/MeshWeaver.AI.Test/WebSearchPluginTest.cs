#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for <see cref="WebSearchPlugin"/>: the search seam (Google Custom Search parsing +
/// tool advertisement) and content handling (RSS/Atom feeds parsed into a structured item list —
/// title↔link pairing preserved — instead of being flattened by the HTML text-extractor, issue
/// #485). A stub <see cref="HttpMessageHandler"/> stands in for the network throughout; no calls
/// go out. The plugin is built with no <c>IoPoolRegistry</c>, so its HTTP leaf runs on
/// <c>IoPool.Unbounded</c>.
/// </summary>
public class WebSearchPluginTest
{
    // ── Search seam (Google Custom Search) ──────────────────────────────────

    /// <summary>Records the last outgoing request and replays a canned JSON body.</summary>
    private sealed class CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WebSearchPlugin MakePlugin(WebSearchConfiguration config, HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(config), NullLogger<WebSearchPlugin>.Instance);

    private static WebSearchConfiguration GoogleConfig(
        WebSearchProviderType provider = WebSearchProviderType.Google,
        string? apiKey = "test-key",
        string? cx = "test-cx") =>
        new()
        {
            Provider = provider,
            Google = new GoogleWebSearchConfiguration { ApiKey = apiKey, Cx = cx },
        };

    /// <summary>(a) A Google CSE JSON payload parses each item's title, link and snippet, and the
    /// outgoing URL carries key + cx + q + num against the Custom Search endpoint.</summary>
    [Fact]
    public async Task Google_Configured_ParsesEachItemTitleLinkSnippet()
    {
        const string json = """
        {"items":[
          {"title":"First Result","link":"https://example.com/1","snippet":"Snippet one."},
          {"title":"Second Result","link":"https://example.com/2","snippet":"Snippet two."}
        ]}
        """;
        var handler = new CapturingHandler(json);
        var plugin = MakePlugin(GoogleConfig(), handler);

        var result = await plugin.SearchWebCore("meshweaver docs", 5)
            .Should().Within(10.Seconds()).Emit();

        // URL shaping — Google Custom Search JSON API with key, cx, q and num. AbsoluteUri keeps
        // the wire escaping (ToString() would unescape %20 for display).
        var uri = handler.Last!.RequestUri!.AbsoluteUri;
        uri.Should().Contain("https://www.googleapis.com/customsearch/v1");
        uri.Should().Contain("key=test-key");
        uri.Should().Contain("cx=test-cx");
        uri.Should().Contain("q=meshweaver%20docs");
        uri.Should().Contain("num=5");

        // Parsing — the per-item title <-> url <-> snippet association is preserved.
        using var doc = JsonDocument.Parse(result);
        var items = doc.RootElement.EnumerateArray().ToList();
        items.Should().HaveCount(2);

        items[0].GetProperty("title").GetString().Should().Be("First Result");
        items[0].GetProperty("url").GetString().Should().Be("https://example.com/1");
        items[0].GetProperty("snippet").GetString().Should().Be("Snippet one.");

        items[1].GetProperty("title").GetString().Should().Be("Second Result");
        items[1].GetProperty("url").GetString().Should().Be("https://example.com/2");
        items[1].GetProperty("snippet").GetString().Should().Be("Snippet two.");
    }

    /// <summary>Google caps <c>num</c> at 10 even when a larger count is requested, and an empty
    /// item list surfaces the friendly "No results found." message.</summary>
    [Fact]
    public async Task Google_ClampsNumToTen_AndReportsNoResults()
    {
        var handler = new CapturingHandler("""{"items":[]}""");
        var plugin = MakePlugin(GoogleConfig(), handler);

        var result = await plugin.SearchWebCore("anything", 20)
            .Should().Within(10.Seconds()).Emit();

        handler.Last!.RequestUri!.ToString().Should().Contain("num=10");
        result.Should().Contain("No results found.");
    }

    /// <summary>(b) With no provider configured (no key/cx, no legacy Bing key) the
    /// <c>SearchWeb</c> tool is NOT advertised — only <c>FetchWebPage</c> is.</summary>
    [Fact]
    public void Unconfigured_SearchWebToolNotAdvertised()
    {
        var plugin = MakePlugin(new WebSearchConfiguration(), new CapturingHandler("{}"));

        var toolNames = plugin.CreateTools().OfType<AIFunction>().Select(t => t.Name).ToList();

        toolNames.Should().NotContain("SearchWeb");
        toolNames.Should().Contain("FetchWebPage");
    }

    /// <summary>And unconfigured <c>SearchWebCore</c> returns the "not configured" message rather
    /// than attempting a call.</summary>
    [Fact]
    public async Task Unconfigured_SearchWebCore_ReturnsNotConfiguredMessage()
    {
        var plugin = MakePlugin(new WebSearchConfiguration(), new CapturingHandler("{}"));

        var result = await plugin.SearchWebCore("query", 5)
            .Should().Within(10.Seconds()).Emit();

        result.Should().Contain("not configured");
    }

    /// <summary>(c) With Google credentials present the <c>SearchWeb</c> tool IS advertised. Uses
    /// the default (auto-detect) provider selection so this also exercises auto-detection.</summary>
    [Fact]
    public void GoogleConfigured_SearchWebToolAdvertised()
    {
        var plugin = MakePlugin(
            GoogleConfig(provider: WebSearchProviderType.None),
            new CapturingHandler("{}"));

        var toolNames = plugin.CreateTools().OfType<AIFunction>().Select(t => t.Name).ToList();

        toolNames.Should().Contain("SearchWeb");
        toolNames.Should().Contain("FetchWebPage");
    }

    /// <summary>Forcing <c>Provider = Google</c> without credentials keeps <c>SearchWeb</c> hidden —
    /// selection alone never advertises an unfulfillable tool.</summary>
    [Fact]
    public void GoogleSelectedButNoCredentials_SearchWebToolNotAdvertised()
    {
        var plugin = MakePlugin(
            GoogleConfig(provider: WebSearchProviderType.Google, apiKey: null, cx: null),
            new CapturingHandler("{}"));

        var toolNames = plugin.CreateTools().OfType<AIFunction>().Select(t => t.Name).ToList();

        toolNames.Should().NotContain("SearchWeb");
        toolNames.Should().Contain("FetchWebPage");
    }

    // ── Content handling (RSS / Atom / HTML) — issue #485 ────────────────────

    private const string RssFixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Example Feed</title>
            <link>https://example.com</link>
            <description>Channel-level description (must NOT become an item)</description>
            <item>
              <title>First Article</title>
              <link>https://example.com/first</link>
              <description>Summary of the first article.</description>
              <pubDate>Mon, 06 Jan 2025 12:00:00 GMT</pubDate>
            </item>
            <item>
              <title>Second Article</title>
              <link>https://example.com/second</link>
              <description>Summary of the second article.</description>
              <pubDate>Tue, 07 Jan 2025 12:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string AtomFixture = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Example Atom Feed</title>
          <link href="https://example.com/"/>
          <updated>2025-01-06T12:00:00Z</updated>
          <entry>
            <title>Atom First</title>
            <link rel="alternate" href="https://example.com/atom-first"/>
            <summary>Summary of atom first.</summary>
            <updated>2025-01-06T12:00:00Z</updated>
          </entry>
          <entry>
            <title>Atom Second</title>
            <link href="https://example.com/atom-second"/>
            <content>Content of atom second.</content>
            <published>2025-01-05T09:00:00Z</published>
          </entry>
        </feed>
        """;

    private const string HtmlFixture =
        "<!DOCTYPE html><html><head><title>Page</title></head><body>" +
        "<h1>Hello</h1><p>Some <b>bold</b> text.</p>" +
        "<a href=\"https://example.com\">Link</a></body></html>";

    private sealed record ParsedItem(string? Title, string? Link, string? Description, string? PubDate);

    private static IReadOnlyList<ParsedItem> ParseItems(string json) =>
        JsonSerializer.Deserialize<List<ParsedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? new List<ParsedItem>();

    // ---- ExtractFeedItems: direct parsing ----------------------------------

    [Fact]
    public void ExtractFeedItems_Rss_PreservesTitleLinkPairs()
    {
        var json = WebSearchPlugin.ExtractFeedItems(RssFixture);
        var items = ParseItems(json);

        items.Should().HaveCount(2);
        items[0].Title.Should().Be("First Article");
        items[0].Link.Should().Be("https://example.com/first");
        items[0].Description.Should().Be("Summary of the first article.");
        items[0].PubDate.Should().Be("Mon, 06 Jan 2025 12:00:00 GMT");
        items[1].Title.Should().Be("Second Article");
        items[1].Link.Should().Be("https://example.com/second");
    }

    [Fact]
    public void ExtractFeedItems_Atom_PreservesTitleLinkPairs()
    {
        var json = WebSearchPlugin.ExtractFeedItems(AtomFixture);
        var items = ParseItems(json);

        items.Should().HaveCount(2);
        // rel="alternate" href is chosen for the first entry.
        items[0].Title.Should().Be("Atom First");
        items[0].Link.Should().Be("https://example.com/atom-first");
        items[0].Description.Should().Be("Summary of atom first.");
        // Second entry: no rel attribute → first (only) link href; content→description; published→pubDate.
        items[1].Title.Should().Be("Atom Second");
        items[1].Link.Should().Be("https://example.com/atom-second");
        items[1].Description.Should().Be("Content of atom second.");
        items[1].PubDate.Should().Be("2025-01-05T09:00:00Z");
    }

    [Fact]
    public void ExtractFeedItems_ValidXmlButNotAFeed_ReturnsRaw()
    {
        // A sitemap is valid XML with no <item>/<entry> — return it raw rather than
        // stripping structure through the HTML extractor.
        const string sitemap =
            "<?xml version=\"1.0\"?><urlset><url><loc>https://example.com/</loc></url></urlset>";

        var result = WebSearchPlugin.ExtractFeedItems(sitemap);

        result.Should().Be(sitemap);
    }

    [Fact]
    public void ExtractFeedItems_MalformedXml_ReturnsRaw()
    {
        const string malformed = "<?xml version=\"1.0\"?><rss><channel><item><title>Broken";

        var result = WebSearchPlugin.ExtractFeedItems(malformed);

        result.Should().Be(malformed);
    }

    // ---- FetchWebPageCore: end-to-end routing (feed vs HTML) ----------------

    [Fact]
    public async Task FetchWebPage_RssContentType_ReturnsStructuredItems()
    {
        var result = await FetchAsync(RssFixture, "application/rss+xml");
        var items = ParseItems(result);

        items.Should().HaveCount(2);
        items[0].Title.Should().Be("First Article");
        items[0].Link.Should().Be("https://example.com/first");
    }

    [Fact]
    public async Task FetchWebPage_AtomContentType_ReturnsStructuredItems()
    {
        var result = await FetchAsync(AtomFixture, "application/atom+xml");
        var items = ParseItems(result);

        items.Should().HaveCount(2);
        items[1].Title.Should().Be("Atom Second");
        items[1].Link.Should().Be("https://example.com/atom-second");
    }

    [Fact]
    public async Task FetchWebPage_Html_StripsTagsAndDoesNotParseAsFeed()
    {
        var result = await FetchAsync(HtmlFixture, "text/html");

        // HTML branch still strips tags to readable text …
        result.Should().Contain("Hello");
        result.Should().Contain("bold");
        result.Should().NotContain("<");
        // … and must NOT have gone through the feed parser.
        result.Should().NotContain("\"link\"");
    }

    private static Task<string> FetchAsync(string body, string mediaType)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(body, mediaType));
        var plugin = new WebSearchPlugin(
            httpClient,
            Options.Create(new WebSearchConfiguration()),
            NullLogger<WebSearchPlugin>.Instance);

        return plugin.FetchWebPageCore("https://feed.example.com/rss").FirstAsync().ToTask();
    }

    private sealed class StubHttpMessageHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
