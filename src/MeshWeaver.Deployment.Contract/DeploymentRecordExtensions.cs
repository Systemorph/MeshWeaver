using System.Collections.Immutable;

namespace MeshWeaver.Deployment;

/// <summary>
/// The fluent language over the Deployment record. Every method is a PURE record transform
/// (<c>record with { … }</c>): immutable, composable, usable from an Aspire AppHost, a test, the
/// setup wizard or the template generator alike — and its only output is a
/// <see cref="DeploymentContent"/>, so nothing is configurable fluently that the record cannot
/// hold, by construction (maintainer, 2026-09-08: "create fluent language to configure … the record
/// must be the only input").
///
/// <para>Rules, stated once: every field of the record is reachable here; defaults come from the
/// record's own defaults, never from the builder (a method with no argument given leaves the
/// field as it is); a null argument means "leave unchanged" unless the method's name says
/// otherwise; list-valued fields are APPENDED to (call the <c>Clear…</c> form to start over), map-valued
/// fields are merged key-wise. The Aspire adapter's <c>IResourceBuilder</c> methods of the same
/// names delegate here one-to-one, so the table in
/// <c>Doc/Architecture/ConfiguringAnInstanceFromAspire</c> (method → record field → Helm value →
/// config key) is the parity contract for both.</para>
/// </summary>
public static class DeploymentRecordExtensions
{
    // ── identity ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The public host the portal serves on, and optionally the DNS zone the record's DNS step writes into.</summary>
    public static DeploymentContent WithHost(this DeploymentContent d, string host, string? dnsZone = null) =>
        d with { Host = host, DnsZone = dnsZone ?? d.DnsZone };

    /// <summary>The Kubernetes namespace (conventionally the deployment id) and, optionally, the Helm release name.</summary>
    public static DeploymentContent WithNamespace(this DeploymentContent d, string ns, string? helmRelease = null) =>
        d with { Namespace = ns, HelmRelease = helmRelease ?? d.HelmRelease };

    /// <summary>The cluster the namespace lives on.</summary>
    public static DeploymentContent WithCluster(this DeploymentContent d, string cluster) => d with { Cluster = cluster };

    /// <summary>Who this instance is for and what it is for.</summary>
    public static DeploymentContent WithOwner(this DeploymentContent d, string owner, string? purpose = null) =>
        d with { Owner = owner, Purpose = purpose ?? d.Purpose };

    /// <summary>The lifecycle status word the record carries (<c>Planned</c>, <c>Provisioning</c>, <c>Live</c>, …).</summary>
    public static DeploymentContent WithStatus(this DeploymentContent d, string status) => d with { Status = status };

    /// <summary>Free-form operator notes (markdown). Replaces; use <see cref="AppendNote"/> to add a dated paragraph.</summary>
    public static DeploymentContent WithNotes(this DeploymentContent d, string? notes) => d with { Notes = notes };

    /// <summary>Appends a paragraph to the notes, separated by a blank line.</summary>
    public static DeploymentContent AppendNote(this DeploymentContent d, string paragraph) =>
        d with { Notes = string.IsNullOrWhiteSpace(d.Notes) ? paragraph : d.Notes.TrimEnd() + "\n\n" + paragraph };

    /// <summary>The config repository and path this record is written through to.</summary>
    public static DeploymentContent WithConfigRepository(this DeploymentContent d, string repository, string? configPath = null) =>
        d with { Repository = repository, ConfigPath = configPath ?? d.ConfigPath };

    /// <summary>Grafana base URL for this instance's dashboards.</summary>
    public static DeploymentContent WithGrafana(this DeploymentContent d, string? baseUrl) => d with { GrafanaBaseUrl = baseUrl };

    // ── database ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PostgreSQL database: its name, optionally the server (Azure server name, FQDN derived),
    /// user, explicit host/port, and the Key Vault secret NAME holding the connection string.
    /// </summary>
    public static DeploymentContent WithDatabase(
        this DeploymentContent d,
        string database,
        string? server = null,
        string? username = null,
        string? host = null,
        int? port = null,
        string? connectionSecret = null) =>
        d with
        {
            Database = database,
            DatabaseServer = server ?? d.DatabaseServer,
            DatabaseUsername = username ?? d.DatabaseUsername,
            DatabaseHost = host ?? d.DatabaseHost,
            DatabasePort = port ?? d.DatabasePort,
            DatabaseConnectionSecret = connectionSecret ?? d.DatabaseConnectionSecret,
        };

