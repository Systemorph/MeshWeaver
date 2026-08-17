using System.Runtime.Versioning;

namespace MeshWeaver.Hosting.SelfUpdate;

// Split from the original file when the AKS/ACR implementations moved to the
// MeshWeaver.SelfUpdate.Aks module: the SEAM stays with the poller that consumes it.

/// <summary>Applies a platform update on the running install. The single k8s/IO leaf — its sole
/// caller wraps <see cref="PatchToVersionAsync"/> in <c>IIoPool.Invoke</c>. An injectable seam so
/// tests substitute a fake.</summary>
public interface IDeploymentUpdater
{
    /// <summary>Whether this install can patch its own workloads (i.e. it runs in Kubernetes with a
    /// projected service-account token). When false the install is detect-and-notify only.</summary>
    bool CanPatch { get; }

    /// <summary>Rolls the portal AND migration Deployments to <paramref name="versionTag"/> (they
    /// share the platform version) by patching their container images; Kubernetes then performs the
    /// rolling update. Patching the migration alongside the portal is how the database schema /
    /// <c>db_version</c> stays in step — the meaningful, safe "auto-update Postgres".</summary>
    Task PatchToVersionAsync(string versionTag, CancellationToken ct);
}

/// <summary>Probes the deployment target so the k8s-patch path only arms where it can actually
/// succeed.</summary>
public static class HostingTarget
{
    private const string TokenFile = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>True when running inside Kubernetes (AKS or local k3s): a projected service-account
    /// token is mounted AND the API-server env is present. Outside k8s (monolith / MAUI host) the
    /// self-updater falls back to detect-and-notify.</summary>
    public static bool IsKubernetes() =>
        File.Exists(TokenFile)
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
}
