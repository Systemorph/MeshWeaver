using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="GitHubAppTokenService.GetScopedInstallationToken"/> — the narrowed token a standing
/// build host checks a run out with (Doc/Architecture/BuildHosts: <c>{"contents":"read"}</c> for the
/// claimed run's ONE repository). Pinned: the exchange POST carries exactly the requested
/// <c>repositories</c> and <c>permissions</c>; a scoped token is NEVER served from the installation
/// token's cache nor cached across scopes (two calls ⇒ two exchanges, each with its own body); the
/// unscoped token still posts NO body; GitHub refusing a scope is a typed mint failure, never a
/// wider token; and a malformed scope is refused before anything reaches GitHub.
/// </summary>
public class GitHubAppScopedTokenTest : IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private readonly IoPoolRegistry pools = new();

    private sealed class RecordingHandler(HttpStatusCode exchangeStatus = HttpStatusCode.Created) : HttpMessageHandler
    {
        public ConcurrentQueue<string?> ExchangeBodies { get; } = new();
        private int minted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id": 11, "account": {"login": "Contoso"}}]"""),
                };
            if (request.Method == HttpMethod.Post && path == "/app/installations/11/access_tokens")
            {
                ExchangeBodies.Enqueue(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
                var n = Interlocked.Increment(ref minted);
                return exchangeStatus == HttpStatusCode.Created
                    ? new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent(
                            $$"""{"token": "ghs_{{n}}", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):o}}"}"""),
                    }
                    : new HttpResponseMessage(exchangeStatus)
                    {
                        Content = new StringContent("""{"message": "The permissions requested are not granted to this installation."}"""),
                    };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private GitHubAppTokenService Service(RSA key, HttpMessageHandler handler) =>
        new(pools,
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = key.ExportRSAPrivateKeyPem(),
                InstallationOwner = "Contoso",
            }),
            httpClient: new HttpClient(handler));

    private static readonly IReadOnlyDictionary<string, string> ContentsRead = new Dictionary<string, string> { ["contents"] = "read" };

    [Fact]
    public async Task TheExchange_CarriesExactlyTheRequestedRepositoriesAndPermissions()
    {
        using var key = RSA.Create(2048);
        var handler = new RecordingHandler();
        var token = await Service(key, handler)
            .GetScopedInstallationToken(null, ["MeshWeaver.Plugins"], ContentsRead)
            .FirstAsync().Timeout(Budget).Await();

        Assert.Equal("ghs_1", token);
        var body = Assert.Single(handler.ExchangeBodies);
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(["MeshWeaver.Plugins"], doc.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        var permissions = doc.RootElement.GetProperty("permissions");
        Assert.Equal("read", permissions.GetProperty("contents").GetString());
        Assert.Single(permissions.EnumerateObject());
    }

    [Fact]
    public async Task AScopedToken_IsNeverCached_AndNeverServedFromTheInstallationTokensCache()
    {
        using var key = RSA.Create(2048);
        var handler = new RecordingHandler();
        var service = Service(key, handler);

        var unscoped = await service.GetInstallationToken().FirstAsync().Timeout(Budget).Await();
        var a = await service.GetScopedInstallationToken(null, ["RepoA"], ContentsRead).FirstAsync().Timeout(Budget).Await();
        var b = await service.GetScopedInstallationToken(null, ["RepoB"], ContentsRead).FirstAsync().Timeout(Budget).Await();
        var again = await service.GetInstallationToken().FirstAsync().Timeout(Budget).Await();

        Assert.Equal(["ghs_1", "ghs_2", "ghs_3"], new[] { unscoped, a, b });
        Assert.Equal(unscoped, again);        // the installation token's cache is untouched by scoped mints
        var bodies = handler.ExchangeBodies.ToArray();
        Assert.Equal(3, bodies.Length);
        Assert.Null(bodies[0]);               // the unscoped exchange posts NO body (GitHub: the installation's full grant)
        Assert.Contains("\"RepoA\"", bodies[1]);
        Assert.Contains("\"RepoB\"", bodies[2]);
        Assert.DoesNotContain("RepoA", bodies[2]);
    }

    [Fact]
    public async Task TheSameRepository_WriteThenRead_AndARepeatedScope_EachMintFreshWithTheirOwnPermissions()
    {
        // A cache keyed by repository alone would hand the read-only request the earlier WRITE token;
        // a cache keyed by the full scope would answer the repeated request without an exchange.
        using var key = RSA.Create(2048);
        var handler = new RecordingHandler();
        var service = Service(key, handler);
        var write = new Dictionary<string, string> { ["contents"] = "write" };

        var first = await service.GetScopedInstallationToken(null, ["Repo"], write).FirstAsync().Timeout(Budget).Await();
        var second = await service.GetScopedInstallationToken(null, ["Repo"], ContentsRead).FirstAsync().Timeout(Budget).Await();
        var third = await service.GetScopedInstallationToken(null, ["Repo"], ContentsRead).FirstAsync().Timeout(Budget).Await();

        Assert.Equal(["ghs_1", "ghs_2", "ghs_3"], new[] { first, second, third });
        var levels = handler.ExchangeBodies.Select(b =>
        {
            using var doc = JsonDocument.Parse(b!);
            Assert.Equal(["Repo"], doc.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
            var permissions = doc.RootElement.GetProperty("permissions");
            Assert.Single(permissions.EnumerateObject());
            return permissions.GetProperty("contents").GetString();
        }).ToArray();
        Assert.Equal(["write", "read", "read"], levels);
    }

    [Fact]
    public async Task GitHubRefusingTheScope_IsATypedMintFailure_NeverAWiderToken()
    {
        using var key = RSA.Create(2048);
        var service = Service(key, new RecordingHandler(HttpStatusCode.UnprocessableEntity));
        var ex = await Assert.ThrowsAsync<GitHubAppTokenMintException>(() =>
            service.GetScopedInstallationToken(null, ["MeshWeaver.Plugins"], new Dictionary<string, string> { ["administration"] = "write" })
                .FirstAsync().Timeout(Budget).Await());
        Assert.Equal(GitHubAppTokenMintStage.TokenExchange, ex.Stage);
        Assert.Equal(422, ex.StatusCode);
    }

    public static TheoryData<string[], string, string> MalformedScopes => new()
    {
        { Array.Empty<string>(), "contents", "read" },
        { new[] { "Systemorph/MeshWeaver" }, "contents", "read" },
        { new[] { "MeshWeaver" }, "contents", "admin" },
        { new[] { " " }, "contents", "read" },
    };

    [Theory]
    [MemberData(nameof(MalformedScopes))]
    public async Task AMalformedScope_IsRefusedBeforeAnythingReachesGitHub(string[] repositories, string permission, string level)
    {
        using var key = RSA.Create(2048);
        var handler = new RecordingHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(key, handler).GetScopedInstallationToken(null, repositories, new Dictionary<string, string> { [permission] = level })
                .FirstAsync().Timeout(Budget).Await());
        Assert.Empty(handler.ExchangeBodies);
    }

    public async ValueTask DisposeAsync()
    {
        pools.Dispose();
        await pools.Disposed.FirstAsync().Timeout(Budget).Await();
        GC.SuppressFinalize(this);
    }
}
