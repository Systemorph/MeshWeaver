using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Memex.Portal.Shared.Courses;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Offline coverage for <see cref="CourseAssetService"/> — the GitHub-contents-API leg of the
/// course-asset endpoint: the tokenized <c>download_url</c> must be fetched with the object media
/// type (so 1-100 MB videos resolve), authenticated as the App installation when one is
/// configured, cached per file inside the TTL (one round-trip for concurrent/repeated requests),
/// NEVER cached across a failure, and a missing file must resolve to null (→ 404), not an error.
/// </summary>
public class CourseAssetServiceTest
{
    private const string RepoUrl = "https://github.com/Systemorph/courses";
    private const string TokenizedUrl =
        "https://raw.githubusercontent.com/Systemorph/courses/main/content/videos/Module1.mp4?token=SHORTLIVED";

    private static CourseAssetService NewService(
        HttpMessageHandler handler,
        GitHubAppTokenService? appTokens = null,
        TimeSpan? cacheTtl = null) =>
        new(new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions()),
            appTokens,
            httpClient: new HttpClient(handler),
            cacheTtl: cacheTtl);

    [Fact]
    public async Task GetDownloadUrl_FetchesTheContentsApi_WithTheObjectMediaType()
    {
        var handler = new FakeContentsHandler();
        var service = NewService(handler);

        var url = await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();

        url.Should().Be(TokenizedUrl);
        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Path.Should().Be("/repos/Systemorph/courses/contents/content/videos/Module1.mp4");
        request.Query.Should().Be("?ref=main");
        // download_url must stay present for 1-100 MB files → the object media type.
        request.Accept.Should().Contain("application/vnd.github.object+json");
        // No App configured → unauthenticated (public repos still resolve).
        request.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task GetDownloadUrl_AuthenticatesAsTheAppInstallation_WhenConfigured()
    {
        using var rsa = RSA.Create(2048);
        var handler = new FakeContentsHandler();
        var appTokens = new GitHubAppTokenService(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = rsa.ExportRSAPrivateKeyPem(),
                InstallationId = 77,
            }),
            httpClient: new HttpClient(handler));
        var service = NewService(handler, appTokens);

        var url = await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();

        url.Should().Be(TokenizedUrl);
        var contents = handler.Requests.Single(r => r.Path.Contains("/contents/"));
        contents.Authorization.Should().Be("Bearer ghs_installation_token");
    }

    [Fact]
    public async Task GetDownloadUrl_MissingFile_ResolvesToNull()
    {
        var handler = new FakeContentsHandler { StatusCode = HttpStatusCode.NotFound };
        var service = NewService(handler);

        var url = await service.GetDownloadUrl(RepoUrl, "main", "content/gone.mp4")
            .FirstAsync().ToTask();

        url.Should().BeNull();
    }

    [Fact]
    public async Task GetDownloadUrl_CachesPerFile_InsideTheTtl()
    {
        var handler = new FakeContentsHandler();
        var service = NewService(handler);

        var first = await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();
        var second = await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();

        second.Should().Be(first);
        handler.Requests.Should().HaveCount(1, "the second request inside the TTL must replay the cached URL");

        // A DIFFERENT file is its own cache entry.
        await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module2.mp4")
            .FirstAsync().ToTask();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDownloadUrl_ExpiredTtl_Refetches()
    {
        var handler = new FakeContentsHandler();
        var service = NewService(handler, cacheTtl: TimeSpan.Zero);

        await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();
        await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();

        handler.Requests.Should().HaveCount(2, "a zero TTL must re-fetch on every request");
    }

    [Fact]
    public async Task GetDownloadUrl_NeverCachesAFailure()
    {
        var handler = new FakeContentsHandler { StatusCode = HttpStatusCode.InternalServerError };
        var service = NewService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
                .FirstAsync().ToTask());

        // The failed entry self-invalidated — the next caller retries and succeeds.
        handler.StatusCode = HttpStatusCode.OK;
        var url = await service.GetDownloadUrl(RepoUrl, "main", "content/videos/Module1.mp4")
            .FirstAsync().ToTask();
        url.Should().Be(TokenizedUrl);
        handler.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// GitHub API fake: answers the contents API with a tokenized <c>download_url</c> (or the
    /// configured error status) and the App token-mint endpoint, capturing every request's
    /// path/query/headers for assertions.
    /// </summary>
    private sealed class FakeContentsHandler : HttpMessageHandler
    {
        public sealed record Captured(string Path, string Query, string Accept, string? Authorization);

        public List<Captured> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            // GitHubAppTokenService leg (only exercised by the authenticated test).
            if (request.Method == HttpMethod.Post && path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                return Task.FromResult(Json(
                    $$"""{"token": "ghs_installation_token", "expires_at": "{{expires}}"}"""));
            }

            Requests.Add(new Captured(
                path,
                request.RequestUri.Query,
                request.Headers.Accept.ToString(),
                request.Headers.Authorization?.ToString()));

            if (StatusCode == HttpStatusCode.NotFound)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"message": "Not Found"}""", Encoding.UTF8, "application/json"),
                });
            if (StatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(StatusCode)
                {
                    Content = new StringContent("""{"message": "boom"}""", Encoding.UTF8, "application/json"),
                });

            return Task.FromResult(Json(
                $$"""{"type": "file", "name": "Module1.mp4", "download_url": "{{TokenizedUrl}}"}"""));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
