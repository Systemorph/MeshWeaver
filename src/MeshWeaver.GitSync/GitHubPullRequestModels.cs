namespace MeshWeaver.GitSync;

/// <summary>
/// A compact pull-request row for the "all pull requests" list. Read LIVE from GitHub —
/// never persisted (a stored copy would drift). One row per open/closed PR in the repo.
/// </summary>
public record GitHubPullRequestSummary(
    int Number,
    string Title,
    string? AuthorLogin,
    PullRequestStatus Status,
    bool Draft,
    string? HeadBranch,
    string? BaseBranch,
    string Url,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>The rolled-up state of a pull request's CI checks.</summary>
public enum GitHubCheckState
{
    /// <summary>No checks are configured / reported for the head commit.</summary>
    None,

    /// <summary>At least one check is still queued or in progress (and none have failed).</summary>
    Pending,

    /// <summary>Every reported check concluded successfully (or neutral / skipped).</summary>
    Passed,

    /// <summary>At least one check failed (failure / timed-out / cancelled / action-required).</summary>
    Failed,
}

/// <summary>A roll-up of the head commit's CI check runs.</summary>
public record GitHubCheckSummary(int Total, int Passed, int Failed, int Pending, GitHubCheckState Overall)
{
    /// <summary>An empty summary (no checks reported).</summary>
    public static GitHubCheckSummary Empty { get; } = new(0, 0, 0, 0, GitHubCheckState.None);
}

/// <summary>A roll-up of a pull request's reviews (latest decision per reviewer).</summary>
public record GitHubReviewSummary(int Approved, int ChangesRequested, int Commented)
{
    /// <summary>An empty summary (no reviews).</summary>
    public static GitHubReviewSummary Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// The LIVE, delegated detail of a single pull request — number, branches, mergeability, a
/// checks roll-up and a reviews roll-up. Read on demand from GitHub, never stored.
/// </summary>
public record GitHubPullRequestDetail(
    int Number,
    string Title,
    string? Body,
    string? AuthorLogin,
    PullRequestStatus Status,
    bool Draft,
    string? HeadBranch,
    string? BaseBranch,
    string? HeadSha,
    string Url,
    bool? Mergeable,
    string? MergeableState,
    int CommentsCount,
    GitHubCheckSummary Checks,
    GitHubReviewSummary Reviews);

/// <summary>The GitHub merge strategy for closing a pull request.</summary>
public enum GitHubMergeMethod
{
    /// <summary>Create a merge commit (default).</summary>
    Merge,

    /// <summary>Squash the PR commits into one, then merge.</summary>
    Squash,

    /// <summary>Rebase the PR commits onto the base, then merge.</summary>
    Rebase,
}

/// <summary>A request to merge an open pull request on GitHub.</summary>
public record GitHubMergePullRequestRequest
{
    /// <summary>The repository URL the pull request belongs to.</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>The pull request number to merge.</summary>
    public required int Number { get; init; }

    /// <summary>The merge strategy. Defaults to a merge commit.</summary>
    public GitHubMergeMethod Method { get; init; } = GitHubMergeMethod.Merge;

    /// <summary>Optional merge-commit title; null lets GitHub pick the default.</summary>
    public string? CommitTitle { get; init; }

    /// <summary>Optional merge-commit message body; null lets GitHub pick the default.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>The committing user's OAuth access token (decrypted).</summary>
    public required string AccessToken { get; init; }
}

/// <summary>The outcome of a merge attempt.</summary>
public record GitHubMergeResult(bool Merged, string? Sha, string? Message);
