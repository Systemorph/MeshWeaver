namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 Policy <c>azure-repos-two-way</c> (MeshWeaver#6500, superseding <c>azure-repos-push-only</c>):
/// an Azure Repos sync source syncs exactly like a GitHub one — import, export, re-import at a
/// commit, update to latest, check branch, and the two-way baseline rules — in every
/// <see cref="SyncDirection"/>. The provider adds NO rule of its own to the sync: the guard
/// sequences below are the generic direction rules, the same for every provider, and the parity
/// suite (<c>AzureReposGitHubParityTest</c>) runs the shared scenarios against both.
///
/// <para><b>What Azure Repos does not offer in this version</b> is refused by name, never silently
/// skipped: pull requests and issues (<see cref="Unsupported"/>, <see cref="Refuse"/>). Inbound sync
/// is ON DEMAND — the GitHub push webhook and the branch reconcile are keyed to a GitHub
/// <c>owner/repo</c> identity, which an Azure Repos URL never resolves to
/// (<see cref="GitHubRepoIdentityResolver.Parse"/>), so neither reaches an Azure Repos source.</para>
///
/// <para>Every Azure Repos refusal is a <see cref="GitSyncRefusal"/> (a catalog key plus named
/// arguments), so it renders in the viewer's language. The two direction refusals keep their
/// shipped contract — a plain <see cref="InvalidOperationException"/> with the exact English message
/// (<see cref="GitSyncRefusal.Legacy"/>) — because GitHub-path callers and the dependent suites
/// assert on that type and text.</para>
/// </summary>
public static class AzureReposSyncPolicy
{
    /// <summary>The operations Azure Repos does not offer — an open vocabulary of string constants;
    /// each value names the catalog key <c>gitsync.azure.unsupported.{value}</c>.</summary>
    public static class Unsupported
    {
        /// <summary>Any pull-request operation.</summary>
        public const string PullRequest = GitHostingOperation.PullRequest;
        /// <summary>Any issue operation (Azure Repos has no issues; work items are not GitSync's).</summary>
        public const string Issues = GitHostingOperation.Issues;
    }

    /// <summary>
    /// The refusal for <paramref name="operation"/> (an <see cref="Unsupported"/> value) against
    /// <paramref name="repositoryUrl"/>, or null when it is not an Azure Repos URL.
    /// </summary>
    public static GitSyncRefusal? Refuse(string? repositoryUrl, string operation)
        => GitRepositoryProvider.IsAzureRepos(repositoryUrl)
            ? GitSyncRefusal.Of("gitsync.azure.unsupported." + operation, ("url", repositoryUrl!))
            : null;

    /// <summary>Forwards to <see cref="SyncDirectionGuards.ExportGuard"/> — the direction rule
    /// every provider shares.</summary>
    public static GitSyncRefusal? ExportGuard(GitHubSyncConfig config) => SyncDirectionGuards.ExportGuard(config);

    /// <summary>Forwards to <see cref="SyncDirectionGuards.ReimportGuard"/> — the direction rule
    /// every provider shares.</summary>
    public static GitSyncRefusal? ReimportGuard(GitHubSyncConfig config) => SyncDirectionGuards.ReimportGuard(config);
}
