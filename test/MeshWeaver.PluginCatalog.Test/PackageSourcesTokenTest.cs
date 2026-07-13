#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The registry serves URL sources with a RESOLVED GitHub App INSTALLATION token, never a hardcoded
/// empty token. Regression for the <c>/api/plugins</c> 500: <c>PackageSources.FromRepo</c> built a
/// URL source with <c>token: ""</c>, which <c>OctokitGitHubRepoClient.Client("")</c> turned into
/// <c>new Credentials("")</c> and Octokit threw <c>ArgumentException</c> — so any configured URL
/// source crashed the endpoint. The fix resolves the token via <see cref="GitHubAppTokenService"/>
/// (the same machine identity GitSync uses) fresh before each fetch. This drives
/// <see cref="PackageSources.FromRepo"/> through a real mesh hub with the App CONFIGURED and a fake
/// repo client that captures the token it is handed.
/// </summary>
public class PackageSourcesAppTokenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly PackageSourcesTokenFakes.TokenCapturingRepoClient client = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                // The registry's GitHub identity + repo client — the same seam FromRepo resolves.
                services.AddSingleton<IGitHubRepoClient>(client);
                services.AddSingleton(sp => new GitHubAppTokenService(
                    sp.GetRequiredService<IoPoolRegistry>(),
                    Options.Create(new GitHubAppOptions
                    {
                        ClientId = "Iv23liRegistryApp",
                        PrivateKey = PackageSourcesTokenFakes.TestKeyPem,
                        InstallationOwner = "Systemorph",
                    }),
                    logger: null,
                    httpClient: new HttpClient(new PackageSourcesTokenFakes.FakeGitHubAppHandler())));
                return services;
            });

    [Fact(Timeout = 120_000)]
    public async Task UrlSource_BuildsNonNull_AndListsWithResolvedAppToken_NotEmpty()
    {
        client.LastToken = null;

        // The exact call the registry endpoint makes for a node-repo URL source.
        var source = PackageSources.FromRepo(
            Mesh, "https://github.com/Systemorph/MeshWeaver.Plugins", sourceSubdir: null, nodeRepo: true);

        Assert.NotNull(source);

        // Listing must succeed WITHOUT throwing an empty-credential exception, and the client must
        // have been handed the App-minted installation token — never the old empty string.
        var packages = await source!.ListPackages("main").FirstAsync().ToTask();
        Assert.Empty(packages); // the fake returns an empty snapshot; the point is that it did not throw

        Assert.Equal("ghs_registry_installation_token", client.LastToken);
    }
}

/// <summary>
/// The unconfigured-App companion to <see cref="PackageSourcesAppTokenTest"/>: when the GitHub App
/// identity is NOT configured, <see cref="PackageSources.FromRepo"/> must still build a URL source
/// and its token provider must degrade to ANONYMOUS (empty token) — the fetch runs unauthenticated
/// against a public repo and does NOT throw.
/// </summary>
public class PackageSourcesAnonymousTokenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly PackageSourcesTokenFakes.TokenCapturingRepoClient client = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IGitHubRepoClient>(client);
                // GitHubAppTokenService present but UNCONFIGURED (no client id / private key).
                services.AddSingleton(sp => new GitHubAppTokenService(
                    sp.GetRequiredService<IoPoolRegistry>(),
                    Options.Create(new GitHubAppOptions())));
                return services;
            });

    [Fact(Timeout = 120_000)]
    public async Task UrlSource_WithNoAppConfigured_FallsBackToAnonymous_EmptyToken_NoThrow()
    {
        client.LastToken = null;

        var source = PackageSources.FromRepo(
            Mesh, "https://github.com/acme/public-plugins", sourceSubdir: null, nodeRepo: true);

        Assert.NotNull(source);
        var packages = await source!.ListPackages("main").FirstAsync().ToTask();
        Assert.Empty(packages);
        Assert.Equal(string.Empty, client.LastToken); // anonymous access, no throw
    }
}

/// <summary>Shared offline fakes for the package-source token tests.</summary>
internal static class PackageSourcesTokenFakes
{
    /// <summary>A test RSA key (PEM), generated once, for the App-JWT signing.</summary>
    public static readonly string TestKeyPem = CreateKey();

    private static string CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    /// <summary>An <see cref="IGitHubRepoClient"/> that records the token it is handed on Fetch and
    /// returns an empty snapshot. Only Fetch is exercised by the package sources.</summary>
    public sealed class TokenCapturingRepoClient : IGitHubRepoClient
    {
        public volatile string? LastToken;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            LastToken = accessToken;
            return Observable.Return(new RepoSnapshot(commitish, Array.Empty<RepoFile>()));
        }

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => throw new NotSupportedException();
        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(string repositoryUrl, GitHubIssueState? state, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => throw new NotSupportedException();
        public IObservable<GitHubIssueComment> CommentIssue(string repositoryUrl, int number, string body, string accessToken) => throw new NotSupportedException();
        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(string repositoryUrl, PullRequestStatus? state, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssueComment> CommentPullRequest(string repositoryUrl, int number, string body, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => throw new NotSupportedException();
    }

    /// <summary>GitHub App API fake: installation discovery + token minting (offline, deterministic).</summary>
    public sealed class FakeGitHubAppHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return Task.FromResult(Json("""[{"id": 42, "account": {"login": "Systemorph"}}]"""));
            if (request.Method == HttpMethod.Post && path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                return Task.FromResult(Json($$"""{"token": "ghs_registry_installation_token", "expires_at": "{{expires}}"}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.Method} {path}"),
            });
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
