namespace MeshWeaver.Hosting.SelfUpdate;

/// <summary>
/// Configuration for the self-update poller. Defaults target the standard AKS / local-k3s topology
/// (ACR <c>meshweaver.azurecr.io</c>, the portal + migration deployments rolled together). Override
/// per environment via the <c>SelfUpdate:*</c> configuration section.
/// </summary>
public record SelfUpdateOptions
{
    /// <summary>Configuration section this binds from (e.g. <c>SelfUpdate__RetryInterval</c>).</summary>
    public const string SectionName = "SelfUpdate";

    /// <summary>
    /// The container registry login server the running install pulls from and polls.
    ///
    /// <para>Two kinds of host are understood, told apart by <see cref="RegistryIsAzureContainerRegistry"/>:
    /// an Azure Container Registry (<c>*.azurecr.io</c>, the default — tags are listed through
    /// ACR's own <c>/acr/v1/{repo}/_tags</c> with Workload Identity), and ANY OTHER host, which is
    /// read as an OCI Distribution registry — in this fleet the read-through mirror another
    /// installation serves at <c>{portal}/v2</c> (#3353, <c>Doc/Architecture/ContainerRegistryInMemex</c>),
    /// authenticated with this installation's own plugin-registry instance key. The chart renders
    /// it from <c>selfUpdate.registry</c>; the images the updater rolls to
    /// (<see cref="PortalImage"/>, <see cref="MigrationImage"/>) are named on this host, so a
    /// mirror-consuming installation must ALSO carry the pull secret the chart's
    /// <c>portal.imagePullSecret</c> declares, or the roll names an image its kubelet cannot pull.</para>
    /// </summary>
    public string Registry { get; init; } = "meshweaver.azurecr.io";

    /// <summary>
    /// The username presented to a non-ACR registry's token endpoint (<c>Basic user:key</c>). The
    /// mirror authenticates the KEY and discards the username by design (<c>RegistryCredential</c>),
    /// so this is a placeholder there; another OCI registry may care.
    /// </summary>
    public string RegistryUsername { get; init; } = "instance";

    /// <summary>
    /// 🚨 The portal that VALIDATES this installation's instance key at <see cref="Registry"/> —
    /// the container registry's own <c>RegistrySpec.ValidationUrl</c>, restated on the consuming
    /// side. Blank (the default) declares no pairing, and an absent declaration is never
    /// permission: the lister then presents the key only to a registry that IS a configured plugin
    /// registry, exactly as before.
    ///
    /// <para><b>Why a declaration and not an inference.</b> A consuming installation holds ONE
    /// <c>mwi_</c> key, and the fleet's registry deliberately validates it by forwarding it to a
    /// portal: <c>cr.meshweaver.cloud</c>'s <c>docker_auth</c> asks
    /// <c>https://memex.meshweaver.cloud/api/instances/token</c> whether the key is good
    /// (<c>Doc/Architecture/ContainerRegistryInMemex</c>). So the image registry and the plugin
    /// registry are two DIFFERENT hosts by design, and a rule that presents the key only to a host
    /// that is itself a plugin registry can never authenticate there (#4093 — every instance
    /// provisioned on <c>cr.meshweaver.cloud</c> booted and then never self-updated). What makes
    /// the pairing safe is not that the names look alike — they must never be compared by suffix or
    /// registrable domain, which is a coincidence of naming, not a grant — but that the registry
    /// DECLARES which portal it trusts with that key, and the operator restates that declaration
    /// here. Two explicit statements, both required: this key names the validator, and
    /// <c>PluginCatalog:Registries</c> must hold a key for it.</para>
    ///
    /// <para>A full URL (copy the registry record's <c>validationUrl</c> verbatim) or a bare host;
    /// only <see cref="RegistryValidatorHost"/> is ever read from it. The chart renders it from
    /// <c>selfUpdate.registryValidationUrl</c> as <c>SelfUpdate__RegistryValidationUrl</c>.</para>
    /// </summary>
    public string RegistryValidationUrl { get; init; } = "";

