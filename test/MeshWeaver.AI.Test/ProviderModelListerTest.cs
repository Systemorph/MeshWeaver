using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Models;
using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// URL-shaping + auth + parsing tests for <see cref="ProviderModelLister"/> — the
/// service that fetches a provider's live <c>/models</c> list for the Settings
/// add-provider flow. A capturing <see cref="HttpMessageHandler"/> records the
/// outgoing request (so we assert the exact URL + headers) and returns a canned
/// body (so we assert parsing) — no network. The lister still runs its HTTP leaf
/// through the mesh's <c>Http</c> I/O pool, so it needs a real hub (the test base's
/// <c>Mesh</c>).
/// </summary>
public class ProviderModelListerTest(ITestOutputHelper output) : AITestBase(output)
{
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

    private (ProviderModelLister lister, CapturingHandler handler) Make(
        string json = "{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"}]}",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(json, status);
        var lister = new ProviderModelLister(Mesh, logger: null, httpClient: new HttpClient(handler));
        return (lister, handler);
    }

    /// <summary>OpenAI-family: <c>GET {base}/models</c> with Bearer auth; results sorted + de-duplicated.</summary>
    [Fact]
    public async Task OpenAiCompatible_AppendsModelsToBaseUrl_WithBearerAuth()
    {
        var (lister, handler) = Make();

        var ids = await lister.ListModels("https://openrouter.ai/api/v1", "sk-or-123", "OpenAICompatible")
            .Should().Within(10.Seconds()).Emit();

        handler.Last!.RequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/models");
        handler.Last.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Last.Headers.Authorization.Parameter.Should().Be("sk-or-123");
        // Sorted, de-duplicated.
        ids.Should().Equal("a", "b");
    }

    /// <summary>A trailing slash on the base URL collapses to a single <c>…/models</c>.</summary>
    [Fact]
    public async Task TrailingSlashOnBaseUrl_IsNormalized()
    {
        var (lister, handler) = Make();

        await lister.ListModels("https://openrouter.ai/api/v1/", "k", "OpenAICompatible")
            .Should().Within(10.Seconds()).Emit();

        handler.Last!.RequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/models");
    }

    /// <summary>A blank endpoint falls back to the OpenAI default base (<c>api.openai.com/v1</c>).</summary>
    [Fact]
    public async Task BlankEndpoint_DefaultsToOpenAiV1()
    {
        var (lister, handler) = Make();

        await lister.ListModels(endpoint: null, apiKey: "sk-1", providerName: "OpenAI")
            .Should().Within(10.Seconds()).Emit();

        handler.Last!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/models");
        handler.Last.Headers.Authorization!.Scheme.Should().Be("Bearer");
    }

    /// <summary>Anthropic uses <c>x-api-key</c> + <c>anthropic-version</c> at the fixed <c>/v1/models</c> URL, not Bearer.</summary>
    [Fact]
    public async Task Anthropic_UsesApiKeyHeader_AndFixedModelsUrl()
    {
        var (lister, handler) = Make();

        // Even given Anthropic's /messages default endpoint, the lister targets the
        // models endpoint and switches auth to x-api-key (not Bearer).
        await lister.ListModels("https://api.anthropic.com/v1/messages", "sk-ant-xyz", "Anthropic")
            .Should().Within(10.Seconds()).Emit();

        handler.Last!.RequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/models");
        handler.Last.Headers.Authorization.Should().BeNull("Anthropic authenticates with x-api-key, not Bearer");
        handler.Last.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-ant-xyz");
        handler.Last.Headers.GetValues("anthropic-version").Should().ContainSingle();
    }

    /// <summary><c>data[].id</c> is sorted and de-duplicated.</summary>
    [Fact]
    public async Task Parse_SortsAndDeduplicates()
    {
        var (lister, _) = Make("{\"data\":[{\"id\":\"z\"},{\"id\":\"a\"},{\"id\":\"a\"}]}");

        var ids = await lister.ListModels("https://gateway/v1", "k", "OpenAICompatible")
            .Should().Within(10.Seconds()).Emit();

        ids.Should().Equal("a", "z");
    }

    /// <summary>A non-success HTTP status surfaces as an error (never a silent empty list).</summary>
    [Fact]
    public async Task NonSuccessResponse_SurfacesError()
    {
        var (lister, _) = Make("{\"error\":\"nope\"}", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            lister.ListModels("https://gateway/v1", "bad-key", "OpenAICompatible")
                .FirstAsync().ToTask(TestContext.Current.CancellationToken));
    }

    /// <summary>A blank key throws before any HTTP request is made.</summary>
    [Fact]
    public async Task MissingApiKey_ThrowsBeforeAnyRequest()
    {
        var (lister, handler) = Make();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lister.ListModels("https://gateway/v1", apiKey: "", "OpenAICompatible")
                .FirstAsync().ToTask(TestContext.Current.CancellationToken));

        handler.Last.Should().BeNull("a blank key must short-circuit before any HTTP call");
    }
}
