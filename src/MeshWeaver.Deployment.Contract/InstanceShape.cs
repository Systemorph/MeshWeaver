// The nested records of the Deployment record — moved with it (see DeploymentContent.cs).
using System.Collections.Immutable;
using System.ComponentModel;
using System;

namespace MeshWeaver.Deployment;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The typed SHAPE of an instance: what the helm chart's templates used to encode as hard-coded
// Kubernetes concerns and what its per-environment values files used to carry by hand. Every
// record below is intent, recorded in git through the Deployment node, and projected onto the
// chart's values surface by HelmValues — and, next, straight onto manifests by the generator that
// replaces the hand-written chart. Where a template carried a 🚨 rationale for a number, that
// number is a property here with the same default and the rationale in its description, so the
// knowledge survives the move out of Go templates and into code.
//
// 🚨 Nothing secret. These records sync to a git repository: identifiers, sizes, names of secrets
// — never a value of one. The Key Vault half stays in Key Vault and is referenced by name only.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The container registry service one instance HOSTS for the fleet (MeshWeaver#3353): an
/// off-the-shelf OCI <c>distribution</c> registry, blob-backed, fronted by <c>docker_auth</c>,
/// deployed by the portal chart under <c>registry.enabled</c>. Only the public instance sets it —
/// every other installation CONSUMES it through its own <see cref="DeploymentContent.ImageRepository"/>
/// (<c>{host}/memex-portal-ai</c>) and <see cref="DeploymentContent.ImagePullSecret"/>.
///
/// <para>Names only, as everywhere in the record: the TLS certificate, the token-signing key and
/// the HTTP secret are Key Vault OBJECTS named in <see cref="KeyVault"/>; the publisher's
/// password is stated as its bcrypt HASH, which is what <c>docker_auth</c> compares against and
/// which cannot be turned back into the password. The registry's own pull is decided by the
/// portal at <see cref="ValidationUrl"/> — the same instance-key gate the plugin registry uses —
/// so a consumer holds ONE credential for both.</para>
/// </summary>
public sealed record RegistrySpec
{
    /// <summary>The public host the registry serves on (<c>cr.meshweaver.cloud</c>). Consumers name it as the host of their image repository.</summary>
    [Description("Registry host (e.g. cr.meshweaver.cloud)")]
    public string? Host { get; init; }

    /// <summary>The <c>distribution</c> image, fully qualified with its tag. Pulled from ACR by the node identity — a registry cannot serve the image that boots it.</summary>
    [Description("Registry image (repository:tag)")]
    public string? Image { get; init; }

    /// <summary>The <c>docker_auth</c> image, fully qualified with its tag.</summary>
    [Description("Token-authentication image (repository:tag)")]
    public string? AuthImage { get; init; }

    /// <summary>The token issuer name the registry and the auth service agree on. Blank → <c>memex-registry</c>.</summary>
    [Description("Token issuer — blank means memex-registry")]
    public string? Issuer { get; init; }

    /// <summary>The Azure storage account holding the blobs. NAME only; the registry reaches it through its workload identity.</summary>
    [Description("Storage account holding the image blobs (name only)")]
    public string? StorageAccountName { get; init; }

    /// <summary>The blob container in that account. Blank → <c>registry</c>.</summary>
    [Description("Blob container — blank means registry")]
    public string? StorageContainer { get; init; }

    /// <summary>The Kubernetes ServiceAccount the registry pods run as — the one federated to the identity that reads the storage account and the vault.</summary>
    [Description("ServiceAccount the registry pods run as")]
    public string? ServiceAccount { get; init; }

    /// <summary>The Key Vault objects the registry reads — the TLS pair, the token-signing key, the HTTP secret. Names only.</summary>
    [Description("Key Vault objects (names only)")]
    public RegistryKeyVaultSpec? KeyVault { get; init; }

    /// <summary>The one account that may PUSH — CD's publish lane. Blank → <c>publisher</c>.</summary>
    [Description("Publisher username — blank means publisher")]
    public string? PublisherUsername { get; init; }

    /// <summary>
    /// The publisher password as its bcrypt HASH (<c>$2y$…</c>) — what <c>docker_auth</c> stores
    /// and compares against. A hash, never the password: it cannot be reversed, which is what makes
    /// it recordable in a git-synced node.
    /// </summary>
    [Description("Publisher password, bcrypt hash — never the password itself")]
    public string? PublisherPasswordBcrypt { get; init; }

    /// <summary>
    /// The portal endpoint that decides a PULL: the auth service forwards the caller's instance key
    /// there and grants <c>pull</c> when the portal answers 200 — so a consumer's plugin-registry
    /// key IS its image-pull credential. Blank → <c>https://memex.meshweaver.cloud/api/instances/token</c>,
    /// the key→JWT exchange: cheap (~0.2 s) and authoritative, where the catalog URL
    /// (<c>/api/plugins?ref=HEAD</c>) is a 14–18 s render that overruns the auth service's budget
    /// and is not a validator.
    /// </summary>
    [Description("Portal URL that validates a consumer's key — blank means the public instance's token exchange")]
    public string? ValidationUrl { get; init; }

    /// <summary>Where the registry POSTs its push/pull notifications (the portal's inbox). Blank → no notifications.</summary>
    [Description("Notification endpoint URL — blank means none")]
    public string? NotificationsUrl { get; init; }

    /// <summary>Registry replicas. Null → 1. Stateless in front of the blob store, so it scales freely.</summary>
    [Description("Replicas — blank means 1")]
    public int? Replicas { get; init; }
}

