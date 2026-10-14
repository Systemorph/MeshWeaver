namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Consumer-side options for the plugin catalog: the registry instance(s) that expose a catalog
/// over <c>/api/plugins</c>. Every installation reads the catalog and downloads packages from a
/// registry over HTTP — the registry holds the source access (the GitHub App credential), so a
/// consumer needs no git/GitHub credentials of its own (npm/NuGet-style credential encapsulation).
///
/// <para>An installation can consume SEVERAL registries (e.g. the platform plugin registry plus an
/// education-content registry) via <see cref="Registries"/>; the legacy single
/// <see cref="RegistryUrl"/>/<see cref="RegistryRef"/> pair keeps working and is folded into
/// <see cref="EffectiveRegistries"/>.</para>
/// </summary>
public sealed class PluginCatalogOptions
{
    /// <summary>Config section that binds these options (<c>PluginCatalog</c>).</summary>
    public const string SectionName = "PluginCatalog";

    /// <summary>
    /// Base URL of the registry instance that exposes the catalog (e.g.
    /// <c>https://memex.meshweaver.cloud</c>). Empty disables the remote catalog (the admin tab shows
    /// a "not configured" note) unless <see cref="Registries"/> is set. Legacy single-registry key —
    /// prefer <see cref="Registries"/> for anything beyond one source.
    /// </summary>
    public string RegistryUrl { get; set; } = "";

    /// <summary>The git ref requested when reading the registry (default <c>HEAD</c>). This is
    /// <b>advisory</b>: the registry is authoritative on the ref it serves (its own
    /// <c>PluginCatalog:SourceRef</c>) and currently ignores the consumer-supplied value — it is sent
    /// so the registry could honor a pinned rollout later without a consumer change.</summary>
    public string RegistryRef { get; set; } = "HEAD";

    /// <summary>The instance token this installation was issued when it registered with the
    /// registry, sent as <c>Authorization: Bearer</c> (see <see cref="PluginRegistryTokens"/>).
    /// Empty → requests go out unauthenticated (only an open dev/e2e registry answers them).
    /// Legacy single-registry key — prefer <see cref="PluginRegistryReference.Token"/>.</summary>
    public string RegistryToken { get; set; } = "";

    /// <summary>
    /// The registries this installation consumes, in display order
    /// (<c>PluginCatalog:Registries:0:{Name,Url,Ref}</c>, …). Empty → falls back to the legacy
    /// <see cref="RegistryUrl"/>/<see cref="RegistryRef"/> pair.
    /// </summary>
    public List<PluginRegistryReference> Registries { get; set; } = [];

    /// <summary>
    /// The deployment's DEFAULT auto-update policy, consulted ONLY at install time: a fresh install
    /// record is seeded with <c>AutoUpdate = AutoUpdateByDefault</c>. Defaults to <c>false</c> —
    /// the platform default is EXPLICIT OPT-IN, so an installation that configures nothing gets
    /// reminder-only updates. A deployment that wants plugins to track their repos continuously
    /// sets <c>PluginCatalog:AutoUpdateByDefault=true</c> (our Helm deployments do) and every
    /// package it installs starts opted in — no per-package step.
    ///
    /// <para>Install-time seed, not a live policy: flipping this later changes nothing for
    /// already-installed packages — the per-record <see cref="PackageManifest.AutoUpdate"/>
    /// stays the sole runtime authority, and an update re-stamp carries the record's existing choice
    /// forward untouched (see <c>PackageInstaller.SeedAutoUpdate</c>).</para>
    /// </summary>
    public bool AutoUpdateByDefault { get; set; }

