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
    /// <see cref="MigrationRunOutcome.Failed"/> / <see cref="MigrationRunOutcome.TimedOut"/> —
    /// and, since #4764, on <see cref="MigrationRunOutcome.Forbidden"/> too.
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
    /// as the only net — and the poller says so at Warning AND on the policy node, as a roll
    /// qualified <c>UNMIGRATED</c> rather than a plain applied one (#4764). A default rather than an
    /// abstract member so the seam can land in core first without turning every dependent's build
    /// red — which is also why <c>NotSupported</c> is the one non-success outcome that still rolls:
    /// every implementation that has not adopted the member answers it.</para>
    /// </summary>
    /// <param name="versionTag">The platform version tag whose migration must run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How the migration ended; never throws for an outcome the poller can act on.</returns>
    Task<MigrationRunOutcome> RunMigrationAsync(string versionTag, CancellationToken ct) =>
        Task.FromResult(MigrationRunOutcome.NotSupported);

    /// <summary>
    /// 🚨 Restarts the portal workloads ON THE IMAGE THEY RUN — the activation of a landed module
    /// generation (#3650). A module never swaps inside a running process (restart-as-activation,
    /// <c>PendingRestart</c>); until this member existed that restart waited for the next
    /// UNRELATED platform roll, which could be days away, so "a module version ships" and "an
    /// installation uses it" were separated by whatever the image happened to do. The poller calls
    /// this when a check finds nothing newer to roll to but the module activation record says a
    /// restart is pending — paced by the same <c>MinRollInterval</c> floor as any other roll.
    ///
    /// <para>🚨 It is NOT <see cref="PatchToVersionAsync"/> with the installed tag. The Kubernetes
    /// implementation patches the container image with a strategic merge, and a patch whose pod
    /// template is unchanged rolls NOTHING — the last-rolled stamp sits on the Deployment's own
    /// metadata, outside the template, precisely so that it never triggers a rollout of its own.
    /// A restart needs a template-level change (what <c>kubectl rollout restart</c> does:
    /// <c>spec.template.metadata.annotations[kubectl.kubernetes.io/restartedAt]</c>), stamped
    /// together with the last-rolled annotation so the floor sees it as the roll it is.</para>
    ///
    /// <para>Returns <c>true</c> when a restart was issued; <c>false</c> when this updater cannot
    /// issue one — the default, so a host whose updater predates the seam (and the detect-only
    /// fallback) keeps building and reports the pending restart as a state an operator has to act
    /// on, never as a restart that happened. A default rather than an abstract member so the seam
    /// can land in core first without turning every dependent's build red; the Kubernetes updater
    /// in <c>MeshWeaver.SelfUpdate.Aks</c> (MeshWeaver.Plugins) implements it.</para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether a restart of the running image was issued.</returns>
    Task<bool> RestartAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Reads the PORTAL Deployment's rollout strategy (<c>spec.strategy</c> and <c>spec.replicas</c>) —
    /// what an instance reboot (<c>Doc/Architecture/InstanceReboot</c>) checks before it rolls, so a
    /// roll can never take the portal below its serving replicas (<see cref="RolloutStrategyReading.NonDisruptiveRefusal"/>).
    /// Null when this updater cannot read it — the default, so the seam lands in core first; a reboot
    /// then REFUSES to roll rather than rolling blind. The Kubernetes updater in
    /// <c>MeshWeaver.SelfUpdate.Aks</c> (MeshWeaver.Plugins) implements it.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<RolloutStrategyReading?> ReadRolloutStrategyAsync(CancellationToken ct) =>
        Task.FromResult<RolloutStrategyReading?>(null);
}