    /// <summary>
    /// The HOST of <see cref="RegistryValidationUrl"/> — the ONE extra plugin registry whose
    /// instance key may be presented to <see cref="Registry"/>. <c>null</c> when nothing is
    /// declared, when the value names no http(s) host, or when it is blank: every one of those is
    /// "no pairing declared", which refuses. Pure; a non-default port is part of the host, the
    /// path and scheme are discarded.
    /// </summary>
    public string? RegistryValidatorHost => HostOf(RegistryValidationUrl);

    /// <summary>
    /// The <c>host[:port]</c> of an http(s) URL, or of a bare host (an operator who states
    /// <c>memex.meshweaver.cloud</c> rather than the full validation URL means the same portal).
    /// Null for anything else — a mailto:, a file path, a blank — so a value that cannot be read
    /// as a host declares no pairing rather than half of one. Pure.
    ///
    /// <para>Public because the tag lister reads plugin-registry URLs with it too: ONE host-reading
    /// rule on both sides of the comparison, so the two can never drift into disagreeing about
    /// what <c>https://memex.meshweaver.cloud:443/</c> is.</para>
    /// </summary>
    public static string? HostOf(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not "http" and not "https" || uri.Host.Length == 0)
            return null;
        // 🚨 USERINFO IS REFUSED, not stripped. `https://memex.meshweaver.cloud@evil.test` has the
        // host `evil.test` and reads to a human as the opposite; a value that can be misread that
        // way declares no pairing at all rather than one nobody meant.
        if (uri.UserInfo.Length > 0)
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// Whether <see cref="Registry"/> is an Azure Container Registry host — the discriminator
    /// between the ACR tag lister (proprietary API, Workload Identity) and the OCI Distribution
    /// lister (<c>/v2/{repo}/tags/list</c>, bearer handshake, instance key). Pure: a suffix test
    /// on the host, case-insensitive, so <c>meshweaver.azurecr.io</c> and <c>MESHWEAVER.AZURECR.IO</c>
    /// answer alike and <c>memex.meshweaver.cloud</c> does not.
    /// </summary>
    public bool RegistryIsAzureContainerRegistry =>
        (Registry ?? string.Empty).Trim().TrimEnd('/')
            .EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The ConfigMap the migration Job reads its environment from — the same one the chart's
    /// <c>memex-migration/job.yaml</c> mounts, so a Job the self-updater creates runs with exactly
    /// the inputs a <c>helm upgrade</c> Job would.
    /// </summary>
    public string MigrationConfigMap { get; init; } = "memex-migration-config";

    /// <summary>The Secret the migration Job reads its connection string from (see <see cref="MigrationConfigMap"/>).</summary>
    public string MigrationSecret { get; init; } = "memex-migration-secrets";