/// <summary>
/// The Key Vault objects a hosted registry reads (<see cref="RegistrySpec.KeyVault"/>): the
/// vault's coordinates and the NAMES of the TLS certificate, its private key, the HTTP secret and
/// the notification secret. Names only — the values live in the vault and reach the pods through
/// the CSI driver, the same path every other declared secret takes.
/// </summary>
public sealed record RegistryKeyVaultSpec
{
    /// <summary>The vault holding the registry's objects. Stated, never derived — a registry's TLS pair is not a portal secret, and naming the vault beside the objects keeps the four together.</summary>
    [Description("Key Vault name")]
    public string? Name { get; init; }

    /// <summary>The vault's Entra tenant. Blank → the record's <c>keyVaultSecrets.tenantId</c>.</summary>
    [Description("Tenant id — blank means the record's keyVaultSecrets tenant")]
    public string? TenantId { get; init; }

    /// <summary>The identity that reads the vault. Blank → the record's <c>keyVaultSecrets.identityClientId</c>.</summary>
    [Description("Identity client id — blank means the record's keyVaultSecrets identity")]
    public string? IdentityClientId { get; init; }

    /// <summary>The vault object holding the token-signing CERTIFICATE (PEM).</summary>
    [Description("Vault object: token-signing certificate")]
    public string? CertObject { get; init; }

    /// <summary>The vault object holding the token-signing private KEY (PEM).</summary>
    [Description("Vault object: token-signing private key")]
    public string? KeyObject { get; init; }

    /// <summary>The vault object holding the registry's HTTP secret (the one every replica must share for upload sessions). Blank → the chart generates none and the registry runs single-replica.</summary>
    [Description("Vault object: registry HTTP secret")]
    public string? HttpSecretObject { get; init; }

    /// <summary>The vault object holding the bearer the registry presents on notifications. Blank → unauthenticated notifications.</summary>
    [Description("Vault object: notification bearer secret")]
    public string? NotificationSecretObject { get; init; }
}

/// <summary>CPU and memory for one side of a resource envelope, as Kubernetes quantities.</summary>
public sealed record ResourceQuantities
{
    /// <summary>CPU quantity (<c>"4"</c>, <c>"500m"</c>).</summary>
    [Description("CPU (e.g. 4 or 500m)")]
    public string? Cpu { get; init; }

    /// <summary>Memory quantity (<c>"8Gi"</c>).</summary>
    [Description("Memory (e.g. 8Gi)")]
    public string? Memory { get; init; }

    /// <summary>True when neither side is stated. Pure.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Cpu) && string.IsNullOrWhiteSpace(Memory);
}

/// <summary>
/// The portal container's RAM/CPU envelope — the standard-rung knob an operator sets first.
/// Requests decide where the pod may be scheduled and what it is guaranteed; limits bound the
/// blast radius on whatever node it lands on. An instance with NO envelope runs BestEffort:
/// unconstrained on the way up and able to OOM the whole node its neighbours share (the memex
/// shape before 2026-08-12). Size the limit for the post-roll NodeType warm-up burst, not for
/// steady state, or every self-update roll OOMs the pod.
/// </summary>
public sealed record ResourceEnvelope
{
    /// <summary>What the pod is guaranteed and scheduled by.</summary>
    [Description("Requests — guaranteed and used for scheduling")]
    public ResourceQuantities? Requests { get; init; }

    /// <summary>The ceiling; memory above it is an OOM kill.</summary>
    [Description("Limits — the ceiling (memory above it is an OOM kill)")]
    public ResourceQuantities? Limits { get; init; }

    /// <summary>True when nothing at all is stated. Pure.</summary>
    public bool IsEmpty => (Requests?.IsEmpty ?? true) && (Limits?.IsEmpty ?? true);
}

/// <summary>
/// KEDA autoscaling of the portal. While it is on, the autoscaler OWNS the replica count and the
/// chart renders no <c>replicas</c> field at all — rendering it would make helm and the HPA fight
/// over one field, and every upgrade would yank a scaled-out deployment back to the chart's
/// number. <see cref="MinReplicas"/> is then the single place the floor is written.
/// </summary>
public sealed record Autoscaling
{
    /// <summary>Whether KEDA owns the replica count.</summary>
    [Description("Autoscale with KEDA")]
    public bool Enabled { get; init; }

    /// <summary>The floor — also the Orleans cluster seed; 2 is the HA minimum.</summary>
    [Description("Minimum replicas (the HA floor; 2 seeds the Orleans cluster)")]
    public int MinReplicas { get; init; } = 2;

    /// <summary>The ceiling under load.</summary>
    [Description("Maximum replicas")]
    public int MaxReplicas { get; init; } = 8;

    /// <summary>% CPU utilisation to scale up past. Null → the chart's 70.</summary>
    [Description("CPU target % (scale up past this)")]
    public int? CpuTarget { get; init; }

    /// <summary>% memory utilisation to scale up past. Null → the chart's 80.</summary>
    [Description("Memory target %")]
    public int? MemoryTarget { get; init; }
}

/// <summary>
/// One persistent volume the portal mounts. <see cref="ClaimName"/> is THE field the chart reads:
/// an entry without one renders NOTHING for content/attachments/workspace, and for data/users it
/// falls back to an <c>emptyDir</c> — which is how a namespace once ran <c>/data</c> ephemeral and
/// every restart wiped the store-landed modules so nothing could ever activate (2026-08-20).
/// Size and storage class describe the claim; they provision nothing by themselves, the PVC
/// pre-exists in the namespace.
/// </summary>
public sealed record VolumeClaim
{
    /// <summary>The volume's role: <c>data</c>, <c>users</c>, <c>content</c>, <c>attachments</c>, <c>workspace</c>, <c>source-replica</c>, <c>postgres</c>.</summary>
    [Description("Role (data, users, content, attachments, workspace, …)")]
    public string Name { get; init; } = "";

