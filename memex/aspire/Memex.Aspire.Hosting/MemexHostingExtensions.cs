using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using MeshWeaver.Deployment;

namespace Aspire.Hosting;

/// <summary>
/// The Memex portal as an Aspire resource, carrying the ONE input every renderer reads: its
/// <see cref="Record"/>, a <see cref="DeploymentContent"/>. The fluent methods on
/// <c>IResourceBuilder&lt;MemexPortalResource&gt;</c> are pure transforms of that record (they
/// delegate to <see cref="DeploymentRecordExtensions"/> one-to-one); the container's image, volumes
/// and environment are DERIVED from it when the application starts, and the same record is what
/// the Helm chart renders from through the Hosting module. Aspire emits a record, never a chart.
/// </summary>
public sealed class MemexPortalResource(string name) : ContainerResource(name)
{
    /// <summary>The Deployment record this portal is built from. Immutable; replaced by every fluent call.</summary>
    public DeploymentContent Record { get; internal set; } = MemexOptions.DefaultRecord(name);

    /// <summary>The one-shot migration container paired with this portal (same tag, always).</summary>
    public ContainerResource? Migration { get; internal set; }
}

/// <summary>
/// Aspire hosting integration for the MeshWeaver Memex portal. <c>builder.AddMemex()</c> wires the
/// full runnable topology — Postgres (pgvector), a one-shot DB migration that gates portal startup,
/// and the portal itself — from the Deployment record: the same <see cref="DeploymentContent"/>
/// the Hosting module renders Helm from, so an AppHost, a Kubernetes namespace and the setup
/// wizard all read one declaration of what an instance IS.
///
/// <para>The record reaches the portal as configuration the way Orleans' Aspire integration hands
/// the silo its clustering: one section injected as environment — <c>Deployment__Record</c>
/// (the whole record as JSON, see <see cref="DeploymentRecordJson"/>) beside the derived per-key
/// surface (<see cref="DeploymentPortalConfig.PortalConfig"/>: <c>Storage__*</c>,
/// <c>PluginCatalog__*</c>, <c>Modules__Required__N</c>, …) — and the portal binds it at boot.
/// The Helm chart renders the same section from the same record, so both routes hand the process
/// an identical configuration surface. The parity table (method → record field → Helm value →
/// config key) is <c>Doc/Architecture/ConfiguringAnInstanceFromAspire</c>.</para>
///
/// <para>🚨 Secrets are never on the record (it syncs to git). In Kubernetes they are Key Vault
/// NAMES on the record and values mounted by the CSI driver; in Aspire they are parameters —
/// <see cref="WithSecret(IResourceBuilder{MemexPortalResource}, string, IResourceBuilder{ParameterResource})"/>.</para>
/// </summary>
public static class MemexHostingExtensions
{
    /// <summary>
    /// Adds the Memex portal — plus its Postgres database and one-shot migration — to the
    /// application model, built from a record: the default record for <paramref name="name"/>
    /// transformed by <paramref name="configure"/>. Chain the fluent methods on the returned builder
    /// for anything the callback did not settle; both are the same pure transforms.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Resource name prefix (default <c>memex</c>); also the record's namespace and Helm release.</param>
    /// <param name="configure">Optional transform of the default record (<see cref="MemexOptions.DefaultRecord"/>).</param>
    public static IResourceBuilder<MemexPortalResource> AddMemex(
        this IDistributedApplicationBuilder builder,
        string name = "memex",
        Func<DeploymentContent, DeploymentContent>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var record = configure?.Invoke(MemexOptions.DefaultRecord(name)) ?? MemexOptions.DefaultRecord(name);
        return builder.AddMemex(name, record);
    }

    /// <summary>Adds the Memex portal from an explicit record — a file handed over, a registry's copy, a test's fixture.</summary>
    public static IResourceBuilder<MemexPortalResource> AddMemex(
        this IDistributedApplicationBuilder builder,
        string name,
        DeploymentContent record)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(record);

