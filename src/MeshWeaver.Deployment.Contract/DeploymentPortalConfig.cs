using System.Collections.Immutable;

namespace MeshWeaver.Deployment;

/// <summary>
/// The PURE derivations every renderer of a <see cref="DeploymentContent"/> shares — what the
/// record MEANS as portal configuration, independent of who renders it: the Helm chart (through the
/// in-mesh Hosting module's <c>HelmValues</c>), the Aspire adapter (a local container), and the
/// portal reading its own record. One place, so a Kubernetes pod and an Aspire container receive
/// the same keys for the same record, and a difference between the two is a bug here rather than
/// a drift between two renderers (the record/overlay gate measured 41 silent differences between
/// two renderers of one intent on 2026-09-06 — this type is why there will not be a third).
///
/// <para>Ported verbatim from the in-mesh <c>InstanceSpec</c> and <c>HelmValues</c> on 2026-09-08;
/// the in-mesh code delegates here. Hub-free, allocation-light, unit-testable without a mesh.</para>
/// </summary>
public static class DeploymentPortalConfig
{
    /// <summary>The configuration key prefix the portal reads its catalog wiring from.</summary>
    public const string CatalogSection = "PluginCatalog";

    /// <summary>The configuration key prefix the portal reads its module policy from.</summary>
    public const string ModulesSection = "Modules";

    /// <summary>Conventional suffix of the vault secret holding the main DB connection string.</summary>
    public const string DatabaseSecretSuffix = "db-connection";

    /// <summary>Conventional suffix of the vault secret holding the storage connection string.</summary>
    public const string StorageSecretSuffix = "storage-connection";

    /// <summary>Azure Database for PostgreSQL host suffix appended to a bare server name.</summary>
    public const string AzurePostgresDomain = ".postgres.database.azure.com";

    /// <summary>The in-cluster Postgres service a record without a server name resolves to.</summary>
    public const string InClusterPostgresService = "memex-postgres-service";

    /// <summary>The portal's in-cluster service name (what <c>Mcp:BaseUrl</c> points at in Kubernetes).</summary>
    public const string PortalService = "memex-portal-service";

    /// <summary>Default self-update spacing when the record states none.</summary>
    public const string DefaultMinRollInterval = "01:00:00";

    // ───────────────────────────────── secret NAMES (never values) ─────────────────────────────

    /// <summary>
    /// The Key Vault secret NAME the main database connection string lives under: the record's
    /// explicit <see cref="DeploymentContent.DatabaseConnectionSecret"/>, else the convention
    /// <c>{KeyVaultSecretPrefix}db-connection</c>. Always a name — the VALUE never appears on any
    /// record. Pure.
    /// </summary>
    public static string DatabaseSecretName(DeploymentContent? record) =>
        !string.IsNullOrWhiteSpace(record?.DatabaseConnectionSecret)
            ? record!.DatabaseConnectionSecret!.Trim()
            : (record?.KeyVaultSecretPrefix ?? "").Trim() + DatabaseSecretSuffix;

    /// <summary>
    /// The Key Vault secret NAME the storage connection string lives under, or null when the
    /// record names no <see cref="DeploymentContent.StorageAccount"/>. Pure.
    /// </summary>
    public static string? StorageSecretName(DeploymentContent? record) =>
        string.IsNullOrWhiteSpace(record?.StorageAccount) ? null
        : !string.IsNullOrWhiteSpace(record!.StorageConnectionSecret)
            ? record.StorageConnectionSecret!.Trim()
            : (record.KeyVaultSecretPrefix ?? "").Trim() + StorageSecretSuffix;

    // ───────────────────────────────── database + ports ────────────────────────────────────────

    /// <summary>Database host: the explicit FQDN, else the Azure server's FQDN, else the in-cluster service.</summary>
    public static string DatabaseHost(DeploymentContent d) =>
        !string.IsNullOrWhiteSpace(d.DatabaseHost) ? d.DatabaseHost!.Trim()
        : !string.IsNullOrWhiteSpace(d.DatabaseServer) ? d.DatabaseServer!.Trim() + AzurePostgresDomain
        : InClusterPostgresService;

