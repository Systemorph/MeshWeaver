namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The content of a <c>PluginCatalog</c> node — points the catalog browse view at a source git
/// repository and ref. The catalog lists that repo's installable folders at
/// <see cref="SourceRef"/> and offers Install / Update per package. Which shape those folders have
/// is <see cref="Format"/>.
/// </summary>
public record PluginCatalogContent
{
    /// <summary>Local path to the source git repository (the plugins repo checkout). A GitHub-URL
    /// source can be added behind the same catalog later.</summary>
    public string? SourceRepoPath { get; init; }

    /// <summary>The git ref (commit SHA or branch) to browse/install from. Defaults to <c>HEAD</c>.</summary>
    public string SourceRef { get; init; } = "HEAD";

    /// <summary>Optional subdirectory within the repo that holds the package folders (e.g.
    /// <c>"catalog"</c>). When empty, package folders are read from the repo root.</summary>
    public string? SourceSubdir { get; init; }

    /// <summary>
    /// The repository's package format — the SAME knob, with the same values and the same default,
    /// as <c>PluginCatalog:Sources:N:Format</c> in configuration: <c>node-repo</c> (the default;
    /// <c>&lt;Plugin&gt;/index.json</c> Space roots, what MeshWeaver.Plugins ships) or
    /// <c>package-json</c> for a manifest repo.
    ///
    /// <para>🚨 It exists because a catalog node previously could not say: the browse view built its
    /// source without a format and so always read <c>package.json</c>, while
    /// <c>PluginUpdateWatcher</c> read the very same content as a node repo. One record, two
    /// readers, opposite answers — a node-repo checkout rendered "No installable packages found."
    /// while its watcher happily listed the same packages (#3384).</para>
    /// </summary>
    public string? Format { get; init; }

    /// <summary>Optional markdown intro shown above the package list.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The content of a node-native plugin's Space root (<c>&lt;Plugin&gt;.json</c> in the plugins repo) —
/// "the node is the manifest". Registered so a consumer deserializes the root verbatim on install
/// instead of degrading it to a raw <c>JsonElement</c>. Deliberately tiny; the plugin's real payload
/// is its <c>NodeType</c> + <c>Source</c> nodes.
/// </summary>
public record PluginManifest
{
    /// <summary>One-line description of what the plugin adds.</summary>
    public string? Description { get; init; }

    /// <summary>Minimum mesh version the plugin needs (advisory).</summary>
    public string? MinMeshVersion { get; init; }
}
