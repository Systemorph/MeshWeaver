using System.ComponentModel;

namespace MeshWeaver.GitSync;

/// <summary>
/// Space-level GitHub sync configuration, stored as a MeshNode at
/// <c>{spaceId}/_GitSync</c>. Holds the target repository the Space exports to —
/// NOT a secret (the per-user OAuth credential lives separately at
/// <c>{userId}/_Provider/GitHub</c> and is never serialized into exported content).
/// Editable by Space admins.
///
/// <para>This record IS the editor: the GitHub Sync settings tab renders it through the
/// standard mesh-node editor (the same data-bound, <c>stream.Update</c>-persisting editor every
/// node uses), so these attributes drive the generated controls. The last-sync fields are written
/// by the sync operation and shown read-only — <see cref="BrowsableAttribute"/> hides them from
/// the editable form.</para>
/// </summary>
public record GitHubSyncConfig
{
    /// <summary>The target repository URL, e.g. <c>https://github.com/owner/repo</c>.</summary>
    [Description("Repository URL")]
    public string? RepositoryUrl { get; init; }

    /// <summary>The branch to commit to. Defaults to <c>main</c>.</summary>
    [Description("Branch")]
    public string Branch { get; init; } = "main";

    /// <summary>
    /// Optional path prefix inside the repo to mirror the Space subtree into
    /// (e.g. <c>content</c>). Empty → the repository root. Mirror semantics apply
    /// only within this subdirectory; files elsewhere in the repo are untouched.
    /// </summary>
    [Description("Subdirectory (optional — blank = repository root)")]
    public string? Subdirectory { get; init; }

    /// <summary>Create <see cref="Branch"/> if it does not exist yet. Default true.</summary>
    [Description("Create the branch if it doesn't exist")]
    public bool CreateBranchIfMissing { get; init; } = true;

    /// <summary>Create the repository (private) if it does not exist yet. Default true.</summary>
    [Description("Create the repository (private) if it doesn't exist")]
    public bool CreateRepoIfMissing { get; init; } = true;

    /// <summary>When the last successful export completed. Set by the sync operation; not user-editable.</summary>
    [Browsable(false)]
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>The commit SHA produced by the last successful export. Set by the sync operation; not user-editable.</summary>
    [Browsable(false)]
    public string? LastSyncCommitSha { get; init; }
}
