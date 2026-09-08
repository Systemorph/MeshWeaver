// The Deployment record — the ONE input every renderer reads (Helm via the Hosting module, the Aspire adapter, the portal binding its own record from configuration). Moved out of the in-mesh Hosting module (MeshWeaver.Plugins Hosting/Deployment/Source) on 2026-09-08, byte-compatible on the wire ($type + camelCase). Zero MeshWeaver dependencies by design: this assembly rides the published Aspire package.
using System.Collections.Immutable;
using System.ComponentModel;
using System;

namespace MeshWeaver.Deployment;

/// <summary>
/// Content of an Hosting/Deployment node — one operated MeshWeaver installation (a namespace on a
/// cluster, its host, its database). This is the INTENDED state, recorded in git; what the cluster
/// actually runs is read live and compared against it (see <c>DeploymentLayoutAreas</c>).
///
/// Immutable; every mutation goes through <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
///
/// 🚨 NOTHING SECRET GOES IN HERE. These nodes sync to a git repository, so they carry only
/// identifiers and topology — never connection strings, client secrets, or Key Vault VALUES. The
/// Key Vault fields below name secrets; they never hold them.
/// </summary>
public record DeploymentContent
{
    /// <summary>Public host the portal serves on (e.g. "memex.systemorph.com"). Null → not routed yet.</summary>
    public string? Host { get; init; }

    /// <summary>Kubernetes namespace. Conventionally the same token as the node id.</summary>
    public string? Namespace { get; init; }

    /// <summary>Cluster the namespace lives on (e.g. "memexaks-cluster").</summary>
    public string? Cluster { get; init; }

    /// <summary>Database name on the shared PostgreSQL server — one database per deployment.</summary>
    public string? Database { get; init; }

    /// <summary>PostgreSQL server hosting <see cref="Database"/> (e.g. "memexaks-pg"). Name only.</summary>
    public string? DatabaseServer { get; init; }

    /// <summary>Container image repository this deployment pulls (e.g. "meshweaver.azurecr.io/memex-portal-ai").</summary>
    public string? ImageRepository { get; init; }

    /// <summary>
    /// Image tag this deployment is INTENDED to run. Empty means "whatever the self-updater picks",
    /// which is the normal state — pin it only to hold a deployment back deliberately.
    /// The tag actually running is read from the cluster, never stored here.
    /// </summary>
    public string? PinnedImageTag { get; init; }

    /// <summary>
    /// The Kubernetes Secret (type <c>kubernetes.io/dockerconfigjson</c>) in the instance's own
    /// namespace that the pods present when they pull <see cref="ImageRepository"/>. A NAME only —
    /// the credential inside it is the instance's plugin-registry key, written by the provision's
    /// <c>hosting-pull-secret</c> step from the vault object <c>hosting-kv-ensure</c> already
    /// requires. Blank → none: the fleet's ACR pull rides the node identity, so an instance on
    /// <c>meshweaver.azurecr.io</c> states nothing here.
    ///
    /// <para>🚨 The rule the fleet follows (maintainer directive, 2026-09-08): every installation
    /// EXCEPT the one serving the images pulls them from that portal's <c>/v2</c> — the
    /// read-through mirror — instead of ACR, at creation and on every self-update. The mirror
    /// instance itself stays on ACR, because a portal cannot serve the image that boots it. So a
    /// record whose <see cref="ImageRepository"/> names a registry other than ACR MUST name this
    /// Secret (<c>HelmValues.Problems</c> refuses it otherwise): without one the very first pull
    /// answers 401 and the instance never starts. It renders as <c>portal.imagePullSecret</c>,
    /// and the registry host of the repository renders as <c>selfUpdate.registry</c> so the
    /// self-updater polls and rolls against the same registry the pods pull from.</para>
    /// </summary>
    [Description("Image pull Secret name in the instance namespace — required when the image registry is not ACR")]
    public string? ImagePullSecret { get; init; }

    /// <summary>Self-update policy: Continuous (default), Stable, or None.</summary>
    public string? UpdatePolicy { get; init; }

    /// <summary>Who this deployment is for — a customer name, or "Systemorph" for our own.</summary>
    public string? Owner { get; init; }