    /// <summary>The pre-existing PVC to bind. Blank renders no volume (or an ephemeral one for data/users).</summary>
    [Description("PersistentVolumeClaim name — blank means ephemeral or unmounted")]
    public string? ClaimName { get; init; }

    /// <summary>Where it is mounted in the container.</summary>
    [Description("Mount path")]
    public string? MountPath { get; init; }

    /// <summary>Requested size (<c>"64Gi"</c>).</summary>
    [Description("Size (e.g. 64Gi)")]
    public string? Size { get; init; }

    /// <summary>Storage class. RWX (Azure Files) is a prerequisite for more than one replica.</summary>
    [Description("Storage class (RWX is required above one replica)")]
    public string? StorageClass { get; init; }

    /// <summary><c>ReadWriteMany</c> for anything more than one pod touches.</summary>
    [Description("Access mode (ReadWriteMany for shared volumes)")]
    public string? AccessMode { get; init; }

    /// <summary>
    /// Render the claim itself — the chart then creates the PersistentVolumeClaim named
    /// <see cref="ClaimName"/> (annotated <c>helm.sh/resource-policy: keep</c>, so a later
    /// uninstall leaves the data), which needs <see cref="Size"/> and <see cref="StorageClass"/>;
    /// <see cref="AccessMode"/> defaults to <c>ReadWriteMany</c>.
    ///
    /// <para>🚨 Default false, because existing instances' claims are UNMANAGED by helm — they were
    /// made by hand — and rendering them would fail the next upgrade with
    /// <c>resource already exists</c>. Set it only on a record whose claims do not exist yet: a
    /// brand-new Provision, which without it comes up on emptyDir or with Pending pods, since the
    /// chart renders no claim on its own.</para>
    /// </summary>
    [Description("Create the PersistentVolumeClaim — only on a record whose claims do not exist yet")]
    public bool Create { get; init; }
}

/// <summary>
/// Sticky sessions at the ingress. A Blazor circuit and its per-circuit hubs live on ONE pod
/// (<c>[PreferLocalPlacement]</c>), so a client bouncing between pods loses its session — the
/// cookie pins it. The nginx/AGIC annotation maps are the controller-specific spellings of the
/// same intent; the future manifest generator derives them, the chart today reads only the
/// flat <c>ingress.annotations</c>, so these are recorded intent until it does.
/// </summary>
public sealed record SessionAffinity
{
    /// <summary>Whether sessions are pinned to a pod.</summary>
    [Description("Pin sessions to one pod")]
    public bool Enabled { get; init; } = true;

    /// <summary>The affinity cookie name (<c>MEMEX_AFFINITY</c>).</summary>
    [Description("Affinity cookie name")]
    public string? CookieName { get; init; }

