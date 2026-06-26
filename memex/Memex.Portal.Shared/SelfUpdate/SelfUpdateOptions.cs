namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Configuration for the self-update poller. Defaults target the standard AKS / local-k3s topology
/// (ACR <c>meshweaver.azurecr.io</c>, the portal + migration deployments rolled together). Override
/// per environment via the <c>SelfUpdate:*</c> configuration section.
/// </summary>
public record SelfUpdateOptions
{
    /// <summary>The container registry login server the running install pulls from and polls.</summary>
    public string Registry { get; init; } = "meshweaver.azurecr.io";

    /// <summary>Repository whose tags are the platform version source of truth (portal + migration
    /// share the same version, built together).</summary>
    public string PortalRepository { get; init; } = "memex-portal-ai";

    /// <summary>Migration image repository (rolled to the same tag as the portal — this is how the
    /// database schema / <c>db_version</c> stays in step, the meaningful "auto-update Postgres").</summary>
    public string MigrationRepository { get; init; } = "memex-migration";

    /// <summary>The portal Deployment name patched on AKS/k3s.</summary>
    public string PortalDeployment { get; init; } = "memex-portal-deployment";

    /// <summary>The portal container name within <see cref="PortalDeployment"/>.</summary>
    public string PortalContainer { get; init; } = "memex-portal";

    /// <summary>The migration Deployment name patched (rolled together with the portal).</summary>
    public string MigrationDeployment { get; init; } = "memex-migration-deployment";

    /// <summary>The migration container name within <see cref="MigrationDeployment"/>.</summary>
    public string MigrationContainer { get; init; } = "memex-migration";

    /// <summary>How often the running install polls the registry ("a few times a day").</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The policy seeded onto <c>Admin/UpdatePolicy</c> when it doesn't exist yet, and the
    /// fallback used before the policy node's first live emission.</summary>
    public UpdatePolicyKind DefaultPolicy { get; init; } = UpdatePolicyKind.Continuous;

    /// <summary>The full image reference for a portal version tag.</summary>
    public string PortalImage(string tag) => $"{Registry}/{PortalRepository}:{tag}";

    /// <summary>The full image reference for a migration version tag.</summary>
    public string MigrationImage(string tag) => $"{Registry}/{MigrationRepository}:{tag}";
}