    /// <summary>Database port, 5432 unless stated.</summary>
    public static int DatabasePort(DeploymentContent d) => d.DatabasePort ?? 5432;

    /// <summary>Database user, <c>postgres</c> unless stated.</summary>
    public static string DatabaseUsername(DeploymentContent d) =>
        string.IsNullOrWhiteSpace(d.DatabaseUsername) ? "postgres" : d.DatabaseUsername!.Trim();

    /// <summary>Database name, trimmed ("" when unset).</summary>
    public static string DatabaseName(DeploymentContent d) => (d.Database ?? "").Trim();

    /// <summary>The JDBC string the migration uses — host, port and database, never a credential.</summary>
    public static string JdbcConnectionString(DeploymentContent d) =>
        $"jdbc:postgresql://{DatabaseHost(d)}:{DatabasePort(d)}/{DatabaseName(d)}";

    /// <summary>HTTP port the portal listens on, 8080 unless stated.</summary>
    public static int HttpPort(DeploymentContent d) => d.HttpPort ?? 8080;

    /// <summary>
    /// Orleans clustering: the stated provider, else <c>AdoNet</c> for a multi-pod record
    /// (autoscaling or more than one replica), else <c>Localhost</c>.
    /// </summary>
    public static string OrleansClustering(DeploymentContent d) =>
        !string.IsNullOrWhiteSpace(d.OrleansClustering) ? d.OrleansClustering!.Trim()
        : (d.Autoscaling?.Enabled ?? false) || (d.Replicas ?? 1) > 1 ? "AdoNet"
        : "Localhost";

    // ───────────────────────────────── images ──────────────────────────────────────────────────

    /// <summary>
    /// The tag a render resolves to: the record's pin when it states one, else the caller's
    /// resolved tag, else null — a roll never guesses <c>latest</c>.
    /// </summary>
    public static string? Tag(DeploymentContent d, string? resolvedTag) =>
        !string.IsNullOrWhiteSpace(d.PinnedImageTag) ? d.PinnedImageTag!.Trim()
        : !string.IsNullOrWhiteSpace(resolvedTag) ? resolvedTag!.Trim()
        : null;

    /// <summary>The portal image reference, or null when the record names no repository or no tag resolves.</summary>
    public static string? PortalImage(DeploymentContent d, string? resolvedTag)
    {
        var tag = Tag(d, resolvedTag);
        return string.IsNullOrWhiteSpace(d.ImageRepository) || tag is null
            ? null
            : $"{d.ImageRepository!.Trim()}:{tag}";
    }

    /// <summary>
    /// The migration image paired with the portal's: the explicit
    /// <see cref="DeploymentContent.MigrationImageRepository"/>, else the portal repository with
    /// <c>memex-portal-ai</c>/<c>memex-portal</c> replaced by <c>memex-migration</c>. Same tag,
    /// always — a migration from a different build lands the schema half-applied.
    /// </summary>
    public static string? MigrationImage(DeploymentContent d, string? resolvedTag)
    {
        var tag = Tag(d, resolvedTag);
        if (tag is null) return null;
        var repository = !string.IsNullOrWhiteSpace(d.MigrationImageRepository)
            ? d.MigrationImageRepository!.Trim()
            : string.IsNullOrWhiteSpace(d.ImageRepository) ? null
            : d.ImageRepository!.Trim().Replace("memex-portal-ai", "memex-migration").Replace("memex-portal", "memex-migration");
        return repository is null ? null : $"{repository}:{tag}";
    }

    // ───────────────────────────────── modules ─────────────────────────────────────────────────

