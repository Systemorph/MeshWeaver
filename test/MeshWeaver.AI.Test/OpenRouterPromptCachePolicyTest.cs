#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests <see cref="OpenRouterPromptCachePolicy"/>: the endpoint gate (only OpenRouter hosts get
/// the policy), the JSON rewrite (top-level <c>cache_control</c> injected exactly once, bodies we
/// don't understand left alone), and — through a real <see cref="OpenAIClient"/> pipeline over a
/// stub <see cref="HttpMessageHandler"/> — that the wire body OpenRouter would receive actually
/// carries the field. No network.
/// </summary>
public class OpenRouterPromptCachePolicyTest
{
    // ── AppliesTo: only OpenRouter hosts ─────────────────────────────────────

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("https://OPENROUTER.AI/api/v1", true)]
    [InlineData("https://gateway.openrouter.ai/api/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://api.groq.com/openai/v1", false)]
    // Host must END with .openrouter.ai — a lookalike prefix is not OpenRouter.
    [InlineData("https://openrouter.ai.evil.example/v1", false)]
    [InlineData("not a uri", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AppliesTo_MatchesOnlyOpenRouterHosts(string? endpoint, bool expected)
    {
        OpenRouterPromptCachePolicy.AppliesTo(endpoint).Should().Be(expected);
    }

    // ── TryAddCacheControl: the JSON rewrite ─────────────────────────────────

    [Fact]
    public void TryAddCacheControl_AddsTopLevelEphemeralField_PreservingBody()
    {
        var body = """{"model":"anthropic/claude-sonnet-4.5","messages":[{"role":"user","content":"hi"}]}""";

        var rewritten = OpenRouterPromptCachePolicy.TryAddCacheControl(body);

        rewritten.Should().NotBeNull();
        var node = JsonNode.Parse(rewritten!)!.AsObject();
        node["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        node["model"]!.GetValue<string>().Should().Be("anthropic/claude-sonnet-4.5");
        node["messages"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public void TryAddCacheControl_ExistingCacheControl_LeavesBodyAlone()
    {
        // A caller's explicit choice (e.g. a 1h TTL) must win over our default.
        var body = """{"model":"m","cache_control":{"type":"ephemeral","ttl":"1h"},"messages":[]}""";

        OpenRouterPromptCachePolicy.TryAddCacheControl(body).Should().BeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void TryAddCacheControl_UnrecognisedBody_LeavesBodyAlone(string body)
    {
        OpenRouterPromptCachePolicy.TryAddCacheControl(body).Should().BeNull();
    }

    // ── Through the real pipeline: the wire body carries the field ───────────

    [Fact]
    public async Task ChatCompletionRequest_ThroughPipeline_CarriesTopLevelCacheControl()
    {
        var handler = new CapturingHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        options.AddPolicy(new OpenRouterPromptCachePolicy(), PipelinePosition.PerCall);
        // Hermetic: the Transport above is a stub handler, so this client can never call out.
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), options); // local-only-guard:allow
        var chat = client.GetChatClient("anthropic/claude-sonnet-4.5");

        var completion = await chat.CompleteChatAsync(new UserChatMessage("hello"));

        completion.Value.Content[0].Text.Should().Be("ok");
        handler.Body.Should().NotBeNull();
        var sent = JsonNode.Parse(handler.Body!)!.AsObject();
        sent["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        sent["model"]!.GetValue<string>().Should().Be("anthropic/claude-sonnet-4.5");
    }

    /// <summary>Captures the outgoing wire body and answers a minimal valid chat completion.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string CannedResponse =
            """
            {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"anthropic/claude-sonnet-4.5",
             "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;

        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