    /// <summary>
    /// The deployment's DEFAULT per-package update policy (<c>PluginCatalog:DefaultUpdatePolicy</c>
    /// = <c>Auto</c> | <c>Notify</c> | <c>None</c>), consulted ONLY at install time: a fresh install
    /// record is seeded with it (<c>PackageInstaller.SeedUpdatePolicy</c>). Unset → the legacy
    /// <see cref="AutoUpdateByDefault"/> decides (<c>true</c> → Auto, else Notify). Separate from
    /// the platform's own image policy on <c>Admin/UpdatePolicy</c> by design: a deployment may
    /// pin its platform and still track every module, or the reverse.
    /// </summary>
    public PackageUpdatePolicy? DefaultUpdatePolicy { get; set; }

    /// <summary>
    /// A registration bootstrap key (<c>mwr_…</c>) for first-startup auto-registration: when set —
    /// and no <see cref="RegistryToken"/> is configured and no instance key is stored yet — the
    /// installation registers itself at the configured registry on startup as
    /// <see cref="InstanceId"/> and persists the issued instance key
    /// (<see cref="InstanceAutoRegistrationService"/>). A secret; deliver it like the tokens.
    /// </summary>
    public string BootstrapKey { get; set; } = "";

    /// <summary>The logical App ID this installation registers itself under (lowercase slug —
    /// see the instance-id rules). REQUIRED when <see cref="BootstrapKey"/> is set: the id is a
    /// stable global identity, so it is never guessed from a machine or pod name.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Display name for the auto-registered instance. Empty → the instance id.</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>
    /// Packages this installation installs by itself at startup, in the same
    /// <c>Source/Package</c> notation as the registry's grants — e.g. <c>["Plugins/*"]</c> for the
    /// whole platform plugin repo (which is what carries the Store), or
    /// <c>["Plugins/Store", "Plugins/Essentials"]</c> to be selective. Empty (the platform default)
    /// = install nothing; the catalog is merely populated and an admin installs by hand.
    ///
    /// <para>🚨 SOURCE-SCOPED on purpose. An instance may be granted paid content (course repos)
    /// as well as the platform repo, and "install everything I'm entitled to" would sweep that in.
    /// Matching goes through <c>PluginGrantEntry</c> against the catalog entry's
    /// <see cref="PackageManifest.Source"/>, so a registry that does not stamp the source matches
    /// nothing and installs nothing — failing closed.</para>
    ///
    /// <para>Runs on EVERY boot, gated PER PACKAGE by the default-install ledger
    /// (<c>Plugins/_DefaultInstallLedger</c>): a package the seed already delivered once is never
    /// re-installed, even if it is gone now (an operator removed it, and the seed does not fight
    /// an operator); a package it never delivered is installed — which is what repairs a bad first
    /// config or a failed install. A package newly listed in a source this installation already
    /// seeded from, and covered only by a whole-source pattern (<c>Plugins/*</c>), is HELD: it lands
    /// through a governed <c>package.provision</c> activity or by being named here; a held package
    /// gets NO ledger entry.</para>
    ///
    /// <para>🚨 <b>COMMERCIAL packages: naming one exactly IS the authorization.</b> A commercial
    /// package (a price, or a sales contact) named EXACTLY here — <c>Source/Package</c>, e.g.
    /// <c>ClientA/Claims</c> — is INSTALLED by the unattended pass, together with everything it
    /// requires (commercial requirements included). The authorizing principal is the deployment
    /// configuration itself, <c>deployment-config:{InstanceId}</c>
    /// (<see cref="DeploymentConfigAuthority"/>): this list lives in the instance's deployment
    /// record, which changes only through a reviewed pull request the auto-merge App merges, so
    /// naming a package here is a reviewed decision on record (policy
    /// <c>deployment-config-authorizes-named-commercial</c>). The principal is logged, stamped on the install record
    /// (<see cref="PackageManifest.AuthorizedBy"/>) and on the ledger (<c>authorized</c>), and the
    /// entitlement gate re-verifies it against the CURRENT list, so a package this list stops naming
    /// stops being authorized by it. Installing grants nobody access — the plan
    /// (<c>Store:InstancePlan</c>) and the subscriptions decide who reads the content; nothing is
    /// swept to users and nobody is notified. A commercial package covered only by a whole-source
    /// pattern (<c>Plugins/*</c>) names nothing: it is never installed (nor pulled in as a
    /// requirement) by this pass and is recorded under <c>skipped</c> on the ledger with its
    /// reason. A package whose licence requires ACCEPTANCE is skipped either way — a configuration
    /// cannot accept terms on a person's behalf. The ledger still seeds once: an operator who
    /// removes a named commercial package is not fought by the next restart.</para>
    /// </summary>
    public List<string> InstallByDefault { get; set; } = [];