    /// <summary>
    /// The <c>Modules:Required:N</c> entries for a record's boot modules — trimmed, <c>.dll</c>
    /// appended when the author wrote the bare assembly name, deduplicated case-insensitively,
    /// order preserved. 🚨 These entries override the image's own list BY INDEX (an array key never
    /// appends), so a non-empty list is the COMPLETE required set.
    /// </summary>
    public static ImmutableList<string> ModuleEntries(IEnumerable<string>? requiredModules)
    {
        var names = (requiredModules ?? Enumerable.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Select(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll")
            .ToArray();
        var entries = ImmutableList.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var name in names)
            if (seen.Add(name))
                entries.Add($"{ModulesSection}:Required:{index++}={name}");
        return entries.ToImmutable();
    }

    /// <summary>
    /// The boot-module slots: the contiguous <see cref="ModuleEntries"/> first, then the record's
    /// explicit <see cref="DeploymentContent.RequiredModuleSlots"/> where no contiguous entry took
    /// the slot. Sorted by slot.
    /// </summary>
    public static ImmutableSortedDictionary<int, string> ModuleSlots(DeploymentContent d)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<int, string>();
        var contiguous = ModuleEntries(d.RequiredModules);
        for (var i = 0; i < contiguous.Count; i++)
            builder[i] = contiguous[i][(contiguous[i].IndexOf('=') + 1)..];
        foreach (var (slot, assembly) in d.RequiredModuleSlots)
        {
            if (builder.ContainsKey(slot) || string.IsNullOrWhiteSpace(assembly))
                continue;
            var name = assembly.Trim();
            builder[slot] = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        }
        return builder.ToImmutable();
    }

    // ───────────────────────────────── plugin catalog wiring ───────────────────────────────────

