using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// Verifies that <see cref="LinkedInPostsApi"/> builds the exact versioned <c>/rest/posts</c> +
/// <c>/rest/socialActions</c> requests LinkedIn's <c>w_member_social</c> API expects, and parses the
/// created-post URN + engagement counts from the response. The HTTP boundary is stubbed with a
/// capturing <see cref="HttpMessageHandler"/> — no live LinkedIn calls, no mesh, no mocking.
/// </summary>
public class LinkedInPostsApiTest
{
    private static PlatformCredential Credential(string subjectId = "abc", string? scope = "openid profile email w_member_social") => new()
    {
        Platform = "LinkedIn",
        SubjectId = subjectId,
        AccessToken = "test-token",
        RefreshToken = "rt",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        AcquiredAt = DateTimeOffset.UtcNow,
        Scope = scope,
    };

    [Fact]
    public async Task PublishAsync_posts_to_rest_posts_with_correct_headers_and_body()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, "", ("x-restli-id", "urn:li:share:7777"));
        var http = new HttpClient(handler);

        var outcome = await LinkedInPostsApi.PublishAsync(
            http, Credential(), "Hello from the mesh 👋", visibility: null, apiVersion: null, CancellationToken.None);

        // --- endpoint + verb ---
        handler.Method.Should().Be(HttpMethod.Post.Method);
        handler.RequestUri.Should().Be("https://api.linkedin.com/rest/posts");

        // --- headers ---
        handler.AuthScheme.Should().Be("Bearer");
        handler.AuthParameter.Should().Be("test-token");
        handler.LinkedInVersion.Should().Be(LinkedInPostsApi.DefaultApiVersion); // "202506"
        handler.RestliVersion.Should().Be("2.0.0");

        // --- body shape (the exact wire contract) ---
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        root.GetProperty("author").GetString().Should().Be("urn:li:person:abc");
        root.GetProperty("commentary").GetString().Should().Be("Hello from the mesh 👋");
        root.GetProperty("visibility").GetString().Should().Be("PUBLIC");
        root.GetProperty("lifecycleState").GetString().Should().Be("PUBLISHED");
        root.GetProperty("isReshareDisabledByAuthor").GetBoolean().Should().BeFalse();

        var dist = root.GetProperty("distribution");
        dist.GetProperty("feedDistribution").GetString().Should().Be("MAIN_FEED");
        dist.GetProperty("targetEntities").GetArrayLength().Should().Be(0);
        dist.GetProperty("thirdPartyDistributionChannels").GetArrayLength().Should().Be(0);

        // --- outcome: URN comes from the x-restli-id response header ---
        outcome.Success.Should().BeTrue();
        outcome.Urn.Should().Be("urn:li:share:7777");
        outcome.PostUrl.Should().Be("https://www.linkedin.com/feed/update/urn:li:share:7777/");
    }

    [Fact]
    public async Task PublishAsync_keeps_an_already_prefixed_author_urn_and_honors_custom_visibility()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, "", ("x-restli-id", "urn:li:share:1"));
        var http = new HttpClient(handler);

        await LinkedInPostsApi.PublishAsync(
            http, Credential(subjectId: "urn:li:person:xyz"), "hi", visibility: "CONNECTIONS", apiVersion: "202401", CancellationToken.None);

        handler.LinkedInVersion.Should().Be("202401");
        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("author").GetString().Should().Be("urn:li:person:xyz");
        doc.RootElement.GetProperty("visibility").GetString().Should().Be("CONNECTIONS");
    }

    [Fact]
    public async Task PublishAsync_surfaces_error_body_on_non_2xx()
    {
        var handler = new CapturingHandler(HttpStatusCode.UnprocessableEntity,
            "{ \"message\": \"Not enough permissions to publish\" }");
        var http = new HttpClient(handler);

        var outcome = await LinkedInPostsApi.PublishAsync(
            http, Credential(), "hi", visibility: null, apiVersion: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.StatusCode.Should().Be(422);
        outcome.Urn.Should().BeNull();
        outcome.Error!.Should().Contain("Not enough permissions");
    }

    [Fact]
    public async Task PublishAsync_falls_back_to_response_body_id_when_no_header()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{ \"id\": \"urn:li:ugcPost:42\" }");
        var http = new HttpClient(handler);

        var outcome = await LinkedInPostsApi.PublishAsync(
            http, Credential(), "hi", visibility: null, apiVersion: null, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Urn.Should().Be("urn:li:ugcPost:42");
    }

    [Fact]
    public async Task GetSocialActionsAsync_parses_likes_and_comments()
    {
        var payload = """
        {
          "likesSummary":    { "totalLikes": 12, "aggregatedTotalLikes": 12 },
          "commentsSummary": { "count": 5,       "aggregatedTotalComments": 5 }
        }
        """;
        var handler = new CapturingHandler(HttpStatusCode.OK, payload);
        var http = new HttpClient(handler);

        var outcome = await LinkedInPostsApi.GetSocialActionsAsync(
            http, "urn:li:share:7777", Credential(), apiVersion: null, CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Get.Method);
        handler.RequestUri.Should().Contain("/rest/socialActions/");
        handler.LinkedInVersion.Should().Be(LinkedInPostsApi.DefaultApiVersion);
        handler.RestliVersion.Should().Be("2.0.0");

        outcome.Success.Should().BeTrue();
        outcome.LikeCount.Should().Be(12);
        outcome.CommentCount.Should().Be(5);
    }

    [Fact]
    public async Task GetSocialActionsAsync_surfaces_error_without_throwing()
    {
        var handler = new CapturingHandler(HttpStatusCode.Forbidden, "{ \"message\": \"insufficient scope\" }");
        var http = new HttpClient(handler);

        var outcome = await LinkedInPostsApi.GetSocialActionsAsync(
            http, "urn:li:share:7777", Credential(), apiVersion: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.StatusCode.Should().Be(403);
        outcome.LikeCount.Should().Be(0);
        outcome.CommentCount.Should().Be(0);
    }

    [Theory]
    [InlineData("abc", "urn:li:person:abc")]
    [InlineData("urn:li:person:xyz", "urn:li:person:xyz")]
    [InlineData("urn:li:organization:99", "urn:li:organization:99")]
    public void NormalizeMemberUrn_prefixes_bare_ids_only(string input, string expected) =>
        LinkedInPostsApi.NormalizeMemberUrn(input).Should().Be(expected);

    /// <summary>
    /// Records the outgoing request (method, URI, auth + versioned headers, body) so the test can
    /// assert the exact wire contract, then returns a canned response with optional headers.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly (string Name, string Value)[] _responseHeaders;

        public string? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthParameter { get; private set; }
        public string? LinkedInVersion { get; private set; }
        public string? RestliVersion { get; private set; }
        public string? Body { get; private set; }

        public CapturingHandler(HttpStatusCode status, string body, params (string, string)[] responseHeaders)
        {
            _status = status;
            _body = body;
            _responseHeaders = responseHeaders;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method.Method;
            RequestUri = request.RequestUri?.GetLeftPart(UriPartial.Path);
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthParameter = request.Headers.Authorization?.Parameter;
            if (request.Headers.TryGetValues("LinkedIn-Version", out var lv))
                LinkedInVersion = System.Linq.Enumerable.FirstOrDefault(lv);
            if (request.Headers.TryGetValues("X-Restli-Protocol-Version", out var rv))
                RestliVersion = System.Linq.Enumerable.FirstOrDefault(rv);
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(ct);

            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in _responseHeaders)
                resp.Headers.TryAddWithoutValidation(name, value);
            return resp;
        }
    }
}
