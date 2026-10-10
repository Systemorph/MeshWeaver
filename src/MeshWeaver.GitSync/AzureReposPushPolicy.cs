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
/// <para><b>Order.</b> <see cref="ExportGuard"/> and <see cref="ReimportGuard"/> are the guard
/// sequences <see cref="GitHubSyncService"/> runs: the provider rule FIRST, then the generic
/// direction rule — so an Azure Repos source always gets the push-only refusal, whatever direction
/// it is declared in. Pure functions of the configuration, so the order is pinned without a mesh.</para>
///
/// <para>Every Azure Repos refusal is a <see cref="GitSyncRefusal"/> (a catalog key plus named
/// arguments), so it renders in the viewer's language. The two GENERIC direction refusals keep their
/// shipped contract — a plain <see cref="InvalidOperationException"/> with the exact English message
/// (<see cref="GitSyncRefusal.Legacy"/>) — because GitHub-path callers and the dependent suites
/// assert on that type and text; this change must leave the GitHub path exactly as it was.</para>
/// </summary>
public static class AzureReposPushPolicy
{
    /// <summary>The inbound operations the policy refuses — an open vocabulary of string constants;
    /// each value names the catalog key <c>gitsync.azure.inbound.{value}</c>.</summary>
    public static class InboundOperation
    {
        /// <summary>Import a repository into a new Space.</summary>
        public const string Import = "import";
        /// <summary>Re-import a Space at a commit / update to latest.</summary>
        public const string Reimport = "reimport";
        /// <summary>Check the branch state ("are we on the latest?").</summary>
        public const string CheckBranch = "checkBranch";
        /// <summary>Resolve a branch head (the branch reconcile).</summary>
        public const string BranchHead = "branchHead";
        /// <summary>Any pull-request operation.</summary>
        public const string PullRequest = "pullRequest";
    }

    /// <summary>
    /// The refusal for an EXPORT of <paramref name="config"/> under this policy, or null when the
    /// export may run. Only Azure Repos sources are judged here; every other provider answers null.
    /// </summary>
    public static GitSyncRefusal? RefuseExport(GitHubSyncConfig config)
    {
        if (!GitRepositoryProvider.IsAzureRepos(config.RepositoryUrl))
            return null;
        return config.Direction == SyncDirection.ExportOnly
            ? null
            : GitSyncRefusal.Of("gitsync.azure.exportNotExportOnly",
                ("url", config.RepositoryUrl!), ("direction", config.Direction.ToString()));
    }

    /// <summary>
    /// The refusal for an INBOUND <paramref name="operation"/> (an <see cref="InboundOperation"/>
    /// value) against <paramref name="repositoryUrl"/>, or null when it is not an Azure Repos URL.
    /// </summary>
    public static GitSyncRefusal? RefuseInbound(string? repositoryUrl, string operation)
        => GitRepositoryProvider.IsAzureRepos(repositoryUrl)
            ? GitSyncRefusal.Of("gitsync.azure.inbound." + operation, ("url", repositoryUrl!))
            : null;

    /// <summary>
    /// The EXPORT guard sequence <see cref="GitHubSyncService.SyncToGitHub"/> runs, in order:
    /// the Azure Repos push-only rule, then the generic import-only rule.
    /// </summary>
    public static GitSyncRefusal? ExportGuard(GitHubSyncConfig config)
        => RefuseExport(config)
           ?? (config.Direction == SyncDirection.ImportOnly
               ? GitSyncRefusal.Legacy("gitsync.direction.importOnlyNoExport",
                   $"This sync source is import-only (repo → mesh): exporting to {config.RepositoryUrl} is not allowed. " +
                   "Change the source's Sync direction to Bidirectional or Export-only to commit.")
               : null);

    /// <summary>
    /// The RE-IMPORT guard sequence <see cref="GitHubSyncService.ReimportAtCommit"/> runs, in order:
    /// the Azure Repos push-only rule, then the generic export-only rule.
    /// </summary>
    public static GitSyncRefusal? ReimportGuard(GitHubSyncConfig config)
        => RefuseInbound(config.RepositoryUrl, InboundOperation.Reimport)
           ?? (config.Direction == SyncDirection.ExportOnly
               ? GitSyncRefusal.Legacy("gitsync.direction.exportOnlyNoImport",
                   $"This sync source is export-only (mesh → repo): importing from {config.RepositoryUrl} is not allowed. " +
                   "Change the source's Sync direction to Bidirectional or Import-only to re-import.")
               : null);
}
