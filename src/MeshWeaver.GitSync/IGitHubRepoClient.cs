using System.Reactive.Linq;

namespace MeshWeaver.GitSync;

/// <summary>
/// The seam over the GitHub repository API used by <see cref="GitHubSyncService"/>.
/// The production implementation (<see cref="OctokitGitHubRepoClient"/>) talks to
/// GitHub via the Octokit Git Data API with every call routed through
/// <see cref="MeshWeaver.Mesh.Threading.IIoPool"/>; tests substitute an in-memory
/// fake so the full export/import loop runs offline and deterministically.
///
/// <para>Every method returns a cold <see cref="IObservable{T}"/> — the work runs on
/// Subscribe, never on call. No <c>async</c>/<c>await</c>/<c>Task</c> escapes this
/// boundary: the implementation bridges every Octokit <c>…Async</c> leaf through
/// the I/O pool.</para>
/// </summary>
public interface IGitHubRepoClient
{
    /// <summary>
    /// Mirrors <see cref="GitHubPushRequest.Files"/> into the repository as a single
    /// commit (blobs → tree → commit → update ref), creating the repo private if
    /// missing. Emits the resulting commit SHA. This IS the "commit" operation —
    /// a sync is a commit.
    /// </summary>
    IObservable<GitHubPushResult> Push(GitHubPushRequest request);

    /// <summary>
    /// Reads every file under <paramref name="subdirectory"/> at the given
    /// <paramref name="commitish"/> (a branch name OR a commit SHA) — commit →
    /// recursive tree → blob per file — and emits them as text, along with the
    /// resolved commit SHA. Used by both the import-into-a-new-Space flow, the
    /// "re-import at a chosen commit" flow, and "update to latest" (re-fetch the
    /// branch HEAD).
    /// </summary>
    IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken);

    /// <summary>
    /// Filtered fetch: like <see cref="Fetch(string, string, string?, string)"/> but downloads ONLY
    /// the blobs whose subdirectory-relative path satisfies <paramref name="pathFilter"/> — e.g.
    /// every package's <c>*/index.json</c> manifest without pulling the rest of the repo. The
    /// default implementation fetches everything and filters afterwards (correct on any
    /// implementation); <see cref="OctokitGitHubRepoClient"/> overrides it to filter the tree
    /// BEFORE the per-blob reads, so a manifest scan of a large repo costs a handful of calls.
    /// </summary>
    IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
    {
        ArgumentNullException.ThrowIfNull(pathFilter);
        return Fetch(repositoryUrl, commitish, subdirectory, accessToken)
            .Select(snapshot => new RepoSnapshot(
                snapshot.CommitSha,
                snapshot.Files.Where(f => pathFilter(f.Path)).ToArray()));
    }

    /// <summary>
    /// The commit SHA a commitish currently resolves to — ONE cheap API call, no tree read, no blob
    /// reads. This is the git-history change counter: poll it and re-fetch only when it moved past
    /// the SHA you already processed (deletions included — the snapshot at the new SHA is the full
    /// truth). The default implementation falls back to a full fetch's SHA (correct, not cheap);
    /// <see cref="OctokitGitHubRepoClient"/> overrides it with the single ref/commit lookup.
    /// </summary>
    IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => Fetch(repositoryUrl, commitish, null, accessToken).Select(snapshot => snapshot.CommitSha);

    /// <summary>
    /// Creates <see cref="GitHubCreateBranchRequest.NewBranch"/> from
    /// <see cref="GitHubCreateBranchRequest.BaseRef"/> (resolving the base ref to its
    /// commit SHA first), and emits the new branch + the SHA it points at. Surfaces a
    /// clear error if the base ref does not exist.
    /// </summary>
    IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request);

    /// <summary>
    /// Opens a pull request <see cref="GitHubOpenPullRequestRequest.HeadBranch"/> →
    /// <see cref="GitHubOpenPullRequestRequest.BaseBranch"/> with the given title + body,
    /// and emits its number, html_url, and state.
    /// </summary>
    IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request);

    /// <summary>
    /// Reads the current state of pull request <paramref name="number"/> from GitHub
    /// (open / closed / merged) — for status sync onto the PullRequest node.
    /// </summary>
    IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken);

    // ── Issues ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the repository's issues (pull requests excluded — GitHub returns PRs from the
    /// issues endpoint), optionally filtered to <paramref name="state"/> (null = all states).
    /// One <see cref="GitHubIssue"/> per issue, WITHOUT comments (a list read).
    /// </summary>
    IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken);

    /// <summary>
    /// Reads a single issue plus its comments — the detailed read used to hydrate the
    /// <c>{spacePath}/_Issue/{number}</c> node's full content.
    /// </summary>
    IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken);

    /// <summary>Opens a new issue on GitHub and emits the created issue (with its assigned number).</summary>
    IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request);

    /// <summary>Posts a comment on issue <paramref name="number"/> and emits the created comment.</summary>
    IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken);

    // ── Pull requests (richer) ────────────────────────────────────────────────

    /// <summary>
    /// Lists the repository's pull requests, optionally filtered to <paramref name="state"/>
    /// (null = all states). One compact <see cref="GitHubPullRequestSummary"/> per PR.
    /// </summary>
    IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken);

    /// <summary>
    /// Reads a pull request's live detail — mergeability, a CI-checks roll-up (over the head
    /// commit) and a reviews roll-up (latest decision per reviewer). Delegated, never stored.
    /// </summary>
    IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken);

    /// <summary>Posts a comment on pull request <paramref name="number"/> (a PR is an issue) and emits it.</summary>
    IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken);

    /// <summary>Merges an open pull request with the requested strategy; emits the merge outcome.</summary>
    IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request);
}

/// <summary>A point-in-time snapshot of a repo subtree — the resolved commit SHA + its files.</summary>
public record RepoSnapshot(string CommitSha, IReadOnlyList<RepoFile> Files);
