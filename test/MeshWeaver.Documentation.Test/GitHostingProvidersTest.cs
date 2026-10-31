#pragma warning disable CS1591
using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The provider-neutral seam GitSync resolves a git HOSTING provider through
/// (<see cref="IGitHostingProvider"/>, <see cref="GitHostingProviders"/>): a claimed URL leaves the
/// GitHub path, an unclaimed one stays on it, and every way two providers could compete is refused
/// BY NAME rather than resolved by picking one. Uses a fake provider — no hosting service's code is
/// needed to pin the seam.
/// </summary>
public class GitHostingProvidersTest
{
    private const string Claimed = "https://git.example/acme/_repos/content";
    private const string GitHubUrl = "https://github.com/acme/content";

    private sealed class FakeProvider(string kind, string claimPrefix, string? refused = null) : IGitHostingProvider
    {
        public string Kind => kind;
        public bool Claims(string repositoryUrl) => repositoryUrl.StartsWith(claimPrefix, StringComparison.Ordinal);
        public IGitHubRepoClient RepoClient => throw new NotSupportedException("not routed in these tests");
        public IObservable<string> GetAccessToken() => Observable.Return("token-" + kind);
        public GitSyncRefusal? Refuse(string repositoryUrl, string operation)
            => operation == refused ? GitSyncRefusal.Of("gitsync.provider.ambiguous", ("url", repositoryUrl)) : null;
    }

    [Fact]
    public void AClaimedUrl_ResolvesToItsProvider_AndEveryOtherUrlStaysOnTheGitHubPath()
    {
        var provider = new FakeProvider("Example", "https://git.example/");
        var registry = new GitHostingProviders([provider]);
        registry.For(Claimed).Should().BeSameAs(provider);
        registry.Classify(Claimed).Should().Be("Example");
        registry.For(GitHubUrl).Should().BeNull("the negative control: GitHub is the platform's own path");
        registry.Classify(GitHubUrl).Should().Be(GitRepositoryProvider.GitHub);
        registry.Classify("/tmp/remote.git").Should().Be(GitRepositoryProvider.Git);
        registry.For(null).Should().BeNull();
        registry.For("  ").Should().BeNull();
        GitHostingProviders.None.For(Claimed).Should().BeNull("no provider registered: nothing is claimed");
    }

