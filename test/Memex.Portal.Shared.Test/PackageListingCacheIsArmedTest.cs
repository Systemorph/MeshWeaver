using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A cache nobody arms is the per-request repository read, still there and now harder to
/// see.</b> <see cref="PackageListingCacheTest"/> drives the cache directly; this drives the path
/// <c>GET /api/plugins</c> actually takes — a REAL mesh built with <c>AddPluginCatalog</c>, and
/// <c>PackageSources.FromRepo</c> called once per "request" exactly as
/// <c>PluginRegistryEndpoints.Sources</c> calls it.
///
/// <para>Without this, every assertion in the sibling suite could hold while
/// <c>AddPluginCatalog</c> registered nothing, <c>FromRepo</c> wrapped nothing, and production kept
/// cloning the plugins repository on every request (MeshWeaver#4222).</para>
/// </summary>
public class PackageListingCacheIsArmedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Repo = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string Ref = "main";

    private readonly CountingRepoClient repoClient = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton<IGitHubRepoClient>(repoClient));

    /// <summary>
    /// 🚨 The arming half: <c>AddPluginCatalog</c> puts a cache on the mesh. Resolved from the mesh's
    /// own provider, not restated — a restatement of the registration would agree with itself while
    /// the deployment registered nothing.
    /// </summary>
    [Fact]
    public void AddPluginCatalog_ArmsTheListingCache()
    {
        var cache = Mesh.ServiceProvider.GetService<PackageListingCache>();

        Assert.NotNull(cache);
        Assert.True(cache.Enabled,
            "the listing cache is registered but switched off by default. The default has to CACHE — "
            + "PluginCatalog:ListingCacheSeconds is the opt-out, not the opt-in (#4222).");
        Assert.Equal(PackageListingCache.DefaultWindow, cache.Window);
    }

    /// <summary>
    /// The end-to-end property, over the real factory: five requests, five freshly-built sources —
    /// one repository fetch. The loop rebuilds the source every time on purpose; that is what
    /// <c>PluginRegistryEndpoints.Sources</c> does, and it is why the cache cannot live on the
    /// source instance.
    /// </summary>
    [Fact]
    public async Task FiveRequestsThroughTheRealFactory_FetchTheRepositoryOnce()
    {
        for (var request = 0; request < 5; request++)
            await Request();

        Assert.Equal(1, repoClient.Fetches);
    }

    /// <summary>
    /// 🚨 The LISTING asks for the narrow transfer, and the INSTALL still asks for the whole
    /// package folder — the two halves of #4222's second fix, asserted against the real factory.
    ///
    /// <para>A listing that quietly reverted to the unfiltered <c>Fetch</c> would still answer
    /// correctly and still be cached, so nothing else in this suite could see it: the only symptom
    /// is that the registry goes back to moving the whole repository (47.8 MB / 13 s against
    /// MeshWeaver.Plugins) for the manifests it parses. Narrowing the INSTALL would be the opposite
    /// and much louder failure — an empty package — which is why the second half is pinned too.</para>
    /// </summary>
    [Fact]
    public async Task TheListingAsksForTheNarrowFetch_AndTheInstallStillReadsTheWholeFolder()
    {
        await Request();

        Assert.Equal(1, repoClient.Fetches);
        Assert.Equal(1, repoClient.FilteredFetches);

        var source = PackageSources.FromRepo(Mesh, Repo, sourceSubdir: null, logger: null, nodeRepo: true);
        Assert.NotNull(source);
        await source.FetchPackageFiles(new PackageManifest { Id = "Widget", SourceFolder = "Widget" }, Ref)
            .Should().Within(TestTimeouts.Quick)
            .Emit("an install must read the package's files",
                cancellationToken: TestContext.Current.CancellationToken);

        // The install read the repository again (file fetches are deliberately NOT cached) and it
        // did so UNFILTERED — the filtered count did not move.
        Assert.Equal(2, repoClient.Fetches);
        Assert.Equal(1, repoClient.FilteredFetches);
    }

    /// <summary>
    /// 🚨 And the green build still gets through: after the webhook's eviction the next request
    /// fetches again, so a merge is visible without waiting out the freshness window.
    /// </summary>
    [Fact]
    public async Task AfterAGreenBuildEviction_TheNextRequestFetchesAgain()
    {
        await Request();
        await Request();
        // 🚨 The control that keeps the assertion below from passing on an UNCACHED path, where the
        // count would climb anyway: two requests, one fetch.
        Assert.Equal(1, repoClient.Fetches);

        Mesh.ServiceProvider.GetRequiredService<PackageListingCache>().EvictRepo(Repo);

        await Request();
        Assert.Equal(2, repoClient.Fetches);
    }

    /// <summary>
    /// One request, the way <c>PluginRegistryEndpoints.Sources</c> makes one: a source built from
    /// scratch, then listed. 🚨 Awaited through the reactive assertion helper, never <c>.Wait()</c> —
    /// a blocking bridge would wedge the run instead of naming what did not arrive.
    /// </summary>
    private Task<IReadOnlyList<PackageManifest>> Request()
    {
        var source = PackageSources.FromRepo(Mesh, Repo, sourceSubdir: null, logger: null, nodeRepo: true);
        Assert.NotNull(source);
        return source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the real factory's source must produce a listing",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 THE SCOPE CONTROL. A LOCAL directory source is NOT cached, and that is a correctness
    /// boundary rather than a tuning choice: a mounted source's listed version is contractually a
    /// function of the files on disk right now — <c>LocalSourceContentVersionTest</c> pins that an
    /// edit shows on the very next listing — and there is no webhook to invalidate it. Caching one
    /// would make a local-dev or air-gapped registry report yesterday's content for five minutes.
    /// </summary>
    [Fact]
    public async Task ALocalDirectorySource_IsNotCached()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-listing-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Widget"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Widget", "index.json"),
                """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget."}}""");
            var file = Path.Combine(root, "Widget", "Page.md");
            File.WriteAllBytes(file, [65, 66, 67]);

            var source = PackageSources.FromRepo(Mesh, root, "", nodeRepo: true);
            Assert.NotNull(source);

            var before = await Listed(source);
            File.WriteAllBytes(file, [68, 69, 70]);
            var after = await Listed(source);

            Assert.NotEqual(before.Version, after.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PackageManifest> Listed(IPackageSource source) =>
        (await source.ListPackages("HEAD").Should().Within(TestTimeouts.Quick)
            .Emit("a local directory source must list its packages",
                cancellationToken: TestContext.Current.CancellationToken)).Single();

    /// <summary>Counts the repository reads the listing path actually performs — the number #4222 is about.</summary>
    private sealed class CountingRepoClient : IGitHubRepoClient
    {
        private int fetches;
        private int filteredFetches;

        public int Fetches => fetches;

        /// <summary>
        /// 🚨 How many reads asked for the NARROW transfer. This override is what makes that
        /// question answerable at all: with only the four-argument <c>Fetch</c> implemented, the
        /// interface's DEFAULT five-argument member forwards to it, so every assertion below would
        /// have held whether or not <c>PackageSources</c> wired <c>NarrowFetch</c> — and the
        /// listing could regress to whole-repository reads with this suite still green (#4222).
        /// </summary>
        public int FilteredFetches => filteredFetches;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            System.Threading.Interlocked.Increment(ref fetches);
            return Observable.Return(new RepoSnapshot("sha-1", []));
        }

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken,
            Func<string, bool> pathFilter)
        {
            System.Threading.Interlocked.Increment(ref fetches);
            System.Threading.Interlocked.Increment(ref filteredFetches);
            return Observable.Return(new RepoSnapshot("sha-1", []));
        }

        // Everything below the listing path. NotSupported rather than a canned answer: the catalog
        // listing must never reach any of them, and a test that silently succeeded through one
        // would be measuring something other than #4222.
        private static IObservable<T> NotOnThisPath<T>() =>
            Observable.Throw<T>(new NotSupportedException("the catalog listing path never calls this"));

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotOnThisPath<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) =>
            NotOnThisPath<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) =>
            NotOnThisPath<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => NotOnThisPath<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken) =>
            NotOnThisPath<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) =>
            NotOnThisPath<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotOnThisPath<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken) =>
            NotOnThisPath<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken) =>
            NotOnThisPath<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken) =>
            NotOnThisPath<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => NotOnThisPath<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken) =>
            NotOnThisPath<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) =>
            NotOnThisPath<GitHubMergeResult>();
    }
}
