namespace MeshWeaver.GitSync;

/// <summary>
/// RETIRED with policy <c>azure-repos-push-only</c>, superseded by <c>azure-repos-two-way</c>
/// (MeshWeaver#6500). Kept as a FORWARDER because it is public surface: a module compiled against an
/// earlier platform binds <c>MeshWeaver.GitSync!MeshWeaver.GitSync.AzureReposPushPolicy</c> by name,
/// and in-mesh source may name it — neither is seen by any build here. Every member keeps its shipped
/// signature and answers what the CURRENT rule says: an Azure Repos source syncs in every direction,
/// so the push-only refusals answer null, and only pull requests are still refused. New code uses
/// <see cref="AzureReposSyncPolicy"/>.
/// </summary>
[Obsolete("Policy azure-repos-push-only is superseded by azure-repos-two-way: use AzureReposSyncPolicy.")]
public static class AzureReposPushPolicy
{
    /// <summary>The inbound operations the retired policy refused. Only <see cref="PullRequest"/> is
    /// still refused (<see cref="AzureReposSyncPolicy.Unsupported.PullRequest"/>).</summary>
    public static class InboundOperation
    {
        /// <summary>Import a repository into a new Space — allowed under <c>azure-repos-two-way</c>.</summary>
        public const string Import = "import";
        /// <summary>Re-import a Space at a commit — allowed under <c>azure-repos-two-way</c>.</summary>
        public const string Reimport = "reimport";
        /// <summary>Check the branch state — allowed under <c>azure-repos-two-way</c>.</summary>
        public const string CheckBranch = "checkBranch";
        /// <summary>Resolve a branch head — allowed under <c>azure-repos-two-way</c>.</summary>
        public const string BranchHead = "branchHead";
        /// <summary>Any pull-request operation — still refused on Azure Repos.</summary>
        public const string PullRequest = AzureReposSyncPolicy.Unsupported.PullRequest;
    }

    /// <summary>Null: an Azure Repos export is no longer bound to the Export-only direction. The
    /// direction rule every provider shares is <see cref="ExportGuard"/>.</summary>
    public static GitSyncRefusal? RefuseExport(GitHubSyncConfig config) => null;

    /// <summary>The refusal for <paramref name="operation"/> on <paramref name="repositoryUrl"/>:
    /// a pull request on Azure Repos (<see cref="AzureReposSyncPolicy.Refuse"/>); null for every
    /// other operation, which <c>azure-repos-two-way</c> allows.</summary>
    public static GitSyncRefusal? RefuseInbound(string? repositoryUrl, string operation)
        => operation == InboundOperation.PullRequest
            ? AzureReposSyncPolicy.Refuse(repositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest)
            : null;

    /// <summary>Forwards to <see cref="AzureReposSyncPolicy.ExportGuard"/>.</summary>
    public static GitSyncRefusal? ExportGuard(GitHubSyncConfig config) => AzureReposSyncPolicy.ExportGuard(config);

    /// <summary>Forwards to <see cref="AzureReposSyncPolicy.ReimportGuard"/>.</summary>
    public static GitSyncRefusal? ReimportGuard(GitHubSyncConfig config) => AzureReposSyncPolicy.ReimportGuard(config);
}
