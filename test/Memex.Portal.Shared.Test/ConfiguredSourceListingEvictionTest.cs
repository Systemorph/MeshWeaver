using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The listing cache has to be invalidated for the sources <c>/api/plugins</c> actually
/// SERVES, and those are a DIFFERENT list from the one <c>PluginUpdateWatcher</c> historically
/// watched</b> (MeshWeaver#4222, found by review).
///
/// <para><c>WatchCatalog</c> walks <c>PluginCatalog</c> NODES (<c>SourceRepoPath</c>); the registry
/// endpoint reads <c>PluginCatalog:Sources:N:RepoPath</c> from CONFIGURATION — which is how the
/// fleet registry is wired. Evicting only on the first would have left the freshness window as the
/// ONLY invalidation for the sources that matter, silently and with the documentation claiming
/// otherwise. This test drives the real <c>Start()</c> over a real mesh whose CONFIG names a source
/// and asserts the watch is opened on that repository's build node.</para>
/// </summary>
public class ConfiguredSourceListingEvictionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Repo = "https://github.com/Systemorph/MeshWeaver.Plugins";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // The registry's OWN shape: sources declared in configuration, no catalog node.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PluginCatalog:Sources:0:RepoPath"] = Repo,
                        ["PluginCatalog:Sources:0:Format"] = "node-repo",
                        ["PluginCatalog:Sources:0:Ref"] = "main",
                        ["PluginCatalog:Sources:0:Name"] = "Plugins",
                    })
                    .Build())
                .AddSingleton<IGitHubRepoClient>(new SilentRepoClient()));

    [Fact]
    public void AConfiguredSource_OpensAnEvictionWatchOnItsBuildNode()
    {
        var lines = new List<string>();
        var watcher = new PluginUpdateWatcher(Mesh, new CollectingLogger<PluginUpdateWatcher>(lines));
        using (watcher)
        {
            watcher.Start();
        }

        var expected = BuildCompletion.PathFor("Systemorph", "MeshWeaver.Plugins");
        Assert.True(
            lines.Any(l => l.Contains(expected, StringComparison.Ordinal)
                           && l.Contains("invalidate the listing cache", StringComparison.Ordinal)),
            "the watcher opened no eviction watch for the CONFIGURED source. /api/plugins serves "
            + "PluginCatalog:Sources:N:RepoPath, not the PluginCatalog node list, so without this "
            + "watch a green build never invalidates the listing the registry actually serves "
            + $"(#4222). Expected a line naming '{expected}'. Lines were:\n  "
            + string.Join("\n  ", lines));
    }

    /// <summary>
    /// 🚨 The NEGATIVE half, so the assertion above cannot pass on a watcher that opens a watch for
    /// everything it can name: a repository that is NOT configured gets none. The watch list is the
    /// CONFIGURED source list, read through <c>PackageSources.FromConfiguration</c> — the same
    /// reader the endpoint uses — not an enumeration of whatever build nodes happen to exist.
    /// </summary>
    [Fact]
    public void ARepositoryThatIsNotConfigured_GetsNoEvictionWatch()
    {
        var lines = new List<string>();
        var watcher = new PluginUpdateWatcher(Mesh, new CollectingLogger<PluginUpdateWatcher>(lines));
        using (watcher)
        {
            watcher.Start();
        }

        var unconfigured = BuildCompletion.PathFor("Systemorph", "MeshWeaver.Education");
        Assert.DoesNotContain(lines, l => l.Contains(unconfigured, StringComparison.Ordinal));
    }

    private sealed class CollectingLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink)
                sink.Add(formatter(state, exception));
        }
    }

    /// <summary>Answers nothing — the watcher must open its subscriptions without fetching.</summary>
    private sealed class SilentRepoClient : IGitHubRepoClient
    {
        private static IObservable<TResult> Never<TResult>() => Observable.Never<TResult>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken) => Never<RepoSnapshot>();

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