    /// <summary>ingress-nginx annotations expressing the affinity.</summary>
    [Description("nginx annotations")]
    public ImmutableSortedDictionary<string, string> NginxAnnotations { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>Application Gateway annotations expressing the affinity.</summary>
    [Description("Application Gateway annotations")]
    public ImmutableSortedDictionary<string, string> AgicAnnotations { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;
}

/// <summary>
/// How the host reaches the portal: the ingress class, the TLS secret and the annotations the
/// controller needs. The TLS secret is the FLAT field the chart reads — a nested
/// <c>tls: {secretName}</c> block was a dead key that left <c>spec.tls</c> without a secret and had
/// the controller serve ANOTHER host's default certificate (live cert errors, 2026-08-20, twice).
/// The secret pre-exists in the namespace; issuing and renewing it is the TLS step, not this record.
/// </summary>
public sealed record IngressSpec
{
    /// <summary>Ingress class (<c>webapprouting.kubernetes.azure.com</c> on AKS app routing).</summary>
    [Description("Ingress class")]
    public string? ClassName { get; init; }

    /// <summary>The <c>kubernetes.io/tls</c> secret for the host. Blank → <c>{namespace}-tls</c>.</summary>
    [Description("TLS secret name — blank means {namespace}-tls")]
    public string? TlsSecret { get; init; }

    /// <summary>
    /// Controller annotations. The OIDC login callback answers with several large Set-Cookie
    /// headers; a large claims set exceeds nginx's default 4k proxy_buffer_size and the LOGIN
    /// answers 502, deterministically per user (2026-08-23) — hence
    /// <c>proxy-buffer-size: 16k</c> on every fleet instance.
    /// </summary>
    [Description("Ingress annotations")]
    public ImmutableSortedDictionary<string, string> Annotations { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>Sticky sessions. Null → not stated here.</summary>
    [Description("Session affinity")]
    public SessionAffinity? SessionAffinity { get; init; }
}

/// <summary>
/// The Next.js React front end (<c>clients/portal-next</c>), served at <c>/next</c> beside the
/// Blazor portal as its OWN Deployment and Service. When it is on, the chart also routes the
/// <c>/next</c> ingress path to it and renders <c>Portal__ReactAppUrl</c> into the portal's
/// ConfigMap, which is what lights up the per-user "Try the new frontend" toggle. Null → the
/// section renders nothing and the chart's own default (off) stands, so every record that does not
/// mention it deploys exactly as before.
///
/// <para>🚨 WHY IT IS A RECORD FIELD, measured. Until this existed the record could not say it, so
/// on both production portals <c>portal-next</c> ran as CLUSTER-ONLY DRIFT — a hand-applied
/// Deployment and Service with no Helm ownership, on an image tag that had never existed in ACR,
/// and 5+ days of <c>ImagePullBackOff</c> that no repository described because there was nowhere to
/// describe it (Systemorph/Memex#172). The overlays then carried a HAND-WRITTEN <c>portalNext:</c>
/// block under a header claiming the file is generated from this record — so the first re-render
/// would have deleted it and reverted <c>/next</c> to "not deployed", silently
/// (MeshWeaver.Plugins#1408).</para>
///
/// <para>🚨 ENABLED WITHOUT AN IMAGE IS REFUSED. The chart's default image is the LOCAL developer
/// tag (<c>meshweaver/portal-next:local</c>), which no cluster node can pull, so turning the flag
/// on without naming a pullable image is the ImagePullBackOff above arrived at from the record
/// instead of by hand — <c>HelmValues.Problems</c> names it and the render refuses.</para>
/// </summary>
public sealed record PortalNextSpec
{
    /// <summary>Whether the React front end is deployed and the <c>/next</c> path is routed to it.</summary>
    [Description("Deploy the React front end at /next")]
    public bool Enabled { get; init; }

    /// <summary>
    /// The container image, fully qualified with its tag. There is NO derivation from the portal's
    /// repository or tag: this front end is built and versioned on its own line
    /// (<c>memex-portal-next:3.0.0-next.N</c>), so a derived tag would name an image that does not
    /// exist. Blank while <see cref="Enabled"/> is a named problem, never a chart default.
    /// </summary>
    [Description("Container image (repository:tag) — its own release line, never derived")]
    public string? Image { get; init; }

    /// <summary>Replicas. Null → the chart's 1. Stateless by design (every request mints its own token), so it scales with no sticky sessions.</summary>
    [Description("Replicas — blank means the chart's 1")]
    public int? Replicas { get; init; }

    /// <summary>
    /// The origin server-side rendering reaches the portal at, skipping the ingress round-trip.
    /// Blank → the chart's in-cluster Service. The BROWSER always talks same-origin, so this
    /// affects SSR only.
    /// </summary>
    [Description("Portal origin for server-side rendering — blank means the in-cluster Service")]
    public string? PortalOrigin { get; init; }
}

/// <summary>
/// One Key Vault secret the pod reads, by NAME: the configuration key it lands as, and the vault
/// object that holds it. <see cref="VaultSecret"/> may stay blank — it is then DERIVED from the
/// key by the fleet's naming rule <c>{keyVaultSecretPrefix}{Section}-{Key}</c>
/// (<c>Email__ClientSecret</c> → <c>memexcloud-Email-ClientSecret</c>), so a record that follows
/// the convention names each secret exactly once. A legacy object that does not follow it (the
/// unprefixed <c>github-app-privatekey</c>) states its name explicitly. Never a value.
/// </summary>
public sealed record KeyVaultSecretRef
{
    /// <summary>The configuration key the secret lands as — <c>Section__Key</c>, exactly as the portal reads it.</summary>
    [Description("Configuration key it lands as (Section__Key, e.g. Email__ClientSecret)")]
    public string Key { get; init; } = "";

    /// <summary>The vault object's name. Blank → derived from <see cref="Key"/> by the naming rule.</summary>
    [Description("Key Vault secret name — blank derives it as {prefix}{Section}-{Key}")]
    public string? VaultSecret { get; init; }
}

/// <summary>
/// The pod's Key Vault secrets, DECLARED: the vault, the identity that reads it, and the secrets
/// by name. The chart renders the whole path from this one section — the SecretProviderClass
/// (<c>keyVaultSecrets</c> in values → <c>secretproviderclass.yaml</c>), the CSI volume, its
/// mount and the <c>envFrom</c> on the synced Secret — so a declared secret cannot be half-wired
/// and the record is the ONLY place a secret's NAME lives. Values never: the driver fetches them
/// from the vault at pod start.
///
/// <para>Why it exists: until 2026-08-30 every SecretProviderClass in the fleet was a hand-made
/// object, present in no repository, and the record could only point at it by name
/// (<see cref="KeyVaultSecretMount"/>, now the legacy escape hatch). Which secrets a pod carried
/// was knowable only from the cluster — and on the memex install a hand-made Secret patched onto
/// the live Deployment carried a SECOND copy of the Email configuration, in another letter case,
/// so which copy won was decided per pod start. The portal crashed with
/// <c>EmailConfigurationGuard</c>; nothing in any repo could see either half.</para>
///
/// <para>Adopting a hand-made SecretProviderClass: state its live <see cref="Name"/> and
/// <see cref="SyncedSecret"/> here, so <c>helm-release adopt</c> stamps ownership on the existing
/// objects instead of creating a second pair beside them — and drop the matching
/// <c>secretMounts</c> entry in the same change (a mount that names the same volume or class is a
/// named problem, never a silent duplicate).</para>
/// </summary>
public sealed record KeyVaultSecretsSpec
{
    /// <summary>The vault. Blank → the record's <c>keyVault</c>.</summary>
    [Description("Key Vault name — blank means the record's keyVault")]
    public string? VaultName { get; init; }

    /// <summary>The Entra tenant the vault lives in.</summary>
    [Description("Tenant id of the vault")]
    public string? TenantId { get; init; }

    /// <summary>
    /// The client id of the identity that READS the vault — the Key Vault Secrets Provider add-on's
    /// user-assigned identity (<c>az aks show … --query addonProfiles.azureKeyvaultSecretsProvider.identity.clientId</c>),
    /// which needs secret GET on the vault. An identifier, committed on purpose.
    /// </summary>
    [Description("Client id of the identity that reads the vault (the Key Vault Secrets Provider add-on's identity)")]
    public string? IdentityClientId { get; init; }

    /// <summary>The SecretProviderClass's name. Blank → <c>memex-portal-keyvault</c>. State the live name to adopt a hand-made one.</summary>
    [Description("SecretProviderClass name — blank means memex-portal-keyvault")]
    public string? Name { get; init; }

    /// <summary>The Kubernetes Secret the driver syncs into and the pod reads via envFrom. Blank → the SecretProviderClass's name.</summary>
    [Description("Synced Kubernetes Secret name — blank means the SecretProviderClass name")]
    public string? SyncedSecret { get; init; }

    /// <summary>The CSI volume's name. Blank → <c>kv-secrets</c>.</summary>
    [Description("Volume name — blank means kv-secrets")]
    public string? VolumeName { get; init; }

    /// <summary>Where the CSI volume is mounted (the mount is what keeps the sync alive). Blank → <c>/mnt/secrets-store</c>.</summary>
    [Description("Mount path — blank means /mnt/secrets-store")]
    public string? MountPath { get; init; }

    /// <summary>The secrets, by name. Empty renders nothing at all.</summary>
    [Description("The secrets, by name — configuration key and vault object")]
    public ImmutableList<KeyVaultSecretRef> Secrets { get; init; } = ImmutableList<KeyVaultSecretRef>.Empty;
}

/// <summary>
/// ⚠️ LEGACY ESCAPE HATCH — a Key Vault secret set the pod reads through a HAND-MADE
/// SecretProviderClass the record can only point at by name. Declare the secrets under
/// <see cref="KeyVaultSecretsSpec"/> instead: that renders the SecretProviderClass itself, so the
/// record says WHICH secrets, not merely where a cluster object lives. Kept for the transition —
/// all THREE halves still render from the chart: the CSI volume (its mount is what makes the driver
/// sync and rotate), the mount, and the <c>envFrom</c> on the synced Secret. Until 2026-08-23 they
/// were live <c>kubectl</c> patches that the next <c>helm upgrade</c> silently dropped, detaching
/// every AI provider key. The values in that Secret are secrets; this record names the objects only.
/// </summary>
public sealed record KeyVaultSecretMount
{
    /// <summary>The pod volume's name (<c>kv-secrets</c>).</summary>
    [Description("Volume name")]
    public string VolumeName { get; init; } = "";

    /// <summary>The SecretProviderClass in the namespace that maps vault secrets to keys.</summary>
    [Description("SecretProviderClass")]
    public string SecretProviderClass { get; init; } = "";

    /// <summary>The Kubernetes Secret the driver syncs it into (read via envFrom).</summary>
    [Description("Synced Kubernetes Secret name")]
    public string SyncedSecret { get; init; } = "";

    /// <summary>Where the CSI volume is mounted (the mount is what keeps the sync alive).</summary>
    [Description("Mount path")]
    public string MountPath { get; init; } = "";
}

/// <summary>
/// One language-model provider: its endpoint, the model ids it serves, its rank among providers.
/// A stated EMPTY endpoint is meaningful — it is how an instance turns a provider off that the
/// chart or a captured vault value would otherwise configure (AzureFoundry, 2026-08-25). Null
/// means "not stated here", and the projection then writes nothing for it.
/// </summary>
public sealed record ModelProvider
{
    /// <summary>The provider endpoint. "" = explicitly off; null = not stated.</summary>
    [Description("Endpoint — empty turns the provider off")]
    public string? Endpoint { get; init; }

    /// <summary>Model ids, by slot. A blank slot renders blank on purpose.</summary>
    [Description("Models, by slot")]
    public ImmutableList<string> Models { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Rank among providers (0 first). Null → not stated.</summary>
    [Description("Order among providers (0 first)")]
    public int? Order { get; init; }

    /// <summary>Feature-flag for the provider. Null → not stated.</summary>
    [Description("Enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>Which model serves each tier — keyed by RANK, not by kind: Heavy is coding, not reasoning.</summary>
public sealed record ModelTiers
{
    /// <summary>The heavy tier (coding).</summary>
    [Description("Heavy tier model")]
    public string? Heavy { get; init; }

    /// <summary>The standard tier (reasoning).</summary>
    [Description("Standard tier model")]
    public string? Standard { get; init; }

    /// <summary>The light tier (chat).</summary>
    [Description("Light tier model")]
    public string? Light { get; init; }

    /// <summary>The utility tier.</summary>
    [Description("Utility tier model")]
    public string? Utility { get; init; }
}

/// <summary>The instance's AI providers and tier map. Keys ride the Key Vault half; these are endpoints and model ids.</summary>
public sealed record AiProviders
{
    /// <summary>Anthropic (direct or through an Azure AI services /anthropic/ endpoint).</summary>
    [Description("Anthropic")]
    public ModelProvider? Anthropic { get; init; }

    /// <summary>Azure AI Services (the /models endpoint).</summary>
    [Description("Azure AI Services")]
    public ModelProvider? AzureAis { get; init; }

    /// <summary>Azure AI Foundry — the MODEL provider only; embeddings read their own config.</summary>
    [Description("Azure AI Foundry (models only)")]
    public ModelProvider? AzureFoundry { get; init; }

    /// <summary>OpenRouter — the converged frontier catalog through one funded key.</summary>
    [Description("OpenRouter")]
    public ModelProvider? OpenRouter { get; init; }

    /// <summary>The tier map.</summary>
    [Description("Model tiers")]
    public ModelTiers? Tiers { get; init; }
}

/// <summary>
/// Sign-in. Client ids are identifiers and live here; every client SECRET rides the Key Vault
/// half. A stated EMPTY client id is meaningful: the chart's defaults inject CHANGE_ME
/// placeholders for providers an instance never configured, which register half-schemes whose
/// empty secret throws on every request (no pod ever became ready, 2026-08-20) — "" makes the
/// app skip the scheme, null leaves the question to the vault half.
/// </summary>
public sealed record SignInSpec
{
    /// <summary>The authentication provider scheme; <c>Custom</c> on every fleet instance.</summary>
    [Description("Provider (Custom)")]
    public string? Provider { get; init; }

    /// <summary>The built-in developer login. Never on a public instance.</summary>
    [Description("Enable developer login")]
    public bool EnableDevLogin { get; init; }

    /// <summary>Entra app registration client id.</summary>
    [Description("Microsoft client id")]
    public string? MicrosoftClientId { get; init; }

    /// <summary>
    /// Entra tenant. Null → the chart's <c>organizations</c> (multi-tenant). Never "" — an env var
    /// cannot be null, so "" reached the portal as a tenant and the OIDC authority became
    /// <c>login.microsoftonline.com//v2.0</c>; every Microsoft sign-in 500-ed (2026-08-28).
    /// </summary>
    [Description("Microsoft tenant id — blank means multi-tenant")]
    public string? MicrosoftTenantId { get; init; }

    /// <summary>Google OAuth client id. "" = scheme off; null = not stated.</summary>
    [Description("Google client id — empty turns the scheme off")]
    public string? GoogleClientId { get; init; }

    /// <summary>LinkedIn OAuth client id. "" = scheme off; null = not stated.</summary>
    [Description("LinkedIn client id — empty turns the scheme off")]
    public string? LinkedInClientId { get; init; }

    /// <summary>Apple sign-in client id.</summary>
    [Description("Apple client id")]
    public string? AppleClientId { get; init; }
}

/// <summary>
/// System email through Microsoft Graph (client-secret flow). The sender must be a DEDICATED mail
/// app holding <c>Mail.Send</c> as an admin-consented APPLICATION role — the portal's own sign-in
/// app carries only delegated scopes and cannot send in this flow; wiring it here looks complete
/// and delivers nothing. The client secret rides the Key Vault half — declared as
/// <c>Email__ClientSecret</c> under <see cref="KeyVaultSecretsSpec"/>. This section is the WHOLE
/// non-secret half: every <c>Email__*</c> key the chart's ConfigMap renders comes from here (the
/// shared <c>Email__SubscriptionClientState</c> webhook guard is a chart global). On 2026-08-30 the
/// memex install carried a SECOND copy of this section as a hand-made Secret on the live pod, in
/// another letter case, and which copy won was decided per pod start — hence "one home".
/// </summary>
public sealed record EmailSpec
{
    /// <summary>Whether outbound mail is on. Off falls through with almost no signal: one Error line per host start, /health still 200.</summary>
    [Description("Enable system email")]
    public bool Enabled { get; init; }

    /// <summary>The Graph sender app's client id.</summary>
    [Description("Sender app client id")]
    public string? ClientId { get; init; }

    /// <summary>The tenant the sender app lives in.</summary>
    [Description("Tenant id")]
    public string? TenantId { get; init; }

    /// <summary>The virtual inbox (an M365 shared mailbox, not a person).</summary>
    [Description("Mailbox address")]
    public string? MailboxAddress { get; init; }

    /// <summary>Managed identity instead of a client secret.</summary>
    [Description("Use managed identity")]
    public bool UseManagedIdentity { get; init; }

    /// <summary>
    /// Inbound mail through a Graph change-notification subscription on the mailbox. Needs the
    /// <c>Mail.ReadWrite</c> application permission and a public <see cref="WebhookBaseUrl"/>;
    /// on without either, the subscription is never created and inbound silently stays off.
    /// </summary>
    [Description("Enable inbound email (Graph subscription on the mailbox)")]
    public bool InboundEnabled { get; init; }

    /// <summary>The public base URL Graph calls back for notifications; the webhook is <c>{WebhookBaseUrl}/api/email</c>. Null → not stated.</summary>
    [Description("Public webhook base URL (Graph calls {url}/api/email)")]
    public string? WebhookBaseUrl { get; init; }

    /// <summary>Where the inbound triage lane's Forward action sends a mail. Empty makes the action report itself unavailable.</summary>
    [Description("Inbound forward address — empty disables Forward")]
    public string? InboundForwardAddress { get; init; }
}

/// <summary>
/// The GitHub App IDENTITY — identifiers, committed on purpose; the private key is the secret and
/// rides the Key Vault half. These live under the PORTAL config: committed one section up (under
/// the migration) they are never read, the overlay looks safe, and every deploy renders them
/// empty (2026-08-24 and again 2026-08-25, MeshWeaver#2210).
/// </summary>
public sealed record GitHubAppIdentity
{
    /// <summary>The App's client id.</summary>
    [Description("GitHub App client id")]
    public string? ClientId { get; init; }

    /// <summary>The installation id on the owner.</summary>
    [Description("Installation id")]
    public string? InstallationId { get; init; }

    /// <summary>The installation's owner (organisation).</summary>
    [Description("Installation owner")]
    public string? InstallationOwner { get; init; }
}

/// <summary>
/// The instance-lifecycle operator: on the CONTROL instance, and only there. The identity it arms
/// can delete namespaces and drop databases across the fleet — this block on a tenant record
/// would hand that tenant's pod a start-a-cluster-powerful-job token. Off by default is a
/// security property. The environment is named keys (KEY=VALUE reach every operator Job);
/// nothing here is secret.
/// </summary>
public sealed record HostingOperatorSpec
{
    /// <summary>Whether this instance runs lifecycle actions for the fleet.</summary>
    [Description("Run the hosting operator here (the control instance only)")]
    public bool Enabled { get; init; }

    /// <summary>The namespace operator Jobs run in. Blank → <c>memex-ops</c>.</summary>
    [Description("Operator namespace")]
    public string? Namespace { get; init; }

    /// <summary>The service account the Jobs run as. Blank → <c>hosting-operator</c>.</summary>
    [Description("Operator service account")]
    public string? ServiceAccount { get; init; }

    /// <summary>The operator image — THE chart version it deploys, baked in.</summary>
    [Description("Operator image")]
    public string? Image { get; init; }

    /// <summary>Environment for every Job: resource group, DNS group, portal identity, OIDC issuer, ingress IP, paywall URL.</summary>
    [Description("Job environment (KEY=VALUE)")]
    public ImmutableSortedDictionary<string, string> Environment { get; init; }
        = ImmutableSortedDictionary<string, string>.Empty;
}

/// <summary>OpenTelemetry export.</summary>
public sealed record TelemetrySpec
{
    /// <summary>The OTLP collector endpoint.</summary>
    [Description("OTLP endpoint")]
    public string? OtlpEndpoint { get; init; }

    /// <summary>The OTLP protocol (<c>grpc</c>).</summary>
    [Description("OTLP protocol")]
    public string? OtlpProtocol { get; init; }
}

/// <summary>
/// The rollout drain. <see cref="DrainSeconds"/> is the answer to "how long may a user keep working
/// on the OLD build after a roll?": the pod's preStop waits for its last Blazor circuit to close,
/// bounded by this ceiling. 120s cut people off mid-task; the k8s default of 30s SIGKILLed the
/// portal mid-drain with a live Orleans silo, leaving ZOMBIE membership entries that timed out
/// writes mesh-wide. The session drain stops <see cref="ShutdownMarginSeconds"/> short of the
/// ceiling and RETURNS, so SIGTERM is delivered with room to shut down in (#1971).
/// </summary>
public sealed record DrainSpec
{
    /// <summary>Termination grace and the drain ceiling, seconds.</summary>
    [Description("Drain ceiling in seconds (termination grace)")]
    public int DrainSeconds { get; init; } = 1800;

    /// <summary>How much of the ceiling is reserved for the host's own shutdown.</summary>
    [Description("Shutdown margin in seconds")]
    public int ShutdownMarginSeconds { get; init; } = 120;
}

/// <summary>
/// The startup probe budget: <c>periodSeconds × failureThreshold</c> is how long a cold boot may
/// take before the pod is killed. The default 5 × 60 = 5 minutes fits a plain boot. It is what
/// bounds a NodeType BAKE (PreWarm gate) and a cold assembly-cache recycle on an SMB volume —
/// an identity flip on Azure Files exceeded it and looped (2026-08-29). The rollout's
/// progress deadline is DERIVED from this budget by the chart, never a second free number.
/// </summary>
public sealed record StartupProbeSpec
{
    /// <summary>Seconds between probes.</summary>
    [Description("Probe period in seconds")]
    public int PeriodSeconds { get; init; } = 5;

    /// <summary>Seconds one probe may take.</summary>
    [Description("Probe timeout in seconds")]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Failures before the pod is killed.</summary>
    [Description("Failures before the pod is killed")]
    public int FailureThreshold { get; init; } = 60;

    /// <summary>The whole budget in seconds. Pure.</summary>
    public int BudgetSeconds => PeriodSeconds * FailureThreshold;
}

/// <summary>
/// Where the portal keeps its state inside the pod. <see cref="DataRoot"/> holds what MUST survive
/// restarts and be SHARED across replicas (DataProtection keys, the NodeType assembly cache, the
/// NuGet cache) — it is the <c>data</c> volume. Content and users have their own volumes so a
/// heap-sized cache never fills the content share.
/// </summary>
public sealed record StorageLayout
{
    /// <summary>The persistence backend; <c>Filesystem</c> on every fleet instance.</summary>
    [Description("Persistence backend")]
    public string Backend { get; init; } = "Filesystem";

    /// <summary>The shared state root (the data volume's mount).</summary>
    [Description("Data root")]
    public string DataRoot { get; init; } = "/data";

    /// <summary>Where landed modules live. Blank → the data root.</summary>
    [Description("Modules root — blank means the data root")]
    public string? ModulesRoot { get; init; }

    /// <summary>The content collection's base path (the content volume's mount).</summary>
    [Description("Content path")]
    public string ContentPath { get; init; } = "/mnt/content";

    /// <summary>The content collection's name.</summary>
    [Description("Content collection name")]
    public string ContentName { get; init; } = "content";

    /// <summary>The content source type.</summary>
    [Description("Content source type")]
    public string ContentSourceType { get; init; } = "FileSystem";

    /// <summary>The graph store.</summary>
    [Description("Graph storage type")]
    public string GraphStorageType { get; init; } = "PostgreSql";

    /// <summary>The graph store's file path (the filesystem fallback). Blank → <c>{DataRoot}/graph</c>.</summary>
    [Description("Graph storage path — blank means {DataRoot}/graph")]
    public string? GraphStoragePath { get; init; }

    /// <summary>Where Claude Code keeps per-user config (the users volume). Null → not stated.</summary>
    [Description("Claude Code config root (per-user)")]
    public string? ClaudeCodeConfigDirRoot { get; init; }
}

// ─────────────────────────────── the LIVE-ONLY layers ───────────────────────────────
// Two things reach a running pod that no values file renders, and that a record with no slot for
// them cannot compare itself against: the chart's own Secret, whose keys come from the Key Vault
// "values half" an operator captured, and the inline `env:` entries somebody set with `kubectl`.
// Both are DECLARATIVE here — they render nothing. Their whole job is to make the record and the
// cluster comparable without a false diff, and to make an instance's ENV PRECEDENCE readable from
// one file. See HelmValues.EnvPrecedence.

/// <summary>
/// One environment variable set INLINE on the live Deployment's pod spec — the highest-precedence
/// layer there is, above every <c>envFrom</c>.
///
/// <para>🚨 THIS RENDERS NOTHING, DELIBERATELY. An inline entry is out-of-band by construction: the
/// chart never emits one, and <c>helm upgrade</c> does not remove one either — three-way merge
/// removes only what helm previously OWNED, measured 2026-09-03 on helm v3.21.1 and v4.2.4 with a
/// positive control (<c>Doc/Architecture/ChartDriftSemantics</c>). So an inline entry SURVIVES
/// every deploy and keeps outranking the ConfigMap and every synced Secret. Recording it does not
/// create it and dropping it does not delete it; what recording buys is that the record stops
/// silently DISAGREEING with the pod, and that a drift report can subtract what is known and
/// deliberate from what is a surprise.</para>
///
/// <para>🚨 NEVER A CREDENTIAL VALUE. Where the entry carries a secret — <c>memex</c> and
/// <c>memex-cloud</c> each hold a plugin-registry instance key inline, in plaintext, readable by
/// anything that can <c>get deploy</c> — set <see cref="IsSecret"/> and leave <see cref="Value"/>
/// null. <c>HelmValues.Problems</c> REFUSES a record that states both, because these nodes sync to
/// a git repository.</para>
/// </summary>
public sealed record InlineEnvOverride
{
    /// <summary>The environment variable's name, exactly as it appears on the pod (<c>Section__Key</c>).</summary>
    [Description("Environment variable name, as set on the pod")]
    public string Key { get; init; } = "";

    /// <summary>
    /// The value, when it is not a credential. Null means "not recorded" — either because it is a
    /// secret (<see cref="IsSecret"/>) or because nobody has written it down yet; the two are
    /// different and only the first is a good reason.
    /// </summary>
    [Description("Value — omit for a credential")]
    public string? Value { get; init; }

    /// <summary>True when the value is a credential. It is then NEVER recorded here.</summary>
    [Description("The value is a credential and is not recorded here")]
    public bool IsSecret { get; init; }

    /// <summary>
    /// What this entry stands over, if anything: the name of the <c>envFrom</c> source that also
    /// supplies the key (a synced Secret, or <c>memex-portal-config</c> for the chart's ConfigMap).
    /// Blank means the inline entry is the SOLE source — dropping it would EMPTY the key, which is
    /// the opposite of the usual worry and true of more entries than people expect
    /// (<c>PluginCatalog__RegistryUrl</c> on both fleet instances, <c>Features__Ai__Clis__*</c> on
    /// memex, <c>Speech__*</c> and <c>Commerce__BaseUrl</c> on memex-cloud).
    /// </summary>
    [Description("The envFrom source it shadows — blank means it is the only source")]
    public string? Shadows { get; init; }

    /// <summary>
    /// Whether the shadowed source is known to carry the SAME value. <c>true</c> = redundant today
    /// (removing it changes nothing now, but the next change to the other source will silently fail
    /// to take effect); <c>false</c> = the pod runs something no committed file states; null = not
    /// compared, which is the honest answer whenever the other source is a Secret, because a drift
    /// check must not copy the credentials it audits onto a CI runner's disk.
    /// </summary>
    [Description("Does the shadowed source agree? Unset means not compared (a Secret)")]
    public bool? AgreesWithShadowed { get; init; }

    /// <summary>Why it is still there, in one line. An entry with no reason is a to-do, not a decision.</summary>
    [Description("Why it is still set inline")]
    public string? Reason { get; init; }

    /// <summary>What retires it — an issue reference, so the entry has an end rather than a history.</summary>
    [Description("What retires it (issue reference)")]
    public string? RetiredBy { get; init; }
}

/// <summary>
/// A foreign-language GATE sidecar in the portal pod — a participant that reaches the portal's
/// loopback-bound trusted gRPC endpoint, so that reachability across the shared pod network
/// namespace IS the authentication. It executes Code nodes of its language exactly as the
/// in-process Roslyn kernel executes C# ones.
///
/// <para>Recorded because it is a container the instance RUNS: on <c>memex</c> the python and node
/// gates have been in the pod since 2026-08-24 as a hand-applied <c>kubectl patch</c>, declared in
/// no repository, while the chart has supported rendering them from values all along. A record that
/// cannot state them describes an instance with one container where three run.</para>
/// </summary>
public sealed record GateSpec
{
    /// <summary>The language key the chart renders the sidecar under (<c>python</c>, <c>node</c>, <c>pandas</c>).</summary>
    [Description("Language (python, node, pandas)")]
    public string Language { get; init; } = "";

    /// <summary>Whether the gate is included. A gate RUNS when it is enabled AND an image is supplied.</summary>
    [Description("Enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>The sidecar image. Blank ⇒ no sidecar, so a bare install never crash-loops on a missing image.</summary>
    [Description("Sidecar image — blank means the gate does not run")]
    public string? Image { get; init; }

    /// <summary>The mesh address the gate registers at. Blank → the chart's default for the language.</summary>
    [Description("Mesh address — blank means the chart's default")]
    public string? Address { get; init; }
}
