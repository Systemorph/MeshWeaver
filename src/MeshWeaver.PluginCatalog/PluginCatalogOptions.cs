namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Consumer-side options for the plugin catalog: the URL of the MeshWeaver instance that acts as the
/// plugin <b>registry</b> (exposes <c>/api/plugins</c>). Every installation reads the catalog and
/// downloads packages from this one registry over HTTP — the registry holds the source access (the
/// GitHub App credential), so a consumer needs no git/GitHub credentials of its own (npm/NuGet-style
/// credential encapsulation).
/// </summary>
public sealed class PluginCatalogOptions
{
    /// <summary>Config section that binds these options (<c>PluginCatalog</c>).</summary>
    public const string SectionName = "PluginCatalog";

    /// <summary>
    /// Base URL of the registry instance that exposes the catalog (e.g.
    /// <c>https://memex.meshweaver.cloud</c>). Empty disables the remote catalog (the admin tab shows
    /// a "not configured" note).
    /// </summary>
    public string RegistryUrl { get; set; } = "";

    /// <summary>The git ref the registry browses/installs from (default <c>HEAD</c>). The registry may
    /// override this with its own configured ref; passed through so a pinned rollout is possible.</summary>
    public string RegistryRef { get; set; } = "HEAD";
}
