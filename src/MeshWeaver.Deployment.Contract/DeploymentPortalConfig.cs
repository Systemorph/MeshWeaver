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

    /// <summary>
    /// The key a record states with that its own <c>Modules:Required</c> entries are the COMPLETE
    /// required set for the instance — see <see cref="DeploymentContent.RequiredModulesAuthoritative"/>.
    /// The reader is <c>MeshWeaver.Mesh.MeshBuilderModuleActivation.RequiredIsAuthoritativeKey</c>;
    /// the string is repeated rather than shared because this assembly deliberately carries ZERO
    /// MeshWeaver references (it ships inside the published Aspire package), and
    /// <c>RequiredModuleAuthorityTest</c> holds the two spellings together.
    /// </summary>
    public const string RequiredIsAuthoritativeKey = $"{ModulesSection}:RequiredIsAuthoritative";

    /// <summary>
    /// The HIGHEST <c>Modules:Required:N</c> index the HELM CHART renders. It names every key
    /// literally (no <c>range</c>, which the key-literal guards cannot see), so the list has a
    /// hand-written ceiling and a slot above it reaches no container IN KUBERNETES.
    ///
    /// <para>🚨 <b>A chart property, not a contract one.</b> The Aspire route has no such ceiling —
    /// it injects whatever <see cref="PortalConfig"/> emits as container environment — so a slot
    /// above this delivers correctly on a laptop and vanishes in the cluster, which is precisely why
    /// it is worth naming rather than a reason to stay quiet. <see cref="ChartModuleSlotProblems"/>
    /// is scoped to the chart for the same reason: a route-neutral "the spec cannot come up" surface
    /// that carried it would be false for an Aspire run.</para>
    ///
    /// <para>🚨 And the consequence is worse than it was. An unrendered slot used to mean "the
    /// image's entry at that index stands", which is merely wrong; under
    /// <see cref="RequiredIsAuthoritativeKey"/> it means the module is NOT REQUIRED AT ALL, because
    /// the claim excludes the image's list. <c>RequiredModuleAuthorityTest</c> holds this number to
    /// the chart's actual block — a constant that drifts from the template is the Memex#128/#131
    /// shape wearing a different hat.</para>
    /// </summary>
    public const int MaxChartRenderedRequiredModuleSlot = 19;

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
    /// order preserved.
    ///
    /// <para>🚨 <b>These entries override the image's own list BY INDEX, and an indexed override
    /// replaces only the entries it NAMES.</b> The tail of a longer list stays exactly where it
    /// was, so this list is NOT the complete required set on its own (#4476): a list shorter than
    /// the image's leaves the image's remaining entries required, and an EMPTY list renders nothing
    /// at all, so the image's list stands in full — emptying a record's
    /// <see cref="DeploymentContent.RequiredModules"/> does not relax the requirement, it restores
    /// it. Neither this renderer nor the record can know how long the image's list is; the image
    /// ships from another repository and has already grown from seven entries to nine underneath a
    /// record that picked "the first free slot".</para>
    ///
    /// <para>A record says "these and only these" with
    /// <see cref="DeploymentContent.RequiredModulesAuthoritative"/>, which renders
    /// <see cref="RequiredIsAuthoritativeKey"/> beside the entries. That is a SCALAR key, so it
    /// cannot be index-merged away, and it is what makes the empty list mean "require nothing".</para>
    /// </summary>
    public static ImmutableList<string> ModuleEntries(IEnumerable<string>? requiredModules)
    {
        var names = (requiredModules ?? Enumerable.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(WithDllSuffix)
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
            builder[slot] = WithDllSuffix(assembly);
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
    public static ImmutableList<string> BootConfigurationEntries(DeploymentContent? record)
    {
        var entries = ConfigurationEntries(record?.PluginRepos, record?.PreInstall);
        if (record is null)
            return entries;

        // 🚨 ModuleSlots, NOT ModuleEntries: the same slots PortalConfig emits. This route used to
        // render the contiguous list alone and drop every explicit RequiredModuleSlots entry, so
        // the two renderers of ONE record described different required sets — the drift this type
        // exists to make impossible. Harmless while both were read as a by-index OVERLAY; with the
        // claim beside them the two routes would state two different COMPLETE sets, and the one
        // that dropped a slot would say a module is not required at all.
        foreach (var (slot, assembly) in ModuleSlots(record))
            entries = entries.Add($"{ModulesSection}:Required:{slot}={assembly}");

        // The authority claim rides with the entries it qualifies, on BOTH routes — an entry list
        // delivered without it reads as a by-index overlay, which is what it was before #4476.
        return record.RequiredModulesAuthoritative
            ? entries.Add($"{RequiredIsAuthoritativeKey}=true")
            : entries;
    }

    /// <summary>
    /// The required-module slots this record declares that the HELM CHART does not render — a slot
    /// above <see cref="MaxChartRenderedRequiredModuleSlot"/> or below 0, neither of which its
    /// literal-key block carries. Empty when every slot is inside the block.
    ///
    /// <para>🚨 <b>Scoped to the chart on purpose, and NOT folded into
    /// <see cref="SpecProblems(IEnumerable{PluginRepoMount}, IEnumerable{string})"/>.</b> The Aspire
    /// route injects whatever <see cref="PortalConfig"/> emits, ceiling and all, so it delivers such
    /// a slot correctly — a route-neutral "why the spec cannot come up" answer that named it would
    /// be false for an Aspire run. A Helm renderer asks this ALONGSIDE the spec problems; anything
    /// else must not.</para>
    ///
    /// <para>Reported rather than dropped, for the same reason <see cref="Validate"/> reports a
    /// half-configured mount: a slot the chart does not carry is invisible at deploy time — helm
    /// succeeds, the ConfigMap is well-formed, and the module is quietly not required. It is also
    /// the nastiest shape of all, because it WORKS under Aspire and disappears in the cluster.
    /// Pure.</para>
    /// </summary>
    public static ImmutableList<string> ChartModuleSlotProblems(DeploymentContent? record)
    {
        if (record is null)
            return ImmutableList<string>.Empty;
        var problems = ImmutableList.CreateBuilder<string>();
        foreach (var (slot, assembly) in ModuleSlots(record))
        {
            if (slot > MaxChartRenderedRequiredModuleSlot)
                problems.Add(
                    $"required module '{assembly}' is declared at slot {slot}, above the highest "
                    + $"slot the Helm chart renders ({MaxChartRenderedRequiredModuleSlot}) — in "
                    + "Kubernetes it would reach no container, so the module would not be required "
                    + "at all (the Aspire route delivers it, so this works locally and disappears "
                    + "in the cluster). Raise the chart's Modules__Required__N block (one hasKey "
                    + "entry per index) and MaxChartRenderedRequiredModuleSlot together, or move "
                    + "the entry into the contiguous requiredModules list.");
            // 🚨 The SAME asymmetry at the other end, and the same remedy shape. A negative slot is
            // not an unbound entry: the reader enumerates the section's CHILDREN rather than
            // binding a CLR array, so an injected `Modules:Required:-1` is returned and the module
            // IS required under Aspire. The chart's literal-key block starts at 0, so in Kubernetes
            // the key reaches no container and the module is required by nobody — works locally,
            // disappears in the cluster, exactly like a slot above the ceiling. Raising the ceiling
            // cannot fix this one, so the remedy names the only two that can.
            else if (slot < 0)
                problems.Add(
                    $"required module '{assembly}' is declared at slot {slot}, below the lowest slot "
                    + "the Helm chart renders (0) — in Kubernetes it would reach no container, so "
                    + "the module would not be required at all (the Aspire route delivers it, "
                    + "because the reader enumerates the section's children rather than binding an "
                    + "array — so this works locally and disappears in the cluster). Move the entry "
                    + "into the contiguous requiredModules list — or give it a slot that is FREE "
                    + "(past that list, whose entries win at the indices they occupy, so a slot "
                    + "inside it is dropped just as silently) and no higher than "
                    + $"{MaxChartRenderedRequiredModuleSlot}.");
        }
        return problems.ToImmutable();
    }

    /// <summary>
    /// Why this record's POSITIONAL boot-module slots cannot be trusted, or an empty list when it
    /// declares none — the ROUTE-NEUTRAL half, wrong under Aspire exactly as in the cluster.
    ///
    /// <para>🚨 <b>A slot names an index in an array whose OTHER HALF this record cannot read.</b>
    /// <c>Modules:Required</c> merges by index, so slot N means "replace whatever the image's own
    /// list holds at N" — and the image's list lives in another repository, versions on its own
    /// schedule and is not given to the record at render time. The advice that produced every one
    /// of these slots ("put it at the first free index") is therefore a measurement of a list the
    /// record does not own, taken once and silently invalidated by the next append.</para>
    ///
    /// <para>🚨 <b>It has already happened twice, on the same instance.</b> Memex#131:
    /// <c>Modules__Required__5</c> named MCP over an image whose index 5 had become
    /// <c>MeshWeaver.Social.dll</c>. Memex#378, measured 2026-09-16 and unchanged at record v92 on
    /// 2026-09-17: <c>requiredModuleSlots {"7": "MeshWeaver.Mcp.dll"}</c> over an image whose list
    /// has grown from seven entries to nine, so index 7 is now
    /// <c>MeshWeaver.Markdown.Collaboration.dll</c> — the collaboration pack is REQUIRED BY NOBODY
    /// on the public instance, and a pod that never landed it reports Healthy and rolls out green.
    /// Neither shadow was visible: the module is not MISSING (nothing asks for it), so the
    /// readiness contract has nothing to say, and the override is rendered, so the key-coverage
    /// gate passes.</para>
    ///
    /// <para><b>The rule, and why it is this one.</b> A positional slot is sound only where the
    /// record owns the WHOLE index space — which is exactly what
    /// <see cref="DeploymentContent.RequiredModulesAuthoritative"/> claims (#4476/#4483). Under the
    /// claim the image's list does not apply at any index, so no entry of it can be shadowed and a
    /// slot is merely a position in the record's own set. Without the claim the slot's meaning is
    /// whatever the image happened to ship that day, and no amount of care in the record can fix
    /// that — so this reports it rather than waiting for the next append to make it wrong
    /// again.</para>
    ///
    /// <para>🚨 <b>This is a rule with no production caller yet</b>, exactly like
    /// <see cref="ChartModuleSlotProblems"/> beside it: the one renderer that asks a record "why
    /// can you not be deployed" is <c>HelmValues.Problems</c> in MeshWeaver.Plugins, and it asks
    /// neither. Until it does, both are pinned here and reach no deploy — stated so the next reader
    /// does not mistake a defined surface for an enforced one.</para>
    ///
    /// <para>A slot BELOW ZERO is NOT this surface's business, and measuring said so: the reader
    /// enumerates <c>GetSection("Modules:Required").GetChildren()</c> rather than binding a CLR
    /// array, so an injected <c>Modules:Required:-1</c> IS returned and the module IS required on
    /// the Aspire route. It is the chart that drops it — the literal-key block starts at 0 — which
    /// makes it the ceiling's question at the other end, and <see cref="ChartModuleSlotProblems"/>
    /// reports it there.</para>
    ///
    /// <para>Pure.</para>
    /// </summary>
    public static ImmutableList<string> PositionalModuleSlotProblems(DeploymentContent? record)
    {
        if (record is null || record.RequiredModuleSlots.IsEmpty)
            return ImmutableList<string>.Empty;

        if (record.RequiredModulesAuthoritative)
            return ImmutableList<string>.Empty;

        var problems = ImmutableList.CreateBuilder<string>();

        // 🚨 The RENDERED slots, never the raw map. <see cref="ModuleSlots"/> drops a blank entry
        // and drops an explicit slot the contiguous list already occupies, so the raw map contains
        // entries that emit no key at all — and a problem saying "the key IS rendered" about one of
        // those would be false where it matters most, in the sentence explaining why nothing else
        // reports this. Above the contiguous count is exactly the explicit half that survived: the
        // list's own entries occupy 0..count-1 and nothing else.
        var contiguous = ModuleEntries(record.RequiredModules).Count;
        foreach (var (slot, assembly) in ModuleSlots(record).Where(entry => entry.Key >= contiguous))
            problems.Add(
                $"required module '{WithDllSuffix(assembly)}' is declared at SLOT {slot}, and this record "
                + "does not claim the complete set — so the slot replaces whatever the IMAGE's own "
                + $"{ModulesSection}:Required list holds at index {slot}, which this record cannot "
                + "read and which versions on its own schedule (it has already grown from seven "
                + "entries to nine underneath a slot chosen as 'the first free index', twice: "
                + "Memex#131 and Memex#378). The replaced module is then required by nobody, which "
                + "nothing reports — it is not missing, so readiness is silent, and the key IS "
                + "rendered, so coverage passes. State the complete set in requiredModules and set "
                + "requiredModulesAuthoritative, after which no index of the image's list applies "
                + "and a slot can shadow nothing.");
        return problems.ToImmutable();
    }

    /// <summary>
    /// A stated module name as the render writes it — trimmed, with the <c>.dll</c> suffix added
    /// when it is not already there. ONE rule, asked by every place that turns a record's word into
    /// a <c>Modules:Required</c> value, so a record cannot be normalized one way into the entries
    /// and another into a problem that names it. Pure.
    /// </summary>
    private static string WithDllSuffix(string? stated)
    {
        var name = (stated ?? "").Trim();
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
    }

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
        Set("Mcp__BaseUrl", options.McpBaseUrl ?? (options.InClusterMcpBaseUrl ? $"http://{PortalService}:{HttpPort(d)}" : null));

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
        // 🚨 What a NEW instance STARTS with (maintainer 2026-09-19: "need to put this to the config
        // where we start"). The record's platform policy and pattern render as the self-updater's
        // SEED keys: the first creation of Admin/UpdatePolicy copies them, an existing node is never
        // touched. Absent renders nothing, and nothing is the chart's own default (Stable, no
        // pattern) — a record that says nothing must not narrow or widen what the image ships.
        // The value binds to an ENUM on the pod (SelfUpdateOptions.DefaultPolicy): a misspelling that
        // reached the ConfigMap would abort the host in the configuration binder, on the new
        // ReplicaSet, while the old pods keep serving — the #2210 shape. So the renderer refuses
        // anything but the three names, and emits them in their canonical casing.
        Set("SelfUpdate__DefaultPolicy", UpdatePolicyName(d.UpdatePolicy));
        Set("SelfUpdate__DefaultPattern", string.IsNullOrWhiteSpace(d.UpdatePattern) ? null : d.UpdatePattern!.Trim());
        // The per-PACKAGE default update policy (Auto | Notify | None) the instance seeds onto every
        // install record — separate from the platform's own image policy (Admin/UpdatePolicy) since
        // 2026-09-14. Absent renders nothing: the chart's default keeps the legacy AutoUpdateByDefault
        // mapping (true → Auto).
        Set("PluginCatalog__DefaultUpdatePolicy", string.IsNullOrWhiteSpace(d.ModuleUpdatePolicy) ? null : d.ModuleUpdatePolicy!.Trim());
        SetBool("Modules__AutoRecycleOnStaleBuild", d.AutoRecycleOnStaleBuild);
        foreach (var (slot, assembly) in ModuleSlots(d))
            Set($"Modules__Required__{slot}", assembly);
        // 🚨 Rendered only when the record CLAIMS it, and never as "false": the reader takes an
        // explicit false as a withdrawal, and a record that says nothing must leave the merged
        // reading exactly as it was. The key is a scalar, so it survives the by-index merge that
        // the entries above cannot — which is the whole reason the empty list can mean "none".
        if (d.RequiredModulesAuthoritative)
            Set(RequiredIsAuthoritativeKey.Replace(":", "__"), "true");

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

        // The executor switch (Plugins#1738), independent of Enabled: the Actions executor runs with
        // the in-cluster Job disabled. Blank emits nothing, and the portal and the chart then both
        // mean Job. ActionsExecutor reads Hosting:Operator:Executor and Hosting:Operator:Maintainer.
        // Canonicalised HERE, not only in WithOperatorExecutor: a record read from JSON or built
        // with an initializer never passes the transform, and the Aspire path hands this value
        // straight to the container. A misspelling fails closed on both renderers.
        if (d.Operator is { } operatorSpec)
        {
            if (!HostingOperatorSpec.TryCanonicalExecutor(operatorSpec.Executor, out var operatorExecutor))
                throw new InvalidOperationException($"operator executor '{operatorSpec.Executor}' is neither Job nor Actions. The portal reads every other value as Job, so it would silently keep the in-cluster operator Job.");
            Set("Hosting__Operator__Executor", operatorExecutor);
            Set("Hosting__Operator__Maintainer", string.IsNullOrWhiteSpace(operatorSpec.Maintainer) ? null : operatorSpec.Maintainer.Trim());
        }

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

    /// <summary>The platform self-update policies the portal binds (<c>UpdatePolicyKind</c>), in the casing the binder reads.</summary>
    public static readonly IReadOnlyList<string> UpdatePolicyNames = ["Continuous", "Stable", "None"];

    /// <summary>
    /// The canonical spelling of a record's <see cref="DeploymentContent.UpdatePolicy"/>, or
    /// <c>null</c> for blank. Any other value throws: it would render into a key the pod binds to an
    /// enum and abort the new replica's host in the configuration binder.
    /// </summary>
    public static string? UpdatePolicyName(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return null;
        var wanted = policy.Trim();
        return UpdatePolicyNames.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"updatePolicy '{wanted}' is not a platform update policy — one of {string.Join(", ", UpdatePolicyNames)}. "
                   + "It renders as SelfUpdate__DefaultPolicy, which the portal binds to an enum: a misspelling would "
                   + "abort the new replica's host in the configuration binder while the old pods keep serving.");
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
/// <param name="McpBaseUrl">The MCP back-connection URL to emit, when the caller knows it as text.</param>
/// <param name="InClusterMcpBaseUrl">When <paramref name="McpBaseUrl"/> is null: derive it from the in-cluster portal Service (Helm: yes; Aspire: no — the adapter sets the key to the endpoint Aspire allocates, which is not text until publish, so the derivation emits NOTHING rather than a blank).</param>
/// <param name="IncludeBootEntries">Emit the plugin-catalog wiring (<c>PluginCatalog__*</c>) as portal keys (Aspire: yes; Helm: no — the operator's catalog config file carries the same entries).</param>
public sealed record PortalConfigOptions(bool DatabaseKeys, string? McpBaseUrl, bool InClusterMcpBaseUrl, bool IncludeBootEntries)
{
    /// <summary>The chart's view: database keys from the record, the in-cluster Service as the MCP base URL, catalog wiring via the catalog file.</summary>
    public static PortalConfigOptions Helm { get; } = new(DatabaseKeys: true, McpBaseUrl: null, InClusterMcpBaseUrl: true, IncludeBootEntries: false);

    /// <summary>
    /// The Aspire view: no database keys (the Postgres resource injects them); the MCP base URL is
    /// <paramref name="mcpBaseUrl"/> when given as text, else NOT emitted here — the adapter sets
    /// <c>Mcp__BaseUrl</c> to the endpoint reference Aspire allocates; catalog wiring as env.
    /// </summary>
    public static PortalConfigOptions Aspire(string? mcpBaseUrl) => new(DatabaseKeys: false, McpBaseUrl: mcpBaseUrl, InClusterMcpBaseUrl: false, IncludeBootEntries: true);
}
