#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#3848 / #4413 — decided by the maintainer, "Record commit on install": the plugin
/// registry's install response returns the EXACT commit it served, and the install record stores
/// that commit, so the combo verifier can check the installed content against it
/// (<see cref="ComboVerifiesTheRecordedCommitTest"/>) instead of the source repository's
/// <c>HEAD</c>.
///
/// <para>Before the decision the files answer carried no commit and the installer recorded the ref
/// it ASKED for (<c>main</c>) — a moving ref, so the verifier could only fetch <c>HEAD</c> and prove
/// the module hash, which fails for every package changed since its install (17 of 86 on the public
/// instance, run 38416709107).</para>
/// </summary>
public class InstallRecordsTheServedCommitTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "ServedCommitPkg";
    private const string Source = "Plugins";
    private const string Repo = "https://github.com/Systemorph/ServedCommitFixture";
    private const string Instance = "served-commit-consumer";
    private static readonly string ServedCommit = new('c', 40);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton<IGitHubRepoClient>(new FixtureRepoClient()));

    // ── The registry half: the install response names the commit ─────────────────────────────────

    [Fact(Timeout = 300_000)]
    public async Task TheRegistryFilesAnswer_NamesTheCommitItServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterInstance(ct);
        var app = await StartRegistryHost(ct);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Post, RegistryPackageSource.RoutePrefix + "/files")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = Package }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var response = await app.GetTestClient().SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = PluginRegistryPayloads.ParseFiles(await response.Content.ReadAsStringAsync(ct));
        fetched.Files.Should().NotBeEmpty("the package's files travel as before");
        fetched.Commit.Should().Be(ServedCommit,
            "the install response states the exact commit the registry read the files at");
    }

    /// <summary>The wire form both ways: a commit round-trips; an OLD registry's answer (no
    /// <c>commit</c>) and a commit that is not a full sha both parse as none.</summary>
    [Fact]
    public void TheFilesPayload_CarriesTheCommit_AndAnOldAnswerCarriesNone()
    {
        IReadOnlyList<PackageFile> files = [new PackageFile($"{Package}/Doc.md", "# Doc")];

        PluginRegistryPayloads.ParseFiles(PluginRegistryPayloads.Files(new PackageFetch(files, ServedCommit)))
            .Commit.Should().Be(ServedCommit);
        PluginRegistryPayloads.ParseFiles(PluginRegistryPayloads.Files(files))
            .Commit.Should().BeNull("an answer without a commit names none");
        PluginRegistryPayloads.Files(new PackageFetch(files, "main"))
            .Should().Be(PluginRegistryPayloads.Files(files),
                "a ref that is not a commit is never stated as one");
    }

    // ── The consumer half: the install record stores it ─────────────────────────────────────────

    [Fact(Timeout = 300_000)]
    public async Task AnInstall_RecordsTheCommitTheSourceServed_NotTheRefItAskedFor()
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(Snapshot()), Repo);

        var record = await InstallAndReadRecord(source, TestContext.Current.CancellationToken);

        record.InstalledFromRef.Should().Be(ServedCommit,
            "the record states the exact commit the source served, so the verifier fetches the "
            + "installed content and not whatever 'main' points at by then");
    }

    /// <summary>NEGATIVE CONTROL — a source that cannot name a commit (an old registry) records the
    /// ref it asked for, exactly as before the decision.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AnInstallFromASourceThatNamesNoCommit_RecordsTheRequestedRef_AsBefore()
    {
        var record = await InstallAndReadRecord(new NoCommitSource(), TestContext.Current.CancellationToken);

        record.InstalledFromRef.Should().Be("main");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private async Task<PackageManifest> InstallAndReadRecord(IPackageSource source, CancellationToken ct)
    {
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallRecordsTheServedCommitTest>();
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "main", Candidate(), logger)
            .Timeout(TimeSpan.FromSeconds(180))
            .Await(ct);

        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{Package}", Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(ct);
        var record = node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
        record.Should().NotBeNull("the installer stamps an install record");
        return record!;
    }

    private static PackageManifest Candidate() => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
    };

    private static RepoSnapshot Snapshot() => new(ServedCommit,
    [
        new RepoFile($"{Package}/index.json", $$"""
            { "id": "{{Package}}", "path": "{{Package}}", "nodeType": "Space", "name": "{{Package}}", "state": "Active" }
            """),
        new RepoFile($"{Package}/Doc.md", "# Doc\n\nThe package's one content node."),
    ]);

    private sealed class NoCommitSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([Candidate()]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return<IReadOnlyList<PackageFile>>(
                [.. Snapshot().Files.Select(f => new PackageFile(f.Path, f.Content))]);
    }

    private Task<string> RegisterInstance(CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection([new KeyValuePair<string, string?>(
                        $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0", $"{Source}/*")])
                    .Build())
            .Register("served-commit-owner", "Served Commit Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(ct);

    private async Task<WebApplication> StartRegistryHost(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
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

    /// <summary>The git transport: every read of the repository resolves 'main' to
    /// <see cref="ServedCommit"/> — the snapshot sha a real transport reports.</summary>
    private sealed class FixtureRepoClient : IGitHubRepoClient
    {
        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(Snapshot());

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken,
            Func<string, bool> pathFilter)
            => Observable.Return(new RepoSnapshot(ServedCommit,
                Snapshot().Files.Where(f => pathFilter(f.Path)).ToImmutableList()));

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
            new InvalidOperationException("The served-commit test uses the fetches only."));
    }
}
