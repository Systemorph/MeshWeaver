namespace MeshWeaver.GitSync;

/// <summary>
/// Options for on-disk git working trees — the per-user checkout root.
///
/// <para>In the portal this is the RWX <c>memex-workspace</c> PVC mount (the host sets
/// <c>GitWorkspace:Root=/workspace</c>); in dev / tests it defaults to a temp subdir so a
/// working tree is never written outside an isolated location. Per-user trees live at
/// <c>{Root}/{userId}/{repoSlug}</c> — the same per-user partitioning shape as the
/// <c>memex-users</c> volume, so one user's checkout can never read or clobber another's.</para>
/// </summary>
public sealed class GitWorkingTreeOptions
{
    /// <summary>Root directory under which per-user working trees live: <c>{Root}/{userId}/{repoSlug}</c>.</summary>
    public string Root { get; set; } = Path.Combine(Path.GetTempPath(), "meshweaver-workspace");
}