    /// <summary>Run an in-cluster Postgres (self-host only) instead of a managed server.</summary>
    public static DeploymentContent WithInClusterPostgres(this DeploymentContent d, bool enabled = true) =>
        d with { InClusterPostgres = enabled };

    // ── images ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The portal image repository and, optionally, the tag to PIN (empty = the self-updater
    /// decides), the image pull Secret name (required off ACR) and an explicit migration repository.
    /// </summary>
    public static DeploymentContent WithImage(
        this DeploymentContent d,
        string repository,
        string? tag = null,
        string? pullSecret = null,
        string? migrationRepository = null) =>
        d with
        {
            ImageRepository = repository,
            PinnedImageTag = tag ?? d.PinnedImageTag,
            ImagePullSecret = pullSecret ?? d.ImagePullSecret,
            MigrationImageRepository = migrationRepository ?? d.MigrationImageRepository,
        };

    /// <summary>Pin the image tag (a deliberate hold or a deliberate roll); null unpins.</summary>
    public static DeploymentContent WithPinnedImageTag(this DeploymentContent d, string? tag) => d with { PinnedImageTag = tag };

    /// <summary>Self-update policy name (<c>None</c>, <c>Continuous</c>, <c>Stable</c>).</summary>
    public static DeploymentContent WithUpdatePolicy(this DeploymentContent d, string policy) => d with { UpdatePolicy = policy };

    /// <summary>Minimum interval between self-update rolls (a <see cref="TimeSpan"/> string, e.g. <c>01:00:00</c>).</summary>
    public static DeploymentContent WithMinRollInterval(this DeploymentContent d, string? interval) => d with { MinRollInterval = interval };

    /// <summary>Recycle a node's hub automatically when its NodeType build goes stale.</summary>
    public static DeploymentContent WithAutoRecycleOnStaleBuild(this DeploymentContent d, bool? enabled = true) =>
        d with { AutoRecycleOnStaleBuild = enabled };

    // ── plugin catalog ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mount a plugin repository: a registry to consume (<paramref name="isRegistrySource"/> false)
    /// or, on the registry installation, a git repo to serve. The name is the registry/source name
    /// a bare pre-install id is qualified against.
    /// </summary>
    public static DeploymentContent WithPluginRepo(
        this DeploymentContent d,
        string name,
        string url,
        string? gitRef = null,
        bool isRegistrySource = false,
        string? secretName = null) =>
        d with
        {
            PluginRepos = d.PluginRepos.Add(new PluginRepoMount
            {
                Name = name, Url = url, Ref = gitRef, IsRegistrySource = isRegistrySource, SecretName = secretName,
            }),
        };

    /// <summary>Removes every plugin mount.</summary>
    public static DeploymentContent ClearPluginRepos(this DeploymentContent d) => d with { PluginRepos = ImmutableList<PluginRepoMount>.Empty };

    /// <summary>Packages a fresh boot seeds itself with (bare ids are qualified against the mounts).</summary>
    public static DeploymentContent PreInstall(this DeploymentContent d, params string[] packageIds) =>
        d with { PreInstall = d.PreInstall.AddRange(packageIds) };

    /// <summary>Removes every pre-install.</summary>
    public static DeploymentContent ClearPreInstall(this DeploymentContent d) => d with { PreInstall = ImmutableList<string>.Empty };

    /// <summary>This deployment IS the plugin registry (serves repos, consumes none).</summary>
    public static DeploymentContent AsPluginRegistry(this DeploymentContent d, bool isRegistry = true) => d with { IsPluginRegistry = isRegistry };

    /// <summary>The instance id at the plugin registry (blank = the deployment id).</summary>
    public static DeploymentContent WithRegistryInstanceId(this DeploymentContent d, string? instanceId) => d with { RegistryInstanceId = instanceId };