        // --- Postgres (pgvector) — mesh data, in every topology. Under Aspire the database is an
        // Aspire resource: Aspire owns its connection string and injects ConnectionStrings__memex,
        // so the record's DatabaseServer/DatabaseHost (a Kubernetes concern) is not rendered here —
        // PortalConfigOptions.Aspire says so explicitly. ---
        var postgres = builder.AddPostgres($"{name}-postgres")
            .WithImage("pgvector/pgvector", "pg17")
            .WithDataVolume($"{name}-pgdata");
        var db = postgres.AddDatabase("memex");
        // Orleans cluster-membership lives on the SAME Postgres server in a SEPARATE database.
        var clusteringDb = postgres.AddDatabase("orleans");

        // --- One-shot DB migration; the portal waits for it to complete (mirrors DbVersionGate) ---
        var (portalImage, portalTag, migrationImage, migrationTag) = Images(record);
        var migration = builder.AddContainer($"{name}-migration", migrationImage, migrationTag)
            .WithReference(db)
            .WithReference(clusteringDb)
            .WaitFor(db)
            .WaitFor(clusteringDb);

        // --- Portal (co-hosted Orleans silo + Blazor web) ---
        var resource = new MemexPortalResource($"{name}-portal") { Record = record, Migration = migration.Resource };
        var portal = builder.AddResource(resource);
        ContainerResourceBuilderExtensions.WithImage(portal, portalImage, portalTag);
        portal
            .WithHttpEndpoint(targetPort: DeploymentPortalConfig.HttpPort(record), name: "http")
            .WithExternalHttpEndpoints()
            .WithReference(db)
            .WithReference(clusteringDb)
            .WaitFor(db)
            .WaitForCompletion(migration);

        // The record as configuration — evaluated when the application starts, so every fluent
        // call made after AddMemex is in it (the Orleans shape: resource → env → options binding).
        portal.WithEnvironment(context =>
        {
            var r = resource.Record;
            context.EnvironmentVariables[DeploymentRecordJson.EnvironmentKey] = DeploymentRecordJson.Write(r);
            foreach (var (key, value) in DeploymentPortalConfig.PortalConfig(r, PortalConfigOptions.Aspire(mcpBaseUrl: null)))
                context.EnvironmentVariables[key] = value;
            // The MCP back-connection URL is the endpoint Aspire allocates (substituted at publish);
            // the derivation above deliberately emits nothing for it (PortalConfigOptions.Aspire).
            context.EnvironmentVariables["Mcp__BaseUrl"] = resource.GetEndpoint("http");
            // Orleans clustering as the feature flag the Distributed host reads.
            context.EnvironmentVariables["Features__Orleans__Clustering"] = DeploymentPortalConfig.OrleansClustering(r);
        });
        // Embeddings — the migration vector-indexes the built-in docs with the same settings the
        // portal embeds search-bar queries with, so the vector dimensions line up.
        migration.WithEnvironment(context =>
        {
            foreach (var (key, value) in resource.Record.ExtraPortalConfig)
                if (key.StartsWith("Embedding__", StringComparison.Ordinal))
                    context.EnvironmentVariables[key] = value;
        });

