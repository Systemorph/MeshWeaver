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

    /// <summary>
    /// The registries this installation consumes, in display order
    /// (<c>PluginCatalog:Registries:0:{Name,Url,Ref}</c>, …). Empty → falls back to the legacy
    /// <see cref="RegistryUrl"/>/<see cref="RegistryRef"/> pair.
    /// </summary>
    public List<PluginRegistryReference> Registries { get; set; } = [];

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
                : [new PluginRegistryReference { Url = RegistryUrl, Ref = RegistryRef }];
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
}
