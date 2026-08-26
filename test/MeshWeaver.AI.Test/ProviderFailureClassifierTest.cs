#pragma warning disable CS1591

using System;
using System.Net;
using System.Net.Http;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the boundaries of <see cref="ProviderFailureClassifier"/> — the classification that decides
/// whether a round's terminal error is a NAMEABLE provider condition (rendered as localized prose)
/// or an unknown fault (whose own message is the diagnosis and must survive verbatim).
///
/// <para>Both directions are load-bearing (#476). Under-classifying leaves the raw transport dump —
/// status line, response body, and the whole HTTP header block — pasted into the thread, which is
/// the defect. Over-classifying would replace a genuine engineering error's message with generic
/// prose and destroy the only clue to what broke.</para>
/// </summary>
public class ProviderFailureClassifierTest
{
    /// <summary>
    /// The real shape of a rate-limit failure on the portal: Azure.Core / System.ClientModel put the
    /// status banner first, then the body, then EVERY response header. This is verbatim-shaped after
    /// the dump #476 quotes — it is the thing that must never reach a user.
    /// </summary>
    private const string RateLimitDump =
        """
        Your requests to DeepSeek-V4-Flash for DeepSeek-V4-Flash in swedencentral have exceeded rate limit.
        Status: 429 (Too Many Requests)
        ErrorCode: RateLimitReached

        Content:
        {"error":{"code":"RateLimitReached","message":"Rate limit is exceeded. Try again in 54 seconds."}}

        Headers:
        Content-Length: 210
        Content-Type: application/json
        x-ms-client-request-id: 1f0c1f4c-0a1e-4f8a-9f2f-2b0a3f6a1234
        x-ratelimit-remaining-requests: 0
        Retry-After: 54
        """;

    private const string ServerErrorDump =
        """
        Service request failed.
        Status: 500 (Internal Server Error)

        Content:
        {"object":"error","message":"Internal server error: 'NoneType' object has no attribute 'items'"}
        """;

    /// <summary>
    /// 🚨 #2233, verbatim from production (<c>Admin/_LogIncident/11973e8dfa3d0711</c>): the shape
    /// <c>System.ClientModel</c> renders for a <c>ClientResultException</c>. Note what it does NOT
    /// contain — the string "Status: ". The classifier knew only that banner, so every one of these
    /// fell through unclassified and its English provider body was pasted into the thread.
    /// </summary>
    private const string CreditExhaustedDump =
        """
        HTTP 402 (: )

        This request requires more credits, or fewer max_tokens. You requested up to 65536 tokens, but can only afford 6383. To increase, visit https://openrouter.ai/settings/credits and add more credits
        """;

    /// <summary>The other #2233 sample — a model id the provider does not serve.</summary>
    private const string ModelNotFoundDump =
        """
        HTTP 404 (: 404)

        Resource not found
        """;

    /// <summary>
    /// The regression #2233 is about: an OpenAI/OpenRouter refusal must classify, and must land on
    /// its OWN condition — 402 is not a rate limit (waiting does not help) and not a 5xx.
    /// </summary>
    [Fact]
    public void ClientResultCreditDump_IsClassifiedAs402()
    {
        var ex = new InvalidOperationException(CreditExhaustedDump);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().Be(402);
        ProviderFailureClassifier.IsQuotaExhausted(ex).Should().BeTrue();
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeFalse();
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeFalse();
        ProviderFailureClassifier.IsModelNotFound(ex).Should().BeFalse();
    }

    [Fact]
    public void ClientResultNotFoundDump_IsClassifiedAs404()
    {
        var ex = new InvalidOperationException(ModelNotFoundDump);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().Be(404);
        ProviderFailureClassifier.IsModelNotFound(ex).Should().BeTrue();
        ProviderFailureClassifier.IsQuotaExhausted(ex).Should().BeFalse();
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeFalse();
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeFalse();
    }

    /// <summary>
    /// The real one is wrapped several frames deep by the streaming pipeline before it reaches
    /// <c>ThreadExecution</c>'s catch — the same chain rule the 429 case relies on.
    /// </summary>
    [Fact]
    public void ClientResultStatusIsFoundThroughTheInnerExceptionChain()
        => ProviderFailureClassifier.IsQuotaExhausted(
                new InvalidOperationException("Agent invocation failed.",
                    new InvalidOperationException(CreditExhaustedDump)))
            .Should().BeTrue();

