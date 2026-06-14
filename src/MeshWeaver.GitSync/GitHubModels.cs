using System.Collections.Immutable;

namespace MeshWeaver.GitSync;

/// <summary>A single file in a repo tree — repo-relative <paramref name="Path"/> + UTF-8 text <paramref name="Content"/>.</summary>
public record RepoFile(string Path, string Content);

/// <summary>
/// A request to mirror a set of files into a GitHub repository as a single commit.
/// Mirror semantics: within <see cref="Subdirectory"/> the repo is made to match
/// <see cref="Files"/> exactly (added / updated / deleted); paths outside the
/// subdirectory are left untouched.
/// </summary>
public record GitHubPushRequest
{
    public required string RepositoryUrl { get; init; }
    public string Branch { get; init; } = "main";
    public string? Subdirectory { get; init; }
    public ImmutableList<RepoFile> Files { get; init; } = ImmutableList<RepoFile>.Empty;
    public required string CommitMessage { get; init; }
    public required string AuthorName { get; init; }
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

/// <summary>An OAuth token from a completed authorization-code exchange.</summary>
public record GitHubToken(
    string AccessToken,
    string? RefreshToken,
    string TokenType,
    string Scope,
    int? ExpiresInSeconds);