    /// <summary>What it is for, in one line. Shown in the overview.</summary>
    public string? Purpose { get; init; }

    /// <summary>True when this deployment serves the plugin registry other deployments read from.</summary>
    public bool IsPluginRegistry { get; init; }

    /// <summary>
    /// The id this deployment is REGISTERED under at the plugin registry — the value its
    /// <c>PluginCatalog__InstanceId</c> carries and the <c>MeshWeaverInstance</c> record's id. Blank
    /// means the deployment id (the fleet's convention: <c>memex</c>, <c>memex-cloud</c>). A key
    /// rotation adopts the new hash on THIS instance, so a mismatch here rotates the wrong instance
    /// out from under a running portal — state it when the two differ.
    /// </summary>
    [Description("Instance id at the plugin registry — blank means the deployment id")]
    public string? RegistryInstanceId { get; init; }

    /// <summary>Lifecycle: Planned, Live, Suspended, Decommissioned. Null is treated as Live.</summary>
    public string? Status { get; init; }

    /// <summary>Key Vault holding this deployment's secrets (e.g. "Systemorph"). NAME only.</summary>
    public string? KeyVault { get; init; }

    /// <summary>Prefix its Key Vault secrets share (e.g. "memex-"). A prefix, never a value.</summary>
    public string? KeyVaultSecretPrefix { get; init; }

    /// <summary>Grafana base URL for this deployment's logs. Empty → no logs link in the overview.</summary>
    public string? GrafanaBaseUrl { get; init; }

    /// <summary>
    /// Path, relative to the repository root, of the per-environment cluster config for this
    /// deployment (e.g. "deployments/aks/memex"). Lets the overview link a record to the files that
    /// realise it.
    /// </summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// URL of the git repository holding this deployment's config and this record itself
    /// (e.g. "https://github.com/Systemorph/Memex"). With <see cref="ConfigPath"/> it lets the
    /// record link straight to the files that realise it.
    /// </summary>
    public string? Repository { get; init; }

    /// <summary>Free-form operator notes (markdown) — quirks, migrations, anything worth knowing.</summary>
    public string? Notes { get; init; }

    // ───────────────────────────────── the instance spec ─────────────────────────────────
    // What it takes to CREATE this deployment, not merely to describe one that exists. Every
    // field below is read by Hosting/InstanceAction when it provisions or tears the instance
    // down; recording them is what makes an instance reproducible by someone other than whoever
    // built the last one.

    /// <summary>
    /// The plugin repositories this instance mounts. Empty means the instance comes up with NO
    /// plugins and its Plugin Catalog reads "not configured" — a legitimate state for a bare
    /// deployment, and a silent surprise for any other, which is why it is recorded rather than
    /// assumed. Rendered into the portal's own <c>PluginCatalog</c> configuration by
    /// <see cref="DeploymentPortalConfig.ConfigurationEntries"/>.
    /// </summary>
    public ImmutableList<PluginRepoMount> PluginRepos { get; init; } = ImmutableList<PluginRepoMount>.Empty;