        // Volumes, images and replicas follow the record; all are re-applied by every fluent call.
        SyncVolumes(portal, resource.Record);
        SyncReplicas(portal, resource.Record);
        return portal;
    }

    /// <summary>Adds the Memex portal from a record FILE (a bare record, or a full mesh node with a <c>content</c> object).</summary>
    public static IResourceBuilder<MemexPortalResource> AddMemexFromFile(
        this IDistributedApplicationBuilder builder, string name, string recordPath) =>
        builder.AddMemex(name, DeploymentRecordJson.ReadFile(recordPath));

    // ── the fluent surface: one-to-one with DeploymentRecordExtensions ─────────────────────────

    /// <summary>
    /// Applies a pure transform to the portal's record and re-syncs what Aspire derives from it
    /// (images, volumes). Every named method below is this with one transform.
    /// </summary>
    public static IResourceBuilder<MemexPortalResource> Configure(
        this IResourceBuilder<MemexPortalResource> portal, Func<DeploymentContent, DeploymentContent> transform)
    {
        var resource = portal.Resource;
        resource.Record = transform(resource.Record);
        var (portalImage, portalTag, migrationImage, migrationTag) = Images(resource.Record);
        // Explicitly Aspire's container extensions — the fluent WithImage/WithVolume above shadow them on this builder type.
        ContainerResourceBuilderExtensions.WithImage(portal, portalImage, portalTag);
        if (resource.Migration is { } migration)
            ContainerResourceBuilderExtensions.WithImage(portal.ApplicationBuilder.CreateResourceBuilder(migration), migrationImage, migrationTag);
        SyncVolumes(portal, resource.Record);
        SyncReplicas(portal, resource.Record);
        return portal;
    }

    /// <summary>The public host (and DNS zone) — <see cref="DeploymentRecordExtensions.WithHost"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithHost(this IResourceBuilder<MemexPortalResource> p, string host, string? dnsZone = null) => p.Configure(r => r.WithHost(host, dnsZone));
    /// <summary>The namespace / Helm release — <see cref="DeploymentRecordExtensions.WithNamespace"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithNamespace(this IResourceBuilder<MemexPortalResource> p, string ns, string? helmRelease = null) => p.Configure(r => r.WithNamespace(ns, helmRelease));
    /// <summary>The cluster — <see cref="DeploymentRecordExtensions.WithCluster"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithCluster(this IResourceBuilder<MemexPortalResource> p, string cluster) => p.Configure(r => r.WithCluster(cluster));
    /// <summary>Owner and purpose — <see cref="DeploymentRecordExtensions.WithOwner"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithOwner(this IResourceBuilder<MemexPortalResource> p, string owner, string? purpose = null) => p.Configure(r => r.WithOwner(owner, purpose));
    /// <summary>The database — <see cref="DeploymentRecordExtensions.WithDatabase"/>. Under Aspire the server is the Aspire Postgres resource; the name is what the record and the migration use.</summary>
    public static IResourceBuilder<MemexPortalResource> WithDatabase(this IResourceBuilder<MemexPortalResource> p, string database, string? server = null, string? username = null, string? host = null, int? port = null, string? connectionSecret = null) => p.Configure(r => r.WithDatabase(database, server, username, host, port, connectionSecret));
    /// <summary>The images — <see cref="DeploymentRecordExtensions.WithImage"/>. Re-tags the portal AND the migration container.</summary>
    public static IResourceBuilder<MemexPortalResource> WithImage(this IResourceBuilder<MemexPortalResource> p, string repository, string? tag = null, string? pullSecret = null, string? migrationRepository = null) => p.Configure(r => r.WithImage(repository, tag, pullSecret, migrationRepository));
    /// <summary>Pin the image tag — <see cref="DeploymentRecordExtensions.WithPinnedImageTag"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithPinnedImageTag(this IResourceBuilder<MemexPortalResource> p, string? tag) => p.Configure(r => r.WithPinnedImageTag(tag));
    /// <summary>Self-update policy — <see cref="DeploymentRecordExtensions.WithUpdatePolicy"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithUpdatePolicy(this IResourceBuilder<MemexPortalResource> p, string policy) => p.Configure(r => r.WithUpdatePolicy(policy));
    /// <summary>A plugin repository mount — <see cref="DeploymentRecordExtensions.WithPluginRepo"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithPluginRepo(this IResourceBuilder<MemexPortalResource> p, string name, string url, string? gitRef = null, bool isRegistrySource = false, string? secretName = null) => p.Configure(r => r.WithPluginRepo(name, url, gitRef, isRegistrySource, secretName));
    /// <summary>Packages a fresh boot seeds — <see cref="DeploymentRecordExtensions.PreInstall"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> PreInstall(this IResourceBuilder<MemexPortalResource> p, params string[] packageIds) => p.Configure(r => r.PreInstall(packageIds));
    /// <summary>Boot modules — <see cref="DeploymentRecordExtensions.WithRequiredModules"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithRequiredModules(this IResourceBuilder<MemexPortalResource> p, params string[] assemblies) => p.Configure(r => r.WithRequiredModules(assemblies));
    /// <summary>One boot module — <see cref="DeploymentRecordExtensions.WithRequiredModule"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithRequiredModule(this IResourceBuilder<MemexPortalResource> p, string assembly) => p.Configure(r => r.WithRequiredModule(assembly));
    /// <summary>A boot module at a slot — <see cref="DeploymentRecordExtensions.WithRequiredModuleSlot"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithRequiredModuleSlot(this IResourceBuilder<MemexPortalResource> p, int slot, string assembly) => p.Configure(r => r.WithRequiredModuleSlot(slot, assembly));
    /// <summary>Replicas — <see cref="DeploymentRecordExtensions.WithReplicas"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithReplicas(this IResourceBuilder<MemexPortalResource> p, int replicas) => p.Configure(r => r.WithReplicas(replicas));
    /// <summary>Orleans clustering — <see cref="DeploymentRecordExtensions.WithOrleansClustering"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithOrleansClustering(this IResourceBuilder<MemexPortalResource> p, string? clustering) => p.Configure(r => r.WithOrleansClustering(clustering));
    /// <summary>Resources — <see cref="DeploymentRecordExtensions.WithResources"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithResources(this IResourceBuilder<MemexPortalResource> p, string? requestsCpu = null, string? requestsMemory = null, string? limitsCpu = null, string? limitsMemory = null) => p.Configure(r => r.WithResources(requestsCpu, requestsMemory, limitsCpu, limitsMemory));
    /// <summary>Autoscaling — <see cref="DeploymentRecordExtensions.WithAutoscaling"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithAutoscaling(this IResourceBuilder<MemexPortalResource> p, bool enabled = true, int? minReplicas = null, int? maxReplicas = null, int? cpuTarget = null, int? memoryTarget = null) => p.Configure(r => r.WithAutoscaling(enabled, minReplicas, maxReplicas, cpuTarget, memoryTarget));
    /// <summary>A volume — <see cref="DeploymentRecordExtensions.WithVolume"/>; under Aspire it is a named Docker volume mounted at the same path.</summary>
    public static IResourceBuilder<MemexPortalResource> WithVolume(this IResourceBuilder<MemexPortalResource> p, string name, string mountPath, string? size = null, string? claimName = null, string? storageClass = null, string? accessMode = null, bool create = false) => p.Configure(r => r.WithVolume(name, mountPath, size, claimName, storageClass, accessMode, create));
    /// <summary>Ingress — <see cref="DeploymentRecordExtensions.WithIngress"/> (a Kubernetes concern; recorded, not applied locally).</summary>
    public static IResourceBuilder<MemexPortalResource> WithIngress(this IResourceBuilder<MemexPortalResource> p, string? className = null, string? tlsSecret = null, IEnumerable<KeyValuePair<string, string>>? annotations = null, bool? sessionAffinity = null, string? affinityCookie = null) => p.Configure(r => r.WithIngress(className, tlsSecret, annotations, sessionAffinity, affinityCookie));
    /// <summary>Startup probe — <see cref="DeploymentRecordExtensions.WithStartupProbe"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithStartupProbe(this IResourceBuilder<MemexPortalResource> p, int? periodSeconds = null, int? timeoutSeconds = null, int? failureThreshold = null) => p.Configure(r => r.WithStartupProbe(periodSeconds, timeoutSeconds, failureThreshold));
    /// <summary>Storage layout — <see cref="DeploymentRecordExtensions.WithStorageLayout"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithStorageLayout(this IResourceBuilder<MemexPortalResource> p, Func<StorageLayout, StorageLayout> configure) => p.Configure(r => r.WithStorageLayout(configure));
    /// <summary>A language gate sidecar — <see cref="DeploymentRecordExtensions.WithGate"/> (recorded; the sidecar container is a Kubernetes concern).</summary>
    public static IResourceBuilder<MemexPortalResource> WithGate(this IResourceBuilder<MemexPortalResource> p, string language, string? image = null, string? address = null, bool enabled = true) => p.Configure(r => r.WithGate(language, image, address, enabled));
    /// <summary>Key Vault name and prefix — <see cref="DeploymentRecordExtensions.WithKeyVault"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithKeyVault(this IResourceBuilder<MemexPortalResource> p, string vault, string? prefix = null) => p.Configure(r => r.WithKeyVault(vault, prefix));
    /// <summary>Key Vault secret class — <see cref="DeploymentRecordExtensions.WithKeyVaultSecrets"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithKeyVaultSecrets(this IResourceBuilder<MemexPortalResource> p, Func<KeyVaultSecretsSpec, KeyVaultSecretsSpec> map, string? vaultName = null, string? tenantId = null, string? identityClientId = null, string? className = null, string? syncedSecret = null, string? volumeName = null, string? mountPath = null) => p.Configure(r => r.WithKeyVaultSecrets(map, vaultName, tenantId, identityClientId, className, syncedSecret, volumeName, mountPath));
    /// <summary>Sign-in — <see cref="DeploymentRecordExtensions.WithSignIn"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithSignIn(this IResourceBuilder<MemexPortalResource> p, string? provider = null, string? microsoftClientId = null, string? microsoftTenantId = null, string? googleClientId = null, string? linkedInClientId = null, string? appleClientId = null, bool? enableDevLogin = null) => p.Configure(r => r.WithSignIn(provider, microsoftClientId, microsoftTenantId, googleClientId, linkedInClientId, appleClientId, enableDevLogin));
    /// <summary>Email — <see cref="DeploymentRecordExtensions.WithEmail"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithEmail(this IResourceBuilder<MemexPortalResource> p, bool enabled = true, string? mailboxAddress = null, string? clientId = null, string? tenantId = null, bool? useManagedIdentity = null, bool? inboundEnabled = null, string? webhookBaseUrl = null, string? inboundForwardAddress = null) => p.Configure(r => r.WithEmail(enabled, mailboxAddress, clientId, tenantId, useManagedIdentity, inboundEnabled, webhookBaseUrl, inboundForwardAddress));
    /// <summary>The LinkedIn app for post publishing — <see cref="DeploymentRecordExtensions.WithSocialLinkedIn"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithSocialLinkedIn(this IResourceBuilder<MemexPortalResource> p, string? clientId) => p.Configure(r => r.WithSocialLinkedIn(clientId));
    /// <summary>GitHub App — <see cref="DeploymentRecordExtensions.WithGitHubApp"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithGitHubApp(this IResourceBuilder<MemexPortalResource> p, string clientId, string installationId, string? installationOwner = null) => p.Configure(r => r.WithGitHubApp(clientId, installationId, installationOwner));
    /// <summary>AI providers — <see cref="DeploymentRecordExtensions.WithAi"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithAi(this IResourceBuilder<MemexPortalResource> p, Func<AiProviders, AiProviders> configure) => p.Configure(r => r.WithAi(configure));
    /// <summary>The hosting operator — <see cref="DeploymentRecordExtensions.WithOperator"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithOperator(this IResourceBuilder<MemexPortalResource> p, bool enabled = true, string? ns = null, string? serviceAccount = null, string? image = null, IEnumerable<KeyValuePair<string, string>>? environment = null) => p.Configure(r => r.WithOperator(enabled, ns, serviceAccount, image, environment));
    /// <summary>Telemetry — <see cref="DeploymentRecordExtensions.WithTelemetry"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithTelemetry(this IResourceBuilder<MemexPortalResource> p, string? otlpEndpoint, string? otlpProtocol = null) => p.Configure(r => r.WithTelemetry(otlpEndpoint, otlpProtocol));
    /// <summary>Idle policy — <see cref="DeploymentRecordExtensions.WithIdlePolicy"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithIdlePolicy(this IResourceBuilder<MemexPortalResource> p, int? suspendAfterDays, int? teardownAfterDays = null) => p.Configure(r => r.WithIdlePolicy(suspendAfterDays, teardownAfterDays));
    /// <summary>A webhook inbox slot — <see cref="DeploymentRecordExtensions.WithWebhookInbox"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithWebhookInbox(this IResourceBuilder<MemexPortalResource> p, string target, string? secretConfigKey = null) => p.Configure(r => r.WithWebhookInbox(target, secretConfigKey));
    /// <summary>One portal configuration key — <see cref="DeploymentRecordExtensions.WithPortalConfig(DeploymentContent, string, string)"/>.</summary>
    public static IResourceBuilder<MemexPortalResource> WithPortalConfig(this IResourceBuilder<MemexPortalResource> p, string key, string value) => p.Configure(r => r.WithPortalConfig(key, value));

    // ── secrets: Aspire parameters, never the record ────────────────────────────────────────────

    /// <summary>
    /// A secret VALUE for one configuration key, from an Aspire parameter (<c>builder.AddParameter(name, secret: true)</c>).
    /// This is the Aspire counterpart of a Key Vault NAME on the record; the record itself never
    /// holds it, and <c>PublishRecord</c> never writes it.
    /// </summary>
    public static IResourceBuilder<MemexPortalResource> WithSecret(
        this IResourceBuilder<MemexPortalResource> portal, string configurationKey, IResourceBuilder<ParameterResource> parameter)
    {
        portal.WithEnvironment(configurationKey.Replace(":", "__"), parameter);
        return portal;
    }

    /// <summary>A secret VALUE for one configuration key, given inline (development only — prefer a parameter).</summary>
    public static IResourceBuilder<MemexPortalResource> WithSecret(
        this IResourceBuilder<MemexPortalResource> portal, string configurationKey, string value)
    {
        portal.WithEnvironment(configurationKey.Replace(":", "__"), value);
        return portal;
    }

    // ── the record leaves the AppHost ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the FINAL record (every fluent call applied) to <paramref name="path"/> when the
    /// application starts — the file a developer hands to the setup wizard or a <c>Provision</c>
    /// action on the control instance. Secrets are never in it, by construction.
    /// </summary>
    public static IResourceBuilder<MemexPortalResource> PublishRecord(
        this IResourceBuilder<MemexPortalResource> portal, string path)
    {
        var resource = portal.Resource;
        portal.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            DeploymentRecordJson.WriteFile(resource.Record, path);
            return Task.CompletedTask;
        });
        return portal;
    }

    // ── derivations ─────────────────────────────────────────────────────────────────────────────

    private static (string PortalImage, string PortalTag, string MigrationImage, string MigrationTag) Images(DeploymentContent record)
    {
        var tag = DeploymentPortalConfig.Tag(record, MemexOptions.DefaultImageTag) ?? MemexOptions.DefaultImageTag;
        var portal = string.IsNullOrWhiteSpace(record.ImageRepository)
            ? $"{MemexOptions.DefaultImageRegistry}/{MemexOptions.DefaultPortalRepository}"
            : record.ImageRepository!.Trim();
        var migration = DeploymentPortalConfig.MigrationImage(record with { ImageRepository = portal }, tag)!;
        var colon = migration.LastIndexOf(':');
        return (portal, tag, migration[..colon], migration[(colon + 1)..]);
    }

    private static void SyncReplicas(IResourceBuilder<MemexPortalResource> portal, DeploymentContent record) =>
        portal.WithAnnotation(new ReplicaAnnotation(record.Replicas is > 0 ? record.Replicas.Value : 1), ResourceAnnotationMutationBehavior.Replace);

    /// <summary>
    /// The container's named volumes ARE the record's volumes: one Docker volume
    /// (<c>{resource}-{volume.Name}</c>) per declared mount path, and a mount derived earlier from
    /// a volume the record no longer declares is removed — the model never diverges from the
    /// record. A mount the caller added through Aspire's own <c>WithVolume</c>/<c>WithBindMount</c>
    /// (a source not carrying this resource's prefix) is left alone.
    /// </summary>
    private static void SyncVolumes(IResourceBuilder<MemexPortalResource> portal, DeploymentContent record)
    {
        var prefix = $"{portal.Resource.Name}-";
        var desired = record.Volumes
            .Where(v => !string.IsNullOrWhiteSpace(v.MountPath))
            .ToDictionary(v => v.MountPath!, v => prefix + v.Name, StringComparer.Ordinal);

        foreach (var stale in portal.Resource.Annotations.OfType<ContainerMountAnnotation>()
                     .Where(m => m.Type == ContainerMountType.Volume && m.Source is { } s && s.StartsWith(prefix, StringComparison.Ordinal))
                     .Where(m => !desired.TryGetValue(m.Target, out var source) || source != m.Source)
                     .ToList())
            portal.Resource.Annotations.Remove(stale);

        var mounted = portal.Resource.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var (mountPath, source) in desired)
            if (!mounted.Contains(mountPath))
                ContainerResourceBuilderExtensions.WithVolume(portal, source, mountPath);
    }
}
