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

    /// <summary>
    /// When self-update last rolled THIS install, or null when it never has (or cannot tell).
    ///
    /// <para>🚨 This must be state that SURVIVES A RESTART, because a successful roll restarts the
    /// process: an in-memory "last rolled at" is always empty exactly when it is needed, so a floor
    /// built on it would never hold. The Kubernetes implementation stamps an annotation on the
    /// Deployment it patches and reads that back.</para>
    ///
    /// <para>🚨 And it must NOT be process uptime, which is the tempting third option and is wrong
    /// for crash recovery: a pod that comes back on an OLD image has a young process but an old
    /// deployment, and uptime would make it wait out a floor it has long since satisfied. The
    /// annotation gives the right answer there — old stamp, floor elapsed, roll immediately.</para>
    /// </summary>
    Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct);

    /// <summary>
    /// 🚨 Moves the SCHEMA before the image moves: runs the database migration for
    /// <paramref name="versionTag"/> to completion — on Kubernetes, a run-once Job
    /// (<c>memex-migration-su-&lt;tag&gt;</c>) built from the same ConfigMap and Secret the chart's
    /// <c>helm upgrade</c> Job uses — and reports how it ended. The poller calls this BEFORE
    /// <see cref="PatchToVersionAsync"/> on every roll and refuses the roll on
    /// <see cref="MigrationRunOutcome.Failed"/> / <see cref="MigrationRunOutcome.TimedOut"/>.
    ///
    /// <para><b>Why it exists (2026-09-03).</b> The migration is a Job that only <c>helm upgrade</c>
    /// minted, named by release revision; a self-update patches the portal image with
    /// <c>kubectl set image</c> and could never mint one. So the first automatic roll across a
    /// <c>db_version</c> boundary (Plugins #1216, V55) rolled both AKS portals to a build whose pods
    /// refused to start (<c>DbVersionGate</c>) while the old ReplicaSet kept answering 200 — memex
    /// for seven hours, memex-cloud while its old pods ran the very fan-out storm the new build
    /// fixed. <c>Doc/Architecture/DatabaseMigrationProcedure</c> is the routine.</para>
    ///
    /// <para>Default implementation answers <see cref="MigrationRunOutcome.NotSupported"/>: a host
    /// whose updater predates this member rolls exactly as it did before, with <c>DbVersionGate</c>
    /// as the only net — and the poller says so at Warning. A default rather than an abstract member
    /// so the seam can land in core first without turning every dependent's build red.</para>
    /// </summary>
    /// <param name="versionTag">The platform version tag whose migration must run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How the migration ended; never throws for an outcome the poller can act on.</returns>
    Task<MigrationRunOutcome> RunMigrationAsync(string versionTag, CancellationToken ct) =>
        Task.FromResult(MigrationRunOutcome.NotSupported);
}

/// <summary>
/// How <see cref="IDeploymentUpdater.RunMigrationAsync"/> ended. The poller's rule: only
/// <see cref="Completed"/> proves the schema moved; <see cref="Failed"/> and <see cref="TimedOut"/>
/// prove it did NOT and refuse the roll; <see cref="NotSupported"/> and <see cref="Forbidden"/> are
/// the two "could not even try" states, which roll as before — loudly.
/// </summary>
public enum MigrationRunOutcome
{
    /// <summary>The migration ran to completion (<c>Database migration completed. Version: N</c>).</summary>
    Completed,

    /// <summary>The migration ran and failed (the Job's pods exhausted their backoff).</summary>
    Failed,

    /// <summary>The migration did not complete within <see cref="SelfUpdateOptions.MigrationJobTimeout"/> — stuck, not slow.</summary>
    TimedOut,

    /// <summary>This updater cannot run a migration at all (a host predating the seam, or no Kubernetes).</summary>
    NotSupported,

    /// <summary>
    /// The cluster refused to create the Job (403): the portal's service account has not been
    /// granted <c>batch/jobs</c> — the chart's <c>memex-portal/rbac.yaml</c> grants it, and takes
    /// effect on the next <c>helm upgrade</c>.
    /// </summary>
    Forbidden,
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
