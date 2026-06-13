namespace MeshWeaver.GitSync;

/// <summary>
/// Space-level GitHub sync configuration, stored as a MeshNode at
/// <c>{spaceId}/_GitSync</c>. Holds the target repository the Space exports to —
/// NOT a secret (the per-user OAuth credential lives separately at
/// <c>{userId}/_Provider/GitHub</c> and is never serialized into exported content).
/// Editable by Space admins.
/// </summary>
public record GitHubSyncConfig
{
    /// <summary>The target repository URL, e.g. <c>https://github.com/owner/repo</c>.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>The branch to commit to. Defaults to <c>main</c>.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>Create <see cref="Branch"/> if it does not exist yet. Default true.</summary>
    public bool CreateBranchIfMissing { get; init; } = true;

    /// <summary>Create the repository (private) if it does not exist yet. Default true.</summary>
    public bool CreateRepoIfMissing { get; init; } = true;

    /// <summary>
    /// Optional path prefix inside the repo to mirror the Space subtree into
    /// (e.g. <c>content</c>). Empty → the repository root. Mirror semantics apply
    /// only within this subdirectory; files elsewhere in the repo are untouched.
    /// </summary>
    public string? Subdirectory { get; init; }

    /// <summary>When the last successful export completed.</summary>
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>The commit SHA produced by the last successful export.</summary>
    public string? LastSyncCommitSha { get; init; }
}