    [Fact]
    public void TwoProvidersOfOneKind_AreRefusedByName()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new GitHostingProviders([
            new FakeProvider("Example", "https://a/"), new FakeProvider("Example", "https://b/")]));
        error.Message.Should().Contain("'Example'").And.Contain("more than once");
    }

    [Theory]
    [InlineData(GitRepositoryProvider.GitHub)]
    [InlineData(GitRepositoryProvider.Git)]
    [InlineData(" ")]
    public void AProviderTakingAPlatformKind_IsRefusedByName(string kind)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new GitHostingProviders([new FakeProvider(kind, "https://a/")]));
        error.Message.Should().Contain("platform's own");
    }

    [Fact]
    public void AUrlTwoProvidersClaim_IsRefusedByName_InBothLanguages()
    {
        var registry = new GitHostingProviders([
            new FakeProvider("First", "https://git.example/"), new FakeProvider("Second", "https://git.example/acme/")]);
        var refusal = Assert.Throws<GitSyncRefusalException>(() => registry.For(Claimed)).Refusal;
        refusal.Key.Should().Be("gitsync.provider.ambiguous");
        refusal.Render("en").Should().Contain(Claimed).And.Contain("First").And.Contain("Second");
        refusal.Render("de").Should().NotBe(refusal.Render("en"));
        registry.For(GitHubUrl).Should().BeNull("the negative control: an unclaimed URL is not ambiguous");
    }

    [Fact]
    public void AContributedProvider_ReplacesABuiltInOfTheSameKind_AndLeavesTheOthers()
    {
        var builtIn = new FakeProvider("Example", "https://git.example/");
        var other = new FakeProvider("Other", "https://other.example/");
        var contributed = new FakeProvider("Example", "https://git.example/");
        var registry = new GitHostingProviders([contributed], [builtIn, other]);
        registry.For(Claimed).Should().BeSameAs(contributed);
        registry.For("https://other.example/x").Should().BeSameAs(other);
        registry.All.Should().HaveCount(2);
    }

    [Fact]
    public void ARefusal_IsTheClaimingProvidersAnswer_AndNothingOnTheGitHubPath()
    {
        var registry = new GitHostingProviders([new FakeProvider("Example", "https://git.example/", GitHostingOperation.Issues)]);
        registry.Refuse(Claimed, GitHostingOperation.Issues).Should().NotBeNull();
        registry.Refuse(Claimed, GitHostingOperation.PullRequest).Should().BeNull("the provider offers it");
        registry.Refuse(GitHubUrl, GitHostingOperation.Issues).Should().BeNull("GitHub refuses nothing here");
    }

    [Fact]
    public void AProvidersDefaults_AuthorAsTheInstance_AndNameItsOwnDescriptionKey()
    {
        IGitHostingProvider provider = new FakeProvider("Example", "https://git.example/");
        provider.CommitAuthorName.Should().Be("MeshWeaver");
        provider.CommitAuthorEmail.Should().Be("noreply@meshweaver.cloud");
        provider.DisplayNameKey.Should().Be("ui.gitSync.provider.Example");
    }

    [Fact]
    public void ThePlatform_ShipsNoHostingProviderOfItsOwn()
    {
        // Azure Repos left with Azure.Identity: it is a module's contribution now. What the platform
        // registers by itself claims nothing, so every URL stays on the GitHub / git path.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new IoPoolRegistry(new IoPoolOptions()));
        services.AddGitHubSyncServices();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<GitHostingProviders>();
        registry.All.Should().BeEmpty();
        registry.Classify("https://dev.azure.com/acme/Content/_git/content").Should().Be(GitRepositoryProvider.Git,
            "without the module an Azure Repos URL is a plain git remote");
        typeof(GitHostingProviders).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name).Should().NotContain("Azure.Identity");
    }

    [Fact]
    public void TheRouter_ForwardsEveryInterfaceMember_DefaultedOnesIncluded()
    {
        // A default interface member the router did not declare would bind to the FALLBACK (a full
        // fetch for a head lookup, "diff unknown" for every diff) instead of the provider's own.
        var map = typeof(GitProviderRepoClient).GetInterfaceMap(typeof(IGitHubRepoClient));
        map.TargetMethods.Where(m => m.DeclaringType != typeof(GitProviderRepoClient))
            .Select(m => m.Name).Should().BeEmpty("every member must be implemented on the router itself");
    }

    [Fact]
    public void TheRouter_FaultsTheSubscription_NotTheCall_ForAnAmbiguousUrl()
    {
        var registry = new GitHostingProviders([
            new FakeProvider("First", "https://git.example/"), new FakeProvider("Second", "https://git.example/")]);
        var router = new GitProviderRepoClient(new NeverCalledClient(), registry);
        // Composing the call must not throw; subscribing must fault with the named refusal.
        var head = router.GetHeadSha(Claimed, "main", "t");
        Exception? error = null;
        using (head.Subscribe(_ => { }, ex => error = ex)) { }
        error.Should().BeOfType<GitSyncRefusalException>()
            .Which.Refusal.Key.Should().Be("gitsync.provider.ambiguous");
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme/Content/_git/content")]
    [InlineData("https://gitlab.example/group/subgroup/repo")]
    public void AUrlDeeperThanOwnerAndRepo_HasNoGitHubIdentity(string url)
    {
        // Read positionally the URL would be "acme/Content" — some GitHub repository, whose push
        // webhook would otherwise import into this source. Structural, so no provider is needed.
        GitHubRepoIdentityResolver.Parse(url).Should().BeNull();
        GitHubRepoIdentityResolver.Parse(GitHubUrl)!.ToString().Should().Be("acme/content",
            "the negative control: a GitHub URL keeps its identity");
        GitHubRepoIdentityResolver.Parse("https://github.com/acme/content.git/")!.ToString().Should().Be("acme/content");
        GitHubRepoIdentityResolver.Parse("acme/content")!.ToString().Should().Be("acme/content");
    }

    [Fact]
    public void TheDirectionRules_KeepTheirShippedExceptionTypeAndMessage()
    {
        // MeshWeaver.Plugins' SyncDirectionAndSourcesTest asserts Assert.Throws<InvalidOperationException>
        // (an EXACT type match) and the English text.
        var export = SyncDirectionGuards.ExportGuard(
            new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ImportOnly })!;
        export.ToException().GetType().Should().Be(typeof(InvalidOperationException));
        export.ToException().Message.Should().Be(
            $"This sync source is import-only (repo → mesh): exporting to {GitHubUrl} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Export-only to commit.");
        export.Key.Should().Be("activity.gitsync.importOnlyNoExport");
        var import = SyncDirectionGuards.ReimportGuard(
            new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.ExportOnly })!;
        import.ToException().Message.Should().Be(
            $"This sync source is export-only (mesh → repo): importing from {GitHubUrl} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Import-only to re-import.");
        SyncDirectionGuards.ExportGuard(new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.Bidirectional })
            .Should().BeNull("the negative control");
        SyncDirectionGuards.ReimportGuard(new GitHubSyncConfig { RepositoryUrl = GitHubUrl, Direction = SyncDirection.Bidirectional })
            .Should().BeNull("the negative control");
    }

    /// <summary>Stands for the GitHub client; the ambiguous route must never reach it.</summary>
    private sealed class NeverCalledClient : IGitHubRepoClient
    {
        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => throw new NotSupportedException();
        public IObservable<RepoSnapshot> Fetch(string repositoryUrl, string commitish, string? subdirectory, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<System.Collections.Generic.IReadOnlyList<GitHubIssue>> ListIssues(string repositoryUrl, GitHubIssueState? state, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => throw new NotSupportedException();
        public IObservable<GitHubIssueComment> CommentIssue(string repositoryUrl, int number, string body, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssue> SetIssueState(string repositoryUrl, int number, GitHubIssueState state, string accessToken) => throw new NotSupportedException();
        public IObservable<System.Collections.Generic.IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(string repositoryUrl, PullRequestStatus? state, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubIssueComment> CommentPullRequest(string repositoryUrl, int number, string body, string accessToken) => throw new NotSupportedException();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => throw new NotSupportedException();
    }
}
