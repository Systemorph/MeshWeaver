#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.AzureFoundry;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression tests for <see cref="AzureClaudeChatClient"/>'s transient-failure retry (issue #494):
/// the backoff must honor the server's <c>Retry-After</c> header (delta-seconds AND HTTP-date), a
/// non-positive value means "retry now", and with no header the backoff is exponential + capped. The
/// non-streaming path must now retry too (it previously did a single send). Deterministic — a stub
/// <see cref="HttpMessageHandler"/>, no network, <c>Retry-After: 0</c> keeps the loop tests instant.
/// </summary>
public class AzureClaudeRetryTest
{
    // ── ComputeRetryDelay: Retry-After is authoritative ──────────────────────

    [Fact]
    public void ComputeRetryDelay_HonorsDeltaSeconds()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.Add("Retry-After", "30");

        // attempt is irrelevant when the server dictates the wait.
        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt: 0, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void ComputeRetryDelay_HonorsHttpDate()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.Add("Retry-After", utcNow.AddSeconds(30).ToString("R")); // RFC1123 HTTP-date

        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt: 0, utcNow);

        Assert.InRange(delay.TotalSeconds, 29.0, 31.0);
    }

    [Fact]
    public void ComputeRetryDelay_NonPositiveDelta_MeansRetryImmediately()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.Add("Retry-After", "0");

        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt: 2, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.Zero, delay); // not the attempt-2 exponential value — the server said "now"
    }

    [Fact]
    public void ComputeRetryDelay_PastHttpDate_MeansRetryImmediately()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.Add("Retry-After", utcNow.AddSeconds(-30).ToString("R")); // already elapsed

        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt: 0, utcNow);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    // ── ComputeRetryDelay: no header → exponential backoff, capped ───────────

    [Theory]
    [InlineData(0, 1)]   // 2^0
    [InlineData(1, 2)]   // 2^1
    [InlineData(2, 4)]   // 2^2
    [InlineData(3, 8)]   // 2^3
    public void ComputeRetryDelay_NoHeader_IsExponential(int attempt, int expectedSeconds)
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError); // no Retry-After

        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void ComputeRetryDelay_NoHeader_IsCappedAt30Seconds()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); // no Retry-After

        var delay = AzureClaudeChatClient.ComputeRetryDelay(resp, attempt: 10, DateTimeOffset.UtcNow); // 2^10 = 1024

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    // ── Loop behavior (non-streaming path — previously NO retry at all) ──────

    [Fact]
    public async Task GetResponseAsync_RetriesThenSucceeds_On429ThenOk()
    {
        // Retry-After: 0 → the retry is immediate, so this proves the non-streaming path now retries.
        var handler = new StubHandler(TooManyRequests, OkJson);
        var client = NewClient(handler);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(2, handler.CallCount);                 // one 429 + one success
        Assert.Contains("ok", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_Persistent429_ThrowsAfterThreeAttempts()
    {
        var handler = new StubHandler(TooManyRequests); // every call 429
        var client = NewClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(3, handler.CallCount); // attempt count unchanged (3), then it gives up
    }

    // ── Loop behavior (streaming path) ───────────────────────────────────────

    [Fact]
    public async Task GetStreamingResponseAsync_Persistent429_ThrowsAfterThreeAttempts()
    {
        var handler = new StubHandler(TooManyRequests);
        var client = NewClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // drain — the throw happens while establishing the response, before any update
            }
        });

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesThenSucceeds_On429ThenOk()
    {
        var handler = new StubHandler(TooManyRequests, OkSse);
        var client = NewClient(handler);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            // an empty SSE stream ([DONE]) — no updates, just confirm it completes without throwing
        }

        Assert.Equal(2, handler.CallCount);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AzureClaudeChatClient NewClient(StubHandler handler) =>
        new("https://api.anthropic.com", "test-key", "claude-test", new HttpClient(handler));

    private static HttpResponseMessage TooManyRequests()
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate_limited\"}", Encoding.UTF8, "application/json")
        };
        r.Headers.Add("Retry-After", "0"); // immediate — keeps the test instant
        return r;
    }

    private static HttpResponseMessage OkJson() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\"}",
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage OkSse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        };

    /// <summary>
    /// Returns queued responses in order; once the last one is reached it is re-created on every
    /// subsequent call (so a single <see cref="TooManyRequests"/> factory yields a fresh 429 each attempt).
    /// </summary>
    private sealed class StubHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> queue = new(responses);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var factory = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Task.FromResult(factory());
        }
    }
}
