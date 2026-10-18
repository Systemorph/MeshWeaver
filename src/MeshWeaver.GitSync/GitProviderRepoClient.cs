using System.Reactive.Linq;

namespace MeshWeaver.GitSync;

/// <summary>
/// The provider seam of GitSync (MeshWeaver#6500): ONE <see cref="IGitHubRepoClient"/> that sends
/// every operation on an <b>Azure Repos</b> URL (<see cref="GitRepositoryProvider.AzureRepos"/>) to
/// <see cref="AzureReposRepoClient"/> and every other URL to the GitHub client it wraps. The
/// provider is read off the URL on each call (<see cref="GitRepositoryProvider.Classify"/>), never
/// stored, so <see cref="GitHubSyncService"/>, the pull-request and issue services and the branch
/// reconcile keep ONE code path and the import, export and two-way rules above the client are the
/// same for both providers by construction.
///
/// <para>🚨 <b>Every interface member is forwarded explicitly — the default-implemented ones
/// included</b> (<c>Fetch</c> with a filter, <c>GetHeadSha</c>, <c>GetChangedPaths</c>,
/// <c>IsAncestor</c>, <c>GetCanonicalRepository</c>, <c>FindIssueState</c>). Inheriting a default
/// here would silently replace the inner client's real implementation with the fallback (a full
/// fetch for a head lookup, "diff unknown" for every diff), because a default member is bound on
/// the type that declares the interface, not on the object it would have reached.</para>
/// </summary>
/// <param name="github">The client for GitHub and every other git remote.</param>
/// <param name="azureRepos">The client for Azure Repos URLs.</param>
public sealed class GitProviderRepoClient(IGitHubRepoClient github, IGitHubRepoClient azureRepos) : IGitHubRepoClient
{
    /// <summary>The client <paramref name="repositoryUrl"/> is served by.</summary>
    /// <param name="repositoryUrl">The repository URL.</param>
    /// <returns>The Azure Repos client for an Azure Repos URL, else the GitHub client.</returns>
    public IGitHubRepoClient For(string? repositoryUrl)
        => GitRepositoryProvider.IsAzureRepos(repositoryUrl) ? azureRepos : github;

    /// <inheritdoc />
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
        => For(request.RepositoryUrl).Push(request);

    /// <inheritdoc />
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => For(repositoryUrl).Fetch(repositoryUrl, commitish, subdirectory, accessToken);

    /// <inheritdoc />
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
        => For(repositoryUrl).Fetch(repositoryUrl, commitish, subdirectory, accessToken, pathFilter);

    /// <inheritdoc />
    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => For(repositoryUrl).GetHeadSha(repositoryUrl, commitish, accessToken);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>?> GetChangedPaths(
        string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        => For(repositoryUrl).GetChangedPaths(repositoryUrl, baseSha, headSha, subdirectory, accessToken);

    /// <inheritdoc />
    public IObservable<bool> IsAncestor(
        string repositoryUrl, string ancestorSha, string descendantRef, string accessToken)
        => For(repositoryUrl).IsAncestor(repositoryUrl, ancestorSha, descendantRef, accessToken);

    /// <inheritdoc />
    /// <remarks>An Azure Repos URL answers null ("unknown"): the GitHub rename lookup does not apply
    /// to it, and <see cref="GitHubRepoIdentityResolver"/> never resolves one.</remarks>
    public IObservable<RepoIdentity?> GetCanonicalRepository(string repositoryUrl, string accessToken)
        => GitRepositoryProvider.IsAzureRepos(repositoryUrl)
            ? Observable.Return<RepoIdentity?>(null)
            : github.GetCanonicalRepository(repositoryUrl, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
        => For(request.RepositoryUrl).CreateBranch(request);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
        => For(request.RepositoryUrl).OpenPullRequest(request);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken)
        => For(repositoryUrl).GetPullRequestStatus(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken)
        => For(repositoryUrl).ListIssues(repositoryUrl, state, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        => For(repositoryUrl).GetIssue(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue?> FindIssueState(string repositoryUrl, int number, string accessToken)
        => For(repositoryUrl).FindIssueState(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
        => For(request.RepositoryUrl).CreateIssue(request);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
        => For(repositoryUrl).CommentIssue(repositoryUrl, number, body, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
        => For(repositoryUrl).SetIssueState(repositoryUrl, number, state, accessToken);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
        => For(repositoryUrl).ListPullRequests(repositoryUrl, state, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken)
        => For(repositoryUrl).GetPullRequestDetail(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken)
        => For(repositoryUrl).CommentPullRequest(repositoryUrl, number, body, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
        => For(request.RepositoryUrl).MergePullRequest(request);
}
