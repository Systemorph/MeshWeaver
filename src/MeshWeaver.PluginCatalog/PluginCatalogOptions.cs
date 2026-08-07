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
    /// Packages a FRESH installation installs by itself on first startup, in the same
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
    /// <para>Runs ONCE, on an installation with no install records yet: this seeds a new
    /// deployment, it is not a policy that re-asserts itself. (An installation whose packages are
    /// ALL later uninstalled looks fresh again and would re-seed on the next restart — deliberate,
    /// so a wiped instance recovers, and harmless because installs are additive.)</para>
    /// </summary>
    public List<string> InstallByDefault { get; set; } = [];

    /// <summary>
    /// Whether this installation installs the packages whose manifest declares
    /// <see cref="PackageManifest.PreInstalled"/> — the platform's own baseline catalogs (the
    /// Agents and Skills libraries, Essentials, …). <b>Defaults to <c>true</c></b>: the platform
    /// only works when its baseline is present, so an operator opts OUT deliberately rather than
    /// having to know to opt in. Set <c>PluginCatalog:InstallPreInstalledPackages=false</c> to
    /// suppress it entirely (an air-gapped or hand-curated instance that manages its own content).
    ///
    /// <para>Unlike <see cref="InstallByDefault"/> — which SEEDS a fresh deployment once — the
    /// pre-installed baseline is reconciled on EVERY boot, because it is what the platform itself
    /// requires to function and what must survive a self-update. That reconcile is free on an
    /// up-to-date instance: the content-identity gate in <c>CatalogLayoutAreas.InstallOrUpdate</c>
    /// turns it into one catalog listing and no writes. It is also the only mechanism that can heal
    /// an instance whose baseline partition was lost (#902 — the platform's agent catalog vanished
    /// from the production portal and nothing could bring it back).</para>
    ///
    /// <para>🚨 Narrowing rather than suppressing: leave this <c>true</c> and set
    /// <see cref="InstallByDefault"/> to restrict what a fresh instance seeds beyond the baseline.
    /// The two knobs are independent — this one governs the platform's own baseline, that one the
    /// operator's chosen extras.</para>
    /// </summary>
    public bool InstallPreInstalledPackages { get; set; } = true;

    /// <summary>This installation's public base URL, recorded on the instance node (advisory).</summary>
    public string HomeUrl { get; set; } = "";

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