    // ── modules ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boot modules the image MUST load (assembly file names; <c>.dll</c> appended when omitted).
    /// 🚨 A non-empty list overrides the image's own list BY INDEX — it is the complete set.
    /// </summary>
    public static DeploymentContent WithRequiredModules(this DeploymentContent d, params string[] assemblies) =>
        d with { RequiredModules = d.RequiredModules.AddRange(assemblies) };

    /// <summary>One boot module (see <see cref="WithRequiredModules"/>).</summary>
    public static DeploymentContent WithRequiredModule(this DeploymentContent d, string assembly) => d.WithRequiredModules(assembly);

    /// <summary>Removes every required module.</summary>
    public static DeploymentContent ClearRequiredModules(this DeploymentContent d) =>
        d with { RequiredModules = ImmutableList<string>.Empty, RequiredModuleSlots = ImmutableSortedDictionary<int, string>.Empty };

    /// <summary>A boot module at an explicit slot (a by-index override of the image's list).</summary>
    public static DeploymentContent WithRequiredModuleSlot(this DeploymentContent d, int slot, string assembly) =>
        d with { RequiredModuleSlots = d.RequiredModuleSlots.SetItem(slot, assembly) };

    // ── shape ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Replica count. Two or more implies <c>AdoNet</c> clustering unless stated otherwise.</summary>
    public static DeploymentContent WithReplicas(this DeploymentContent d, int replicas) => d with { Replicas = replicas };

    /// <summary>Orleans clustering provider (<c>Localhost</c>, <c>AdoNet</c>, <c>AzureTables</c>); null derives it from the replica count.</summary>
    public static DeploymentContent WithOrleansClustering(this DeploymentContent d, string? clustering) => d with { OrleansClustering = clustering };

    /// <summary>HTTP port the portal listens on.</summary>
    public static DeploymentContent WithHttpPort(this DeploymentContent d, int port) => d with { HttpPort = port };

    /// <summary>CPU / memory requests and limits (Kubernetes quantities, e.g. <c>4</c>, <c>8Gi</c>).</summary>
    public static DeploymentContent WithResources(
        this DeploymentContent d,
        string? requestsCpu = null, string? requestsMemory = null,
        string? limitsCpu = null, string? limitsMemory = null)
    {
        var r = d.Resources ?? new ResourceEnvelope();
        return d with
        {
            Resources = r with
            {
                Requests = new ResourceQuantities { Cpu = requestsCpu ?? r.Requests?.Cpu, Memory = requestsMemory ?? r.Requests?.Memory },
                Limits = new ResourceQuantities { Cpu = limitsCpu ?? r.Limits?.Cpu, Memory = limitsMemory ?? r.Limits?.Memory },
            },
        };
    }

    /// <summary>Horizontal autoscaling bounds and targets.</summary>
    public static DeploymentContent WithAutoscaling(
        this DeploymentContent d, bool enabled = true, int? minReplicas = null, int? maxReplicas = null,
        int? cpuTarget = null, int? memoryTarget = null)
    {
        var a = d.Autoscaling ?? new Autoscaling();
        return d with
        {
            Autoscaling = a with
            {
                Enabled = enabled,
                MinReplicas = minReplicas ?? a.MinReplicas,
                MaxReplicas = maxReplicas ?? a.MaxReplicas,
                CpuTarget = cpuTarget ?? a.CpuTarget,
                MemoryTarget = memoryTarget ?? a.MemoryTarget,
            },
        };
    }

    /// <summary>
    /// A persistent volume: its name, mount path, size, the claim name (defaults to <c>memex-{name}</c>
    /// when created by the chart), storage class, access mode, and whether the chart CREATES the claim
    /// (a fresh namespace) or binds an existing one.
    /// </summary>
    public static DeploymentContent WithVolume(
        this DeploymentContent d,
        string name,
        string mountPath,
        string? size = null,
        string? claimName = null,
        string? storageClass = null,
        string? accessMode = null,
        bool create = false)
    {
        var existing = d.Volumes.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        var volume = (existing ?? new VolumeClaim { Name = name }) with
        {
            MountPath = mountPath,
            Size = size ?? existing?.Size,
            ClaimName = claimName ?? existing?.ClaimName ?? $"memex-{name}",
            StorageClass = storageClass ?? existing?.StorageClass,
            AccessMode = accessMode ?? existing?.AccessMode,
            Create = create || (existing?.Create ?? false),
        };
        var volumes = existing is null ? d.Volumes.Add(volume) : d.Volumes.Replace(existing, volume);
        return d with { Volumes = volumes };
    }

