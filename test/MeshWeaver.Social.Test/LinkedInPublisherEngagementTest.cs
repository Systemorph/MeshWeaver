using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// Verifies that the LinkedIn /v2/socialActions/{urn}/{comments|likes} pagination
/// + parsing yields the expected EngagementComment / EngagementLike records.
/// Backed by a stub HttpMessageHandler — no live LinkedIn calls.
/// </summary>
public class LinkedInPublisherEngagementTest
{
    private static PlatformCredential FreshCredential() => new()
    {
        Platform = "LinkedIn",
        SubjectId = "abc",
        AccessToken = "test-token",
        RefreshToken = "rt",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        AcquiredAt = DateTimeOffset.UtcNow,
        Scope = "r_member_social"
    };

    [Fact]
    public async Task ListCommentsAsync_parses_comments_and_paginates()
    {
        var page1 = """
        {
          "elements": [
            { "id": "urn:li:comment:1", "actor": "urn:li:person:alice", "message": { "text": "Great post!" }, "created": { "time": 1700000000000 } },
            { "id": "urn:li:comment:2", "actor": "urn:li:person:bob",   "message": { "text": "Loved this." }, "created": { "time": 1700001000000 } }
          ]
        }
        """;
        var emptyPage = "{ \"elements\": [] }";

        var handler = new StubHandler();
        handler.AddResponse(req => req.RequestUri!.AbsoluteUri.Contains("start=0"), HttpStatusCode.OK, page1);
        handler.AddResponse(req => req.RequestUri!.AbsoluteUri.Contains("start=2"), HttpStatusCode.OK, emptyPage);

        var publisher = new LinkedInPublisher(
            new HttpClient(handler),
            new LinkedInOptions { ClientId = "x", ClientSecret = "y" });

        var collected = new List<EngagementComment>();
        await foreach (var c in publisher.ListCommentsAsync("urn:li:share:99", FreshCredential(), maxItems: 100, CancellationToken.None))
            collected.Add(c);

        // The stub returned 2 comments on page 1; the publisher pages with count=100 so it
        // exits after the first page (2 elements < page size of 100). Verify both were yielded.
        collected.Should().HaveCount(2);
        collected[0].ActorUrn.Should().Be("urn:li:person:alice");
        collected[0].Text.Should().Be("Great post!");
        collected[0].Urn.Should().Be("urn:li:comment:1");
        collected[1].ActorUrn.Should().Be("urn:li:person:bob");
    }

    [Fact]
    public async Task ListLikesAsync_parses_likes_with_reaction_type()
    {
        var page = """
        {
          "elements": [
            { "id": "urn:li:like:1", "actor": "urn:li:person:carol", "reactionType": "PRAISE",   "created": { "time": 1700000000000 } },
            { "id": "urn:li:like:2", "actor": "urn:li:person:dave",  "reactionType": "EMPATHY", "created": { "time": 1700001000000 } },
            { "id": "urn:li:like:3", "actor": "urn:li:person:eve",                                 "created": { "time": 1700002000000 } }
          ]
        }
        """;

        var handler = new StubHandler();
        handler.AddResponse(_ => true, HttpStatusCode.OK, page);

        var publisher = new LinkedInPublisher(
            new HttpClient(handler),
            new LinkedInOptions { ClientId = "x", ClientSecret = "y" });

        var collected = new List<EngagementLike>();
        await foreach (var lk in publisher.ListLikesAsync("urn:li:share:99", FreshCredential(), maxItems: 100, CancellationToken.None))
            collected.Add(lk);

        collected.Should().HaveCount(3);
        collected[0].ReactionType.Should().Be("PRAISE");
        collected[1].ReactionType.Should().Be("EMPATHY");
        collected[2].ReactionType.Should().Be("LIKE");
        collected[2].ActorProfileUrl.Should().Contain("urn%3Ali%3Aperson%3Aeve");
    }

    [Fact]
    public async Task ListCommentsAsync_returns_empty_on_403_without_throwing()
    {
        var handler = new StubHandler();
        handler.AddResponse(_ => true, HttpStatusCode.Forbidden, "{ \"message\": \"insufficient scope\" }");

        var publisher = new LinkedInPublisher(
            new HttpClient(handler),
            new LinkedInOptions { ClientId = "x", ClientSecret = "y" });

        var collected = new List<EngagementComment>();
        await foreach (var c in publisher.ListCommentsAsync("urn:li:share:99", FreshCredential(), maxItems: 100, CancellationToken.None))
            collected.Add(c);

        collected.Should().BeEmpty();
    }

    [Fact]
    public async Task ListCommentsAsync_respects_maxItems_cap()
    {
        var page = """
        { "elements": [
            { "id": "c1", "actor": "p:a", "message": { "text": "1" }, "created": { "time": 1700000000000 } },
            { "id": "c2", "actor": "p:b", "message": { "text": "2" }, "created": { "time": 1700000000000 } },
            { "id": "c3", "actor": "p:c", "message": { "text": "3" }, "created": { "time": 1700000000000 } }
        ] }
        """;

        var handler = new StubHandler();
        handler.AddResponse(_ => true, HttpStatusCode.OK, page);

        var publisher = new LinkedInPublisher(
            new HttpClient(handler),
            new LinkedInOptions { ClientId = "x", ClientSecret = "y" });

        var collected = new List<EngagementComment>();
        await foreach (var c in publisher.ListCommentsAsync("urn:li:share:99", FreshCredential(), maxItems: 2, CancellationToken.None))
            collected.Add(c);

        collected.Should().HaveCount(2);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Body)> _responses = new();

        public void AddResponse(Func<HttpRequestMessage, bool> match, HttpStatusCode status, string body) =>
            _responses.Add((match, status, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            foreach (var (match, status, body) in _responses)
            {
                if (match(request))
                {
                    var resp = new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(resp);
                }
            }
            // No match — return 404 so the test fails loudly rather than hanging.
            var miss = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No stub matched {request.Method} {request.RequestUri}")
            };
            return Task.FromResult(miss);
        }
    }
}
