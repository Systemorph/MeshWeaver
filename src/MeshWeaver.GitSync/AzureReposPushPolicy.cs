namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 Policy <c>azure-repos-push-only</c> (MeshWeaver#5248): an Azure Repos sync source is
/// <b>push-only</b> in this version — mesh → repo, nothing inbound. No import, no re-import at a
/// commit, no branch-state lookup, no webhook (service-hook) ingestion, no pull requests.
///
/// <para><b>How it is enforced.</b> An Azure Repos source must be declared
/// <see cref="SyncDirection.ExportOnly"/>; every inbound path in GitSync (webhook, branch
/// reconcile, sealed-publication reconciler, partition source tracking) already excludes
/// export-only sources, so the declaration alone keeps them out. The service ALSO refuses each
/// inbound operation on an Azure Repos URL by name, so a source declared in any other direction
/// is refused loudly rather than half-synced — never silently skipped.</para>
///
/// <para>Pure functions of the configuration, so the rule is pinned without a mesh.</para>
/// </summary>
public static class AzureReposPushPolicy
{
    /// <summary>
    /// The refusal for an EXPORT of <paramref name="config"/>, or null when the export may run.
    /// Only Azure Repos sources are judged here; every other provider answers null.
    /// </summary>
    public static string? RefuseExport(GitHubSyncConfig config)
    {
        if (!GitRepositoryProvider.IsAzureRepos(config.RepositoryUrl))
            return null;
        return config.Direction == SyncDirection.ExportOnly
            ? null
            : $"Azure Repos sync is push-only (mesh → repo) in this version: {config.RepositoryUrl} is declared " +
              $"'{config.Direction}'. Set this source's Sync direction to Export-only, then Sync. " +
              "Importing from Azure Repos is not supported yet (MeshWeaver#5248).";
    }

    /// <summary>
    /// The refusal for any INBOUND operation (import, re-import at a commit, branch-state or head
    /// lookup) against <paramref name="repositoryUrl"/>, or null when it is not an Azure Repos URL.
    /// </summary>
    /// <param name="repositoryUrl">The repository the operation would read from.</param>
    /// <param name="operation">What was asked, for the message (e.g. "Re-import").</param>
    public static string? RefuseInbound(string? repositoryUrl, string operation)
        => GitRepositoryProvider.IsAzureRepos(repositoryUrl)
            ? $"{operation} is not available for {repositoryUrl}: Azure Repos sync is push-only (mesh → repo) " +
              "in this version — nothing is read from the repository. Export with Sync now; importing from " +
              "Azure Repos is not supported yet (MeshWeaver#5248)."
            : null;
}
