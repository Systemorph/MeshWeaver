#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#5825 — <c>POST /api/plugins/files</c> ANSWERS or REFUSES within the registry's
/// deadline, like every other registry route since #6253. It was the one route left without it:
/// a package-folder fetch that queued (behind a GitSync wave of whole-repository clones on the
/// process pool) held the consumer's request with nothing written and nothing logged until the
/// consumer's own 30 s attempt cut it — memex, 2026-10-10 07:44Z and 08:55Z, eight such cuts and
/// no registry-side line, after #6389 had removed every 503 from the catalog and index routes.
///
/// <para>The repro is the production shape at the route: the REAL mesh with the plugin catalog,
/// a configured node-repo URL source, a registered instance, the registry routes on a TestServer
/// — and a git transport whose LISTING answers while the package-folder FETCH never does. The
/// negative control is the same request with the deadline pushed out of reach: held with nothing
/// written, which is the defect, so the green above it is the fix's and not the harness's.</para>
/// </summary>
public class RegistryFilesRouteAnswersOrRefusesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "FilesDeadlinePkg";
    private const string Source = "Plugins";
    private const string Repo = "https://github.com/Systemorph/FilesDeadlineFixture";
    private const string Instance = "files-deadline-consumer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton<IGitHubRepoClient>(new ListingOnlyRepoClient()));

    [Fact(Timeout = 300_000)]
    public async Task TheFilesRoute_WithAFolderFetchThatNeverAnswers_RefusesWithinItsBudget_AndNamesTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance(ct);
        var app = await StartRegistryHost(budgetSeconds: 2, ct);
        await using var _ = app;

        var clock = Stopwatch.StartNew();
        var result = await Files(app, key, TestTimeouts.Quick, ct);
        using var response = result.Response;
        var elapsed = clock.Elapsed;

        Assert.False(result.ClientCut, "the registry must answer before the client's patience ends");
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response!.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(InstanceRegistryAuthenticator.RetryAfterSeconds),
            response.Headers.RetryAfter?.Delta);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var waitingOn = body.RootElement.GetProperty("waitingOn").EnumerateArray().Select(e => e.GetString()).ToArray();
        // The listing ANSWERED (the package was found); the refusal names the folder fetch.
        Assert.Equal([$"package files '{Package}' from '{Source}'"], waitingOn);
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"the refusal must come at the 2 s budget, not at the client's patience (took {elapsed.TotalSeconds:F1} s)");
    }

    /// <summary>Negative control: the deadline out of reach, the same stalled folder fetch — the
    /// request is held with nothing written, exactly what the consumers measured.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheFilesRoute_WithTheDeadlineOutOfReach_IsHeldWithNothingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance(ct);
        var app = await StartRegistryHost(budgetSeconds: 3600, ct);
        await using var _ = app;

        var result = await Files(app, key, TimeSpan.FromSeconds(5), ct);
        using var response = result.Response;

        // A 499 is the host recording the client's own hang-up (see RegistryAnswersOrRefusesTest):
        // the same reading as no response — nothing the ROUTE answered.
        Assert.True(response is null || response.StatusCode == (HttpStatusCode)StatusCodes.Status499ClientClosedRequest,
            $"the held request must not be answered by the route; got {(int?)response?.StatusCode}");
        Assert.True(result.ClientCut, "nothing may come back before the client's own cancellation fires");
    }

    private Task<string> RegisterInstance(CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection([new KeyValuePair<string, string?>(
                        $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0", $"{Source}/*")])
                    .Build())
            .Register("files-deadline-owner", "Files Deadline Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(ct);

    private async Task<WebApplication> StartRegistryHost(int budgetSeconds, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RegistryAnswerDeadline.BudgetSecondsConfigKey] =
                budgetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PluginCatalog:Sources:0:Name"] = Source,
            ["PluginCatalog:Sources:0:RepoPath"] = Repo,
            ["PluginCatalog:Sources:0:Ref"] = "main",
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        var app = builder.Build();
        app.MapPluginRegistry();
        await app.StartAsync(ct);
        return app;
    }

    /// <summary>The files response, or null when the client's own <paramref name="patience"/> ran
    /// out with no status line received, together with whether that cancellation fired.</summary>
    private static async Task<(HttpResponseMessage? Response, bool ClientCut)> Files(
        WebApplication app, string key, TimeSpan patience, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RegistryPackageSource.RoutePrefix + "/files")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = Package }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var patienceCut = new CancellationTokenSource();
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct, patienceCut.Token);
        patienceCut.CancelAfter(patience);
        try
        {
            var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cut.Token);
            return (response, patienceCut.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, patienceCut.IsCancellationRequested);
        }
    }

    /// <summary>
    /// The git transport, reduced to the production split: the LISTING read (a filter that keeps
    /// only each folder's root and manifest) answers with one package; the package-FOLDER read (a
    /// filter that keeps the folder's other files) never answers — the queued clone, held.
    /// </summary>
    private sealed class ListingOnlyRepoClient : IGitHubRepoClient
    {
        private static RepoSnapshot Listing() => new("0123456789abcdef0123456789abcdef01234567",
        [
            new RepoFile($"{Package}/index.json",
                "{\"nodeType\":\"Space\",\"name\":\"" + Package + "\"}"),
        ]);

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Never<RepoSnapshot>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken,
            Func<string, bool> pathFilter)
            => pathFilter($"{Package}/Doc.md")
                ? Observable.Never<RepoSnapshot>()
                : Observable.Return(Listing());

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotUsed<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => NotUsed<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => NotUsed<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken) => NotUsed<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken) => NotUsed<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken) => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new InvalidOperationException("The files-route test uses the fetches only."));
    }
}