    /// <summary>
    /// "HTTP" without the space is the PROTOCOL VERSION, which appears in every header dump. Reading
    /// a status out of it would classify healthy responses as failures.
    /// </summary>
    [Theory]
    [InlineData("HTTP/1.1 200 OK")]
    [InlineData("Headers:\nHTTP/2 401")]
    [InlineData("HTTP 40213 is a support ticket, not a status")]
    [InlineData("the HTTP spec")]
    public void HttpBannerProbeDoesNotInventAStatus(string message)
        => ProviderFailureClassifier.TryGetProviderStatus(new InvalidOperationException(message))
            .Should().BeNull();

    [Fact]
    public void RateLimitDump_IsClassifiedAs429()
    {
        var ex = new InvalidOperationException(RateLimitDump);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().Be(429);
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeTrue();
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeFalse();
    }

    [Fact]
    public void ServerErrorDump_IsClassifiedAsProviderUnavailable()
    {
        var ex = new InvalidOperationException(ServerErrorDump);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().Be(500);
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeTrue();
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeFalse();
    }

    /// <summary>
    /// The streaming pipeline wraps provider faults (Microsoft.Extensions.AI middleware, the agent
    /// framework's invocation wrapper), so the transport exception is rarely the outermost one. A
    /// classifier that only looked at the top exception would miss every real portal failure.
    /// </summary>
    [Fact]
    public void StatusIsFoundThroughTheInnerExceptionChain()
    {
        var ex = new InvalidOperationException("Agent invocation failed.",
            new AggregateException("One or more errors occurred.",
                new InvalidOperationException(RateLimitDump)));
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeTrue();
    }

    /// <summary>A plain-HTTP provider (Ollama, a custom gateway) throws the typed exception instead.</summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public void TypedHttpStatusIsRead(HttpStatusCode code, int expected)
    {
        var ex = new HttpRequestException("boom", null, code);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().Be(expected);
    }

    /// <summary>
    /// 5xx is the provider faulting; 4xx other than 429 is not "unavailable" and must not be dressed
    /// up as one — a 401/404 says the deployment or key is wrong, a different remedy entirely.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public void NonRateLimitClientErrorsAreNeitherCondition(int status)
    {
        var ex = new InvalidOperationException($"Service request failed.\nStatus: {status} (Bad)");
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeFalse();
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeFalse();
    }

    /// <summary>
    /// The critical negative: an ordinary engineering fault carries no transport status, so the
    /// caller keeps reporting its message verbatim. Replacing THIS with "the provider returned an
    /// error" would erase the diagnosis.
    /// </summary>
    [Theory]
    [InlineData("Object reference not set to an instance of an object.")]
    [InlineData("The tool 'get_node' threw: node not found at 'a/b'.")]
    [InlineData("")]
    public void OrdinaryFaultsAreNotClassified(string message)
    {
        var ex = new InvalidOperationException(message);
        ProviderFailureClassifier.TryGetProviderStatus(ex).Should().BeNull();
        ProviderFailureClassifier.IsRateLimited(ex).Should().BeFalse();
        ProviderFailureClassifier.IsProviderUnavailable(ex).Should().BeFalse();
    }

    [Fact]
    public void NullIsNotClassified()
    {
        ProviderFailureClassifier.TryGetProviderStatus(null).Should().BeNull();
        ProviderFailureClassifier.IsRateLimited(null).Should().BeFalse();
    }

    /// <summary>
    /// The banner probe must not fire on prose or on a longer digit run that merely follows the
    /// word — those are not HTTP statuses, and truncating them to three digits would invent one.
    /// </summary>
    [Theory]
    [InlineData("Status: ok")]
    [InlineData("Status: unknown (the endpoint did not answer)")]
    [InlineData("Status: 4291 tokens remaining")]
    public void BannerProbeDoesNotInventAStatus(string message)
        => ProviderFailureClassifier.TryGetProviderStatus(new InvalidOperationException(message))
            .Should().BeNull();
}