/// <summary>
/// The portal Deployment's rollout strategy as read from the cluster. <see cref="MaxSurge"/> and
/// <see cref="MaxUnavailable"/> are the raw IntOrString values (<c>1</c>, <c>0</c>, <c>25%</c>) or null
/// when unset (Kubernetes then applies 25% / 25%).
/// </summary>
/// <param name="Type">The strategy type: <c>RollingUpdate</c> or <c>Recreate</c> (null = Kubernetes' default, RollingUpdate).</param>
/// <param name="MaxSurge">The raw <c>rollingUpdate.maxSurge</c>.</param>
/// <param name="MaxUnavailable">The raw <c>rollingUpdate.maxUnavailable</c>.</param>
/// <param name="Replicas">The declared replica count (null = Kubernetes' default, 1).</param>
public sealed record RolloutStrategyReading(string? Type, string? MaxSurge, string? MaxUnavailable, int? Replicas)
{
    /// <summary>
    /// 🚨 Why a roll under <paramref name="reading"/> could take the portal below its serving replicas,
    /// or null when it cannot: the strategy must be a RollingUpdate whose <c>maxUnavailable</c> resolves to
    /// 0 and whose <c>maxSurge</c> resolves to at least 1 for the declared replica count (Kubernetes rounds
    /// a percentage DOWN for maxUnavailable and UP for maxSurge; an unset value is 25%). An unreadable
    /// strategy, or a value that cannot be resolved, is a refusal — never a pass. Pure.
    /// </summary>
    public static string? NonDisruptiveRefusal(RolloutStrategyReading? reading)
    {
        if (reading is null)
            return "the portal Deployment's rollout strategy could not be read (the updater cannot read it, or the read failed) — "
                   + "a roll that might take the portal below its serving replicas is not issued";
        if (string.Equals(reading.Type, "Recreate", StringComparison.OrdinalIgnoreCase))
            return "the portal Deployment's strategy is Recreate — every pod is deleted before a new one is Ready; "
                   + "set a RollingUpdate with maxSurge ≥ 1 and maxUnavailable 0";
        var replicas = reading.Replicas ?? 1;
        var unavailable = Resolve(reading.MaxUnavailable ?? "25%", replicas, roundUp: false);
        var surge = Resolve(reading.MaxSurge ?? "25%", replicas, roundUp: true);
        if (unavailable is null || surge is null)
            return $"the portal Deployment's rollout values could not be resolved (maxSurge '{reading.MaxSurge ?? "unset"}', "
                   + $"maxUnavailable '{reading.MaxUnavailable ?? "unset"}', replicas {replicas})";
        if (unavailable != 0)
            return $"the portal Deployment's maxUnavailable resolves to {unavailable} of {replicas} replica(s) "
                   + $"('{reading.MaxUnavailable ?? "unset = 25%"}') — a roll would remove serving pods before new ones are Ready; set maxUnavailable: 0";
        if (surge < 1)
            return $"the portal Deployment's maxSurge resolves to {surge} ('{reading.MaxSurge ?? "unset = 25%"}') — with maxUnavailable 0 "
                   + "the roll could never start a new pod; set maxSurge ≥ 1";
        return null;
    }

    private static int? Resolve(string raw, int replicas, bool roundUp)
    {
        var value = raw.Trim();
        if (value.EndsWith('%'))
            return int.TryParse(value[..^1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent >= 0
                ? (int)(roundUp ? Math.Ceiling(replicas * percent / 100.0) : Math.Floor(replicas * percent / 100.0))
                : null;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var absolute) && absolute >= 0
            ? absolute
            : null;
    }
}

/// <summary>
/// How <see cref="IDeploymentUpdater.RunMigrationAsync"/> ended. The poller's rule: only
/// <see cref="Completed"/> proves the schema moved; <see cref="Failed"/> and <see cref="TimedOut"/>
/// prove it did NOT and refuse the roll; <see cref="Forbidden"/> proves nothing either way and also
/// refuses, because the <c>helm upgrade</c> that grants the missing permission runs the migration
/// itself (#4764); <see cref="NotSupported"/> alone still rolls — an install that can NEVER migrate
/// would otherwise freeze for ever (#2553) — and is recorded as an <c>UNMIGRATED</c> roll.
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
    ///
    /// <para>🚨 This REFUSES the roll (#4764). It reads like the other "could not even try" state
    /// and is not one: the remedy is a <c>helm upgrade</c> an operator already owes this install, and
    /// that upgrade renders the migration Job itself — so refusing asks for nothing new, while
    /// patching asks <c>DbVersionGate</c> to veto the new pods three seconds into their boot behind
    /// an old ReplicaSet that keeps answering 200, which nothing in the process can undo.</para>
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
