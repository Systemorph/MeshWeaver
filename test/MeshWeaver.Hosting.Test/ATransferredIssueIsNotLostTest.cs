using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A state question must not be answerable only through the comments</b> —
/// Systemorph/MeshWeaver#4629.
///
/// <para>GitHub's transfer redirect covers the ISSUE resource and not its sub-resources. Measured
/// 2026-09-17 over REST: <c>Systemorph/MeshWeaver#2950</c>, transferred to
/// <c>Systemorph/MeshWeaver.Plugins#1139</c>, answers <b>200</b> for the issue — carrying its new
/// number and repository — and <b>404</b> for <c>/comments</c> and <c>/events</c>. The old
/// <c>GetIssue</c> read both legs and faulted the whole call on the second, so a successful read of
/// a real issue was discarded and a transferred issue was indistinguishable from a deleted one.
/// That is how <c>Admin/_LogIncident/9b70b639c4e77af3</c> sat at <c>Failed</c> / "Not Found" from
/// 2026-09-01 (MeshWeaver.Plugins#2028).</para>
///
/// <para>🚨 <b>What this file can and cannot pin.</b> The redirect arms live in
/// <c>OctokitGitHubRepoClient</c>, against a live HTTP endpoint — there is no seam here that could
/// exercise them without mocking the transport, which this repository does not do, so those arms
/// are evidenced by the REST measurement above rather than by a test. What IS testable, and what
/// this file pins, is the half every OTHER implementer inherits: the interface's DEFAULT
/// <see cref="IGitHubRepoClient.FindIssueState"/>. It is the reason adding this member obliges no
/// implementer, and a wrong default would silently change what every stub client answers.</para>
/// </summary>
public class ATransferredIssueIsNotLostTest
{
    private const string Repo = "https://github.com/Systemorph/MeshWeaver";

    [Fact(Timeout = 30_000)]
    public async Task TheDefaultFindIssueState_DelegatesToGetIssue_SoNoImplementerIsObliged()
    {
        var stub = new OnlyGetIssueClient(new GitHubIssue
        {
            Number = 1139,
            State = GitHubIssueState.Closed,
            ClosedAt = new DateTimeOffset(2026, 9, 1, 23, 38, 30, TimeSpan.Zero),
            Url = "https://github.com/Systemorph/MeshWeaver.Plugins/issues/1139",
        });

        // Through the INTERFACE deliberately: a default member is reached that way and not off the
        // concrete type, which is exactly why adding it obliges no implementer.
        IGitHubRepoClient client = stub;

        var found = await client.FindIssueState(Repo, 2950, "token")
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(1139, found!.Number);
        Assert.Equal(GitHubIssueState.Closed, found.State);
        Assert.Equal(1, stub.GetIssueCalls);
    }

    /// <summary>
    /// The flag exists so an empty comment list can say WHICH empty it is. A caller that reads
    /// <see cref="GitHubIssue.Comments"/> without consulting it cannot tell "no comments" from
    /// "the comments could not be read", which is the confusion #4629 is about.
    /// </summary>
    [Fact]
    public void AnIssueDeclaresWhetherItsCommentListIsWhole()
    {
        var whole = new GitHubIssue { Number = 1 };
        Assert.True(whole.CommentsAreComplete,
            "a read that returned the comments must not look partial — the default is the complete answer");
        Assert.Empty(whole.Comments);

        var partial = whole with { CommentsAreComplete = false };
        Assert.False(partial.CommentsAreComplete);
        Assert.Empty(partial.Comments);
        Assert.NotEqual(whole, partial);
    }

    /// <summary>A client that implements only the members it needs — the shape every stub in this
    /// project has, and the one that proves the new member obliges nobody.</summary>
    private sealed class OnlyGetIssueClient : IGitHubRepoClient
    {
        private readonly GitHubIssue issue;

        public OnlyGetIssueClient(GitHubIssue issue) => this.issue = issue;

        public int GetIssueCalls { get; private set; }

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        {
            GetIssueCalls++;
            return Observable.Return(issue);
        }

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
            => NotUsed<GitHubPushResult>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => NotUsed<RepoSnapshot>();

        public IObservable<IReadOnlyList<string>?> GetChangedPaths(
            string repositoryUrl, string baseSha, string headSha, string accessToken)
            => NotUsed<IReadOnlyList<string>?>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
            => NotUsed<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
            => NotUsed<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
            => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>()
            => Observable.Throw<T>(new NotSupportedException(
                "this stub implements only what the test exercises"));
    }
}