    /// <summary>Removes every volume.</summary>
    public static DeploymentContent ClearVolumes(this DeploymentContent d) => d with { Volumes = ImmutableList<VolumeClaim>.Empty };

    /// <summary>Ingress class, TLS secret, annotations (merged) and cookie-based session affinity.</summary>
    public static DeploymentContent WithIngress(
        this DeploymentContent d,
        string? className = null,
        string? tlsSecret = null,
        IEnumerable<KeyValuePair<string, string>>? annotations = null,
        bool? sessionAffinity = null,
        string? affinityCookie = null)
    {
        var i = d.Ingress ?? new IngressSpec();
        var merged = i.Annotations;
        foreach (var (k, v) in annotations ?? Enumerable.Empty<KeyValuePair<string, string>>()) merged = merged.SetItem(k, v);
        var affinity = i.SessionAffinity;
        if (sessionAffinity is not null || affinityCookie is not null)
            affinity = (affinity ?? new SessionAffinity()) with
            {
                Enabled = sessionAffinity ?? affinity?.Enabled ?? true,
                CookieName = affinityCookie ?? affinity?.CookieName,
            };
        return d with
        {
            Ingress = i with
            {
                ClassName = className ?? i.ClassName,
                TlsSecret = tlsSecret ?? i.TlsSecret,
                Annotations = merged,
                SessionAffinity = affinity,
            },
        };
    }

    /// <summary>The React front end at <c>/next</c>: on/off, its image and replica count.</summary>
    public static DeploymentContent WithPortalNext(this DeploymentContent d, bool enabled = true, string? image = null, int? replicas = null, string? portalOrigin = null)
    {
        var p = d.PortalNext ?? new PortalNextSpec();
        return d with { PortalNext = p with { Enabled = enabled, Image = image ?? p.Image, Replicas = replicas ?? p.Replicas, PortalOrigin = portalOrigin ?? p.PortalOrigin } };
    }

    /// <summary>Startup probe cadence and the failure threshold (their product is the startup budget).</summary>
    public static DeploymentContent WithStartupProbe(this DeploymentContent d, int? periodSeconds = null, int? timeoutSeconds = null, int? failureThreshold = null)
    {
        var p = d.StartupProbe ?? new StartupProbeSpec();
        return d with
        {
            StartupProbe = p with
            {
                PeriodSeconds = periodSeconds ?? p.PeriodSeconds,
                TimeoutSeconds = timeoutSeconds ?? p.TimeoutSeconds,
                FailureThreshold = failureThreshold ?? p.FailureThreshold,
            },
        };
    }

    /// <summary>Rollout drain: how long a pod keeps serving after the stop signal, and the shutdown margin.</summary>
    public static DeploymentContent WithDrain(this DeploymentContent d, int? drainSeconds = null, int? shutdownMarginSeconds = null)
    {
        var s = d.Drain ?? new DrainSpec();
        return d with { Drain = s with { DrainSeconds = drainSeconds ?? s.DrainSeconds, ShutdownMarginSeconds = shutdownMarginSeconds ?? s.ShutdownMarginSeconds } };
    }

    /// <summary>The storage layout (backend, data root, content path, graph storage, …) as a transform of the current one.</summary>
    public static DeploymentContent WithStorageLayout(this DeploymentContent d, Func<StorageLayout, StorageLayout> configure) =>
        d with { Storage = configure(d.Storage ?? new StorageLayout()) };

    /// <summary>Object storage account name and the secret NAME of its connection string.</summary>
    public static DeploymentContent WithStorageAccount(this DeploymentContent d, string? account, string? connectionSecret = null) =>
        d with { StorageAccount = account, StorageConnectionSecret = connectionSecret ?? d.StorageConnectionSecret };

