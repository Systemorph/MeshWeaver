using System.Collections.Immutable;

namespace MeshWeaver.GitSync;

/// <summary>
/// A single file in a repo tree — repo-relative <paramref name="Path"/> plus its bytes. A TEXT file
/// carries its UTF-8 text in <paramref name="Content"/> (and <see cref="Binary"/> is null). A BINARY
/// file (a course video/poster committed under <c>{node}/content/**</c>, a font, any non-UTF-8 blob)
/// carries its raw bytes in <see cref="Binary"/> — <paramref name="Content"/> is then empty, because
/// round-tripping arbitrary bytes through a UTF-8 string corrupts them (the bug that repeatedly nuked
/// the course videos: the fetch decoded a base64 <c>.mp4</c> blob as UTF-8 and mangled it). Use
/// <see cref="Bytes"/> to read the raw content regardless of kind.
/// </summary>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Content">The file's UTF-8 text (empty for a binary file — read <see cref="Bytes"/>).</param>
/// <param name="Binary">The file's raw bytes when it is NOT valid UTF-8 text; null for a text file.</param>
public record RepoFile(string Path, string Content, byte[]? Binary = null)
{
    /// <summary>True when this file holds raw (non-text) bytes that must never pass through the text API.</summary>
    public bool IsBinary => Binary is not null;

    /// <summary>The file's raw bytes: <see cref="Binary"/> for a binary file, else the UTF-8 encoding of <see cref="Content"/>.</summary>
    public byte[] Bytes => Binary ?? System.Text.Encoding.UTF8.GetBytes(Content);
}

/// <summary>
/// A request to mirror a set of files into a GitHub repository as a single commit.
/// Mirror semantics: within <see cref="Subdirectory"/> the repo is made to match
/// <see cref="Files"/> exactly (added / updated / deleted); paths outside the
/// subdirectory are left untouched.
/// </summary>
public record GitHubPushRequest
{
    /// <summary>The target repository URL (e.g. <c>https://github.com/owner/repo</c>).</summary>
    public required string RepositoryUrl { get; init; }
    /// <summary>The branch to commit onto. Defaults to <c>main</c>.</summary>
    public string Branch { get; init; } = "main";
    /// <summary>The repo subdirectory the mirror is confined to; null/empty mirrors the whole repo.</summary>
    public string? Subdirectory { get; init; }
    /// <summary>The files to mirror into the repo (within <see cref="Subdirectory"/>) as the commit.</summary>
    public ImmutableList<RepoFile> Files { get; init; } = ImmutableList<RepoFile>.Empty;
    /// <summary>The commit message for the single mirror commit.</summary>
    public required string CommitMessage { get; init; }
    /// <summary>The display name to record as the commit author/committer.</summary>
    public required string AuthorName { get; init; }
    /// <summary>The email to record as the commit author/committer.</summary>
    public required string AuthorEmail { get; init; }
    /// <summary>The committing user's OAuth access token (decrypted).</summary>
    public required string AccessToken { get; init; }
    /// <summary>Create the repository as <b>private</b> if it does not yet exist.</summary>
    public bool CreatePrivateIfMissing { get; init; } = true;
    /// <summary>
    /// Create <see cref="Branch"/> if it does not exist yet (as a fresh snapshot
    /// commit). When false and the branch is missing, the push fails rather than
    /// silently creating a branch.
    /// </summary>
    public bool CreateBranchIfMissing { get; init; } = true;
}

/// <summary>Outcome of a <see cref="IGitHubRepoClient.Push"/>.</summary>
public record GitHubPushResult(
    string CommitSha,
    string RepositoryUrl,
    int FilesWritten,
    int FilesDeleted,
    bool RepoCreated);

/// <summary>
/// A LIVE answer to "which branch are we on and are we on its latest commit?" — asked from
/// GitHub, never stored. <see cref="HeadCommitSha"/> is the branch's current HEAD on GitHub;
/// <see cref="LastSyncedCommitSha"/> is the Space's last *sync action*; <see cref="UpToDate"/>
/// is true when they match.
/// </summary>
public record BranchState(string Branch, string HeadCommitSha, string? LastSyncedCommitSha, bool UpToDate);

/// <summary>An OAuth token from a completed authorization-code exchange.</summary>
public record GitHubToken(
    string AccessToken,
    string? RefreshToken,
    string TokenType,
    string Scope,
    int? ExpiresInSeconds);

/// <summary>A request to create a branch in a repository from an existing ref (branch or SHA).</summary>
public record GitHubCreateBranchRequest
{
    /// <summary>The repository URL the branch is created in.</summary>
    public required string RepositoryUrl { get; init; }
    /// <summary>The new branch's short name (e.g. <c>feature/x</c>) — no <c>refs/heads/</c> prefix.</summary>
    public required string NewBranch { get; init; }
    /// <summary>The ref to branch from — a branch name OR a commit SHA. Defaults to <c>main</c>.</summary>
    public string BaseRef { get; init; } = "main";
    /// <summary>The committing user's OAuth access token (decrypted).</summary>
    public required string AccessToken { get; init; }
}

/// <summary>Outcome of a <see cref="IGitHubRepoClient.CreateBranch"/> — the new branch + the SHA it points at.</summary>
public record GitHubBranchResult(string Branch, string CommitSha);

/// <summary>A request to open a pull request <see cref="HeadBranch"/> → <see cref="BaseBranch"/>.</summary>
public record GitHubOpenPullRequestRequest
{
    /// <summary>The repository URL the pull request is opened in.</summary>
    public required string RepositoryUrl { get; init; }
    /// <summary>The pull request title.</summary>
    public required string Title { get; init; }
    /// <summary>The pull request body (markdown); null for no description.</summary>
    public string? Body { get; init; }
    /// <summary>The branch the changes live on (the PR head).</summary>
    public required string HeadBranch { get; init; }
    /// <summary>The branch the PR targets (the PR base). Defaults to <c>main</c>.</summary>
    public string BaseBranch { get; init; } = "main";
    /// <summary>The committing user's OAuth access token (decrypted).</summary>
    public required string AccessToken { get; init; }
}

/// <summary>
/// A pull request's current state on GitHub — the shape both the open and status-sync
/// operations return. <see cref="Status"/> is the merged GitHub state (open/closed/merged),
/// mapped from the GitHub <c>state</c> + <c>merged</c> flags.
/// </summary>
public record GitHubPullRequestInfo(int Number, string Url, PullRequestStatus Status);
