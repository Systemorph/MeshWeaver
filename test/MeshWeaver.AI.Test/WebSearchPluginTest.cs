using System.Linq;
using System.Net;
using System.Net.Http;
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
/// Parsing + advertisement tests for <see cref="WebSearchPlugin"/>'s search seam. A stub
/// <see cref="HttpMessageHandler"/> captures the outgoing request (so we assert the Google
/// Custom Search URL) and returns a canned body (so we assert parsing) — no network. The plugin
/// is built with no <c>IoPoolRegistry</c>, so its HTTP leaf runs on <c>IoPool.Unbounded</c>.
/// Covers: (a) a Google CSE payload parses title/link/snippet per item; (b) with no provider
/// configured the <c>SearchWeb</c> tool is NOT advertised; (c) configured Google advertises it.
/// </summary>
public class WebSearchPluginTest
{
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
}