    /// <summary>A language gate sidecar (<c>python</c>, <c>node</c>, …).</summary>
    public static DeploymentContent WithGate(this DeploymentContent d, string language, string? image = null, string? address = null, bool enabled = true)
    {
        var existing = d.Gates.FirstOrDefault(g => string.Equals(g.Language, language, StringComparison.OrdinalIgnoreCase));
        var gate = (existing ?? new GateSpec { Language = language }) with { Image = image ?? existing?.Image, Address = address ?? existing?.Address, Enabled = enabled };
        return d with { Gates = existing is null ? d.Gates.Add(gate) : d.Gates.Replace(existing, gate) };
    }

    // ── secrets by NAME ─────────────────────────────────────────────────────────────────────────

    /// <summary>The Key Vault this instance's secrets are NAMED in, and the per-instance name prefix.</summary>
    public static DeploymentContent WithKeyVault(this DeploymentContent d, string vault, string? prefix = null) =>
        d with { KeyVault = vault, KeyVaultSecretPrefix = prefix ?? d.KeyVaultSecretPrefix };

    /// <summary>
    /// The chart-owned Key Vault secret class: which vault objects land under which configuration
    /// keys (names only, never values). <paramref name="map"/> receives the class to extend with
    /// <see cref="Map"/>.
    /// </summary>
    public static DeploymentContent WithKeyVaultSecrets(
        this DeploymentContent d,
        Func<KeyVaultSecretsSpec, KeyVaultSecretsSpec> map,
        string? vaultName = null, string? tenantId = null, string? identityClientId = null,
        string? className = null, string? syncedSecret = null, string? volumeName = null, string? mountPath = null)
    {
        var s = d.KeyVaultSecrets ?? new KeyVaultSecretsSpec();
        s = s with
        {
            VaultName = vaultName ?? s.VaultName ?? d.KeyVault,
            TenantId = tenantId ?? s.TenantId,
            IdentityClientId = identityClientId ?? s.IdentityClientId,
            Name = className ?? s.Name,
            SyncedSecret = syncedSecret ?? s.SyncedSecret,
            VolumeName = volumeName ?? s.VolumeName,
            MountPath = mountPath ?? s.MountPath,
        };
        return d with { KeyVaultSecrets = map(s) };
    }

    /// <summary>Maps one configuration key onto a vault object NAME (an object may feed several keys).</summary>
    public static KeyVaultSecretsSpec Map(this KeyVaultSecretsSpec s, string key, string vaultSecret) =>
        s with { Secrets = s.Secrets.Add(new KeyVaultSecretRef { Key = key, VaultSecret = vaultSecret }) };

    /// <summary>An additional (second, third, …) Key Vault secret class.</summary>
    public static DeploymentContent WithKeyVaultSecretClass(this DeploymentContent d, KeyVaultSecretsSpec secretClass) =>
        d with { KeyVaultSecretClasses = d.KeyVaultSecretClasses.Add(secretClass) };

    /// <summary>Keys the chart's own Secret supplies from the Key Vault values half (names only).</summary>
    public static DeploymentContent WithVaultValuesKeys(this DeploymentContent d, params string[] keys) =>
        d with { VaultValuesKeys = d.VaultValuesKeys.AddRange(keys) };

    /// <summary>A legacy hand-made SecretProviderClass mount, by name.</summary>
    public static DeploymentContent WithSecretMount(this DeploymentContent d, string volumeName, string secretProviderClass, string syncedSecret, string mountPath) =>
        d with { SecretMounts = d.SecretMounts.Add(new KeyVaultSecretMount { VolumeName = volumeName, SecretProviderClass = secretProviderClass, SyncedSecret = syncedSecret, MountPath = mountPath }) };

    /// <summary>Records an inline env entry that exists on the live Deployment (declarative — renders nothing).</summary>
    public static DeploymentContent WithInlineEnv(this DeploymentContent d, string key, string? value = null, bool isSecret = false, string? shadows = null, bool? agreesWithShadowed = null, string? reason = null) =>
        d with { InlineEnv = d.InlineEnv.Add(new InlineEnvOverride { Key = key, Value = value, IsSecret = isSecret, Shadows = shadows, AgreesWithShadowed = agreesWithShadowed, Reason = reason }) };

    // ── sign-in, email, integrations ────────────────────────────────────────────────────────────