    /// <summary>
    /// 🚨 How long a migration Job may run before the roll is REFUSED as stuck. A migration is a
    /// bounded amount of work (every step is idempotent and batched); one that has not completed in
    /// this long is not slow, it is wedged — deadlocked against live traffic, or waiting on a lock
    /// it will never get — and rolling the portal image on top of an unmigrated schema is exactly
    /// the state <c>DbVersionGate</c> refuses. Measured: 6 min on memex (153 schemas), 11 min on
    /// memex-cloud (208 schemas, after contention was removed).
    /// </summary>
    public TimeSpan MigrationJobTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the Job's status is read while it runs.</summary>
    public TimeSpan MigrationJobPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The RETRY interval — how long a faulted watch waits before re-establishing itself.
    ///
    /// <para>🚨 No longer a poll cadence. The update check is event-driven: exactly one pass at
    /// startup (to catch publications missed while this install was down), and after that a check
    /// per build-completion event from the platform or from any module the environment deploys.
    /// This value survives because a stream that faults still has to come back, and it must not
    /// come back in a hot loop.</para>
    /// </summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// 🚨 The retry cadence while the update policy has NEVER been read — the ESTABLISHING case,
    /// which <see cref="RetryInterval"/> is the wrong value for.
    ///
    /// <para>Every trigger — startup, build completion, policy change, AND the
    /// <see cref="SafetyNetCheckInterval"/> safety net — is gated on the first policy emission,
    /// deliberately: the poller must never decide under a policy it has not read (#2731/#2797).
    /// So until that first read succeeds self-update is not slow, it is entirely INERT, and the
    /// safety net that exists to bound a dead channel sits BEHIND that same gate and cannot bound
    /// this one. Pacing the first attempt at the 6 h <see cref="RetryInterval"/> therefore turns a
    /// single slow boot-time <c>SubscribeRequest</c> into six hours with no checks at all.</para>
    ///
    /// <para>Measured, not hypothetical: on 2026-09-01 memex-cloud served
    /// <c>memex.meshweaver.cloud</c> from <c>rc8.ci.6829</c> while ACR had reached
    /// <c>rc9.ci.7231</c> — roughly 400 builds behind, its pinned module set advanced past the
    /// image, modules "contributing nothing", and satellite CI gates failing on the bundle
    /// endpoints. Two pods of the SAME build told the whole story: the one whose policy read
    /// faulted (<c>policy stream faulted; re-establishing in 06:00:00</c>) had run ZERO checks,
    /// while its sibling, which read the policy cleanly, had run two.</para>
    ///
    /// <para>Once the policy HAS been read a later fault keeps the long
    /// <see cref="RetryInterval"/>: the last value is retained, checks keep running, and the
    /// pacing intent is untouched. <see cref="PolicyRetryDelay"/> is that decision, and it is the
    /// only place the two cadences are chosen between.</para>
    /// </summary>
    public TimeSpan PolicyEstablishRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a faulted watch waits before re-establishing: <see cref="PolicyEstablishRetryInterval"/>
    /// while the poller has never read a policy (it is inert until it has, so a fault in that state
    /// costs the whole feature), <see cref="RetryInterval"/> once it has (the value is retained and
    /// checks continue, so the fault costs only freshness). Pure — pinned by
    /// <c>SelfUpdatePollerResilienceTest</c> in both directions.
    /// </summary>
    public TimeSpan PolicyRetryDelay(bool policyEstablished) =>
        policyEstablished ? RetryInterval : PolicyEstablishRetryInterval;

