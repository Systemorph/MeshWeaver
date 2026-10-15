using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A package installed from a CONFIGURED GitHub source updates itself on a green build of that
/// source</b> — not only one installed from a <c>PluginCatalog</c> node's repository.
///
/// <para>The measured defect (an enterprise client's test instance, 2026-10-13/14): the instance
/// mounts a GitHub repository as a package source (<c>PluginCatalog:Sources:N:RepoPath</c>, no
/// catalog node), the repository's green-build fact arrived at <c>Admin/_Build/{owner}.{repo}</c>,
/// and the Auto install record stayed on its old commit for 14+ hours.
/// <see cref="PluginUpdateWatcher"/> reconciled installed packages only for catalog NODES; for a
/// configured source it subscribed to the very same build node and only evicted the listing cache.
/// <see cref="RegistryUpdateReconciler"/> reconciles only against HTTP registries, so nothing ever
/// looked again.</para>
///
/// <para>This drives the real hosted watcher over a mesh whose CONFIGURATION names the source and
/// whose GitHub client serves two commits of a node repository: the Auto record installs the new
/// build, the Notify record gets a reminder and keeps its installed content.</para>
/// </summary>
public class ConfiguredSourceReconcilesOnGreenBuildTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/acme/client-packages";
    private const string Origin = "github.com/acme/client-packages";
    private const string OldSha = "1111111111111111111111111111111111111111";
    private const string NewSha = "2222222222222222222222222222222222222222";
    private const string AutoPkg = "ConfiguredAutoPkg";
    private const string NotifyPkg = "ConfiguredNotifyPkg";

    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The BuildCompletion node type, as a portal with GitHub sync registers it.
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services
                // The measured shape: a GitHub repository mounted as a configured source, no node.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PluginCatalog:Sources:0:RepoPath"] = RepoUrl,
                        ["PluginCatalog:Sources:0:Format"] = "node-repo",
                        ["PluginCatalog:Sources:0:Ref"] = "main",
                        ["PluginCatalog:Sources:0:Name"] = "Client",
                    })
                    .Build())
                .AddSingleton<IGitHubRepoClient>(new TwoCommitRepoClient())
                // Nothing is installed at boot: the records under test are seeded as they stand on
                // the instance — installed at the OLD commit.
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 300_000)]
    public async Task AGreenBuildOfAConfiguredSource_InstallsTheAutoRecord_AndRemindsTheNotifyRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(AutoPkg, PackageUpdatePolicy.Auto, ct);
        await Seed(NotifyPkg, PackageUpdatePolicy.Notify, ct);

        // The green-build fact, exactly as GitHubWebhookProcessor writes it.
        await WriteBuild(NewSha, ct);

        var auto = await AwaitRecord(AutoPkg, r => r.ModuleVersion == ModuleVersion(AutoPkg, NewSha), 240, ct);
        auto.Version.Should().Be("2.0.0", "the Auto record now names the new build's released version");
        auto.Origin.Should().Be(Origin, "the update stays bound to the configured source's origin");

        var notify = await AwaitRecord(NotifyPkg, r => r.NotifiedModuleVersion == ModuleVersion(NotifyPkg, NewSha), 60, ct);
        notify.ModuleVersion.Should().Be(ModuleVersion(NotifyPkg, OldSha),
            "a Notify record is REMINDED about the new build; its installed content does not move");
    }

    /// <summary>
    /// 🚨 A lost reaction self-heals: the safety net re-reads the DURABLE build fact and runs the
    /// same reconcile. The record is seeded only after the live stream has already seen this sha
    /// (and the stream never re-emits the same sha), so only the safety-net pass can reach it.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheSafetyNet_ReconcilesFromTheStoredBuildFact_WhenTheLiveReactionWasMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = Mesh.ServiceProvider.GetRequiredService<PluginUpdateWatcher>();
        await WriteBuild(NewSha, ct);
        // Drain whatever the live stream enqueued for this sha (the lane is serialized).
        await watcher.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        await Seed(NotifyPkg, PackageUpdatePolicy.Notify, ct);
        await watcher.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        var notify = await AwaitRecord(NotifyPkg, r => r.NotifiedModuleVersion == ModuleVersion(NotifyPkg, NewSha), 60, ct);
        notify.Origin.Should().Be(Origin);
    }

    /// <summary>
    /// The NEGATIVE control: a record installed from ANOTHER repository is not touched by this
    /// source's build — the configured source reconciles bound to its own origin, never by id alone.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARecordFromAnotherOrigin_IsNotUpdatedByTheConfiguredSourcesBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var watcher = Mesh.ServiceProvider.GetRequiredService<PluginUpdateWatcher>();
        await Seed(NotifyPkg, PackageUpdatePolicy.Notify, ct, origin: "github.com/someone-else/packages");
        await Seed(AutoPkg, PackageUpdatePolicy.Notify, ct);
        await WriteBuild(NewSha, ct);

        // Positive signal that the pass ran: the same-origin control record is reminded.
        await AwaitRecord(AutoPkg, r => r.NotifiedModuleVersion == ModuleVersion(AutoPkg, NewSha), 60, ct);
        await watcher.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        var foreign = await AwaitRecord(NotifyPkg, _ => true, 30, ct);
        foreign.NotifiedModuleVersion.Should().BeNull("a candidate from a repository the package was not installed from is ignored");
        foreign.ModuleVersion.Should().Be(ModuleVersion(NotifyPkg, OldSha));
    }

    private Task WriteBuild(string sha, CancellationToken ct)
    {
        var path = BuildCompletion.PathFor("acme", "client-packages");
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = BuildCompletion.NodeType,
            Name = "acme/client-packages build",
            State = MeshNodeState.Active,
            Content = new BuildCompletion
            {
                RepositoryUrl = RepoUrl,
                Branch = "main",
                HeadSha = sha,
                WorkflowName = "CI",
                WorkflowPath = ".github/workflows/ci.yml",
                RunNumber = 7,
                Conclusion = "success",
            },
        };
        return Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateOrUpdateNode(node))
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private async Task Seed(string id, PackageUpdatePolicy policy, CancellationToken ct, string origin = Origin)
    {
        var auto = policy == PackageUpdatePolicy.Auto;
        var manifest = new PackageManifest
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = "1.0.0",
            ModuleVersion = ModuleVersion(id, OldSha),
            Source = "Client",
            Origin = origin,
            InstalledFromRef = "main",
            AutoUpdate = auto,
            UpdatePolicy = policy,
            UpdatePolicySetAt = DateTimeOffset.UtcNow,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{id}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = manifest,
        };
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record))
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private async Task<PackageManifest> AwaitRecord(string id, Func<PackageManifest, bool> predicate, int seconds, CancellationToken ct) =>
        await Mesh.GetMeshNodeStream($"{PackageInstaller.InstalledPartition}/{id}")
            .Select(n => n?.ContentAs<PackageManifest>(Json))
            .Where(r => r is not null && predicate(r))
            .Select(r => r!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(seconds)).Await(ct);

    private static string ModuleVersion(string id, string sha) => $"mv-{id}-{sha[..4]}";

    /// <summary>One package at one commit: a Space root, a page, and the CI-maintained manifest.lock
    /// carrying the content identity the reconcile compares.</summary>
    private static IEnumerable<RepoFile> Package(string id, string sha, string version) =>
    [
        new RepoFile($"{id}/index.json",
            $$"""{"$type":"MeshNode","id":"{{id}}","namespace":"","path":"{{id}}","mainNode":"{{id}}","name":"{{id}}","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"{{id}}."} }"""),
        new RepoFile($"{id}/Page.json",
            $$"""{"$type":"MeshNode","id":"Page","namespace":"{{id}}","path":"{{id}}/Page","mainNode":"{{id}}/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# {{id}} at {{sha}}"}"""),
        new RepoFile($"{id}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{id}}",
              "moduleVersion": "{{ModuleVersion(id, sha)}}",
              "version": "{{version}}",
              "files": {
                "{{id}}/index.json": "{{sha[..8]}}a",
                "{{id}}/Page.json": "{{sha[..8]}}b"
              }
            }
            """),
    ];

    /// <summary>Serves the repository at two commits; any other ref (the branch name) is the new one.</summary>
    private sealed class TwoCommitRepoClient : IGitHubRepoClient
    {
        private static RepoSnapshot At(string sha) =>
            new(sha, [.. Package(AutoPkg, sha, sha == OldSha ? "1.0.0" : "2.0.0"),
                      .. Package(NotifyPkg, sha, sha == OldSha ? "1.0.0" : "2.0.0")]);

        private static IObservable<TResult> Never<TResult>() => Observable.Never<TResult>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken) =>
            Observable.Return(At(commitish == OldSha ? OldSha : NewSha));

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => Never<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => Never<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => Never<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => Never<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken) => Never<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => Never<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => Never<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken) => Never<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken) => Never<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken) => Never<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => Never<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken) => Never<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => Never<GitHubMergeResult>();
    }
}