    /// <summary>Sign-in providers by CLIENT ID (secrets are Key Vault names, mapped separately).</summary>
    public static DeploymentContent WithSignIn(
        this DeploymentContent d,
        string? provider = null,
        string? microsoftClientId = null, string? microsoftTenantId = null,
        string? googleClientId = null, string? linkedInClientId = null, string? appleClientId = null,
        bool? enableDevLogin = null)
    {
        var s = d.SignIn ?? new SignInSpec();
        return d with
        {
            SignIn = s with
            {
                Provider = provider ?? s.Provider,
                MicrosoftClientId = microsoftClientId ?? s.MicrosoftClientId,
                MicrosoftTenantId = microsoftTenantId ?? s.MicrosoftTenantId,
                GoogleClientId = googleClientId ?? s.GoogleClientId,
                LinkedInClientId = linkedInClientId ?? s.LinkedInClientId,
                AppleClientId = appleClientId ?? s.AppleClientId,
                EnableDevLogin = enableDevLogin ?? s.EnableDevLogin,
            },
        };
    }

    /// <summary>Outbound (and optionally inbound) email through Microsoft Graph — the non-secret half.</summary>
    public static DeploymentContent WithEmail(
        this DeploymentContent d,
        bool enabled = true,
        string? mailboxAddress = null, string? clientId = null, string? tenantId = null,
        bool? useManagedIdentity = null, bool? inboundEnabled = null, string? webhookBaseUrl = null, string? inboundForwardAddress = null)
    {
        var e = d.Email ?? new EmailSpec();
        return d with
        {
            Email = e with
            {
                Enabled = enabled,
                MailboxAddress = mailboxAddress ?? e.MailboxAddress,
                ClientId = clientId ?? e.ClientId,
                TenantId = tenantId ?? e.TenantId,
                UseManagedIdentity = useManagedIdentity ?? e.UseManagedIdentity,
                InboundEnabled = inboundEnabled ?? e.InboundEnabled,
                WebhookBaseUrl = webhookBaseUrl ?? e.WebhookBaseUrl,
                InboundForwardAddress = inboundForwardAddress ?? e.InboundForwardAddress,
            },
        };
    }

    /// <summary>The GitHub App identity the instance acts as.</summary>
    public static DeploymentContent WithGitHubApp(this DeploymentContent d, string clientId, string installationId, string? installationOwner = null) =>
        d with { GitHubApp = new GitHubAppIdentity { ClientId = clientId, InstallationId = installationId, InstallationOwner = installationOwner ?? d.GitHubApp?.InstallationOwner } };

    /// <summary>The LinkedIn client id the Social plugin posts with.</summary>
    public static DeploymentContent WithSocialLinkedIn(this DeploymentContent d, string? clientId) => d with { SocialLinkedInClientId = clientId };

    /// <summary>AI providers and model tiers, as a transform of the current block (see the <c>AiProviders</c> helpers).</summary>
    public static DeploymentContent WithAi(this DeploymentContent d, Func<AiProviders, AiProviders> configure) =>
        d with { Ai = configure(d.Ai ?? new AiProviders()) };

    /// <summary>OpenRouter models (and optionally an endpoint / order).</summary>
    public static AiProviders OpenRouter(this AiProviders a, IEnumerable<string> models, string? endpoint = null, int? order = null, bool? enabled = null) =>
        a with { OpenRouter = Provider(a.OpenRouter, models, endpoint, order, enabled) };

    /// <summary>Anthropic models behind an endpoint (an Azure AI Foundry Anthropic endpoint, or Anthropic's own).</summary>
    public static AiProviders Anthropic(this AiProviders a, IEnumerable<string> models, string? endpoint = null, int? order = null, bool? enabled = null) =>
        a with { Anthropic = Provider(a.Anthropic, models, endpoint, order, enabled) };

    /// <summary>Azure AI Foundry models.</summary>
    public static AiProviders AzureFoundry(this AiProviders a, IEnumerable<string> models, string? endpoint = null, int? order = null, bool? enabled = null) =>
        a with { AzureFoundry = Provider(a.AzureFoundry, models, endpoint, order, enabled) };