    /// <summary>
    /// The minimum time between two automatic rolls of this install.
    ///
    /// <para>Since the check became event-driven, a publication is a roll and a roll is a pod
    /// restart — so without a floor, publication frequency IS restart frequency, and every restart
    /// drops the live circuits of everyone using the portal. This paces the AUTOMATIC cadence only:
    /// an operator who wants a fix now still has <c>kubectl rollout restart</c> (which picks the
    /// newest image immediately via the startup pass) and a <c>main-cd.yml</c> dispatch.</para>
    ///
    /// <para>The default is one hour, matching CD's reconcile tick — the value that makes hourly
    /// publication actually mean hourly delivery. It is deliberately NOT the 24 h the AKS chart
    /// used to impose: that predates adopt-before-compile (which took a prod roll from 80 compiles
    /// / 64.8 s to 0 compiles / 32.1 s) and it answers "how fast can a fix reach prod" with
    /// "tomorrow", which is the complaint that prompted the faster tick in the first place.</para>
    ///
    /// <para>A roll deferred by this floor is re-decided by the next publication event, exactly
    /// like one deferred for a missing artifact — the floor introduces no timer.</para>
    /// </summary>
    public TimeSpan MinRollInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 🚨 The SAFETY NET: the longest this install may go without ASKING whether a newer release
    /// exists. Zero or negative disables it.
    ///
    /// <para>The check is event-driven, and that is still the design: a build completion wakes it in
    /// seconds, which no interval can match. What the event-only shape got wrong is its failure
    /// mode. Every event reaches this install over a chain of configuration nobody re-verifies — a
    /// GitHub webhook, a <c>WebhookInbox</c> allowlist slot, an HMAC secret that must be
    /// byte-identical on both sides — and EVERY joint of that chain fails SILENTLY. An install
    /// whose event channel is dead is byte-identical, from outside and from its own logs, to an
    /// install that is perfectly up to date: healthy pods, no errors, and a check that simply never
    /// runs. memex sat three builds behind for 7 h in exactly that state (#2494, #2553), and
    /// nothing in the product could have told anyone.</para>
    ///
    /// <para>🚨 So this is NOT the recurring poll this service deliberately removed, and the
    /// distinction is the whole point: a poll DRIVES the update, a safety net BOUNDS how long a
    /// broken driver can hide. Events stay the fast path and decide the latency; this decides the
    /// worst case. It is also, by construction, unable to change the ROLL cadence — a safety-net
    /// check is gated by <see cref="MinRollInterval"/> exactly like an event-driven one, so it can
    /// only ever discover a release sooner, never roll more often.</para>
    ///
    /// <para>Default one hour: the same value as <see cref="MinRollInterval"/>, so the safety net
    /// can never propose a roll the floor would refuse anyway, and the same cadence as CD's own
    /// reconcile tick. The cost is one ACR tag list per hour per install.</para>
    /// </summary>
    public TimeSpan SafetyNetCheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a burst of build-completion events is coalesced before one check runs.
    ///
    /// <para>Several repositories publishing at once (a platform release plus its satellites
    /// re-baking) should cost ONE availability check, not one per repository. Configurable rather
    /// than hard-coded so tests can drive the event path without waiting out the production
    /// window.</para>
    /// </summary>
    public TimeSpan EventCoalesceWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 🚨 Roll even though the release-availability gate is NOT WIRED into this host.
    ///
    /// <para>Default <c>false</c>: an unwired gate is a HOLD. The gate answers "does every package
    /// this environment deploys have a usable artifact for the target release" (#1754), and a host
    /// that has no gate registered has not answered it — it has failed to ask. That is the absence
    /// of a verdict, not a passing one, and this repo has paid for treating the two the same more
    /// than once.</para>
    ///
    /// <para>Set it <c>true</c> to roll unverified — deliberately, in configuration, where it is
    /// visible — exactly as <c>PreWarm:AllowUnprovenBake</c> lets a pod serve on an unproven bake.
    /// The roll then proceeds AND says so, at Warning, on every tick. It can never waive a gate
    /// that DID run: a real hold is unaffected by this key.</para>
    /// </summary>
    public bool AllowUnverifiedRoll { get; init; }

    /// <summary>
    /// The policy seeded onto <c>Admin/UpdatePolicy</c> when it doesn't exist yet.
    ///
    /// <para>🚨 <see cref="UpdatePolicyKind.Stable"/> — clean releases only (maintainer,
    /// 2026-09-08: <i>"by default we will not upgrade as long as no version without <c>-ci…</c> is
    /// labelled"</i>). A host that wants a fresh install to follow continuous builds seeds
    /// <see cref="UpdatePolicyKind.Continuous"/> together with a <see cref="DefaultPattern"/>;
    /// <c>Continuous</c> alone is still clean-only (see <see cref="UpdateChannelPattern"/>).</para>
    /// </summary>
    public UpdatePolicyKind DefaultPolicy { get; init; } = UpdatePolicyKind.Stable;

    /// <summary>
    /// The version pattern seeded beside <see cref="DefaultPolicy"/> when the node is created —
    /// <c>null</c> (the default) seeds none. Only meaningful with
    /// <see cref="UpdatePolicyKind.Continuous"/>: a dev/test host that should stay on the current
    /// line's continuous builds sets e.g. <c>SelfUpdate__DefaultPattern=3.0.0-ci*</c>. An EXISTING
    /// record is never touched by this value — the pattern is edited on the record itself
    /// (Settings → Updates).
    /// </summary>
    public string? DefaultPattern { get; init; }

    /// <summary>The full image reference for a portal version tag.</summary>
    public string PortalImage(string tag) => $"{Registry}/{PortalRepository}:{tag}";

    /// <summary>The full image reference for a migration version tag.</summary>
    public string MigrationImage(string tag) => $"{Registry}/{MigrationRepository}:{tag}";
}
