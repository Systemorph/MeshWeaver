using System.Reactive.Linq;

namespace MeshWeaver.GitSync;

/// <summary>
/// The provider seam of GitSync: ONE <see cref="IGitHubRepoClient"/> that sends every operation on a
/// URL a registered <see cref="IGitHostingProvider"/> claims to that provider's
/// <see cref="IGitHostingProvider.RepoClient"/>, and every other URL to the GitHub client it wraps.
/// The provider is resolved off the URL on each call (<see cref="GitHostingProviders.For"/>), never
/// stored, so <see cref="GitHubSyncService"/>, the pull-request and issue services and the branch
/// reconcile keep ONE code path and the import, export and two-way rules above the client are the
/// same for every provider by construction.
///
/// <para>🚨 <b>Every interface member is forwarded explicitly — the default-implemented ones
/// included</b> (<c>Fetch</c> with a filter, <c>GetHeadSha</c>, <c>GetChangedPaths</c>,
/// <c>IsAncestor</c>, <c>GetCanonicalRepository</c>, <c>FindIssueState</c>). Inheriting a default
/// here would silently replace the inner client's real implementation with the fallback (a full
/// fetch for a head lookup, "diff unknown" for every diff), because a default member is bound on
/// the type that declares the interface, not on the object it would have reached.</para>
///
/// <para>Every member resolves the provider when it is SUBSCRIBED, so a URL two providers claim
/// faults that subscription by name (<c>gitsync.provider.ambiguous</c>) instead of throwing out of
/// the call that composed it.</para>
/// </summary>
public sealed class GitProviderRepoClient : IGitHubRepoClient
{
    private readonly IGitHubRepoClient github;
    private readonly GitHostingProviders providers;

    /// <summary>Routes every URL a registered hosting provider claims to that provider.</summary>
    /// <param name="github">The client for GitHub and every other git remote.</param>
    /// <param name="providers">The registered hosting providers.</param>
    public GitProviderRepoClient(IGitHubRepoClient github, GitHostingProviders providers)
    {
        this.github = github;
        this.providers = providers;
    }

    /// <summary>The client <paramref name="repositoryUrl"/> is served by.</summary>
    /// <param name="repositoryUrl">The repository URL.</param>
    /// <returns>The claiming provider's client, else the GitHub client.</returns>
    /// <exception cref="GitSyncRefusalException">Two providers claim the URL.</exception>
    public IGitHubRepoClient For(string? repositoryUrl)
        => providers.For(repositoryUrl)?.RepoClient ?? github;

    private IObservable<T> Route<T>(string? repositoryUrl, Func<IGitHubRepoClient, IObservable<T>> call)
        => Observable.Defer(() => call(For(repositoryUrl)));

    /// <inheritdoc />
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
        => Route(request.RepositoryUrl, c => c.Push(request));

    /// <inheritdoc />
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => Route(repositoryUrl, c => c.Fetch(repositoryUrl, commitish, subdirectory, accessToken));

    /// <inheritdoc />
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
        => Route(repositoryUrl, c => c.Fetch(repositoryUrl, commitish, subdirectory, accessToken, pathFilter));

    /// <inheritdoc />
    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => Route(repositoryUrl, c => c.GetHeadSha(repositoryUrl, commitish, accessToken));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>?> GetChangedPaths(
        string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        => Route(repositoryUrl, c => c.GetChangedPaths(repositoryUrl, baseSha, headSha, subdirectory, accessToken));

    /// <inheritdoc />
    public IObservable<bool> IsAncestor(
        string repositoryUrl, string ancestorSha, string descendantRef, string accessToken)
        => Route(repositoryUrl, c => c.IsAncestor(repositoryUrl, ancestorSha, descendantRef, accessToken));

    /// <inheritdoc />
    /// <remarks>A URL a hosting provider claims answers null ("unknown"): the GitHub rename lookup
    /// does not apply to it, and <see cref="GitHubRepoIdentityResolver"/> never resolves one.</remarks>
    public IObservable<RepoIdentity?> GetCanonicalRepository(string repositoryUrl, string accessToken)
        => Observable.Defer(() => providers.For(repositoryUrl) is not null
            ? Observable.Return<RepoIdentity?>(null)
            : github.GetCanonicalRepository(repositoryUrl, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
        => Route(request.RepositoryUrl, c => c.CreateBranch(request));

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
        => Route(request.RepositoryUrl, c => c.OpenPullRequest(request));

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken)
        => Route(repositoryUrl, c => c.GetPullRequestStatus(repositoryUrl, number, accessToken));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken)
        => Route(repositoryUrl, c => c.ListIssues(repositoryUrl, state, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        => Route(repositoryUrl, c => c.GetIssue(repositoryUrl, number, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubIssue?> FindIssueState(string repositoryUrl, int number, string accessToken)
        => Route(repositoryUrl, c => c.FindIssueState(repositoryUrl, number, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
        => Route(request.RepositoryUrl, c => c.CreateIssue(request));

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
        => Route(repositoryUrl, c => c.CommentIssue(repositoryUrl, number, body, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
        => Route(repositoryUrl, c => c.SetIssueState(repositoryUrl, number, state, accessToken));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
        => Route(repositoryUrl, c => c.ListPullRequests(repositoryUrl, state, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken)
        => Route(repositoryUrl, c => c.GetPullRequestDetail(repositoryUrl, number, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken)
        => Route(repositoryUrl, c => c.CommentPullRequest(repositoryUrl, number, body, accessToken));

    /// <inheritdoc />
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
        => Route(request.RepositoryUrl, c => c.MergePullRequest(request));
}