    /// <summary>
    /// Packages a FRESH instance seeds itself with, beyond the platform baseline every
    /// installation gets. Package ids (<c>Edu</c>) or explicit <c>Source/Package</c> patterns
    /// (<c>Plugins/Edu</c>, <c>Plugins/*</c>); a bare id is qualified against
    /// <see cref="PluginRepos"/>, because the catalog matches source-scoped and FAILS CLOSED on an
    /// unqualified one. Rendered into <c>PluginCatalog:InstallByDefault</c>, which the portal
    /// applies once, on an installation with no install records.
    /// </summary>
    public ImmutableList<string> PreInstall { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Key Vault secret NAME holding the MAIN database connection string. Blank → the convention
    /// <c>{KeyVaultSecretPrefix}db-connection</c> (<see cref="DeploymentPortalConfig.DatabaseSecretName"/>).
    /// A NAME, never a value — the value lives in the vault, written by the provision's
    /// <c>hosting-kv-ensure</c> step or by an operator's hand.
    /// </summary>
    public string? DatabaseConnectionSecret { get; init; }

    /// <summary>
    /// Azure storage account backing this deployment's file shares and content (the PVC storage
    /// class and blob/content storage). NAME only. Blank → the deployment rides the cluster's
    /// shared account, which is the fleet default today.
    /// </summary>
    public string? StorageAccount { get; init; }

    /// <summary>
    /// Key Vault secret NAME holding the MAIN storage account's connection string. Blank → the
    /// convention <c>{KeyVaultSecretPrefix}storage-connection</c>
    /// (<see cref="DeploymentPortalConfig.StorageSecretName"/>). A NAME, never a value — same rule as every
    /// other field here.
    /// </summary>
    public string? StorageConnectionSecret { get; init; }

    /// <summary>
    /// Module assemblies this instance must BOOT — the database driver, the AI engine, the MCP
    /// endpoint (<c>MeshWeaver.Hosting.PostgreSql.dll</c>, <c>MeshWeaver.AI.dll</c>,
    /// <c>MeshWeaver.Mcp.dll</c>, …). Rendered into <c>Modules:Required</c> by
    /// <see cref="DeploymentPortalConfig.ModuleEntries"/>, so a missing module STALLS the rollout instead of
    /// booting green without the feature (the 2026-08-26/27 AI-engine shape).
    ///
    /// <para>🚨 <c>Modules:Required</c> configuration overrides the image's own list BY INDEX —
    /// entry N here replaces the image's entry N, it never appends. So when this list is used at
    /// all, record the COMPLETE required set for the instance (the image baseline included), not a
    /// delta; a partial list silently rewrites what the image's leading indices name.</para>
    /// </summary>
    public ImmutableList<string> RequiredModules { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Where this deployment's database backups go — the path of a <c>Hosting/BackupStore</c> node.
    /// Blank → the fleet default store. A teardown that is asked to back up and finds NO store
    /// resolvable is REFUSED, never silently skipped.
    /// </summary>
    public string? BackupStore { get; init; }

    /// <summary>
    /// Days without recorded activity before the instance is SUSPENDED automatically — the
    /// reversible paywall + verified dump, exactly the manual Suspend. Null = never. The idle
    /// reconciler acts only when a status sample actually carries an activity timestamp
    /// (<c>DeploymentStatusContent.LastActivityAt</c>); no signal, no automatic action.
    /// </summary>
    public int? IdleSuspendDays { get; init; }

    /// <summary>
    /// Days a suspension may stand before the instance is TORN DOWN automatically. The suspension
    /// IS the notice period. Null = never automatically. See <c>IdlePolicy</c>.
    /// </summary>
    public int? IdleTeardownDays { get; init; }

    /// <summary>
    /// Public DNS zone this deployment's host lives in (e.g. <c>meshweaver.cloud</c>). Provision
    /// adds the A-record in it; teardown removes it. Blank → DNS is managed outside this record,
    /// and both phases skip it and SAY SO rather than guessing a zone.
    /// </summary>
    public string? DnsZone { get; init; }

    /// <summary>
    /// Helm release name for this deployment. Blank → the namespace. Recorded because a release
    /// named differently from its namespace is invisible until an upgrade targets the wrong one.
    /// </summary>
    public string? HelmRelease { get; init; }

    /// <summary>
    /// Intended portal replicas. Null → the chart's default. Intent only: what is RUNNING is
    /// sampled into Hosting/DeploymentStatus, never written back here. A suspension records the
    /// value it scaled DOWN from here, so reactivation restores the same shape.
    /// </summary>
    public int? Replicas { get; init; }

    /// <summary>
    /// When this instance was suspended. Null → never, or already reactivated. The grace period
    /// before a teardown may proceed is measured from here, so it is part of the RECORD rather
    /// than something reconstructed from an activity log.
    /// </summary>
    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>
    /// Days a suspension is held before this instance may be torn down. 0 → the platform default.
    /// The suspension IS the notice period: the host answers with the paywall throughout it and
    /// the owner can export their data the whole time.
    /// </summary>
    public int GracePeriodDays { get; init; }

    /// <summary>Why the instance was suspended, in one line. Shown to nobody but operators.</summary>
    public string? SuspensionReason { get; init; }

    // ───────────────────────────────── the instance SHAPE ─────────────────────────────────
    // What the helm chart's values used to carry per environment, by hand, and what its
    // templates hard-coded — typed here so the record is the ONLY source of an instance's
    // deployment. HelmValues projects this onto the chart's values surface today; the manifest
    // generator that replaces the hand-written chart projects the same fields tomorrow. Every
    // property is optional: a null means "not stated here" and renders NOTHING, so a record that
    // omits a section neither invents a value nor blanks one the Key Vault half supplies.
    // See InstanceShape.cs for the records and the rationale each carries.

    /// <summary>
    /// The database server's HOST as the portal connects to it. Blank → derived from
    /// <see cref="DatabaseServer"/> as an Azure flexible server
    /// (<c>{server}.postgres.database.azure.com</c>), or the in-cluster service when no server is
    /// named. Always the FQDN, never the private IP: the IP changes on server maintenance.
    /// </summary>
    [Description("Database host (FQDN) — blank derives it from the server name")]
    public string? DatabaseHost { get; init; }

    /// <summary>The database port. Null → 5432.</summary>
    [Description("Database port")]
    public int? DatabasePort { get; init; }

    /// <summary>The database user the portal and the migration connect as. Blank → <c>postgres</c>. The password is a secret and rides the Key Vault half.</summary>
    [Description("Database user")]
    public string? DatabaseUsername { get; init; }

    /// <summary>
    /// Whether the chart runs its own in-cluster Postgres. False on every fleet instance — they
    /// share an Azure flexible server, and the retired in-cluster StatefulSet must STAY at zero;
    /// rendering it again would scale a dead database back up beside the live one.
    /// </summary>
    [Description("Run an in-cluster Postgres (self-host only)")]
    public bool InClusterPostgres { get; init; }

    /// <summary>The migration image repository. Blank → the portal repository with <c>memex-portal-ai</c> replaced by <c>memex-migration</c> (the fleet's pairing).</summary>
    [Description("Migration image repository — blank derives it from the portal's")]
    public string? MigrationImageRepository { get; init; }

    /// <summary>The portal's HTTP port inside the pod. Null → 8080.</summary>
    [Description("HTTP port")]
    public int? HttpPort { get; init; }

    /// <summary>The RAM/CPU envelope — the first thing an operator sizes.</summary>
    [Description("Resources (RAM / CPU)")]
    public ResourceEnvelope? Resources { get; init; }

    /// <summary>KEDA autoscaling. Null → not stated; the chart renders a fixed <see cref="Replicas"/>.</summary>
    [Description("Autoscaling")]
    public Autoscaling? Autoscaling { get; init; }

    /// <summary>The persistent volumes the portal mounts.</summary>
    [Description("Volumes")]
    public ImmutableList<VolumeClaim> Volumes { get; init; } = ImmutableList<VolumeClaim>.Empty;

    /// <summary>How the host reaches the portal.</summary>
    [Description("Ingress")]
    public IngressSpec? Ingress { get; init; }

    /// <summary>
    /// The Next.js React front end served at <c>/next</c> beside the Blazor portal. Null → nothing
    /// renders and the chart's default (off) stands, so a record that does not mention it deploys
    /// exactly as before. Stating it here is what stops a re-render of the values file from
    /// DELETING a deployed front end: the block used to live only in a hand-written overlay under
    /// a header claiming the file is generated from this record (MeshWeaver.Plugins#1408).
    /// </summary>
    [Description("React front end at /next")]
    public PortalNextSpec? PortalNext { get; init; }

    /// <summary>
    /// The pod's FIRST Key Vault secret class, DECLARED by name — the chart renders the
    /// SecretProviderClass, the CSI volume, its mount and the envFrom from this one section.
    /// Null → nothing renders. Names only, never values; see <see cref="KeyVaultSecretsSpec"/>.
    ///
    /// <para>An instance that reads MORE than one vault-secret set declares the rest in
    /// <see cref="KeyVaultSecretClasses"/>; the two together are the instance's classes, in that
    /// order, and that order is env PRECEDENCE.</para>
    /// </summary>
    [Description("Key Vault secrets (declared by name)")]
    public KeyVaultSecretsSpec? KeyVaultSecrets { get; init; }

    /// <summary>
    /// ADDITIONAL Key Vault secret classes — one entry per further SecretProviderClass the instance
    /// reads, same shape as <see cref="KeyVaultSecrets"/>.
    ///
    /// <para>🚨 WHY IT EXISTS, measured. <c>memex</c> runs TWO classes: the hand-made <c>memex-kv</c>
    /// (13 keys — the connection string, the GitHub App private key, the AI provider keys) and the
    /// chart-owned <c>memex-portal-keyvault</c> (the plugin-registry token). With one slot the
    /// record had to CHOOSE, and whichever it named a re-render DROPPED the other. On 2026-09-06
    /// the record named <c>memex-kv</c> while the deployed overlay named <c>memex-portal-keyvault</c>:
    /// a re-render from the record would have removed the class that had just fixed the registry
    /// poll (MeshWeaver#3201 / Memex#164) AND rendered a class for <c>memex-kv</c> naming a vault
    /// object that does not exist — a failed CSI mount, i.e. a portal that cannot start. The record
    /// could not describe the instance, so re-deriving the overlay from it was destructive.</para>
    ///
    /// <para>ORDER IS PRECEDENCE. The classes are appended to the pod's <c>envFrom</c> after the
    /// chart's ConfigMap and Secret, in this order; Kubernetes keeps the LAST entry on a key clash.
    /// A key declared by two classes is therefore ambiguous to a reader and is a named problem —
    /// see <c>HelmValues.Problems</c>.</para>
    /// </summary>
    [Description("Additional Key Vault secret classes (a second SecretProviderClass, and further)")]
    public ImmutableList<KeyVaultSecretsSpec> KeyVaultSecretClasses { get; init; }
        = ImmutableList<KeyVaultSecretsSpec>.Empty;

    /// <summary>
    /// Configuration keys the chart's OWN Secret (<c>memex-portal-secrets</c>) supplies, by NAME.
    /// That Secret is rendered from the Key Vault "values half" — the captured
    /// <c>secrets.memex_portal.*</c> block that <c>helm-release</c> layers under the committed
    /// overlay — so its keys are decided outside this record and outside the repository.
    ///
    /// <para>Declarative: this renders NOTHING. It exists so the record knows what that layer
    /// supplies, which is what makes the precedence stack complete and shadow analysis honest. On
    /// <c>memex</c> six of its eleven keys are ALSO supplied by a declared class
    /// (<c>Ai__KeyProtection__MasterKey</c>, <c>Authentication__Microsoft__ClientSecret</c>,
    /// <c>AzureFoundry__ApiKey</c>, <c>ConnectionStrings__memex</c>, <c>OpenRouter__ApiKey</c>,
    /// <c>PluginCatalog__RegistryToken</c>) and the class wins, because it sits later in
    /// <c>envFrom</c>. Without this list, a reader of the record cannot tell that the chart's Secret
    /// even carries them.</para>
    ///
    /// <para>NAMES ONLY. Never a value — the same rule as every other field here.</para>
    /// </summary>
    [Description("Keys the chart's own Secret supplies, from the Key Vault values half (names only)")]
    public ImmutableList<string> VaultValuesKeys { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Environment variables set INLINE on the live Deployment — the layer above every
    /// <c>envFrom</c>, and the only one no file renders.
    ///
    /// <para>Declarative: this renders NOTHING, and that is the point. <c>helm upgrade</c> does not
    /// remove an inline entry (three-way merge removes only what helm previously owned, measured
    /// 2026-09-03), so one SURVIVES every deploy and keeps outranking the ConfigMap and every synced
    /// Secret. A record that cannot state them disagrees with the pod at every read, which makes
    /// every real difference indistinguishable from the known ones. See
    /// <see cref="InlineEnvOverride"/> — and never record a credential's value there.</para>
    /// </summary>
    [Description("Inline env entries on the live Deployment (declarative — renders nothing)")]
    public ImmutableList<InlineEnvOverride> InlineEnv { get; init; }
        = ImmutableList<InlineEnvOverride>.Empty;

    /// <summary>
    /// Foreign-language GATE sidecars in the portal pod (python, node, pandas). Empty → the pod runs
    /// the portal container alone. Rendered into the chart's <c>grpc.gates</c>.
    /// </summary>
    [Description("Language gate sidecars")]
    public ImmutableList<GateSpec> Gates { get; init; } = ImmutableList<GateSpec>.Empty;

    /// <summary>
    /// ⚠️ LEGACY — mounts of HAND-MADE SecretProviderClasses, by name. Declare the secrets under
    /// <see cref="KeyVaultSecrets"/> instead; kept for the transition (a mount that names the same
    /// volume or class as the declaration is a named problem).
    /// </summary>
    [Description("Key Vault secret mounts (legacy — hand-made SecretProviderClass by name)")]
    public ImmutableList<KeyVaultSecretMount> SecretMounts { get; init; } = ImmutableList<KeyVaultSecretMount>.Empty;

    /// <summary>AI providers and the tier map.</summary>
    [Description("AI providers")]
    public AiProviders? Ai { get; init; }

    /// <summary>Sign-in providers — client ids; secrets ride the Key Vault half.</summary>
    [Description("Sign-in")]
    public SignInSpec? SignIn { get; init; }

    /// <summary>System email through Microsoft Graph.</summary>
    [Description("Email")]
    public EmailSpec? Email { get; init; }

    /// <summary>The GitHub App identity (identifiers only).</summary>
    [Description("GitHub App")]
    public GitHubAppIdentity? GitHubApp { get; init; }

    /// <summary>The lifecycle operator — the control instance only.</summary>
    [Description("Hosting operator")]
    public HostingOperatorSpec? Operator { get; init; }

    /// <summary>
    /// The container registry service this instance HOSTS for the fleet (MeshWeaver#3353) —
    /// <c>cr.meshweaver.cloud</c>, an off-the-shelf blob-backed OCI registry the portal chart
    /// deploys beside the portal under <c>registry.enabled</c>. Only the PUBLIC instance sets it;
    /// null → nothing renders and no registry runs in the namespace. Every other installation is a
    /// consumer: it names <c>{host}/memex-portal-ai</c> as its <see cref="ImageRepository"/> and
    /// the pull Secret in <see cref="ImagePullSecret"/>, and pulls with its plugin-registry key.
    /// Names only — see <see cref="RegistrySpec"/>.
    /// </summary>
    [Description("Container registry this instance hosts (the public instance only)")]
    public RegistrySpec? Registry { get; init; }

    /// <summary>OpenTelemetry export.</summary>
    [Description("Telemetry")]
    public TelemetrySpec? Telemetry { get; init; }

    /// <summary>The rollout drain. Null → the platform defaults (1800 s ceiling, 120 s margin).</summary>
    [Description("Rollout drain")]
    public DrainSpec? Drain { get; init; }

    /// <summary>The startup probe budget. Null → the platform default (5 × 60 = 5 minutes).</summary>
    [Description("Startup probe")]
    public StartupProbeSpec? StartupProbe { get; init; }

    /// <summary>Where the portal keeps its state. Null → the platform layout.</summary>
    [Description("Storage layout")]
    public StorageLayout? Storage { get; init; }

    /// <summary>
    /// Orleans clustering: <c>AdoNet</c> or <c>Localhost</c>. Blank → AdoNet when the instance runs
    /// more than one pod (fixed replicas above 1, or autoscaling), else Localhost. "Localhost" on a
    /// multi-pod instance is single-process membership: every pod becomes its OWN one-silo cluster,
    /// every singleton runs N times, node writes collide and cross-pod calls fail (measured live
    /// 2026-08-25) — which is why the derivation exists.
    /// </summary>
    [Description("Orleans clustering — blank derives it from the replica count")]
    public string? OrleansClustering { get; init; }

    /// <summary>The self-updater's restart floor. Blank → <c>01:00:00</c>, matched to the publication tick.</summary>
    [Description("Minimum interval between self-update rolls")]
    public string? MinRollInterval { get; init; }

    /// <summary>
    /// Converge onto every newly published NodeType build (MeshWeaver#2192). Off, every publication
    /// wave leaves serving pods on their old assemblies behind the Recycle banner until the
    /// type-hosting silos go GC-bound at 17–20 GB while still Ready. Null → not stated.
    /// </summary>
    [Description("Auto-recycle on a stale NodeType build")]
    public bool? AutoRecycleOnStaleBuild { get; init; }

    /// <summary>
    /// Boot modules placed at an EXPLICIT index past the contiguous <see cref="RequiredModules"/>
    /// list — the by-index override. <c>Modules:Required</c> replaces the image's entry N and never
    /// appends, so "require MCP without touching the image's slots 5 and 6" is slot 7 here, not a
    /// sixth list entry (the Memex#131 replacement that silently un-required a module).
    /// </summary>
    [Description("Boot modules at explicit slots (by-index override)")]
    public ImmutableSortedDictionary<int, string> RequiredModuleSlots { get; init; }
        = ImmutableSortedDictionary<int, string>.Empty;

    /// <summary>
    /// Webhook inbox targets by slot. Both fleet slots are stated explicitly where used, because a
    /// hand-set <c>env:</c> on the live Deployment permanently shadows whatever the ConfigMap renders
    /// for the same name (#2235).
    /// </summary>
    [Description("Webhook inbox targets, by slot (legacy — prefer the typed slots)")]
    public ImmutableList<string> WebhookInboxTargets { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// 🚨 The webhook inbox slots WITH their authentication (MeshWeaver.Plugins#1442). The chart
    /// renders two keys per slot — <c>WebhookInbox__Targets__N</c> (where a delivery goes) and
    /// <c>WebhookInbox__Targets__N__SecretConfigKey</c> (the configuration key holding the HMAC the
    /// inbox verifies <c>X-Hub-Signature-256</c> against; empty = accept unverified). Stating the
    /// second half in the free-form extras is how memex-cloud's key was reverted by the next
    /// automated write while the target survived (Systemorph/Memex#205): reachability was typed,
    /// authentication was not, and the two came apart. Here they are one record per slot.
    /// <para>When this list is set, <see cref="WebhookInboxTargets"/> must be empty
    /// (<c>HelmValues.Problems</c> names a record that states both), and an extra naming a
    /// slot's key is a collision, not an override.</para>
    /// </summary>
    [Description("Webhook inbox slots: target + the config key holding its HMAC secret")]
    public ImmutableList<WebhookInboxSlot> WebhookInbox { get; init; } = ImmutableList<WebhookInboxSlot>.Empty;

    /// <summary>The Social plugin's LinkedIn app client id (identifier only).</summary>
    [Description("Social LinkedIn client id")]
    public string? SocialLinkedInClientId { get; init; }

    /// <summary>
    /// The advanced rung: portal configuration keys (<c>Section__Key</c>) beyond the typed surface,
    /// rendered verbatim under the portal config. A key the chart's ConfigMap does not list never
    /// reaches a container — the typed properties above are the ones it is known to read.
    /// </summary>
    [Description("Extra portal configuration (Section__Key = value)")]
    public ImmutableSortedDictionary<string, string> ExtraPortalConfig { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;
}

/// <summary>
/// One webhook inbox slot: where a delivery is routed and how it is authenticated — both halves
/// of what the chart renders per slot, on one record so they cannot be edited, rendered, reverted
/// or migrated independently (MeshWeaver.Plugins#1442).
/// </summary>
public sealed record WebhookInboxSlot
{
    /// <summary>The mesh path the slot routes deliveries to, e.g. <c>Hosting/PlatformBuilds</c>.</summary>
    [Description("Target path the slot routes deliveries to")]
    public string Target { get; init; } = "";

    /// <summary>
    /// The CONFIGURATION KEY holding this slot's shared HMAC secret — never the secret itself. When
    /// set, the inbox verifies <c>X-Hub-Signature-256</c> over the raw body and answers 401 to a
    /// delivery it cannot verify; blank keeps the unverified contract, which is the only safe default
    /// for a slot whose sender does not sign GitHub-style (a Stripe target signs
    /// <c>Stripe-Signature</c>, which this endpoint does not speak).
    /// </summary>
    [Description("Configuration key holding the slot's HMAC secret (blank = unverified)")]
    public string? SecretConfigKey { get; init; }
}