    /// <summary>
    /// Whether this installation installs the packages whose manifest declares
    /// <see cref="PackageManifest.PreInstalled"/> — the platform's own baseline catalogs (the
    /// Agents and Skills libraries, Essentials, …). <b>Applies by default wherever the deployment
    /// does not name its packages</b>: the platform only works when its baseline is present, so an
    /// operator opts OUT deliberately (or by naming an explicit set) rather than having to know to
    /// opt in. Set <c>PluginCatalog:InstallPreInstalledPackages=false</c> to
    /// suppress it entirely (an air-gapped or hand-curated instance that manages its own content).
    ///
    /// <para>Unlike <see cref="InstallByDefault"/> — which SEEDS each package once, per the
    /// ledger — the
    /// pre-installed baseline is reconciled on EVERY boot, because it is what the platform itself
    /// requires to function and what must survive a self-update. That reconcile is free on an
    /// up-to-date instance: the content-identity gate in <c>CatalogLayoutAreas.InstallOrUpdate</c>
    /// turns it into one catalog listing and no writes. It is also the only mechanism that can heal
    /// an instance whose baseline partition was lost (#902 — the platform's agent catalog vanished
    /// from the production portal and nothing could bring it back).</para>
    ///
    /// <para>🚨 <b>Unset it follows <see cref="InstallByDefault"/></b> (policy
    /// <c>declared-package-set-is-exact</c>): the baseline applies unless <see cref="InstallByDefault"/>
    /// declares an EXPLICIT set — every entry naming a package (<c>Source/Package</c>), none a whole
    /// source (<c>Source/*</c>). A deployment that names its packages gets exactly those plus their
    /// declared <c>requires</c>; until this, the baseline added every <c>preInstalled</c> package of
    /// every source on top (a fresh control instance naming five packages selected 48). Set it
    /// <c>true</c> to keep the baseline beside a declared set, <c>false</c> to suppress it
    /// everywhere — an explicit value always wins. This property keeps its surface and default
    /// (<c>true</c>); "unset" is read from <see cref="InstallPreInstalledPackagesConfigured"/>, which
    /// the configuration binds from the SAME key.</para>
    /// </summary>
    public bool InstallPreInstalledPackages { get; set; } = true;

    /// <summary>
    /// <c>PluginCatalog:InstallPreInstalledPackages</c> exactly as CONFIGURED — null when the key is
    /// absent. Bound from the same key as <see cref="InstallPreInstalledPackages"/>
    /// (<see cref="Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute"/>).
    ///
    /// <para>🚨 <b>Why a second, nullable property and not a nullable backing field behind the
    /// bool.</b> <c>ConfigurationBinder</c> reads every bindable property's CURRENT value and writes
    /// it back through the setter whether or not the key exists (it skips only a null value). A bool
    /// whose getter answered <c>true</c> while unset was therefore always "set to true" by binding,
    /// and the explicit-set rule never fired on a deployed portal — measured on a fresh control
    /// instance running the first fix: the pass still selected 48 packages. A <c>bool?</c> that is
    /// null is left alone by the binder, so null here really means "not configured".</para>
    ///
    /// <para>Code that builds the options by hand may set either: an explicit <c>false</c> on
    /// <see cref="InstallPreInstalledPackages"/> still suppresses the baseline; to keep the baseline
    /// beside an explicit set in code, set this property to <c>true</c>.</para>
    /// </summary>
    [Microsoft.Extensions.Configuration.ConfigurationKeyName(nameof(InstallPreInstalledPackages))]
    public bool? InstallPreInstalledPackagesConfigured { get; set; }