    /// <summary>Azure AI Services models.</summary>
    public static AiProviders AzureAis(this AiProviders a, IEnumerable<string> models, string? endpoint = null, int? order = null, bool? enabled = null) =>
        a with { AzureAis = Provider(a.AzureAis, models, endpoint, order, enabled) };

    /// <summary>Which model serves each tier.</summary>
    public static AiProviders Tiers(this AiProviders a, string? heavy = null, string? standard = null, string? light = null, string? utility = null)
    {
        var t = a.Tiers ?? new ModelTiers();
        return a with { Tiers = t with { Heavy = heavy ?? t.Heavy, Standard = standard ?? t.Standard, Light = light ?? t.Light, Utility = utility ?? t.Utility } };
    }

    private static ModelProvider Provider(ModelProvider? current, IEnumerable<string> models, string? endpoint, int? order, bool? enabled)
    {
        var p = current ?? new ModelProvider();
        return p with
        {
            Models = p.Models.AddRange(models),
            Endpoint = endpoint ?? p.Endpoint,
            Order = order ?? p.Order,
            Enabled = enabled ?? p.Enabled,
        };
    }

    // ── operations ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The hosting operator this instance runs actions through (the control instance sets it).</summary>
    public static DeploymentContent WithOperator(this DeploymentContent d, bool enabled = true, string? ns = null, string? serviceAccount = null, string? image = null, IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        var o = d.Operator ?? new HostingOperatorSpec();
        var env = o.Environment;
        foreach (var (k, v) in environment ?? Enumerable.Empty<KeyValuePair<string, string>>()) env = env.SetItem(k, v);
        return d with { Operator = o with { Enabled = enabled, Namespace = ns ?? o.Namespace, ServiceAccount = serviceAccount ?? o.ServiceAccount, Image = image ?? o.Image, Environment = env } };
    }

    /// <summary>The container registry this instance HOSTS (the public instance only).</summary>
    public static DeploymentContent WithRegistry(this DeploymentContent d, Func<RegistrySpec, RegistrySpec> configure) =>
        d with { Registry = configure(d.Registry ?? new RegistrySpec()) };

    /// <summary>OTLP telemetry export.</summary>
    public static DeploymentContent WithTelemetry(this DeploymentContent d, string? otlpEndpoint, string? otlpProtocol = null) =>
        d with { Telemetry = new TelemetrySpec { OtlpEndpoint = otlpEndpoint, OtlpProtocol = otlpProtocol ?? d.Telemetry?.OtlpProtocol } };

    /// <summary>Where this instance's database backups go (a <c>Hosting/BackupStore</c> path).</summary>
    public static DeploymentContent WithBackupStore(this DeploymentContent d, string? backupStore) => d with { BackupStore = backupStore };

    /// <summary>Idle policy in days (null = never).</summary>
    public static DeploymentContent WithIdlePolicy(this DeploymentContent d, int? suspendAfterDays, int? teardownAfterDays = null) =>
        d with { IdleSuspendDays = suspendAfterDays, IdleTeardownDays = teardownAfterDays };

    /// <summary>The grace period a suspended instance keeps its data for.</summary>
    public static DeploymentContent WithGracePeriod(this DeploymentContent d, int days) => d with { GracePeriodDays = days };

    /// <summary>A webhook inbox slot: the target path and the configuration key holding its HMAC secret.</summary>
    public static DeploymentContent WithWebhookInbox(this DeploymentContent d, string target, string? secretConfigKey = null) =>
        d with { WebhookInbox = d.WebhookInbox.Add(new WebhookInboxSlot { Target = target, SecretConfigKey = secretConfigKey }) };

    /// <summary>The advanced rung: one portal configuration key (<c>Section__Key</c>) beyond the typed surface.</summary>
    public static DeploymentContent WithPortalConfig(this DeploymentContent d, string key, string value) =>
        d with { ExtraPortalConfig = d.ExtraPortalConfig.SetItem(key, value) };

    /// <summary>Several portal configuration keys at once (merged).</summary>
    public static DeploymentContent WithPortalConfig(this DeploymentContent d, IEnumerable<KeyValuePair<string, string>> entries)
    {
        var map = d.ExtraPortalConfig;
        foreach (var (k, v) in entries) map = map.SetItem(k, v);
        return d with { ExtraPortalConfig = map };
    }
}
