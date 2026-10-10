namespace MeshWeaver.GitSync;

/// <summary>
/// The Azure DevOps identity an instance pushes to Azure Repos with, bound from the
/// <c>AzureDevOps</c> configuration section (MeshWeaver#5248).
///
/// <para>🚨 <b>DECLARED, not derived, and not a secret</b> (policy <c>azure-repos-push-only</c>,
/// <c>declared-not-derived</c>). Both values are identifiers of an app registration in the
/// <b>client's</b> Entra tenant, declared per client tenant in that instance's deployment record
/// (<c>AzureDevOps:TenantId</c>, <c>AzureDevOps:ClientId</c>). The credential itself is the
/// instance's workload identity, federated to that app registration — so nothing secret is stored
/// anywhere: no PAT, no client secret. Left unset, an Azure Repos export is refused by name.</para>
/// </summary>
public sealed record AzureDevOpsOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string ConfigSection = "AzureDevOps";

    /// <summary>
    /// The Entra scope of the Azure DevOps resource — the well-known application id
    /// <c>499b84ac-1321-427f-aa17-267ca6975798</c>, the same in every tenant.
    /// </summary>
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    /// <summary>The client's Entra tenant id (where the app registration lives).</summary>
    public string? TenantId { get; init; }

    /// <summary>The app registration's client (application) id in that tenant, which carries the
    /// federated credential for this instance's workload identity.</summary>
    public string? ClientId { get; init; }

    /// <summary>True when both identifiers are declared.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}