    /// <summary>
    /// The baseline decision's input: <see cref="InstallPreInstalledPackagesConfigured"/> when the
    /// configuration states it, else <c>false</c> when code set
    /// <see cref="InstallPreInstalledPackages"/> to false, else null (unset — follow
    /// <see cref="InstallByDefault"/>).
    /// </summary>
    public bool? EffectiveInstallPreInstalledPackages =>
        InstallPreInstalledPackagesConfigured ?? (InstallPreInstalledPackages ? null : false);
    /// <summary>This installation's public base URL, recorded on the instance node (advisory).
    /// 🚨 Since #3650 it is also where a registry DELIVERS its module-published broadcast
    /// (<see cref="ModulePublished"/>): an installation that records none is reached by the safety
    /// net alone.</summary>
    public string HomeUrl { get; set; } = "";

    /// <summary>
    /// 🚨 The SAFETY NET of the module lane (#3650): the longest this installation may go without
    /// reconciling its installed packages against each configured registry's feed. Zero or
    /// negative disables it.
    ///
    /// <para>The fast path is the registry's <see cref="ModulePublished"/> broadcast, delivered to
    /// this installation's webhook inbox within seconds of a publish — and, like every event
    /// channel, it reaches this installation over a chain nobody re-verifies (the
    /// <see cref="HomeUrl"/> the registry recorded, a <c>WebhookInbox:Targets</c> allowlist slot,
    /// a reachable ingress), every joint of which fails SILENTLY. An installation whose channel is
    /// dead looks exactly like one that is up to date. This interval BOUNDS that, the way
    /// <c>SelfUpdate:SafetyNetCheckInterval</c> bounds a dead build-event channel: it is not a poll
    /// that drives the update — the broadcast does — it is the worst case a lost broadcast can
    /// cost. It cannot change WHAT lands (the same <see cref="ModuleUpdateDecision"/> runs, and an
    /// unchanged module costs one feed read and one index read, no download), only how late.</para>
    ///
    /// <para>Default 30 minutes — half of <c>SelfUpdate:MinRollInterval</c>, so a module that
    /// ships is landed before the next restart the roll floor allows, and the restart that
    /// activates it (a landed generation raises <c>PendingRestart</c>, and the self-updater rolls
    /// the running image on it) is never held for a reconcile that has not happened yet.</para>
    /// </summary>
    public TimeSpan ReconcileSafetyNetInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The registries to actually consume: <see cref="Registries"/> (entries with a URL), or the
    /// legacy <see cref="RegistryUrl"/> as a single unnamed entry, or empty when nothing is
    /// configured (the admin tab shows a "not configured" note).
    /// </summary>
    public IReadOnlyList<PluginRegistryReference> EffectiveRegistries =>
        Registries.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList() is { Count: > 0 } configured
            ? configured
            : string.IsNullOrWhiteSpace(RegistryUrl)
                ? []
                : [new PluginRegistryReference { Url = RegistryUrl, Ref = RegistryRef, Token = RegistryToken }];
}

/// <summary>One registry a consumer reads its plugin catalog from (an entry of
/// <see cref="PluginCatalogOptions.Registries"/>).</summary>
public sealed class PluginRegistryReference
{
    /// <summary>Display name for the catalog section (e.g. "Plugins", "Education"). Empty → the URL
    /// is shown instead.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base URL of the registry instance (e.g. <c>https://memex.meshweaver.cloud</c>).</summary>
    public string Url { get; set; } = "";

    /// <summary>Advisory git ref (see <see cref="PluginCatalogOptions.RegistryRef"/>).</summary>
    public string Ref { get; set; } = "HEAD";

    /// <summary>The instance token issued for THIS registry when the installation registered with it
    /// (see <see cref="PluginCatalogOptions.RegistryToken"/> and <see cref="PluginRegistryTokens"/>).</summary>
    public string Token { get; set; } = "";
}