    /// <summary>
    /// The mounts that are actually usable, with their problems collected. A half-configured mount
    /// is REPORTED rather than dropped — dropping it is how an instance comes up empty with nothing
    /// anywhere saying why. Pure.
    /// </summary>
    public static (ImmutableList<PluginRepoMount> Usable, ImmutableList<string> Problems) Validate(
        IEnumerable<PluginRepoMount>? mounts)
    {
        var usable = ImmutableList.CreateBuilder<PluginRepoMount>();
        var problems = ImmutableList.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts ?? Enumerable.Empty<PluginRepoMount>())
        {
            if (mount is null)
                continue;
            if (mount.Problem() is { } problem)
            {
                problems.Add(problem);
                continue;
            }
            if (!seen.Add(mount.NormalizedName))
            {
                problems.Add($"plugin mount '{mount.NormalizedName}' is declared twice");
                continue;
            }
            usable.Add(mount);
        }
        return (usable.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>
    /// The flat <c>Key=Value</c> catalog configuration a new instance is deployed with — consumer
    /// mounts as <c>PluginCatalog:Registries:N:*</c>, registry sources as
    /// <c>PluginCatalog:Sources:N:*</c>, the pre-install seed as <c>PluginCatalog:InstallByDefault:N</c>.
    /// Deterministic and ordered.
    /// </summary>
    public static ImmutableList<string> ConfigurationEntries(
        IEnumerable<PluginRepoMount>? mounts, IEnumerable<string>? preInstall)
    {
        var (usable, _) = Validate(mounts);
        var entries = ImmutableList.CreateBuilder<string>();
        var consumers = usable.Where(m => !m.IsRegistrySource).ToArray();
        for (var i = 0; i < consumers.Length; i++)
        {
            entries.Add($"{CatalogSection}:Registries:{i}:Name={consumers[i].NormalizedName}");
            entries.Add($"{CatalogSection}:Registries:{i}:Url={consumers[i].NormalizedUrl}");
        }
        var sources = usable.Where(m => m.IsRegistrySource).ToArray();
        for (var i = 0; i < sources.Length; i++)
        {
            entries.Add($"{CatalogSection}:Sources:{i}:Name={sources[i].NormalizedName}");
            entries.Add($"{CatalogSection}:Sources:{i}:RepoPath={sources[i].NormalizedUrl}");
            entries.Add($"{CatalogSection}:Sources:{i}:Ref={sources[i].EffectiveRef}");
        }
        var patterns = InstallPatterns(mounts, preInstall);
        for (var i = 0; i < patterns.Count; i++)
            entries.Add($"{CatalogSection}:InstallByDefault:{i}={patterns[i]}");
        return entries.ToImmutable();
    }

    /// <summary>
    /// The <c>InstallByDefault</c> patterns for the declared pre-installs — an unqualified id is
    /// qualified against every usable mount (the catalog fails closed on a bare id); an id that
    /// already carries a source passes through untouched.
    /// </summary>
    public static ImmutableList<string> InstallPatterns(
        IEnumerable<PluginRepoMount>? mounts, IEnumerable<string>? preInstall)
    {
        var ids = (preInstall ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();
        if (ids.Length == 0)
            return ImmutableList<string>.Empty;
        var (usable, _) = Validate(mounts);
        var sourceNames = usable.Select(m => m.NormalizedName).ToArray();
        var patterns = ImmutableList.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (id.Contains('/') || sourceNames.Length == 0)
            {
                if (seen.Add(id)) patterns.Add(id);
                continue;
            }
            foreach (var source in sourceNames)
            {
                var pattern = $"{source}/{id}";
                if (seen.Add(pattern)) patterns.Add(pattern);
            }
        }
        return patterns.ToImmutable();
    }

    /// <summary>
    /// The FULL flat configuration a new instance is born with: the catalog wiring plus the
    /// module policy — one deterministic list, so "what will this instance boot with" is
    /// answerable from the record alone.
    /// </summary>
    public static ImmutableList<string> BootConfigurationEntries(DeploymentContent? record) =>
        ConfigurationEntries(record?.PluginRepos, record?.PreInstall)
            .AddRange(ModuleEntries(record?.RequiredModules));

    /// <summary>
    /// Why the instance spec cannot bring an instance up, or an empty list when it can — the
    /// per-mount problems plus "packages declared for pre-install with nowhere to install from".
    /// </summary>
    public static ImmutableList<string> SpecProblems(
        IEnumerable<PluginRepoMount>? mounts, IEnumerable<string>? preInstall)
    {
        var (usable, problems) = Validate(mounts);
        var builder = problems.ToBuilder();
        var wanted = (preInstall ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (wanted.Length > 0 && usable.Count == 0)
            builder.Add(
                $"{wanted.Length} package(s) are declared for pre-install but the deployment mounts "
                + "no plugin repository — nothing could be installed from anywhere");
        return builder.ToImmutable();
    }

    // ───────────────────────────────── the portal config surface ───────────────────────────────

    /// <summary>
    /// Every portal configuration key a record declares, as the portal reads it
    /// (<c>Section__Key</c>) — THE parity contract. The Helm chart renders exactly this map into the
    /// portal ConfigMap; the Aspire adapter injects exactly this map as container environment,
    /// with two documented substitutions (<see cref="PortalConfigOptions.DatabaseKeys"/> and
    /// <see cref="PortalConfigOptions.McpBaseUrl"/>), so both routes hand the process the same
    /// surface. Typed keys win over <see cref="DeploymentContent.ExtraPortalConfig"/>,
    /// case-insensitively.
    /// </summary>
    public static SortedDictionary<string, string> PortalConfig(DeploymentContent d, PortalConfigOptions? options = null)
    {
        options ??= PortalConfigOptions.Helm;
        var c = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Set(string key, string? value) { if (value is not null) c[key] = value; }
        void SetBool(string key, bool? value) { if (value is bool b) c[key] = b ? "true" : "false"; }

        if (options.DatabaseKeys)
        {
            Set("MEMEX_DATABASENAME", DatabaseName(d));
            Set("MEMEX_HOST", DatabaseHost(d));
            Set("MEMEX_JDBCCONNECTIONSTRING", JdbcConnectionString(d));
            Set("MEMEX_PORT", DatabasePort(d).ToString());
            Set("MEMEX_USERNAME", DatabaseUsername(d));
        }
        Set("ASPNETCORE_HTTP_PORTS", HttpPort(d).ToString());
        Set("Mcp__BaseUrl", options.McpBaseUrl ?? $"http://{PortalService}:{HttpPort(d)}");

        var s = d.Storage ?? new StorageLayout();
        Set("Deployment__Backend", s.Backend);
        Set("Deployment__DataRoot", s.DataRoot);
        Set("Modules__Root", string.IsNullOrWhiteSpace(s.ModulesRoot) ? s.DataRoot : s.ModulesRoot!.Trim());
        Set("Graph__Storage__Type", s.GraphStorageType);
        Set("Graph__Storage__BasePath", string.IsNullOrWhiteSpace(s.GraphStoragePath)
            ? s.DataRoot.TrimEnd('/') + "/graph" : s.GraphStoragePath!.Trim());
        Set("Storage__Name", s.ContentName);
        Set("Storage__SourceType", s.ContentSourceType);
        Set("Storage__BasePath", s.ContentPath);
        Set("ClaudeCode__ConfigDirRoot", s.ClaudeCodeConfigDirRoot);

        Set("Deployment__Orleans__Clustering", OrleansClustering(d));
        Set("SelfUpdate__MinRollInterval", string.IsNullOrWhiteSpace(d.MinRollInterval) ? DefaultMinRollInterval : d.MinRollInterval!.Trim());
        SetBool("Modules__AutoRecycleOnStaleBuild", d.AutoRecycleOnStaleBuild);
        foreach (var (slot, assembly) in ModuleSlots(d))
            Set($"Modules__Required__{slot}", assembly);

        // The catalog wiring rides with the record on BOTH routes, but the chart delivers it through
        // the operator's catalog config file (BootConfigurationEntries → HOSTING_CATALOG_CONFIG)
        // rather than the portal ConfigMap, so it is emitted here only for a renderer with no second
        // file — the Aspire adapter.
        if (options.IncludeBootEntries)
            foreach (var entry in ConfigurationEntries(d.PluginRepos, d.PreInstall))
            {
                var eq = entry.IndexOf('=');
                Set(entry[..eq].Replace(":", "__"), entry[(eq + 1)..]);
            }

        Set("OTEL_EXPORTER_OTLP_ENDPOINT", d.Telemetry?.OtlpEndpoint);
        Set("OTEL_EXPORTER_OTLP_PROTOCOL", d.Telemetry?.OtlpProtocol);

        Set("GitHub__App__ClientId", d.GitHubApp?.ClientId);
        Set("GitHub__App__InstallationId", d.GitHubApp?.InstallationId);
        Set("GitHub__App__InstallationOwner", d.GitHubApp?.InstallationOwner);

        if (d.SignIn is { } signIn)
        {
            Set("Authentication__Provider", string.IsNullOrWhiteSpace(signIn.Provider) ? "Custom" : signIn.Provider!.Trim());
            SetBool("Authentication__EnableDevLogin", signIn.EnableDevLogin);
            Set("Authentication__Microsoft__ClientId", signIn.MicrosoftClientId);
            Set("Authentication__Microsoft__TenantId", signIn.MicrosoftTenantId);
            Set("Authentication__Google__ClientId", signIn.GoogleClientId);
            Set("Authentication__LinkedIn__ClientId", signIn.LinkedInClientId);
            Set("Authentication__Apple__ClientId", signIn.AppleClientId);
        }
        Set("Social__LinkedIn__ClientId", d.SocialLinkedInClientId);

        if (d.Ai is { } ai)
        {
            Provider(c, "Anthropic", ai.Anthropic, "Features__Ai__Providers__Anthropic");
            Provider(c, "AzureAIS", ai.AzureAis, null);
            Provider(c, "AzureFoundry", ai.AzureFoundry, "Features__Ai__Providers__AzureFoundry");
            Provider(c, "OpenRouter", ai.OpenRouter, null);
            Set("ModelTier__Heavy", ai.Tiers?.Heavy);
            Set("ModelTier__Standard", ai.Tiers?.Standard);
            Set("ModelTier__Light", ai.Tiers?.Light);
            Set("ModelTier__Utility", ai.Tiers?.Utility);
        }

        if (d.Email is { } email)
        {
            SetBool("Email__Enabled", email.Enabled);
            Set("Email__ClientId", email.ClientId);
            Set("Email__TenantId", email.TenantId);
            Set("Email__MailboxAddress", email.MailboxAddress);
            SetBool("Email__UseManagedIdentity", email.UseManagedIdentity);
            SetBool("Email__InboundEnabled", email.InboundEnabled);
            Set("Email__WebhookBaseUrl", email.WebhookBaseUrl);
            Set("Email__Inbound__ForwardAddress", email.InboundForwardAddress);
        }

        if (d.WebhookInbox.Count > 0)
            for (var i = 0; i < d.WebhookInbox.Count; i++)
            {
                Set($"WebhookInbox__Targets__{i}", d.WebhookInbox[i].Target);
                c[$"WebhookInbox__Targets__{i}__SecretConfigKey"] = d.WebhookInbox[i].SecretConfigKey ?? "";
            }
        else
            for (var i = 0; i < d.WebhookInboxTargets.Count; i++)
                Set($"WebhookInbox__Targets__{i}", d.WebhookInboxTargets[i]);

        if (d.Operator is { Enabled: true })
            Set("Hosting__Operator__Enabled", "true");

        foreach (var (key, value) in d.ExtraPortalConfig)
            if (!c.Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                c[key] = value ?? "";

        return c;
    }

    private static void Provider(SortedDictionary<string, string> c, string section, ModelProvider? p, string? flagKey)
    {
        if (p is null) return;
        if (p.Endpoint is not null) c[$"{section}__Endpoint"] = p.Endpoint;
        for (var i = 0; i < p.Models.Count; i++) c[$"{section}__Models__{i}"] = p.Models[i] ?? "";
        if (p.Order is int order) c[$"{section}__Order"] = order.ToString();
        if (flagKey is not null && p.Enabled is bool enabled) c[flagKey] = enabled ? "true" : "false";
    }
}

/// <summary>
/// The two places the two renderers legitimately differ, stated as options so the difference is
/// declared rather than discovered: in Kubernetes the database is a server the record names and
/// the portal is reached through its Service; under Aspire the database is an Aspire resource
/// whose connection string Aspire injects (<c>ConnectionStrings__memex</c>) and the portal's URL
/// is the endpoint Aspire allocates.
/// </summary>
/// <param name="DatabaseKeys">Emit the <c>MEMEX_*</c> database keys (Helm: yes; Aspire: no — the Postgres resource supplies them).</param>
/// <param name="McpBaseUrl">The MCP back-connection URL to emit; null = the in-cluster portal Service.</param>
/// <param name="IncludeBootEntries">Emit the plugin-catalog wiring (<c>PluginCatalog__*</c>) as portal keys (Aspire: yes; Helm: no — the operator's catalog config file carries the same entries).</param>
public sealed record PortalConfigOptions(bool DatabaseKeys, string? McpBaseUrl, bool IncludeBootEntries)
{
    /// <summary>The chart's view: database keys from the record, the in-cluster Service as the MCP base URL, catalog wiring via the catalog file.</summary>
    public static PortalConfigOptions Helm { get; } = new(DatabaseKeys: true, McpBaseUrl: null, IncludeBootEntries: false);

    /// <summary>The Aspire view: no database keys (the Postgres resource injects them); the MCP base URL is the allocated endpoint the adapter supplies; catalog wiring as env.</summary>
    public static PortalConfigOptions Aspire(string? mcpBaseUrl) => new(DatabaseKeys: false, McpBaseUrl: mcpBaseUrl ?? "", IncludeBootEntries: true);
}
